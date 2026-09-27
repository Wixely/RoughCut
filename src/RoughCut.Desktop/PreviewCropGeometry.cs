using System.Globalization;
using RoughCut.Core;

namespace RoughCut.Desktop;

/// How the player is sized so a cropped clip plays cropped. The preview copy is the whole source frame, so
/// the crop has to be done by the view: the visible box takes the crop's shape, and while frames are
/// running the picture inside it is enlarged and offset so the cropped region fills it exactly. Both
/// numbers come from one scale, so nothing is stretched.
public readonly record struct PreviewLayout(string ContainerStyle, string VideoStyle, string Fit);

public static class PreviewCropGeometry
{
    private const string Whole = "left:0;top:0;width:100%;height:100%";

    public static PreviewLayout ForCrop(double previewWidth, double previewHeight,
        int sourceWidth, int sourceHeight, Crop? crop, bool playing)
    {
        // With no crop, or before the view has been measured, the player simply fills the preview and the
        // poster — the exact rendered frame — is already what the edit shows.
        if (crop is null || previewWidth <= 0 || previewHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            crop.Width <= 0 || crop.Height <= 0 || crop.X < 0 || crop.Y < 0 ||
            crop.X + crop.Width > sourceWidth || crop.Y + crop.Height > sourceHeight)
            return new(Whole, Whole, "contain");

        var scale = Math.Min(previewWidth / crop.Width, previewHeight / crop.Height);
        var width = crop.Width * scale;
        var height = crop.Height * scale;
        var container = Style((previewWidth - width) / 2, (previewHeight - height) / 2, width, height);
        // Paused, the poster is the exact rendered frame, which is already cropped: it fills the box as it is.
        return playing
            ? new(container, Style(-crop.X * scale, -crop.Y * scale, sourceWidth * scale, sourceHeight * scale), "fill")
            : new(container, Whole, "contain");
    }

    private static string Style(double left, double top, double width, double height) => string.Format(
        CultureInfo.InvariantCulture, "left:{0:0.##}px;top:{1:0.##}px;width:{2:0.##}px;height:{3:0.##}px",
        left, top, width, height);
}
