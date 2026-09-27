namespace RoughCut.Desktop;

public enum ClipEdge { Start, End }

/// Dragging a clip's edge in the timeline moves that boundary only. The clip keeps the other end, so the
/// drag is a trim of this clip rather than a ripple into its neighbours, and the interval it produces is
/// always inside the source. The pixel-to-tick conversion belongs to the caller, which knows the layout.
public static class ClipDragGeometry
{
    public static (long In, long Out) Update(long startIn, long startOut, ClipEdge edge,
        double deltaTicks, long sourceDuration, long minimumTicks)
    {
        if (minimumTicks < 1) throw new ArgumentOutOfRangeException(nameof(minimumTicks));
        if (startIn < 0 || startIn >= startOut || startOut > sourceDuration)
            throw new ArgumentOutOfRangeException(nameof(startIn), "The clip must start inside its source and be nonempty.");
        // A source shorter than the minimum would leave nothing to clamp to, so the clip keeps what it has.
        if (sourceDuration < minimumTicks) return (startIn, startOut);
        var delta = double.IsFinite(deltaTicks) ? Math.Round(deltaTicks, MidpointRounding.AwayFromZero) : 0;
        return edge switch
        {
            ClipEdge.Start => (Clamp(startIn + delta, 0, startOut - minimumTicks), startOut),
            ClipEdge.End => (startIn, Clamp(startOut + delta, startIn + minimumTicks, sourceDuration)),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    /// The shortest clip a drag may produce: twenty milliseconds, or one tick in a coarser time base.
    public static long MinimumTicks(RoughCut.Core.TimeBase timeBase) =>
        Math.Max(1, timeBase.Denominator / (timeBase.Numerator * 50));

    private static long Clamp(double value, long minimum, long maximum) =>
        (long)Math.Clamp(value, minimum, Math.Max(minimum, maximum));
}
