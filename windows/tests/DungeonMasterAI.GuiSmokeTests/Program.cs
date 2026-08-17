using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DungeonMasterAI.App;
using DungeonMasterAI.App.Views;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;

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

            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(window.IsVisible, "AAA shell can be shown without startup binding failures.", failures);

            PopulateGreenhavenPreview(window, failures);

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
            Console.WriteLine("AAA shell can be shown; populated Greenhaven views, packaged artwork, and 1536x864 CI screenshots verified.");
            return 0;
        }

        Console.Error.WriteLine($"GUI SMOKE FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures)
            Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static void PopulateGreenhavenPreview(AaaShellWindow window, ICollection<string> failures)
    {
        if (window.DataContext is not MainViewModel viewModel)
        {
            failures.Add("AAA shell exposes the authoritative MainViewModel for populated preview rendering.");
            return;
        }

        var samplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sample", "sample_campaign_manifest.json");
        if (!File.Exists(samplePath))
        {
            failures.Add($"Included Greenhaven sample exists at runtime path: {samplePath}");
            return;
        }

        var manifestJson = File.ReadAllText(samplePath);
        var imported = new CampaignImportService().ImportManifestJson(manifestJson, Path.GetFileName(samplePath));
        var campaign = imported.Campaign;
        campaign.Name = "Greenhaven";
        campaign.Day = 2;
        campaign.MinuteOfDay = 19 * 60 + 43;
        foreach (var location in campaign.Locations.Where(l => !l.DmOnly))
            location.Discovered = true;

        var encounter = campaign.Encounters.FirstOrDefault();
        if (encounter is not null)
        {
            encounter.Status = "active";
            encounter.Round = 3;
            encounter.TurnIndex = 0;
            var player = campaign.Characters.FirstOrDefault(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase));
            if (player is not null && encounter.Combatants.All(c => c.CharacterId != player.Id))
                encounter.Combatants.Insert(0, new CombatantState { CharacterId = player.Id, Side = "player", TieBreaker = -1 });

            var positions = new (int X, int Y)[] { (4, 6), (9, 4), (11, 7), (8, 9), (5, 10), (12, 10) };
            for (var i = 0; i < encounter.Combatants.Count; i++)
            {
                var combatant = encounter.Combatants[i];
                var position = positions[i % positions.Length];
                combatant.Positioned = true;
                combatant.GridX = position.X;
                combatant.GridY = position.Y;
                combatant.Initiative = Math.Max(1, 18 - i * 2);
                combatant.MovementRemainingFeet = 30;
                if (string.IsNullOrWhiteSpace(combatant.Side)) combatant.Side = "enemy";
            }
        }

        viewModel.SelectedCampaign = campaign;
        viewModel.ShowDmMap = true;
        PumpDispatcher(TimeSpan.FromMilliseconds(180));

        Check(viewModel.SelectedCampaign is not null, "Direct-import Greenhaven preview selects a campaign.", failures);
        Check(viewModel.SelectedEncounter is not null, "Direct-import Greenhaven preview selects an encounter.", failures);
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
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
            PumpDispatcher(TimeSpan.FromMilliseconds(120));

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
