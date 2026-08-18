using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DungeonMasterAI.App;
using DungeonMasterAI.App.Controls;
using DungeonMasterAI.App.Views;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.R56MapBuilderTests;

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

            var provision = CoreFantasyMapAssetPackProvisioner.EnsureInstalled();
            Check(provision.Success, "First-party HD map asset pack provisions successfully.", failures);
            Check(provision.AssetFileCount == 17, "First-party HD pack contains exactly 17 raster image files.", failures);
            Check(Directory.Exists(provision.PackDirectory), "First-party HD map pack directory exists.", failures);

            var catalog = new TacticalMapAssetCatalog();
            var pack = catalog.Packs.FirstOrDefault(item => item.PackId.Equals(CoreFantasyMapAssetPackProvisioner.PackId, StringComparison.OrdinalIgnoreCase));
            Check(pack is not null, "Production asset catalog discovers core.fantasy.crypt.", failures);
            if (pack is not null)
            {
                Check(pack.Version == CoreFantasyMapAssetPackProvisioner.PackVersion, "Production catalog prefers the locally provisioned v2 HD pack over the packaged fallback manifest.", failures);
                Check(pack.Assets.SelectMany(asset => asset.Variants).Select(variant => variant.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 17,
                    "Production manifest references all 17 first-party raster files.", failures);
            }
            Check(catalog.LoadWarnings.Count == 0, "Production HD pack loads without manifest warnings.", failures);

            foreach (var key in new[]
                     {
                         "floor.stone.crypt_flagstone", "wall.stone.crypt_block", "door.wood.ironbound",
                         "terrain.water.crypt_shallow", "prop.pillar.stone_round", "light.torch.wall"
                     })
            {
                Check(catalog.TryResolve(CoreFantasyMapAssetPackProvisioner.PackId, key, 784211, 4, 5, out var resolved) && resolved is not null,
                    $"Production catalog resolves real raster asset '{key}'.", failures);
                if (resolved is not null)
                {
                    Check(resolved.SourcePath.StartsWith(provision.PackDirectory, StringComparison.OrdinalIgnoreCase),
                        $"Resolved '{key}' comes from the provisioned HD pack.", failures);
                    Check(resolved.Image.Width > 0 && resolved.Image.Height > 0, $"Resolved '{key}' bitmap decoded successfully.", failures);
                }
            }

            window = new AaaShellWindow(initializeOnLoad: false);
            if (window.DataContext is not MainViewModel viewModel)
            {
                failures.Add("AAA shell exposes MainViewModel to the map builder.");
                return Finish(failures, application, window);
            }

            var campaign = new CampaignState { Name = "R56 Map Builder Test" };
            var map = BuildReferenceMap();
            campaign.TacticalMaps.Add(map);
            viewModel.SelectedCampaign = campaign;
            viewModel.SelectedCampaignMap = map;
            viewModel.InitializeMapWorkspace();

            Check(ReferenceEquals(viewModel.SelectedCampaignMap, map), "Map workspace preserves the selected saved campaign map.", failures);
            Check(viewModel.MapPreview is not null, "Map workspace produces a review preview clone.", failures);
            Check(viewModel.MapPreview?.Visibility.RevealAll == true, "Review preview reveals fog for DM inspection.", failures);
            Check(map.Visibility.RevealAll == false, "Review preview does not mutate the saved map fog state.", failures);
            Check(viewModel.MapPreview is not null && !ReferenceEquals(viewModel.MapPreview, map), "Review preview is isolated from authoritative campaign map state.", failures);

            window.Show();
            FlushUi(window);
            var tabs = window.FindName("MainTabs") as TabControl;
            Check(tabs is not null, "Main navigation exists for r56 map-builder integration.", failures);
            if (tabs is not null)
            {
                var tabItems = tabs.Items.OfType<TabItem>().ToArray();
                Check(tabItems.Length == 10, "Main navigation still contains ten destinations.", failures);
                Check(tabItems.Length > 5 && tabItems[5].Content is MapsView, "Maps destination now hosts dedicated MapsView instead of WorldView.", failures);
                if (tabItems.Length > 5)
                {
                    tabs.SelectedIndex = 5;
                    tabs.ApplyTemplate();
                    FlushUi(window);
                    Check(tabItems[5].Content is MapsView, "Dedicated MapsView constructs and loads inside the shell.", failures);
                }
            }

            var outputDirectory = Path.GetFullPath(Path.Combine("artifacts", "r56-map-builder"));
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "r56-map-builder-1536x864.png");
            Capture(window, outputPath);
            Check(File.Exists(outputPath) && new FileInfo(outputPath).Length > 20_000,
                "r56 Map Builder renders a non-empty 1536x864 WPF reference image.", failures);
            if (File.Exists(outputPath))
            {
                using var stream = File.OpenRead(outputPath);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                Check(frame.PixelWidth == ReferenceWidth && frame.PixelHeight == ReferenceHeight,
                    "r56 Map Builder reference image is exactly 1536x864.", failures);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled r56 map-builder test exception: {ex}");
        }

        return Finish(failures, application, window);
    }

    private static int Finish(ICollection<string> failures, Application? application, AaaShellWindow? window)
    {
        try { window?.Close(); } catch { }
        try { application?.Shutdown(); } catch { }

        if (failures.Count == 0)
        {
            Console.WriteLine("R56 MAP BUILDER PASS");
            Console.WriteLine("First-party raster assets, local-pack precedence, isolated fog preview, dedicated MapsView, and 1536x864 rendering verified.");
            return 0;
        }

        Console.Error.WriteLine($"R56 MAP BUILDER FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static TacticalMap BuildReferenceMap()
    {
        var map = new TacticalMap
        {
            Name = "The Reliquary of Ash",
            Key = "r56.reliquary_of_ash",
            MapType = "dungeon",
            Theme = "ancient crypt",
            AssetSetId = CoreFantasyMapAssetPackProvisioner.PackId,
            WidthSquares = 24,
            HeightSquares = 15,
            FeetPerSquare = 5,
            Seed = 784211,
            FogOfWarEnabled = true,
            SourceKind = "ai_generated",
            Visibility = new TacticalMapVisibility { RevealAll = false }
        };

        map.Rooms.AddRange([
            new TacticalMapRoom { Name = "Entry Hall", X = 2, Y = 3, WidthSquares = 7, HeightSquares = 8, FloorAssetKey = "floor.stone.crypt_flagstone", WallAssetKey = "wall.stone.crypt_block" },
            new TacticalMapRoom { Name = "Flooded Reliquary", X = 9, Y = 2, WidthSquares = 7, HeightSquares = 10, FloorAssetKey = "floor.stone.crypt_flagstone", WallAssetKey = "wall.stone.crypt_block" },
            new TacticalMapRoom { Name = "Ash Chapel", X = 16, Y = 4, WidthSquares = 6, HeightSquares = 7, FloorAssetKey = "floor.stone.flagstone", WallAssetKey = "wall.stone.crypt_block" }
        ]);

        map.Walls.AddRange([
            new TacticalMapWall { FromX = 2, FromY = 3, ToX = 9, ToY = 3, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 2, FromY = 11, ToX = 9, ToY = 11, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 2, FromY = 3, ToX = 2, ToY = 11, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 9, FromY = 2, ToX = 16, ToY = 2, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 9, FromY = 12, ToX = 16, ToY = 12, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 9, FromY = 2, ToX = 9, ToY = 12, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 16, FromY = 2, ToX = 16, ToY = 12, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 16, FromY = 4, ToX = 22, ToY = 4, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 16, FromY = 11, ToX = 22, ToY = 11, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 22, FromY = 4, ToX = 22, ToY = 11, AssetKey = "wall.stone.crypt_block" }
        ]);
        map.Doors.AddRange([
            new TacticalMapDoor { Name = "Reliquary Door", X = 9, Y = 6, Orientation = "vertical", State = "closed", AssetKey = "door.wood.ironbound" },
            new TacticalMapDoor { Name = "Secret Chapel Door", X = 16, Y = 7, Orientation = "vertical", State = "closed", Secret = true, Discovered = false, AssetKey = "door.stone.secret" }
        ]);
        map.Terrain.Add(new TacticalMapTerrain { Name = "Floodwater", TerrainType = "water", X = 11, Y = 4, WidthSquares = 4, HeightSquares = 5, AssetKey = "terrain.water.crypt_shallow", DifficultTerrain = true });
        map.Terrain.Add(new TacticalMapTerrain { Name = "Collapsed Masonry", TerrainType = "rubble", X = 4, Y = 8, WidthSquares = 3, HeightSquares = 2, AssetKey = "terrain.rubble.stone", DifficultTerrain = true });
        map.Props.Add(new TacticalMapProp { Name = "Stone Pillar", X = 13, Y = 6, AssetKey = "prop.pillar.stone_round", BlocksMovement = true, BlocksLineOfSight = true });
        map.Props.Add(new TacticalMapProp { Name = "Ash Altar", X = 19, Y = 7, WidthSquares = 2, HeightSquares = 1, AssetKey = "prop.altar.stone_crypt", BlocksMovement = true, Cover = "half" });
        map.Lights.Add(new TacticalMapLight { Name = "Entry Torch", AssetKey = "light.torch.wall", X = 5, Y = 4, BrightRadiusFeet = 20, DimRadiusFeet = 20 });
        map.Lights.Add(new TacticalMapLight { Name = "Chapel Brazier", AssetKey = "light.brazier", X = 18, Y = 6, BrightRadiusFeet = 20, DimRadiusFeet = 20 });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Name = "Party Entrance", Side = "player", X = 3, Y = 6, DmOnly = true });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Name = "Guardian Spawn", Side = "enemy", X = 19, Y = 8, DmOnly = true });
        map.Zones.Add(new TacticalMapZone { Name = "Reliquary Trigger", ZoneType = "encounter", X = 17, Y = 5, WidthSquares = 4, HeightSquares = 5, DmOnly = true });
        return map;
    }

    private static void FlushUi(DispatcherObject owner)
    {
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void Capture(AaaShellWindow window, string path)
    {
        if (window.Content is not FrameworkElement root) throw new InvalidOperationException("AAA shell root visual is unavailable.");
        root.Measure(new Size(ReferenceWidth, ReferenceHeight));
        root.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(ReferenceWidth, ReferenceHeight, 96, 96, PixelFormats.Pbgra32);
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
