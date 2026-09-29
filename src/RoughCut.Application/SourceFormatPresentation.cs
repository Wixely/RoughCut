namespace RoughCut.Application;

/// Turning what a source offers into what a person can choose between. A source lists the same picture size
/// several times over in different codecs — 44 renditions for an ordinary video — which is a distinction
/// nobody picking a quality wants to make, and no interface can show at once.
///
/// This lives here rather than in the review window because every interface needs it: a second window, a web
/// page, or an agent summarising the choice before it asks. The window keeps only the drawing.
public static class SourceFormatPresentation
{
    /// How many sizes an interface should offer. Eight covers 144p through 4K, which is every size an
    /// ordinary source carries.
    public const int DefaultLimit = 8;

    /// One rendition per picture size, largest first. H.264 is preferred where a size offers it, because it
    /// plays everywhere and can be copied into an MP4 rather than needing a re-encode; then AV1, then
    /// anything else, with a declared size breaking the remaining tie. Audio-only renditions are left out:
    /// choosing one would produce an edit with no picture.
    public static IReadOnlyList<SourceFormat> BestOfEachSize(SourceFormatList offered, int limit = DefaultLimit) =>
        offered.Formats
            .Where(format => format.Kind is "video" or "muxed" && format.Height > 0)
            .GroupBy(format => format.Height)
            .OrderByDescending(group => group.Key)
            .Select(group => group
                .OrderBy(format => Rank(format.VideoCodec))
                .ThenByDescending(format => format.Bytes > 0)
                .ThenBy(format => format.Bytes)
                .First())
            .Take(limit)
            .ToArray();

    /// How many distinct picture sizes the source carries, so an interface can say what it left out.
    public static int SizeCount(SourceFormatList offered) =>
        offered.Formats.Where(format => format.Kind is "video" or "muxed" && format.Height > 0)
            .Select(format => format.Height).Distinct().Count();

    /// The name a codec is known by, from the profile string a source reports it as — `avc1.4d4020` is
    /// H.264 to everyone but the container. An unrecognised codec keeps its own name rather than being
    /// guessed at.
    public static string CodecName(string codec) => codec switch
    {
        _ when codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) => "H.264",
        _ when codec.StartsWith("av0", StringComparison.OrdinalIgnoreCase) => "AV1",
        _ when codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) ||
               codec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) => "VP9",
        _ when codec.StartsWith("vp8", StringComparison.OrdinalIgnoreCase) => "VP8",
        _ when codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
               codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) => "HEVC",
        _ when codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) => "AAC",
        _ => codec
    };

    private static int Rank(string codec) =>
        codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ? 0 :
        codec.StartsWith("av0", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
}
