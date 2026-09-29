using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

/// One thing this project can be exported as. `Copy` separates the two kinds, because they make different
/// promises: a copy keeps the source's own packets and is fast, but its cuts land on the source's keyframes,
/// where a re-encode lands the cut exactly and rewrites every frame. `AudioCodec` reads "source" for a copy,
/// since a copy carries whatever the source holds.
public sealed record ExportFormatOption(string Name, string Label, string Extension,
    string VideoCodec, string AudioCodec, bool Copy);

/// What a caller may offer for one project, in the order it should be offered: copies first, because keeping
/// the source's own packets is the cheapest answer and the right default for a cut-down.
public sealed record ExportFormatList(int SchemaVersion, long Revision, ExportFormatOption[] Options);

/// Which exporter a format name means. `Copy` names the mux path, whose cuts land on the source's keyframes;
/// otherwise `Target` names the delivery format, whose cuts land exactly.
public sealed record ExportFormatChoice(bool Copy, string Container, DeliveryTarget? Target)
{
    /// The file a bundle of this kind holds, so a caller need not rebuild the name from the format.
    public string FileName => Copy ? "video." + Container : Target!.FileName;
}

/// Resolving the names `ListExportFormatsAsync` publishes. Kept beside that list so the two cannot disagree:
/// an interface offering a name it read from there can always act on it.
public static class ExportFormats
{
    public const string DefaultCopyName = "original-mkv";

    public static ExportFormatChoice Resolve(string? name)
    {
        var chosen = string.IsNullOrWhiteSpace(name) ? DefaultCopyName : name.Trim();
        if (!chosen.StartsWith("original-", StringComparison.Ordinal))
            return new(false, DeliveryTarget.Parse(chosen).Container, DeliveryTarget.Parse(chosen));
        var container = chosen["original-".Length..];
        // Matroska takes any codec; MP4 is offered only where the source's codecs belong in one, which the
        // published list decides. Anything else was never offered and is refused by name.
        if (container is not ("mkv" or "mp4"))
            throw new ArgumentOutOfRangeException(nameof(name),
                $"Unknown export format '{name}'. Copies are original-mkv and original-mp4.");
        return new(true, container, null);
    }
}
