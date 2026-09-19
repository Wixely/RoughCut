namespace RoughCut.Core;

public static class ProjectValidator
{
    public static ValidationIssue[] Validate(EditProject project)
    {
        var issues = new List<ValidationIssue>();
        void Check(bool condition, string location, string message, string code = "invalid-project")
        {
            if (!condition) issues.Add(new(code, location, message));
        }
        Check(project.SchemaVersion == 1, "schemaVersion", "Only schema version 1 is supported.", "unsupported-version");
        Check(!string.IsNullOrWhiteSpace(project.ProjectId), "projectId", "A project ID is required.");
        Check(project.Revision > 0, "revision", "Revision must be positive.");
        Check(project.TimeBase.IsValid, "timeBase", "Numerator and denominator must be positive.");
        Check(project.ExportMode is "prefer-stream-copy" or "copy-only" or "exact-edit", "exportMode", "Unknown export mode.");
        if (project.Assets.Any(x => x is null) || project.Timeline.Any(x => x is null) ||
            project.Speakers.Any(x => x is null) || project.Speech.Any(x => x is null) ||
            project.Evidence.Any(x => x is null) || project.Observations.Any(x => x is null) ||
            project.Proposals.Any(x => x is null) ||
            project.Voices.Any(x => x is null) || project.Replacements.Any(x => x is null) ||
            project.Provenance.Any(x => x is null))
        {
            issues.Add(new("invalid-project", "collections", "Null collection entries are not allowed."));
            return issues.ToArray();
        }

        var assets = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Id(string id, string location) => Check(!string.IsNullOrWhiteSpace(id) && ids.Add(id), location, "IDs must be nonempty and unique within each collection.");
        foreach (var asset in project.Assets)
        {
            Id(asset.Id, "assets.id");
            assets.TryAdd(asset.Id, asset);
            Check(asset.Kind is "video" or "image" or "audio", asset.Id, "Unknown asset kind.");
            Check(IsPortablePath(asset.Path), asset.Id, "Asset path must be a portable relative path within the project directory.");
            Check(asset.Sha256.Length == 64 && asset.Sha256.All(Uri.IsHexDigit), asset.Id, "SHA-256 must have 64 hexadecimal digits.");
            Check(asset.Kind == "image" ? asset.Duration == 0 : asset.Duration > 0, asset.Id, "Image duration must be zero; timed media duration must be positive.");
            Check(asset.Kind == "audio" || (asset.Width > 0 && asset.Height > 0), asset.Id, "Visual assets require positive dimensions.");
            Check(!string.IsNullOrWhiteSpace(asset.MediaType), asset.Id, "Media type is required.");
        }
        ids.Clear();
        long duration = 0;
        foreach (var clip in project.Timeline)
        {
            Id(clip.Id, "timeline.id");
            Check(clip.In >= 0 && clip.Out > clip.In, clip.Id, "Intervals must be nonnegative and nonempty.");
            Check(clip.Fit is "contain" or "cover", clip.Id, "Unknown image fit policy.");
            Check(clip.Audio is "source" or "silence", clip.Id, "Unknown audio policy.");
            if (!assets.TryGetValue(clip.AssetId, out var asset))
                Check(false, clip.Id, "Unknown asset reference.");
            else
            {
                Check(asset.Kind != "audio", clip.Id, "Audio-only timeline clips are not supported in schema 1.");
                Check(asset.Kind == "image" ? clip.In == 0 && clip.Audio == "silence" : clip.Out <= asset.Duration,
                    clip.Id, "Video intervals must fit the source; image clips start at zero with explicit silence.");
                if (clip.Crop is { } crop)
                    Check(crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0 &&
                        (long)crop.X + crop.Width <= asset.Width && (long)crop.Y + crop.Height <= asset.Height,
                        clip.Id, "Crop must fit within source display dimensions.");
            }
            try { duration = checked(duration + checked(clip.Out - clip.In)); }
            catch (OverflowException) { Check(false, clip.Id, "Timeline duration exceeds the supported integer range."); }
        }
        ids.Clear();
        foreach (var speaker in project.Speakers)
        {
            Id(speaker.Id, "speakers.id");
            Check(!string.IsNullOrWhiteSpace(speaker.Label), speaker.Id, "Speaker label is required.");
        }
        var speakerIds = project.Speakers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        ids.Clear();
        foreach (var segment in project.Speech)
        {
            Id(segment.Id, "speech.id");
            Check(assets.TryGetValue(segment.AssetId, out var asset) && asset.Kind is "video" or "audio" &&
                segment.Start >= 0 && segment.End > segment.Start && segment.End <= asset.Duration,
                segment.Id, "Speech interval must reference valid timed media.");
            Check(segment.SpeakerIds.All(speakerIds.Contains) && segment.SpeakerIds.Distinct().Count() == segment.SpeakerIds.Length,
                segment.Id, "Speaker references must exist and be unique.");
            Check(segment.Assignment is "unknown" or "inferred" or "corrected", segment.Id, "Unknown speaker assignment state.");
            Check(segment.SpeakerIds.Length <= 1 || segment.Overlap, segment.Id, "Multiple speakers require an overlap annotation.");
        }
        if (project.Transcription is { } transcription)
        {
            Check(assets.TryGetValue(transcription.AssetId, out var sourceAsset) && sourceAsset.Kind is "video" or "audio",
                "transcription.assetId", "Transcription provenance must reference timed media.");
            Check(sourceAsset is not null && string.Equals(sourceAsset.Sha256, transcription.SourceSha256, StringComparison.OrdinalIgnoreCase),
                "transcription.sourceSha256", "Transcription source fingerprint must match its asset.");
            Check(!string.IsNullOrWhiteSpace(transcription.Provider) && !string.IsNullOrWhiteSpace(transcription.Model) &&
                !string.IsNullOrWhiteSpace(transcription.Language), "transcription", "Transcription provider, model and language are required.");
            Check(transcription.ChunkSeconds is >= 5 and <= 30, "transcription.chunkSeconds", "Transcription chunk size must be between 5 and 30 seconds.");
        }
        Check(project.Evidence.Length <= 5000 && project.Observations.Length <= 2000 && project.Proposals.Length <= 5000,
            "analysis", "Analysis exceeds the bounded evidence, observation or proposal count.");
        var speechById = new Dictionary<string, SpeechSegment>(StringComparer.Ordinal);
        foreach (var segment in project.Speech) speechById.TryAdd(segment.Id, segment);
        ids.Clear();
        foreach (var evidence in project.Evidence)
        {
            Id(evidence.Id, "evidence.id");
            Check(assets.TryGetValue(evidence.AssetId, out var asset) && asset.Kind is "video" or "audio" &&
                evidence.Start >= 0 && evidence.End > evidence.Start && evidence.End <= asset.Duration,
                evidence.Id, "Evidence interval must reference valid timed media.");
            Check(evidence.Kind is "transcript" or "frame" or "chapter" or "activity" or "metadata",
                evidence.Id, "Unknown evidence kind.");
            Check(!string.IsNullOrWhiteSpace(evidence.Summary) && evidence.Summary.Length <= 2000,
                evidence.Id, "Evidence summary must contain at most 2000 characters.");
            Check(evidence.SpeechSegmentIds.Length <= 100 && evidence.SpeechSegmentIds.Distinct().Count() == evidence.SpeechSegmentIds.Length &&
                evidence.SpeechSegmentIds.All(id => speechById.TryGetValue(id, out var segment) && segment.AssetId == evidence.AssetId),
                evidence.Id, "Evidence speech references must be unique and belong to the same asset.");
            Check(evidence.FrameTicks.Length <= 100 && evidence.FrameTicks.Distinct().Count() == evidence.FrameTicks.Length &&
                evidence.FrameTicks.All(tick => tick >= evidence.Start && tick < evidence.End),
                evidence.Id, "Evidence frame timestamps must be unique and fall inside the evidence interval.");
        }
        var evidenceById = new Dictionary<string, AnalysisEvidence>(StringComparer.Ordinal);
        foreach (var evidence in project.Evidence) evidenceById.TryAdd(evidence.Id, evidence);
        ids.Clear();
        foreach (var observation in project.Observations)
        {
            Id(observation.Id, "observations.id");
            Check(assets.TryGetValue(observation.AssetId, out var asset) && asset.Kind is "video" or "audio" &&
                observation.Start >= 0 && observation.End > observation.Start && observation.End <= asset.Duration,
                observation.Id, "Observation interval must reference valid timed media.");
            Check(!string.IsNullOrWhiteSpace(observation.Label) && observation.Label.Length <= 128 &&
                !string.IsNullOrWhiteSpace(observation.Summary) && observation.Summary.Length <= 2000,
                observation.Id, "Observation label and summary are required and bounded.");
            Check(observation.Certainty is "low" or "medium" or "high", observation.Id, "Unknown observation certainty.");
            Check(observation.RecommendedAction is "retain" or "remove" or "review", observation.Id, "Unknown recommended action.");
            Check(observation.EvidenceIds is { Length: > 0 and <= 100 } && observation.EvidenceIds.Distinct().Count() == observation.EvidenceIds.Length &&
                observation.EvidenceIds.All(id => evidenceById.TryGetValue(id, out var evidence) && evidence.AssetId == observation.AssetId &&
                    evidence.Start < observation.End && evidence.End > observation.Start),
                observation.Id, "Observation evidence must be unique, overlap it and belong to the same asset.");
        }
        var observationById = new Dictionary<string, AnalysisObservation>(StringComparer.Ordinal);
        foreach (var observation in project.Observations) observationById.TryAdd(observation.Id, observation);
        var clipsById = new Dictionary<string, TimelineClip>(StringComparer.Ordinal);
        foreach (var clip in project.Timeline) clipsById.TryAdd(clip.Id, clip);
        ids.Clear();
        foreach (var proposal in project.Proposals)
        {
            Id(proposal.Id, "proposals.id");
            Check(observationById.TryGetValue(proposal.ObservationId, out var observation), proposal.Id, "Proposal must reference an observation.");
            Check(clipsById.TryGetValue(proposal.ClipId, out var clip) && proposal.In >= clip.In && proposal.Out <= clip.Out && proposal.In < proposal.Out,
                proposal.Id, "Proposal interval must fit its timeline clip.");
            Check(proposal.RecommendedAction is "retain" or "remove" or "review" && proposal.Decision is "retain" or "remove" or "review",
                proposal.Id, "Unknown proposal action or decision.");
            Check(!string.IsNullOrWhiteSpace(proposal.Reason) && proposal.Reason.Length <= 2000 &&
                proposal.Certainty is "low" or "medium" or "high", proposal.Id, "Proposal reason and certainty are required and bounded.");
            Check(observation is not null && clip is not null && clip.AssetId == observation.AssetId &&
                proposal.In >= observation.Start && proposal.Out <= observation.End &&
                proposal.Certainty == observation.Certainty &&
                proposal.RecommendedAction == observation.RecommendedAction &&
                proposal.EvidenceIds.SequenceEqual(observation.EvidenceIds), proposal.Id, "Proposal must preserve its observation judgement and evidence.");
        }
        if (project.Analysis is { } analysis)
        {
            Check(assets.TryGetValue(analysis.AssetId, out var sourceAsset) && sourceAsset.Kind is "video" or "audio",
                "analysis.assetId", "Analysis provenance must reference timed media.");
            Check(sourceAsset is not null && string.Equals(sourceAsset.Sha256, analysis.SourceSha256, StringComparison.OrdinalIgnoreCase),
                "analysis.sourceSha256", "Analysis source fingerprint must match its asset.");
            Check(!string.IsNullOrWhiteSpace(analysis.Provider) && analysis.Provider.Length <= 128 &&
                !string.IsNullOrWhiteSpace(analysis.Model) && analysis.Model.Length <= 256 &&
                !string.IsNullOrWhiteSpace(analysis.Prompt) && analysis.Prompt.Length <= 4000,
                "analysis", "Analysis provider, model and bounded prompt are required.");
            Check(analysis.Policy is "review" or "auto-high-certainty", "analysis.policy", "Unknown analysis policy.");
            Check(analysis.ProposalRevision > 0 && analysis.ProposalRevision <= project.Revision,
                "analysis.proposalRevision", "Analysis proposal revision is invalid.");
            foreach (var proposal in project.Proposals)
            {
                var expectedDecision = proposal.RecommendedAction switch
                {
                    "retain" => "retain",
                    "remove" when proposal.Certainty == "high" && analysis.Policy == "auto-high-certainty" => "remove",
                    _ => "review"
                };
                Check(proposal.Decision == expectedDecision, proposal.Id,
                    "Proposal decision does not follow the recorded conservative analysis policy.");
            }
        }
        else Check(project.Evidence.Length == 0 && project.Observations.Length == 0 && project.Proposals.Length == 0,
            "analysis", "Analysis content requires provenance.");
        ids.Clear();
        foreach (var voice in project.Voices)
        {
            Id(voice.Id, "voices.id");
            Check(speakerIds.Contains(voice.SpeakerId), voice.Id, "Unknown speaker reference.");
            Check(voice.Provider == "qwen-tts" && !string.IsNullOrWhiteSpace(voice.Voice), voice.Id, "A Qwen TTS voice configuration is required.");
        }
        ids.Clear();
        foreach (var replacement in project.Replacements)
        {
            Id(replacement.Id, "replacements.id");
            var segment = project.Speech.FirstOrDefault(s => s.Id == replacement.SegmentId);
            var voice = project.Voices.FirstOrDefault(v => v.Id == replacement.MappingId);
            Check(segment is not null && voice is not null && segment.SpeakerIds.Contains(voice.SpeakerId),
                replacement.Id, "Replacement voice must match a speaker assigned to the speech segment.");
            Check(!string.IsNullOrWhiteSpace(replacement.Text), replacement.Id, "Synthesis text is required.");
            Check(replacement.GeneratedAssetId is null || (assets.TryGetValue(replacement.GeneratedAssetId, out var audio) && audio.Kind == "audio"),
                replacement.Id, "Generated speech must reference an audio asset.");
        }
        foreach (var provenance in project.Provenance)
            Check(assets.ContainsKey(provenance.AssetId) && (provenance.SourceAssetId is null || assets.ContainsKey(provenance.SourceAssetId)),
                "provenance", "Unknown provenance asset reference.");
        if (project.Captions is { } captions)
        {
            Check(assets.TryGetValue(captions.AssetId, out var captionAsset) && captionAsset.Kind is "video" or "audio",
                "captions.assetId", "Caption track must reference timed media.");
            Check(IsPortablePath(captions.SourcePath), "captions.sourcePath", "Caption source path must be portable.");
            Check(captions.SourceSha256.Length == 64 && captions.SourceSha256.All(Uri.IsHexDigit), "captions.sourceSha256", "Invalid caption source hash.");
            Check(captions.TimeBase.IsValid, "captions.timeBase", "Caption time base must be positive.");
            Check(captions.SourceKind is "supplied" or "manual" or "automatic" or "local-stt",
                "captions.sourceKind", "Unknown caption source kind.");
            Check(!string.IsNullOrWhiteSpace(captions.Language) && captions.Language.Length <= 35 &&
                captions.Language.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
                "captions.language", "Caption language must be a short language tag.");
            Check(captions.Selection is "explicit" or "recommended" or "override",
                "captions.selection", "Unknown caption selection policy.");
            Check(captions.CandidateId is null || !string.IsNullOrWhiteSpace(captions.CandidateId),
                "captions.candidateId", "Caption candidate ID cannot be empty.");
            Check(captions.CoverageBasisPoints is >= 0 and <= 10_000,
                "captions.coverageBasisPoints", "Caption coverage must be between 0 and 10,000 basis points.");
            Check(captions.Cues.Length <= 10_000, "captions.cues", "Too many caption cues.");
            ids.Clear();
            foreach (var cue in captions.Cues)
            {
                if (cue is null) { Check(false, "captions.cues", "Null cue is invalid."); continue; }
                Id(cue.Id, "captions.cues.id");
                Check(cue.Start >= 0 && cue.End > cue.Start, cue.Id, "Caption interval must be nonempty and nonnegative.");
                Check(!string.IsNullOrWhiteSpace(cue.Text) && cue.Text.Length <= 8000 && !cue.Text.Contains('\0'), cue.Id, "Caption text is empty, too long or invalid.");
                Check(!cue.Text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) &&
                    cue.Text.Split('\n').All(line => !string.IsNullOrWhiteSpace(line)), cue.Id, "Caption text must contain nonempty lines without unsupported control characters.");
                if (captionAsset is not null && project.TimeBase.IsValid && captions.TimeBase.IsValid)
                    Check(new MediaTime(cue.End, captions.TimeBase).CompareTo(new(captionAsset.Duration, project.TimeBase)) <= 0,
                        cue.Id, "Caption exceeds the source duration.");
            }
        }
        return issues.ToArray();
    }

    public static void EnsureValid(EditProject project)
    {
        var issues = Validate(project);
        if (issues.Length != 0) throw new ProjectValidationException(issues);
    }

    public static bool IsPortablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') ||
            path.IndexOfAny([':', '\0', '<', '>', '"', '|', '?', '*']) >= 0 || path.Any(char.IsControl)) return false;
        return path.Split('/').All(part => part.Length > 0 && part is not "." and not ".." &&
            !part.EndsWith(' ') && !part.EndsWith('.'));
    }

    public static TimelineMapping[] MapTimeline(EditProject project)
    {
        EnsureValid(project);
        long position = 0;
        return project.Timeline.Select(clip =>
        {
            var start = position;
            position = checked(position + checked(clip.Out - clip.In));
            return new TimelineMapping(clip.Id, clip.AssetId, clip.In, clip.Out, start, position);
        }).ToArray();
    }
}

public sealed class ProjectValidationException(ValidationIssue[] issues)
    : Exception("Project validation failed.")
{
    public ValidationIssue[] Issues { get; } = issues;
}
