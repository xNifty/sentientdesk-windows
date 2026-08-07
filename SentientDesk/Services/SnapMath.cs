using System.Windows;

namespace SentientDesk.Services;

/// <summary>
/// Quantize fence geometry to a fixed desktop grid relative to a monitor work-area origin.
/// </summary>
internal static class SnapMath
{
    public const double GridSize = 16;

    public static double Quantize(double value, double origin) =>
        origin + Math.Round((value - origin) / GridSize) * GridSize;

    public static Point SnapPoint(double left, double top, Point workAreaOrigin) =>
        new(Quantize(left, workAreaOrigin.X), Quantize(top, workAreaOrigin.Y));

    /// <summary>
    /// Snap the bottom-right corner onto the grid, then derive width/height from a fixed top-left.
    /// </summary>
    public static Size SnapSize(
        double left,
        double top,
        double width,
        double height,
        Point workAreaOrigin,
        double minWidth,
        double minHeight)
    {
        var right = Quantize(left + width, workAreaOrigin.X);
        var bottom = Quantize(top + height, workAreaOrigin.Y);
        var w = Math.Max(minWidth, right - left);
        var h = Math.Max(minHeight, bottom - top);
        return new Size(w, h);
    }
}
