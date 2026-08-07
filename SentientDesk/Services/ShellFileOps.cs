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

    /// <summary>
    /// Suggests an unused folder name under the user Desktop (New folder, New folder (2), …).
    /// </summary>
    public static string UniqueDesktopFolderName(string preferred = "New folder")
    {
        preferred = string.IsNullOrWhiteSpace(preferred) ? "New folder" : preferred.Trim();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var candidate = preferred;
        var n = 2;
        while (Directory.Exists(Path.Combine(desktop, candidate)) ||
               File.Exists(Path.Combine(desktop, candidate)))
        {
            candidate = $"{preferred} ({n})";
            n++;
        }

        return candidate;
    }

    /// <summary>
    /// Creates a folder on the user Desktop. Returns the full path, or null on failure.
    /// </summary>
    public static string? TryCreateDesktopFolder(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("The name contains invalid characters.", "SentientDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            {
                MessageBox.Show("Could not find your Desktop folder.", "SentientDesk",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var path = Path.Combine(desktop, name);
            if (File.Exists(path) || Directory.Exists(path))
            {
                MessageBox.Show($"A file or folder named '{name}' already exists.",
                    "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create folder '{name}':\n{ex.Message}",
                "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>
    /// Moves a Desktop item into a Desktop folder (filesystem). Returns false on failure.
    /// </summary>
    public static bool TryMoveIntoFolder(DesktopItem item, DesktopItem folder)
    {
        if (!folder.IsDirectory)
            return false;

        if (item.Id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Cannot move a folder into itself.", "SentientDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (item.IsDirectory && IsDescendantPath(folder.Path, item.Path))
        {
            MessageBox.Show("Cannot move a folder into one of its subfolders.", "SentientDesk",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            if (!Directory.Exists(folder.Path))
            {
                MessageBox.Show($"Folder '{folder.DisplayName}' is no longer on disk.",
                    "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                MessageBox.Show($"'{item.DisplayName}' is no longer on disk.",
                    "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var fileName = Path.GetFileName(item.Path);
            var dest = Path.Combine(folder.Path, fileName);
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                MessageBox.Show($"'{fileName}' already exists inside '{folder.DisplayName}'.",
                    "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (item.IsDirectory)
                Directory.Move(item.Path, dest);
            else
                File.Move(item.Path, dest);

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not move '{item.DisplayName}' into '{folder.DisplayName}':\n{ex.Message}",
                "SentientDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is inside <paramref name="ancestor"/> (or equal).
    /// </summary>
    private static bool IsDescendantPath(string candidate, string ancestor)
    {
        var cand = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var anc = Path.GetFullPath(ancestor)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (cand.Equals(anc, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = anc + Path.DirectorySeparatorChar;
        return cand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
