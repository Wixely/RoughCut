using RoughCut.Core;

namespace RoughCut.Desktop;

/// What the player should do next while approximating an edited timeline over an unedited source copy.
public sealed record PlaybackStep(int ClipIndex, double? SeekSeconds, bool Ended);

/// Cuts and reordering are approximated by jumping the source position at clip boundaries, so no video is
/// rendered while editing. The picture is the real source; only which parts play, and in what order, is ours.
public static class TimelinePlayback
{
    /// Boundaries are compared with a half-frame tolerance so a decoder landing fractionally short of an
    /// out point does not replay a few frames that the timeline removed.
    public const double BoundarySeconds = 0.020;

    public static bool CanApproximate(EditProject project) =>
        project.Timeline.Length > 0 &&
        project.Timeline.Select(clip => clip.AssetId).Distinct(StringComparer.Ordinal).Count() == 1 &&
        project.Assets.Single(asset => asset.Id == project.Timeline[0].AssetId).Kind == "video";

    public static double Seconds(long ticks, TimeBase timeBase) =>
        (double)ticks * timeBase.Numerator / timeBase.Denominator;

    /// Where an output time sits: which clip is playing and the source second to seek the copy to.
    public static PlaybackStep Locate(TimelineMapping[] timeline, TimeBase timeBase, double outputSeconds)
    {
        if (timeline.Length == 0) return new(0, null, true);
        for (var index = 0; index < timeline.Length; index++)
        {
            var end = Seconds(timeline[index].OutputOut, timeBase);
            if (outputSeconds < end || index == timeline.Length - 1)
            {
                var offset = Math.Max(0, outputSeconds - Seconds(timeline[index].OutputIn, timeBase));
                var source = Seconds(timeline[index].SourceIn, timeBase) + offset;
                return new(index, Math.Min(source, Seconds(timeline[index].SourceOut, timeBase)), false);
            }
        }
        return new(timeline.Length - 1, null, true);
    }

    /// Called as the copy plays: keeps the current clip, jumps to the next one, or reports the end.
    public static PlaybackStep Advance(TimelineMapping[] timeline, TimeBase timeBase, int clipIndex, double sourceSeconds)
    {
        if (timeline.Length == 0) return new(0, null, true);
        clipIndex = Math.Clamp(clipIndex, 0, timeline.Length - 1);
        var clip = timeline[clipIndex];
        var start = Seconds(clip.SourceIn, timeBase);
        var end = Seconds(clip.SourceOut, timeBase);

        // Still inside the clip, allowing for a decoder that has not quite reached the in point yet.
        if (sourceSeconds >= start - BoundarySeconds && sourceSeconds < end - BoundarySeconds)
            return new(clipIndex, null, false);

        if (clipIndex + 1 >= timeline.Length) return new(clipIndex, null, true);
        var next = timeline[clipIndex + 1];
        var nextStart = Seconds(next.SourceIn, timeBase);
        // Contiguous clips need no seek; the copy is already playing the right source material.
        var contiguous = sourceSeconds >= end - BoundarySeconds && Math.Abs(nextStart - end) <= BoundarySeconds;
        return new(clipIndex + 1, contiguous ? null : nextStart, false);
    }

    /// The output second a source position corresponds to, for reporting progress against the timeline.
    public static double OutputSeconds(TimelineMapping[] timeline, TimeBase timeBase, int clipIndex, double sourceSeconds)
    {
        if (timeline.Length == 0) return 0;
        clipIndex = Math.Clamp(clipIndex, 0, timeline.Length - 1);
        var clip = timeline[clipIndex];
        var offset = Math.Clamp(sourceSeconds - Seconds(clip.SourceIn, timeBase), 0,
            Seconds(clip.SourceOut - clip.SourceIn, timeBase));
        return Seconds(clip.OutputIn, timeBase) + offset;
    }
}
