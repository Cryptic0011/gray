using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Gmux.Core.Models;
using Gmux.Core.Services;
using Gmux.App.Services;
using Gmux.App.ViewModels;
using WinRT.Interop;

namespace Gmux.App;

public sealed partial class MainWindow : Window
{
    private readonly SessionManager _sessionManager;
    private UpdateBannerViewModel? _updateBannerViewModel;
    private readonly PaneFocusManager _focusManager = new();
    private AppWindow _appWindow = null!;
    private DispatcherTimer? _stateSaveTimer;
    private DispatcherTimer? _notificationDismissTimer;
    private DispatcherTimer? _waitingNotificationDelayTimer;
    private DispatcherTimer? _notificationClearGraceTimer;
    private string? _lastWaitingNotificationSignature;
    private string? _pendingWaitingNotificationSignature;
    private string? _pendingWaitingNotificationTitle;
    private string? _pendingWaitingNotificationMessage;
    private bool _isLoadingActiveTab;
    private bool _pendingActiveTabLoad;
    private static readonly TimeSpan WaitingNotificationDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan NotificationClearGrace = TimeSpan.FromSeconds(3);

    public MainWindow()
    {
        InitializeComponent();

        _sessionManager = new SessionManager(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            App.SettingsManager);
        _sessionManager.SetAgentMonitor(App.AgentMonitor);

        // Store on App for IPC access
        App.SessionManager = _sessionManager;

        // Customize window – remove native title bar, use custom drag region
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion); // Drag region is behind buttons so they get full input
        Title = "gray";

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

        // Fully remove native caption buttons (keep border for resizing)
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
        }

        // Subscribe to model events
        App.WorkspaceManager.WorkspacesChanged += OnWorkspacesChanged;
        App.WorkspaceManager.ActiveTabChanged += OnActiveTabChanged;
        App.WorkspaceManager.SplitTreeChanged += OnSplitTreeChanged;
        App.SettingsManager.SettingsChanged += OnSettingsChanged;

        // Subscribe to UI events
        PaneContainer.PaneFocused += OnPaneFocused;
        PaneContainer.SplitVerticalRequested += OnContextSplitVertical;
        PaneContainer.SplitHorizontalRequested += OnContextSplitHorizontal;
        PaneContainer.ClosePaneRequested += OnContextClosePane;
        PaneContainer.NewTabRequested += OnContextNewTab;
        PaneContainer.ChangeDirectoryRequested += OnContextChangeDirectory;
        PaneContainer.InitialDirectorySelectionRequested += OnInitialDirectorySelectionRequested;
        PaneContainer.RefreshRequested += () => DispatcherQueue.TryEnqueue(RequestActiveTabLoad);
        _focusManager.FocusChanged += OnFocusChanged;

        // Agent monitor — refresh UI when waiting state changes
        App.AgentMonitor.StateChanged += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshTabBar();
                PaneContainer.UpdateWaitingIndicators();
            });

        // Notification toasts
        App.NotificationService.OnNotification += notification =>
            DispatcherQueue.TryEnqueue(() => ShowNotification(notification));

        // Agent monitor: show toast when a tracked agent finishes working
        App.AgentMonitor.StateChanged += () =>
            DispatcherQueue.TryEnqueue(CheckAgentWaitingNotifications);

        // Persist workspace state on window close
        this.Closed += async (s, e) =>
        {
            _stateSaveTimer?.Stop();
            await App.WorkspaceManager.SaveStateAsync();
            _sessionManager.Dispose();
        };

        // Keyboard shortcuts
        Content.KeyDown += OnGlobalKeyDown;
        Content.PreviewKeyDown += OnPreviewKeyDown;

        _updateBannerViewModel = new UpdateBannerViewModel(
            App.UpdateChecker,
            App.UpdateDownloader,
            App.UpdateInstaller,
            App.SettingsManager,
            dispatch: action =>
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    action();
                    return Task.CompletedTask;
                }
                var tcs = new TaskCompletionSource();
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { action(); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            exitAction: () => Microsoft.UI.Xaml.Application.Current.Exit());
        UpdateBannerControl.ViewModel = _updateBannerViewModel;

        Closed += OnClosed;

        _ = _updateBannerViewModel.InitializeAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _updateBannerViewModel?.Dispose();
    }

    // --- Context Menu Handlers ---

    private void OnContextSplitVertical(Guid paneId)
    {
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace == null || tab == null) return;
        var newId = App.WorkspaceManager.SplitPane(workspace.Id, tab.Id, paneId, SplitDirection.Vertical);
        if (newId.HasValue)
        {
            // Inherit working directory from the source pane
            var sourceDir = _sessionManager.GetWorkingDirectory(paneId) ?? workspace.WorkingDirectory;
            _sessionManager.ConfigurePendingPane(newId.Value, sourceDir, App.SettingsManager.Current.GetEnabledLaunchOptions());
            _focusManager.SetFocus(newId.Value);
        }
    }

    private void OnContextSplitHorizontal(Guid paneId)
    {
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace == null || tab == null) return;
        var newId = App.WorkspaceManager.SplitPane(workspace.Id, tab.Id, paneId, SplitDirection.Horizontal);
        if (newId.HasValue)
        {
            var sourceDir = _sessionManager.GetWorkingDirectory(paneId) ?? workspace.WorkingDirectory;
            _sessionManager.ConfigurePendingPane(newId.Value, sourceDir, App.SettingsManager.Current.GetEnabledLaunchOptions());
            _focusManager.SetFocus(newId.Value);
        }
    }

    private void OnContextClosePane(Guid paneId)
    {
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace == null || tab == null) return;
        var destroyed = App.WorkspaceManager.ClosePane(workspace.Id, tab.Id, paneId);
        _sessionManager.DestroySessions(destroyed);
        var remaining = App.WorkspaceManager.ActiveWorkspace?.ActiveTab?.RootSplit;
        var nextFocus = remaining?.GetAllPaneIds().FirstOrDefault();
        if (nextFocus.HasValue && nextFocus.Value != default)
            _focusManager.SetFocus(nextFocus.Value);
    }

    private void OnContextNewTab()
    {
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        if (workspace == null) return;

        // Inherit CWD from the focused pane and auto-launch Claude in the new tab
        string sourceDir = workspace.WorkingDirectory;
        if (_focusManager.FocusedPaneId.HasValue)
            sourceDir = _sessionManager.GetWorkingDirectory(_focusManager.FocusedPaneId.Value) ?? sourceDir;

        workspace.WorkingDirectory = sourceDir;

        var tab = App.WorkspaceManager.AddTab(workspace.Id);
        if (tab != null)
        {
            var newPaneId = tab.RootSplit.GetAllPaneIds().FirstOrDefault();
            if (newPaneId != default)
                _sessionManager.ConfigurePendingPane(newPaneId, sourceDir, App.SettingsManager.Current.GetEnabledLaunchOptions());
        }
    }

    private async void OnContextChangeDirectory(Guid paneId)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
        {
            var session = _sessionManager.GetOrCreateSession(paneId);
            // Send cd command to the terminal — works for cmd.exe, powershell, bash
            session.SendInput($"cd /d \"{folder.Path}\"\r");
        }
    }

    private async void OnInitialDirectorySelectionRequested(Guid paneId)
    {
        var folder = await PickFolderAsync();
        if (folder == null)
            return;

        var workspace = App.WorkspaceManager.ActiveWorkspace;
        if (workspace == null)
            return;

        App.WorkspaceManager.UpdateWorkspaceDirectory(workspace.Id, folder.Path);
        _sessionManager.CompleteDirectorySelection(paneId, folder.Path);
        _sessionManager.ConfigurePendingPane(
            paneId,
            folder.Path,
            App.SettingsManager.Current.GetEnabledLaunchOptions());
        RequestActiveTabLoad();
    }

    private async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        return await picker.PickSingleFolderAsync();
    }

    // --- Event Handlers ---

    private void OnWorkspacesChanged()
    {
        QueueStateSave();
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshTabBar();
            RequestActiveTabLoad();
        });
    }

    private void OnActiveTabChanged()
    {
        QueueStateSave();
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshTabBar();
            RequestActiveTabLoad();
        });
    }

    private void OnSplitTreeChanged()
    {
        QueueStateSave();
        DispatcherQueue.TryEnqueue(RequestActiveTabLoad);
    }

    private void OnPaneFocused(Guid paneId)
    {
        _focusManager.SetFocus(paneId);
        App.AgentMonitor.Dismiss(paneId);

        // Persist focus to the active tab
        var tab = App.WorkspaceManager.ActiveWorkspace?.ActiveTab;
        if (tab != null)
        {
            tab.FocusedPaneId = paneId;
            QueueStateSave();
        }
    }

    private void OnFocusChanged(Guid paneId)
    {
        PaneContainer.HighlightPane(paneId);
        PaneContainer.FocusTerminal(paneId);
    }

    // --- Tab Bar ---

    // Tab drag state — manual pointer-based reorder
    private Guid? _dragTabId;
    private Guid _dragTabWsId;
    private Windows.Foundation.Point _dragStartPoint;
    private bool _isDraggingTab;
    private bool _tabRenameActive;
    private const double DragThreshold = 8;

    // Monokai palette constants
    private static readonly Windows.UI.Color TabBg = Windows.UI.Color.FromArgb(255, 0x3e, 0x3d, 0x32);
    private static readonly Windows.UI.Color TabActiveBg = Windows.UI.Color.FromArgb(255, 0x66, 0xd9, 0xef);
    private static readonly Windows.UI.Color TabActiveFg = Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22);
    private static readonly Windows.UI.Color TabFg = Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2);
    private static readonly Windows.UI.Color TabDragOverBg = Windows.UI.Color.FromArgb(255, 0x49, 0x48, 0x3e);

    private void RefreshTabBar()
    {
        TabBar.Children.Clear();
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        if (workspace == null) return;

        foreach (var tab in workspace.Tabs)
        {
            var tabId = tab.Id;
            var wsId = workspace.Id;
            bool isActive = tab.Id == workspace.ActiveTabId;

            var titleBlock = new TextBlock
            {
                Text = tab.Title,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(isActive ? TabActiveFg : TabFg),
                FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };

            var tabPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsHitTestVisible = false };
            tabPanel.Children.Add(titleBlock);

            // Badge showing number of panes waiting for input in this tab
            var waitingCount = App.AgentMonitor.WaitingCountForPanes(tab.RootSplit.GetAllPaneIds());
            if (waitingCount > 0)
            {
                var badge = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 0xfd, 0x97, 0x1f)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5, 1, 5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = waitingCount.ToString(),
                        FontSize = 10,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(TabActiveFg),
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    }
                };
                tabPanel.Children.Add(badge);
            }

            // "..." menu button
            var menuBtn = new Button
            {
                Content = "\u22EF",
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 12,
                MinWidth = 0,
                MinHeight = 0,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(isActive ? TabActiveFg : TabFg),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var menuFlyout = new MenuFlyout();
            var renameItem = new MenuFlyoutItem { Text = "Rename" };
            renameItem.Click += (s, e) => BeginTabInlineRename(wsId, tabId);
            menuFlyout.Items.Add(renameItem);

            if (workspace.Tabs.Count > 1)
            {
                var closeItem = new MenuFlyoutItem { Text = "Close" };
                closeItem.Click += (s, e) =>
                {
                    var paneIds = App.WorkspaceManager.RemoveTab(wsId, tabId);
                    _sessionManager.DestroySessions(paneIds);
                };
                menuFlyout.Items.Add(closeItem);
            }
            menuBtn.Flyout = menuFlyout;

            var contentGrid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 4 };
            Grid.SetColumn(tabPanel, 0);
            Grid.SetColumn(menuBtn, 1);
            contentGrid.Children.Add(tabPanel);
            contentGrid.Children.Add(menuBtn);

            var tabElement = new Border
            {
                Tag = tab.Id,
                Child = contentGrid,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 2, 0),
                CornerRadius = new CornerRadius(4),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(isActive ? TabActiveBg : TabBg),
            };

            // Pointer events for click, double-click, and drag reorder
            tabElement.PointerPressed += (s, e) =>
            {
                if (s is not Border b) return;
                var pt = e.GetCurrentPoint(b);
                if (!pt.Properties.IsLeftButtonPressed) return;

                _dragTabId = tabId;
                _dragTabWsId = wsId;
                _dragStartPoint = pt.Position;
                _isDraggingTab = false;
                b.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            tabElement.PointerMoved += (s, e) =>
            {
                if (_dragTabId == null || _dragTabId.Value != tabId) return;
                if (s is not Border b) return;

                var pt = e.GetCurrentPoint(TabBar).Position;
                if (!_isDraggingTab)
                {
                    var current = e.GetCurrentPoint(b).Position;
                    double dx = current.X - _dragStartPoint.X;
                    double dy = current.Y - _dragStartPoint.Y;
                    if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
                    _isDraggingTab = true;
                    b.Opacity = 0.6;
                }

                // Find which tab we're hovering over and do a live reorder
                for (int i = 0; i < TabBar.Children.Count; i++)
                {
                    if (TabBar.Children[i] is not Border target) continue;
                    if (target.Tag is not Guid targetId || targetId == tabId) continue;

                    var transform = target.TransformToVisual(TabBar);
                    var targetPos = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    double targetMid = targetPos.X + target.ActualWidth / 2;

                    if (pt.X >= targetPos.X && pt.X <= targetPos.X + target.ActualWidth)
                    {
                        var ws = App.WorkspaceManager.ActiveWorkspace;
                        if (ws == null) break;
                        App.WorkspaceManager.MoveTab(ws.Id, tabId, i);
                        break;
                    }
                }

                e.Handled = true;
            };

            tabElement.PointerReleased += (s, e) =>
            {
                // If a double-tap rename just started, skip activation
                if (_tabRenameActive)
                {
                    _tabRenameActive = false;
                    e.Handled = true;
                    return;
                }

                if (_dragTabId == null || _dragTabId.Value != tabId) return;
                if (s is Border b)
                {
                    try { b.ReleasePointerCapture(e.Pointer); } catch { }
                    b.Opacity = 1.0;
                }

                if (!_isDraggingTab)
                {
                    // It was a click, not a drag — activate tab (only if not already active,
                    // so the element survives for DoubleTapped to fire on a second click)
                    var ws = App.WorkspaceManager.ActiveWorkspace;
                    if (ws == null || ws.ActiveTabId != tabId)
                        App.WorkspaceManager.ActivateTab(_dragTabWsId, tabId);
                }

                _dragTabId = null;
                _isDraggingTab = false;
                e.Handled = true;
            };

            tabElement.PointerCaptureLost += (s, e) =>
            {
                if (s is Border b) b.Opacity = 1.0;
                _dragTabId = null;
                _isDraggingTab = false;
            };

            // Double-tap — inline rename
            tabElement.DoubleTapped += (s, e) =>
            {
                if (s is Border b)
                {
                    try { b.ReleasePointerCaptures(); } catch { }
                }
                _dragTabId = null;
                _isDraggingTab = false;
                _tabRenameActive = true;
                BeginTabInlineRename(wsId, tabId);
                e.Handled = true;
            };

            // Hover highlight for drag targets
            tabElement.DragEnter += (s, e) =>
            {
                if (s is Border b && _dragTabId.HasValue && _dragTabId.Value != tabId)
                    b.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(TabDragOverBg);
            };
            tabElement.DragLeave += (s, e) =>
            {
                if (s is Border b)
                {
                    var ws = App.WorkspaceManager.ActiveWorkspace;
                    bool active = ws?.ActiveTabId == tabId;
                    b.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(active ? TabActiveBg : TabBg);
                }
            };

            TabBar.Children.Add(tabElement);
        }
    }

    private void BeginTabInlineRename(Guid wsId, Guid tabId)
    {
        var workspace = App.WorkspaceManager.Workspaces.FirstOrDefault(w => w.Id == wsId);
        var tab = workspace?.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null) return;

        foreach (var child in TabBar.Children)
        {
            if (child is Border border && border.Tag is Guid id && id == tabId)
            {
                var textBox = new TextBox
                {
                    Text = tab.Title,
                    SelectionStart = 0,
                    SelectionLength = tab.Title.Length,
                    MinWidth = 60,
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 14,
                };

                var originalChild = border.Child;
                border.Child = textBox;
                textBox.Focus(FocusState.Programmatic);

                bool committed = false;
                void CommitRename()
                {
                    if (committed) return;
                    committed = true;
                    var newTitle = textBox.Text;
                    border.Child = originalChild;
                    if (!string.IsNullOrWhiteSpace(newTitle) && newTitle != tab.Title)
                        App.WorkspaceManager.RenameTab(wsId, tabId, newTitle);
                }

                textBox.LostFocus += (s, e) => CommitRename();
                textBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        CommitRename();
                        e.Handled = true;
                    }
                    else if (e.Key == Windows.System.VirtualKey.Escape)
                    {
                        committed = true;
                        border.Child = originalChild;
                        e.Handled = true;
                    }
                };

                break;
            }
        }
    }

    private void RequestActiveTabLoad()
    {
        _pendingActiveTabLoad = true;
        if (_isLoadingActiveTab)
            return;

        _ = ProcessActiveTabLoadsAsync();
    }

    private async Task ProcessActiveTabLoadsAsync()
    {
        _isLoadingActiveTab = true;
        try
        {
            while (_pendingActiveTabLoad)
            {
                _pendingActiveTabLoad = false;
                await LoadActiveTabAsync();
            }
        }
        finally
        {
            _isLoadingActiveTab = false;
        }
    }

    private async Task LoadActiveTabAsync()
    {
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        var tab = workspace?.ActiveTab;
        if (tab == null || workspace == null) return;

        await PaneContainer.SetSplitTree(
            tab.RootSplit,
            _sessionManager,
            workspace.WorkingDirectory);

        // Restore focus first (does NOT dismiss notifications — only clicking does)
        var focusId = tab.FocusedPaneId ?? tab.RootSplit.GetAllPaneIds().FirstOrDefault();
        if (focusId != default)
            _focusManager.SetFocus(focusId);

        // Re-apply waiting indicators AFTER focus so orange isn't overwritten
        PaneContainer.UpdateWaitingIndicators();
    }

    // --- Notifications ---

    private void ShowNotification(GmuxNotification notification)
    {
        NotificationBar.Title = notification.WorkspaceName;
        NotificationBar.Message = notification.Message;
        NotificationBar.Severity = notification.Type == NotificationType.Error
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Informational;
        NotificationBar.IsOpen = true;
        AutoDismissNotification();
    }

    private void CheckAgentWaitingNotifications()
    {
        var waitingTabs = App.WorkspaceManager.Workspaces
            .SelectMany(workspace => workspace.Tabs.Select(tab => new
            {
                WorkspaceName = workspace.Name,
                TabTitle = tab.Title,
                WaitingCount = App.AgentMonitor.WaitingCountForPanes(tab.RootSplit.GetAllPaneIds())
            }))
            .Where(x => x.WaitingCount > 0)
            .ToList();

        if (App.SettingsManager.Current.WaitingNotificationScope == NotificationScope.ActiveTabOnly)
        {
            var workspace = App.WorkspaceManager.ActiveWorkspace;
            var tab = workspace?.ActiveTab;
            waitingTabs = tab == null
                ? []
                : waitingTabs
                    .Where(x => x.WorkspaceName == workspace!.Name && x.TabTitle == tab.Title)
                    .ToList();
        }

        if (waitingTabs.Count == 0)
        {
            _pendingWaitingNotificationSignature = null;
            _pendingWaitingNotificationTitle = null;
            _pendingWaitingNotificationMessage = null;
            _waitingNotificationDelayTimer?.Stop();

            // Grace period: don't clear signature immediately.
            // If waiting comes back within a few seconds (detection blip),
            // the same signature won't re-trigger the notification.
            if (_lastWaitingNotificationSignature != null)
            {
                _notificationDismissTimer?.Stop();
                NotificationBar.IsOpen = false;
                _notificationClearGraceTimer ??= new DispatcherTimer();
                _notificationClearGraceTimer.Interval = NotificationClearGrace;
                _notificationClearGraceTimer.Stop();
                _notificationClearGraceTimer.Tick -= OnNotificationClearGraceTick;
                _notificationClearGraceTimer.Tick += OnNotificationClearGraceTick;
                _notificationClearGraceTimer.Start();
            }
            return;
        }

        // Waiting tabs found — cancel any pending clear grace
        _notificationClearGraceTimer?.Stop();

        int totalWaiting = waitingTabs.Sum(x => x.WaitingCount);
        string signature = string.Join("|", waitingTabs
            .OrderBy(x => x.WorkspaceName, StringComparer.Ordinal)
            .ThenBy(x => x.TabTitle, StringComparer.Ordinal)
            .Select(x => $"{x.WorkspaceName}:{x.TabTitle}:{x.WaitingCount}"));

        if (signature == _lastWaitingNotificationSignature)
            return;

        var title = totalWaiting == 1 ? "Agent waiting" : "Agents waiting in multiple panes";
        var message = waitingTabs.Count == 1
            ? $"{waitingTabs[0].WorkspaceName} / {waitingTabs[0].TabTitle} needs input"
            : $"{totalWaiting} panes across {waitingTabs.Count} tabs need input";

        if (signature == _pendingWaitingNotificationSignature)
            return;

        _pendingWaitingNotificationSignature = signature;
        _pendingWaitingNotificationTitle = title;
        _pendingWaitingNotificationMessage = message;

        _waitingNotificationDelayTimer ??= new DispatcherTimer();
        _waitingNotificationDelayTimer.Interval = WaitingNotificationDelay;
        _waitingNotificationDelayTimer.Stop();
        _waitingNotificationDelayTimer.Tick -= OnWaitingNotificationDelayTimerTick;
        _waitingNotificationDelayTimer.Tick += OnWaitingNotificationDelayTimerTick;
        _waitingNotificationDelayTimer.Start();
    }

    private void OnWaitingNotificationDelayTimerTick(object? sender, object e)
    {
        _waitingNotificationDelayTimer?.Stop();

        if (string.IsNullOrWhiteSpace(_pendingWaitingNotificationSignature) ||
            string.IsNullOrWhiteSpace(_pendingWaitingNotificationTitle) ||
            string.IsNullOrWhiteSpace(_pendingWaitingNotificationMessage))
        {
            return;
        }

        _lastWaitingNotificationSignature = _pendingWaitingNotificationSignature;
        NotificationBar.Title = _pendingWaitingNotificationTitle;
        NotificationBar.Message = _pendingWaitingNotificationMessage;
        NotificationBar.Severity = InfoBarSeverity.Warning;
        NotificationBar.IsOpen = true;
        AutoDismissNotification();
    }

    private void OnNotificationClearGraceTick(object? sender, object e)
    {
        _notificationClearGraceTimer?.Stop();
        _lastWaitingNotificationSignature = null;
        _notificationDismissTimer?.Stop();
        NotificationBar.IsOpen = false;
    }

    private void AutoDismissNotification()
    {
        _notificationDismissTimer ??= new DispatcherTimer();
        _notificationDismissTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, App.SettingsManager.Current.WaitingNotificationDurationSeconds));
        _notificationDismissTimer.Stop();
        _notificationDismissTimer.Tick -= OnNotificationDismissTimerTick;
        _notificationDismissTimer.Tick += OnNotificationDismissTimerTick;
        _notificationDismissTimer.Start();
    }

    private void OnNotificationDismissTimerTick(object? sender, object e)
    {
        _notificationDismissTimer?.Stop();
        NotificationBar.IsOpen = false;
    }

    // --- Button Handlers ---

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        OnContextNewTab();
    }

    private void QueueStateSave()
    {
        _stateSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stateSaveTimer.Tick -= OnStateSaveTimerTick;
        _stateSaveTimer.Tick += OnStateSaveTimerTick;
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private async void OnStateSaveTimerTick(object? sender, object e)
    {
        _stateSaveTimer?.Stop();
        await App.WorkspaceManager.SaveStateAsync();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _sessionManager.ApplySettings();
            PaneContainer.ApplySettings();
            CheckAgentWaitingNotifications();
        });
    }

    // --- Traffic Light Button Handlers ---

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore();
            else
                presenter.Maximize();
        }
    }

    // --- Keyboard Shortcuts ---

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Ctrl+Tab — cycle tabs
        if (ctrl && e.Key == Windows.System.VirtualKey.Tab)
        {
            var workspace = App.WorkspaceManager.ActiveWorkspace;
            if (workspace != null)
                App.WorkspaceManager.CycleTab(workspace.Id, shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Arrow — navigate between panes
        if (ctrl && shift)
        {
            var tab = App.WorkspaceManager.ActiveWorkspace?.ActiveTab;
            if (tab == null) return;

            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                case Windows.System.VirtualKey.Up:
                {
                    var target = _focusManager.NavigateFocus(tab.RootSplit, NavigationDirection.Previous);
                    if (target.HasValue) App.AgentMonitor.Dismiss(target.Value);
                    e.Handled = true;
                    return;
                }
                case Windows.System.VirtualKey.Right:
                case Windows.System.VirtualKey.Down:
                {
                    var target = _focusManager.NavigateFocus(tab.RootSplit, NavigationDirection.Next);
                    if (target.HasValue) App.AgentMonitor.Dismiss(target.Value);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!ctrl || !shift) return;

        var workspace = App.WorkspaceManager.ActiveWorkspace;
        var tab = workspace?.ActiveTab;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.T: // New tab
                if (workspace != null)
                    OnContextNewTab();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.D: // Split vertical (right)
                if (workspace != null && tab != null && _focusManager.FocusedPaneId.HasValue)
                    OnContextSplitVertical(_focusManager.FocusedPaneId.Value);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.E: // Split horizontal (down)
                if (workspace != null && tab != null && _focusManager.FocusedPaneId.HasValue)
                    OnContextSplitHorizontal(_focusManager.FocusedPaneId.Value);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.W: // Close pane
                if (workspace != null && tab != null && _focusManager.FocusedPaneId.HasValue)
                {
                    var destroyed = App.WorkspaceManager.ClosePane(
                        workspace.Id, tab.Id, _focusManager.FocusedPaneId.Value);
                    _sessionManager.DestroySessions(destroyed);

                    // Focus the first remaining pane
                    var remaining = App.WorkspaceManager.ActiveWorkspace?.ActiveTab?.RootSplit;
                    var nextFocus = remaining?.GetAllPaneIds().FirstOrDefault();
                    if (nextFocus.HasValue && nextFocus.Value != default)
                        _focusManager.SetFocus(nextFocus.Value);
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.N: // New workspace
                var count = App.WorkspaceManager.Workspaces.Count + 1;
                var newWs = App.WorkspaceManager.CreateWorkspace(
                    $"Workspace {count}",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var newWsPaneId = newWs.ActiveTab?.RootSplit.GetAllPaneIds().FirstOrDefault();
                if (newWsPaneId.HasValue && newWsPaneId.Value != default)
                    _sessionManager.RequireDirectorySelection(newWsPaneId.Value);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Up:
                if (tab != null)
                {
                    var prev = _focusManager.NavigateFocus(tab.RootSplit, NavigationDirection.Previous);
                    if (prev.HasValue) App.AgentMonitor.Dismiss(prev.Value);
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.Down:
                if (tab != null)
                {
                    var next = _focusManager.NavigateFocus(tab.RootSplit, NavigationDirection.Next);
                    if (next.HasValue) App.AgentMonitor.Dismiss(next.Value);
                }
                e.Handled = true;
                break;
        }
    }
}
