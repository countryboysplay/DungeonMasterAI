using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App.Controls;

public sealed record CoreMapAssetProvisionResult(bool Success, string Message, string PackDirectory, int AssetFileCount);

/// <summary>
/// Creates the first-party Core Crypt raster pack in the user's local map-pack directory.
/// The source recipes are project-owned and deterministic. Tactical maps continue to store only
/// semantic asset keys, so these images can be upgraded later without changing campaign geometry.
/// </summary>
public static class CoreFantasyMapAssetPackProvisioner
{
    public const string PackId = "core.fantasy.crypt";
    public const string PackVersion = "2.0.0";

    public static CoreMapAssetProvisionResult EnsureInstalled()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return new CoreMapAssetProvisionResult(false, "Local application-data storage is unavailable.", "", 0);

        var root = Path.Combine(local, "DungeonMasterAI", "MapPacks", PackId);
        try
        {
            Directory.CreateDirectory(root);
            var manifestPath = Path.Combine(root, "manifest.json");
            if (IsCurrentInstallation(manifestPath, root, out var existingCount))
                return new CoreMapAssetProvisionResult(true, $"Core fantasy asset pack {PackVersion} ready.", root, existingCount);

            WriteFloor(Path.Combine(root, "floor-flagstone-a.png"), 101, false);
            WriteFloor(Path.Combine(root, "floor-flagstone-b.png"), 127, false);
            WriteFloor(Path.Combine(root, "floor-crypt-a.png"), 211, true);
            WriteFloor(Path.Combine(root, "floor-crypt-b.png"), 239, true);
            WriteWall(Path.Combine(root, "wall-block.png"), 307, false);
            WriteWall(Path.Combine(root, "wall-crypt.png"), 331, true);
            WriteDoor(Path.Combine(root, "door-ironbound.png"), DoorRecipe.Ironbound);
            WriteDoor(Path.Combine(root, "door-broken.png"), DoorRecipe.Broken);
            WriteDoor(Path.Combine(root, "door-secret-stone.png"), DoorRecipe.SecretStone);
            WriteTerrain(Path.Combine(root, "terrain-water-shallow.png"), true, 401);
            WriteTerrain(Path.Combine(root, "terrain-rubble.png"), false, 433);
            WriteProp(Path.Combine(root, "prop-pillar-round.png"), PropRecipe.Pillar, 503);
            WriteProp(Path.Combine(root, "prop-rubble-pillar.png"), PropRecipe.Rubble, 509);
            WriteProp(Path.Combine(root, "prop-altar-crypt.png"), PropRecipe.Altar, 521);
            WriteProp(Path.Combine(root, "prop-sarcophagus.png"), PropRecipe.Sarcophagus, 541);
            WriteLight(Path.Combine(root, "light-torch.png"), false);
            WriteLight(Path.Combine(root, "light-brazier.png"), true);

            var manifest = BuildManifest();
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return new CoreMapAssetProvisionResult(true, $"Installed Core fantasy raster pack {PackVersion} with 17 project-original image assets.", root, 17);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CoreMapAssetProvisionResult(false, $"Core map artwork could not be provisioned: {ex.Message}", root, 0);
        }
    }

    private static bool IsCurrentInstallation(string manifestPath, string root, out int assetCount)
    {
        assetCount = 0;
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var manifest = JsonSerializer.Deserialize<TacticalMapAssetPackManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || !manifest.PackId.Equals(PackId, StringComparison.OrdinalIgnoreCase) || manifest.Version != PackVersion) return false;
            var variants = manifest.Assets.SelectMany(asset => asset.Variants).ToArray();
            if (variants.Length == 0 || variants.Any(variant => !File.Exists(Path.Combine(root, variant.File)))) return false;
            assetCount = variants.Select(v => v.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private static TacticalMapAssetPackManifest BuildManifest() => new()
    {
        PackId = PackId,
        Name = "DungeonMasterAI Core Crypt HD",
        Version = PackVersion,
        Author = "DungeonMasterAI project",
        License = "Project-original generated artwork bundled for use and redistribution with DungeonMasterAI",
        Credits = "First-party r56 fantasy tactical-map artwork generated from deterministic project-owned vector recipes.",
        Assets =
        [
            Asset("floor.stone.flagstone", "floor", "tile", [V("floor-flagstone-a.png", 3), V("floor-flagstone-b.png", 2, 90)]),
            Asset("floor.stone.crypt_flagstone", "floor", "tile", [V("floor-crypt-a.png", 3), V("floor-crypt-b.png", 2, 90)]),
            Asset("wall.stone.block", "wall", "segment", [V("wall-block.png")]),
            Asset("wall.stone.crypt_block", "wall", "segment", [V("wall-crypt.png")]),
            Asset("door.wood.ironbound", "door", "segment", [V("door-ironbound.png")]),
            Asset("door.wood.broken", "door", "segment", [V("door-broken.png")]),
            Asset("door.stone.secret", "door", "segment", [V("door-secret-stone.png")]),
            Asset("terrain.water.crypt_shallow", "terrain", "stretch", [V("terrain-water-shallow.png")], 0.92),
            Asset("terrain.rubble.stone", "terrain", "stretch", [V("terrain-rubble.png")]),
            Asset("prop.pillar.stone_round", "prop", "sprite", [V("prop-pillar-round.png")], 1, 0.9),
            Asset("prop.rubble.pillar", "prop", "sprite", [V("prop-rubble-pillar.png")]),
            Asset("prop.altar.stone_crypt", "prop", "sprite", [V("prop-altar-crypt.png")], 1, 0.95),
            Asset("prop.sarcophagus.stone", "prop", "sprite", [V("prop-sarcophagus.png")], 1, 0.95),
            Asset("light.torch.wall", "light", "sprite", [V("light-torch.png")], 1, 0.72),
            Asset("light.brazier", "light", "sprite", [V("light-brazier.png")], 1, 0.82)
        ]
    };

    private static TacticalMapAssetDefinition Asset(string key, string kind, string mode, List<TacticalMapAssetVariant> variants, double opacity = 1, double scale = 1)
        => new() { Key = key, Kind = kind, RenderMode = mode, Variants = variants, Opacity = opacity, Scale = scale, AllowProceduralFallback = true };

    private static TacticalMapAssetVariant V(string file, int weight = 1, int rotation = 0)
        => new() { File = file, Weight = weight, RotationDegrees = rotation };

    private static void WriteFloor(string path, int seed, bool crypt)
    {
        const int size = 256;
        var rng = new Random(seed);
        SaveVisual(path, size, size, dc =>
        {
            var baseColor = crypt ? Color.FromRgb(64, 66, 62) : Color.FromRgb(79, 77, 70);
            dc.DrawRectangle(new SolidColorBrush(baseColor), null, new Rect(0, 0, size, size));
            for (var i = 0; i < 150; i++)
            {
                var tone = (byte)rng.Next(28, 72);
                var alpha = (byte)rng.Next(10, 35);
                var r = rng.NextDouble() * 4 + 0.8;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, tone, tone, (byte)Math.Max(0, tone - 5))), null,
                    new Point(rng.Next(size), rng.Next(size)), r, r * 0.7);
            }
            var joint = new Pen(new SolidColorBrush(Color.FromArgb(175, 22, 21, 19)), 3);
            var highlight = new Pen(new SolidColorBrush(Color.FromArgb(42, 235, 226, 203)), 1);
            const int rowHeight = 64;
            for (var row = 0; row < 4; row++)
            {
                var y = row * rowHeight;
                dc.DrawLine(joint, new Point(0, y), new Point(size, y));
                dc.DrawLine(highlight, new Point(0, y + 3), new Point(size, y + 3));
                var offset = row % 2 == 0 ? 0 : 32;
                for (var x = -offset; x <= size; x += 64)
                    dc.DrawLine(joint, new Point(x, y), new Point(x, Math.Min(size, y + rowHeight)));
            }
            dc.DrawLine(joint, new Point(0, size - 1), new Point(size, size - 1));
            var crack = new Pen(new SolidColorBrush(Color.FromArgb(130, 18, 17, 15)), 2);
            for (var i = 0; i < (crypt ? 22 : 13); i++)
            {
                var x = rng.Next(12, size - 12);
                var y = rng.Next(12, size - 12);
                var p1 = new Point(x, y);
                var p2 = new Point(x + rng.Next(-18, 19), y + rng.Next(-10, 11));
                var p3 = new Point(p2.X + rng.Next(-12, 13), p2.Y + rng.Next(-8, 9));
                dc.DrawLine(crack, p1, p2);
                dc.DrawLine(crack, p2, p3);
            }
            if (crypt)
            {
                for (var i = 0; i < 16; i++)
                {
                    var radius = rng.Next(5, 18);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)rng.Next(10, 28), 44, 65, 42)), null,
                        new Point(rng.Next(size), rng.Next(size)), radius, radius * 0.65);
                }
            }
        });
    }

    private static void WriteWall(string path, int seed, bool crypt)
    {
        const int width = 384;
        const int height = 96;
        var rng = new Random(seed);
        SaveVisual(path, width, height, dc =>
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(crypt ? Color.FromRgb(71, 68, 61) : Color.FromRgb(88, 84, 75)),
                new Pen(new SolidColorBrush(Color.FromRgb(29, 27, 24)), 4), new Rect(2, 4, width - 4, height - 8), 4, 4);
            var joint = new Pen(new SolidColorBrush(Color.FromArgb(170, 27, 25, 22)), 3);
            for (var y = 4; y < height; y += 44)
            {
                dc.DrawLine(joint, new Point(3, y), new Point(width - 3, y));
                var offset = ((y / 44) % 2) * 32;
                for (var x = 32 - offset; x < width; x += 64)
                    dc.DrawLine(joint, new Point(x, y), new Point(x, Math.Min(height - 4, y + 44)));
            }
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)), null, new Rect(4, height - 18, width - 8, 14));
            var crack = new Pen(new SolidColorBrush(Color.FromArgb(125, 18, 17, 15)), 2);
            for (var i = 0; i < 13; i++)
            {
                var x = rng.Next(12, width - 12);
                var y = rng.Next(10, height - 16);
                dc.DrawLine(crack, new Point(x, y), new Point(x + rng.Next(-18, 19), y + rng.Next(-8, 9)));
            }
        });
    }

    private enum DoorRecipe { Ironbound, Broken, SecretStone }

    private static void WriteDoor(string path, DoorRecipe recipe)
    {
        const int width = 384;
        const int height = 96;
        SaveVisual(path, width, height, dc =>
        {
            if (recipe == DoorRecipe.SecretStone)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(98, 94, 86)), new Pen(new SolidColorBrush(Color.FromRgb(34, 32, 29)), 5), new Rect(2, 8, width - 4, height - 16), 6, 6);
                for (var x = 18; x < width; x += 48) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(100, 47, 44, 40)), 2), new Point(x, 12), new Point(x + 18, height - 12));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(176, 143, 70)), null, new Point(width / 2d, height / 2d), 7, 7);
                return;
            }

            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(92, 55, 29)), new Pen(new SolidColorBrush(Color.FromRgb(31, 22, 15)), 5), new Rect(2, 7, width - 4, height - 14), 6, 6);
            for (var y = 16; y < height - 10; y += 16) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(105, 47, 28, 16)), 2), new Point(8, y), new Point(width - 8, y));
            foreach (var x in new[] { 44d, width / 2d, width - 44d })
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(54, 57, 57)), null, new Rect(x - 6, 10, 12, height - 20));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(184, 150, 77)), null, new Point(x, 24), 3, 3);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(184, 150, 77)), null, new Point(x, height - 24), 3, 3);
            }
            if (recipe == DoorRecipe.Broken)
            {
                var geometry = new StreamGeometry();
                using var ctx = geometry.Open();
                ctx.BeginFigure(new Point(width * .59, 8), true, true);
                ctx.LineTo(new Point(width * .73, 8), true, false);
                ctx.LineTo(new Point(width * .67, height * .48), true, false);
                ctx.LineTo(new Point(width * .79, height - 8), true, false);
                ctx.LineTo(new Point(width * .61, height - 8), true, false);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(225, 12, 10, 8)), null, geometry);
            }
        });
    }

    private static void WriteTerrain(string path, bool water, int seed)
    {
        const int size = 256;
        var rng = new Random(seed);
        SaveVisual(path, size, size, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(water ? Color.FromRgb(38, 70, 78) : Color.FromRgb(76, 73, 67)), null, new Rect(0, 0, size, size));
            if (water)
            {
                for (var y = 14; y < size; y += 22)
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(92, 145, 192, 196)), 2.5);
                    var geometry = new StreamGeometry();
                    using var ctx = geometry.Open();
                    ctx.BeginFigure(new Point(0, y), false, false);
                    for (var x = 8; x <= size; x += 8) ctx.LineTo(new Point(x, y + Math.Sin((x + seed) / 18d) * 3), true, false);
                    dc.DrawGeometry(null, pen, geometry);
                }
                return;
            }

            var mortar = new Pen(new SolidColorBrush(Color.FromArgb(120, 30, 28, 25)), 2);
            for (var y = 0; y < size; y += 64) dc.DrawLine(mortar, new Point(0, y), new Point(size, y));
            for (var x = 0; x < size; x += 64) dc.DrawLine(mortar, new Point(x, 0), new Point(x, size));
            for (var i = 0; i < 70; i++)
            {
                var x = rng.Next(size);
                var y = rng.Next(size);
                var radius = rng.Next(3, 11);
                var c = (byte)rng.Next(78, 126);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(205, c, (byte)Math.Max(0, c - 5), (byte)Math.Max(0, c - 13))),
                    new Pen(new SolidColorBrush(Color.FromArgb(170, 37, 34, 30)), 1), new Point(x, y), radius, radius * .7);
            }
        });
    }

    private enum PropRecipe { Pillar, Rubble, Altar, Sarcophagus }

    private static void WriteProp(string path, PropRecipe recipe, int seed)
    {
        const int size = 256;
        var rng = new Random(seed);
        SaveVisual(path, size, size, dc =>
        {
            if (recipe == PropRecipe.Pillar)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, new Point(138, 143), 78, 74);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(55, 52, 48)), new Pen(new SolidColorBrush(Color.FromRgb(25, 23, 21)), 6), new Point(128, 122), 80, 80);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(123, 117, 104)), new Pen(new SolidColorBrush(Color.FromRgb(57, 53, 48)), 4), new Point(128, 122), 65, 65);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(55, 238, 227, 203)), null, new Point(108, 100), 22, 18);
                return;
            }
            if (recipe == PropRecipe.Rubble)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)), null, new Point(132, 178), 90, 38);
                for (var i = 0; i < 31; i++)
                {
                    var x = rng.Next(44, 214);
                    var y = rng.Next(70, 208);
                    var r = rng.Next(6, 23);
                    var tone = (byte)rng.Next(76, 124);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(245, tone, (byte)Math.Max(0, tone - 4), (byte)Math.Max(0, tone - 12))),
                        new Pen(new SolidColorBrush(Color.FromRgb(39, 36, 32)), 1.5), new Point(x, y), r, r * .65);
                }
                return;
            }
            if (recipe == PropRecipe.Altar)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(42, 78, 180, 130), 12, 12);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(113, 106, 92)), new Pen(new SolidColorBrush(Color.FromRgb(35, 32, 28)), 5), new Rect(34, 56, 188, 136), 10, 10);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(190, 184, 150, 73)), 5), new Point(128, 121), 37, 29);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(185, 184, 150, 73)), 4), new Point(128, 84), new Point(128, 159));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(185, 184, 150, 73)), 4), new Point(103, 121), new Point(153, 121));
                return;
            }

            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)), null, new Rect(69, 35, 130, 198), 28, 28);
            var coffin = new StreamGeometry();
            using (var ctx = coffin.Open())
            {
                ctx.BeginFigure(new Point(78, 26), true, true);
                ctx.LineTo(new Point(178, 26), true, false);
                ctx.LineTo(new Point(198, 66), true, false);
                ctx.LineTo(new Point(185, 220), true, false);
                ctx.LineTo(new Point(70, 220), true, false);
                ctx.LineTo(new Point(58, 66), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(112, 107, 98)), new Pen(new SolidColorBrush(Color.FromRgb(33, 31, 28)), 5), coffin);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(78, 74, 68)), null, new Point(128, 78), 20, 20);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(67, 63, 58)), 8), new Point(128, 98), new Point(128, 166));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(67, 63, 58)), 7), new Point(105, 126), new Point(151, 126));
        });
    }

    private static void WriteLight(string path, bool brazier)
    {
        const int size = 256;
        SaveVisual(path, size, size, dc =>
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(110, 255, 151, 49), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 151, 49), 1));
            dc.DrawEllipse(glow, null, new Point(128, 96), brazier ? 86 : 72, brazier ? 86 : 72);
            if (brazier)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(45, 48, 48)), new Pen(new SolidColorBrush(Color.FromRgb(22, 21, 19)), 5), new Point(128, 145), 66, 34);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(94, 54, 24)), null, new Point(128, 137), 52, 20);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(53, 54, 54)), 8), new Point(84, 166), new Point(73, 218));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(53, 54, 54)), 8), new Point(172, 166), new Point(183, 218));
            }
            else
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(86, 50, 25)), new Pen(new SolidColorBrush(Color.FromRgb(34, 23, 15)), 3), new Rect(120, 105, 16, 108), 4, 4);
            }
            var fire = new StreamGeometry();
            using (var ctx = fire.Open())
            {
                ctx.BeginFigure(new Point(brazier ? 94 : 108, 126), true, true);
                ctx.LineTo(new Point(115, 69), true, false);
                ctx.LineTo(new Point(128, 119), true, false);
                ctx.LineTo(new Point(141, 55), true, false);
                ctx.LineTo(new Point(154, 122), true, false);
                ctx.LineTo(new Point(brazier ? 172 : 149, 88), true, false);
                ctx.LineTo(new Point(brazier ? 178 : 151, 135), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(244, 102, 29)), null, fire);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 218, 92)), null, new Point(133, 103), 17, 28);
        });
    }

    private static void SaveVisual(string path, int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
