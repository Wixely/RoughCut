using System.Globalization;

namespace RoughCut.Media;

/// One thing delivery can produce: a container, the codecs written into it, and the codec names FFprobe
/// must report back before the file is published. A target therefore cannot claim a format it did not
/// produce, and nothing outside this list is offered — every option here is one the suite exercises.
///
/// Copying the source's own packets is not a target. That is the mux path, which makes a different claim
/// and whose cuts land on the source's keyframes; see <see cref="MuxExporter"/>.
public sealed record DeliveryTarget(string Name, string Label, string Container, string Extension,
    string VideoCodec, string AudioCodec)
{
    public static readonly DeliveryTarget Mp4 = new("mp4", "MP4 · H.264 + AAC", "mp4", ".mp4", "h264", "aac");
    public static readonly DeliveryTarget Mkv = new("mkv", "MKV · H.264 + AAC", "matroska", ".mkv", "h264", "aac");
    public static readonly DeliveryTarget Mov = new("mov", "MOV · H.264 + AAC", "mov", ".mov", "h264", "aac");
    public static readonly DeliveryTarget WebM = new("webm", "WebM · VP9 + Opus", "webm", ".webm", "vp9", "opus");

    /// MP4 stays first, and stays the default for a caller that names nothing, because every earlier
    /// delivery produced exactly that and a report written then must still describe what it holds.
    public static IReadOnlyList<DeliveryTarget> All { get; } = [Mp4, Mkv, Mov, WebM];

    public static DeliveryTarget Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Mp4;
        var trimmed = name.Trim().TrimStart('.');
        return All.FirstOrDefault(target => string.Equals(target.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(nameof(name),
                $"Unknown delivery format '{name}'. Available: {string.Join(", ", All.Select(target => target.Name))}.");
    }

    public string FileName => "video" + Extension;

    /// The encoder settings for this target, ending at the muxer. `-movflags` is passed only where the
    /// container understands it: FFmpeg reports an unused option on the others, and an unexplained
    /// diagnostic fails the export rather than being ignored.
    internal string[] EncodeArguments(long maximumBytes, string output) =>
    [
        .. VideoCodec == "vp9"
            // Quality-targeted VP9 rather than the realtime settings the preview proxies use: a delivered
            // file is watched and kept, where a proxy is rebuilt whenever the edit moves.
            ? new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "2",
                      "-row-mt", "1", "-pix_fmt", "yuv420p" }
            : ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"],
        .. AudioCodec == "opus"
            ? new[] { "-c:a", "libopus", "-b:a", "128k", "-ar", "48000", "-ac", "2" }
            : ["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"],
        .. Container is "mp4" or "mov" ? new[] { "-movflags", "+faststart" } : [],
        "-map_metadata", "-1", "-map_chapters", "-1",
        "-fs", maximumBytes.ToString(CultureInfo.InvariantCulture), "-f", Container, output,
    ];
}
