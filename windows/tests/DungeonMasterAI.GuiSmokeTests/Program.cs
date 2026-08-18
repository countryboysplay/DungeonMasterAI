using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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
            application = new DungeonMasterAI.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
            FlushUi(window);
            Check(window.IsVisible, "AAA shell can be shown without startup binding failures.", failures);

            PopulateGreenhavenPreview(window, failures);
            FlushUi(window);

            var tabs = window.FindName("MainTabs") as TabControl;
            Check(tabs is not null, "Main navigation TabControl exists.", failures);
            if (tabs is not null)
            {
                Check(tabs.Items.Count == 10, "Main navigation contains the 10 approved destinations.", failures);

                var tabItems = tabs.Items.OfType<TabItem>().ToArray();
                var requiredTypes = new[] { typeof(HomeView), typeof(LivePlayView), typeof(CharactersView), typeof(WorldView) };
                foreach (var requiredType in requiredTypes)
                    Check(tabItems.Any(item => requiredType.IsInstanceOfType(item.Content)),
                        $"Approved view {requiredType.Name} constructs in the shell.", failures);

                VerifyCommandReachability(tabs, failures);

                CaptureApprovedViews(window, tabs, failures);

                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    tabs.ApplyTemplate();
                    FlushUi(window);
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

        var imported = new CampaignImportService().ImportManifestJson(File.ReadAllText(samplePath), Path.GetFileName(samplePath));
        var campaign = imported.Campaign;
        campaign.Name = "Greenhaven";
        campaign.Day = 2;
        campaign.MinuteOfDay = 19 * 60 + 43;
        foreach (var location in campaign.Locations.Where(l => !l.DmOnly)) location.Discovered = true;

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

        Check(viewModel.SelectedCampaign is not null, "Direct-import Greenhaven preview selects a campaign.", failures);
        Check(viewModel.SelectedEncounter is not null, "Direct-import Greenhaven preview selects an encounter.", failures);
    }

    /// <summary>
    /// Commands the shell deliberately invokes from code-behind instead of binding directly,
    /// because the click handler must synchronise view-model selection state first. Each entry
    /// needs a comment naming the handler that executes it.
    /// </summary>
    private static readonly string[] CodeBehindInvokedCommands =
    [
        // CombatView.CastSpell_Click copies the combat spell/target selection onto the
        // spellcasting properties, then executes this command.
        nameof(MainViewModel.CastSelectedSpellCommand)
    ];

    /// <summary>
    /// Asserts every public <see cref="ICommand"/> on <see cref="MainViewModel"/> is reachable from
    /// the live shell.
    ///
    /// This gate cannot live in tools/validate_source.py: that script scans every XAML file in the
    /// repository, including the legacy MainWindow.xaml, which binds all commands but is not hosted
    /// by the shell. It therefore reports full coverage even when a command is unreachable in the
    /// shipped UI. Walking the real tab tree is the only reliable check.
    /// </summary>
    private static void VerifyCommandReachability(TabControl tabs, ICollection<string> failures)
    {
        var expected = typeof(MainViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Check(expected.Count > 0, "MainViewModel exposes discoverable ICommand properties.", failures);

        var viewModel = tabs.DataContext as MainViewModel;
        Check(viewModel is not null, "Navigation host inherits the MainViewModel data context.", failures);

        // Map each command instance back to its property name so buttons wired by assignment
        // (rather than by binding) still count as reachable.
        var instanceToName = new Dictionary<ICommand, string>(CommandReferenceComparer.Instance);
        if (viewModel is not null)
        {
            foreach (var property in typeof(MainViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)))
            {
                if (property.GetValue(viewModel) is ICommand command)
                    instanceToName[command] = property.Name;
            }
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tabItem in tabs.Items.OfType<TabItem>())
        {
            if (tabItem.Content is not DependencyObject content) continue;
            CollectReachableCommands(content, instanceToName, reachable);
        }

        foreach (var allowed in CodeBehindInvokedCommands)
        {
            Check(expected.Contains(allowed),
                $"Code-behind command allowlist entry {allowed} still exists on MainViewModel.", failures);
            reachable.Add(allowed);
        }

        var unreachable = expected.Except(reachable, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Check(unreachable.Length == 0,
            unreachable.Length == 0
                ? "Every MainViewModel command is reachable from a shell destination."
                : $"Every MainViewModel command is reachable from a shell destination. Unreachable: {string.Join(", ", unreachable)}",
            failures);
    }

    /// <summary>
    /// Walks the logical tree collecting command names, both from bindings on
    /// <see cref="ButtonBase.CommandProperty"/> and from directly assigned command instances.
    /// Buttons inside a DataTemplate are not in the logical tree until items are generated, so
    /// command buttons must stay outside item templates.
    /// </summary>
    private static void CollectReachableCommands(
        DependencyObject node,
        IReadOnlyDictionary<ICommand, string> instanceToName,
        ISet<string> reachable)
    {
        if (node is ICommandSource source)
        {
            if (BindingOperations.GetBinding(node, ButtonBase.CommandProperty)?.Path?.Path is { Length: > 0 } path)
                reachable.Add(path.Split('.')[^1]);
            else if (BindingOperations.GetBinding(node, MenuItem.CommandProperty)?.Path?.Path is { Length: > 0 } menuPath)
                reachable.Add(menuPath.Split('.')[^1]);

            if (source.Command is { } assigned && instanceToName.TryGetValue(assigned, out var name))
                reachable.Add(name);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyChild)
                CollectReachableCommands(dependencyChild, instanceToName, reachable);
        }
    }

    /// <summary>Compares commands by reference so a command cannot be matched by value equality.</summary>
    private sealed class CommandReferenceComparer : IEqualityComparer<ICommand>
    {
        internal static readonly CommandReferenceComparer Instance = new();

        public bool Equals(ICommand? left, ICommand? right) => ReferenceEquals(left, right);

        public int GetHashCode(ICommand command) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(command);
    }

    private static void FlushUi(DispatcherObject dispatcherOwner)
    {
        dispatcherOwner.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        dispatcherOwner.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        dispatcherOwner.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void CaptureApprovedViews(AaaShellWindow window, TabControl tabs, ICollection<string> failures)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("artifacts", "gui-snapshots"));
        Directory.CreateDirectory(outputDirectory);

        var captures = new (int Index, string Name)[]
        {
            (0, "home"), (1, "live-play"), (3, "characters"), (4, "world")
        };

        foreach (var capture in captures)
        {
            tabs.SelectedIndex = capture.Index;
            tabs.ApplyTemplate();
            FlushUi(window);

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
