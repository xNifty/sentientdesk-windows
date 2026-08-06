using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SentientDesk.Services;

/// <summary>
/// Registers a global Ctrl+Shift+F hotkey to create a new fence.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4601; // 'F' vibe
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF = 0x46;
    private const int WmHotkey = 0x0312;

    private readonly Window _messageWindow;
    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyService()
    {
        _messageWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden
        };
        _messageWindow.SourceInitialized += OnSourceInitialized;
        _messageWindow.Show();
        _messageWindow.Hide();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(_messageWindow);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        _registered = RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModShift, VkF);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            var hwnd = new WindowInteropHelper(_messageWindow).Handle;
            UnregisterHotKey(hwnd, HotkeyId);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _messageWindow.Close();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
