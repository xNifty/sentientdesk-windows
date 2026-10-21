using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SentientDesk.Services;

/// <summary>
/// Raises <see cref="ShellRestarted"/> after explorer.exe restarts, so the desktop icon
/// ListView can be hidden again and fences re-layered.
/// </summary>
public sealed class ShellRestartWatcher : IDisposable
{
    // The desktop ListView can appear a few seconds after the taskbar; re-raise a few times.
    private const int RetryCount = 5;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    private readonly uint _taskbarCreatedMsg;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _retryTimer;
    private int _retriesLeft;

    public event Action? ShellRestarted;

    public ShellRestartWatcher()
    {
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        // Hidden top-level window (not HWND_MESSAGE): only top-level windows receive the broadcast.
        _source = new HwndSource(new HwndSourceParameters("SentientDesk.ShellWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        _source.AddHook(WndProc);

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) =>
        {
            ShellRestarted?.Invoke();
            if (--_retriesLeft <= 0)
                _retryTimer.Stop();
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            _retriesLeft = RetryCount;
            _retryTimer.Stop();
            _retryTimer.Start();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);
}
