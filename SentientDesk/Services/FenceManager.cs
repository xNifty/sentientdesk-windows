using System.Windows;
using System.Windows.Interop;
using SentientDesk.Models;
using SentientDesk.Native;
using SentientDesk.ViewModels;
using SentientDesk.Views;

namespace SentientDesk.Services;

public sealed class FenceManager : IDisposable
{
    private readonly LayoutStore _store;
    private readonly DesktopIconService _icons;
    private readonly DesktopIconHider _hider;
    private readonly DesktopLayerService _desktopLayer = new();
    private readonly List<FenceWindow> _windows = [];
    private AppLayout _layout;
    private bool _disposed;

    public FenceManager(LayoutStore store, DesktopIconService icons, DesktopIconHider hider)
    {
        _store = store;
        _icons = icons;
        _hider = hider;
        _layout = store.Load();
        _icons.Changed += OnIconsChanged;
    }

    public AppLayout Layout => _layout;
    public IReadOnlyList<FenceWindow> Windows => _windows;
    public DesktopLayerService DesktopLayer => _desktopLayer;

    public void Start()
    {
        if (_layout.Fences.Count == 0)
            _layout = _store.Load();

        DistributeUnassignedItems();
        CreateWindowsFromLayout();
        _hider.SetHidden(_layout.HideDesktopIcons);
        StartupService.SetEnabled(_layout.StartWithWindows);

        // Persist item assignments only — do not rewrite geometry from live window sizes
        // (startup layout can briefly inflate and would clobber user-resized fences).
        Persist();
    }

    public FenceWindow CreateFence(string? title = null, Point? origin = null)
    {
        var data = new FenceData
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title ?? $"Fence {_windows.Count + 1}",
            Left = origin?.X ?? 80 + (_windows.Count * 28),
            Top = origin?.Y ?? 80 + (_windows.Count * 28),
            Width = 240,
            Height = 200,
            Color = PickColor(_windows.Count)
        };

        _layout.Fences.Add(data);
        var vm = new FenceViewModel(data);
        var window = AttachWindow(vm);
        Persist();
        return window;
    }

    public void RemoveFence(FenceWindow window)
    {
        var orphanIds = window.ViewModel.Items.Select(i => i.Id).ToList();
        window.ViewModel.Items.Clear();

        _windows.Remove(window);
        _layout.Fences.RemoveAll(f => f.Id == window.ViewModel.Id);
        window.Close();

        if (_windows.Count > 0 && orphanIds.Count > 0)
        {
            var target = _windows[0].ViewModel;
            foreach (var id in orphanIds)
            {
                var item = _icons.Items.FirstOrDefault(i => i.Id == id);
                if (item is null) continue;
                if (target.Items.All(x => x.Id != id))
                    target.Items.Add(item);
                _layout.ItemFenceMap[id] = target.Id;
            }
        }
        else
        {
            foreach (var id in orphanIds)
                _layout.ItemFenceMap.Remove(id);
        }

        Persist();
    }

    public void MoveItem(DesktopItem item, FenceViewModel from, FenceViewModel to, int? insertIndex = null)
    {
        var fromIndex = from.Items.IndexOf(item);
        if (fromIndex < 0)
            return;

        from.Items.RemoveAt(fromIndex);

        var index = insertIndex ?? to.Items.Count;
        if (ReferenceEquals(from, to) && index > fromIndex)
            index--;
        index = Math.Clamp(index, 0, to.Items.Count);

        to.Items.Insert(index, item);
        _layout.ItemFenceMap[item.Id] = to.Id;

        // Item reflow must not change fence geometry.
        foreach (var window in _windows)
            window.LockSizeToViewModel();
        Persist();
    }

    public void SetHideDesktopIcons(bool hide)
    {
        _layout.HideDesktopIcons = hide;
        _hider.SetHidden(hide);
        Persist();
    }

    public void SetStartWithWindows(bool enabled)
    {
        _layout.StartWithWindows = enabled;
        StartupService.SetEnabled(enabled);
        Persist();
    }

    public void Persist()
    {
        if (_disposed)
            return;

        if (_windows.Count == 0)
            return;

        // Geometry lives on ViewModel and is updated only by user move/resize.
        _layout.Fences = _windows.Select(w => w.ViewModel.ToData()).ToList();
        _layout.ItemFenceMap = _windows
            .SelectMany(w => w.ViewModel.Items.Select(i => (i.Id, FenceId: w.ViewModel.Id)))
            .ToDictionary(x => x.Id, x => x.FenceId, StringComparer.OrdinalIgnoreCase);
        _store.Save(_layout);
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var window in _windows)
            window.StopPersistence();

        _icons.Changed -= OnIconsChanged;

        if (_windows.Count > 0)
        {
            _layout.Fences = _windows.Select(w => w.ViewModel.ToData()).ToList();
            _layout.ItemFenceMap = _windows
                .SelectMany(w => w.ViewModel.Items.Select(i => (i.Id, FenceId: w.ViewModel.Id)))
                .ToDictionary(x => x.Id, x => x.FenceId, StringComparer.OrdinalIgnoreCase);
            _store.Save(_layout);
        }

        _disposed = true;
        _hider.Restore();

        foreach (var w in _windows.ToList())
            w.Close();
        _windows.Clear();
        _desktopLayer.Dispose();
        _icons.Dispose();
    }

    public FenceWindow? FindFenceAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return null;

        FenceWindow? hit = null;
        foreach (var window in _windows)
        {
            if (!window.IsVisible)
                continue;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                continue;

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                continue;

            if (pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom)
                hit = window;
        }

        return hit;
    }

    private void CreateWindowsFromLayout()
    {
        foreach (var data in _layout.Fences)
        {
            var vm = new FenceViewModel(data);
            foreach (var itemId in data.ItemIds)
            {
                var item = _icons.Items.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                    vm.Items.Add(item);
            }

            foreach (var (itemId, fenceId) in _layout.ItemFenceMap)
            {
                if (!fenceId.Equals(data.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (vm.Items.Any(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var item = _icons.Items.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                    vm.Items.Add(item);
            }

            AttachWindow(vm);
        }
    }

    private FenceWindow AttachWindow(FenceViewModel vm)
    {
        var window = new FenceWindow(vm, this);
        window.Closed += (_, _) =>
        {
            if (_disposed)
                return;

            var hwnd = new WindowInteropHelper(window).Handle;
            _desktopLayer.Unregister(hwnd);
            _windows.Remove(window);
            // Do not Persist here — unexpected closes must not wipe layout.json.
        };
        _windows.Add(window);
        window.Show();
        return window;
    }

    private void DistributeUnassignedItems()
    {
        if (_layout.Fences.Count == 0)
            return;

        var defaultFence = _layout.Fences[0];
        var assigned = new HashSet<string>(
            _layout.Fences.SelectMany(f => f.ItemIds)
                .Concat(_layout.ItemFenceMap.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in _icons.Items)
        {
            if (assigned.Contains(item.Id))
                continue;

            if (_layout.ItemFenceMap.TryGetValue(item.Id, out var fenceId) &&
                _layout.Fences.Any(f => f.Id == fenceId))
                continue;

            defaultFence.ItemIds.Add(item.Id);
            _layout.ItemFenceMap[item.Id] = defaultFence.Id;
        }
    }

    private void OnIconsChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _windows.Count == 0)
                return;

            var known = _icons.Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var window in _windows)
            {
                for (var i = window.ViewModel.Items.Count - 1; i >= 0; i--)
                {
                    if (!known.ContainsKey(window.ViewModel.Items[i].Id))
                        window.ViewModel.Items.RemoveAt(i);
                }
            }

            var placed = _windows.SelectMany(w => w.ViewModel.Items.Select(i => i.Id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fallback = _windows[0].ViewModel;

            foreach (var item in _icons.Items)
            {
                if (placed.Contains(item.Id))
                    continue;

                if (_layout.ItemFenceMap.TryGetValue(item.Id, out var fenceId))
                {
                    var target = _windows.FirstOrDefault(w => w.ViewModel.Id == fenceId)?.ViewModel ?? fallback;
                    target.Items.Add(item);
                }
                else
                {
                    fallback.Items.Add(item);
                    _layout.ItemFenceMap[item.Id] = fallback.Id;
                }
            }

            foreach (var window in _windows)
                window.LockSizeToViewModel();
            Persist();
        });
    }

    private static string PickColor(int index)
    {
        string[] palette =
        [
            "#66202838",
            "#661C3D5A",
            "#663D2A1C",
            "#662A3D2A",
            "#663D1C3A",
            "#661C3A3D"
        ];
        return palette[index % palette.Length];
    }
}
