using System.Numerics;

namespace RoughCut.Core;

public static class TimeMath
{
    public static long ExactTicks(MediaTime time, TimeBase target)
    {
        if (!time.TimeBase.IsValid || !target.IsValid) throw new ArgumentException("Invalid time base.");
        var numerator = (BigInteger)time.Ticks * time.TimeBase.Numerator * target.Denominator;
        var denominator = (BigInteger)time.TimeBase.Denominator * target.Numerator;
        var result = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder != 0) throw new NotSupportedException("Timestamp is not exactly representable in the required time base.");
        return checked((long)result);
    }

    /// Rounds to the nearest tick. Only for a quantity that was already an estimate — a speech boundary a
    /// model guessed, say. An edit boundary decides which frames survive and must use ExactTicks.
    public static long NearestTicks(MediaTime time, TimeBase target)
    {
        if (!time.TimeBase.IsValid || !target.IsValid) throw new ArgumentException("Invalid time base.");
        var numerator = (BigInteger)time.Ticks * time.TimeBase.Numerator * target.Denominator;
        var denominator = (BigInteger)time.TimeBase.Denominator * target.Numerator;
        var result = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= BigInteger.Abs(denominator)) result += time.Ticks < 0 ? -1 : 1;
        return checked((long)result);
    }

    /// Rounds towards zero. For a length being measured rather than a boundary being cut: a source's own
    /// duration is rarely a whole number of milliseconds, and rounding down never claims material that is
    /// not there.
    public static long FloorTicks(MediaTime time, TimeBase target)
    {
        if (!time.TimeBase.IsValid || !target.IsValid) throw new ArgumentException("Invalid time base.");
        var numerator = (BigInteger)time.Ticks * time.TimeBase.Numerator * target.Denominator;
        var denominator = (BigInteger)time.TimeBase.Denominator * target.Numerator;
        return checked((long)(numerator / denominator));
    }

    public static MediaTime Add(MediaTime left, MediaTime right)
    {
        if (!left.TimeBase.IsValid || !right.TimeBase.IsValid) throw new ArgumentException("Invalid time base.");
        var numerator = (BigInteger)left.Ticks * left.TimeBase.Numerator * right.TimeBase.Denominator +
            (BigInteger)right.Ticks * right.TimeBase.Numerator * left.TimeBase.Denominator;
        var denominator = (BigInteger)left.TimeBase.Denominator * right.TimeBase.Denominator;
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return new(checked((long)(numerator / gcd)), new(1, checked((long)(denominator / gcd))));
    }

    public static MediaTime Subtract(MediaTime left, MediaTime right) => Add(left, right with { Ticks = checked(-right.Ticks) });
}

public sealed record EditOperation(string Action, string? ClipId = null, long? In = null,
    long? Out = null, long? At = null, string? NewClipId = null, string[]? Order = null,
    Crop? Crop = null, string? Mode = null, string? AssetId = null, long? Duration = null,
    string? BeforeClipId = null, string? Fit = null, string? Audio = null);

public static class TimelineEditor
{
    // Apply to an isolated list; a failed operation never partly changes the caller's project.
    public static EditProject Apply(EditProject project, EditOperation[] operations)
    {
        ProjectValidator.EnsureValid(project);
        if (operations.Length is 0 or > 1000) throw new ArgumentException("Supply 1 to 1000 edit operations.");
        var clips = project.Timeline.ToList();
        var mode = project.ExportMode;
        foreach (var operation in operations)
        {
            if (operation is null) throw new ArgumentException("Null edit operation.");
            var index = clips.FindIndex(c => c.Id == operation.ClipId);
            if (operation.Action is not ("reorder" or "export-mode" or "insert-image" or "insert-clip") && index < 0)
                throw new ArgumentException("Edit references an unknown clip.");
            var fields = new HashSet<string>();
            if (operation.ClipId is not null) fields.Add("clipId");
            if (operation.In is not null) fields.Add("in");
            if (operation.Out is not null) fields.Add("out");
            if (operation.At is not null) fields.Add("at");
            if (operation.NewClipId is not null) fields.Add("newClipId");
            if (operation.Order is not null) fields.Add("order");
            if (operation.Crop is not null) fields.Add("crop");
            if (operation.Mode is not null) fields.Add("mode");
            if (operation.AssetId is not null) fields.Add("assetId");
            if (operation.Duration is not null) fields.Add("duration");
            if (operation.BeforeClipId is not null) fields.Add("beforeClipId");
            if (operation.Fit is not null) fields.Add("fit");
            if (operation.Audio is not null) fields.Add("audio");
            string[] allowed = operation.Action switch
            {
                "trim" => ["clipId", "in", "out"],
                "set-range" => ["clipId", "in", "out"],
                "split" => ["clipId", "at", "newClipId"],
                "remove" => ["clipId"],
                "reorder" => ["order"],
                "crop" => ["clipId", "crop"],
                "export-mode" => ["mode"],
                "insert-image" => ["clipId", "assetId", "duration", "beforeClipId", "fit"],
                "insert-clip" => ["clipId", "assetId", "in", "out", "crop", "fit", "audio", "beforeClipId"],
                _ => throw new ArgumentException("Unknown edit action.")
            };
            if (fields.Except(allowed).Any()) throw new ArgumentException("Edit contains fields unrelated to its action.");
            switch (operation.Action)
            {
                case "trim":
                    if (operation.In is not { } start || operation.Out is not { } end ||
                        start < clips[index].In || end > clips[index].Out || start >= end)
                        throw new ArgumentException("Trim must retain a nonempty interval inside the clip.");
                    clips[index] = clips[index] with { In = start, Out = end };
                    break;
                // Unlike trim this restores material inside the source, so an interactive trim is reversible.
                case "set-range":
                    var ranged = clips[index];
                    var rangedAsset = project.Assets.Single(a => a.Id == ranged.AssetId);
                    if (operation.In is not { } rangeIn || operation.Out is not { } rangeOut ||
                        rangeIn < 0 || rangeIn >= rangeOut ||
                        (rangedAsset.Kind == "video" ? rangeOut > rangedAsset.Duration : rangeIn != 0))
                        throw new ArgumentException("Set-range must name a nonempty interval inside the source; image holds start at zero.");
                    clips[index] = ranged with { In = rangeIn, Out = rangeOut };
                    break;
                case "split":
                    var clip = clips[index];
                    if (operation.At is not { } at || at <= clip.In || at >= clip.Out ||
                        string.IsNullOrWhiteSpace(operation.NewClipId) || clips.Any(c => c.Id == operation.NewClipId))
                        throw new ArgumentException("Split requires an internal source boundary and a new unique clip ID.");
                    if (project.Assets.Single(a => a.Id == clip.AssetId).Kind != "video")
                        throw new NotSupportedException("Splitting image holds is not implemented.");
                    clips[index] = clip with { Out = at };
                    clips.Insert(index + 1, clip with { Id = operation.NewClipId, In = at });
                    break;
                case "remove": clips.RemoveAt(index); break;
                // The inverse of remove. Without it a removed video clip could not be put back, so an
                // interactive editor could not offer removal at all and keep its undo exact.
                case "insert-clip":
                    var restored = project.Assets.SingleOrDefault(asset => asset.Id == operation.AssetId);
                    if (string.IsNullOrWhiteSpace(operation.ClipId) || clips.Any(c => c.Id == operation.ClipId) ||
                        restored is not { Kind: "video" } ||
                        operation.In is not { } restoredIn || operation.Out is not { } restoredOut ||
                        restoredIn < 0 || restoredIn >= restoredOut || restoredOut > restored.Duration)
                        throw new ArgumentException("Insert-clip requires a new clip ID and a nonempty interval inside a video source.");
                    if (operation.Audio is not (null or "source" or "silence"))
                        throw new ArgumentException("Insert-clip audio policy must be source or silence.");
                    var restorePosition = operation.BeforeClipId is null
                        ? clips.Count
                        : clips.FindIndex(existing => existing.Id == operation.BeforeClipId);
                    if (restorePosition < 0) throw new ArgumentException("Insert-clip references an unknown before-clip ID.");
                    clips.Insert(restorePosition, new(operation.ClipId!, restored.Id, restoredIn, restoredOut,
                        operation.Crop, operation.Fit ?? "contain", operation.Audio ?? "source"));
                    break;
                case "crop": clips[index] = clips[index] with { Crop = operation.Crop }; break;
                case "reorder":
                    if (operation.Order is not { } order || order.Any(id => id is null) ||
                        order.Length != clips.Count || order.Distinct().Count() != clips.Count ||
                        order.Except(clips.Select(c => c.Id)).Any())
                        throw new ArgumentException("Reorder must name every current clip exactly once.");
                    clips = order.Select(id => clips.Single(c => c.Id == id)).ToList();
                    break;
                case "export-mode": mode = operation.Mode ?? throw new ArgumentException("Export mode is required."); break;
                case "insert-image":
                    if (string.IsNullOrWhiteSpace(operation.ClipId) || clips.Any(c => c.Id == operation.ClipId) ||
                        string.IsNullOrWhiteSpace(operation.AssetId) || operation.Duration is not > 0 ||
                        project.Assets.SingleOrDefault(asset => asset.Id == operation.AssetId) is not { Kind: "image" })
                        throw new ArgumentException("Image insertion requires a new clip ID, an image asset and a positive hold duration.");
                    var insertion = operation.BeforeClipId is null
                        ? clips.Count
                        : clips.FindIndex(existing => existing.Id == operation.BeforeClipId);
                    if (insertion < 0) throw new ArgumentException("Image insertion references an unknown before-clip ID.");
                    clips.Insert(insertion, new(operation.ClipId, operation.AssetId, 0, operation.Duration.Value,
                        Fit: operation.Fit ?? "contain", Audio: "silence"));
                    break;
            }
        }
        var result = project with
        {
            Revision = checked(project.Revision + 1),
            Timeline = clips.ToArray(),
            ExportMode = mode,
            Proposals = []
        };
        ProjectValidator.EnsureValid(result);
        return result;
    }
}
