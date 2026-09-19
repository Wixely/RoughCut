using System.Numerics;
using System.Text;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public static class CaptionSelection
{
    public static async Task<(CaptionCandidateAssessment Assessment, CaptionCue[]? Cues, byte[]? Bytes)> AssessAsync(
        string projectPath, TimeBase projectTimeBase, MediaAsset asset, CaptionCandidate candidate, string preferredLanguage,
        WorkspaceBoundary workspace, CancellationToken cancellationToken)
    {
        ValidateCandidate(candidate);
        try
        {
            var source = workspace.Resolve(candidate.Path);
            var relative = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, source).Replace('\\', '/');
            if (!ProjectValidator.IsPortablePath(relative)) throw new ArgumentException("Caption candidate must be inside the project directory.");
            var bytes = await ProjectFiles.ReadBoundedAsync(source, Captions.MaxBytes, cancellationToken);
            var cues = Captions.ParseSrt(new UTF8Encoding(false, true).GetString(bytes));
            if (cues.Any(cue => new MediaTime(cue.End, new(1, 1000)).CompareTo(new(asset.Duration, projectTimeBase)) > 0))
                throw new InvalidDataException("Caption cue exceeds the source duration.");
            var covered = CoveredMilliseconds(cues);
            var durationMs = DurationMilliseconds(asset.Duration, projectTimeBase);
            var coverage = durationMs <= 0 ? 0 : checked((int)Math.Min(10_000, covered * 10_000 / durationMs));
            var reasons = new List<string> { $"{coverage / 100m:0.00}% source-time coverage", $"{cues.Length} valid cues" };
            var score = SourcePriority(candidate.SourceKind);
            if (string.Equals(candidate.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
                reasons.Add("exact preferred-language match");
            }
            else if (PrimaryLanguage(candidate.Language) == PrimaryLanguage(preferredLanguage))
            {
                score += 25;
                reasons.Add("primary-language match");
            }
            score += coverage / 100;
            return (new(candidate.Id, relative, candidate.SourceKind, candidate.Language, true, cues.Length,
                coverage, score, reasons.ToArray()), cues, bytes);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            NotSupportedException or DecoderFallbackException or OverflowException)
        {
            return (new(candidate.Id, candidate.Path, candidate.SourceKind, candidate.Language, false, 0, 0, 0, [], exception.Message), null, null);
        }
    }

    private static void ValidateCandidate(CaptionCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id.Length > 128) throw new ArgumentException("Caption candidate ID is required and limited to 128 characters.");
        if (candidate.SourceKind is not ("supplied" or "manual" or "automatic" or "local-stt")) throw new ArgumentException("Unknown caption source kind.");
        if (string.IsNullOrWhiteSpace(candidate.Language) || candidate.Language.Length > 35 ||
            candidate.Language.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Caption language must be a short language tag.");
    }

    private static long CoveredMilliseconds(CaptionCue[] cues)
    {
        long total = 0;
        long start = -1;
        long end = -1;
        foreach (var cue in cues.OrderBy(cue => cue.Start).ThenBy(cue => cue.End))
        {
            if (start < 0) { start = cue.Start; end = cue.End; continue; }
            if (cue.Start <= end) end = Math.Max(end, cue.End);
            else { total = checked(total + end - start); start = cue.Start; end = cue.End; }
        }
        return start < 0 ? 0 : checked(total + end - start);
    }

    private static long DurationMilliseconds(long ticks, TimeBase timeBase)
    {
        var numerator = (BigInteger)ticks * timeBase.Numerator * 1000;
        var value = BigInteger.DivRem(numerator, timeBase.Denominator, out var remainder);
        if (remainder > 0) value++;
        return checked((long)value);
    }

    private static int SourcePriority(string kind) => kind switch
    {
        "manual" => 400,
        "supplied" => 350,
        "automatic" => 250,
        "local-stt" => 200,
        _ => throw new ArgumentException("Unknown caption source kind.")
    };

    private static string PrimaryLanguage(string language) => language.Split(['-', '_'], 2)[0].ToLowerInvariant();
}
