using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DungeonMasterAI.App.Controls;

namespace DungeonMasterAI.App;

public partial class AaaShellWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _navigationCollapsed;
    private bool _brandMarkApplied;
    private bool _campaignCrestApplied;

    public AaaShellWindow() : this(initializeOnLoad: true)
    {
    }

    public AaaShellWindow(bool initializeOnLoad)
    {
        App.LogStartup("AaaShellWindow constructor entered.");
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        if (initializeOnLoad)
            Loaded += OnLoaded;

        Loaded += (_, _) =>
        {
            ApplyApprovedBrandMark();
            ApplyApprovedCampaignCrest();
            ApplyNavigationState();
        };

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
        // Major screens bind directly to the authoritative MainViewModel.
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

    private void ApplyApprovedCampaignCrest()
    {
        if (_campaignCrestApplied) return;
        var placeholder = FindTextBlock(this, "♜");
        if (placeholder?.Parent is not Border border) return;

        var viewbox = new Viewbox { Margin = new Thickness(5) };
        var grid = new Grid { Width = 30, Height = 34 };
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse("M15,1 L28,6 L26,23 C24,28 20,31 15,33 C10,31 6,28 4,23 L2,6 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(11, 18, 16)),
            Stroke = new SolidColorBrush(Color.FromRgb(168, 139, 84)),
            StrokeThickness = 1.2
        });
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse("M15,7 C13,11 11,13 8,16 M15,7 C17,11 19,13 22,16 M15,7 L15,25 M10,23 C12,20 13,18 15,15 C17,18 18,20 20,23 M8,16 C10,17 12,17 15,15 C18,17 20,17 22,16"),
            Stroke = new SolidColorBrush(Color.FromRgb(181, 193, 157)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent
        });
        viewbox.Child = grid;
        border.Child = viewbox;
        border.Width = 40;
        border.Height = 42;
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(121, 97, 61));
        _campaignCrestApplied = true;
    }

    private void ApplyNavigationState()
    {
        MainTabs.ApplyTemplate();
        var layoutGrid = FindNavigationLayoutGrid(MainTabs);
        if (layoutGrid is not null)
            layoutGrid.ColumnDefinitions[0].Width = new GridLength(_navigationCollapsed ? 64 : 198);

        foreach (var item in MainTabs.Items.OfType<TabItem>())
        {
            item.Padding = _navigationCollapsed ? new Thickness(10, 13, 8, 13) : new Thickness(18, 13, 18, 13);
            item.Margin = _navigationCollapsed ? new Thickness(5, 2, 5, 2) : new Thickness(8, 2, 8, 2);

            if (item.Header is not StackPanel header) continue;
            var textBlocks = header.Children.OfType<TextBlock>().ToArray();
            if (textBlocks.Length < 2) continue;
            textBlocks[1].Visibility = _navigationCollapsed ? Visibility.Collapsed : Visibility.Visible;
            textBlocks[0].Width = _navigationCollapsed ? 26 : 30;
            textBlocks[0].TextAlignment = TextAlignment.Center;
        }

        ToolTipService.SetToolTip(MainTabs,
            _navigationCollapsed ? "Expand navigation" : "Collapse navigation");
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
