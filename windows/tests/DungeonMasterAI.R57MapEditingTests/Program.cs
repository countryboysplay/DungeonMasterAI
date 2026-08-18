using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DungeonMasterAI.App;
using DungeonMasterAI.App.Views;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.R57MapEditingTests;

internal static class Program
{
    private const int Width = 1536;
    private const int Height = 864;

    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        Application? app = null;
        AaaShellWindow? window = null;
        try
        {
            app = new DungeonMasterAI.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = new AaaShellWindow(initializeOnLoad: false);
            var vm = (MainViewModel)window.DataContext;
            var campaign = new CampaignState { Name = "R57 Editing Test" };
            var map = BuildMap();
            campaign.TacticalMaps.Add(map);
            vm.SelectedCampaign = campaign;
            vm.SelectedCampaignMap = map;
            vm.InitializeMapWorkspace();

            vm.BeginMapEditCommand.Execute(null);
            Check(vm.MapEditDraft is not null && !ReferenceEquals(vm.MapEditDraft, map), "Saved map editing starts from an isolated working copy.", failures);
            Check(ReferenceEquals(campaign.TacticalMaps[0], map), "Opening the editor does not replace campaign state.", failures);

            var originalSeed = vm.MapEditDraft!.Seed;
            var roomGeometry = vm.MapEditDraft.Rooms.Select(r => (r.X, r.Y, r.WidthSquares, r.HeightSquares)).ToArray();
            var doorGeometry = vm.MapEditDraft.Doors.Select(d => (d.X, d.Y, d.Orientation)).ToArray();
            vm.RerollMapVisualsCommand.Execute(null);
            Check(vm.MapEditDraft.Seed != originalSeed, "Visual reroll changes the renderer seed.", failures);
            Check(vm.MapEditDraft.GenerationSeed == originalSeed, "Visual reroll preserves the original generation seed.", failures);
            Check(roomGeometry.SequenceEqual(vm.MapEditDraft.Rooms.Select(r => (r.X, r.Y, r.WidthSquares, r.HeightSquares))), "Visual reroll preserves room geometry.", failures);
            Check(doorGeometry.SequenceEqual(vm.MapEditDraft.Doors.Select(d => (d.X, d.Y, d.Orientation))), "Visual reroll preserves door geometry.", failures);

            var savedName = map.Name;
            vm.MapEditDraft.Rooms[0].X = -5;
            vm.ApplyMapEditCommand.Execute(null);
            Check(campaign.TacticalMaps[0].Name == savedName, "Invalid draft is not persisted.", failures);
            Check(vm.MapEditorStatus.Contains("not applied", StringComparison.OrdinalIgnoreCase), "Invalid geometry is visibly rejected.", failures);

            vm.MapEditDraft.Rooms[0].X = 2;
            vm.MapEditDraft.Name = "Reliquary Revised";
            vm.ApplyMapEditCommand.Execute(null);
            Check(campaign.TacticalMaps[0].Name == "Reliquary Revised", "Valid draft replaces the saved map only after Apply Edits.", failures);
            Check(campaign.TacticalMaps[0].GenerationSeed == originalSeed, "Saved edit retains generation seed after visual reroll.", failures);
            WaitFor(() => vm.MapEditorStatus.Contains("saved", StringComparison.OrdinalIgnoreCase), window);

            var candidate = BuildMap();
            candidate.Id = Guid.NewGuid().ToString("N");
            candidate.Name = "Candidate Only";
            SetPrivateProperty(vm, "GeneratedMapCandidate", candidate);
            SetPrivateField(vm, "_generatedMapCampaignId", campaign.Id);
            vm.BeginMapEditCommand.Execute(null);
            var savedCount = campaign.TacticalMaps.Count;
            vm.MapEditDraft!.Name = "Candidate Edited";
            vm.ApplyMapEditCommand.Execute(null);
            WaitFor(() => vm.GeneratedMapCandidate?.Name == "Candidate Edited", window);
            Check(campaign.TacticalMaps.Count == savedCount, "Applying candidate edits does not add or mutate campaign maps.", failures);
            Check(vm.GeneratedMapCandidate?.Name == "Candidate Edited", "Candidate edits return to the review candidate.", failures);

            window.Show();
            Flush(window);
            var tabs = (TabControl?)window.FindName("MainTabs");
            Check(tabs is not null && tabs.Items.OfType<TabItem>().ElementAt(5).Content is MapsView, "Maps navigation still hosts MapsView.", failures);
            if (tabs is not null)
            {
                tabs.SelectedIndex = 5;
                tabs.ApplyTemplate();
                vm.BeginMapEditCommand.Execute(null);
                Flush(window);
            }

            var outputDir = Path.GetFullPath(Path.Combine("artifacts", "r57-map-editing"));
            Directory.CreateDirectory(outputDir);
            var output = Path.Combine(outputDir, "r57-map-editing-1536x864.png");
            Capture(window, output);
            Check(File.Exists(output) && new FileInfo(output).Length > 20_000, "r57 editor renders a non-empty WPF reference image.", failures);
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled r57 editing test exception: {ex}");
        }
        finally
        {
            try { window?.Close(); } catch { }
            try { app?.Shutdown(); } catch { }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("R57 MAP EDITING PASS");
            Console.WriteLine("Working-copy editing, validation gating, visual-only rerolls, candidate isolation, and WPF rendering verified.");
            return 0;
        }
        Console.Error.WriteLine($"R57 MAP EDITING FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
        return 1;
    }

    private static TacticalMap BuildMap()
    {
        var map = new TacticalMap
        {
            Name = "Reliquary", Key = "r57.reliquary", MapType = "dungeon", Theme = "ancient crypt",
            AssetSetId = "core.fantasy.crypt", WidthSquares = 24, HeightSquares = 15, FeetPerSquare = 5,
            // Deliberately uses the legacy provenance alias and omits GenerationSeed so this
            // fixture keeps exercising the pre-unification map shape through the editor.
            Seed = 784211, FogOfWarEnabled = true, SourceKind = CampaignProvenance.LegacyAiGenerated,
            Visibility = new TacticalMapVisibility { RevealAll = false }
        };
        map.Rooms.Add(new TacticalMapRoom { Name = "Entry", X = 2, Y = 3, WidthSquares = 7, HeightSquares = 8, FloorAssetKey = "floor.stone.crypt_flagstone", WallAssetKey = "wall.stone.crypt_block" });
        map.Walls.Add(new TacticalMapWall { FromX = 2, FromY = 3, ToX = 9, ToY = 3, AssetKey = "wall.stone.crypt_block" });
        map.Walls.Add(new TacticalMapWall { FromX = 2, FromY = 11, ToX = 9, ToY = 11, AssetKey = "wall.stone.crypt_block" });
        map.Walls.Add(new TacticalMapWall { FromX = 2, FromY = 3, ToX = 2, ToY = 11, AssetKey = "wall.stone.crypt_block" });
        map.Walls.Add(new TacticalMapWall { FromX = 9, FromY = 3, ToX = 9, ToY = 11, AssetKey = "wall.stone.crypt_block" });
        map.Doors.Add(new TacticalMapDoor { Name = "Entry Door", X = 9, Y = 6, Orientation = "vertical", State = "closed", AssetKey = "door.wood.ironbound" });
        map.Terrain.Add(new TacticalMapTerrain { Name = "Rubble", TerrainType = "rubble", X = 4, Y = 7, WidthSquares = 2, HeightSquares = 2, AssetKey = "terrain.rubble.stone", DifficultTerrain = true });
        map.Props.Add(new TacticalMapProp { Name = "Pillar", X = 6, Y = 6, AssetKey = "prop.pillar.stone_round", BlocksMovement = true });
        map.Lights.Add(new TacticalMapLight { Name = "Torch", AssetKey = "light.torch.wall", X = 5, Y = 4 });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Name = "Party", Side = "player", X = 3, Y = 6 });
        return map;
    }

    private static void SetPrivateProperty(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(name);
        property.SetValue(target, value);
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);
        field.SetValue(target, value);
    }

    private static void WaitFor(Func<bool> condition, DispatcherObject owner)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Flush(owner);
            Thread.Sleep(20);
        }
    }

    private static void Flush(DispatcherObject owner)
    {
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void Capture(AaaShellWindow window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
