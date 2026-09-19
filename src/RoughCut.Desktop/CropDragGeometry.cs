using RoughCut.Core;

namespace RoughCut.Desktop;

public enum CropDragMode { Move, NorthWest, NorthEast, SouthWest, SouthEast }

public static class CropDragGeometry
{
    public static Crop Update(Crop start, CropDragMode mode, int deltaX, int deltaY,
        int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || start.X < 0 || start.Y < 0 ||
            start.Width <= 0 || start.Height <= 0 || (long)start.X + start.Width > sourceWidth ||
            (long)start.Y + start.Height > sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(start), "The starting crop must fit within the source frame.");

        var left = start.X;
        var top = start.Y;
        var right = start.X + start.Width;
        var bottom = start.Y + start.Height;
        switch (mode)
        {
            case CropDragMode.Move:
                left = Clamp((long)start.X + deltaX, 0, sourceWidth - start.Width);
                top = Clamp((long)start.Y + deltaY, 0, sourceHeight - start.Height);
                right = left + start.Width;
                bottom = top + start.Height;
                break;
            case CropDragMode.NorthWest:
                left = Clamp((long)start.X + deltaX, 0, right - 1);
                top = Clamp((long)start.Y + deltaY, 0, bottom - 1);
                break;
            case CropDragMode.NorthEast:
                right = Clamp((long)right + deltaX, left + 1, sourceWidth);
                top = Clamp((long)start.Y + deltaY, 0, bottom - 1);
                break;
            case CropDragMode.SouthWest:
                left = Clamp((long)start.X + deltaX, 0, right - 1);
                bottom = Clamp((long)bottom + deltaY, top + 1, sourceHeight);
                break;
            case CropDragMode.SouthEast:
                right = Clamp((long)right + deltaX, left + 1, sourceWidth);
                bottom = Clamp((long)bottom + deltaY, top + 1, sourceHeight);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return new(left, top, right - left, bottom - top);
    }

    private static int Clamp(long value, int minimum, int maximum) =>
        (int)Math.Clamp(value, minimum, maximum);
}
