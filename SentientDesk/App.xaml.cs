using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using SentientDesk.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SentientDesk;

public partial class App : Application
{
    private NotifyIcon? _tray;
    private Icon? _trayIcon;
    private Bitmap? _trayBitmap;
    private FenceManager? _manager;
    private DesktopIconService? _icons;
    private DesktopIconHider? _hider;
    private LayoutStore? _store;
    private HotkeyService? _hotkeys;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--restore-desktop", StringComparison.OrdinalIgnoreCase)))
        {
            DesktopIconHider.ForceShowDesktopIcons();
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, "SentientDesk.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("SentientDesk is already running.", "SentientDesk",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "SentientDesk error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _store = new LayoutStore();
        _icons = new DesktopIconService();
        _hider = new DesktopIconHider();
        _manager = new FenceManager(_store, _icons, _hider);
        _manager.Start();

        _hotkeys = new HotkeyService();
        _hotkeys.HotkeyPressed += () => Dispatcher.BeginInvoke(() => _manager?.CreateFence());

        CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _manager?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        _trayBitmap?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();

        var newFence = new ToolStripMenuItem("New fence\tCtrl+Shift+F");
        newFence.Click += (_, _) => _manager?.CreateFence();
        menu.Items.Add(newFence);

        menu.Items.Add(new ToolStripSeparator());

        var hideIcons = new ToolStripMenuItem("Hide desktop icons")
        {
            Checked = _manager?.Layout.HideDesktopIcons ?? true,
            CheckOnClick = true
        };
        hideIcons.CheckedChanged += (_, _) => _manager?.SetHideDesktopIcons(hideIcons.Checked);
        menu.Items.Add(hideIcons);

        var startWithWindows = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _manager?.Layout.StartWithWindows ?? false,
            CheckOnClick = true
        };
        startWithWindows.CheckedChanged += (_, _) => _manager?.SetStartWithWindows(startWithWindows.Checked);
        menu.Items.Add(startWithWindows);

        var refresh = new ToolStripMenuItem("Refresh icons");
        refresh.Click += (_, _) => _icons?.Refresh();
        menu.Items.Add(refresh);

        menu.Items.Add(new ToolStripSeparator());

        var openLayout = new ToolStripMenuItem("Open layout folder");
        openLayout.Click += (_, _) =>
        {
            if (_store is null) return;
            var dir = System.IO.Path.GetDirectoryName(_store.FilePath)!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        };
        menu.Items.Add(openLayout);

        var about = new ToolStripMenuItem("About");
        about.Click += (_, _) => MessageBox.Show(
            "SentientDesk 0.1.0\n\nA free, open desktop organizer inspired by that other desktop organizer.\nFences overlay your Desktop folder — Exit restores normal desktop icons.\n\nHotkey: Ctrl+Shift+F — new fence",
            "About SentientDesk",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        menu.Items.Add(about);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);

        _trayBitmap = CreateTrayBitmap();
        _trayIcon = Icon.FromHandle(_trayBitmap.GetHicon());

        _tray = new NotifyIcon
        {
            Text = "SentientDesk",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) => _manager?.CreateFence();
    }

    private static Bitmap CreateTrayBitmap()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(220, 32, 48, 64));
        using var border = new Pen(Color.FromArgb(240, 200, 210, 220), 1.5f);
        g.FillRectangle(fill, 4, 6, 24, 20);
        g.DrawRectangle(border, 4, 6, 24, 20);
        g.DrawLine(border, 4, 12, 28, 12);
        return bmp;
    }
}
