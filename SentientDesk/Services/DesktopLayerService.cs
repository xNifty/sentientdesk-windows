using System.Text;
using SentientDesk.Native;

namespace SentientDesk.Services;

/// <summary>
/// Keeps fence windows above the wallpaper (including Wallpaper Engine) but
/// strictly below normal application windows — without parenting to WorkerW,
/// which Wallpaper Engine owns and would make fences invisible.
/// </summary>
public sealed class DesktopLayerService : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly HashSet<IntPtr> _fences = [];
    private readonly NativeMethods.WinEventDelegate _winEventProc;
    private readonly object _gate = new();
    private IntPtr _hook;
    private bool _disposed;
    private bool _sinking;
    private int _interactionDepth;

    public DesktopLayerService()
    {
        _winEventProc = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemMinimizeEnd,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    public bool IsInteracting
    {
        get { lock (_gate) return _interactionDepth > 0; }
    }

    public void Register(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed)
            return;

        lock (_gate)
            _fences.Add(hwnd);

        ApplyBaseStyles(hwnd);
        if (!IsInteracting)
            Sink(hwnd);
    }

    /// <summary>
    /// Register a click-through overlay (grid) with the same desktop layering as fences.
    /// </summary>
    public void RegisterClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed)
            return;

        lock (_gate)
            _fences.Add(hwnd);

        ApplyBaseStyles(hwnd);
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

        if (!IsInteracting)
            Sink(hwnd);
    }

    public void Unregister(IntPtr hwnd)
    {
        lock (_gate)
            _fences.Remove(hwnd);
    }

    public void BeginInteraction()
    {
        lock (_gate)
        {
            _interactionDepth++;
            if (_interactionDepth == 1)
            {
                foreach (var hwnd in _fences)
                {
                    if (NativeMethods.IsWindow(hwnd))
                        SetNoActivate(hwnd, enabled: false);
                }
            }
        }
    }

    public void EndInteraction()
    {
        lock (_gate)
        {
            if (_interactionDepth == 0)
                return;

            _interactionDepth--;
            if (_interactionDepth == 0)
            {
                foreach (var hwnd in _fences)
                {
                    if (!NativeMethods.IsWindow(hwnd))
                        continue;
                    SetNoActivate(hwnd, enabled: true);
                    Sink(hwnd);
                }
            }
        }
    }

    public void KeepBehind(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed || IsInteracting)
            return;
        Sink(hwnd);
    }

    public void SinkAll()
    {
        if (_disposed || _sinking || IsInteracting)
            return;

        _sinking = true;
        try
        {
            IntPtr[] snapshot;
            lock (_gate)
                snapshot = _fences.ToArray();

            foreach (var hwnd in snapshot)
            {
                if (NativeMethods.IsWindow(hwnd))
                    Sink(hwnd);
            }
        }
        finally
        {
            _sinking = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        lock (_gate)
        {
            _fences.Clear();
            _interactionDepth = 0;
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || IsInteracting)
            return;

        bool isOurs;
        lock (_gate)
            isOurs = _fences.Contains(hwnd);

        if (!isOurs)
            SinkAll();
    }

    private static void ApplyBaseStyles(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        // Clear topmost if somehow set.
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private static void SetNoActivate(IntPtr hwnd, bool enabled)
    {
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if (enabled)
            ex |= NativeMethods.WS_EX_NOACTIVATE;
        else
            ex &= ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private static void Sink(IntPtr hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
