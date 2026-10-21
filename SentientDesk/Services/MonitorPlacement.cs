using System.Windows;
using SentientDesk.Native;
using WinForms = System.Windows.Forms;

namespace SentientDesk.Services;

/// <summary>
/// Multi-monitor placement helpers using virtual-desktop coordinates.
/// All public inputs/outputs are WPF device-independent units (DIPs), matching
/// <see cref="Window.Left"/>/<see cref="Window.Top"/>. Win32 / WinForms values
/// (<see cref="WinForms.Screen"/>, GetCursorPos, PointToScreen) are physical pixels
/// and must go through <see cref="ToDip(Point)"/>.
/// </summary>
internal static class MonitorPlacement
{
    private const double MinVisiblePx = 40;
    private const double DefaultCascade = 80;
    private const double CascadeStep = 28;

    // WPF runs system-DPI-aware (no per-monitor manifest), so every Win32 coordinate in
    // this process is scaled by the single system DPI, fixed for the process lifetime.
    private static readonly Lazy<double> DipPerPixel = new(() =>
    {
        try
        {
            var dpi = NativeMethods.GetDpiForSystem();
            return dpi > 0 ? 96.0 / dpi : 1.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 1.0;
        }
    });

    public static Point ToDip(Point pixels) =>
        new(pixels.X * DipPerPixel.Value, pixels.Y * DipPerPixel.Value);

    /// <summary>
    /// Current cursor position in DIPs, or null if it cannot be read.
    /// </summary>
    public static Point? CursorDip() =>
        NativeMethods.GetCursorPos(out var pt) ? ToDip(new Point(pt.X, pt.Y)) : null;

    /// <summary>
    /// Cap absurd sizes from corrupt layouts against the full virtual desktop.
    /// </summary>
    public static (double Width, double Height) CapAbsurdSize(double width, double height)
    {
        var maxW = Math.Max(320, SystemParameters.VirtualScreenWidth * 0.75);
        var maxH = Math.Max(240, SystemParameters.VirtualScreenHeight * 0.75);

        if (width > maxW)
            width = 240;
        if (height > maxH)
            height = 200;

        return (width, height);
    }

    /// <summary>
    /// If the fence does not meaningfully intersect any monitor work area, move it
    /// onto the nearest (or primary) work area. Size is preserved.
    /// </summary>
    public static (double Left, double Top) EnsureOnScreen(double left, double top, double width, double height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var fence = new Rect(left, top, width, height);
        if (IntersectsAnyWorkArea(fence, MinVisiblePx))
            return (left, top);

        var target = NearestWorkArea(fence) ?? PrimaryWorkArea();
        var x = Math.Clamp(left, target.Left, Math.Max(target.Left, target.Right - width));
        var y = Math.Clamp(top, target.Top, Math.Max(target.Top, target.Bottom - height));

        // If the fence is larger than the work area, pin to the top-left of that work area.
        if (width > target.Width)
            x = target.Left;
        if (height > target.Height)
            y = target.Top;

        return (x, y);
    }

    /// <summary>
    /// Place a new fence near the cascade offset on the monitor under the cursor.
    /// </summary>
    public static Point OriginForNewFence(double width, double height, int offsetIndex)
    {
        var work = WorkAreaUnderCursor() ?? PrimaryWorkArea();
        var offset = DefaultCascade + (Math.Max(0, offsetIndex) * CascadeStep);
        var left = work.Left + offset;
        var top = work.Top + offset;

        (left, top) = EnsureOnScreen(left, top, width, height);
        return new Point(left, top);
    }

    /// <summary>
    /// Work-area top-left for the monitor containing <paramref name="point"/>
    /// (virtual-desktop coordinates). Falls back to primary.
    /// </summary>
    public static Point WorkAreaOriginForPoint(Point point)
    {
        var work = WorkAreaContaining(point) ?? PrimaryWorkArea();
        return new Point(work.Left, work.Top);
    }

    /// <summary>
    /// All monitor work areas in virtual-desktop coordinates (for grid overlay drawing).
    /// </summary>
    public static IEnumerable<Rect> AllWorkAreas()
    {
        foreach (var screen in WinForms.Screen.AllScreens)
            yield return ToRect(screen.WorkingArea);
    }

    private static bool IntersectsAnyWorkArea(Rect fence, double minVisible)
    {
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa = ToRect(screen.WorkingArea);
            var overlap = Rect.Intersect(fence, wa);
            if (overlap.IsEmpty)
                continue;
            if (overlap.Width >= minVisible && overlap.Height >= minVisible)
                return true;
            // Title-bar-sized strip still counts as recoverable visibility.
            if (overlap.Height >= minVisible && overlap.Width > 0)
                return true;
        }

        return false;
    }

    private static Rect? NearestWorkArea(Rect fence)
    {
        var screens = WinForms.Screen.AllScreens;
        if (screens.Length == 0)
            return null;

        Rect? best = null;
        var bestDist = double.MaxValue;
        var center = new Point(fence.Left + fence.Width / 2, fence.Top + fence.Height / 2);

        foreach (var screen in screens)
        {
            var wa = ToRect(screen.WorkingArea);
            var dx = 0.0;
            if (center.X < wa.Left) dx = wa.Left - center.X;
            else if (center.X > wa.Right) dx = center.X - wa.Right;
            var dy = 0.0;
            if (center.Y < wa.Top) dy = wa.Top - center.Y;
            else if (center.Y > wa.Bottom) dy = center.Y - wa.Bottom;
            var dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = wa;
            }
        }

        return best;
    }

    private static Rect PrimaryWorkArea()
    {
        var primary = WinForms.Screen.PrimaryScreen;
        if (primary is not null)
            return ToRect(primary.WorkingArea);

        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private static Rect? WorkAreaUnderCursor() =>
        CursorDip() is { } cursor ? WorkAreaContaining(cursor) : null;

    private static Rect? WorkAreaContaining(Point point)
    {
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var bounds = ToRect(screen.Bounds);
            // Half-open on the right/bottom so a shared monitor edge belongs to one screen.
            if (point.X >= bounds.Left && point.X < bounds.Right &&
                point.Y >= bounds.Top && point.Y < bounds.Bottom)
                return ToRect(screen.WorkingArea);
        }

        return null;
    }

    /// <summary>
    /// Pixel rectangle (WinForms / Win32) to DIPs.
    /// </summary>
    private static Rect ToRect(System.Drawing.Rectangle r)
    {
        var scale = DipPerPixel.Value;
        return new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
    }
}
