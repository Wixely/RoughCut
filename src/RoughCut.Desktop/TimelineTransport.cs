using RoughCut.Core;

namespace RoughCut.Desktop;

/// The transport shown under the player, in timeline time. The player itself is either the whole source
/// copy, where position has to be mapped back through the clips, or an exact render, where it is already
/// the timeline. Both are expressed here so the numbers under the picture always describe the edit.
public static class TimelineTransport
{
    public static double Duration(TimelineMapping[] timeline, TimeBase timeBase) =>
        timeline.Length == 0 ? 0 : TimelinePlayback.Seconds(timeline[^1].OutputOut, timeBase);

    /// Where the position sits in the timeline, as a fraction for the scrub bar.
    public static double Fraction(double positionSeconds, double durationSeconds) =>
        durationSeconds <= 0 ? 0 : Math.Clamp(positionSeconds / durationSeconds, 0, 1);

    /// Where a press or drag along the track lands, clamped to the timeline it is scrubbing.
    public static double Seek(double pointerX, double trackX, double trackWidth, double durationSeconds) =>
        trackWidth <= 0 ? 0 : Math.Clamp((pointerX - trackX) / trackWidth, 0, 1) * durationSeconds;

    /// m:ss.mmm below an hour, h:mm:ss.mmm above it, so a cut of a few seconds stays readable.
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss\.fff") : span.ToString(@"m\:ss\.fff");
    }
}
