using System.Windows.Media;

namespace DungeonMasterAI.App.Controls;

/// <summary>
/// Prototype material resolver. Asset keys are deliberately stable so high-resolution tile/sprite
/// packs can replace these procedural fallbacks later without changing campaign map JSON.
/// </summary>
internal static class TacticalMapAssetPalette
{
    public static Color MaterialColor(string assetKey, int variation = 0)
    {
        var key = (assetKey ?? "").ToLowerInvariant();
        var baseColor = key switch
        {
            var k when k.Contains("water") => Color.FromRgb(38, 76, 88),
            var k when k.Contains("lava") => Color.FromRgb(105, 43, 26),
            var k when k.Contains("grass") || k.Contains("moss") => Color.FromRgb(57, 72, 48),
            var k when k.Contains("dirt") || k.Contains("earth") => Color.FromRgb(73, 59, 43),
            var k when k.Contains("wood") => Color.FromRgb(85, 61, 39),
            var k when k.Contains("marble") => Color.FromRgb(116, 113, 105),
            var k when k.Contains("crypt") => Color.FromRgb(67, 68, 65),
            var k when k.Contains("stone") || k.Contains("flagstone") => Color.FromRgb(74, 73, 68),
            _ => Color.FromRgb(66, 66, 62)
        };

        var delta = variation switch
        {
            0 => -7,
            1 => -3,
            2 => 0,
            3 => 4,
            _ => 7
        };
        return Shift(baseColor, delta);
    }

    public static Color TerrainColor(string assetKey, string terrainType)
    {
        var combined = $"{assetKey} {terrainType}".ToLowerInvariant();
        if (combined.Contains("water")) return Color.FromArgb(190, 39, 88, 105);
        if (combined.Contains("rubble")) return Color.FromArgb(180, 91, 82, 68);
        if (combined.Contains("mud")) return Color.FromArgb(190, 74, 58, 39);
        if (combined.Contains("ice")) return Color.FromArgb(170, 105, 137, 148);
        if (combined.Contains("lava")) return Color.FromArgb(210, 146, 55, 27);
        if (combined.Contains("vegetation") || combined.Contains("moss")) return Color.FromArgb(170, 57, 88, 51);
        return Color.FromArgb(145, 78, 76, 68);
    }

    public static Color PropColor(string assetKey)
    {
        var key = (assetKey ?? "").ToLowerInvariant();
        if (key.Contains("wood") || key.Contains("chest")) return Color.FromRgb(112, 75, 43);
        if (key.Contains("iron") || key.Contains("metal")) return Color.FromRgb(91, 96, 98);
        if (key.Contains("bone") || key.Contains("sarcophagus")) return Color.FromRgb(123, 116, 96);
        if (key.Contains("statue") || key.Contains("pillar") || key.Contains("altar")) return Color.FromRgb(101, 97, 88);
        if (key.Contains("rubble")) return Color.FromRgb(89, 84, 75);
        return Color.FromRgb(96, 86, 70);
    }

    private static Color Shift(Color color, int delta)
        => Color.FromRgb(Clamp(color.R + delta), Clamp(color.G + delta), Clamp(color.B + delta));

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
