using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonMasterAI.App.Controls;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.MapAssetTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        Application? application = null;
        string? tempRoot = null;
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            tempRoot = Path.Combine(Path.GetTempPath(), "DungeonMasterAI-map-assets-" + Guid.NewGuid().ToString("N"));
            var packDir = Path.Combine(tempRoot, "test.hq.crypt");
            Directory.CreateDirectory(packDir);

            WriteTexture(Path.Combine(packDir, "floor-a.png"), Color.FromRgb(82, 78, 70), Color.FromRgb(132, 122, 104));
            WriteTexture(Path.Combine(packDir, "floor-b.png"), Color.FromRgb(67, 70, 68), Color.FromRgb(110, 120, 112));
            WriteTexture(Path.Combine(packDir, "wall.png"), Color.FromRgb(62, 58, 53), Color.FromRgb(142, 130, 111));
            WriteTexture(Path.Combine(packDir, "door.png"), Color.FromRgb(91, 57, 31), Color.FromRgb(189, 139, 70));
            WriteTexture(Path.Combine(packDir, "pillar.png"), Color.FromArgb(0, 0, 0, 0), Color.FromRgb(145, 139, 126), circular: true);

            var manifest = new TacticalMapAssetPackManifest
            {
                PackId = "test.hq.crypt",
                Name = "CI High Quality Test Pack",
                Version = "1.0.0",
                Author = "DungeonMasterAI CI",
                License = "Project test fixture",
                Credits = "Generated during test execution",
                Assets =
                [
                    new TacticalMapAssetDefinition
                    {
                        Key = "floor.stone.crypt_flagstone", Kind = "floor", RenderMode = "tile", AllowProceduralFallback = false,
                        Variants =
                        [
                            new TacticalMapAssetVariant { File = "floor-a.png", Weight = 3 },
                            new TacticalMapAssetVariant { File = "floor-b.png", Weight = 1, RotationDegrees = 90 }
                        ]
                    },
                    new TacticalMapAssetDefinition
                    {
                        Key = "wall.stone.crypt_block", Kind = "wall", RenderMode = "segment", AllowProceduralFallback = false,
                        Variants = [new TacticalMapAssetVariant { File = "wall.png" }]
                    },
                    new TacticalMapAssetDefinition
                    {
                        Key = "door.wood.ironbound", Kind = "door", RenderMode = "segment", AllowProceduralFallback = false,
                        Variants = [new TacticalMapAssetVariant { File = "door.png" }]
                    },
                    new TacticalMapAssetDefinition
                    {
                        Key = "prop.pillar.stone_round", Kind = "prop", RenderMode = "sprite", Scale = 0.86, AllowProceduralFallback = false,
                        Variants = [new TacticalMapAssetVariant { File = "pillar.png" }]
                    }
                ]
            };
            File.WriteAllText(Path.Combine(packDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var validation = TacticalMapAssetPackValidator.Validate(manifest);
            Check(validation.IsValid, "Valid licensed asset manifest passes validation.", failures);

            var unsafeManifest = new TacticalMapAssetPackManifest
            {
                PackId = "unsafe",
                Name = "Unsafe",
                Author = "Test",
                License = "Test",
                Assets = [new TacticalMapAssetDefinition
                {
                    Key = "prop.bad", AllowProceduralFallback = false,
                    Variants = [new TacticalMapAssetVariant { File = "../escape.png" }]
                }]
            };
            Check(!TacticalMapAssetPackValidator.Validate(unsafeManifest).IsValid,
                "Asset manifest rejects directory traversal outside the pack.", failures);

            var catalog = new TacticalMapAssetCatalog([tempRoot]);
            Check(catalog.Packs.Count == 1 && catalog.Packs.Single().PackId == "test.hq.crypt",
                "Catalog discovers the asset pack and preserves pack identity.", failures);
            Check(catalog.Packs.Single().Author == "DungeonMasterAI CI" && catalog.Packs.Single().License == "Project test fixture",
                "Catalog preserves author and license metadata.", failures);

            Check(catalog.TryResolve("test.hq.crypt", "floor.stone.crypt_flagstone", 784211, 3, 4, out var first) && first is not null,
                "Catalog resolves a raster floor asset.", failures);
            Check(catalog.TryResolve("test.hq.crypt", "floor.stone.crypt_flagstone", 784211, 3, 4, out var repeated) && repeated is not null,
                "Catalog resolves the same deterministic floor request twice.", failures);
            if (first is not null && repeated is not null)
            {
                Check(first.SourcePath == repeated.SourcePath, "Variant selection is deterministic for the same seed/cell/key.", failures);
                Check(ReferenceEquals(first.Image, repeated.Image), "Decoded bitmap is cached and reused.", failures);
            }
            Check(!catalog.TryResolve("test.hq.crypt", "prop.not-installed", 1, 0, 0, out _),
                "Missing asset key returns false so renderer can fall back safely.", failures);

            var map = BuildMap();
            var control = new TacticalMapControl
            {
                Map = map,
                AssetPackRoot = tempRoot,
                ShowDmView = true,
                Width = 1280,
                Height = 720
            };
            control.Measure(new Size(1280, 720));
            control.Arrange(new Rect(0, 0, 1280, 720));
            control.UpdateLayout();
            Check(control.LoadedAssetPacks.Any(pack => pack.PackId == "test.hq.crypt"),
                "Renderer loads the requested real image asset pack.", failures);
            Check(control.AssetPackWarnings.Count == 0, "Test image pack loads without warnings.", failures);

            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(control);
            var outputDirectory = Path.GetFullPath(Path.Combine("artifacts", "map-assets"));
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "r54-image-asset-pack-1280x720.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(outputPath)) encoder.Save(stream);
            Check(File.Exists(outputPath) && new FileInfo(outputPath).Length > 10_000,
                "Asset-backed renderer writes a non-empty 1280x720 PNG artifact.", failures);

            if (failures.Count == 0)
            {
                Console.WriteLine("MAP ASSET PACK PASS");
                Console.WriteLine($"Manifest, deterministic variants, image cache, license metadata, fallback contract, and renderer verified at {outputPath}.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled map asset test exception: {ex}");
        }
        finally
        {
            try { application?.Shutdown(); } catch { }
            if (!string.IsNullOrWhiteSpace(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        Console.Error.WriteLine($"MAP ASSET PACK FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static TacticalMap BuildMap()
    {
        var map = new TacticalMap
        {
            Name = "Asset Pack Crypt",
            MapType = "dungeon",
            Theme = "crypt",
            AssetSetId = "test.hq.crypt",
            WidthSquares = 16,
            HeightSquares = 10,
            FeetPerSquare = 5,
            Seed = 784211,
            FogOfWarEnabled = false
        };
        map.Rooms.Add(new TacticalMapRoom
        {
            Name = "Reliquary",
            X = 1, Y = 1, WidthSquares = 14, HeightSquares = 8,
            FloorAssetKey = "floor.stone.crypt_flagstone",
            WallAssetKey = "wall.stone.crypt_block"
        });
        map.Walls.AddRange([
            new TacticalMapWall { FromX = 1, FromY = 1, ToX = 15, ToY = 1, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 15, FromY = 1, ToX = 15, ToY = 9, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 15, FromY = 9, ToX = 1, ToY = 9, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 1, FromY = 9, ToX = 1, ToY = 1, AssetKey = "wall.stone.crypt_block" }
        ]);
        map.Doors.Add(new TacticalMapDoor { Name = "Ironbound Door", X = 8, Y = 1, Orientation = "horizontal", State = "closed", AssetKey = "door.wood.ironbound" });
        map.Props.Add(new TacticalMapProp { Name = "Reliquary Pillar", X = 8, Y = 5, AssetKey = "prop.pillar.stone_round", BlocksMovement = true, BlocksLineOfSight = true });
        return map;
    }

    private static void WriteTexture(string path, Color background, Color accent, bool circular = false)
    {
        const int size = 96;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size));
            if (circular)
            {
                dc.DrawEllipse(new SolidColorBrush(accent), new Pen(new SolidColorBrush(Color.FromRgb(42, 40, 36)), 4), new Point(size / 2d, size / 2d), 34, 34);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 245, 235, 210)), null, new Point(39, 37), 10, 8);
            }
            else
            {
                var pen = new Pen(new SolidColorBrush(accent), 3);
                for (var y = 12; y < size; y += 24) dc.DrawLine(pen, new Point(0, y), new Point(size, y));
                for (var x = 16; x < size; x += 32) dc.DrawLine(pen, new Point(x, 0), new Point(x, size));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 245, 220)), 2), new Point(5, 5), new Point(90, 5));
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
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
