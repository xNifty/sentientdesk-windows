using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SentientDesk.Models;
using SentientDesk.Native;
using Clipboard = System.Windows.Forms.Clipboard;
using DataObject = System.Windows.Forms.DataObject;

namespace SentientDesk.Services;

/// <summary>
/// Shell-backed file verbs used by fence icon menus (rename / properties / clipboard).
/// Does not delete files — remove-from-fence is handled by <see cref="FenceManager"/>.
/// </summary>
public static class ShellFileOps
{
    private const int DropeffectCopy = 1;
    private const int DropeffectMove = 2;

    public static void ShowProperties(DesktopItem item)
    {
        try
        {
            var info = new NativeMethods.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
                fMask = NativeMethods.SEE_MASK_INVOKEIDLIST | NativeMethods.SEE_MASK_FLAG_DDEWAIT,
                hwnd = IntPtr.Zero,
                lpVerb = "properties",
                lpFile = item.Path,
                nShow = NativeMethods.SW_SHOWNORMAL
            };

            if (!NativeMethods.ShellExecuteEx(ref info))
            {
                MessageBox.Show($"Could not open properties for '{item.DisplayName}'.",
                    "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open properties for '{item.DisplayName}':\n{ex.Message}",
                "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void CopyToClipboard(DesktopItem item) => SetClipboard(item, cut: false);

    public static void CutToClipboard(DesktopItem item) => SetClipboard(item, cut: true);

    /// <summary>
    /// Renames the on-disk file/folder. Returns the new full path, or null on failure/cancel.
    /// </summary>
    public static string? TryRename(DesktopItem item, string newBaseName)
    {
        newBaseName = newBaseName.Trim();
        if (string.IsNullOrWhiteSpace(newBaseName))
            return null;

        if (newBaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("The name contains invalid characters.", "SentientDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        try
        {
            var dir = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrEmpty(dir))
                return null;

            var extension = item.IsDirectory ? "" : Path.GetExtension(item.Path);
            // Keep .lnk / .url extension when the user edits the display stem.
            var keepExt = !item.IsDirectory &&
                          (item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase));

            string newFileName;
            if (keepExt)
            {
                // DisplayName is stem; if user typed an extension already, respect it only when matching.
                if (newBaseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    newFileName = newBaseName;
                else
                    newFileName = newBaseName + extension;
            }
            else if (item.IsDirectory)
            {
                newFileName = newBaseName;
            }
            else
            {
                // Regular files: DisplayName includes extension in our scanner.
                newFileName = newBaseName;
            }

            var newPath = Path.Combine(dir, newFileName);
            if (newPath.Equals(item.Path, StringComparison.OrdinalIgnoreCase))
                return item.Path;

            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                MessageBox.Show($"A file or folder named '{newFileName}' already exists.",
                    "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            if (item.IsDirectory)
                Directory.Move(item.Path, newPath);
            else
                File.Move(item.Path, newPath);

            return newPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not rename '{item.DisplayName}':\n{ex.Message}",
                "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    public static string DisplayNameForPath(string path, bool isDirectory)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            return path;

        if (!isDirectory &&
            (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(stem) ? name : stem;
        }

        return name;
    }

    private static void SetClipboard(DesktopItem item, bool cut)
    {
        try
        {
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                MessageBox.Show($"'{item.DisplayName}' is no longer on disk.", "SentientDesk",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var files = new StringCollection { item.Path };
            var data = new DataObject();
            data.SetFileDropList(files);

            // Preferred DropEffect: Explorer uses this to distinguish Cut vs Copy.
            var effect = cut ? DropeffectMove : DropeffectCopy;
            var effectBytes = BitConverter.GetBytes(effect);
            data.SetData("Preferred DropEffect", new MemoryStream(effectBytes));

            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not place '{item.DisplayName}' on the clipboard:\n{ex.Message}",
                "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
