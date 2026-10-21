using System.Text;
using SentientDesk.Native;

namespace SentientDesk.Services;

/// <summary>
/// Hides or shows the native Windows desktop icon ListView so SentientDesk can own the presentation.
/// </summary>
public sealed class DesktopIconHider
{
    private bool _hidden;

    public bool IsHidden => _hidden;

    /// <summary>
    /// Always re-applies, even when <paramref name="hide"/> matches the cached state —
    /// explorer may have restarted and recreated the ListView.
    /// </summary>
    public void SetHidden(bool hide)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return;

        NativeMethods.ShowWindow(listView, hide ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOW);
        _hidden = hide;
    }

    public void Restore()
    {
        SetHidden(false);
        _hidden = false;
    }

    /// <summary>
    /// Shows the desktop icon ListView regardless of prior hide state.
    /// Used on Exit and by <c>--restore-desktop</c> during uninstall.
    /// </summary>
    public static void ForceShowDesktopIcons()
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return;

        NativeMethods.ShowWindow(listView, NativeMethods.SW_SHOW);
    }

    private static IntPtr FindDesktopListView()
    {
        // Classic Progman path
        var progman = NativeMethods.FindWindow("Progman", "Program Manager");
        var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            var list = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
            if (list != IntPtr.Zero)
                return list;
        }

        // Windows 10/11 WorkerW path
        IntPtr result = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var shellView = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero)
                return true;

            var list = NativeMethods.FindWindowEx(shellView, IntPtr.Zero, "SysListView32", "FolderView");
            if (list != IntPtr.Zero)
            {
                result = list;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static string? GetWindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
