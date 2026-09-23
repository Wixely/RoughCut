using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoughCut.Application;
using RoughCut.Core;
using RoughCut.Media;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
var store = new ProjectStore();
var tools = ToolSettings.Default;
var ffmpeg = tools.Ffmpeg;
var ffprobe = tools.Ffprobe;
var ytDlp = tools.YtDlp;
var media = new MediaReader(ffmpeg, ffprobe);
try
{
    switch (args)
    {
        case [] or ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut development CLI
                  tools
                  validate <project.json>
                  map <project.json>
                  save <candidate.json> <project.json> <expected-revision>
                  inspect <local-video>
                  create <local-video> <new-project.json>
                  frame <local-video> <seconds> <new-output.png>
                  timeline-frame <project.json> <revision> <seconds> <new-output.png>
                  edit <project.json> <operations.json> <expected-revision>
                  captions <project.json> <asset-id> <source.srt> <expected-revision>
                  captions-select <project.json> <asset-id> <candidates.json> <expected-revision> <preferred-language> [override-candidate-id]
                  analyse <project.json> <submission.json> <expected-revision> <review|auto-high-certainty> <prompt>
                  analysis-apply <project.json> <expected-revision> [proposal-id ...]
                  diarization-save <project.json> <submission.json> <expected-revision>
                  speaker-edit <project.json> <speaker-edits.json> <expected-revision>
                  voice-plan <project.json> <voice-plan.json> <expected-revision>
                  voice-preview-import <project.json> <replacement-id> <preview.wav> <expected-revision> <runtime>
                  voice-synthesize <project.json> <expected-revision> <replacement-id>
                  voice-preview <project.json> <expected-revision> <replacement-id> <new-output.wav>
                  voice-state <project.json> <expected-revision> <replacement-id> <applied|reverted>
                  acquire <url> <new-output-directory> [deno-executable]
                  create-url <url> <new-output-directory> [deno-executable]
                  preflight <project.json>
                  export <project.json> <new-output-directory> [--allow-encode]
                  preflight-delivery <project.json>
                  deliver <project.json> <new-output-directory>

                Frame requests use source-relative presentation time. JSON metadata goes to stdout.
                Export is bounded to the documented Matroska video/PCM fixture matrix.
                Deliver re-encodes any decodable timeline into H.264/AAC MP4; it never claims a copy.
                Local speech inference requires a separately configured compatible runtime.
                Executable locations come from ROUGHCUT_FFMPEG / ROUGHCUT_FFPROBE / ROUGHCUT_YTDLP / ROUGHCUT_DENO,
                then a tools settings file, then PATH. Run `tools` to see what resolves.
                """);
            break;
        case ["tools"]:
            {
                Console.WriteLine($"settings file: {tools.SettingsPath}" +
                    (File.Exists(tools.SettingsPath) ? "" : " (absent)"));
                foreach (var resolved in tools.Describe())
                    Console.WriteLine($"  {resolved.Name,-8} {resolved.Value}  [{resolved.Source}]" +
                        (resolved.Missing ? "  MISSING" : ""));
                break;
            }
        case ["validate", var path]:
            await store.LoadAsync(path, token);
            Console.WriteLine("{\"valid\":true}");
            break;
        case ["map", var path]:
            Console.WriteLine(JsonSerializer.Serialize(ProjectValidator.MapTimeline(await store.LoadAsync(path, token)), ProjectJson.Default.TimelineMappingArray));
            break;
        case ["save", var candidate, var destination, var expected]:
            await store.SaveAsync(destination, await store.LoadAsync(candidate, token), long.Parse(expected, CultureInfo.InvariantCulture), token);
            Console.WriteLine("{\"saved\":true}");
            break;
        case ["edit", var path, var operationsPath, var expected]:
            {
                var project = await store.LoadAsync(path, token);
                var revision = long.Parse(expected, CultureInfo.InvariantCulture);
                if (project.Revision != revision) throw new RevisionConflictException();
                var operations = JsonSerializer.Deserialize(await ProjectFiles.ReadBoundedAsync(operationsPath, ProjectStore.MaxDocumentBytes, token), ProjectJson.Default.EditOperationArray)
                    ?? throw new InvalidDataException("Edit operations cannot be null.");
                var edited = TimelineEditor.Apply(project, operations);
                await store.SaveAsync(path, edited, revision, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["acquire", var sourceUrl, var destination, .. var acquisitionOptions] when acquisitionOptions.Length <= 1:
            {
                var fullDestination = Path.GetFullPath(destination);
                var boundary = new WorkspaceBoundary(Path.GetDirectoryName(fullDestination)!);
                var result = await new YtDlpAcquirer(boundary, ytDlp).AcquireAsync(sourceUrl,
                    Path.GetFileName(fullDestination), acquisitionOptions.FirstOrDefault() ?? tools.Deno, token);
                Console.WriteLine(JsonSerializer.Serialize(result, ApplicationJson.Default.AcquisitionResult));
                break;
            }
        case ["captions", var path, var assetId, var sourcePath, var expected]:
            {
                var project = await store.LoadAsync(path, token);
                var revision = long.Parse(expected, CultureInfo.InvariantCulture);
                if (project.Revision != revision) throw new RevisionConflictException();
                var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFullPath(sourcePath)).Replace('\\', '/');
                var bytes = await ProjectFiles.ReadBoundedAsync(ProjectFiles.Resolve(path, relative), Captions.MaxBytes, token);
                var cues = Captions.ParseSrt(new UTF8Encoding(false, true).GetString(bytes));
                var edited = project with { Revision = checked(revision + 1), Captions = new(assetId, relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), new(1, 1000), cues) };
                await store.SaveAsync(path, edited, revision, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["captions-select", var path, var assetId, var candidatesPath, var expected, var preferredLanguage, .. var selectionOptions]
            when selectionOptions.Length <= 1:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var root = Path.GetDirectoryName(fullProjectPath)!;
                var candidates = JsonSerializer.Deserialize(
                    await ProjectFiles.ReadBoundedAsync(candidatesPath, ProjectStore.MaxDocumentBytes, token),
                    ProjectJson.Default.CaptionCandidateArray) ?? throw new InvalidDataException("Caption candidates cannot be null.");
                var operations = new RoughCutOperations(new WorkspaceBoundary(root), ffmpeg, ffprobe);
                var result = await operations.SelectCaptionsAsync(Path.GetFileName(fullProjectPath), assetId, candidates,
                    long.Parse(expected, CultureInfo.InvariantCulture), preferredLanguage, selectionOptions.FirstOrDefault(), token);
                Console.WriteLine(JsonSerializer.Serialize(result, ProjectJson.Default.CaptionSelectionResult));
                break;
            }
        case ["analyse", var path, var submissionPath, var expected, var policy, var prompt]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var submission = JsonSerializer.Deserialize(
                    await ProjectFiles.ReadBoundedAsync(submissionPath, ProjectStore.MaxDocumentBytes, token),
                    ProjectJson.Default.AnalysisSubmission) ?? throw new InvalidDataException("Analysis submission cannot be null.");
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var result = await operations.SaveAnalysisAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), prompt, policy, submission, token);
                Console.WriteLine(JsonSerializer.Serialize(result, ProjectJson.Default.AnalysisPlanResult));
                break;
            }
        case ["analysis-apply", var path, var expected, .. var proposalIds]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var edited = await operations.ApplyAnalysisAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), proposalIds.Length == 0 ? null : proposalIds, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["speaker-edit", var path, var editsPath, var expected]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var edits = JsonSerializer.Deserialize(
                    await ProjectFiles.ReadBoundedAsync(editsPath, ProjectStore.MaxDocumentBytes, token),
                    ProjectJson.Default.SpeakerEditArray) ?? throw new InvalidDataException("Speaker edits cannot be null.");
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var edited = await operations.ApplySpeakerEditsAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), edits, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["diarization-save", var path, var submissionPath, var expected]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var submission = JsonSerializer.Deserialize(
                    await ProjectFiles.ReadBoundedAsync(submissionPath, ProjectStore.MaxDocumentBytes, token),
                    ProjectJson.Default.DiarizationSubmission) ?? throw new InvalidDataException("Diarization submission cannot be null.");
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var result = await operations.SaveDiarizationAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), submission, token);
                Console.WriteLine(JsonSerializer.Serialize(result, ProjectJson.Default.DiarizationPlanResult));
                break;
            }
        case ["voice-plan", var path, var submissionPath, var expected]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var submission = JsonSerializer.Deserialize(
                    await ProjectFiles.ReadBoundedAsync(submissionPath, ProjectStore.MaxDocumentBytes, token),
                    ProjectJson.Default.VoicePlanSubmission) ?? throw new InvalidDataException("Voice plan cannot be null.");
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var edited = await operations.PlanVoiceAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), submission, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["voice-preview-import", var path, var replacementId, var wavPath, var expected, var runtime]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var wav = await ProjectFiles.ReadBoundedAsync(wavPath, WaveAudio.MaxBytes, token);
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var edited = await operations.ImportVoicePreviewAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), replacementId, Convert.ToBase64String(wav), runtime, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["voice-preview", var path, var expected, var replacementId, var outputPath]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var preview = await operations.GetVoicePreviewAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), replacementId, token);
                outputPath = Path.GetFullPath(outputPath);
                if (File.Exists(outputPath)) throw new IOException("Output already exists; choose a new output path.");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, preview.Wav, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporaryPath, outputPath, overwrite: false);
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                Console.WriteLine(JsonSerializer.Serialize(preview.Info, ProjectJson.Default.VoicePreviewInfo));
                break;
            }
        case ["voice-synthesize", var path, var expected, var replacementId]:
            {
                var endpoint = Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_ENDPOINT");
                if (string.IsNullOrWhiteSpace(endpoint))
                    throw new InvalidOperationException("Live Qwen synthesis requires ROUGHCUT_QWEN_ENDPOINT, for example http://127.0.0.1:8080/.");
                var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_TIMEOUT_SECONDS"), out var configuredTimeout)
                    ? configuredTimeout : 600;
                using var synthesizer = new QwenSpeechClient(endpoint,
                    Environment.GetEnvironmentVariable("ROUGHCUT_QWEN_API_KEY"), TimeSpan.FromSeconds(timeoutSeconds));
                var fullProjectPath = Path.GetFullPath(path);
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var edited = await operations.SynthesizeVoiceAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), replacementId, synthesizer, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["voice-state", var path, var expected, var replacementId, var state]:
            {
                var fullProjectPath = Path.GetFullPath(path);
                var operations = new RoughCutOperations(new WorkspaceBoundary(Path.GetDirectoryName(fullProjectPath)!), ffmpeg, ffprobe);
                var edited = await operations.SetVoiceStateAsync(Path.GetFileName(fullProjectPath),
                    long.Parse(expected, CultureInfo.InvariantCulture), replacementId, state, token);
                Console.WriteLine(JsonSerializer.Serialize(edited, ProjectJson.Default.EditProject));
                break;
            }
        case ["preflight", var path]:
            {
                var plan = await new ExportPlanner(ffmpeg, ffprobe).PreflightAsync(await store.LoadAsync(path, token), path, token);
                Console.WriteLine(JsonSerializer.Serialize(plan, ProjectJson.Default.ExportPlan));
                return plan.Supported ? 0 : 2;
            }
        case ["export", var path, var destination, .. var options] when options.Length == 0 || options is ["--allow-encode"]:
            {
                var report = await new ExportEngine(ffmpeg, ffprobe).ExportAsync(path, destination, options.Length == 1, token);
                Console.WriteLine(JsonSerializer.Serialize(report, ProjectJson.Default.ExportReport));
                break;
            }
        case ["preflight-delivery", var path]:
            {
                var plan = DeliveryExporter.Plan(await store.LoadAsync(path, token), path);
                Console.WriteLine(JsonSerializer.Serialize(plan, ProjectJson.Default.DeliveryPlan));
                return plan.Supported ? 0 : 2;
            }
        case ["deliver", var path, var destination]:
            {
                var report = await new DeliveryExporter(ffmpeg, ffprobe).ExportAsync(path, destination, token);
                Console.WriteLine(JsonSerializer.Serialize(report, ProjectJson.Default.DeliveryReport));
                break;
            }
        case ["inspect", var path]:
            Console.WriteLine(JsonSerializer.Serialize(await media.InspectAsync(path, token), MediaJson.Default.VideoInfo));
            break;
        case ["create", var source, var destination]:
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(destination))!, Path.GetFullPath(source)).Replace('\\', '/');
                if (!ProjectValidator.IsPortablePath(relative))
                    throw new ArgumentException("Keep the source inside the project directory (for example media/source.mp4).");
                if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Project destination must differ from source media.");
                var info = await media.InspectAsync(source, token);
                var project = new EditProject
                {
                    TimeBase = info.TimeBase,
                    Assets = [new("source-1", "video", relative, info.Sha256, info.DurationTicks, info.Width, info.Height, "application/octet-stream")],
                    Timeline = [new("clip-1", "source-1", 0, info.DurationTicks)]
                };
                await store.SaveAsync(destination, project, 0, token);
                Console.WriteLine(JsonSerializer.Serialize(project, ProjectJson.Default.EditProject));
                break;
            }
        case ["create-url", var sourceUrl, var destination, .. var urlOptions] when urlOptions.Length <= 1:
            {
                var fullDestination = Path.GetFullPath(destination);
                var boundary = new WorkspaceBoundary(Path.GetDirectoryName(fullDestination)!);
                var operations = new RoughCutOperations(boundary, ffmpeg, ffprobe, ytDlp);
                var result = await operations.CreateProjectFromUrlAsync(sourceUrl, Path.GetFileName(fullDestination),
                    urlOptions.FirstOrDefault() ?? tools.Deno, token: token);
                Console.WriteLine(JsonSerializer.Serialize(result, ApplicationJson.Default.UrlProjectResult));
                break;
            }
        case ["frame", var path, var secondsText, var destination]:
            {
                var seconds = decimal.Parse(secondsText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (seconds < 0 || seconds * 1_000_000 != decimal.Truncate(seconds * 1_000_000))
                    throw new ArgumentException("Seconds must be nonnegative with at most six fractional digits.");
                destination = Path.GetFullPath(destination);
                if (File.Exists(destination)) throw new IOException("Output already exists; choose a new output path.");
                var frame = await media.GetFrameAsync(path, new("source-1", new(checked((long)(seconds * 1_000_000)), TimeBase.Microseconds)), token);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, frame.Png, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporaryPath, destination, overwrite: false);
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                Console.WriteLine(JsonSerializer.Serialize(frame.Info, ProjectJson.Default.FrameInfo));
                break;
            }
        case ["timeline-frame", var path, var expected, var secondsText, var destination]:
            {
                var seconds = decimal.Parse(secondsText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (seconds < 0 || seconds * 1_000_000 != decimal.Truncate(seconds * 1_000_000))
                    throw new ArgumentException("Seconds must be nonnegative with at most six fractional digits.");
                var project = await store.LoadAsync(path, token);
                var timelineTicks = TimeMath.ExactTicks(new(checked((long)(seconds * 1_000_000)), TimeBase.Microseconds), project.TimeBase);
                destination = Path.GetFullPath(destination);
                if (File.Exists(destination)) throw new IOException("Output already exists; choose a new output path.");
                var frame = await new TimelinePreviewer(ffmpeg, ffprobe).GetFrameAsync(path,
                    long.Parse(expected, CultureInfo.InvariantCulture), timelineTicks, cancellationToken: token);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, frame.Png, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporaryPath, destination, overwrite: false);
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                Console.WriteLine(JsonSerializer.Serialize(frame.Info, ProjectJson.Default.TimelineFrameInfo));
                break;
            }
        default: Console.Error.WriteLine("Unknown command or arguments. Run roughcut help."); return 2;
    }
    return 0;
}
catch (ProjectValidationException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(exception.Issues, ProjectJson.Default.ValidationIssueArray));
    return 2;
}
catch (ExportRejectedException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(exception.Plan, ProjectJson.Default.ExportPlan));
    return 2;
}
catch (DeliveryRejectedException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(exception.Plan, ProjectJson.Default.DeliveryPlan));
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled or media-tool time limit reached.");
    return 130;
}
catch (Exception exception) when (exception is IOException or ArgumentException or JsonException or
    NotSupportedException or RevisionConflictException or MediaToolException or OverflowException or HttpRequestException or TimeoutException or
    FormatException or KeyNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    // Filesystem and tool diagnostics can contain private paths or media metadata.
    Console.Error.WriteLine(exception switch
    {
        RevisionConflictException => "Project changed; reload before saving.",
        MediaToolException or NotSupportedException => exception.Message,
        JsonException => "Invalid project JSON; check field names, types and required values.",
        _ => "Operation failed; check arguments, file access, output collisions and media-tool availability."
    });
    return 2;
}
