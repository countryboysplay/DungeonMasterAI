using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App.Controls;

/// <summary>
/// Renders authoritative TacticalMap geometry. Stable asset keys are resolved through an optional
/// image asset pack; every image path has a procedural fallback so visual installation failures can
/// never change movement, line of sight, doors, fog, or encounter state.
/// </summary>
public sealed class TacticalMapControl : FrameworkElement
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(TacticalMap), typeof(TacticalMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CampaignProperty = DependencyProperty.Register(
        nameof(Campaign), typeof(CampaignState), typeof(TacticalMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EncounterProperty = DependencyProperty.Register(
        nameof(Encounter), typeof(EncounterState), typeof(TacticalMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowDmViewProperty = DependencyProperty.Register(
        nameof(ShowDmView), typeof(bool), typeof(TacticalMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(TacticalMapControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AssetPackRootProperty = DependencyProperty.Register(
        nameof(AssetPackRoot), typeof(string), typeof(TacticalMapControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnAssetPackRootChanged));

    private TacticalMapAssetCatalog? _assetCatalog;

    public TacticalMap? Map { get => (TacticalMap?)GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public CampaignState? Campaign { get => (CampaignState?)GetValue(CampaignProperty); set => SetValue(CampaignProperty, value); }
    public EncounterState? Encounter { get => (EncounterState?)GetValue(EncounterProperty); set => SetValue(EncounterProperty, value); }
    public bool ShowDmView { get => (bool)GetValue(ShowDmViewProperty); set => SetValue(ShowDmViewProperty, value); }
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public string AssetPackRoot { get => (string)GetValue(AssetPackRootProperty); set => SetValue(AssetPackRootProperty, value); }

    public IReadOnlyCollection<TacticalMapAssetPackManifest> LoadedAssetPacks => AssetCatalog.Packs;
    public IReadOnlyList<string> AssetPackWarnings => AssetCatalog.LoadWarnings;

    private TacticalMapAssetCatalog AssetCatalog => _assetCatalog ??= string.IsNullOrWhiteSpace(AssetPackRoot)
        ? new TacticalMapAssetCatalog()
        : new TacticalMapAssetCatalog([AssetPackRoot]);

    private static void OnAssetPackRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TacticalMapControl control) control._assetCatalog = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 10, 12)), null,
            new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));

        var map = Map;
        if (map is null || map.WidthSquares <= 0 || map.HeightSquares <= 0)
        {
            DrawCentered(dc, "No authored tactical map selected.", 17, Brushes.LightGray);
            return;
        }

        var frameMargin = 42d;
        var availableWidth = Math.Max(60, ActualWidth - frameMargin * 2);
        var availableHeight = Math.Max(60, ActualHeight - frameMargin * 2 - 24);
        var cell = Math.Max(4, Math.Min(availableWidth / map.WidthSquares, availableHeight / map.HeightSquares));
        var mapWidth = map.WidthSquares * cell;
        var mapHeight = map.HeightSquares * cell;
        var ox = (ActualWidth - mapWidth) / 2;
        var oy = (ActualHeight - mapHeight) / 2 + 8;
        var mapRect = new Rect(ox, oy, mapWidth, mapHeight);

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 17)),
            new Pen(new SolidColorBrush(Color.FromArgb(90, 181, 145, 76)), 3), mapRect);

        DrawFloors(dc, map, ox, oy, cell);
        DrawTerrain(dc, map, ox, oy, cell);
        DrawLighting(dc, map, ox, oy, cell);
        DrawGrid(dc, map, ox, oy, cell);
        DrawWalls(dc, map, ox, oy, cell);
        DrawDoors(dc, map, ox, oy, cell);
        DrawProps(dc, map, ox, oy, cell);
        DrawRoomLabels(dc, map, ox, oy, cell);
        if (ShowDmView) DrawDmLayer(dc, map, ox, oy, cell);
        DrawEncounterTokens(dc, map, ox, oy, cell);
        DrawFog(dc, map, ox, oy, cell);

        DrawText(dc, map.Name, 15, new SolidColorBrush(Color.FromRgb(230, 214, 181)), ox, 8, false, FontWeights.SemiBold);
        var packState = LoadedAssetPacks.Any(pack => pack.PackId.Equals(map.AssetSetId, StringComparison.OrdinalIgnoreCase))
            ? $"assets {map.AssetSetId}"
            : $"procedural fallback ({map.AssetSetId})";
        DrawText(dc, $"{map.MapType} • {map.Theme} • {map.WidthSquares}×{map.HeightSquares} • {map.FeetPerSquare} ft/grid • seed {map.Seed} • {packState}",
            10, new SolidColorBrush(Color.FromRgb(145, 145, 137)), ox, 27, false, FontWeights.Normal);
    }

    private void DrawFloors(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var room in map.Rooms.Where(r => !r.DmOnly))
        {
            for (var y = room.Y; y < room.Y + room.HeightSquares; y++)
            for (var x = room.X; x < room.X + room.WidthSquares; x++)
            {
                if (!InBounds(map, x, y)) continue;
                var rect = CellRect(ox, oy, cell, x, y);
                if (TryDrawAsset(dc, map, room.FloorAssetKey, x, y, rect)) continue;

                var variation = Math.Abs(StableHash(map.Seed, x, y, room.FloorAssetKey)) % 5;
                dc.DrawRectangle(new SolidColorBrush(TacticalMapAssetPalette.MaterialColor(room.FloorAssetKey, variation)), null, rect);
                var inset = Math.Max(1, cell * 0.08);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(34, 230, 220, 195)), Math.Max(0.5, cell * 0.025)),
                    new Point(rect.Left + inset, rect.Top + inset), new Point(rect.Right - inset, rect.Top + inset));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Math.Max(0.5, cell * 0.03)),
                    new Point(rect.Left + inset, rect.Bottom - inset), new Point(rect.Right - inset, rect.Bottom - inset));
                if ((StableHash(map.Seed, x, y, room.FloorAssetKey) & 3) == 0)
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(38, 20, 18, 15)), Math.Max(0.6, cell * 0.035)),
                        new Point(rect.Left + cell * 0.18, rect.Top + cell * 0.72),
                        new Point(rect.Left + cell * 0.55, rect.Top + cell * 0.58));
            }
        }
    }

    private void DrawTerrain(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var terrain in map.Terrain.Where(t => !t.DmOnly))
        {
            var rect = GridRect(ox, oy, cell, terrain.X, terrain.Y, terrain.WidthSquares, terrain.HeightSquares);
            if (TryDrawAsset(dc, map, terrain.AssetKey, terrain.X, terrain.Y, rect)) continue;

            dc.DrawRectangle(new SolidColorBrush(TacticalMapAssetPalette.TerrainColor(terrain.AssetKey, terrain.TerrainType)), null, rect);
            var key = $"{terrain.AssetKey} {terrain.TerrainType}".ToLowerInvariant();
            if (key.Contains("water"))
            {
                var wave = new Pen(new SolidColorBrush(Color.FromArgb(95, 142, 190, 201)), Math.Max(1, cell * 0.05));
                for (var py = rect.Top + cell * 0.35; py < rect.Bottom; py += Math.Max(7, cell * 0.55))
                    dc.DrawLine(wave, new Point(rect.Left + cell * 0.2, py), new Point(rect.Right - cell * 0.2, py));
            }
            else if (key.Contains("rubble"))
            {
                var rock = new SolidColorBrush(Color.FromArgb(130, 135, 126, 111));
                var count = Math.Clamp(terrain.WidthSquares * terrain.HeightSquares * 4, 4, 80);
                for (var i = 0; i < count; i++)
                {
                    var hash = StableHash(map.Seed + i, terrain.X, terrain.Y, terrain.AssetKey);
                    var px = rect.Left + ((hash & 0xFF) / 255d) * rect.Width;
                    var py = rect.Top + (((hash >> 8) & 0xFF) / 255d) * rect.Height;
                    var radius = Math.Max(1.5, cell * (0.04 + ((hash >> 16) & 7) * 0.008));
                    dc.DrawEllipse(rock, null, new Point(px, py), radius, radius * 0.72);
                }
            }
        }
    }

    private void DrawLighting(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var light in map.Lights.Where(l => !l.DmOnly))
        {
            var color = ParseColor(light.Color, Color.FromRgb(242, 179, 95));
            var totalRadiusSquares = (light.BrightRadiusFeet + light.DimRadiusFeet) / (double)Math.Max(1, map.FeetPerSquare);
            var radius = Math.Max(cell * 0.75, totalRadiusSquares * cell);
            var center = new Point(ox + light.X * cell, oy + light.Y * cell);
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(75, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(30, color.R, color.G, color.B), 0.45));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            dc.DrawEllipse(glow, null, center, radius, radius);

            var spriteRect = new Rect(center.X - cell * 0.35, center.Y - cell * 0.35, cell * 0.7, cell * 0.7);
            if (!TryDrawAsset(dc, map, light.AssetKey, (int)Math.Round(light.X), (int)Math.Round(light.Y), spriteRect))
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B)), null, center, Math.Max(2, cell * 0.08), Math.Max(2, cell * 0.08));
        }
    }

    private static void DrawGrid(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(58, 28, 31, 31)), Math.Max(0.5, Math.Min(1, cell * 0.035)));
        for (var x = 0; x <= map.WidthSquares; x++) dc.DrawLine(pen, new Point(ox + x * cell, oy), new Point(ox + x * cell, oy + map.HeightSquares * cell));
        for (var y = 0; y <= map.HeightSquares; y++) dc.DrawLine(pen, new Point(ox, oy + y * cell), new Point(ox + map.WidthSquares * cell, oy + y * cell));
    }

    private void DrawWalls(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var wall in map.Walls.Where(w => !w.DmOnly))
        {
            var a = GridPoint(ox, oy, cell, wall.FromX, wall.FromY);
            var b = GridPoint(ox, oy, cell, wall.ToX, wall.ToY);
            var horizontal = Math.Abs(wall.ToX - wall.FromX) >= Math.Abs(wall.ToY - wall.FromY);
            var segmentRect = horizontal
                ? new Rect(Math.Min(a.X, b.X), a.Y - cell * 0.16, Math.Max(cell * 0.25, Math.Abs(b.X - a.X)), cell * 0.32)
                : new Rect(a.X - cell * 0.16, Math.Min(a.Y, b.Y), cell * 0.32, Math.Max(cell * 0.25, Math.Abs(b.Y - a.Y)));
            if (TryDrawAsset(dc, map, wall.AssetKey, wall.FromX, wall.FromY, segmentRect, horizontal ? 0 : 90)) continue;

            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(26, 25, 23)), Math.Max(5, cell * 0.2)) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }, a, b);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(111, 104, 90)), Math.Max(2.2, cell * 0.085)) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }, a, b);
        }
    }

    private void DrawDoors(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var door in map.Doors.Where(d => !d.DmOnly && (!d.Secret || d.Discovered)))
        {
            var horizontal = door.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase);
            var a = GridPoint(ox, oy, cell, door.X, door.Y);
            var b = horizontal ? GridPoint(ox, oy, cell, door.X + 1, door.Y) : GridPoint(ox, oy, cell, door.X, door.Y + 1);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(51, 48, 42)), Math.Max(6, cell * 0.23)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, a, b);

            var open = door.State.Equals("open", StringComparison.OrdinalIgnoreCase);
            var rect = horizontal
                ? new Rect(a.X, a.Y - cell * 0.18, cell, cell * 0.36)
                : new Rect(a.X - cell * 0.18, a.Y, cell * 0.36, cell);
            if (TryDrawAsset(dc, map, door.AssetKey, door.X, door.Y, rect, horizontal ? 0 : 90, open ? -75 : 0)) continue;

            var locked = door.State.Equals("locked", StringComparison.OrdinalIgnoreCase) || door.State.Equals("barred", StringComparison.OrdinalIgnoreCase);
            var doorPen = new Pen(new SolidColorBrush(locked ? Color.FromRgb(157, 98, 58) : Color.FromRgb(128, 86, 47)), Math.Max(3, cell * 0.11));
            if (!open)
            {
                dc.DrawLine(doorPen, a, b);
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(200, 164, 86)), null, mid, Math.Max(1.5, cell * 0.055), Math.Max(1.5, cell * 0.055));
            }
            else
            {
                var openEnd = horizontal ? new Point(a.X, a.Y - cell * 0.78) : new Point(a.X + cell * 0.78, a.Y);
                dc.DrawLine(doorPen, a, openEnd);
            }
        }
    }

    private void DrawProps(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var prop in map.Props.Where(p => !p.DmOnly))
        {
            var rawRect = GridRect(ox, oy, cell, prop.X, prop.Y, prop.WidthSquares, prop.HeightSquares);
            if (TryDrawAsset(dc, map, prop.AssetKey, prop.X, prop.Y, rawRect)) continue;

            var rect = rawRect;
            rect.Inflate(-Math.Max(2, cell * 0.12), -Math.Max(2, cell * 0.12));
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var fill = new SolidColorBrush(TacticalMapAssetPalette.PropColor(prop.AssetKey));
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 40, 35, 29)), Math.Max(1, cell * 0.045));
            var key = prop.AssetKey.ToLowerInvariant();
            if (key.Contains("pillar") || key.Contains("statue"))
            {
                var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)), null, new Point(center.X + cell * 0.08, center.Y + cell * 0.08), rect.Width * 0.42, rect.Height * 0.42);
                dc.DrawEllipse(fill, outline, center, rect.Width * 0.42, rect.Height * 0.42);
            }
            else if (key.Contains("rubble"))
            {
                for (var i = 0; i < 7; i++)
                {
                    var hash = StableHash(map.Seed + i * 97, prop.X, prop.Y, prop.AssetKey);
                    var px = rect.Left + ((hash & 0xFF) / 255d) * rect.Width;
                    var py = rect.Top + (((hash >> 8) & 0xFF) / 255d) * rect.Height;
                    var r = Math.Max(2, cell * (0.06 + ((hash >> 16) & 3) * 0.02));
                    dc.DrawEllipse(fill, outline, new Point(px, py), r, r * 0.7);
                }
            }
            else dc.DrawRoundedRectangle(fill, outline, rect, Math.Max(1, cell * 0.06), Math.Max(1, cell * 0.06));
        }
    }

    private static void DrawRoomLabels(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        if (cell < 16) return;
        foreach (var room in map.Rooms.Where(r => !r.DmOnly))
            DrawText(dc, room.Name.ToUpperInvariant(), Math.Clamp(cell * 0.24, 7.5, 11), new SolidColorBrush(Color.FromArgb(130, 225, 216, 195)),
                ox + (room.X + room.WidthSquares / 2d) * cell, oy + (room.Y + room.HeightSquares / 2d) * cell, true, FontWeights.SemiBold);
    }

    private static void DrawDmLayer(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        foreach (var zone in map.Zones)
        {
            var rect = GridRect(ox, oy, cell, zone.X, zone.Y, zone.WidthSquares, zone.HeightSquares);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 150, 90, 160)),
                new Pen(new SolidColorBrush(Color.FromArgb(185, 183, 125, 190)), Math.Max(1, cell * 0.04)) { DashStyle = DashStyles.Dash }, rect);
            if (cell >= 14) DrawText(dc, zone.Name, 8, new SolidColorBrush(Color.FromRgb(219, 169, 225)), rect.Left + 3, rect.Top + 2, false, FontWeights.SemiBold);
        }
        foreach (var spawn in map.SpawnPoints)
        {
            var center = new Point(ox + (spawn.X + 0.5) * cell, oy + (spawn.Y + 0.5) * cell);
            var radius = Math.Max(4, cell * 0.18);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 196, 75, 70)), new Pen(new SolidColorBrush(Color.FromRgb(235, 126, 118)), 1), center, radius, radius);
        }
        foreach (var secret in map.Doors.Where(d => d.Secret && !d.Discovered))
        {
            var horizontal = secret.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase);
            var center = horizontal ? new Point(ox + (secret.X + 0.5) * cell, oy + secret.Y * cell) : new Point(ox + secret.X * cell, oy + (secret.Y + 0.5) * cell);
            DrawText(dc, "S", Math.Clamp(cell * 0.28, 8, 13), new SolidColorBrush(Color.FromRgb(221, 171, 93)), center.X, center.Y - 6, true, FontWeights.Bold);
        }
    }

    private void DrawEncounterTokens(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        if (Campaign is null || Encounter is null) return;
        var activeId = Encounter.Combatants.Count > 0 && Encounter.TurnIndex >= 0 && Encounter.TurnIndex < Encounter.Combatants.Count ? Encounter.Combatants[Encounter.TurnIndex].Id : null;
        foreach (var combatant in Encounter.Combatants.Where(c => c.Positioned && InBounds(map, c.GridX, c.GridY)))
        {
            var character = Campaign.Characters.FirstOrDefault(c => c.Id == combatant.CharacterId);
            var isPc = character?.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) == true;
            var center = new Point(ox + (combatant.GridX + 0.5) * cell, oy + (combatant.GridY + 0.5) * cell);
            var radius = Math.Max(6, cell * 0.32);
            var fill = new SolidColorBrush(isPc ? Color.FromRgb(49, 104, 148) : Color.FromRgb(139, 63, 57));
            var border = combatant.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase)
                ? new Pen(new SolidColorBrush(Color.FromRgb(226, 190, 104)), Math.Max(2, cell * 0.09))
                : new Pen(new SolidColorBrush(Color.FromRgb(215, 210, 195)), Math.Max(1, cell * 0.045));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, new Point(center.X + cell * 0.07, center.Y + cell * 0.08), radius, radius);
            dc.DrawEllipse(fill, border, center, radius, radius);
            var name = character?.Name ?? "?";
            var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));
            DrawText(dc, string.IsNullOrWhiteSpace(initials) ? "?" : initials, Math.Clamp(cell * 0.3, 8, 13), Brushes.White, center.X, center.Y - Math.Clamp(cell * 0.17, 4, 7), true, FontWeights.Bold);
        }
    }

    private static void DrawFog(DrawingContext dc, TacticalMap map, double ox, double oy, double cell)
    {
        if (!map.FogOfWarEnabled || map.Visibility.RevealAll) return;
        var revealed = map.Visibility.RevealedCells.ToHashSet();
        foreach (var roomId in map.Visibility.RevealedRoomIds)
        {
            var room = map.Rooms.FirstOrDefault(r => r.Id.Equals(roomId, StringComparison.OrdinalIgnoreCase));
            if (room is null) continue;
            for (var y = room.Y; y < room.Y + room.HeightSquares; y++)
            for (var x = room.X; x < room.X + room.WidthSquares; x++) revealed.Add(new TacticalMapCell(x, y));
        }
        var fog = new SolidColorBrush(Color.FromArgb(238, 4, 6, 7));
        for (var y = 0; y < map.HeightSquares; y++)
        for (var x = 0; x < map.WidthSquares; x++)
            if (!revealed.Contains(new TacticalMapCell(x, y))) dc.DrawRectangle(fog, null, CellRect(ox, oy, cell, x, y));
    }

    private bool TryDrawAsset(DrawingContext dc, TacticalMap map, string key, int x, int y, Rect rect, double orientationDegrees = 0, double stateDegrees = 0)
    {
        if (!AssetCatalog.TryResolve(map.AssetSetId, key, map.Seed, x, y, out var resolved) || resolved is null) return false;
        var definition = resolved.Definition;
        var scale = Math.Max(0.05, definition.Scale);
        var drawRect = ScaleRect(rect, scale);
        var center = new Point(drawRect.Left + drawRect.Width / 2, drawRect.Top + drawRect.Height / 2);
        dc.PushOpacity(Math.Clamp(definition.Opacity, 0, 1));
        dc.PushTransform(new RotateTransform(resolved.Variant.RotationDegrees + orientationDegrees + stateDegrees, center.X, center.Y));
        if (resolved.Variant.FlipX || resolved.Variant.FlipY)
            dc.PushTransform(new ScaleTransform(resolved.Variant.FlipX ? -1 : 1, resolved.Variant.FlipY ? -1 : 1, center.X, center.Y));
        dc.DrawImage(resolved.Image, drawRect);
        if (resolved.Variant.FlipX || resolved.Variant.FlipY) dc.Pop();
        dc.Pop();
        dc.Pop();
        return true;
    }

    private static Rect ScaleRect(Rect rect, double scale)
    {
        if (Math.Abs(scale - 1) < 0.001) return rect;
        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        return new Rect(cx - rect.Width * scale / 2, cy - rect.Height * scale / 2, rect.Width * scale, rect.Height * scale);
    }

    private static bool InBounds(TacticalMap map, int x, int y) => x >= 0 && y >= 0 && x < map.WidthSquares && y < map.HeightSquares;
    private static Rect CellRect(double ox, double oy, double cell, int x, int y) => new(ox + x * cell, oy + y * cell, cell, cell);
    private static Rect GridRect(double ox, double oy, double cell, int x, int y, int width, int height) => new(ox + x * cell, oy + y * cell, width * cell, height * cell);
    private static Point GridPoint(double ox, double oy, double cell, double x, double y) => new(ox + x * cell, oy + y * cell);

    private static int StableHash(int seed, int x, int y, string key)
    {
        unchecked
        {
            var hash = seed == 0 ? 17 : seed;
            hash = hash * 31 + x;
            hash = hash * 31 + y;
            foreach (var ch in key ?? "") hash = hash * 31 + ch;
            return hash;
        }
    }

    private static Color ParseColor(string text, Color fallback)
    {
        try { return ColorConverter.ConvertFromString(text) is Color color ? color : fallback; }
        catch { return fallback; }
    }

    private void DrawCentered(DrawingContext dc, string text, double size, Brush brush)
        => DrawText(dc, text, size, brush, ActualWidth / 2, ActualHeight / 2, true, FontWeights.Normal);

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, double x, double y, bool centered, FontWeight weight)
    {
        var formatted = new FormattedText(text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0);
        dc.DrawText(formatted, new Point(centered ? x - formatted.Width / 2 : x, centered ? y - formatted.Height / 2 : y));
    }
}
