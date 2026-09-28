using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

/// One retained interval as it will actually be copied. `CopyIn`/`CopyOut` are the anchors the cut lands on;
/// the offsets say how far each moved from what was asked for, negative earlier and positive later.
public sealed record MuxSegment(string ClipId, string AssetId, long RequestedIn, long RequestedOut,
    long CopyIn, long CopyOut, long InOffsetTicks, long OutOffsetTicks);

public sealed record MuxPlan(int SchemaVersion, string ProjectId, long Revision, string ProjectSha256,
    bool Supported, ValidationIssue[] Issues, MuxSegment[] Segments, string Container,
    string VideoCodec, string AudioCodec, long RequestedDuration, long CopiedDuration,
    long WorstOffsetTicks, string Policy = "stream-copy-v1");

/// `JoinAdjustments` counts the packets whose timestamps the muxer moved where two copied segments meet.
/// Audio packets are whole and do not align with a picture cut, so the sound of a following segment can
/// begin fractionally before the previous one ends; the muxer pushes those packets forward. The packets
/// themselves are untouched — only where they sit — and the count is reported rather than hidden.
public sealed record MuxReport(int ReportVersion, MuxPlan Plan, string SourceSha256, string OutputSha256,
    string FfmpegVersion, double ExpectedSeconds, double ActualSeconds, long OutputBytes,
    int JoinAdjustments, OutputCaption[] Captions,
    string Claim = "Every retained packet is the source's own, unaltered, though a few may be moved in time where two segments meet. Each segment starts at the copy anchor the plan names rather than the requested time, because a copy can only begin at a keyframe; it ends where asked. Audio packets are copied whole, so a fraction of a second of sound can precede a segment's first picture, and on a source with B-frames the last frames of a segment may reference frames beyond the cut.");

/// Copies retained material into a new container without re-encoding it. This is neither the strict
/// exporter nor delivery: it makes no claim about a validated matrix and it decodes nothing, so it is fast
/// and bit-exact for the packets it keeps — but a copy can only begin at a keyframe, so the cuts land where
/// the source allows rather than where they were asked for. The plan says exactly how far each moved, and
/// a caller that needs a cut on the requested frame must re-encode instead.
public sealed class MuxExporter(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    public const int MaxSegments = 200;
    public const long MaxOutputBytes = 8L * 1024 * 1024 * 1024;
    private static readonly string[] Containers = ["mkv", "mp4"];

    /// Resolves every requested boundary onto a copy anchor. Reading anchors needs the source, so unlike
    /// the delivery preflight this one touches the media — but only a bounded window per boundary.
    public async Task<MuxPlan> PlanAsync(EditProject project, string projectPath, string container = "mkv",
        CancellationToken cancellationToken = default)
    {
        ProjectValidator.EnsureValid(project);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Default.EditProject)));
        MuxPlan Rejected(string code, string location, string message) =>
            new(1, project.ProjectId, project.Revision, hash, false, [new(code, location, message)], [],
                container, "", "", 0, 0, 0);

        if (!Containers.Contains(container))
            return Rejected("unsupported-container", "container", $"Copying supports {string.Join(" and ", Containers)}.");
        if (project.Timeline.Length is 0 or > MaxSegments)
            return Rejected("unsupported-timeline", "timeline", $"Copying supports 1 to {MaxSegments} clips.");
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        if (project.Timeline.Any(clip => assets[clip.AssetId].Kind != "video"))
            return Rejected("unsupported-timeline", "timeline", "Copying carries video clips only; timed images must be rendered.");
        // Copied segments share one video stream, so they must share its parameter sets: one source only.
        var sources = project.Timeline.Select(clip => clip.AssetId).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length != 1)
            return Rejected("unsupported-timeline", "timeline",
                "Copying carries one source per output, because copied segments must share its codec parameters.");
        if (project.Timeline.Any(clip => clip.Crop is not null))
            return Rejected("unsupported-crop", "timeline", "A crop changes the picture, which copying cannot do; render instead.");
        if (project.Replacements.Any(replacement => replacement.State == "applied"))
            return Rejected("unsupported-replacements", "replacements", "Applied voice replacements change the sound, which copying cannot do.");

        var asset = assets[sources[0]];
        var path = ProjectFiles.Resolve(projectPath, asset.Path);
        if (!File.Exists(path)) return Rejected("missing-media", asset.Id, "Source media is missing from the project directory.");
        var probe = await ProbeAsync(path, cancellationToken);
        if (probe.VideoCodec is null) return Rejected("missing-video", asset.Id, "The source carries no video stream.");
        if (container == "mp4" && probe.VideoCodec is not ("h264" or "hevc" or "av1" or "mpeg4") )
            return Rejected("unsupported-container", "container",
                $"MP4 does not carry {probe.VideoCodec} from a copy; use mkv.");

        var reader = new CutPointReader(ffprobe);
        var segments = new List<MuxSegment>();
        foreach (var clip in project.Timeline)
        {
            // Only the start needs an anchor: a copy must begin at a keyframe, but it can end at any
            // packet. Snapping the end too would throw away up to a whole keyframe interval of material.
            var start = await NearestAsync(reader, project, projectPath, asset.Id, clip.In, cancellationToken);
            var end = clip.Out;
            if (start is null)
                return Rejected("no-copy-anchor", clip.Id,
                    $"No copy anchor was found near clip {clip.Id}; widen the search or re-encode.");
            if (end <= start.Value)
                return Rejected("collapsed-clip", clip.Id,
                    $"Clip {clip.Id} collapses to nothing once its start moves to copy anchor {start}.");
            segments.Add(new(clip.Id, clip.AssetId, clip.In, clip.Out, start.Value, end,
                start.Value - clip.In, 0));
        }
        var requested = project.Timeline.Sum(clip => clip.Out - clip.In);
        var copied = segments.Sum(segment => segment.CopyOut - segment.CopyIn);
        var worst = segments.Max(segment => Math.Max(Math.Abs(segment.InOffsetTicks), Math.Abs(segment.OutOffsetTicks)));
        return new(1, project.ProjectId, project.Revision, hash, true, [], segments.ToArray(), container,
            probe.VideoCodec, probe.AudioCodec ?? "none", requested, copied, worst);
    }

    private static async Task<long?> NearestAsync(CutPointReader reader, EditProject project, string projectPath,
        string assetId, long ticks, CancellationToken cancellationToken)
    {
        var points = await reader.ReadAsync(project, projectPath, assetId, ticks, cancellationToken: cancellationToken);
        return (points.Before, points.After) switch
        {
            (null, null) => null,
            ({ } before, null) => before.Ticks,
            (null, { } after) => after.Ticks,
            ({ } before, { } after) => Math.Abs(before.OffsetTicks) <= Math.Abs(after.OffsetTicks) ? before.Ticks : after.Ticks
        };
    }

    public async Task<MuxReport> ExportAsync(string projectPath, string outputDirectory, string container = "mkv",
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        projectPath = Path.GetFullPath(projectPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (File.Exists(outputDirectory) || Directory.Exists(outputDirectory))
            throw new IOException("Output directory already exists; choose a new destination.");
        var project = await new ProjectStore().LoadAsync(projectPath, cancellationToken);
        var plan = await PlanAsync(project, projectPath, container, cancellationToken);
        if (!plan.Supported) throw new MuxRejectedException(plan);
        var asset = project.Assets.Single(item => item.Id == plan.Segments[0].AssetId);
        var source = ProjectFiles.Resolve(projectPath, asset.Path);
        if (!string.Equals(await MediaReader.FingerprintAsync(source, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source fingerprint differs from the project; relink or reinspect before copying.");

        var parent = Path.GetDirectoryName(outputDirectory)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".roughcut-mux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // The concat list sits beside the source so it can name it by the project's own portable relative
        // path, which keeps absolute and caller-supplied paths out of the concat syntax entirely.
        var listPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
            ".roughcut-mux-" + Guid.NewGuid().ToString("N") + ".ffconcat");
        try
        {
            // Cutting and joining in one pass: the demuxer seeks to each in-point, and rebases the joined
            // timestamps itself. Cutting each segment to its own file first does not — a part whose
            // timestamps are not rebased inflates its own duration and the join inherits that.
            var list = new StringBuilder("ffconcat version 1.0\n");
            foreach (var segment in plan.Segments)
                list.Append("file ").Append(Quote(asset.Path)).Append('\n')
                    .Append("inpoint ").Append(Text(segment.CopyIn, project.TimeBase)).Append('\n')
                    .Append("outpoint ").Append(Text(segment.CopyOut, project.TimeBase)).Append('\n');
            await File.WriteAllTextAsync(listPath, list.ToString(), new UTF8Encoding(false), cancellationToken);

            var output = Path.Combine(staging, "video." + container);
            // Warnings are read rather than suppressed: a join legitimately produces non-monotonic audio
            // timestamps, and anything else the muxer says about a copy is a failure.
            var arguments = new List<string> { "-v", "warning", "-nostdin", "-n" };
            // Progress goes to standard output, which a copy does not otherwise use.
            if (progress is not null) arguments.AddRange(EncodeProgress.Arguments);
            arguments.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "concat,matroska,webm,mov,mp4",
                "-f", "concat", "-safe", "1", "-i", listPath, "-map", "0:v:0"]);
            if (plan.AudioCodec != "none") arguments.AddRange(["-map", "0:a:0"]);
            arguments.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero",
                "-map_metadata", "-1", "-map_chapters", "-1",
                "-fs", MaxOutputBytes.ToString(CultureInfo.InvariantCulture),
                "-f", container == "mp4" ? "mp4" : "matroska", output]);
            var planned = Seconds(plan.CopiedDuration, project.TimeBase);
            var run = await ToolProcess.RunAsync(ffmpeg, arguments, timeout: TimeSpan.FromMinutes(60),
                allowDiagnostics: true, cancellationToken: cancellationToken,
                onOutputLine: progress is null ? null : line => EncodeProgress.Report(line, planned, progress));
            var adjustments = 0;
            foreach (var line in run.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Contains("Non-monotonous DTS", StringComparison.Ordinal) ||
                    line.Contains("Non-monotonic DTS", StringComparison.Ordinal)) adjustments++;
                else throw new MediaToolException(0, line);
            }
            await VerifyStartsOnKeyframeAsync(output, cancellationToken);

            var delivered = await ProbeAsync(output, cancellationToken);
            if (delivered.VideoCodec != plan.VideoCodec || (plan.AudioCodec != "none" && delivered.AudioCodec != plan.AudioCodec))
                throw new InvalidDataException("The copied output does not carry the source's own codecs.");
            var expected = Seconds(plan.CopiedDuration, project.TimeBase);
            // Whole packets are kept, and a segment can carry a fraction of a second of sound before its
            // first picture, so the length is allowed to run slightly over what was asked for.
            if (delivered.Seconds < expected - 0.25 || delivered.Seconds > expected + 0.25 + 0.5 * plan.Segments.Length)
                throw new InvalidDataException(FormattableString.Invariant(
                    $"The copied output is {delivered.Seconds:0.000}s where the copied intervals total {expected:0.000}s."));
            var bytes = new FileInfo(output).Length;
            if (bytes <= 0) throw new InvalidDataException("The copied output is empty.");
            if (!string.Equals(await MediaReader.FingerprintAsync(source, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The source changed while copying; output was not published.");

            // Captions follow the cut that was made, not the one that was asked for.
            var snapped = project with
            {
                Timeline = [.. plan.Segments.Select(segment =>
                    project.Timeline.Single(clip => clip.Id == segment.ClipId) with { In = segment.CopyIn, Out = segment.CopyOut })]
            };
            var captions = Captions.Retime(snapped);
            var report = new MuxReport(1, plan, asset.Sha256,
                await MediaReader.FingerprintAsync(output, cancellationToken), await VersionAsync(cancellationToken),
                expected, delivered.Seconds, bytes, adjustments, captions);
            await File.WriteAllTextAsync(Path.Combine(staging, "mux.json"),
                JsonSerializer.Serialize(report, MediaJson.Default.MuxReport), cancellationToken);
            if (captions.Length > 0)
                await File.WriteAllTextAsync(Path.Combine(staging, "captions.srt"), Captions.WriteSrt(captions),
                    new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, outputDirectory);
            return report;
        }
        finally
        {
            if (File.Exists(listPath)) File.Delete(listPath);
            if (Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
            }
        }
    }

    /// ffconcat takes a quoted filename; a quote inside one is escaped rather than ending it.
    internal static string Quote(string relativePath) => "'" + relativePath.Replace("'", "'\\''") + "'";

    /// A copy that begins anywhere but a keyframe writes a file whose picture only starts later, with
    /// nothing before it and no error from the muxer. Measured, so it is checked. A fraction of a second
    /// of sound may precede the first picture, because audio packets are copied whole.
    private async Task VerifyStartsOnKeyframeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm",
             "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,flags",
             "-read_intervals", "%+1", "-of", "json", "-i", path],
            timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(result.Output);
        var first = document.RootElement.GetProperty("packets").EnumerateArray().FirstOrDefault();
        var startsClean = first.ValueKind == JsonValueKind.Object &&
            first.TryGetProperty("flags", out var flags) && flags.GetString()?.Contains('K') == true &&
            first.TryGetProperty("pts_time", out var pts) && pts.GetString() is { } text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds <= 0.5;
        if (!startsClean)
            throw new InvalidDataException("The copied output does not begin on a keyframe: its picture starts late or not on one.");
    }

    private async Task<(double Seconds, string? VideoCodec, string? AudioCodec)> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file",
             "-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "json", "-i", path],
            timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(result.Output);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray()
            .Select(stream => (Type: stream.GetProperty("codec_type").GetString(),
                Codec: stream.GetProperty("codec_name").GetString())).ToArray();
        var duration = document.RootElement.GetProperty("format").TryGetProperty("duration", out var value) &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : -1;
        return (duration, streams.FirstOrDefault(stream => stream.Type == "video").Codec,
            streams.FirstOrDefault(stream => stream.Type == "audio").Codec);
    }

    private static string Text(long ticks, TimeBase timeBase) =>
        Seconds(ticks, timeBase).ToString("0.######", CultureInfo.InvariantCulture);

    private static double Seconds(long ticks, TimeBase timeBase) =>
        (double)ticks * timeBase.Numerator / timeBase.Denominator;

    private async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffmpeg, ["-version"], cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(result.Output).Split('\n')[0].Trim();
    }
}

public sealed class MuxRejectedException(MuxPlan plan) : Exception("Copy preflight rejected the request.")
{
    public MuxPlan Plan { get; } = plan;
}
