using System.Globalization;

namespace RoughCut.Media;

/// Reading FFmpeg's own progress so a caller can show how far an export has got. FFmpeg is asked to write
/// machine-readable key/value lines to standard output — which no exporter here uses, because the media goes
/// to a file — rather than the status line it draws for a terminal.
///
/// The fraction is elapsed output time against the length the plan promises, so it measures the work that
/// remains rather than guessing from bytes: a quiet section and a busy one take the same place in the
/// timeline but very different amounts of space.
internal static class EncodeProgress
{
    public static string[] Arguments { get; } = ["-progress", "pipe:1", "-nostats"];

    /// FFmpeg writes `out_time_us=…` per update and `progress=end` when it finishes. Anything else on the
    /// line is ignored, and a line that cannot be read moves nothing: progress is a comfort, never a check.
    public static void Report(string line, double plannedSeconds, IProgress<double> progress)
    {
        if (line.StartsWith("progress=end", StringComparison.Ordinal)) { progress.Report(1); return; }
        if (plannedSeconds <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
        if (!long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var microseconds) || microseconds < 0) return;
        progress.Report(Math.Clamp(microseconds / 1_000_000d / plannedSeconds, 0, 1));
    }
}
