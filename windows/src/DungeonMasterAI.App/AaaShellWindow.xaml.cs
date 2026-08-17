using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DungeonMasterAI.App;

public partial class AaaShellWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _navigationCollapsed;

    public AaaShellWindow()
    {
        App.LogStartup("AaaShellWindow constructor entered.");
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

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
            ApplyNavigationState();
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
