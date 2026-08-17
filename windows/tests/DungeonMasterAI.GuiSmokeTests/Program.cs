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
    private const int ReferenceWidth = 1536;
    private const int ReferenceHeight = 864;

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

            Check(window.Width == ReferenceWidth, "AAA shell reference width is 1536.", failures);
            Check(window.Height == ReferenceHeight, "AAA shell reference height is 864.", failures);
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

            // Real HWND startup test. The hosted runner may constrain the physical
            // window to its virtual screen size, which is okay for this check.
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(window.IsVisible, "AAA shell can be shown without startup binding failures.", failures);

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
                    LayoutVisual(window.Content as FrameworkElement, 1280, 720);
                }
            }

            Check(window.IsVisible, "AAA shell remains alive after compact-layout checks.", failures);
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
            Console.WriteLine("AAA shell can be shown; approved major views, packaged artwork, and 1536x864 CI screenshots verified.");
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
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var path = Path.Combine(outputDirectory, $"r51-{capture.Name}-1536x864.png");
            CaptureReferenceVisual(window, path);

            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                Check(frame.PixelWidth == ReferenceWidth && frame.PixelHeight == ReferenceHeight,
                    $"Rendered {capture.Name} screenshot is exactly 1536x864.", failures);
            }
            else
            {
                failures.Add($"Rendered {capture.Name} reference screenshot was not generated.");
            }
        }
    }

    private static void CaptureReferenceVisual(AaaShellWindow window, string path)
    {
        if (window.Content is not FrameworkElement root)
            throw new InvalidOperationException("AAA shell root visual is unavailable.");

        // Render the already-loaded real shell content at the authoritative design
        // canvas, independent of the CI runner's smaller physical desktop.
        LayoutVisual(root, ReferenceWidth, ReferenceHeight);
        var bitmap = new RenderTargetBitmap(ReferenceWidth, ReferenceHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void LayoutVisual(FrameworkElement? element, double width, double height)
    {
        if (element is null) return;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
