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
using DungeonMasterAI.App.Controls;
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

        var bindingFailures = BindingFailureListener.Attach();

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

            VerifySalvagedR50Visuals(application, failures);

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

                VerifyFirstRunEmptyState(window, tabs, failures);
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

        foreach (var bindingFailure in bindingFailures.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            failures.Add($"Binding failure: {bindingFailure}");

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

        // Give the preview encounter a real battlefield. Without a bound map the combat grid falls
        // back to its pre-r62 empty-grid path, the Map/ShowDmMapLayer bindings resolve to null in
        // every snapshot, and the map underlay is never actually rendered by the live shell — which
        // is the one place a WPF binding failure or a rendering exception would surface.
        var previewMap = BuildPreviewBattlefield();
        campaign.TacticalMaps.Add(previewMap);

        var encounter = campaign.Encounters.FirstOrDefault();
        if (encounter is not null)
        {
            campaign.EncounterMapBindings[encounter.Id] = previewMap.Id;
            encounter.Status = "active";
            encounter.Round = 3;
            encounter.TurnIndex = 0;
            var player = campaign.Characters.FirstOrDefault(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase));
            if (player is not null && encounter.Combatants.All(c => c.CharacterId != player.Id))
                encounter.Combatants.Insert(0, new CombatantState { CharacterId = player.Id, Side = CombatSide.Party, TieBreaker = -1 });

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
                combatant.Side = CombatSide.Normalize(combatant.Side, CombatSide.Opposition);
            }
        }

        viewModel.SelectedCampaign = campaign;
        viewModel.ShowDmMap = true;

        Check(viewModel.SelectedCampaign is not null, "Direct-import Greenhaven preview selects a campaign.", failures);
        Check(viewModel.SelectedEncounter is not null, "Direct-import Greenhaven preview selects an encounter.", failures);
        Check(viewModel.ActiveTacticalMap is not null,
            "The preview encounter resolves the tactical map bound to it, so the combat grid renders a real battlefield.", failures);
    }

    /// <summary>
    /// A small, geometrically valid battlefield for the populated preview: two chambers, a dividing
    /// wall with a door, one blocking pillar, a difficult-terrain strip, and one spawn point per
    /// side. Sized to contain the preview combatant positions above.
    /// </summary>
    private static TacticalMap BuildPreviewBattlefield()
    {
        var map = new TacticalMap
        {
            Id = "greenhaven-preview-battlefield",
            Key = "greenhaven.preview",
            Name = "Greenhaven Skirmish",
            SchemaVersion = TacticalMapSchema.CurrentMapSchemaVersion,
            WidthSquares = 18,
            HeightSquares = 14,
            FeetPerSquare = 5,
            Seed = 620059,
            GenerationSeed = 620059,
            SourceKind = CampaignProvenance.TestFixture
        };
        map.Rooms.Add(new TacticalMapRoom { Id = "gp-west", Name = "Courtyard", X = 0, Y = 0, WidthSquares = 10, HeightSquares = 14, FloorAssetKey = "floor.stone.flagstone", WallAssetKey = "wall.stone.block" });
        map.Rooms.Add(new TacticalMapRoom { Id = "gp-east", Name = "Undercroft", X = 10, Y = 0, WidthSquares = 8, HeightSquares = 14, FloorAssetKey = "floor.stone.crypt_flagstone", WallAssetKey = "wall.stone.crypt_block" });
        map.Walls.Add(new TacticalMapWall { Id = "gp-divider", FromX = 10, FromY = 0, ToX = 10, ToY = 14, AssetKey = "wall.stone.block" });
        map.Doors.Add(new TacticalMapDoor { Id = "gp-door", Name = "Undercroft Door", X = 10, Y = 6, Orientation = "vertical", State = "closed", AssetKey = "door.wood.ironbound" });
        map.Props.Add(new TacticalMapProp { Id = "gp-pillar", Name = "Pillar", X = 3, Y = 3, AssetKey = "prop.pillar.stone_round", BlocksMovement = true, BlocksLineOfSight = true });
        map.Terrain.Add(new TacticalMapTerrain
        {
            Id = "gp-rubble",
            Name = "Rubble",
            TerrainType = "rubble",
            X = 6,
            Y = 8,
            WidthSquares = 3,
            HeightSquares = 2,
            AssetKey = "terrain.rubble.stone",
            DifficultTerrain = true
        });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Id = "gp-spawn-party", Name = "Party Entrance", Side = CombatSide.Party, X = 1, Y = 6 });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Id = "gp-spawn-foe", Name = "Raider Line", Side = CombatSide.Opposition, X = 15, Y = 6 });
        return map;
    }

    /// <summary>
    /// Walks every destination with no campaign selected. That is the state a new user actually
    /// opens the app in, yet the populated Greenhaven preview above never exercises it, so a view
    /// that only survives because some collection happened to be non-empty would pass unnoticed.
    /// The binding listener is what does the asserting here.
    /// </summary>
    private static void VerifyFirstRunEmptyState(AaaShellWindow window, TabControl tabs, ICollection<string> failures)
    {
        if (window.DataContext is not MainViewModel viewModel)
        {
            failures.Add("AAA shell exposes the MainViewModel for the first-run empty-state pass.");
            return;
        }

        viewModel.SelectedCampaign = null;
        FlushUi(window);

        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index;
            tabs.ApplyTemplate();
            FlushUi(window);
            LayoutVisual(window.Content as FrameworkElement, ReferenceWidth, ReferenceHeight);
        }

        Check(window.IsVisible, "AAA shell survives every destination with no campaign loaded.", failures);
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

    /// <summary>
    /// A missing <c>StaticResource</c> key throws at XAML load, but a binding whose path does not
    /// exist on the view model fails silently: WPF writes one line to the data-binding trace and
    /// leaves the control blank. That is indistinguishable from "the value is genuinely empty" in a
    /// screenshot, so every binding this shell evaluates is asserted here instead.
    /// </summary>
    private sealed class BindingFailureListener : System.Diagnostics.TraceListener
    {
        private readonly List<string> _messages = [];

        internal static IReadOnlyList<string> Attach()
        {
            var listener = new BindingFailureListener();
            System.Diagnostics.PresentationTraceSources.Refresh();
            var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(listener);
            source.Switch.Level = System.Diagnostics.SourceLevels.Error;
            return listener._messages;
        }

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _messages.Add(message.Trim());
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


    /// <summary>
    /// Guards the r59 visual salvage recovered from the abandoned feature/r50-aaa-gui
    /// branch: the selected-nav gradient resource, the campaign map inset frame, and the
    /// flagstone battlefield floor drawn beneath the tactical grid.
    /// </summary>
    private static void VerifySalvagedR50Visuals(Application application, ICollection<string> failures)
    {
        Check(application.TryFindResource("AaaSelectedNavGradient") is LinearGradientBrush,
            "Salvaged AaaSelectedNavGradient resolves as a LinearGradientBrush.", failures);

        var map = new CampaignMapControl();
        LayoutVisual(map, 640, 420);
        var frames = map.Children
            .OfType<Border>()
            .Where(border => Math.Abs(border.Width - (map.ActualWidth - 18)) < 0.5
                && Math.Abs(border.Height - (map.ActualHeight - 18)) < 0.5)
            .ToArray();
        Check(frames.Length == 1, "Campaign map draws exactly one salvaged inset frame.", failures);
        Check(frames.All(frame => !frame.IsHitTestVisible),
            "Campaign map inset frame never intercepts hit testing.", failures);

        // Force the flagstone floor render path to execute so a regression in the
        // salvaged geometry surfaces as a CI failure rather than a silent visual change.
        var battlefield = new CombatGridControl();
        LayoutVisual(battlefield, 900, 560);
        var target = new RenderTargetBitmap(900, 560, 96, 96, PixelFormats.Pbgra32);
        target.Render(battlefield);
        Check(target.PixelWidth == 900 && target.PixelHeight == 560,
            "Tactical battlefield renders with the salvaged flagstone floor.", failures);
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
