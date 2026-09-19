using System.Security.Cryptography;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

public sealed class ExportPlanner(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    public async Task<ExportPlan> PreflightAsync(EditProject project, string projectPath, CancellationToken cancellationToken = default)
        => (await PrepareAsync(project, projectPath, cancellationToken)).Plan;

    internal async Task<(ExportPlan Plan, ExportSource? Source)> PrepareAsync(EditProject project, string projectPath, CancellationToken cancellationToken)
    {
        ProjectValidator.EnsureValid(project);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Default.EditProject)));
        ExportPlan Rejected(string code, string message) => new(project.ProjectId, project.Revision, hash, project.ExportMode,
            project.TimeBase, false, false, [new(code, "export", message)], [], [], 0, 0, 0);
        if (project.Timeline.Length is 0 or > 32) return (Rejected("unsupported-timeline", "Export requires 1 to 32 clips."), null);
        if (project.Replacements.Length != 0) return (Rejected("unsupported-voice-replacement", "Voice replacement rendering is not implemented; it also cannot be copy-only."), null);
        var assetIds = project.Timeline.Select(c => c.AssetId).Distinct().ToArray();
        if (assetIds.Length != 1 || project.Assets.Single(a => a.Id == assetIds[0]).Kind != "video" || project.Timeline.Any(c => c.Audio != "source"))
            return (Rejected("unsupported-timeline", "This slice supports one video source with its original audio; still images, multiple sources and silence edits remain unsupported."), null);
        try
        {
            var asset = project.Assets.Single(a => a.Id == assetIds[0]);
            var path = ProjectFiles.Resolve(projectPath, asset.Path);
            var source = await new ExportProbe(ffmpeg, ffprobe).ReadAsync(path, inspectPngPackets: true, cancellationToken);
            var video = source.Video;
            if (!string.Equals(video.Info.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                return (Rejected("source-changed", "Source fingerprint differs from the project; relink/reinspect before export."), null);
            if (video.Info.Width != asset.Width || video.Info.Height != asset.Height ||
                new MediaTime(asset.Duration, project.TimeBase).CompareTo(new(video.Info.DurationTicks, video.Info.TimeBase)) != 0)
                return (Rejected("source-metadata-mismatch", "Source dimensions or duration differ from the project."), null);
            if (project.Captions is { } captions)
            {
                if (captions.AssetId != asset.Id || !string.Equals(await MediaReader.FingerprintAsync(ProjectFiles.Resolve(projectPath, captions.SourcePath), cancellationToken),
                    captions.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    return (Rejected("captions-changed", "Caption source fingerprint or asset reference changed; import the captions again."), null);
            }
            var maps = ProjectValidator.MapTimeline(project);
            if (new MediaTime(maps[^1].OutputOut, project.TimeBase).CompareTo(new(60, new(1, 1))) > 0)
                return (Rejected("output-limit", "Output duration must not exceed 60 seconds in this slice."), null);
            int width = project.Timeline[0].Crop?.Width ?? asset.Width;
            int height = project.Timeline[0].Crop?.Height ?? asset.Height;
            var clips = new List<ResolvedClip>();
            bool copyAudio = source.ExactAudioPackets;
            var audioBoundaries = source.AudioPackets.Select(p => p.Pts).Append(TimeMath.ExactTicks(new(video.Info.DurationTicks, video.Info.TimeBase), source.AudioTimeBase)).ToHashSet();
            for (int i = 0; i < project.Timeline.Length; i++)
            {
                var clip = project.Timeline[i];
                if ((clip.Crop?.Width ?? asset.Width) != width || (clip.Crop?.Height ?? asset.Height) != height)
                    return (Rejected("inconsistent-crop-size", "All clips must have the same output dimensions; use consistent crop sizes in this slice."), null);
                var start = TimeMath.ExactTicks(new(clip.In, project.TimeBase), video.Info.TimeBase);
                var end = TimeMath.ExactTicks(new(clip.Out, project.TimeBase), video.Info.TimeBase);
                long frameDuration = video.Frames[0].Duration;
                if (start % frameDuration != 0 || end % frameDuration != 0)
                    return (Rejected("unsupported-cut", "Cut is not on an exact displayed-frame boundary; adjust the edit explicitly. This slice never silently snaps."), null);
                var startMs = TimeMath.ExactTicks(new(clip.In, project.TimeBase), source.AudioTimeBase);
                var endMs = TimeMath.ExactTicks(new(clip.Out, project.TimeBase), source.AudioTimeBase);
                copyAudio &= audioBoundaries.Contains(startMs) && audioBoundaries.Contains(endMs);
                clips.Add(new(clip.Id, clip.AssetId, clip.In, clip.Out, clip.In, clip.Out, maps[i].OutputIn, maps[i].OutputOut,
                    checked((int)(start / frameDuration)), checked((int)(end / frameDuration)),
                    TimeMath.ExactTicks(new(clip.In, project.TimeBase), new(1, source.SampleRate)),
                    TimeMath.ExactTicks(new(clip.Out, project.TimeBase), new(1, source.SampleRate)), clip.Crop));
            }
            bool copyVideo = source.CompletePngPackets && project.Timeline.All(c => c.Crop is null);
            var streams = new StreamDecision[]
            {
                new("video", video.Info.Codec, copyVideo ? "png" : "ffv1", copyVideo ? "copy" : "encode",
                    copyVideo ? "Each packet contains a complete independent RGB8 PNG; cuts match its display interval." : "Crop or unproven copy codec requires lossless FFV1 encoding; processing is slower."),
                new("audio", "pcm_s16le", "pcm_s16le", copyAudio ? "copy" : "encode",
                    copyAudio ? "PCM packets have exact sample timing and both cuts align to packet boundaries." : "Cuts or container timestamp rounding require exact sample trimming and PCM encoding.")
            };
            var requiresEncoding = !copyVideo || !copyAudio;
            var issues = project.ExportMode == "copy-only" && requiresEncoding
                ? new ValidationIssue[] { new("unsupported-copy-only", "exportMode", "Strict copy-only cannot perform this edit. Use an encoding-permitted mode and explicitly allow encoding, or adjust the source/edit.") } : [];
            return (new(project.ProjectId, project.Revision, hash, project.ExportMode, project.TimeBase,
                issues.Length == 0, requiresEncoding, issues, streams, clips.ToArray(), width, height, maps[^1].OutputOut), source);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or MediaToolException or
            IOException or JsonException or KeyNotFoundException or FormatException or OverflowException)
        {
            return (Rejected("unsupported-media", exception is NotSupportedException ? exception.Message :
                "Media could not be validated within the configured limits; check source files, stream layout and tool diagnostics."), null);
        }
    }
}
