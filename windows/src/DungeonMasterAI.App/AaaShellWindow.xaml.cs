using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App;

public partial class AaaShellWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _navigationCollapsed;
    private bool _brandMarkApplied;
    private bool _campaignCrestApplied;
    private bool _vectorChromeApplied;
    private bool _compassApplied;

    public AaaShellWindow() : this(initializeOnLoad: true)
    {
    }

    public AaaShellWindow(bool initializeOnLoad)
    {
        App.LogStartup("AaaShellWindow constructor entered.");
        InitializeComponent();
        MainTabs.TabStripPlacement = Dock.Left;
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        ApplyApprovedStaticChrome();
        ApplyNavigationState();

        if (initializeOnLoad)
            Loaded += OnLoaded;

        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            _viewModel.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.LogStartup("AAA shell loaded; initializing application data.");
            await _viewModel.InitializeAsync();
            App.LogStartup("AAA shell initialization completed successfully.");
        }
        catch (Exception ex)
        {
            App.LogException("AAA shell initialization failed", ex);
            MessageBox.Show(
                $"The redesigned window opened, but application initialization failed.{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}Diagnostic log:{Environment.NewLine}{App.StartupLogPath}",
                "Dungeon Master AI startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void NewSession_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;

    private void SelectLivePlay_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        _navigationCollapsed = !_navigationCollapsed;
        ApplyNavigationState();
    }

    private void ApplyApprovedStaticChrome()
    {
        ApplyApprovedBrandMark();
        ApplyApprovedCampaignSelectorCrest();
        ApplyVectorChrome();
    }

    private void ApplyApprovedBrandMark()
    {
        if (_brandMarkApplied) return;
        var placeholder = FindTextBlock(this, "◈");
        if (placeholder?.Parent is not StackPanel parent) return;

        var index = parent.Children.IndexOf(placeholder);
        if (index < 0) return;
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, new AaaBrandMark
        {
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        _brandMarkApplied = true;
    }

    private void ApplyApprovedCampaignSelectorCrest()
    {
        if (_campaignCrestApplied) return;
        var placeholder = FindTextBlock(this, "♜");
        if (placeholder?.Parent is not Border frame) return;

        frame.Child = new AaaCampaignCrest
        {
            Width = 25,
            Height = 31,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _campaignCrestApplied = true;
    }

    private void ApplyVectorChrome()
    {
        if (_vectorChromeApplied) return;

        var gold = TryFindResource("AaaGoldBrush") as Brush ?? Brushes.BurlyWood;
        var blue = TryFindResource("AaaBlueBrush") as Brush ?? Brushes.CornflowerBlue;
        var ivory = new SolidColorBrush(Color.FromRgb(226, 217, 199));
        ivory.Freeze();

        var navKinds = new[]
        {
            AaaIconKind.Home,
            AaaIconKind.LivePlay,
            AaaIconKind.Combat,
            AaaIconKind.Characters,
            AaaIconKind.World,
            AaaIconKind.Maps,
            AaaIconKind.Quests,
            AaaIconKind.Rules,
            AaaIconKind.Import,
            AaaIconKind.Settings
        };

        var tabItems = MainTabs.Items.OfType<TabItem>().ToArray();
        for (var i = 0; i < Math.Min(tabItems.Length, navKinds.Length); i++)
        {
            if (tabItems[i].Header is not StackPanel header || header.Children.Count == 0) continue;
            header.Children.RemoveAt(0);
            header.Children.Insert(0, new AaaVectorIcon
            {
                Kind = navKinds[i],
                Width = 30,
                Height = 20,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = gold,
                StrokeThickness = 1.45
            });
        }

        ReplaceButtonGlyph("☰", AaaIconKind.Menu, gold, null, 18);
        ReplaceButtonGlyph("▶  New Session", AaaIconKind.Play, blue, "New Session", 16);
        ReplaceButtonGlyph("▤  Add Note", AaaIconKind.Note, ivory, "Add Note", 15);
        ReplaceButtonGlyph("⬡  Quick Roll", AaaIconKind.Dice, ivory, "Quick Roll", 16);
        ReplaceButtonGlyph("✦  Ask AI", AaaIconKind.Spark, blue, "Ask AI", 15);

        _vectorChromeApplied = true;
    }

    private void ReplaceButtonGlyph(string currentContent, AaaIconKind kind, Brush brush, string? label, double iconSize)
    {
        var button = FindButtonByStringContent(this, currentContent);
        if (button is null) return;

        if (label is null)
        {
            button.Content = new AaaVectorIcon
            {
                Kind = kind,
                Width = iconSize,
                Height = iconSize,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return;
        }

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new AaaVectorIcon
        {
            Kind = kind,
            Width = iconSize,
            Height = iconSize,
            Foreground = brush,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Georgia"),
            FontSize = 12,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center
        });
        button.Content = content;
    }

    private void ApplyNavigationState()
    {
        MainTabs.ApplyTemplate();
        ApplyApprovedCompass();
        var layoutGrid = FindNavigationLayoutGrid(MainTabs);
        if (layoutGrid is not null)
            layoutGrid.ColumnDefinitions[0].Width = new GridLength(_navigationCollapsed ? 64 : 198);

        foreach (var item in MainTabs.Items.OfType<TabItem>())
        {
            item.Padding = _navigationCollapsed ? new Thickness(10, 13, 8, 13) : new Thickness(18, 13, 18, 13);
            item.Margin = _navigationCollapsed ? new Thickness(5, 2, 5, 2) : new Thickness(8, 2, 8, 2);

            if (item.Header is not StackPanel header) continue;
            var label = header.Children.OfType<TextBlock>().LastOrDefault();
            if (label is not null)
                label.Visibility = _navigationCollapsed ? Visibility.Collapsed : Visibility.Visible;

            var icon = header.Children.OfType<AaaVectorIcon>().FirstOrDefault();
            if (icon is not null)
            {
                icon.Width = _navigationCollapsed ? 25 : 30;
                icon.Margin = _navigationCollapsed ? new Thickness(0) : new Thickness(0, 0, 4, 0);
            }
        }

        ToolTipService.SetToolTip(MainTabs,
            _navigationCollapsed ? "Expand navigation" : "Collapse navigation");
    }

    private void ApplyApprovedCompass()
    {
        if (_compassApplied) return;
        var placeholder = FindLargeTextBlock(this, "✦", 40);
        if (placeholder?.Parent is not Grid compassGrid) return;

        compassGrid.Children.Clear();
        compassGrid.Children.Add(new AaaArcaneCompass
        {
            Width = 132,
            Height = 132,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        _compassApplied = true;
    }

    private static Button? FindButtonByStringContent(DependencyObject root, string content)
    {
        if (root is Button button && button.Content is string text && text == content) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindButtonByStringContent(VisualTreeHelper.GetChild(root, i), content);
            if (found is not null) return found;
        }
        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindTextBlock(VisualTreeHelper.GetChild(root, i), text);
            if (found is not null) return found;
        }
        return null;
    }

    private static TextBlock? FindLargeTextBlock(DependencyObject root, string text, double minimumFontSize)
    {
        if (root is TextBlock block && block.Text == text && block.FontSize >= minimumFontSize) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindLargeTextBlock(VisualTreeHelper.GetChild(root, i), text, minimumFontSize);
            if (found is not null) return found;
        }
        return null;
    }

    private static Grid? FindNavigationLayoutGrid(DependencyObject root)
    {
        if (root is Grid grid
            && grid.ColumnDefinitions.Count == 2
            && grid.ColumnDefinitions[1].Width.IsStar
            && grid.ColumnDefinitions[0].Width.IsAbsolute
            && Math.Abs(grid.ColumnDefinitions[0].Width.Value - 198) < 1)
            return grid;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindNavigationLayoutGrid(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}
