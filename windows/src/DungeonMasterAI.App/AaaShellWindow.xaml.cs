using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
        // Views bind directly to the authoritative MainViewModel.  This hook is
        // intentionally kept for shell-wide polish such as toast notifications.
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
        // The approved renders use a 198px full rail.  Until the compact icon-only
        // rail is finished, keep the visual geometry stable rather than distorting
        // the content.  The state is retained so the next shell pass can animate it.
        ToolTipService.SetToolTip(MainTabs,
            _navigationCollapsed ? "Navigation compact mode queued" : "Navigation expanded");
    }
}
