using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SentientDesk.Models;
using SentientDesk.Native;

namespace SentientDesk.Services;

public sealed class DesktopIconService : IDisposable
{
    // Coalesces watcher bursts (a download or installer can fire dozens of events) into one rescan.
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(300);

    private readonly FileSystemWatcher _userWatcher;
    private readonly FileSystemWatcher _publicWatcher;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private List<DesktopItem> _items = [];

    // Last-write time each item's icon was extracted at; unchanged items skip SHGetFileInfo.
    private Dictionary<string, DateTime> _iconStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when the set of desktop items changes (added / removed / renamed).
    /// Icon or name updates on existing items flow through <see cref="DesktopItem"/> property changes.
    /// </summary>
    public event EventHandler? Changed;

    public DesktopIconService()
    {
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        _refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = RefreshDebounce };
        _refreshTimer.Tick += (_, _) => Refresh();

        _userWatcher = CreateWatcher(userDesktop);
        _publicWatcher = CreateWatcher(publicDesktop);
        Refresh();
    }

    public IReadOnlyList<DesktopItem> Items => _items;

    /// <summary>
    /// Rescans the Desktop folders synchronously. Existing <see cref="DesktopItem"/> instances are
    /// reused by Id so fences holding them stay in sync, and icons are re-extracted only for new
    /// or modified entries.
    /// </summary>
    public void Refresh()
    {
        _refreshTimer.Stop();

        var existing = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
            existing.TryAdd(item.Id, item);

        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DesktopItem>();
        var membershipChanged = false;

        foreach (var folder in GetDesktopFolders())
        {
            if (!Directory.Exists(folder))
                continue;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var path = entry.FullName;
                var name = entry.Name;
                if (string.IsNullOrWhiteSpace(name) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (stamps.ContainsKey(path))
                    continue;

                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                var lastWrite = entry.LastWriteTimeUtc;
                stamps[path] = lastWrite;

                var displayName = ShellFileOps.DisplayNameForPath(path, isDirectory);
                var iconStale = !_iconStamps.TryGetValue(path, out var oldStamp) || oldStamp != lastWrite;

                if (existing.Remove(path, out var item))
                {
                    item.Path = path;
                    item.DisplayName = displayName;
                    item.IsDirectory = isDirectory;
                    if (iconStale || item.Icon is null)
                        item.Icon = ExtractIcon(path, isDirectory);
                }
                else
                {
                    membershipChanged = true;
                    item = new DesktopItem
                    {
                        Id = path,
                        Path = path,
                        DisplayName = displayName,
                        IsDirectory = isDirectory,
                        Icon = ExtractIcon(path, isDirectory)
                    };
                }

                result.Add(item);
            }
        }

        // Anything left in `existing` disappeared from disk.
        if (existing.Count > 0)
            membershipChanged = true;

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _items = result;
        _iconStamps = stamps;

        if (membershipChanged)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public static void Launch(DesktopItem item)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open '{item.DisplayName}':\n{ex.Message}", "SentientDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static BitmapSource? GetIcon(string path, bool isDirectory) => ExtractIcon(path, isDirectory);

    public void Dispose()
    {
        _refreshTimer.Stop();
        _userWatcher.Dispose();
        _publicWatcher.Dispose();
    }

    private FileSystemWatcher CreateWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = Directory.Exists(path)
        };

        // Watcher events arrive on thread-pool threads; the timer lives on the UI dispatcher.
        void OnChange(object _, FileSystemEventArgs __) =>
            Application.Current?.Dispatcher.BeginInvoke(ScheduleRefresh);

        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += OnChange;
        watcher.Changed += OnChange;
        // Buffer overflow drops events — a full rescan recovers whatever was missed.
        watcher.Error += (_, _) => Application.Current?.Dispatcher.BeginInvoke(ScheduleRefresh);
        return watcher;
    }

    private static IEnumerable<string> GetDesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static BitmapSource? ExtractIcon(string path, bool isDirectory)
    {
        var info = new NativeMethods.SHFILEINFO();
        var attrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;

        // Prefer real file attributes when the path exists.
        if (File.Exists(path) || Directory.Exists(path))
            NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), flags);
        else
            NativeMethods.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info),
                flags | NativeMethods.SHGFI_USEFILEATTRIBUTES);

        if (info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }
}
