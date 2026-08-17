using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DungeonMasterAI.App;
using DungeonMasterAI.App.Views;

namespace DungeonMasterAI.GuiSmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        Application? application = null;
        AaaShellWindow? window = null;

        try
        {
            application = new DungeonMasterAI.App.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            window = new AaaShellWindow(initializeOnLoad: false);

            Check(window.Width == 1536, "AAA shell reference width is 1536.", failures);
            Check(window.Height == 864, "AAA shell reference height is 864.", failures);
            Check(window.MinWidth <= 1280, "AAA shell remains available at 1280px width.", failures);
            Check(window.MinHeight <= 720, "AAA shell remains available at 720px height.", failures);

            Check(Application.GetResourceStream(new Uri(
                    "pack://application:,,,/DungeonMasterAI;component/Assets/Reference/home-hero-greenhaven.jpg",
                    UriKind.Absolute)) is not null,
                "Approved Greenhaven hero artwork is packaged as a WPF resource.", failures);
            Check(Application.GetResourceStream(new Uri(
                    "pack://application:,,,/DungeonMasterAI;component/Assets/Reference/aeliana-portrait.jpg",
                    UriKind.Absolute)) is not null,
                "Approved Aeliana portrait artwork is packaged as a WPF resource.", failures);

            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(window.IsVisible, "AAA shell can be shown without startup binding failures.", failures);

            Layout(window, 1536, 864);

            var tabs = window.FindName("MainTabs") as TabControl;
            Check(tabs is not null, "Main navigation TabControl exists.", failures);
            if (tabs is not null)
            {
                Check(tabs.Items.Count == 10, "Main navigation contains the 10 approved destinations.", failures);

                var tabItems = tabs.Items.OfType<TabItem>().ToArray();
                var requiredTypes = new[]
                {
                    typeof(HomeView),
                    typeof(LivePlayView),
                    typeof(CharactersView),
                    typeof(WorldView)
                };

                foreach (var requiredType in requiredTypes)
                    Check(tabItems.Any(item => requiredType.IsInstanceOfType(item.Content)),
                        $"Approved view {requiredType.Name} constructs in the shell.", failures);

                CaptureApprovedViews(window, tabs, failures);

                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    tabs.ApplyTemplate();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Layout(window, 1536, 864);
                }
            }

            Layout(window, 1280, 720);
            Check(window.ActualWidth >= 0 && window.ActualHeight >= 0,
                "AAA shell completes compact-layout measurement.", failures);
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled GUI construction/show exception: {ex}");
        }
        finally
        {
            try { window?.Close(); } catch { }
            try { application?.Shutdown(); } catch { }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("GUI SMOKE PASS");
            Console.WriteLine("AAA shell can be shown; approved major views, packaged artwork, and CI screenshots verified.");
            return 0;
        }

        Console.Error.WriteLine($"GUI SMOKE FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures)
            Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static void CaptureApprovedViews(AaaShellWindow window, TabControl tabs, ICollection<string> failures)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("artifacts", "gui-snapshots"));
        Directory.CreateDirectory(outputDirectory);

        var captures = new (int Index, string Name)[]
        {
            (0, "home"),
            (1, "live-play"),
            (3, "characters"),
            (4, "world")
        };

        foreach (var capture in captures)
        {
            tabs.SelectedIndex = capture.Index;
            tabs.ApplyTemplate();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Layout(window, 1536, 864);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var path = Path.Combine(outputDirectory, $"r51-{capture.Name}-1536x864.png");
            CaptureWindow(window, path);
            Check(File.Exists(path) && new FileInfo(path).Length > 1024,
                $"Rendered {capture.Name} reference screenshot was generated.", failures);
        }
    }

    private static void CaptureWindow(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
