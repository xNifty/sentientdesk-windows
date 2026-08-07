using System.Windows;
using System.Windows.Media;
using SentientDesk.Services;

namespace SentientDesk.Views;

/// <summary>
/// Full-virtual-desktop click-through overlay that draws a faint snap grid per monitor work area.
/// Built in code (no XAML) so it stays a lightweight tool window.
/// </summary>
public sealed class GridOverlayWindow : Window
{
    private readonly GridCanvas _canvas = new();

    public GridOverlayWindow()
    {
        Title = "SentientDesk Grid";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Nearly-clear background so the transparent window still composites OnRender content.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = false;
        Content = _canvas;
        FitVirtualScreen();
    }

    public void FitVirtualScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _canvas.InvalidateVisual();
    }

    private sealed class GridCanvas : FrameworkElement
    {
        private static readonly Pen GridPen;

        static GridCanvas()
        {
            GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);
            GridPen.Freeze();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (Window.GetWindow(this) is not { } host)
                return;

            var originX = host.Left;
            var originY = host.Top;
            var step = SnapMath.GridSize;

            foreach (var work in MonitorPlacement.AllWorkAreas())
            {
                var local = new Rect(
                    work.X - originX,
                    work.Y - originY,
                    work.Width,
                    work.Height);

                if (local.Width <= 0 || local.Height <= 0)
                    continue;

                for (var x = local.Left; x <= local.Right + 0.5; x += step)
                {
                    drawingContext.DrawLine(
                        GridPen,
                        new Point(x, local.Top),
                        new Point(x, local.Bottom));
                }

                for (var y = local.Top; y <= local.Bottom + 0.5; y += step)
                {
                    drawingContext.DrawLine(
                        GridPen,
                        new Point(local.Left, y),
                        new Point(local.Right, y));
                }
            }
        }
    }
}
