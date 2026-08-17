using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = new AaaShellWindow();

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

            Layout(window, 1536, 864);
            var tabs = FindVisualDescendant<TabControl>(window);
            Check(tabs is not null, "Main navigation TabControl exists.", failures);
            if (tabs is not null)
            {
                Check(tabs.Items.Count == 10, "Main navigation contains the 10 approved destinations.", failures);
                var requiredTypes = new[]
                {
                    typeof(HomeView),
                    typeof(LivePlayView),
                    typeof(CharactersView),
                    typeof(WorldView)
                };

                foreach (var requiredType in requiredTypes)
                    Check(FindVisualDescendant(window, requiredType) is not null,
                        $"Approved view {requiredType.Name} constructs in the shell.", failures);

                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    Layout(window, 1536, 864);
                }
            }

            Layout(window, 1280, 720);
            Check(window.ActualWidth >= 0 && window.ActualHeight >= 0,
                "AAA shell completes compact-layout measurement.", failures);
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled GUI construction exception: {ex}");
        }
        finally
        {
            try { window?.Close(); } catch { }
            try { application?.Shutdown(); } catch { }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("GUI SMOKE PASS");
            Console.WriteLine("AAA shell, approved major views, and packaged reference artwork verified.");
            return 0;
        }

        Console.Error.WriteLine($"GUI SMOKE FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures)
            Console.Error.WriteLine($" - {failure}");
        return 1;
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

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        return FindVisualDescendant(root, typeof(T)) as T;
    }

    private static DependencyObject? FindVisualDescendant(DependencyObject root, Type type)
    {
        if (type.IsInstanceOfType(root)) return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindVisualDescendant(child, type);
            if (found is not null) return found;
        }
        return null;
    }
}
