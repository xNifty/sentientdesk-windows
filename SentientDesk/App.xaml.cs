using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using SentientDesk.Models;
using SentientDesk.Services;
using Velopack;
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
    private UpdateService? _updates;
    private Mutex? _singleInstance;
    private bool _updateCheckInProgress;

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
        _hotkeys.HotkeyPressed += () => Dispatcher.BeginInvoke(() => _manager?.CreateFence(FenceTemplate.Blank));

        _updates = new UpdateService();
        CreateTrayIcon();
        _ = CheckForUpdatesOnStartupAsync();
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

        var newFence = new ToolStripMenuItem("New fence");
        foreach (var template in FenceTemplate.All)
        {
            var label = template.Id == FenceTemplate.Blank.Id
                ? $"{template.MenuLabel}\tCtrl+Shift+F"
                : template.MenuLabel;
            var item = new ToolStripMenuItem(label);
            var captured = template;
            item.Click += (_, _) => _manager?.CreateFence(captured);
            newFence.DropDownItems.Add(item);
        }
        menu.Items.Add(newFence);

        menu.Items.Add(new ToolStripSeparator());

        var hideIcons = new ToolStripMenuItem("Hide desktop icons")
        {
            Checked = _manager?.Layout.HideDesktopIcons ?? true,
            CheckOnClick = true
        };
        hideIcons.CheckedChanged += (_, _) => _manager?.SetHideDesktopIcons(hideIcons.Checked);
        menu.Items.Add(hideIcons);

        var snapToGrid = new ToolStripMenuItem("Snap to grid")
        {
            Checked = _manager?.Layout.SnapToGrid ?? true,
            CheckOnClick = true
        };
        snapToGrid.CheckedChanged += (_, _) => _manager?.SetSnapToGrid(snapToGrid.Checked);
        menu.Items.Add(snapToGrid);

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

        var checkUpdates = new ToolStripMenuItem("Check for updates…");
        checkUpdates.Click += async (_, _) => await CheckForUpdatesManualAsync();
        menu.Items.Add(checkUpdates);

        var about = new ToolStripMenuItem("About");
        about.Click += (_, _) => MessageBox.Show(
            $"SentientDesk {GetAppVersion()}\n\nA free, open desktop organizer inspired by that other desktop organizer.\nFences overlay your Desktop folder — Exit restores normal desktop icons.\n\nHotkey: Ctrl+Shift+F — new blank fence\nHold Alt while dragging to bypass snap-to-grid",
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

        _tray.DoubleClick += (_, _) => _manager?.CreateFence(FenceTemplate.Blank);
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_updates is null || !_updates.IsInstalled)
            return;

        try
        {
            var update = await _updates.CheckForUpdatesAsync();
            if (update is null)
                return;

            await Dispatcher.InvokeAsync(() => PromptAndApplyUpdate(update, silentIfDeclined: true));
        }
        catch
        {
            // Background check should never interrupt normal use.
        }
    }

    private async Task CheckForUpdatesManualAsync()
    {
        if (_updateCheckInProgress)
            return;

        if (_updates is null || !_updates.IsInstalled)
        {
            MessageBox.Show(
                "Updates are only available when SentientDesk is installed via the official installer.",
                "SentientDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            var update = await _updates.CheckForUpdatesAsync();
            if (update is null)
            {
                MessageBox.Show(
                    $"You're up to date ({GetAppVersion()}).",
                    "SentientDesk",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await PromptAndApplyUpdate(update, silentIfDeclined: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not check for updates:\n{ex.Message}",
                "SentientDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task PromptAndApplyUpdate(UpdateInfo update, bool silentIfDeclined)
    {
        if (_updates is null)
            return;

        var version = update.TargetFullRelease.Version.ToString();
        var result = MessageBox.Show(
            $"SentientDesk {version} is available.\n\nDownload and install now? The app will restart.",
            "Update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            if (!silentIfDeclined && _tray is not null)
            {
                _tray.ShowBalloonTip(
                    4000,
                    "Update available",
                    $"SentientDesk {version} is ready when you are — use Check for updates…",
                    ToolTipIcon.Info);
            }
            return;
        }

        try
        {
            if (_tray is not null)
            {
                _tray.ShowBalloonTip(3000, "Updating", "Downloading update…", ToolTipIcon.Info);
            }

            await _updates.DownloadAndApplyAsync(update);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Update failed:\n{ex.Message}",
                "SentientDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string GetAppVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null)
            return "0.0.0";
        return $"{version.Major}.{version.Minor}.{version.Build}";
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
