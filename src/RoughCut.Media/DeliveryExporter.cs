using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

internal sealed record DeliveryProbe(double Seconds, bool HasAudio, string? VideoCodec, string? AudioCodec);

/// Renders the retained timeline into a portable H.264/AAC file by decoding, cutting and re-encoding.
/// This is deliberately not the strict copy path. That path proves retained packets and samples survive
/// untouched, which it can only do inside the narrow matrix it validates, so it refuses ordinary media.
/// Delivery makes the opposite trade: it always re-encodes, so it renders anything FFmpeg decodes, and
/// what it claims is a faithful edit of the requested intervals at the length the timeline says — never
/// an untouched copy of the source.
public sealed class DeliveryExporter(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    public const int MaxClips = 400;
    public const long MaxOutputBytes = 8L * 1024 * 1024 * 1024;
    private const string Containers = "mov,matroska,webm,avi,mpegts,ogg,flv";

    /// Four hours, which is far beyond anything acquisition or the review window produces today.
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    public async Task<DeliveryPlan> PreflightAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        return Plan(await new ProjectStore().LoadAsync(projectPath, cancellationToken), projectPath);
    }

    /// Preflight is pure and never starts the media tools, so a caller can see what delivery would do
    /// before paying for an encode. Everything that needs the decoder is checked inside ExportAsync.
    public static DeliveryPlan Plan(EditProject project, string projectPath)
    {
        ProjectValidator.EnsureValid(project);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Default.EditProject)));
        DeliveryPlan Rejected(string code, string location, string message) =>
            new(project.ProjectId, project.Revision, hash, false, [new(code, location, message)], [], 0, 0, 0);
        if (project.Timeline.Length is 0 or > MaxClips)
            return Rejected("unsupported-timeline", "timeline", $"Delivery renders 1 to {MaxClips} clips.");
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        if (project.Timeline.Any(clip => assets[clip.AssetId].Kind != "video"))
            return Rejected("unsupported-timeline", "timeline",
                "Delivery renders video clips only in this slice; timed images render through the validated export path.");
        if (project.Replacements.Any(replacement => replacement.State == "applied"))
            return Rejected("unsupported-replacements", "replacements",
                "Delivery does not render applied voice replacements in this slice; use the validated export path.");
        var maps = ProjectValidator.MapTimeline(project);
        var duration = maps[^1].OutputOut;
        if (new MediaTime(duration, project.TimeBase).CompareTo(new((long)MaxDuration.TotalSeconds, new(1, 1))) > 0)
            return Rejected("output-limit", "timeline", $"Delivered duration must not exceed {MaxDuration.TotalHours:0} hours.");
        foreach (var asset in project.Timeline.Select(clip => assets[clip.AssetId]).DistinctBy(asset => asset.Id))
            if (!File.Exists(ProjectFiles.Resolve(projectPath, asset.Path)))
                return Rejected("missing-media", asset.Id, "Source media for a timeline asset is missing from the project directory.");
        // Every clip is fitted into one frame size, so a timeline spanning differently sized sources still
        // produces one file. The first clip sets that size, as it does in the validated export path.
        var first = assets[project.Timeline[0].AssetId];
        var width = Even(project.Timeline[0].Crop?.Width ?? first.Width);
        var height = Even(project.Timeline[0].Crop?.Height ?? first.Height);
        if (width < 16 || height < 16)
            return Rejected("unsupported-size", "timeline", "Delivered frames must be at least 16 by 16 pixels.");
        var clips = maps.Select(map =>
        {
            var clip = project.Timeline.Single(item => item.Id == map.ClipId);
            return new DeliveryClip(map.ClipId, map.AssetId, map.SourceIn, map.SourceOut, map.OutputIn, map.OutputOut,
                clip.Crop, clip.Fit, clip.Audio);
        }).ToArray();
        return new(project.ProjectId, project.Revision, hash, true, [], clips, width, height, duration);
    }

    public async Task<DeliveryReport> ExportAsync(string projectPath, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (File.Exists(outputDirectory) || Directory.Exists(outputDirectory))
            throw new IOException("Output directory already exists; choose a new destination.");
        var project = await new ProjectStore().LoadAsync(projectPath, cancellationToken);
        var plan = Plan(project, projectPath);
        if (!plan.Supported) throw new DeliveryRejectedException(plan);
        var assets = plan.Clips.Select(clip => clip.AssetId).Distinct(StringComparer.Ordinal)
            .Select(id => project.Assets.Single(asset => asset.Id == id)).ToArray();
        var paths = assets.ToDictionary(asset => asset.Id, asset => ProjectFiles.Resolve(projectPath, asset.Path), StringComparer.Ordinal);
        // Fingerprinting before the encode fails a relinked or edited source in seconds instead of hours.
        foreach (var asset in assets)
            if (!string.Equals(await MediaReader.FingerprintAsync(paths[asset.Id], cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Source fingerprint for asset {asset.Id} differs from the project; relink or reinspect before delivery.");
        var probes = new Dictionary<string, DeliveryProbe>(StringComparer.Ordinal);
        foreach (var asset in assets) probes[asset.Id] = await ProbeAsync(paths[asset.Id], cancellationToken);
        var silenced = assets.Where(asset => !probes[asset.Id].HasAudio &&
            plan.Clips.Any(clip => clip.AssetId == asset.Id && clip.Audio == "source")).Select(asset => asset.Id).ToArray();

        var parent = Path.GetDirectoryName(outputDirectory)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".roughcut-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var output = Path.Combine(staging, "video.mp4");
            var order = assets.Select(asset => asset.Id).ToArray();
            await ToolProcess.RunAsync(ffmpeg,
                BuildArguments(project, plan, order, id => paths[id], id => probes[id].HasAudio, output),
                timeout: MaxDuration, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new FileInfo(output).Length;
            if (bytes <= 0 || bytes >= MaxOutputBytes)
                throw new InvalidDataException("Delivered file is empty or reached the 8 GiB output budget.");
            var delivered = await ProbeAsync(output, cancellationToken);
            if (delivered.VideoCodec != "h264" || delivered.AudioCodec != "aac")
                throw new InvalidDataException("Delivered file does not carry the promised H.264 video and AAC audio.");
            var expected = Seconds(plan.Duration, project.TimeBase);
            // A re-encode still has to land on the timeline it claims. Each cut can move by at most the
            // frame it falls inside, so the tolerance grows with the number of cuts rather than being fixed.
            if (Math.Abs(delivered.Seconds - expected) > 0.25 + 0.05 * plan.Clips.Length)
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Delivered duration {delivered.Seconds:0.000}s does not match the timeline's {expected:0.000}s."));
            foreach (var asset in assets)
                if (!string.Equals(await MediaReader.FingerprintAsync(paths[asset.Id], cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A source changed during delivery; output was not published.");
            var captions = Captions.Retime(project);
            var report = new DeliveryReport(1, plan, assets.Select(asset => asset.Sha256).ToArray(), silenced,
                await MediaReader.FingerprintAsync(output, cancellationToken), await VersionAsync(cancellationToken),
                expected, delivered.Seconds, bytes, captions);
            await File.WriteAllTextAsync(Path.Combine(staging, "delivery.json"),
                JsonSerializer.Serialize(report, ProjectJson.Default.DeliveryReport), cancellationToken);
            if (captions.Length > 0)
                await File.WriteAllTextAsync(Path.Combine(staging, "captions.srt"), Captions.WriteSrt(captions),
                    new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, outputDirectory); // The bundle becomes visible at once, with no overwrite.
            return report;
        }
        finally
        {
            // Only direct files created inside this unique staging directory are removed; no recursive deletion.
            if (Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    /// One filter graph cuts each clip out of its source, fits it to the delivered frame and concatenates
    /// the result, so ordering and cuts are applied exactly once, by the encoder, from decoded frames.
    internal static string[] BuildArguments(EditProject project, DeliveryPlan plan, string[] assets,
        Func<string, string> path, Func<string, bool> hasAudio, string output)
    {
        var arguments = new List<string> { "-v", "error", "-nostdin", "-xerror", "-n" };
        foreach (var id in assets)
            arguments.AddRange(["-protocol_whitelist", "file", "-format_whitelist", Containers, "-noautorotate", "-i", path(id)]);
        var silence = assets.Length;
        if (plan.Clips.Any(clip => clip.Audio == "silence" || !hasAudio(clip.AssetId)))
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);

        var graph = new StringBuilder();
        var concat = new StringBuilder();
        for (var index = 0; index < plan.Clips.Length; index++)
        {
            var clip = plan.Clips[index];
            var input = Array.IndexOf(assets, clip.AssetId);
            var start = Text(Seconds(clip.SourceIn, project.TimeBase));
            var end = Text(Seconds(clip.SourceOut, project.TimeBase));
            graph.Append(FormattableString.Invariant($"[{input}:v]trim=start={start}:end={end},setpts=PTS-STARTPTS,"))
                .Append(Fit(clip, plan.Width, plan.Height))
                .Append(FormattableString.Invariant($"[v{index}];"));
            // Silence is generated per clip, so the stand-in runs exactly as long as the clip it stands in for.
            graph.Append(clip.Audio == "silence" || !hasAudio(clip.AssetId)
                    ? FormattableString.Invariant($"[{silence}:a]atrim=start=0:end={Text(Seconds(clip.OutputOut - clip.OutputIn, project.TimeBase))},")
                    : FormattableString.Invariant($"[{input}:a]atrim=start={start}:end={end},"))
                .Append(FormattableString.Invariant(
                    $"asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a{index}];"));
            concat.Append(FormattableString.Invariant($"[v{index}][a{index}]"));
        }
        graph.Append(concat).Append(FormattableString.Invariant($"concat=n={plan.Clips.Length}:v=1:a=1[v][a]"));

        arguments.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]", "-map", "[a]", "-fps_mode", "vfr",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-fs", MaxOutputBytes.ToString(CultureInfo.InvariantCulture), "-f", "mp4", output]);
        return arguments.ToArray();
    }

    private static string Fit(DeliveryClip clip, int width, int height)
    {
        var filters = new List<string>();
        if (clip.Crop is { } crop)
            filters.Add(FormattableString.Invariant($"crop=w={crop.Width}:h={crop.Height}:x={crop.X}:y={crop.Y}:exact=1"));
        if (clip.Fit == "cover")
        {
            filters.Add(FormattableString.Invariant($"scale=w={width}:h={height}:force_original_aspect_ratio=increase"));
            filters.Add(FormattableString.Invariant($"crop=w={width}:h={height}:x=(iw-ow)/2:y=(ih-oh)/2:exact=1"));
        }
        else
        {
            filters.Add(FormattableString.Invariant($"scale=w={width}:h={height}:force_original_aspect_ratio=decrease"));
            filters.Add(FormattableString.Invariant($"pad=w={width}:h={height}:x=(ow-iw)/2:y=(oh-ih)/2:color=black"));
        }
        filters.Add("setsar=1");
        return string.Join(',', filters);
    }

    private async Task<DeliveryProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", Containers + ",mp4",
             "-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "json", "-i", path],
            timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(result.Output);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray()
            .Select(stream => (Type: stream.GetProperty("codec_type").GetString(),
                Codec: stream.GetProperty("codec_name").GetString())).ToArray();
        var duration = document.RootElement.GetProperty("format").TryGetProperty("duration", out var value) &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : -1;
        return new(duration, streams.Any(stream => stream.Type == "audio"),
            streams.FirstOrDefault(stream => stream.Type == "video").Codec,
            streams.FirstOrDefault(stream => stream.Type == "audio").Codec);
    }

    // H.264 in MP4 needs even dimensions; rounding down never invents picture that was not requested.
    private static int Even(int value) => value - value % 2;
    private static string Text(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);
    private static double Seconds(long ticks, TimeBase timeBase) => (double)ticks * timeBase.Numerator / timeBase.Denominator;

    private async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffmpeg, ["-version"], cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(result.Output).Split('\n')[0].Trim();
    }
}

public sealed class DeliveryRejectedException(DeliveryPlan plan) : Exception("Delivery preflight rejected the request.")
{
    public DeliveryPlan Plan { get; } = plan;
}
