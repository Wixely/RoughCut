using System.Security.Cryptography;
using System.Text;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record UrlProjectResult(string ProjectPath, AcquisitionResult Acquisition,
    CaptionSelectionResult? Captions, string? CaptionIssue, EditProject Project);

public sealed class RoughCutOperations(WorkspaceBoundary workspace, string ffmpeg = "ffmpeg", string ffprobe = "ffprobe",
    string ytDlp = "yt-dlp", IAcquisitionTool? acquisitionTool = null)
{
    private readonly ProjectStore _store = new();
    private readonly MediaReader _media = new(ffmpeg, ffprobe);

    public Task<EditProject> ReadProjectAsync(string projectPath, CancellationToken token = default)
        => _store.LoadAsync(workspace.Resolve(projectPath), token);

    public Task<VideoInfo> InspectAsync(string mediaPath, CancellationToken token = default)
        => _media.InspectAsync(workspace.Resolve(mediaPath), token);

    public async Task<EditProject> CreateProjectAsync(string mediaPath, string projectPath, CancellationToken token = default)
    {
        var source = workspace.Resolve(mediaPath);
        var destination = workspace.Resolve(projectPath, mustExist: false);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Project destination already exists.");
        var relative = Path.GetRelativePath(Path.GetDirectoryName(destination)!, source).Replace('\\', '/');
        if (!ProjectValidator.IsPortablePath(relative))
            throw new ArgumentException("Media must be inside the project directory.");
        var info = await _media.InspectAsync(source, token);
        var project = new EditProject
        {
            TimeBase = info.TimeBase,
            Assets = [new("source-1", "video", relative, info.Sha256, info.DurationTicks, info.Width, info.Height, "application/octet-stream")],
            Timeline = [new("clip-1", "source-1", 0, info.DurationTicks)]
        };
        await _store.SaveAsync(destination, project, 0, token);
        return project;
    }

    public async Task<FrameImage> GetFrameAsync(string projectPath, string assetId, long timestampTicks,
        long timeBaseNumerator, long timeBaseDenominator, int maxWidth, CancellationToken token = default)
    {
        var absoluteProject = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(absoluteProject, token);
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind == "video")
            ?? throw new KeyNotFoundException("Video asset was not found in the project.");
        var frame = await _media.GetFrameAsync(ProjectFiles.Resolve(absoluteProject, asset.Path),
            new(assetId, new(timestampTicks, new(timeBaseNumerator, timeBaseDenominator)), maxWidth), token);
        if (!string.Equals(frame.Info.SourceSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source media changed after the project was created.");
        return frame;
    }

    public async Task<EditProject> ApplyEditsAsync(string projectPath, long expectedRevision,
        EditOperation[] edits, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var edited = TimelineEditor.Apply(project, edits);
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<EditProject> ApplySpeakerEditsAsync(string projectPath, long expectedRevision,
        SpeakerEdit[] edits, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var edited = SpeakerEditor.Apply(project, edits);
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<DiarizationPlanResult> SaveDiarizationAsync(string projectPath, long expectedRevision,
        DiarizationSubmission submission, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var asset = project.Assets.SingleOrDefault(item => item.Id == submission.AssetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Diarization asset was not found in the project.");
        var source = ProjectFiles.Resolve(path, asset.Path);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Diarization source fingerprint differs from the project.");
        var result = DiarizationPlanner.Plan(project, submission);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Diarization source changed while speaker assignments were prepared.");
        await _store.SaveAsync(path, result.Project, expectedRevision, token);
        return result;
    }

    public async Task<DiarizationPlanResult> DiarizeAsync(string projectPath, string assetId, long expectedRevision,
        ISpeakerDiarizer diarizer, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Diarization asset was not found in the project.");
        var source = ProjectFiles.Resolve(path, asset.Path);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Diarization source fingerprint differs from the project.");
        var submission = await diarizer.DiarizeAsync(project, assetId, source, token);
        token.ThrowIfCancellationRequested();
        if (submission.AssetId != assetId)
            throw new InvalidDataException("Diarization provider returned a different asset ID.");
        return await SaveDiarizationAsync(projectPath, expectedRevision, submission, token);
    }

    public async Task<EditProject> PlanVoiceAsync(string projectPath, long expectedRevision,
        VoicePlanSubmission submission, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var edited = VoicePlanner.Plan(project, submission);
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<EditProject> ImportVoicePreviewAsync(string projectPath, long expectedRevision,
        string replacementId, string base64Wav, string runtime, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(base64Wav) || base64Wav.Length > ((WaveAudio.MaxBytes + 2) / 3) * 4)
            throw new InvalidDataException("Encoded voice preview exceeds the 16 MiB WAVE limit.");
        byte[] wav;
        try { wav = Convert.FromBase64String(base64Wav); }
        catch (FormatException) { throw new InvalidDataException("Voice preview is not valid base64."); }
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var result = VoiceWorkflow.ImportPreview(project, path, replacementId, wav, runtime);
        try { await _store.SaveAsync(path, result.Project, expectedRevision, token); }
        catch
        {
            var referenced = false;
            try { referenced = (await _store.LoadAsync(path, CancellationToken.None)).Assets.Any(item => item.Path == result.RelativePath); }
            catch (Exception) { referenced = true; }
            var destination = ProjectFiles.Resolve(path, result.RelativePath);
            if (result.Created && !referenced && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        return result.Project;
    }

    public async Task<EditProject> SynthesizeVoiceAsync(string projectPath, long expectedRevision,
        string replacementId, IVoiceSynthesizer synthesizer, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var replacement = project.Replacements.SingleOrDefault(item => item.Id == replacementId)
            ?? throw new KeyNotFoundException("Voice replacement was not found.");
        if (replacement.State != "requested") throw new InvalidOperationException("Voice replacement already has a generated preview.");
        var mapping = project.Voices.Single(item => item.Id == replacement.MappingId);
        var output = await synthesizer.SynthesizeAsync(mapping, replacement.Text, token);
        token.ThrowIfCancellationRequested();
        return await ImportVoicePreviewAsync(projectPath, expectedRevision, replacementId,
            Convert.ToBase64String(output.Wav), output.Runtime, token);
    }

    public async Task<VoicePreviewAudio> GetVoicePreviewAsync(string projectPath, long expectedRevision,
        string replacementId, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var replacement = project.Replacements.SingleOrDefault(item => item.Id == replacementId)
            ?? throw new KeyNotFoundException("Voice replacement was not found.");
        var provenance = project.Synthesis.SingleOrDefault(item => item.ReplacementId == replacementId)
            ?? throw new InvalidOperationException("Voice replacement has no generated preview.");
        var asset = project.Assets.Single(item => item.Id == replacement.GeneratedAssetId);
        var wav = await ProjectFiles.ReadBoundedAsync(ProjectFiles.Resolve(path, asset.Path), WaveAudio.MaxBytes, token);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(wav)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated voice preview changed after it was imported.");
        WaveAudio.Inspect(wav);
        return new(new(replacement.Id, asset.Id, replacement.State, replacement.FitPolicy,
            provenance.RequestedDuration, provenance.ActualDuration, asset.MediaType), wav);
    }

    public async Task<EditProject> SetVoiceStateAsync(string projectPath, long expectedRevision,
        string replacementId, string state, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var edited = VoicePlanner.SetState(project, replacementId, state);
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<AnalysisPlanResult> SaveAnalysisAsync(string projectPath, long expectedRevision,
        string prompt, string policy, AnalysisSubmission submission, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var asset = project.Assets.SingleOrDefault(item => item.Id == submission.AssetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Analysis asset was not found in the project.");
        var source = ProjectFiles.Resolve(path, asset.Path);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Analysis source fingerprint differs from the project.");
        var result = AnalysisPlanner.Plan(project, submission, prompt, policy);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Analysis source changed while proposals were prepared.");
        await _store.SaveAsync(path, result.Project, expectedRevision, token);
        return result;
    }

    public async Task<AnalysisPlanResult> AnalyzeAsync(string projectPath, string assetId, long expectedRevision,
        string prompt, string policy, IContentAnalyzer analyzer, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Analysis asset was not found in the project.");
        var source = ProjectFiles.Resolve(path, asset.Path);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Analysis source fingerprint differs from the project.");
        var submission = await analyzer.AnalyzeAsync(project, assetId, prompt, token);
        if (submission.AssetId != assetId) throw new InvalidDataException("Analysis provider changed the requested asset ID.");
        return await SaveAnalysisAsync(projectPath, expectedRevision, prompt, policy, submission, token);
    }

    public async Task<EditProject> ApplyAnalysisAsync(string projectPath, long expectedRevision,
        string[]? proposalIds = null, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var edited = AnalysisPlanner.Apply(project, proposalIds);
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<EditProject> ImportCaptionsAsync(string projectPath, string assetId, string captionPath,
        long expectedRevision, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var source = workspace.Resolve(captionPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, source).Replace('\\', '/');
        var bytes = await ProjectFiles.ReadBoundedAsync(ProjectFiles.Resolve(path, relative), Captions.MaxBytes, token);
        var cues = Captions.ParseSrt(new UTF8Encoding(false, true).GetString(bytes));
        var edited = project with
        {
            Revision = checked(expectedRevision + 1),
            Captions = new(assetId, relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), new(1, 1000), cues)
        };
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return edited;
    }

    public async Task<CaptionSelectionResult> SelectCaptionsAsync(string projectPath, string assetId,
        CaptionCandidate[] candidates, long expectedRevision, string preferredLanguage = "en",
        string? overrideCandidateId = null, CancellationToken token = default)
    {
        if (candidates.Length is 0 or > 32 || candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
            throw new ArgumentException("Provide 1 to 32 caption candidates with unique IDs.");
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind is "video" or "audio")
            ?? throw new KeyNotFoundException("Timed media asset was not found in the project.");
        var assessed = new List<(CaptionCandidateAssessment Assessment, CaptionCue[]? Cues, byte[]? Bytes)>();
        foreach (var candidate in candidates)
            assessed.Add(await CaptionSelection.AssessAsync(path, project.TimeBase, asset, candidate, preferredLanguage, workspace, token));
        var valid = assessed.Where(item => item.Assessment.Valid).ToArray();
        if (valid.Length == 0) throw new InvalidDataException("No valid caption candidate was available.");
        var selected = overrideCandidateId is null
            ? valid.OrderByDescending(item => item.Assessment.Score).ThenBy(item => item.Assessment.Id, StringComparer.Ordinal).First()
            : valid.SingleOrDefault(item => item.Assessment.Id == overrideCandidateId);
        if (selected.Assessment is null)
            throw new ArgumentException("Caption override must identify a valid candidate.");
        var edited = project with
        {
            Revision = checked(expectedRevision + 1),
            Captions = new(assetId, selected.Assessment.Path,
                Convert.ToHexStringLower(SHA256.HashData(selected.Bytes!)), new(1, 1000), selected.Cues!,
                selected.Assessment.SourceKind, selected.Assessment.Language,
                overrideCandidateId is null ? "recommended" : "override", selected.Assessment.Id,
                selected.Assessment.CoverageBasisPoints)
        };
        await _store.SaveAsync(path, edited, expectedRevision, token);
        return new(selected.Assessment.Id, overrideCandidateId is not null,
            assessed.Select(item => item.Assessment).ToArray(), edited);
    }

    public async Task<EditProject> TranscribeLocalAsync(string projectPath, string assetId, long expectedRevision,
        ILocalSpeechTranscriber transcriber, int chunkSeconds = LocalSpeechProcessor.DefaultChunkSeconds,
        CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        var report = await new LocalSpeechProcessor(ffmpeg).TranscribeAsync(project, path, assetId, transcriber, chunkSeconds, token);
        if (report.Segments.Length == 0) throw new InvalidDataException("Local transcription returned no timed speech.");
        var output = report.Segments.Select(segment => new OutputCaption(segment.Id, assetId,
            new(segment.Start, project.TimeBase), new(segment.End, project.TimeBase), segment.Text)).ToArray();
        var srt = Captions.WriteSrt(output);
        var bytes = new UTF8Encoding(false).GetBytes(srt);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var relative = $"assets/captions/{hash}.srt";
        var destination = ProjectFiles.Resolve(path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var created = false;
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                created = true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else if (!string.Equals(await MediaReader.FingerprintAsync(destination, token), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Existing transcription caption asset does not match its content hash.");
        var cues = Captions.ParseSrt(srt);
        var asset = project.Assets.Single(item => item.Id == assetId);
        var covered = cues.Sum(cue => cue.End - cue.Start);
        var durationMs = TimeMath.ExactTicks(new(asset.Duration, project.TimeBase), new(1, 1000));
        var coverage = durationMs == 0 ? 0 : checked((int)Math.Min(10_000, covered * 10_000 / durationMs));
        var edited = project with
        {
            Revision = checked(expectedRevision + 1),
            Speech = [.. project.Speech.Where(segment => segment.AssetId != assetId), .. report.Segments],
            Transcription = new(assetId, asset.Sha256, report.Provider, report.Model, report.Language, chunkSeconds),
            Diarization = project.Diarization?.AssetId == assetId ? null : project.Diarization,
            Captions = new(assetId, relative, hash, new(1, 1000), cues, "local-stt", report.Language,
                "recommended", $"{report.Provider}:{report.Model}", coverage)
        };
        try { await _store.SaveAsync(path, edited, expectedRevision, token); }
        catch
        {
            var referenced = false;
            try { referenced = string.Equals((await _store.LoadAsync(path, CancellationToken.None)).Captions?.SourceSha256, hash, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { referenced = true; }
            if (created && !referenced && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        return edited;
    }

    public async Task<EditProject> ImportPngAsync(string projectPath, string assetId, string base64Data,
        long expectedRevision, string provider, string modelVersion, CancellationToken token = default)
    {
        const int maxBytes = 8 * 1024 * 1024;
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128)
            throw new ArgumentException("Image asset ID must contain 1 to 128 characters.");
        if (string.IsNullOrWhiteSpace(base64Data) || base64Data.Length > ((maxBytes + 2) / 3) * 4)
            throw new InvalidDataException("Encoded image exceeds the 8 MiB PNG limit.");
        byte[] png;
        try { png = Convert.FromBase64String(base64Data); }
        catch (FormatException) { throw new InvalidDataException("Image data is not valid base64."); }
        if (png.Length > maxBytes) throw new InvalidDataException("Image exceeds the 8 MiB PNG limit.");
        var dimensions = PngImage.ReadDimensions(png);
        var path = workspace.Resolve(projectPath);
        var project = await _store.LoadAsync(path, token);
        if (project.Revision != expectedRevision) throw new RevisionConflictException();
        if (project.Assets.Any(asset => asset.Id == assetId)) throw new ArgumentException("Image asset ID already exists.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        var relative = $"assets/images/{hash}.png";
        var destination = ProjectFiles.Resolve(path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var createdAssetFile = false;
        if (File.Exists(destination))
        {
            if (!string.Equals(await MediaReader.FingerprintAsync(destination, token), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing image asset does not match its content hash.");
        }
        else
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await output.WriteAsync(png, token);
                    output.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                createdAssetFile = true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var edited = project with
        {
            Revision = checked(expectedRevision + 1),
            Assets = [.. project.Assets, new(assetId, "image", relative, hash, 0, dimensions.Width, dimensions.Height, "image/png")],
            Provenance = [.. project.Provenance, new(assetId, provider, modelVersion)]
        };
        try { await _store.SaveAsync(path, edited, expectedRevision, token); }
        catch
        {
            var referenced = false;
            try { referenced = (await _store.LoadAsync(path, CancellationToken.None)).Assets.Any(asset => asset.Sha256 == hash); }
            catch (Exception) { referenced = true; }
            if (createdAssetFile && !referenced && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        return edited;
    }

    public async Task<ExportPlan> PreflightAsync(string projectPath, CancellationToken token = default)
    {
        var path = workspace.Resolve(projectPath);
        return await new ExportPlanner(ffmpeg, ffprobe).PreflightAsync(await _store.LoadAsync(path, token), path, token);
    }

    public Task<TimelineFrame> GetTimelineFrameAsync(string projectPath, long expectedRevision, long timelineTicks,
        int maxWidth, CancellationToken token = default)
        => new TimelinePreviewer(ffmpeg, ffprobe).GetFrameAsync(workspace.Resolve(projectPath), expectedRevision,
            timelineTicks, maxWidth, token);

    public Task<AcquisitionResult> AcquireAsync(string sourceUrl, string destinationDirectory,
        string? denoPath = null, CancellationToken token = default)
        => new YtDlpAcquirer(workspace, ytDlp, acquisitionTool).AcquireAsync(sourceUrl, destinationDirectory, denoPath, token);

    /// Acquires one URL with its subtitles, creates a project beside the downloaded media and selects the
    /// best caption track. Keeps the whole URL-to-project sequence in one place so hosts cannot diverge.
    public async Task<UrlProjectResult> CreateProjectFromUrlAsync(string sourceUrl, string destinationDirectory,
        string? denoPath = null, string preferredLanguage = "en", CancellationToken token = default)
    {
        var acquisition = await AcquireAsync(sourceUrl, destinationDirectory, denoPath, token);
        // The media must stay inside the project directory, so the project is written into the acquired folder.
        var directory = workspace.Resolve(destinationDirectory);
        var inner = new RoughCutOperations(new WorkspaceBoundary(directory), ffmpeg, ffprobe, ytDlp, acquisitionTool);
        const string projectName = "project.json";
        var project = await inner.CreateProjectAsync(acquisition.MediaPath, projectName, token);
        if (acquisition.Captions.Length == 0)
            return new(Path.Combine(directory, projectName), acquisition, null, "No subtitles were available.", project);
        var candidates = acquisition.Captions
            .Select(caption => new CaptionCandidate($"{caption.SourceKind}-{caption.Language}",
                caption.Path, caption.SourceKind, caption.Language))
            .ToArray();
        try
        {
            var captions = await inner.SelectCaptionsAsync(projectName, project.Assets[0].Id, candidates,
                project.Revision, preferredLanguage, token: token);
            return new(Path.Combine(directory, projectName), acquisition, captions, null, captions.Project);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            // Unusable subtitles must not discard a perfectly good project; report instead of failing.
            return new(Path.Combine(directory, projectName), acquisition, null, exception.Message, project);
        }
    }
}
