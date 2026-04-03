using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Gmux.Core.Models;
using Gmux.Core.Services;
using Gmux.App.Services;
using WinRT.Interop;

namespace Gmux.App;

public sealed partial class MainWindow : Window
{
    private readonly SessionManager _sessionManager;
    private readonly PaneFocusManager _focusManager = new();
    private AppWindow _appWindow = null!;
    private DispatcherTimer? _stateSaveTimer;
    private DispatcherTimer? _notificationDismissTimer;
    private string? _lastWaitingNotificationSignature;

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
            _sessionManager.ConfigurePendingPane(newId.Value, sourceDir, App.SettingsManager.Current.GetEnabledAgentClis());
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
            _sessionManager.ConfigurePendingPane(newId.Value, sourceDir, App.SettingsManager.Current.GetEnabledAgentClis());
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
                _sessionManager.ConfigurePendingPane(newPaneId, sourceDir, App.SettingsManager.Current.GetEnabledAgentClis());
        }
    }

    private async void OnContextChangeDirectory(Guid paneId)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        // Initialize picker with the window handle (required for WinUI 3)
        var hwnd = WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            var session = _sessionManager.GetOrCreateSession(paneId);
            // Send cd command to the terminal — works for cmd.exe, powershell, bash
            session.SendInput($"cd /d \"{folder.Path}\"\r");
        }
    }

    // --- Event Handlers ---

    private void OnWorkspacesChanged()
    {
        QueueStateSave();
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshTabBar();
            _ = LoadActiveTabAsync();
        });
    }

    private void OnActiveTabChanged()
    {
        QueueStateSave();
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshTabBar();
            _ = LoadActiveTabAsync();
        });
    }

    private void OnSplitTreeChanged()
    {
        QueueStateSave();
        DispatcherQueue.TryEnqueue(() => _ = LoadActiveTabAsync());
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

    private void RefreshTabBar()
    {
        TabBar.Children.Clear();
        var workspace = App.WorkspaceManager.ActiveWorkspace;
        if (workspace == null) return;

        foreach (var tab in workspace.Tabs)
        {
            var tabPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

            var titleBlock = new TextBlock
            {
                Text = tab.Title,
                VerticalAlignment = VerticalAlignment.Center
            };
            tabPanel.Children.Add(titleBlock);

            // Badge showing number of panes waiting for input in this tab
            var waitingCount = App.AgentMonitor.WaitingCountForPanes(tab.RootSplit.GetAllPaneIds());
            if (waitingCount > 0)
            {
                var badge = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 0xfd, 0x97, 0x1f)), // Monokai orange — matches pane border
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5, 1, 5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = waitingCount.ToString(),
                        FontSize = 10,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22)),
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    }
                };
                tabPanel.Children.Add(badge);
            }

            // "..." menu button with Rename (and Close if multiple tabs)
            var tabId = tab.Id;
            var wsId = workspace.Id;
            var menuBtn = new Button
            {
                Content = "\u22EF", // ⋯
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 12,
                MinWidth = 0,
                MinHeight = 0,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var menuFlyout = new MenuFlyout();

            var renameItem = new MenuFlyoutItem { Text = "Rename" };
            renameItem.Click += async (s, e) =>
            {
                var t = workspace.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (t == null) return;
                var textBox = new TextBox { Text = t.Title, SelectionStart = 0, SelectionLength = t.Title.Length };
                var dialog = new ContentDialog
                {
                    Title = "Rename Tab",
                    Content = textBox,
                    PrimaryButtonText = "Rename",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    App.WorkspaceManager.RenameTab(wsId, tabId, textBox.Text);
            };
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
            tabPanel.Children.Add(menuBtn);

            var tabButton = new Button
            {
                Content = tabPanel,
                Tag = tab.Id,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 2, 0),
            };

            if (tab.Id == workspace.ActiveTabId)
            {
                tabButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
            }

            tabButton.Click += (s, e) =>
            {
                if (s is Button btn && btn.Tag is Guid id)
                    App.WorkspaceManager.ActivateTab(workspace.Id, id);
            };

            TabBar.Children.Add(tabButton);
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
            _lastWaitingNotificationSignature = null;
            _notificationDismissTimer?.Stop();
            NotificationBar.IsOpen = false;
            return;
        }

        int totalWaiting = waitingTabs.Sum(x => x.WaitingCount);
        string signature = string.Join("|", waitingTabs
            .OrderBy(x => x.WorkspaceName, StringComparer.Ordinal)
            .ThenBy(x => x.TabTitle, StringComparer.Ordinal)
            .Select(x => $"{x.WorkspaceName}:{x.TabTitle}:{x.WaitingCount}"));

        if (signature == _lastWaitingNotificationSignature)
            return;

        _lastWaitingNotificationSignature = signature;

        NotificationBar.Title = totalWaiting == 1 ? "Agent waiting" : "Agents waiting in multiple panes";
        NotificationBar.Message = waitingTabs.Count == 1
            ? $"{waitingTabs[0].WorkspaceName} / {waitingTabs[0].TabTitle} needs input"
            : $"{totalWaiting} panes across {waitingTabs.Count} tabs need input";
        NotificationBar.Severity = InfoBarSeverity.Warning;
        NotificationBar.IsOpen = true;
        AutoDismissNotification();
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
                App.WorkspaceManager.CreateWorkspace(
                    $"Workspace {count}",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
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
