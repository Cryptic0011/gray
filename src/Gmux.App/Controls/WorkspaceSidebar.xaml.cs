using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Gmux.Core.Models;
using Gmux.Core.Services;

namespace Gmux.App.Controls;

public sealed partial class WorkspaceSidebar : UserControl
{
    private bool _suppressSelection;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly IntPtr HT_CAPTION = new(2);

    public WorkspaceSidebar()
    {
        InitializeComponent();

        App.WorkspaceManager.WorkspacesChanged += RefreshList;
        App.AgentMonitor.StateChanged += RefreshList;
        Loaded += (s, e) => RefreshList();
        WorkspaceList.SelectionChanged += OnWorkspaceSelected;

        // Make the sidebar header act as a window drag region
        SidebarHeader.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(SidebarHeader).Properties.IsLeftButtonPressed) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, IntPtr.Zero);
        };
    }

    private void OnWorkspaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (WorkspaceList.SelectedItem is WorkspaceItem item)
            App.WorkspaceManager.ActivateWorkspace(item.Id);
    }

    private void RefreshList()
    {
        var dq = DispatcherQueue;
        if (dq == null) return;
        dq.TryEnqueue(() =>
        {
            _suppressSelection = true;

            var items = App.WorkspaceManager.Workspaces.Select(w => new WorkspaceItem
            {
                Id = w.Id,
                Name = w.Name,
                GitBranch = w.GitBranch ?? string.Empty,
                ShortPath = ShortenPath(w.WorkingDirectory),
                UnreadCount = App.AgentMonitor.WaitingCountForPanes(
                    w.Tabs.SelectMany(t => t.RootSplit.GetAllPaneIds())),
                IsActive = w.IsActive
            }).ToList();

            WorkspaceList.ItemsSource = items;

            var activeItem = items.FirstOrDefault(i => i.IsActive);
            if (activeItem != null)
                WorkspaceList.SelectedItem = activeItem;

            _suppressSelection = false;
        });
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..];
        return path;
    }

    // The ... button click just opens its flyout (handled by XAML Button.Flyout)
    private void WorkspaceMenu_Click(object sender, RoutedEventArgs e) { }

    private async void RenameWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not Guid id) return;
        var workspace = App.WorkspaceManager.Workspaces.FirstOrDefault(w => w.Id == id);
        if (workspace == null) return;

        var textBox = new TextBox { Text = workspace.Name, SelectionStart = 0, SelectionLength = workspace.Name.Length };
        var dialog = new ContentDialog
        {
            Title = "Rename Workspace",
            Content = textBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            App.WorkspaceManager.RenameWorkspace(id, textBox.Text);
    }

    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not Guid id) return;
        if (App.WorkspaceManager.Workspaces.Count <= 1) return; // Don't delete last workspace
        App.WorkspaceManager.RemoveWorkspace(id);
    }

    private async void NewWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var defaultName = $"Workspace {App.WorkspaceManager.Workspaces.Count + 1}";
        var textBox = new TextBox { Text = defaultName, SelectionStart = 0, SelectionLength = defaultName.Length };
        var dialog = new ContentDialog
        {
            Title = "New Workspace",
            Content = textBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            var ws = App.WorkspaceManager.CreateWorkspace(
                textBox.Text.Trim(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var paneId = ws.ActiveTab?.RootSplit.GetAllPaneIds().FirstOrDefault();
            if (paneId.HasValue && paneId.Value != default && App.SessionManager != null)
                App.SessionManager.RequireDirectorySelection(paneId.Value);
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var current = App.SettingsManager.Current;
        var claude = new CheckBox { Content = "Claude CLI", IsChecked = current.UseClaudeCli };
        var codex = new CheckBox { Content = "Codex CLI", IsChecked = current.UseCodexCli };
        var gemini = new CheckBox { Content = "Gemini CLI", IsChecked = current.UseGeminiCli };
        var shellBox = new ComboBox
        {
            ItemsSource = new[] { "cmd.exe", "powershell.exe", "pwsh.exe" },
            SelectedItem = current.DefaultShell
        };
        var launchModeBox = new ComboBox
        {
            ItemsSource = Enum.GetValues<AgentLaunchMode>(),
            SelectedItem = current.AgentLaunchMode
        };
        var preferredAgentBox = new ComboBox
        {
            ItemsSource = Enum.GetValues<AgentCliKind>(),
            SelectedItem = current.PreferredAgent
        };
        var notificationScopeBox = new ComboBox
        {
            ItemsSource = Enum.GetValues<NotificationScope>(),
            SelectedItem = current.WaitingNotificationScope
        };
        var durationBox = new NumberBox { Value = current.WaitingNotificationDurationSeconds, Minimum = 1, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var fontSizeBox = new NumberBox { Value = current.TerminalFontSize, Minimum = 8, Maximum = 32, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var scrollbackBox = new NumberBox { Value = current.ScrollbackSize, Minimum = 100, Maximum = 50000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var claudeCommandBox = new TextBox { Text = current.ClaudeLaunchCommand };
        var codexCommandBox = new TextBox { Text = current.CodexLaunchCommand };
        var geminiCommandBox = new TextBox { Text = current.GeminiLaunchCommand };
        var customCommandRows = new List<(TextBox TitleBox, TextBox CommandBox, FrameworkElement Row)>();
        var customCommandsPanel = new StackPanel { Spacing = 8 };
        var previewPanel = new StackPanel { Spacing = 6 };
        var addCustomCommandButton = new Button
        {
            Content = "Add custom command",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var updateStatus = new TextBlock
        {
            Text = "Not checked yet",
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e))
        };
        var releaseLink = new HyperlinkButton
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var checkUpdatesButton = new Button
        {
            Content = "Check for updates",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        checkUpdatesButton.Click += async (_, _) =>
        {
            checkUpdatesButton.IsEnabled = false;
            updateStatus.Text = "Checking GitHub releases...";
            releaseLink.Visibility = Visibility.Collapsed;

            var result = await App.UpdateChecker.CheckForUpdatesAsync();
            updateStatus.Text = result.Message;
            if (!string.IsNullOrWhiteSpace(result.ReleaseUrl))
            {
                releaseLink.Content = "Open release page";
                releaseLink.NavigateUri = new Uri(result.ReleaseUrl);
                releaseLink.Visibility = Visibility.Visible;
            }
            checkUpdatesButton.IsEnabled = true;
        };

        var shellStatus = AppInfoService.GetToolStatus(current.DefaultShell);
        var claudeStatus = AppInfoService.GetToolStatus(current.ClaudeLaunchCommand);
        var codexStatus = AppInfoService.GetToolStatus(current.CodexLaunchCommand);
        var geminiStatus = AppInfoService.GetToolStatus(current.GeminiLaunchCommand);

        void RefreshPreview()
        {
            previewPanel.Children.Clear();

            void addPreviewLine(string title, string command)
            {
                previewPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(command) ? title : $"{title} - {command}",
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            if (claude.IsChecked == true) addPreviewLine("Claude", claudeCommandBox.Text.Trim());
            if (codex.IsChecked == true) addPreviewLine("Codex", codexCommandBox.Text.Trim());
            if (gemini.IsChecked == true) addPreviewLine("Gemini", geminiCommandBox.Text.Trim());

            foreach (var (titleBox, commandBox, _) in customCommandRows)
            {
                var title = titleBox.Text.Trim();
                var command = commandBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(command))
                    addPreviewLine(title, command);
            }

            addPreviewLine("Blank", string.Empty);
        }

        void addCustomCommandRow(string title = "", string command = "")
        {
            var titleBox = new TextBox { Text = title, PlaceholderText = "Title, e.g. Aider" };
            var commandBox = new TextBox { Text = command, PlaceholderText = "Command, e.g. aider" };
            var removeButton = new Button
            {
                Content = "Remove",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var rowContent = new StackPanel { Spacing = 6 };
            rowContent.Children.Add(Labeled("Title", titleBox));
            rowContent.Children.Add(Labeled("Command", commandBox));
            rowContent.Children.Add(removeButton);

            var row = new Border
            {
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x49, 0x48, 0x3e)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Child = rowContent
            };

            customCommandRows.Add((titleBox, commandBox, row));
            customCommandsPanel.Children.Add(row);

            titleBox.TextChanged += (_, _) => RefreshPreview();
            commandBox.TextChanged += (_, _) => RefreshPreview();
            removeButton.Click += (_, _) =>
            {
                customCommandsPanel.Children.Remove(row);
                customCommandRows.RemoveAll(item => ReferenceEquals(item.Row, row));
                RefreshPreview();
            };

            RefreshPreview();
        }

        addCustomCommandButton.Click += (_, _) => addCustomCommandRow();
        foreach (var command in current.CustomTerminalCommands ?? new List<CustomTerminalCommand>())
            addCustomCommandRow(command.Title, command.Command);
        claude.Checked += (_, _) => RefreshPreview();
        claude.Unchecked += (_, _) => RefreshPreview();
        codex.Checked += (_, _) => RefreshPreview();
        codex.Unchecked += (_, _) => RefreshPreview();
        gemini.Checked += (_, _) => RefreshPreview();
        gemini.Unchecked += (_, _) => RefreshPreview();
        claudeCommandBox.TextChanged += (_, _) => RefreshPreview();
        codexCommandBox.TextChanged += (_, _) => RefreshPreview();
        geminiCommandBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"{AppInfoService.ProductName} {AppInfoService.CurrentVersion}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock { Text = "Agents", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(claude);
        content.Children.Add(codex);
        content.Children.Add(gemini);
        content.Children.Add(Labeled("New pane mode", launchModeBox));
        content.Children.Add(Labeled("Preferred agent", preferredAgentBox));

        content.Children.Add(new TextBlock { Text = "Commands", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        content.Children.Add(Labeled("Default shell", shellBox));
        content.Children.Add(Labeled("Claude launch", claudeCommandBox));
        content.Children.Add(Labeled("Codex launch", codexCommandBox));
        content.Children.Add(Labeled("Gemini launch", geminiCommandBox));
        content.Children.Add(new TextBlock { Text = "Custom commands", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        content.Children.Add(customCommandsPanel);
        content.Children.Add(addCustomCommandButton);
        content.Children.Add(Labeled("Launch chooser preview", previewPanel));

        content.Children.Add(new TextBlock { Text = "Notifications", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        content.Children.Add(Labeled("Waiting scope", notificationScopeBox));
        content.Children.Add(Labeled("Toast duration (s)", durationBox));

        content.Children.Add(new TextBlock { Text = "Terminal", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        content.Children.Add(Labeled("Font size", fontSizeBox));
        content.Children.Add(Labeled("Scrollback lines", scrollbackBox));

        content.Children.Add(new TextBlock { Text = "Diagnostics", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        content.Children.Add(StatusLine("Shell", shellStatus));
        content.Children.Add(StatusLine("Claude", claudeStatus));
        content.Children.Add(StatusLine("Codex", codexStatus));
        content.Children.Add(StatusLine("Gemini", geminiStatus));
        content.Children.Add(checkUpdatesButton);
        content.Children.Add(updateStatus);
        content.Children.Add(releaseLink);

        var scrollViewer = new ScrollViewer
        {
            Content = content,
            MaxHeight = 560
        };

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        current.UseClaudeCli = claude.IsChecked == true;
        current.UseCodexCli = codex.IsChecked == true;
        current.UseGeminiCli = gemini.IsChecked == true;
        current.DefaultShell = (string?)shellBox.SelectedItem ?? "cmd.exe";
        current.AgentLaunchMode = launchModeBox.SelectedItem is AgentLaunchMode mode ? mode : AgentLaunchMode.PreferredAgent;
        current.PreferredAgent = preferredAgentBox.SelectedItem is AgentCliKind preferred ? preferred : AgentCliKind.Claude;
        current.ClaudeLaunchCommand = string.IsNullOrWhiteSpace(claudeCommandBox.Text) ? "claude --dangerously-skip-permissions" : claudeCommandBox.Text.Trim();
        current.CodexLaunchCommand = string.IsNullOrWhiteSpace(codexCommandBox.Text) ? "codex --yolo" : codexCommandBox.Text.Trim();
        current.GeminiLaunchCommand = string.IsNullOrWhiteSpace(geminiCommandBox.Text) ? "gemini --yolo" : geminiCommandBox.Text.Trim();
        current.CustomTerminalCommands = customCommandRows
            .Select(row => new CustomTerminalCommand
            {
                Title = row.TitleBox.Text.Trim(),
                Command = row.CommandBox.Text.Trim()
            })
            .Where(command => !string.IsNullOrWhiteSpace(command.Title) && !string.IsNullOrWhiteSpace(command.Command))
            .ToList();
        current.WaitingNotificationScope = notificationScopeBox.SelectedItem is NotificationScope scope ? scope : NotificationScope.AllTabs;
        current.WaitingNotificationDurationSeconds = Math.Clamp((int)Math.Round(durationBox.Value), 1, 60);
        current.TerminalFontSize = Math.Clamp(fontSizeBox.Value, 8, 32);
        current.ScrollbackSize = Math.Clamp((int)Math.Round(scrollbackBox.Value), 100, 50000);

        await App.SettingsManager.SaveAsync();
    }

    private static FrameworkElement Labeled(string label, FrameworkElement value)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e))
        });
        panel.Children.Add(value);
        return panel;
    }

    private static FrameworkElement StatusLine(string label, ToolStatus status)
    {
        return new TextBlock
        {
            Text = $"{label}: {(status.IsAvailable ? "OK" : "Missing")} - {status.Message}",
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                status.IsAvailable
                    ? Windows.UI.Color.FromArgb(255, 0xa6, 0xe2, 0x2e)
                    : Windows.UI.Color.FromArgb(255, 0xf9, 0x26, 0x72))
        };
    }

    private class WorkspaceItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string GitBranch { get; set; } = string.Empty;
        public string ShortPath { get; set; } = string.Empty;
        public int UnreadCount { get; set; }
        public bool IsActive { get; set; }
        public string GitBranchLabel => string.IsNullOrWhiteSpace(GitBranch) ? string.Empty : $"git: {GitBranch}";
        public Visibility GitBranchVisibility => string.IsNullOrWhiteSpace(GitBranch) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility BadgeVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
