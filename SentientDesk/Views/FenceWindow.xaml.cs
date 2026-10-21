using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SentientDesk.Models;
using SentientDesk.Native;
using SentientDesk.Services;
using SentientDesk.ViewModels;

namespace SentientDesk.Views;

public partial class FenceWindow : Window
{
    private readonly FenceManager _manager;
    private readonly DispatcherTimer _persistTimer;
    private Point? _dragStart;
    private DesktopItem? _dragItem;
    private bool _isDraggingIcon;
    private FrameworkElement? _dragSource;
    private DateTime _lastClickUtc;
    private DesktopItem? _lastClickItem;
    private double _expandedHeight;
    private bool _isEditingTitle;
    private bool _ignoreTitleLostFocus;
    private bool _interactionHeld;
    private bool _persistenceStopped;
    private bool _boundsLocked; // when true, ignore shell/layout size chatter
    private bool _isMovingFence;
    private bool _isResizingFence;
    private bool _gestureHasMoved;
    private bool _gridOverlayShown;
    private double _resizeDragDistance;
    private Point _moveStartCursor;
    private Point _moveStartOrigin;
    private const double GridOverlayMoveThresholdPx = 4;

    public FenceViewModel ViewModel { get; }

    public FenceWindow(FenceViewModel viewModel, FenceManager manager)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _manager = manager;
        DataContext = viewModel;
        SizeToContent = SizeToContent.Manual;
        _expandedHeight = Math.Max(viewModel.Height, 100);
        _boundsLocked = true;

        // Cap absurd sizes from older corrupt layout files (full virtual desktop).
        var (w, h) = MonitorPlacement.CapAbsurdSize(ViewModel.Width, ViewModel.Height);
        ViewModel.Width = w;
        ViewModel.Height = h;

        ApplySavedBounds();

        _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            if (!_persistenceStopped)
                _manager.Persist();
        };

        // Content reflow must never change the fence chrome size.
        ViewModel.Items.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(LockSizeToViewModel, DispatcherPriority.Loaded);

        SourceInitialized += (_, _) => AttachToDesktopLayer();
        Loaded += (_, _) =>
        {
            ApplySavedBounds();
            ApplyCollapsedState(initial: true);
            // Re-assert after WPF finishes measuring, then unlock for user edits.
            Dispatcher.BeginInvoke(() =>
            {
                ApplySavedBounds();
                _boundsLocked = false;
            }, DispatcherPriority.Loaded);
        };
        Activated += (_, _) =>
        {
            if (!_interactionHeld)
                PushBehindApps();
        };
        PreviewMouseMove += Window_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Window_PreviewMouseLeftButtonUp;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closed += (_, _) =>
        {
            CancelIconDrag();
            _isMovingFence = false;
            _isResizingFence = false;
            _gestureHasMoved = false;
            HideGridOverlayIfShown();
            ReleaseInteraction();
        };
    }

    /// <summary>
    /// ViewModel is the source of truth. Do not copy Window size back here —
    /// shell/layout noise used to inflate Width/Height and overwrite user resizes.
    /// </summary>
    public void FlushBoundsToViewModel()
    {
        // Intentionally empty for Persist/Dispose. Geometry is written only from
        // explicit user move/resize handlers via CommitUserBounds().
    }

    public void ApplySavedBounds()
    {
        var height = ViewModel.IsCollapsed ? 36 : Math.Max(100, ViewModel.Height);
        var (left, top) = MonitorPlacement.EnsureOnScreen(
            ViewModel.Left, ViewModel.Top, Math.Max(MinWidth, ViewModel.Width), height);

        // Persist corrected coords so off-screen layouts do not stick forever.
        ViewModel.Left = left;
        ViewModel.Top = top;

        Left = left;
        Top = top;
        LockSizeToViewModel();
        _expandedHeight = ViewModel.Height;
    }

    /// <summary>
    /// Force window size from ViewModel so item add/remove/reflow cannot resize the fence.
    /// </summary>
    public void LockSizeToViewModel()
    {
        Width = Math.Max(MinWidth, ViewModel.Width);
        Height = ViewModel.IsCollapsed ? 36 : Math.Max(100, ViewModel.Height);
    }

    /// <summary>
    /// Grid insertion index under the cursor for icon rearrange / cross-fence drops.
    /// </summary>
    public int InsertionIndexAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return ViewModel.Items.Count;

        try
        {
            var screen = new Point(pt.X, pt.Y);
            // Icon cells are 88 wide + ~4 margin; row height ~88 with label.
            const double cellW = 92;
            const double cellH = 88;
            var local = IconList.PointFromScreen(screen);
            var width = Math.Max(IconList.ActualWidth, 160);
            var columns = Math.Max(1, (int)(width / cellW));
            var col = Math.Clamp((int)Math.Floor(local.X / cellW), 0, columns);
            var row = Math.Max(0, (int)Math.Floor(local.Y / cellH));
            var index = col >= columns ? (row + 1) * columns : row * columns + col;
            return Math.Clamp(index, 0, ViewModel.Items.Count);
        }
        catch
        {
            return ViewModel.Items.Count;
        }
    }

    /// <summary>
    /// Folder icon under the cursor in this fence, if any (for drag-onto-folder).
    /// </summary>
    public DesktopItem? FindFolderIconAtCursor(DesktopItem? exclude = null)
    {
        if (!IsVisible)
            return null;

        try
        {
            if (!NativeMethods.GetCursorPos(out var pt))
                return null;

            var local = PointFromScreen(new Point(pt.X, pt.Y));
            var hit = VisualTreeHelper.HitTest(this, local);
            DependencyObject? current = hit?.VisualHit;
            while (current is not null)
            {
                if (current is FrameworkElement { DataContext: DesktopItem folder } &&
                    folder.IsDirectory &&
                    (exclude is null || !folder.Id.Equals(exclude.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return folder;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }
        catch
        {
            // Ignore hit-test failures across DPI / disposed visuals.
        }

        return null;
    }

    private void CommitUserBounds(bool includeSize)
    {
        if (_persistenceStopped) return;

        ViewModel.Left = Left;
        ViewModel.Top = Top;
        if (includeSize && !ViewModel.IsCollapsed)
        {
            ViewModel.Width = Math.Max(MinWidth, Width);
            ViewModel.Height = Math.Max(100, Height);
            _expandedHeight = ViewModel.Height;
        }
        SchedulePersist();
    }

    public void StopPersistence()
    {
        _persistenceStopped = true;
        _persistTimer.Stop();
    }

    private void SchedulePersist()
    {
        if (_persistenceStopped) return;
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void AttachToDesktopLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _manager.DesktopLayer.Register(hwnd);
        ApplySavedBounds();
    }

    private void PushBehindApps()
    {
        if (_persistenceStopped) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        _manager.DesktopLayer.KeepBehind(hwnd);
    }

    private void HoldInteraction()
    {
        if (_interactionHeld) return;
        _interactionHeld = true;
        _manager.DesktopLayer.BeginInteraction();
    }

    private void ReleaseInteraction()
    {
        if (!_interactionHeld) return;
        _interactionHeld = false;
        _manager.DesktopLayer.EndInteraction();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isEditingTitle) return;
        if (e.OriginalSource is TextBox) return;

        if (e.ClickCount == 2)
        {
            if (e.OriginalSource is not TextBlock)
            {
                ToggleCollapse();
                e.Handled = true;
            }
            return;
        }

        // Manual move — DragMove() can alter transparent window size on some DPI setups.
        _isMovingFence = true;
        _gestureHasMoved = false;
        // PointToScreen returns pixels; Left/Top are DIPs.
        _moveStartCursor = MonitorPlacement.ToDip(PointToScreen(e.GetPosition(this)));
        _moveStartOrigin = new Point(Left, Top);
        HoldInteraction();
        CaptureMouse();
        PreviewMouseMove += FenceMove_PreviewMouseMove;
        PreviewMouseLeftButtonUp += FenceMove_PreviewMouseLeftButtonUp;
        e.Handled = true;
    }

    private void FenceMove_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMovingFence || e.LeftButton != MouseButtonState.Pressed)
            return;

        var cursor = MonitorPlacement.ToDip(PointToScreen(e.GetPosition(this)));
        var dx = cursor.X - _moveStartCursor.X;
        var dy = cursor.Y - _moveStartCursor.Y;
        Left = _moveStartOrigin.X + dx;
        Top = _moveStartOrigin.Y + dy;
        ApplyMoveSnap();
        MaybeArmGridOverlay(Math.Abs(dx) + Math.Abs(dy));
        LockSizeToViewModel();
        e.Handled = true;
    }

    private void FenceMove_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMovingFence)
            return;

        _isMovingFence = false;
        _gestureHasMoved = false;
        ReleaseMouseCapture();
        PreviewMouseMove -= FenceMove_PreviewMouseMove;
        PreviewMouseLeftButtonUp -= FenceMove_PreviewMouseLeftButtonUp;
        ApplyMoveSnap();
        CommitUserBounds(includeSize: false);
        LockSizeToViewModel();
        HideGridOverlayIfShown();
        ReleaseInteraction();
        PushBehindApps();
        e.Handled = true;
    }

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            BeginRename();
        }
    }

    private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowFenceMenu((UIElement)sender);
        e.Handled = true;
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void Menu_Click(object sender, RoutedEventArgs e) => ShowFenceMenu((UIElement)sender);

    private void ToggleCollapse()
    {
        if (!ViewModel.IsCollapsed)
            _expandedHeight = ViewModel.Height;

        ViewModel.IsCollapsed = !ViewModel.IsCollapsed;
        ApplyCollapsedState(initial: false);
        _manager.Persist();
    }

    private void ApplyCollapsedState(bool initial)
    {
        if (ViewModel.IsCollapsed)
        {
            ContentHost.Visibility = Visibility.Collapsed;
            ResizeThumb.Visibility = Visibility.Collapsed;
            Height = 36;
            MinHeight = 36;
        }
        else
        {
            ContentHost.Visibility = Visibility.Visible;
            ResizeThumb.Visibility = Visibility.Visible;
            MinHeight = 100;
            Height = Math.Max(_expandedHeight, 100);
            Width = Math.Max(MinWidth, ViewModel.Width);
        }

        if (!initial)
            _manager.Persist();
    }

    private void BeginRename()
    {
        HoldInteraction();
        _isEditingTitle = true;
        _ignoreTitleLostFocus = true;

        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Text = ViewModel.Title;

        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            TitleEdit.Focus();
            Keyboard.Focus(TitleEdit);
            TitleEdit.SelectAll();
            _ignoreTitleLostFocus = false;
        }, DispatcherPriority.Input);
    }

    private void CommitRename(bool save)
    {
        if (!_isEditingTitle) return;

        if (save)
        {
            var text = TitleEdit.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                ViewModel.Title = text;
        }

        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        _isEditingTitle = false;
        _manager.Persist();
        ReleaseInteraction();
        PushBehindApps();
    }

    private void TitleEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_ignoreTitleLostFocus || !_isEditingTitle)
            return;

        if (e.NewFocus is DependencyObject next && IsAncestorOf(next))
            return;

        CommitRename(save: true);
    }

    private void TitleEdit_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(save: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitRename(save: false);
            e.Handled = true;
        }
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (ViewModel.IsCollapsed || _boundsLocked) return;
        _isResizingFence = true;
        _gestureHasMoved = false;
        _resizeDragDistance = 0;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (ViewModel.IsCollapsed || _boundsLocked) return;

        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(100, Height + e.VerticalChange);
        ApplyResizeSnap();
        _resizeDragDistance += Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange);
        MaybeArmGridOverlay(_resizeDragDistance);
        ViewModel.Width = Width;
        ViewModel.Height = Height;
        _expandedHeight = Height;
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel.IsCollapsed || _boundsLocked) return;
        _isResizingFence = false;
        _gestureHasMoved = false;
        _resizeDragDistance = 0;
        ApplyResizeSnap();
        ViewModel.Width = Width;
        ViewModel.Height = Height;
        _expandedHeight = Height;
        CommitUserBounds(includeSize: true);
        HideGridOverlayIfShown();
        _manager.Persist();
    }

    private bool IsSnapActive =>
        _manager.Layout.SnapToGrid && (Keyboard.Modifiers & ModifierKeys.Alt) == 0;

    private void MaybeArmGridOverlay(double travelPx)
    {
        if (!_gestureHasMoved)
        {
            if (travelPx < GridOverlayMoveThresholdPx)
                return;
            _gestureHasMoved = true;
        }

        UpdateGridOverlayForGesture();
    }

    private void UpdateGridOverlayForGesture()
    {
        var shouldShow = _gestureHasMoved && IsSnapActive && (_isMovingFence || _isResizingFence);
        if (shouldShow == _gridOverlayShown)
            return;

        if (shouldShow)
        {
            _manager.ShowGridOverlay();
            _gridOverlayShown = true;
        }
        else
        {
            HideGridOverlayIfShown();
        }
    }

    private void HideGridOverlayIfShown()
    {
        if (!_gridOverlayShown)
            return;
        _manager.HideGridOverlay();
        _gridOverlayShown = false;
    }

    private void ApplyMoveSnap()
    {
        if (!IsSnapActive)
            return;

        var workOrigin = MonitorPlacement.WorkAreaOriginForPoint(new Point(Left, Top));
        var snapped = SnapMath.SnapPoint(Left, Top, workOrigin);
        Left = snapped.X;
        Top = snapped.Y;
    }

    private void ApplyResizeSnap()
    {
        if (!IsSnapActive)
            return;

        var workOrigin = MonitorPlacement.WorkAreaOriginForPoint(new Point(Left, Top));
        var size = SnapMath.SnapSize(Left, Top, Width, Height, workOrigin, MinWidth, 100);
        Width = size.Width;
        Height = size.Height;
    }

    private void ShowFenceMenu(UIElement placementTarget)
    {
        HoldInteraction();

        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) =>
            Dispatcher.BeginInvoke(BeginRename, DispatcherPriority.ApplicationIdle);
        menu.Items.Add(rename);

        var collapse = new MenuItem { Header = ViewModel.IsCollapsed ? "Expand" : "Collapse" };
        collapse.Click += (_, _) => ToggleCollapse();
        menu.Items.Add(collapse);

        var newFolder = new MenuItem { Header = "New folder…" };
        newFolder.Click += (_, _) =>
            Dispatcher.BeginInvoke(PromptCreateFolder, DispatcherPriority.ApplicationIdle);
        menu.Items.Add(newFolder);

        menu.Items.Add(new Separator());

        var colors = new (string Name, string Hex)[]
        {
            ("Slate", "#66202838"),
            ("Ocean", "#661C3D5A"),
            ("Earth", "#663D2A1C"),
            ("Forest", "#662A3D2A"),
            ("Plum", "#663D1C3A"),
            ("Teal", "#661C3A3D")
        };

        var colorRoot = new MenuItem { Header = "Color" };
        foreach (var (name, hex) in colors)
        {
            var item = new MenuItem { Header = name };
            var captured = hex;
            item.Click += (_, _) =>
            {
                ViewModel.ColorHex = captured;
                ViewModel.Background = FenceViewModel.ParseBrush(captured);
                _manager.Persist();
            };
            colorRoot.Items.Add(item);
        }
        menu.Items.Add(colorRoot);

        menu.Items.Add(new Separator());

        var setDefault = new MenuItem
        {
            Header = "Set as default for new icons",
            IsChecked = _manager.IsDefaultFence(this)
        };
        setDefault.Click += (_, _) => _manager.SetDefaultFence(this);
        menu.Items.Add(setDefault);

        menu.Items.Add(new Separator());

        var remove = new MenuItem
        {
            Header = _manager.CanRemoveFence ? "Remove fence" : "Remove fence (last fence)",
            IsEnabled = _manager.CanRemoveFence
        };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show($"Remove fence \"{ViewModel.Title}\"? Icons move to another fence.",
                    "SentientDesk", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _manager.RemoveFence(this);
            }
        };
        menu.Items.Add(remove);

        menu.Closed += (_, _) =>
        {
            if (!_isEditingTitle)
            {
                ReleaseInteraction();
                PushBehindApps();
            }
        };

        menu.IsOpen = true;
    }

    private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item } element)
            return;

        _dragItem = item;
        _dragSource = element;
        _dragStart = e.GetPosition(this);
        _isDraggingIcon = false;
        element.CaptureMouse();
        e.Handled = true;
    }

    private void Icon_MouseMove(object sender, MouseEventArgs e) => TryContinueIconDrag(e);

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is not null)
            TryContinueIconDrag(e);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem is not null)
            FinishIconPointerUp(e);
    }

    private void TryContinueIconDrag(MouseEventArgs e)
    {
        if (_dragItem is null || _dragStart is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(this);
        if (!_isDraggingIcon)
        {
            if (Math.Abs(pos.X - _dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isDraggingIcon = true;
            HoldInteraction();
            Mouse.OverrideCursor = Cursors.SizeAll;
            if (_dragSource is not null)
                _dragSource.Opacity = 0.45;
        }
    }

    private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FinishIconPointerUp(e);

    private void FinishIconPointerUp(MouseButtonEventArgs e)
    {
        if (_dragSource is { IsMouseCaptured: true })
            _dragSource.ReleaseMouseCapture();

        if (_dragItem is null)
            return;

        var item = _dragItem;
        var wasDragging = _isDraggingIcon;
        CancelIconDrag();

        if (wasDragging)
        {
            var target = _manager.FindFenceAtCursor() ?? this;
            var folder = target.FindFolderIconAtCursor(exclude: item);
            if (folder is not null)
            {
                _manager.MoveIntoFolder(item, folder);
            }
            else
            {
                var index = target.InsertionIndexAtCursor();
                _manager.MoveItem(item, ViewModel, target.ViewModel, index);
            }

            ReleaseInteraction();
            PushBehindApps();
            e.Handled = true;
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastClickItem?.Id == item.Id && (now - _lastClickUtc).TotalMilliseconds < 400)
        {
            DesktopIconService.Launch(item);
            _lastClickItem = null;
        }
        else
        {
            _lastClickItem = item;
            _lastClickUtc = now;
            Focus();
            Keyboard.Focus(this);
        }

        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _lastClickItem is null)
            return;

        if (!ViewModel.Items.Contains(_lastClickItem))
            return;

        var item = _lastClickItem;
        _lastClickItem = null;
        _manager.RemoveItem(item, ViewModel);
        e.Handled = true;
    }

    private void Icon_MouseLeave(object sender, MouseEventArgs e)
    {
        // Keep drag alive while captured; ignore leave.
    }

    private void Icon_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item } element)
            return;

        CancelIconDrag();
        ShowIconMenu(item, element);
        e.Handled = true;
    }

    private void CancelIconDrag()
    {
        if (_dragSource is not null)
        {
            if (_dragSource.IsMouseCaptured)
                _dragSource.ReleaseMouseCapture();
            _dragSource.Opacity = 1;
        }

        _dragItem = null;
        _dragStart = null;
        _dragSource = null;
        _isDraggingIcon = false;
        Mouse.OverrideCursor = null;
    }

    private void ShowIconMenu(DesktopItem item, UIElement placementTarget)
    {
        HoldInteraction();
        _lastClickItem = item;
        Focus();
        Keyboard.Focus(this);

        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.MousePoint
        };

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => DesktopIconService.Launch(item);
        menu.Items.Add(open);

        menu.Items.Add(new Separator());

        var cut = new MenuItem { Header = "Cut" };
        cut.Click += (_, _) => ShellFileOps.CutToClipboard(item);
        menu.Items.Add(cut);

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => ShellFileOps.CopyToClipboard(item);
        menu.Items.Add(copy);

        menu.Items.Add(new Separator());

        var removeFromFence = new MenuItem
        {
            Header = "Remove from fence",
            InputGestureText = "Del",
            ToolTip = "Hides the icon from SentientDesk; the file stays on disk. Restore via tray → Hidden icons."
        };
        removeFromFence.Click += (_, _) =>
        {
            _lastClickItem = null;
            _manager.RemoveItem(item, ViewModel);
        };
        menu.Items.Add(removeFromFence);

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) =>
            Dispatcher.BeginInvoke(() => BeginItemRename(item), DispatcherPriority.ApplicationIdle);
        menu.Items.Add(rename);

        var properties = new MenuItem { Header = "Properties" };
        properties.Click += (_, _) => ShellFileOps.ShowProperties(item);
        menu.Items.Add(properties);

        menu.Items.Add(new Separator());

        var newFolderWithSelection = new MenuItem { Header = "New folder with selection…" };
        newFolderWithSelection.Click += (_, _) =>
            Dispatcher.BeginInvoke(() => PromptCreateFolderWithSelection(item), DispatcherPriority.ApplicationIdle);
        menu.Items.Add(newFolderWithSelection);

        var moveIntoRoot = new MenuItem { Header = "Move into" };
        var folders = ViewModel.Items
            .Where(i => i.IsDirectory && !i.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 0)
        {
            moveIntoRoot.Items.Add(new MenuItem { Header = "(no folders in this fence)", IsEnabled = false });
        }
        else
        {
            foreach (var folder in folders)
            {
                var target = folder;
                var entry = new MenuItem { Header = target.DisplayName };
                entry.Click += (_, _) => _manager.MoveIntoFolder(item, target);
                moveIntoRoot.Items.Add(entry);
            }
        }
        menu.Items.Add(moveIntoRoot);

        var moveRoot = new MenuItem { Header = "Move to" };
        foreach (var fence in _manager.Windows)
        {
            if (ReferenceEquals(fence, this))
                continue;

            var target = fence;
            var entry = new MenuItem { Header = target.ViewModel.Title };
            entry.Click += (_, _) => _manager.MoveItem(item, ViewModel, target.ViewModel);
            moveRoot.Items.Add(entry);
        }

        if (moveRoot.Items.Count == 0)
            moveRoot.Items.Add(new MenuItem { Header = "(no other fences)", IsEnabled = false });

        menu.Items.Add(moveRoot);

        menu.Closed += (_, _) =>
        {
            ReleaseInteraction();
            PushBehindApps();
        };

        menu.IsOpen = true;
    }

    private void PromptCreateFolder()
    {
        HoldInteraction();
        try
        {
            var suggested = ShellFileOps.UniqueDesktopFolderName();
            var name = RenamePrompt.Ask("New folder", "Folder name:", suggested);
            if (string.IsNullOrWhiteSpace(name))
                return;
            _manager.CreateFolderInFence(ViewModel, name.Trim());
        }
        finally
        {
            ReleaseInteraction();
            PushBehindApps();
        }
    }

    private void PromptCreateFolderWithSelection(DesktopItem item)
    {
        HoldInteraction();
        try
        {
            var suggested = ShellFileOps.UniqueDesktopFolderName();
            var name = RenamePrompt.Ask("New folder", "Folder name:", suggested);
            if (string.IsNullOrWhiteSpace(name))
                return;
            _manager.CreateFolderWithItems(ViewModel, name.Trim(), [item]);
        }
        finally
        {
            ReleaseInteraction();
            PushBehindApps();
        }
    }

    private void BeginItemRename(DesktopItem item)
    {
        HoldInteraction();
        try
        {
            var proposed = RenamePrompt.Ask("Rename", "New name:", item.DisplayName);
            if (proposed is null)
                return;

            _manager.TryRenameItem(item, proposed);
        }
        finally
        {
            ReleaseInteraction();
            PushBehindApps();
        }
    }
}
