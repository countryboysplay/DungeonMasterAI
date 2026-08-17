using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DungeonMasterAI.App;

public partial class App : Application
{
    private static readonly object LogGate = new();
    public static string StartupLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DungeonMasterAI",
        "Logs",
        "startup.log");

    public App()
    {
        // Major views are constructed before they are fully parented into the shell.
        // Keep the approved AAA theme at application scope so every UserControl can
        // resolve its StaticResource references during InitializeComponent().
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/AaaTheme.xaml", UriKind.Relative)
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            LogStartup($"Application starting. BaseDirectory={AppContext.BaseDirectory}");
            var window = new DungeonMasterAI.App.AaaShellWindow();
            MainWindow = window;
            window.Show();
            LogStartup("AAA shell window shown.");
        }
        catch (Exception ex)
        {
            LogException("Fatal startup exception before the main window could be shown", ex);
            ShowStartupFailure("Dungeon Master AI could not start.", ex);
            Shutdown(-1);
        }
    }

    public static void LogStartup(string message)
    {
        try
        {
            lock (LogGate)
            {
                var directory = Path.GetDirectoryName(StartupLogPath)!;
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    StartupLogPath,
                    $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become a startup dependency.
        }
    }

    public static void LogException(string context, Exception exception)
    {
        LogStartup($"{context}{Environment.NewLine}{exception}");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("Unhandled WPF dispatcher exception", e.Exception);
        ShowStartupFailure("Dungeon Master AI encountered an unexpected UI error.", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException($"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}", ex);
        else
            LogStartup($"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}. Value={e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static void ShowStartupFailure(string heading, Exception exception)
    {
        try
        {
            MessageBox.Show(
                $"{heading}{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}Diagnostic log:{Environment.NewLine}{StartupLogPath}",
                "Dungeon Master AI",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // If WPF itself cannot display a dialog, the startup log is still our fallback.
        }
    }
}
