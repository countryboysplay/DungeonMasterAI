using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DungeonMasterAI.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        App.LogStartup("MainWindow constructor entered.");
        InitializeComponent();
        App.LogStartup("MainWindow XAML initialized.");

        _viewModel = new MainViewModel();
        App.LogStartup("MainViewModel constructed.");
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
            App.LogStartup("MainWindow Loaded event entered; initializing application data.");
            await _viewModel.InitializeAsync();
            App.LogStartup("MainViewModel initialization completed successfully.");
            ScrollSessionToLatest();
        }
        catch (Exception ex)
        {
            App.LogException("MainViewModel initialization failed", ex);
            MessageBox.Show(
                $"The window opened, but application initialization failed.{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}Diagnostic log:{Environment.NewLine}{App.StartupLogPath}",
                "Dungeon Master AI startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void PlayerInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (_viewModel.SendPlayerInputCommand.CanExecute(null))
            _viewModel.SendPlayerInputCommand.Execute(null);
        e.Handled = true;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SessionChat) or nameof(MainViewModel.SelectedCampaign))
            ScrollSessionToLatest();
    }

    private void ScrollSessionToLatest()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (SessionChatList.Items.Count > 0)
                SessionChatList.ScrollIntoView(SessionChatList.Items[SessionChatList.Items.Count - 1]);
        }, DispatcherPriority.Background);
    }
}
