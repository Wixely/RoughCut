using System.Globalization;
using System.Numerics;
using System.Text;

namespace RoughCut.Core;

public sealed record CaptionCue(string Id, long Start, long End, string Text);
public sealed record CaptionTrack(string AssetId, string SourcePath, string SourceSha256, TimeBase TimeBase, CaptionCue[] Cues,
    string SourceKind = "supplied", string Language = "und", string Selection = "explicit", string? CandidateId = null,
    int CoverageBasisPoints = 0);
public sealed record OutputCaption(string CueId, string ClipId, MediaTime Start, MediaTime End, string Text);
public sealed record CaptionCandidate(string Id, string Path, string SourceKind, string Language = "und");
public sealed record CaptionCandidateAssessment(string Id, string Path, string SourceKind, string Language,
    bool Valid, int CueCount, int CoverageBasisPoints, int Score, string[] Reasons, string? Error = null);
public sealed record CaptionSelectionResult(string SelectedId, bool ExplicitOverride,
    CaptionCandidateAssessment[] Candidates, EditProject Project);

public static class Captions
{
    public const int MaxBytes = 1024 * 1024;

    // Deliberately plain SRT: cue index, strict timestamp line, then text. No executable markup.
    public static CaptionCue[] ParseSrt(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("Captions exceed 1 MiB.");
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) throw new InvalidDataException("Caption file is empty.");
        var blocks = new List<List<string>>();
        var current = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) { blocks.Add(current); current = []; }
            }
            else current.Add(line);
        }
        if (current.Count > 0) blocks.Add(current);
        if (blocks.Count > 10_000) throw new InvalidDataException("Too many caption cues.");
        var cues = new List<CaptionCue>();
        foreach (var block in blocks)
        {
            if (block.Count < 3 || !int.TryParse(block[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                throw new InvalidDataException("SRT requires a positive cue number, a timestamp line and text.");
            var times = block[1].Split(" --> ", StringSplitOptions.None);
            if (times.Length != 2) throw new InvalidDataException("Invalid SRT timestamp line.");
            var start = ParseTimestamp(times[0]);
            var end = ParseTimestamp(times[1]);
            if (end <= start) throw new InvalidDataException("Caption intervals must be nonempty.");
            var body = string.Join('\n', block.Skip(2));
            if (body.Length > 8000) throw new InvalidDataException("Caption cue text is too long.");
            cues.Add(new($"cue-{cues.Count + 1}", start, end, body));
        }
        return cues.ToArray();
    }

    private static long ParseTimestamp(string text)
    {
        var parts = text.Split([':', ',']);
        if (parts.Length != 4 || parts[0].Length < 2 || parts[1].Length != 2 || parts[2].Length != 2 || parts[3].Length != 3 ||
            parts.Any(p => p.Any(c => c is < '0' or > '9')))
            throw new InvalidDataException("Use SRT timestamps HH:MM:SS,mmm.");
        var hours = long.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var seconds = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(parts[3], CultureInfo.InvariantCulture);
        if (minutes > 59 || seconds > 59) throw new InvalidDataException("Invalid SRT minute or second value.");
        return checked(((hours * 60 + minutes) * 60 + seconds) * 1000 + milliseconds);
    }

    public static OutputCaption[] Retime(EditProject project)
    {
        ProjectValidator.EnsureValid(project);
        if (project.Captions is not { } track) return [];
        var output = new List<OutputCaption>();
        foreach (var clip in ProjectValidator.MapTimeline(project).Where(c => c.AssetId == track.AssetId))
        {
            var clipIn = new MediaTime(clip.SourceIn, project.TimeBase);
            var clipOut = new MediaTime(clip.SourceOut, project.TimeBase);
            var offset = TimeMath.Subtract(new(clip.OutputIn, project.TimeBase), clipIn);
            foreach (var cue in track.Cues)
            {
                var start = new MediaTime(cue.Start, track.TimeBase);
                var end = new MediaTime(cue.End, track.TimeBase);
                if (start.CompareTo(clipIn) < 0) start = clipIn;
                if (end.CompareTo(clipOut) > 0) end = clipOut;
                if (start.CompareTo(end) < 0)
                    output.Add(new(cue.Id, clip.ClipId, TimeMath.Add(start, offset), TimeMath.Add(end, offset), cue.Text));
            }
        }
        return output.OrderBy(c => c.Start).ThenBy(c => c.End).ToArray();
    }

    public static string WriteSrt(OutputCaption[] cues)
    {
        var text = new StringBuilder();
        for (int i = 0; i < cues.Length; i++)
        {
            text.Append(i + 1).Append('\n').Append(Format(cues[i].Start, ceiling: false))
                .Append(" --> ").Append(Format(cues[i].End, ceiling: true)).Append('\n')
                .Append(cues[i].Text).Append("\n\n");
        }
        return text.ToString();
    }

    private static string Format(MediaTime time, bool ceiling)
    {
        var numerator = (BigInteger)time.Ticks * time.TimeBase.Numerator * 1000;
        var value = BigInteger.DivRem(numerator, time.TimeBase.Denominator, out var remainder);
        if (ceiling && remainder > 0) value++;
        var ms = checked((long)value);
        return FormattableString.Invariant($"{ms / 3_600_000:D2}:{ms / 60_000 % 60:D2}:{ms / 1000 % 60:D2},{ms % 1000:D3}");
    }
}
