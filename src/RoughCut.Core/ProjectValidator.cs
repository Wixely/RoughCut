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
