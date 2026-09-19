using System.Globalization;
using RoughCut.Core;

namespace RoughCut.Media;

internal static class VisualFilters
{
    public static (int Width, int Height) Canvas(EditProject project)
    {
        var assets = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var videoClips = project.Timeline.Where(clip => assets[clip.AssetId].Kind == "video").ToArray();
        if (videoClips.Select(clip => clip.AssetId).Distinct().Count() != 1)
            throw new NotSupportedException("Timeline rendering requires exactly one active video source.");
        var first = videoClips.FirstOrDefault()
            ?? throw new NotSupportedException("Timeline rendering requires one active video source to define cadence and canvas.");
        var asset = assets[first.AssetId];
        var width = first.Crop?.Width ?? asset.Width;
        var height = first.Crop?.Height ?? asset.Height;
        if (videoClips.Any(clip => (clip.Crop?.Width ?? assets[clip.AssetId].Width) != width ||
            (clip.Crop?.Height ?? assets[clip.AssetId].Height) != height))
            throw new NotSupportedException("All active video clips must use one output canvas size.");
        return (width, height);
    }

    public static string ForClip(TimelineClip clip, MediaAsset asset, int width, int height)
    {
        var filters = new List<string>();
        if (clip.Crop is { } crop)
            filters.Add(FormattableString.Invariant($"crop=w={crop.Width}:h={crop.Height}:x={crop.X}:y={crop.Y}:exact=1"));
        if (asset.Kind == "image")
        {
            if (clip.Fit == "contain")
            {
                filters.Add(FormattableString.Invariant($"scale=w={width}:h={height}:force_original_aspect_ratio=decrease:flags=neighbor"));
                filters.Add(FormattableString.Invariant($"pad=w={width}:h={height}:x=(ow-iw)/2:y=(oh-ih)/2:color=black"));
            }
            else
            {
                filters.Add(FormattableString.Invariant($"scale=w={width}:h={height}:force_original_aspect_ratio=increase:flags=neighbor"));
                filters.Add(FormattableString.Invariant($"crop=w={width}:h={height}:x=(iw-ow)/2:y=(ih-oh)/2:exact=1"));
            }
        }
        filters.Add("setsar=1");
        filters.Add("format=rgb24");
        return string.Join(',', filters);
    }

    public static string ScaleToMaxWidth(int canvasWidth, int maxWidth)
        => canvasWidth <= maxWidth ? "" : ",scale=w=" + maxWidth.ToString(CultureInfo.InvariantCulture) + ":h=-1:flags=neighbor";
}
