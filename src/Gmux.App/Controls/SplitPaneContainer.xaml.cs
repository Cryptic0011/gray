using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Gmux.Core.Models;
using Gmux.Core.Services;

namespace Gmux.App.Controls;

public sealed partial class SplitPaneContainer : UserControl
{
    private readonly Dictionary<Guid, TerminalControl> _terminalControls = new();
    private readonly Dictionary<Guid, Border> _paneBorders = new();
    private readonly Dictionary<Guid, Border> _paneOverlays = new(); // tint overlay
    private SplitNode? _currentTree;
    private Guid? _highlightedPaneId;

    public event Action<Guid>? PaneFocused;
    // Context menu events
    public event Action<Guid>? SplitVerticalRequested;
    public event Action<Guid>? SplitHorizontalRequested;
    public event Action<Guid>? ClosePaneRequested;
    public event Action? NewTabRequested;
    public event Action<Guid>? ChangeDirectoryRequested;
    public event Action<Guid>? InitialDirectorySelectionRequested;
    public event Action? RefreshRequested;

    public SplitPaneContainer()
    {
        InitializeComponent();
    }

    public async Task SetSplitTree(SplitNode tree, SessionManager sessionManager, string workingDirectory)
    {
        _currentTree = tree;

        // Detach all cached controls from their current parents
        foreach (var kvp in _terminalControls)
        {
            var control = kvp.Value;
            if (control.Parent is Grid grid)
                grid.Children.Remove(control);
            else if (control.Parent is Border border)
                border.Child = null;
        }

        RootGrid.Children.Clear();
        RootGrid.RowDefinitions.Clear();
        RootGrid.ColumnDefinitions.Clear();
        _paneBorders.Clear();
        _paneOverlays.Clear();

        // Prune stale controls
        var activePaneIds = new HashSet<Guid>(tree.GetAllPaneIds());
        var toRemove = _terminalControls.Keys.Where(id => !activePaneIds.Contains(id)).ToList();
        foreach (var id in toRemove)
            _terminalControls.Remove(id);

        var element = await BuildElement(tree, sessionManager, workingDirectory);
        RootGrid.Children.Add(element);
    }

    // Visual state colors
    private static readonly Windows.UI.Color WaitingBorderColor = Windows.UI.Color.FromArgb(255, 0xfd, 0x97, 0x1f); // Monokai orange — needs attention
    private static readonly Windows.UI.Color WaitingTintColor = Windows.UI.Color.FromArgb(20, 0xfd, 0x97, 0x1f);

    private static readonly Windows.UI.Color FocusBorderColor = Windows.UI.Color.FromArgb(255, 0x66, 0xd9, 0xef); // Cyan — focused pane
    private static readonly Windows.UI.Color TransparentColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    public void HighlightPane(Guid? paneId)
    {
        var oldId = _highlightedPaneId;
        _highlightedPaneId = paneId;

        // Restore old pane
        if (oldId.HasValue)
            ApplyPaneVisuals(oldId.Value, isFocused: false);

        // Highlight new focused pane
        if (paneId.HasValue)
            ApplyPaneVisuals(paneId.Value, isFocused: true);
    }

    public void UpdateWaitingIndicators()
    {
        foreach (var (paneId, _) in _paneBorders)
        {
            ApplyPaneVisuals(paneId, isFocused: paneId == _highlightedPaneId);
        }
    }

    private void ApplyPaneVisuals(Guid paneId, bool isFocused)
    {
        var monitor = App.AgentMonitor;
        bool isWaiting = monitor.IsWaiting(paneId);
        // Logging removed — see AgentMonitorService log file

        if (_paneBorders.TryGetValue(paneId, out var border))
        {
            if (isFocused)
            {
                border.BorderBrush = new SolidColorBrush(FocusBorderColor);
                border.BorderThickness = new Thickness(2);
            }
            else if (isWaiting)
            {
                border.BorderBrush = new SolidColorBrush(WaitingBorderColor);
                border.BorderThickness = new Thickness(2);
            }
            else
            {
                border.BorderBrush = new SolidColorBrush(TransparentColor);
                border.BorderThickness = new Thickness(1);
            }
        }

        if (_paneOverlays.TryGetValue(paneId, out var overlay))
        {
            if (isFocused || !isWaiting)
            {
                overlay.Background = new SolidColorBrush(TransparentColor);
            }
            else
            {
                overlay.Background = new SolidColorBrush(WaitingTintColor);
            }
        }
    }

    public void FocusTerminal(Guid paneId)
    {
        if (_terminalControls.TryGetValue(paneId, out var control))
        {
            control.FocusInput();
        }
    }

    public void ApplySettings()
    {
        double fontSize = App.SettingsManager.Current.TerminalFontSize;
        foreach (var control in _terminalControls.Values)
            control.ApplySettings(fontSize);
    }

    private async Task<FrameworkElement> BuildElement(SplitNode node, SessionManager sessionManager, string workingDirectory)
    {
        if (node.Type == SplitNodeType.Leaf && node.PaneId.HasValue)
        {
            var paneId = node.PaneId.Value;
            var needsDirectorySelection = sessionManager.NeedsDirectorySelection(paneId);
            var agentChoices = sessionManager.GetPendingAgentChoices(paneId);
            var hasPendingOverlay = needsDirectorySelection || agentChoices.Count > 0;

            _terminalControls.TryGetValue(paneId, out var terminal);

            if (!hasPendingOverlay)
            {
                terminal ??= new TerminalControl { PaneId = paneId };
                await sessionManager.EnsureStartedAsync(paneId, workingDirectory);
                var session = sessionManager.GetOrCreateSession(paneId);
                await terminal.AttachSession(session);
                _terminalControls[paneId] = terminal;
            }

            // Tint overlay — semi-transparent layer on top of the terminal
            var tintOverlay = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                IsHitTestVisible = false, // clicks pass through to the terminal
            };
            _paneOverlays[paneId] = tintOverlay;

            var innerGrid = new Grid();
            if (!hasPendingOverlay && terminal != null)
                innerGrid.Children.Add(terminal);
            innerGrid.Children.Add(tintOverlay); // overlay on top

            if (needsDirectorySelection)
            {
                innerGrid.Children.Add(BuildDirectoryLauncherOverlay(paneId));
            }
            else if (agentChoices.Count > 0)
            {
                innerGrid.Children.Add(BuildAgentLauncherOverlay(paneId, agentChoices, sessionManager));
            }

            var wrapper = new Border
            {
                Child = innerGrid,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            };
            wrapper.Tapped += (s, e) =>
            {
                PaneFocused?.Invoke(paneId);
                e.Handled = true;
            };

            // Right-click context menu
            var menu = new MenuFlyout();

            var splitRight = new MenuFlyoutItem { Text = "Split Right", Icon = new SymbolIcon(Symbol.Add) };
            splitRight.KeyboardAcceleratorTextOverride = "Ctrl+Shift+D";
            splitRight.Click += (s, e) => SplitVerticalRequested?.Invoke(paneId);
            menu.Items.Add(splitRight);

            var splitDown = new MenuFlyoutItem { Text = "Split Down", Icon = new SymbolIcon(Symbol.Add) };
            splitDown.KeyboardAcceleratorTextOverride = "Ctrl+Shift+E";
            splitDown.Click += (s, e) => SplitHorizontalRequested?.Invoke(paneId);
            menu.Items.Add(splitDown);

            menu.Items.Add(new MenuFlyoutSeparator());

            var newTab = new MenuFlyoutItem { Text = "New Tab", Icon = new SymbolIcon(Symbol.Document) };
            newTab.KeyboardAcceleratorTextOverride = "Ctrl+Shift+T";
            newTab.Click += (s, e) => NewTabRequested?.Invoke();
            menu.Items.Add(newTab);

            menu.Items.Add(new MenuFlyoutSeparator());

            var changeDir = new MenuFlyoutItem { Text = "Change Directory", Icon = new SymbolIcon(Symbol.Folder) };
            changeDir.Click += (s, e) => ChangeDirectoryRequested?.Invoke(paneId);
            menu.Items.Add(changeDir);

            menu.Items.Add(new MenuFlyoutSeparator());

            var closePane = new MenuFlyoutItem { Text = "Close Pane", Icon = new SymbolIcon(Symbol.Delete) };
            closePane.KeyboardAcceleratorTextOverride = "Ctrl+Shift+W";
            closePane.Click += (s, e) => ClosePaneRequested?.Invoke(paneId);
            menu.Items.Add(closePane);

            wrapper.ContextFlyout = menu;

            _paneBorders[paneId] = wrapper;
            return wrapper;
        }

        var grid = new Grid();

        if (node.Direction == SplitDirection.Vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Pixel) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - node.Ratio, GridUnitType.Star) });

            var first = await BuildElement(node.First!, sessionManager, workingDirectory);
            Grid.SetColumn(first, 0);
            grid.Children.Add(first);

            var splitter = CreateSplitter();
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            var second = await BuildElement(node.Second!, sessionManager, workingDirectory);
            Grid.SetColumn(second, 2);
            grid.Children.Add(second);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Pixel) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - node.Ratio, GridUnitType.Star) });

            var first = await BuildElement(node.First!, sessionManager, workingDirectory);
            Grid.SetRow(first, 0);
            grid.Children.Add(first);

            var splitter = CreateSplitter();
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            var second = await BuildElement(node.Second!, sessionManager, workingDirectory);
            Grid.SetRow(second, 2);
            grid.Children.Add(second);
        }

        return grid;
    }

    private static Border CreateSplitter()
    {
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x49, 0x48, 0x3e)), // Monokai dark line
        };
    }

    private FrameworkElement BuildAgentLauncherOverlay(Guid paneId, IReadOnlyList<TerminalLaunchOption> agentChoices, SessionManager sessionManager)
    {
        var host = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 0x1e, 0x1e, 0x19)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x49, 0x48, 0x3e)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 280,
        };

        var customChoices = agentChoices.Where(option => option.Agent == null).ToArray();
        var builtInChoices = agentChoices.Where(option => option.Agent != null).ToArray();

        host.Child = BuildLaunchChoicePanel();
        return host;

        FrameworkElement BuildLaunchChoicePanel()
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "Launch agent",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Choose which CLI to start in this pane.",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            foreach (var option in builtInChoices)
                buttons.Children.Add(CreateLaunchButton(option, launchOnClick: true));

            if (customChoices.Length > 0)
            {
                var customOption = new TerminalLaunchOption
                {
                    Title = "Custom",
                    Command = "custom"
                };
                var customButton = CreateLaunchButton(customOption, launchOnClick: false);
                customButton.Click += (_, _) => host.Child = BuildCustomChoicePanel();
                buttons.Children.Add(customButton);
            }

            buttons.Children.Add(CreateLaunchButton(TerminalLaunchOption.BuiltIn(AgentCliKind.None, null), launchOnClick: true));

            panel.Children.Add(buttons);
            return panel;
        }

        FrameworkElement BuildCustomChoicePanel()
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
            panel.Children.Add(new TextBlock
            {
                Text = "Custom commands",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Choose which command to start in this pane.",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var commandList = new StackPanel { Spacing = 8 };
            foreach (var option in customChoices)
            {
                var button = new Button
                {
                    Content = option.Title,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(14, 9, 14, 9),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22)),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    CornerRadius = new CornerRadius(8)
                };
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    host.Visibility = Visibility.Collapsed;
                    await sessionManager.LaunchAgentAsync(paneId, option);
                    RefreshRequested?.Invoke();
                };
                commandList.Children.Add(button);
            }

            var backButton = new Button
            {
                Content = "Back",
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(14, 8, 14, 8),
                CornerRadius = new CornerRadius(8)
            };
            backButton.Click += (_, _) => host.Child = BuildLaunchChoicePanel();

            panel.Children.Add(commandList);
            panel.Children.Add(backButton);
            return panel;
        }

        Button CreateLaunchButton(TerminalLaunchOption option, bool launchOnClick)
        {
            var buttonContent = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            buttonContent.Children.Add(CreateAgentLogo(option));
            buttonContent.Children.Add(new TextBlock
            {
                Text = option.Title,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22)),
            });

            var button = new Button
            {
                Content = buttonContent,
                Padding = new Thickness(16, 10, 16, 10),
                Background = new SolidColorBrush(GetAgentButtonColor(option)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CornerRadius = new CornerRadius(8),
                MinWidth = 88
            };

            if (launchOnClick)
            {
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    host.Visibility = Visibility.Collapsed;
                    await sessionManager.LaunchAgentAsync(paneId, option);
                    RefreshRequested?.Invoke();
                };
            }

            return button;
        }
    }

    private FrameworkElement BuildDirectoryLauncherOverlay(Guid paneId)
    {
        var host = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 0x1e, 0x1e, 0x19)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x49, 0x48, 0x3e)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18, 20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 320,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose working directory",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Pick the folder for this first pane before launching an agent.",
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e)),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 260
        });

        var chooseButton = new Button
        {
            Content = "Choose Folder",
            Padding = new Thickness(16, 10, 16, 10),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x66, 0xd9, 0xef)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        chooseButton.Click += (s, e) => InitialDirectorySelectionRequested?.Invoke(paneId);

        panel.Children.Add(chooseButton);
        host.Child = panel;
        return host;
    }

    private static FrameworkElement CreateAgentLogo(TerminalLaunchOption option)
    {
        var image = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Source = new BitmapImage(new Uri(GetAgentLogoUri(option.Agent)))
        };

        return image;
    }

    private static string GetAgentLogoUri(AgentCliKind? agent) => agent switch
    {
        AgentCliKind.Claude => "ms-appx:///Assets/claude.png",
        AgentCliKind.Codex => "ms-appx:///Assets/codex.png",
        AgentCliKind.Gemini => "ms-appx:///Assets/gemini.png",
        AgentCliKind.None => "ms-appx:///ico.ico",
        _ => "ms-appx:///ico.ico"
    };

    private static Windows.UI.Color GetAgentButtonColor(TerminalLaunchOption option) => option.Agent switch
    {
        AgentCliKind.Claude => Windows.UI.Color.FromArgb(255, 0xfd, 0x97, 0x1f),
        AgentCliKind.Codex => Windows.UI.Color.FromArgb(255, 0xa6, 0xe2, 0x2e),
        AgentCliKind.Gemini => Windows.UI.Color.FromArgb(255, 0xae, 0x81, 0xff),
        AgentCliKind.None => Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e),
        _ => Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)
    };

}
