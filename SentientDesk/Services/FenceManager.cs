using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
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
    private GridOverlayWindow? _gridOverlay;
    private bool _disposed;

    public FenceManager(LayoutStore store, DesktopIconService icons, DesktopIconHider hider)
    {
        _store = store;
        _icons = icons;
        _hider = hider;
        _layout = store.Load();
        _icons.Changed += OnIconsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public AppLayout Layout => _layout;
    public IReadOnlyList<FenceWindow> Windows => _windows;
    public DesktopLayerService DesktopLayer => _desktopLayer;

    public void Start()
    {
        if (_layout.Fences.Count == 0)
            _layout = _store.Load();

        EnsureDefaultFenceId();
        DistributeUnassignedItems();
        CreateWindowsFromLayout();
        _hider.SetHidden(_layout.HideDesktopIcons);
        StartupService.SetEnabled(_layout.StartWithWindows);

        // Persist item assignments only — do not rewrite geometry from live window sizes
        // (startup layout can briefly inflate and would clobber user-resized fences).
        Persist();
    }

    public FenceWindow CreateFence(string? title = null, Point? origin = null) =>
        CreateFence(FenceTemplate.Blank, origin, title);

    public FenceWindow CreateFence(FenceTemplate template, Point? origin = null, string? titleOverride = null)
    {
        var width = template.Width;
        var height = template.Height;
        var place = origin ?? MonitorPlacement.OriginForNewFence(width, height, _windows.Count);

        if (_layout.SnapToGrid)
        {
            var workOrigin = MonitorPlacement.WorkAreaOriginForPoint(place);
            place = SnapMath.SnapPoint(place.X, place.Y, workOrigin);
        }

        var data = new FenceData
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = titleOverride ?? template.Title ?? $"Fence {_windows.Count + 1}",
            Left = place.X,
            Top = place.Y,
            Width = width,
            Height = height,
            Color = PickColor(_windows.Count)
        };

        _layout.Fences.Add(data);
        if (string.IsNullOrWhiteSpace(_layout.DefaultFenceId))
            _layout.DefaultFenceId = data.Id;

        var vm = new FenceViewModel(data);
        var window = AttachWindow(vm);
        Persist();
        return window;
    }

    public void SetDefaultFence(FenceWindow window)
    {
        _layout.DefaultFenceId = window.ViewModel.Id;
        Persist();
    }

    public bool IsDefaultFence(FenceWindow window) =>
        !string.IsNullOrWhiteSpace(_layout.DefaultFenceId) &&
        window.ViewModel.Id.Equals(_layout.DefaultFenceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The last fence cannot be removed: with desktop icons hidden its items would have
    /// nowhere to show, and the layout store refuses to persist an empty fence list.
    /// </summary>
    public bool CanRemoveFence => _windows.Count > 1;

    public void RemoveFence(FenceWindow window)
    {
        if (!CanRemoveFence)
            return;

        var orphanIds = window.ViewModel.Items.Select(i => i.Id).ToList();
        var removedId = window.ViewModel.Id;
        window.ViewModel.Items.Clear();

        _windows.Remove(window);
        _layout.Fences.RemoveAll(f => f.Id == window.ViewModel.Id);
        window.Close();

        if (_windows.Count > 0 && orphanIds.Count > 0)
        {
            var target = ResolveDefaultViewModel() ?? _windows[0].ViewModel;
            foreach (var id in orphanIds)
            {
                if (IsHidden(id))
                    continue;

                var item = _icons.Items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (item is null) continue;
                if (target.Items.All(x => !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    target.Items.Add(item);
                _layout.ItemFenceMap[id] = target.Id;
            }
        }
        else
        {
            foreach (var id in orphanIds)
                _layout.ItemFenceMap.Remove(id);
        }

        if (removedId.Equals(_layout.DefaultFenceId, StringComparison.OrdinalIgnoreCase))
            _layout.DefaultFenceId = _windows.Count > 0 ? _windows[0].ViewModel.Id : null;

        Persist();
    }

    /// <summary>
    /// Removes an icon from its fence without deleting the on-disk file.
    /// The item is marked hidden so refresh / new-icon sync will not reassign it.
    /// </summary>
    public void RemoveItem(DesktopItem item, FenceViewModel from)
    {
        var index = from.Items.IndexOf(item);
        if (index >= 0)
            from.Items.RemoveAt(index);

        _layout.ItemFenceMap.Remove(item.Id);
        MarkHidden(item.Id);

        foreach (var window in _windows)
            window.LockSizeToViewModel();
        Persist();
    }

    /// <summary>
    /// Desktop items removed from fences that still exist on disk.
    /// </summary>
    public IReadOnlyList<DesktopItem> HiddenItems()
    {
        var hidden = HiddenSet();
        return _icons.Items.Where(i => hidden.Contains(i.Id)).ToList();
    }

    /// <summary>
    /// Puts previously removed icons back into the default fence.
    /// </summary>
    public void RestoreHiddenItems(IEnumerable<DesktopItem> items)
    {
        var target = ResolveDefaultViewModel();
        if (target is null)
            return;

        foreach (var item in items)
            PlaceItemInFence(item, target);
        Persist();
    }

    public void MoveItem(DesktopItem item, FenceViewModel from, FenceViewModel to, int? insertIndex = null)
    {
        var fromIndex = from.Items.IndexOf(item);
        if (fromIndex < 0)
        {
            // Allow restoring a dismissed item if it somehow is moved into a fence.
            if (!ReferenceEquals(from, to) && to.Items.All(x => !x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var index = insertIndex ?? to.Items.Count;
                index = Math.Clamp(index, 0, to.Items.Count);
                to.Items.Insert(index, item);
                _layout.ItemFenceMap[item.Id] = to.Id;
                ClearHidden(item.Id);
                foreach (var window in _windows)
                    window.LockSizeToViewModel();
                Persist();
            }
            return;
        }

        from.Items.RemoveAt(fromIndex);

        var destIndex = insertIndex ?? to.Items.Count;
        if (ReferenceEquals(from, to) && destIndex > fromIndex)
            destIndex--;
        destIndex = Math.Clamp(destIndex, 0, to.Items.Count);

        to.Items.Insert(destIndex, item);
        _layout.ItemFenceMap[item.Id] = to.Id;
        ClearHidden(item.Id);

        // Item reflow must not change fence geometry.
        foreach (var window in _windows)
            window.LockSizeToViewModel();
        Persist();
    }

    public bool TryRenameItem(DesktopItem item, string newBaseName)
    {
        var oldId = item.Id;
        var newPath = ShellFileOps.TryRename(item, newBaseName);
        if (newPath is null)
            return false;

        if (newPath.Equals(oldId, StringComparison.OrdinalIgnoreCase))
            return true;

        var wasHidden = IsHidden(oldId);
        var fenceId = _layout.ItemFenceMap.TryGetValue(oldId, out var mapped) ? mapped : null;

        item.Id = newPath;
        item.Path = newPath;
        item.DisplayName = ShellFileOps.DisplayNameForPath(newPath, item.IsDirectory);
        item.Icon = DesktopIconService.GetIcon(newPath, item.IsDirectory);

        _layout.ItemFenceMap.Remove(oldId);
        if (fenceId is not null)
            _layout.ItemFenceMap[newPath] = fenceId;

        ClearHidden(oldId);
        if (wasHidden)
            MarkHidden(newPath);

        // Keep fence ItemIds lists consistent via Persist rebuild.
        Persist();
        return true;
    }

    /// <summary>
    /// Creates a folder on the user Desktop and places it in <paramref name="fence"/>.
    /// Returns the new item, or null on cancel/failure.
    /// </summary>
    public DesktopItem? CreateFolderInFence(FenceViewModel fence, string name)
    {
        var path = ShellFileOps.TryCreateDesktopFolder(name);
        if (path is null)
            return null;

        _layout.ItemFenceMap[path] = fence.Id;
        ClearHidden(path);
        _icons.Refresh();

        var item = _icons.Items.FirstOrDefault(i => i.Id.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return null;

        PlaceItemInFence(item, fence);
        Persist();
        return item;
    }

    /// <summary>
    /// Creates a Desktop folder in <paramref name="fence"/> and moves <paramref name="items"/> into it.
    /// </summary>
    public DesktopItem? CreateFolderWithItems(FenceViewModel fence, string name, IReadOnlyList<DesktopItem> items)
    {
        var folderItem = CreateFolderInFence(fence, name);
        if (folderItem is null)
            return null;

        var movedAny = false;
        foreach (var item in items)
        {
            if (item.Id.Equals(folderItem.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ShellFileOps.TryMoveIntoFolder(item, folderItem))
                movedAny = true;
        }

        if (movedAny)
            _icons.Refresh();

        return folderItem;
    }

    /// <summary>
    /// Moves a Desktop item into a folder on disk; fences update via icon refresh.
    /// </summary>
    public bool MoveIntoFolder(DesktopItem item, DesktopItem folder)
    {
        if (!ShellFileOps.TryMoveIntoFolder(item, folder))
            return false;

        _layout.ItemFenceMap.Remove(item.Id);
        _icons.Refresh();
        return true;
    }

    private void PlaceItemInFence(DesktopItem item, FenceViewModel fence)
    {
        foreach (var window in _windows)
        {
            var list = window.ViewModel.Items;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
                    !ReferenceEquals(window.ViewModel, fence))
                    list.RemoveAt(i);
            }
        }

        if (fence.Items.All(x => !x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
            fence.Items.Add(item);

        _layout.ItemFenceMap[item.Id] = fence.Id;
        ClearHidden(item.Id);

        foreach (var window in _windows)
            window.LockSizeToViewModel();
    }

    public void SetHideDesktopIcons(bool hide)
    {
        _layout.HideDesktopIcons = hide;
        _hider.SetHidden(hide);
        Persist();
    }

    /// <summary>
    /// explorer.exe recreates the desktop ListView (visible) and may restack windows on restart.
    /// </summary>
    public void ReapplyShellState()
    {
        if (_disposed)
            return;

        _hider.SetHidden(_layout.HideDesktopIcons);
        _desktopLayer.SinkAll();
    }

    public void SetStartWithWindows(bool enabled)
    {
        _layout.StartWithWindows = enabled;
        StartupService.SetEnabled(enabled);
        Persist();
    }

    public void SetSnapToGrid(bool enabled)
    {
        _layout.SnapToGrid = enabled;
        if (!enabled)
            HideGridOverlay();
        Persist();
    }

    /// <summary>
    /// Show the snap grid overlay while a fence is being moved or resized (snap must be on).
    /// </summary>
    public void ShowGridOverlay()
    {
        if (_disposed || !_layout.SnapToGrid)
            return;

        EnsureGridOverlay();
        _gridOverlay!.FitVirtualScreen();
        if (!_gridOverlay.IsVisible)
            _gridOverlay.Show();
    }

    public void HideGridOverlay()
    {
        if (_gridOverlay is null)
            return;
        _gridOverlay.Hide();
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
        // HiddenItemIds and DefaultFenceId are preserved on _layout as-is.
        EnsureDefaultFenceId();
        _store.Save(_layout);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;

            var moved = false;
            foreach (var window in _windows)
            {
                var beforeLeft = window.ViewModel.Left;
                var beforeTop = window.ViewModel.Top;
                window.ApplySavedBounds();
                if (Math.Abs(beforeLeft - window.ViewModel.Left) > 0.1 ||
                    Math.Abs(beforeTop - window.ViewModel.Top) > 0.1)
                    moved = true;
            }

            if (moved)
                Persist();

            if (_gridOverlay is not null && _gridOverlay.IsVisible)
                _gridOverlay.FitVirtualScreen();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var window in _windows)
            window.StopPersistence();

        _icons.Changed -= OnIconsChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_windows.Count > 0)
        {
            _layout.Fences = _windows.Select(w => w.ViewModel.ToData()).ToList();
            _layout.ItemFenceMap = _windows
                .SelectMany(w => w.ViewModel.Items.Select(i => (i.Id, FenceId: w.ViewModel.Id)))
                .ToDictionary(x => x.Id, x => x.FenceId, StringComparer.OrdinalIgnoreCase);
            EnsureDefaultFenceId();
            _store.Save(_layout);
        }

        _disposed = true;
        _hider.Restore();
        DesktopIconHider.ForceShowDesktopIcons();

        if (_gridOverlay is not null)
        {
            var overlayHwnd = new WindowInteropHelper(_gridOverlay).Handle;
            if (overlayHwnd != IntPtr.Zero)
                _desktopLayer.Unregister(overlayHwnd);
            _gridOverlay.Close();
            _gridOverlay = null;
        }

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
        var hidden = HiddenSet();

        foreach (var data in _layout.Fences)
        {
            var vm = new FenceViewModel(data);
            foreach (var itemId in data.ItemIds)
            {
                if (hidden.Contains(itemId))
                    continue;
                var item = _icons.Items.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                    vm.Items.Add(item);
            }

            foreach (var (itemId, fenceId) in _layout.ItemFenceMap)
            {
                if (!fenceId.Equals(data.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (hidden.Contains(itemId))
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

    private void EnsureGridOverlay()
    {
        if (_gridOverlay is not null)
            return;

        _gridOverlay = new GridOverlayWindow();
        _gridOverlay.SourceInitialized += (_, _) =>
        {
            if (_disposed || _gridOverlay is null)
                return;
            var hwnd = new WindowInteropHelper(_gridOverlay).Handle;
            _desktopLayer.RegisterClickThrough(hwnd);
        };
    }

    private void DistributeUnassignedItems()
    {
        if (_layout.Fences.Count == 0)
            return;

        EnsureDefaultFenceId();
        var defaultFence = ResolveDefaultFenceData() ?? _layout.Fences[0];
        var hidden = HiddenSet();
        var assigned = new HashSet<string>(
            _layout.Fences.SelectMany(f => f.ItemIds)
                .Concat(_layout.ItemFenceMap.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in _icons.Items)
        {
            if (hidden.Contains(item.Id))
                continue;

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
            var hidden = HiddenSet();

            // Drop dismissals for paths that no longer exist on the desktop.
            if (_layout.HiddenItemIds is { Count: > 0 })
            {
                _layout.HiddenItemIds.RemoveAll(id => !known.ContainsKey(id));
                hidden = HiddenSet();
            }

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

            var fallback = ResolveDefaultViewModel() ?? _windows[0].ViewModel;

            foreach (var item in _icons.Items)
            {
                if (hidden.Contains(item.Id))
                    continue;

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

    private void EnsureDefaultFenceId()
    {
        if (_layout.Fences.Count == 0)
        {
            _layout.DefaultFenceId = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(_layout.DefaultFenceId) ||
            _layout.Fences.All(f => !f.Id.Equals(_layout.DefaultFenceId, StringComparison.OrdinalIgnoreCase)))
        {
            _layout.DefaultFenceId = _layout.Fences[0].Id;
        }
    }

    private FenceData? ResolveDefaultFenceData()
    {
        EnsureDefaultFenceId();
        if (string.IsNullOrWhiteSpace(_layout.DefaultFenceId))
            return _layout.Fences.FirstOrDefault();

        return _layout.Fences.FirstOrDefault(f =>
                   f.Id.Equals(_layout.DefaultFenceId, StringComparison.OrdinalIgnoreCase))
               ?? _layout.Fences.FirstOrDefault();
    }

    private FenceViewModel? ResolveDefaultViewModel()
    {
        EnsureDefaultFenceId();
        if (_windows.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(_layout.DefaultFenceId))
        {
            var match = _windows.FirstOrDefault(w =>
                w.ViewModel.Id.Equals(_layout.DefaultFenceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.ViewModel;
        }

        return _windows[0].ViewModel;
    }

    private HashSet<string> HiddenSet() =>
        new(_layout.HiddenItemIds ?? [], StringComparer.OrdinalIgnoreCase);

    private bool IsHidden(string itemId) =>
        _layout.HiddenItemIds.Any(id => id.Equals(itemId, StringComparison.OrdinalIgnoreCase));

    private void MarkHidden(string itemId)
    {
        _layout.HiddenItemIds ??= [];
        if (!_layout.HiddenItemIds.Any(id => id.Equals(itemId, StringComparison.OrdinalIgnoreCase)))
            _layout.HiddenItemIds.Add(itemId);
    }

    private void ClearHidden(string itemId)
    {
        if (_layout.HiddenItemIds is null || _layout.HiddenItemIds.Count == 0)
            return;

        _layout.HiddenItemIds.RemoveAll(id => id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
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
