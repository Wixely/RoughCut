using System.Security.Cryptography;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

public sealed class ExportPlanner(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private const int MaxImageBytes = 8 * 1024 * 1024;

    public async Task<ExportPlan> PreflightAsync(EditProject project, string projectPath, CancellationToken cancellationToken = default)
        => (await PrepareAsync(project, projectPath, cancellationToken)).Plan;

    internal async Task<(ExportPlan Plan, ExportSource? Source)> PrepareAsync(EditProject project, string projectPath, CancellationToken cancellationToken)
    {
        ProjectValidator.EnsureValid(project);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Default.EditProject)));
        ExportPlan Rejected(string code, string message) => new(project.ProjectId, project.Revision, hash, project.ExportMode,
            project.TimeBase, false, false, [new(code, "export", message)], [], [], 0, 0, 0);
        if (project.Timeline.Length is 0 or > 32) return (Rejected("unsupported-timeline", "Export requires 1 to 32 clips."), null);
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var appliedReplacements = project.Replacements.Where(item => item.State == "applied").ToArray();
        var activeVideoIds = project.Timeline.Where(clip => assets[clip.AssetId].Kind == "video").Select(clip => clip.AssetId).Distinct().ToArray();
        if (activeVideoIds.Length != 1 || project.Timeline.Any(clip => assets[clip.AssetId].Kind == "audio" ||
            (assets[clip.AssetId].Kind == "video" ? clip.Audio != "source" : clip.Audio != "silence")))
            return (Rejected("unsupported-timeline", "Export requires one video/audio source; inserted PNG images must use explicit silence."), null);
        try
        {
            var videoAsset = assets[activeVideoIds[0]];
            var sourcePath = ProjectFiles.Resolve(projectPath, videoAsset.Path);
            var source = await new ExportProbe(ffmpeg, ffprobe).ReadAsync(sourcePath, inspectPngPackets: true, cancellationToken);
            var video = source.Video;
            if (!string.Equals(video.Info.Sha256, videoAsset.Sha256, StringComparison.OrdinalIgnoreCase))
                return (Rejected("source-changed", "Source fingerprint differs from the project; relink/reinspect before export."), null);
            if (video.Info.Width != videoAsset.Width || video.Info.Height != videoAsset.Height ||
                new MediaTime(videoAsset.Duration, project.TimeBase).CompareTo(new(video.Info.DurationTicks, video.Info.TimeBase)) != 0)
                return (Rejected("source-metadata-mismatch", "Source dimensions or duration differ from the project."), null);
            var voicesByClip = new Dictionary<string, List<ResolvedVoiceReplacement>>(StringComparer.Ordinal);
            foreach (var replacement in appliedReplacements)
            {
                var segment = project.Speech.Single(item => item.Id == replacement.SegmentId);
                if (segment.AssetId != videoAsset.Id)
                    return (Rejected("unsupported-voice-source", "Applied voice replacements must reference the active video/audio source."), null);
                var intersecting = project.Timeline.Where(clip => clip.AssetId == segment.AssetId &&
                    clip.In < segment.End && clip.Out > segment.Start).ToArray();
                if (intersecting.Length != 1 || intersecting[0].In > segment.Start || intersecting[0].Out < segment.End)
                    return (Rejected("unsupported-voice-edit", "Each applied voice interval must be fully retained exactly once inside one video clip."), null);
                var synthesis = project.Synthesis.Single(item => item.ReplacementId == replacement.Id);
                if (replacement.FitPolicy == "exact" && synthesis.ActualDuration != synthesis.RequestedDuration)
                    return (Rejected("unsupported-voice-fit", "Exact voice replacement duration differs from its speech interval."), null);
                if (replacement.FitPolicy == "time-stretch" &&
                    !VoiceFitPolicy.IsWithinLimit(synthesis.RequestedDuration, synthesis.ActualDuration))
                    return (Rejected("unsupported-voice-fit", "Voice time-stretch exceeds the supported 0.8x to 1.25x tempo range."), null);
                var generated = assets[replacement.GeneratedAssetId!];
                if (generated.MediaType != "audio/wav")
                    return (Rejected("unsupported-voice-audio", "Applied voice assets must be PCM WAVE previews."), null);
                var generatedPath = ProjectFiles.Resolve(projectPath, generated.Path);
                var bytes = await ProjectFiles.ReadBoundedAsync(generatedPath, WaveAudio.MaxBytes, cancellationToken);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), generated.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (Rejected("voice-audio-changed", "A generated voice fingerprint differs from the project; synthesize or import it again."), null);
                var wave = WaveAudio.Inspect(bytes);
                var inputDuration = TimeMath.ExactTicks(new(wave.Samples, new(1, wave.SampleRate)), project.TimeBase);
                if (inputDuration != generated.Duration || inputDuration != synthesis.ActualDuration)
                    return (Rejected("voice-metadata-mismatch", "Generated voice duration differs from its project provenance."), null);
                var firstSample = TimeMath.ExactTicks(new(segment.Start, project.TimeBase), new(1, source.SampleRate));
                var endSample = TimeMath.ExactTicks(new(segment.End, project.TimeBase), new(1, source.SampleRate));
                if (!voicesByClip.TryGetValue(intersecting[0].Id, out var resolved))
                    voicesByClip[intersecting[0].Id] = resolved = [];
                resolved.Add(new(replacement.Id, generated.Id, segment.Start, segment.End, firstSample, endSample,
                    replacement.FitPolicy, wave.Samples, wave.SampleRate, wave.Channels));
            }
            foreach (var voices in voicesByClip.Values)
            {
                voices.Sort((left, right) => left.FirstSample.CompareTo(right.FirstSample));
                if (voices.Zip(voices.Skip(1)).Any(pair => pair.First.EndSample > pair.Second.FirstSample))
                    return (Rejected("unsupported-voice-overlap", "Applied voice replacement intervals cannot overlap."), null);
            }
            foreach (var imageAsset in project.Timeline.Select(clip => assets[clip.AssetId]).Where(asset => asset.Kind == "image").DistinctBy(asset => asset.Id))
            {
                if (imageAsset.MediaType != "image/png") return (Rejected("unsupported-image", "Timed images must be PNG assets."), null);
                var imagePath = ProjectFiles.Resolve(projectPath, imageAsset.Path);
                var bytes = await ProjectFiles.ReadBoundedAsync(imagePath, MaxImageBytes, cancellationToken);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), imageAsset.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (Rejected("image-changed", "An image fingerprint differs from the project; import it again."), null);
                var dimensions = PngImage.ReadDimensions(bytes);
                if (dimensions.Width != imageAsset.Width || dimensions.Height != imageAsset.Height)
                    return (Rejected("image-metadata-mismatch", "An image dimension differs from the project."), null);
            }
            if (project.Captions is { } captions)
            {
                if (captions.AssetId != videoAsset.Id || !string.Equals(await MediaReader.FingerprintAsync(ProjectFiles.Resolve(projectPath, captions.SourcePath), cancellationToken),
                    captions.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    return (Rejected("captions-changed", "Caption source fingerprint or asset reference changed; import the captions again."), null);
            }
            var maps = ProjectValidator.MapTimeline(project);
            if (new MediaTime(maps[^1].OutputOut, project.TimeBase).CompareTo(new(60, new(1, 1))) > 0)
                return (Rejected("output-limit", "Output duration must not exceed 60 seconds in this slice."), null);
            var firstVideoClip = project.Timeline.First(clip => assets[clip.AssetId].Kind == "video");
            var width = firstVideoClip.Crop?.Width ?? videoAsset.Width;
            var height = firstVideoClip.Crop?.Height ?? videoAsset.Height;
            var clips = new List<ResolvedClip>();
            var hasImages = project.Timeline.Any(clip => assets[clip.AssetId].Kind == "image");
            var hasVoices = appliedReplacements.Length > 0;
            var copyAudio = source.ExactAudioPackets && !hasImages && !hasVoices;
            var audioBoundaries = source.AudioPackets.Select(packet => packet.Pts)
                .Append(TimeMath.ExactTicks(new(video.Info.DurationTicks, video.Info.TimeBase), source.AudioTimeBase)).ToHashSet();
            var frameDuration = video.Frames[0].Duration;
            for (var i = 0; i < project.Timeline.Length; i++)
            {
                var clip = project.Timeline[i];
                var asset = assets[clip.AssetId];
                if (asset.Kind == "video")
                {
                    if ((clip.Crop?.Width ?? asset.Width) != width || (clip.Crop?.Height ?? asset.Height) != height)
                        return (Rejected("inconsistent-crop-size", "All video clips must have the same output dimensions; inserted images are fitted to that canvas."), null);
                    var start = TimeMath.ExactTicks(new(clip.In, project.TimeBase), video.Info.TimeBase);
                    var end = TimeMath.ExactTicks(new(clip.Out, project.TimeBase), video.Info.TimeBase);
                    if (start % frameDuration != 0 || end % frameDuration != 0)
                        return (Rejected("unsupported-cut", "Cut is not on an exact displayed-frame boundary; adjust the edit explicitly. This slice never silently snaps."), null);
                    var audioStart = TimeMath.ExactTicks(new(clip.In, project.TimeBase), source.AudioTimeBase);
                    var audioEnd = TimeMath.ExactTicks(new(clip.Out, project.TimeBase), source.AudioTimeBase);
                    copyAudio &= audioBoundaries.Contains(audioStart) && audioBoundaries.Contains(audioEnd);
                    var clipVoices = voicesByClip.TryGetValue(clip.Id, out var replacements)
                        ? replacements.ToArray() : [];
                    clips.Add(new(clip.Id, clip.AssetId, clip.In, clip.Out, clip.In, clip.Out, maps[i].OutputIn, maps[i].OutputOut,
                        checked((int)(start / frameDuration)), checked((int)(end / frameDuration)),
                        TimeMath.ExactTicks(new(clip.In, project.TimeBase), new(1, source.SampleRate)),
                        TimeMath.ExactTicks(new(clip.Out, project.TimeBase), new(1, source.SampleRate)), clip.Crop, clipVoices));
                }
                else
                {
                    var duration = TimeMath.ExactTicks(new(clip.Out, project.TimeBase), video.Info.TimeBase);
                    if (duration % frameDuration != 0)
                        return (Rejected("unsupported-image-duration", "Image hold duration must align exactly to the source frame cadence."), null);
                    var samples = TimeMath.ExactTicks(new(clip.Out, project.TimeBase), new(1, source.SampleRate));
                    clips.Add(new(clip.Id, clip.AssetId, 0, clip.Out, 0, clip.Out, maps[i].OutputIn, maps[i].OutputOut,
                        0, checked((int)(duration / frameDuration)), 0, samples, clip.Crop, []));
                }
            }
            var copyVideo = !hasImages && source.CompletePngPackets && project.Timeline.All(clip => clip.Crop is null);
            StreamDecision[] streams =
            [
                new("video", hasImages ? $"{video.Info.Codec}+png" : video.Info.Codec, copyVideo ? "png" : "ffv1", copyVideo ? "copy" : "encode",
                    copyVideo ? "Each packet contains a complete independent RGB8 PNG; cuts match its display interval." :
                    hasImages ? "Timed images require a fitted lossless render of the complete output timeline." : "Crop or unproven copy codec requires lossless FFV1 encoding; processing is slower."),
                new("audio", hasVoices ? "pcm_s16le+qwen-wav" : hasImages ? "pcm_s16le+silence" : "pcm_s16le", "pcm_s16le", copyAudio ? "copy" : "encode",
                    copyAudio ? "PCM packets have exact sample timing and both cuts align to packet boundaries." :
                    hasVoices ? "Applied voice intervals replace isolated source dialogue with fitted, validated PCM; the complete audio timeline is encoded." :
                    hasImages ? "Timed images insert exact-duration PCM silence, so the complete audio timeline is encoded." : "Cuts or container timestamp rounding require exact sample trimming and PCM encoding.")
            ];
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
