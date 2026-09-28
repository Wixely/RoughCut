using System.Globalization;
using System.Text.RegularExpressions;

namespace RoughCut.Application;

/// One downloadable rendition of a source. `Kind` is "video" for picture without sound, "audio" for sound
/// without picture, and "muxed" for a rendition carrying both.
public sealed record SourceFormat(string Id, string Extension, string Kind, int Width, int Height,
    double Fps, string VideoCodec, string AudioCodec, double BitrateKbps, long Bytes);

/// What a source offers, without downloading any of it. The caller decides which rendition it wants; the
/// policies are named here so a caller can ask for one without knowing the source's format identifiers.
public sealed record SourceFormatList(int SchemaVersion, string Source, string Title, double DurationSeconds,
    long MaxBytes, SourceFormat[] Formats, string[] Policies);

/// The rendition an acquisition will actually fetch, and why. Recorded with the download so a project says
/// what it holds rather than only where it came from.
public sealed record FormatChoice(string Request, string Expression, string SelectedId, string? AudioId,
    long EstimatedBytes, double BitrateKbps, string Reason);

/// Turning a caller's request — an explicit format identifier, or one of the named policies — into the one
/// expression yt-dlp is given. Deliberately decided here rather than by yt-dlp's own sort: the choice, its
/// size and its reason are recorded, and a rendition that cannot fit the download bound is refused before
/// anything is fetched rather than aborting part-way and leaving a fragment behind.
public static partial class SourceFormatPolicy
{
    public static readonly string[] Policies = ["highest", "medium", "lowest"];

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,32}(\+[A-Za-z0-9_.\-]{1,32})?$")]
    private static partial Regex FormatIdPattern();

    public static FormatChoice Choose(SourceFormatList list, string? request, long maxBytes)
    {
        request = string.IsNullOrWhiteSpace(request) ? "medium" : request.Trim();
        return Policies.Contains(request, StringComparer.OrdinalIgnoreCase)
            ? ByPolicy(list, request.ToLowerInvariant(), maxBytes)
            : ByIdentifier(list, request, maxBytes);
    }

    private static FormatChoice ByPolicy(SourceFormatList list, string policy, long maxBytes)
    {
        var audio = list.Formats.Where(format => format.Kind == "audio")
            .OrderBy(format => Weight(format)).FirstOrDefault();
        var candidates = list.Formats
            .Where(format => format.Kind is "video" or "muxed")
            .Select(format => (Format: format, Audio: format.Kind == "video" ? audio : null))
            .Where(pair => pair.Format.Kind == "muxed" || pair.Audio is not null)
            .Select(pair => (pair.Format, pair.Audio,
                Bytes: pair.Format.Bytes + (pair.Audio?.Bytes ?? 0),
                Bitrate: pair.Format.BitrateKbps + (pair.Audio?.BitrateKbps ?? 0)))
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("The source offers no video rendition that can be downloaded.");

        var affordable = candidates.Where(pair => pair.Bytes > 0 && pair.Bytes <= maxBytes).ToArray();
        if (affordable.Length == 0)
        {
            // Every rendition is too large, or none declares a size. Say which, with the numbers, rather
            // than starting a download that the size bound will abort half way through.
            var smallest = candidates.Where(pair => pair.Bytes > 0).OrderBy(pair => pair.Bytes).FirstOrDefault();
            throw new InvalidDataException(smallest.Format is null
                ? "No rendition of this source declares a size, so none can be chosen within the download bound."
                : string.Format(CultureInfo.InvariantCulture,
                    "The smallest video rendition is {0:0.#} MiB, over the {1:0.#} MiB download bound; ask for a specific smaller format identifier.",
                    smallest.Bytes / 1048576d, maxBytes / 1048576d));
        }

        // Ordered by bitrate, which is what the policies name; size breaks ties and stands in where a
        // rendition declares no bitrate.
        var ordered = affordable.OrderBy(pair => pair.Bitrate > 0 ? pair.Bitrate : pair.Bytes / 1024d)
            .ThenBy(pair => pair.Bytes).ToArray();
        var chosen = policy switch
        {
            "highest" => ordered[^1],
            "lowest" => ordered[0],
            _ => ordered[ordered.Length / 2]
        };
        var reason = string.Format(CultureInfo.InvariantCulture,
            "{0} of {1} renditions within the {2:0.#} MiB bound: {3}p at {4:0.#} kbit/s, about {5:0.#} MiB.",
            policy, ordered.Length, maxBytes / 1048576d, chosen.Format.Height, chosen.Bitrate, chosen.Bytes / 1048576d);
        return new(policy, Expression(chosen.Format, chosen.Audio), chosen.Format.Id, chosen.Audio?.Id,
            chosen.Bytes, chosen.Bitrate, reason);
    }

    /// The request to make for one rendition a person picked from a list: unchanged where it already
    /// carries sound or already names a pair, and joined to the source's best audio where it does not.
    /// Returns the identifier untouched when the source offers no audio at all, so the caller still gets the
    /// refusal that explains why rather than a request that silently means something else.
    public static string WithAudio(SourceFormatList list, string formatId)
    {
        if (formatId.Contains('+', StringComparison.Ordinal)) return formatId;
        var picture = list.Formats.FirstOrDefault(format => format.Id == formatId);
        if (picture is null || picture.Kind != "video") return formatId;
        var audio = list.Formats.Where(format => format.Kind == "audio").OrderBy(Weight).FirstOrDefault();
        return audio is null ? formatId : formatId + "+" + audio.Id;
    }

    private static FormatChoice ByIdentifier(SourceFormatList list, string request, long maxBytes)
    {
        if (!FormatIdPattern().IsMatch(request))
            throw new ArgumentException("A format request must be a policy name, one format identifier, or two joined by '+'.");
        var parts = request.Split('+');
        var chosen = parts.Select(part => list.Formats.FirstOrDefault(format => format.Id == part)
            ?? throw new ArgumentException($"This source has no format '{part}'; list its formats first.")).ToArray();
        if (parts.Length == 1 && chosen[0].Kind == "video")
            throw new ArgumentException($"Format '{parts[0]}' carries no audio; pair it with an audio format as '{parts[0]}+<audio>'.");
        var bytes = chosen.Sum(format => format.Bytes);
        if (bytes > maxBytes)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "Format '{0}' is about {1:0.#} MiB, over the {2:0.#} MiB download bound.",
                request, bytes / 1048576d, maxBytes / 1048576d));
        var bitrate = chosen.Sum(format => format.BitrateKbps);
        var reason = bytes > 0
            ? string.Format(CultureInfo.InvariantCulture, "requested format '{0}': about {1:0.#} MiB.", request, bytes / 1048576d)
            : $"requested format '{request}', whose size this source does not declare.";
        return new(request, request, chosen[0].Id, parts.Length > 1 ? parts[1] : null, bytes, bitrate, reason);
    }

    private static string Expression(SourceFormat video, SourceFormat? audio) =>
        audio is null ? video.Id : video.Id + "+" + audio.Id;

    private static double Weight(SourceFormat format) =>
        format.BitrateKbps > 0 ? format.BitrateKbps : format.Bytes / 1024d;
}
