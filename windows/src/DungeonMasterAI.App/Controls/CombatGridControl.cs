using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.App.Controls;

public sealed class CombatGridControl : FrameworkElement
{
    public static readonly DependencyProperty CampaignProperty = DependencyProperty.Register(
        nameof(Campaign), typeof(CampaignState), typeof(CombatGridControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The authored tactical map bound to the encounter being fought, or <c>null</c> when the
    /// encounter has no map. The map is drawn underneath the combatant layer; it never replaces
    /// it, and every element above it renders exactly as it did before.
    /// </summary>
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(TacticalMap), typeof(CombatGridControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Whether DM-only map annotations — spawn points, undiscovered secret doors — are drawn.
    /// Bound to the shell's existing DM-view toggle so player-facing rendering stays player-facing.
    /// </summary>
    public static readonly DependencyProperty ShowDmMapLayerProperty = DependencyProperty.Register(
        nameof(ShowDmMapLayer), typeof(bool), typeof(CombatGridControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EncounterProperty = DependencyProperty.Register(
        nameof(Encounter), typeof(EncounterState), typeof(CombatGridControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(CombatGridControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewSpellIdProperty = DependencyProperty.Register(
        nameof(PreviewSpellId), typeof(string), typeof(CombatGridControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewCasterIdProperty = DependencyProperty.Register(
        nameof(PreviewCasterId), typeof(string), typeof(CombatGridControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewCenterXTextProperty = DependencyProperty.Register(
        nameof(PreviewCenterXText), typeof(string), typeof(CombatGridControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewCenterYTextProperty = DependencyProperty.Register(
        nameof(PreviewCenterYText), typeof(string), typeof(CombatGridControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewDirectionProperty = DependencyProperty.Register(
        nameof(PreviewDirection), typeof(string), typeof(CombatGridControl),
        new FrameworkPropertyMetadata("north", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewSlotLevelTextProperty = DependencyProperty.Register(
        nameof(PreviewSlotLevelText), typeof(string), typeof(CombatGridControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public CampaignState? Campaign
    {
        get => (CampaignState?)GetValue(CampaignProperty);
        set => SetValue(CampaignProperty, value);
    }

    public EncounterState? Encounter
    {
        get => (EncounterState?)GetValue(EncounterProperty);
        set => SetValue(EncounterProperty, value);
    }

    public TacticalMap? Map
    {
        get => (TacticalMap?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public bool ShowDmMapLayer
    {
        get => (bool)GetValue(ShowDmMapLayerProperty);
        set => SetValue(ShowDmMapLayerProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public string PreviewSpellId
    {
        get => (string)GetValue(PreviewSpellIdProperty);
        set => SetValue(PreviewSpellIdProperty, value);
    }

    public string PreviewCasterId
    {
        get => (string)GetValue(PreviewCasterIdProperty);
        set => SetValue(PreviewCasterIdProperty, value);
    }

    public string PreviewCenterXText
    {
        get => (string)GetValue(PreviewCenterXTextProperty);
        set => SetValue(PreviewCenterXTextProperty, value);
    }

    public string PreviewCenterYText
    {
        get => (string)GetValue(PreviewCenterYTextProperty);
        set => SetValue(PreviewCenterYTextProperty, value);
    }

    public string PreviewDirection
    {
        get => (string)GetValue(PreviewDirectionProperty);
        set => SetValue(PreviewDirectionProperty, value);
    }

    public string PreviewSlotLevelText
    {
        get => (string)GetValue(PreviewSlotLevelTextProperty);
        set => SetValue(PreviewSlotLevelTextProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        AaaDungeonFloor.Render(dc, bounds);

        if (Campaign is null || Encounter is null)
        {
            DrawCentered(dc, "Select an encounter to view the tactical battlefield.", 16, Brushes.LightGray);
            return;
        }

        var positioned = Encounter.Combatants.Where(c => c.Positioned).ToArray();
        var terrain = Encounter.Terrain.ToArray();
        var battlefieldEffects = Encounter.BattlefieldEffects.ToArray();
        var persistentEffectCells = battlefieldEffects
            .SelectMany(effect => SpellAreaGeometry.EnumerateCells(effect.Shape, effect.SizeFeet, effect.OriginX, effect.OriginY, effect.Direction, effect.WidthFeet)
                .Select(cell => (Effect: effect, Cell: cell)))
            .ToArray();
        var preview = BuildAreaPreview(Campaign, Encounter);
        var map = Map is { WidthSquares: > 0, HeightSquares: > 0 } ? Map : null;
        if (map is null && positioned.Length == 0 && terrain.Length == 0 && persistentEffectCells.Length == 0 && preview.AllCells.Count == 0)
        {
            DrawCentered(dc, "This encounter has no positioned combatants, tactical terrain, or area preview yet.", 16, Brushes.LightGray);
            return;
        }

        var xs = positioned.Select(c => c.GridX)
            .Concat(terrain.Select(t => t.GridX))
            .Concat(terrain.Select(t => t.GridX + Math.Max(1, t.WidthSquares) - 1))
            .Concat(persistentEffectCells.Select(c => c.Cell.X))
            .Concat(preview.AllCells.Select(c => c.X))
            .Concat(preview.HasOrigin ? new[] { preview.OriginX } : Array.Empty<int>())
            .ToArray();
        var ys = positioned.Select(c => c.GridY)
            .Concat(terrain.Select(t => t.GridY))
            .Concat(terrain.Select(t => t.GridY + Math.Max(1, t.HeightSquares) - 1))
            .Concat(persistentEffectCells.Select(c => c.Cell.Y))
            .Concat(preview.AllCells.Select(c => c.Y))
            .Concat(preview.HasOrigin ? new[] { preview.OriginY } : Array.Empty<int>())
            .ToArray();
        // With a bound map the viewport is the authored map, expanded only if a combatant sits
        // outside it (legacy encounter state saved before the map was bound). Without one the
        // viewport is derived from content exactly as it was before.
        int minX, maxX, minY, maxY;
        if (map is not null)
        {
            minX = Math.Min(0, xs.Length == 0 ? 0 : xs.Min());
            maxX = Math.Max(map.WidthSquares - 1, xs.Length == 0 ? map.WidthSquares - 1 : xs.Max());
            minY = Math.Min(0, ys.Length == 0 ? 0 : ys.Min());
            maxY = Math.Max(map.HeightSquares - 1, ys.Length == 0 ? map.HeightSquares - 1 : ys.Max());
        }
        else
        {
            minX = xs.Min() - 2;
            maxX = xs.Max() + 2;
            minY = ys.Min() - 2;
            maxY = ys.Max() + 2;
        }
        var columns = Math.Max(1, maxX - minX + 1);
        var rows = Math.Max(1, maxY - minY + 1);
        var margin = 34.0;
        var usableWidth = Math.Max(50, ActualWidth - (margin * 2));
        var usableHeight = Math.Max(50, ActualHeight - (margin * 2));
        // A 30x20 authored map does not fit at the 22px floor the combatant-only view uses, and a
        // map that only half fits is worse than no map, so the floor drops when one is bound.
        var minimumCell = map is null ? 22 : 8;
        var cell = Math.Clamp(Math.Min(usableWidth / columns, usableHeight / rows), minimumCell, 58);
        var gridWidth = columns * cell;
        var gridHeight = rows * cell;
        var originX = (ActualWidth - gridWidth) / 2;
        var originY = (ActualHeight - gridHeight) / 2;

        if (map is not null) DrawMapUnderlay(dc, map, originX, originY, cell, minX, minY);

        foreach (var feature in terrain)
        {
            var gx = feature.GridX - minX;
            var gy = feature.GridY - minY;
            var rect = new Rect(
                originX + gx * cell,
                originY + gy * cell,
                Math.Max(1, feature.WidthSquares) * cell,
                Math.Max(1, feature.HeightSquares) * cell);
            var fill = feature.BlocksMovement
                ? new SolidColorBrush(Color.FromArgb(125, 95, 78, 68))
                : feature.DifficultTerrain
                    ? new SolidColorBrush(Color.FromArgb(105, 92, 105, 70))
                    : new SolidColorBrush(Color.FromArgb(75, 65, 78, 92));
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(130, 139, 150)), 1.5);
            dc.DrawRectangle(fill, outline, rect);
            var cover = (feature.Cover ?? "none").Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : $" • {feature.Cover} cover";
            var difficult = feature.DifficultTerrain ? " • difficult" : "";
            var blocked = feature.BlocksMovement ? " • blocked" : "";
            var sight = feature.BlocksLineOfSight ? " • blocks sight" : "";
            var obscure = feature.HeavilyObscured ? " • heavily obscured" : "";
            DrawText(dc, $"{feature.Name}{cover}{difficult}{blocked}{sight}{obscure}", 9, Brushes.Gainsboro, rect.Left + 4, rect.Top + 3, centered: false, FontWeights.SemiBold);
        }

        foreach (var effect in battlefieldEffects)
        {
            var cells = SpellAreaGeometry.EnumerateCells(effect.Shape, effect.SizeFeet, effect.OriginX, effect.OriginY, effect.Direction, effect.WidthFeet);
            var harmful = !string.IsNullOrWhiteSpace(effect.DamageExpression);
            var fill = harmful
                ? new SolidColorBrush(Color.FromArgb(70, 185, 78, 72))
                : effect.HeavilyObscured || effect.BlocksLineOfSight
                    ? new SolidColorBrush(Color.FromArgb(80, 92, 98, 110))
                    : new SolidColorBrush(Color.FromArgb(62, 80, 135, 110));
            var border = new Pen(new SolidColorBrush(harmful ? Color.FromRgb(210, 105, 95) : Color.FromRgb(120, 170, 145)), 1.2) { DashStyle = DashStyles.Dash };
            foreach (var effectCell in cells)
            {
                var rect = new Rect(
                    originX + (effectCell.X - minX) * cell,
                    originY + (effectCell.Y - minY) * cell,
                    cell,
                    cell);
                dc.DrawRectangle(fill, border, rect);
            }

            var labelX = originX + (effect.OriginX - minX) * cell + 3;
            var labelY = originY + (effect.OriginY - minY) * cell + 3;
            var tags = new List<string>();
            if (effect.DifficultTerrain) tags.Add("difficult");
            if (effect.HeavilyObscured) tags.Add("obscured");
            if (effect.BlocksLineOfSight) tags.Add("blocks sight");
            if (!string.IsNullOrWhiteSpace(effect.DamageExpression)) tags.Add($"{effect.DamageExpression} {effect.DamageType}".Trim());
            var suffix = tags.Count == 0 ? "" : $" • {string.Join(" • ", tags)}";
            DrawText(dc, $"{effect.Name}{suffix}", 9, Brushes.WhiteSmoke, labelX, labelY, centered: false, FontWeights.SemiBold);
        }

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(66, 72, 80)), 1);
        for (var x = 0; x <= columns; x++)
            dc.DrawLine(gridPen, new Point(originX + x * cell, originY), new Point(originX + x * cell, originY + gridHeight));
        for (var y = 0; y <= rows; y++)
            dc.DrawLine(gridPen, new Point(originX, originY + y * cell), new Point(originX + gridWidth, originY + y * cell));

        if (preview.AllCells.Count > 0)
        {
            var effective = preview.EffectiveCells.ToHashSet();
            var activeFill = new SolidColorBrush(Color.FromArgb(72, 224, 183, 92));
            var blockedFill = new SolidColorBrush(Color.FromArgb(58, 150, 65, 65));
            var activeBorder = new Pen(new SolidColorBrush(Color.FromArgb(185, 224, 183, 92)), 1.4);
            var blockedBorder = new Pen(new SolidColorBrush(Color.FromArgb(170, 190, 95, 90)), 1.1) { DashStyle = DashStyles.Dash };
            foreach (var areaCell in preview.AllCells)
            {
                var rect = new Rect(
                    originX + (areaCell.X - minX) * cell,
                    originY + (areaCell.Y - minY) * cell,
                    cell,
                    cell);
                var isEffective = effective.Contains(areaCell);
                dc.DrawRectangle(isEffective ? activeFill : blockedFill, isEffective ? activeBorder : blockedBorder, rect);
            }

            if (preview.HasOrigin)
            {
                var center = new Point(
                    originX + (preview.OriginX - minX) * cell + cell / 2,
                    originY + (preview.OriginY - minY) * cell + cell / 2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(210, 224, 183, 92)), null, center, Math.Max(3, cell * 0.1), Math.Max(3, cell * 0.1));
            }
        }

        var currentId = Encounter.Combatants.Count > 0 && Encounter.TurnIndex >= 0 && Encounter.TurnIndex < Encounter.Combatants.Count
            ? Encounter.Combatants[Encounter.TurnIndex].Id
            : null;

        foreach (var combatant in positioned)
        {
            var character = Campaign.Characters.FirstOrDefault(c => c.Id == combatant.CharacterId);
            var name = character?.Name ?? "?";
            var gx = combatant.GridX - minX;
            var gy = combatant.GridY - minY;
            var center = new Point(originX + gx * cell + cell / 2, originY + gy * cell + cell / 2);
            var radius = Math.Max(9, cell * 0.34);
            var isPc = character?.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) == true;
            var fill = isPc
                ? new SolidColorBrush(Color.FromRgb(45, 105, 150))
                : new SolidColorBrush(Color.FromRgb(145, 62, 58));
            var border = combatant.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase)
                ? new Pen(new SolidColorBrush(Color.FromRgb(224, 183, 92)), 4)
                : new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 1.5);
            dc.DrawEllipse(fill, border, center, radius, radius);

            var initials = string.Join("", name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => char.ToUpperInvariant(x[0])));
            DrawText(dc, initials.Length == 0 ? "?" : initials, 12, Brushes.White, center.X, center.Y - 7, centered: true, FontWeights.Bold);
            DrawText(dc, name, 11, Brushes.WhiteSmoke, center.X, center.Y + radius + 4, centered: true, FontWeights.Normal);
            DrawText(dc, $"HP {character?.CurrentHp ?? 0}/{character?.MaxHp ?? 0}  Move {combatant.MovementRemainingFeet}'", 10, Brushes.LightGray, center.X, center.Y + radius + 18, centered: true, FontWeights.Normal);
            var state = combatant.IsHidden ? "Hidden" : combatant.ReadiedAction is not null ? $"Ready: {combatant.ReadiedAction.Kind}" : "";
            if (!string.IsNullOrWhiteSpace(state))
                DrawText(dc, state, 9, Brushes.Gold, center.X, center.Y + radius + 31, centered: true, FontWeights.SemiBold);
        }

        var previewLabel = preview.AllCells.Count > 0
            ? $" • Preview: {preview.SpellName} ({preview.EffectiveCells.Count} effective square{(preview.EffectiveCells.Count == 1 ? "" : "s")}{(preview.OriginBlocked ? ", origin blocked" : "")})"
            : "";
        var scaleFeet = map?.FeetPerSquare > 0 ? map.FeetPerSquare : 5;
        // The map name goes last. The host views overlay tool buttons on the top-left of this
        // control, so anything prefixed to this line is the part that gets covered.
        var mapLabel = map is null ? "" : $" • {map.Name}";
        DrawText(dc, $"Each square = {scaleFeet} feet • terrain affects movement, cover, line of sight, obscurement, and area line of effect{previewLabel}{mapLabel}", 11, Brushes.Gray, 10, 8, centered: false, FontWeights.Normal);
    }

    /// <summary>
    /// Draws the authored map beneath the combatant layer: room floors, walls and doors, blocking
    /// and difficult terrain, props, and — under the DM layer — spawn points.
    /// <para>
    /// This is a deliberately compact underlay, not a second copy of <see cref="TacticalMapControl"/>.
    /// It shares that control's material palette so a map looks like itself in both places, and it
    /// draws only what changes how the fight plays: where you can stand, what costs extra to cross,
    /// and what stops movement or sight. Lighting, fog, decorative variation, and asset-pack images
    /// stay with the Map Builder, which is where they are being judged.
    /// </para>
    /// </summary>
    private void DrawMapUnderlay(DrawingContext dc, TacticalMap map, double originX, double originY, double cell, int minX, int minY)
    {
        Rect CellRect(int x, int y) => new(originX + (x - minX) * cell, originY + (y - minY) * cell, cell, cell);

        var bounds = new Rect(
            originX + (0 - minX) * cell,
            originY + (0 - minY) * cell,
            map.WidthSquares * cell,
            map.HeightSquares * cell);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 17)),
            new Pen(new SolidColorBrush(Color.FromArgb(90, 181, 145, 76)), 2), bounds);

        foreach (var room in map.Rooms.Where(r => !r.DmOnly || ShowDmMapLayer))
        {
            var floor = new SolidColorBrush(TacticalMapAssetPalette.MaterialColor(room.FloorAssetKey, 2));
            for (var y = room.Y; y < room.Y + Math.Max(1, room.HeightSquares); y++)
                for (var x = room.X; x < room.X + Math.Max(1, room.WidthSquares); x++)
                {
                    if (x < 0 || y < 0 || x >= map.WidthSquares || y >= map.HeightSquares) continue;
                    dc.DrawRectangle(floor, null, CellRect(x, y));
                }
        }

        foreach (var feature in map.Terrain.Where(t => !t.DmOnly || ShowDmMapLayer))
        {
            var fill = new SolidColorBrush(TacticalMapAssetPalette.TerrainColor(feature.AssetKey, feature.TerrainType));
            for (var y = feature.Y; y < feature.Y + Math.Max(1, feature.HeightSquares); y++)
                for (var x = feature.X; x < feature.X + Math.Max(1, feature.WidthSquares); x++)
                {
                    if (x < 0 || y < 0 || x >= map.WidthSquares || y >= map.HeightSquares) continue;
                    dc.DrawRectangle(fill, null, CellRect(x, y));
                }
        }

        foreach (var prop in map.Props.Where(p => !p.DmOnly || ShowDmMapLayer))
        {
            var fill = new SolidColorBrush(TacticalMapAssetPalette.PropColor(prop.AssetKey));
            for (var y = prop.Y; y < prop.Y + Math.Max(1, prop.HeightSquares); y++)
                for (var x = prop.X; x < prop.X + Math.Max(1, prop.WidthSquares); x++)
                {
                    if (x < 0 || y < 0 || x >= map.WidthSquares || y >= map.HeightSquares) continue;
                    var rect = CellRect(x, y);
                    rect.Inflate(-cell * 0.14, -cell * 0.14);
                    dc.DrawRectangle(fill, new Pen(new SolidColorBrush(Color.FromArgb(120, 24, 22, 19)), 1), rect);
                }
        }

        // Unwalkable squares are hatched rather than merely tinted: this is the layer that now
        // rejects a move, so the player must be able to see the refusal before making it.
        var blockedFill = new SolidColorBrush(Color.FromArgb(96, 12, 14, 16));
        var difficultPen = new Pen(new SolidColorBrush(Color.FromArgb(105, 176, 158, 104)), Math.Max(0.6, cell * 0.05))
        {
            DashStyle = DashStyles.Dot
        };
        for (var y = 0; y < map.HeightSquares; y++)
            for (var x = 0; x < map.WidthSquares; x++)
            {
                var rect = CellRect(x, y);
                if (!TacticalMapGeometry.IsCellWalkable(map, x, y))
                {
                    dc.DrawRectangle(blockedFill, null, rect);
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 205, 196, 176)), Math.Max(0.5, cell * 0.04)),
                        new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Bottom));
                }
                else if (TacticalMapGeometry.IsDifficultTerrain(map, x, y))
                {
                    dc.DrawLine(difficultPen, new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Top));
                }
            }

        var wallPen = new Pen(new SolidColorBrush(Color.FromRgb(126, 118, 100)), Math.Max(1.6, cell * 0.14));
        foreach (var wall in map.Walls.Where(w => !w.DmOnly || ShowDmMapLayer))
            dc.DrawLine(wallPen,
                new Point(originX + (wall.FromX - minX) * cell, originY + (wall.FromY - minY) * cell),
                new Point(originX + (wall.ToX - minX) * cell, originY + (wall.ToY - minY) * cell));

        foreach (var door in map.Doors)
        {
            if (door.Secret && !door.Discovered && !ShowDmMapLayer) continue;
            if (door.DmOnly && !ShowDmMapLayer) continue;
            var open = door.State.Equals("open", StringComparison.OrdinalIgnoreCase);
            var color = door.Secret
                ? Color.FromRgb(126, 96, 148)
                : open ? Color.FromRgb(120, 158, 122) : Color.FromRgb(168, 122, 64);
            var pen = new Pen(new SolidColorBrush(color), Math.Max(2.0, cell * 0.2));
            var start = new Point(originX + (door.X - minX) * cell, originY + (door.Y - minY) * cell);
            var end = door.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
                ? new Point(originX + (door.X + 1 - minX) * cell, originY + (door.Y - minY) * cell)
                : new Point(originX + (door.X - minX) * cell, originY + (door.Y + 1 - minY) * cell);
            dc.DrawLine(pen, start, end);
        }

        if (!ShowDmMapLayer) return;

        foreach (var spawn in map.SpawnPoints)
        {
            if (spawn.X < 0 || spawn.Y < 0 || spawn.X >= map.WidthSquares || spawn.Y >= map.HeightSquares) continue;
            var rect = CellRect(spawn.X, spawn.Y);
            var side = CombatSide.TryNormalize(spawn.Side);
            var color = side switch
            {
                CombatSide.Party => Color.FromArgb(150, 74, 140, 190),
                CombatSide.Opposition => Color.FromArgb(150, 190, 92, 84),
                CombatSide.Neutral => Color.FromArgb(150, 176, 158, 104),
                // An unrecognized side reaches campaign readiness as an error. Draw it as one
                // rather than picking a colour that implies the engine understood it.
                _ => Color.FromArgb(150, 150, 90, 190)
            };
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(color), Math.Max(1.2, cell * 0.08)) { DashStyle = DashStyles.Dash },
                new Rect(rect.X + cell * 0.12, rect.Y + cell * 0.12, cell * 0.76, cell * 0.76));
            // A short glyph rather than the side's full name: the marker is one grid square wide,
            // and "opposition" rendered at 9pt overruns it into the neighbouring squares.
            var glyph = side switch
            {
                CombatSide.Party => "P",
                CombatSide.Opposition => "O",
                CombatSide.Neutral => "N",
                _ => "?"
            };
            if (cell >= 20)
                DrawText(dc, glyph, Math.Min(11, cell * 0.3), new SolidColorBrush(color), rect.Left + cell / 2, rect.Top + cell * 0.2, centered: true, FontWeights.Bold);
        }
    }

    private AreaPreview BuildAreaPreview(CampaignState campaign, EncounterState encounter)
    {
        if (string.IsNullOrWhiteSpace(PreviewSpellId) || string.IsNullOrWhiteSpace(PreviewCasterId)) return AreaPreview.Empty;
        var spell = campaign.Spells.FirstOrDefault(s => s.Id.Equals(PreviewSpellId, StringComparison.OrdinalIgnoreCase)
            || s.Key.Equals(PreviewSpellId, StringComparison.OrdinalIgnoreCase));
        if (spell is null || (spell.Resolution is not ("area_save" or "persistent_area")) || spell.AreaSizeFeet <= 0) return AreaPreview.Empty;
        var caster = encounter.Combatants.FirstOrDefault(c => c.Positioned && c.CharacterId.Equals(PreviewCasterId, StringComparison.OrdinalIgnoreCase));
        if (caster is null) return AreaPreview.Empty;

        var originKind = (spell.AreaOrigin ?? "self").Trim().ToLowerInvariant();
        int ox;
        int oy;
        if (originKind == "point")
        {
            if (!int.TryParse(PreviewCenterXText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ox)
                || !int.TryParse(PreviewCenterYText, NumberStyles.Integer, CultureInfo.InvariantCulture, out oy))
                return AreaPreview.Empty;
        }
        else
        {
            ox = caster.GridX;
            oy = caster.GridY;
        }

        var previewSizeFeet = spell.AreaSizeFeet;
        if (spell.Level > 0 && spell.ExtraAreaSizePerSlotFeet > 0
            && int.TryParse(PreviewSlotLevelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var previewSlot)
            && previewSlot >= spell.Level)
            previewSizeFeet = checked(spell.AreaSizeFeet + (previewSlot - spell.Level) * spell.ExtraAreaSizePerSlotFeet);

        IReadOnlyList<(int X, int Y)> all;
        try
        {
            all = SpellAreaGeometry.EnumerateCells(spell.AreaShape, previewSizeFeet, ox, oy, PreviewDirection, spell.AreaWidthFeet)
                .Where(c => originKind != "self" || c.X != caster.GridX || c.Y != caster.GridY)
                .ToArray();
        }
        catch
        {
            return AreaPreview.Empty;
        }

        var effective = all.Where(c => !IsLineOfEffectBlocked(encounter, ox, oy, c.X, c.Y)).ToArray();
        var originBlocked = originKind == "point" && IsLineOfEffectBlocked(encounter, caster.GridX, caster.GridY, ox, oy);
        return new AreaPreview(spell.Name, ox, oy, true, originBlocked, all, effective);
    }

    private static bool IsLineOfEffectBlocked(EncounterState encounter, int originX, int originY, int targetX, int targetY)
    {
        foreach (var (x, y) in SpellAreaGeometry.TraceGridLine(originX, originY, targetX, targetY))
        {
            if (encounter.Terrain.Any(t => ContainsSquare(t, x, y)
                && (t.BlocksLineOfSight || NormalizeCover(t.Cover) == "total"))
                || encounter.BattlefieldEffects.Any(e => e.BlocksLineOfSight && SpellAreaGeometry.ContainsCell(e.Shape, e.SizeFeet, e.OriginX, e.OriginY, x, y, e.Direction, e.WidthFeet)))
                return true;
        }
        return false;
    }

    private static bool ContainsSquare(TerrainFeature terrain, int x, int y) =>
        x >= terrain.GridX && x < terrain.GridX + Math.Max(1, terrain.WidthSquares)
        && y >= terrain.GridY && y < terrain.GridY + Math.Max(1, terrain.HeightSquares);

    private static string NormalizeCover(string? value) =>
        (value ?? "none").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-") switch
        {
            "threequarters" => "three-quarters",
            var normalized => normalized
        };

    private void DrawCentered(DrawingContext dc, string text, double size, Brush brush) =>
        DrawText(dc, text, size, brush, ActualWidth / 2, ActualHeight / 2, centered: true, FontWeights.Normal);

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, double x, double y, bool centered, FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            1.0);
        var point = centered ? new Point(x - formatted.Width / 2, y) : new Point(x, y);
        dc.DrawText(formatted, point);
    }

    private sealed record AreaPreview(
        string SpellName,
        int OriginX,
        int OriginY,
        bool HasOrigin,
        bool OriginBlocked,
        IReadOnlyList<(int X, int Y)> AllCells,
        IReadOnlyList<(int X, int Y)> EffectiveCells)
    {
        public static AreaPreview Empty { get; } = new("", 0, 0, false, false, [], []);
    }
}
