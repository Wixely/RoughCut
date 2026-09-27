using System.Globalization;
using System.Text.Json;
using RoughCut.Core;

namespace RoughCut.Media;

/// One place a stream copy may begin or end. Video copies can only start at a keyframe, so these are the
/// keyframe packets near a requested time; `OffsetTicks` is how far each sits from what was asked for,
/// negative behind and positive ahead. `AudioAligned` says whether an audio packet starts within half a
/// packet of it, which is what decides whether the sound needs re-encoding to meet the picture. It is null
/// where the audio was not sampled — a reader asked about one window can be handed video packets from
/// before it — because "not known here" is not the same as "not aligned".
public sealed record CutAnchor(long Ticks, double Seconds, long OffsetTicks, bool? AudioAligned);

/// What a caller needs to choose between copying and cutting exactly at one point in one source.
public sealed record CutPoints(int SchemaVersion, string AssetId, long RequestedTicks, long WindowTicks,
    CutAnchor? Before, CutAnchor? After, CutAnchor[] Anchors, double KeyframeIntervalSeconds,
    int KeyframesSeen, double AudioPacketSeconds, string Guidance);

/// Reads copy anchors around a time without indexing the whole file. A full index costs a decode pass over
/// the entire source — minutes on a long one — which makes "look at the cut I am about to make" unusable.
/// Packets carry the keyframe flag without decoding anything, and reading a bounded interval keeps the cost
/// proportional to the window rather than the file.
public sealed class CutPointReader(string ffprobe = "ffprobe")
{
    /// Used when the source's own keyframe spacing is not yet known.
    public const double DefaultWindowSeconds = 10;
    private const double MaxWindowSeconds = 120;
    private const string Containers = "mov,matroska,webm,avi,mpegts,ogg,flv,mp4";

    public async Task<CutPoints> ReadAsync(EditProject project, string projectPath, string assetId,
        long requestedTicks, long? windowTicks = null, CancellationToken cancellationToken = default)
    {
        var asset = project.Assets.SingleOrDefault(item => item.Id == assetId && item.Kind == "video")
            ?? throw new KeyNotFoundException("Video asset was not found in the project.");
        if (requestedTicks < 0 || requestedTicks > asset.Duration)
            throw new ArgumentOutOfRangeException(nameof(requestedTicks), "The requested time is outside the source.");
        var path = ProjectFiles.Resolve(projectPath, asset.Path);
        var timeBase = project.TimeBase;
        var requested = Seconds(requestedTicks, timeBase);

        // A first bounded read establishes the source's own keyframe spacing, so the window can be chosen
        // from what this file actually does rather than from a guess. An explicit window overrides it.
        var window = windowTicks is { } explicitWindow
            ? Math.Clamp(Seconds(explicitWindow, timeBase), 0.5, MaxWindowSeconds)
            : DefaultWindowSeconds;
        var keyframes = await KeyframesAsync(path, requested, window, cancellationToken);
        var interval = Interval(keyframes);
        if (windowTicks is null && interval > 0 && interval * 4 > window)
        {
            // Sparse keyframes: widen once to the source's own spacing so both neighbours are in view.
            window = Math.Min(interval * 4, MaxWindowSeconds);
            keyframes = await KeyframesAsync(path, requested, window, cancellationToken);
            interval = Interval(keyframes);
        }
        var audio = await AudioPacketsAsync(path, requested, window, cancellationToken);
        var audioInterval = Interval(audio);

        var tolerance = Math.Max(audioInterval / 2, 0.005);
        var anchors = keyframes.Select(seconds => new CutAnchor(Ticks(seconds, timeBase), seconds,
            Ticks(seconds, timeBase) - requestedTicks,
            // Only judge alignment where audio was actually read; outside that span it is unknown.
            audio.Count == 0 || seconds < audio[0] - tolerance || seconds > audio[^1] + tolerance
                ? null
                : audio.Min(packet => Math.Abs(packet - seconds)) <= tolerance))
            .OrderBy(anchor => anchor.Ticks).ToArray();
        var before = anchors.LastOrDefault(anchor => anchor.OffsetTicks <= 0);
        var after = anchors.FirstOrDefault(anchor => anchor.OffsetTicks >= 0);
        var guidance = anchors.Length == 0
            ? $"No keyframe was found within {window:0.#} s of the requested time; widen the window or cut exactly, which re-encodes."
            : "A stream copy must begin at one of these anchors. Cutting exactly at the requested time means re-encoding the material from the anchor before it up to that time; everything after can still be copied.";
        return new(1, assetId, requestedTicks, Ticks(window, timeBase), before, after, anchors,
            interval, anchors.Length, audioInterval, guidance);
    }

    private async Task<List<double>> KeyframesAsync(string path, double centre, double window, CancellationToken cancellationToken)
    {
        var start = Math.Max(0, centre - window / 2);
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", Containers,
             "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,flags",
             "-read_intervals", Interval(start, window), "-of", "json", "-i", path],
            timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        return Packets(result.Output, keyframesOnly: true);
    }

    private async Task<List<double>> AudioPacketsAsync(string path, double centre, double window, CancellationToken cancellationToken)
    {
        var start = Math.Max(0, centre - window / 2);
        var result = await ToolProcess.RunAsync(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", Containers,
             "-select_streams", "a:0", "-show_packets", "-show_entries", "packet=pts_time,flags",
             "-read_intervals", Interval(start, window), "-of", "json", "-i", path],
            timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        return Packets(result.Output, keyframesOnly: false);
    }

    private static string Interval(double start, double window) => string.Format(CultureInfo.InvariantCulture,
        "{0:0.###}%+{1:0.###}", start, window);

    private static List<double> Packets(byte[] output, bool keyframesOnly)
    {
        var times = new List<double>();
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("packets", out var packets)) return times;
        foreach (var packet in packets.EnumerateArray())
        {
            if (keyframesOnly && (!packet.TryGetProperty("flags", out var flags) ||
                flags.GetString()?.Contains('K') != true)) continue;
            if (packet.TryGetProperty("pts_time", out var pts) && pts.GetString() is { } text &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                times.Add(seconds);
        }
        times.Sort();
        return times;
    }

    /// The median gap, which describes a source with one odd spacing better than the mean.
    private static double Interval(List<double> times)
    {
        if (times.Count < 2) return 0;
        var gaps = times.Zip(times.Skip(1), (first, second) => second - first).Where(gap => gap > 0).OrderBy(gap => gap).ToArray();
        return gaps.Length == 0 ? 0 : gaps[gaps.Length / 2];
    }

    private static double Seconds(long ticks, TimeBase timeBase) =>
        (double)ticks * timeBase.Numerator / timeBase.Denominator;

    private static long Ticks(double seconds, TimeBase timeBase) =>
        (long)Math.Round(seconds * timeBase.Denominator / timeBase.Numerator, MidpointRounding.AwayFromZero);
}
