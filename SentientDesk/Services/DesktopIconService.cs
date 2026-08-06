using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SentientDesk.Models;
using SentientDesk.Native;

namespace SentientDesk.Services;

public sealed class DesktopIconService
{
    private readonly FileSystemWatcher _userWatcher;
    private readonly FileSystemWatcher _publicWatcher;
    private List<DesktopItem> _items = [];

    public event EventHandler? Changed;

    public DesktopIconService()
    {
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        _userWatcher = CreateWatcher(userDesktop);
        _publicWatcher = CreateWatcher(publicDesktop);
        Refresh();
    }

    public IReadOnlyList<DesktopItem> Items => _items;

    public void Refresh()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DesktopItem>();

        foreach (var folder in GetDesktopFolders())
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!paths.Add(path))
                    continue;

                result.Add(new DesktopItem
                {
                    Id = path,
                    Path = path,
                    DisplayName = Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem &&
                                  (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        ? stem
                        : name,
                    IsDirectory = Directory.Exists(path),
                    Icon = ExtractIcon(path, Directory.Exists(path))
                });
            }
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _items = result;
        Changed?.Invoke(this, EventArgs.Empty);
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

    public void Dispose()
    {
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

        void OnChange(object _, FileSystemEventArgs __) =>
            Application.Current?.Dispatcher.BeginInvoke(Refresh);

        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += OnChange;
        watcher.Changed += OnChange;
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
