using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

/// <summary>
/// Connects authored tactical maps to combat adjudication.
/// <para>
/// r53-r57 built map generation, asset packs, a Map Builder, and non-destructive map editing, and
/// none of it reached play: <see cref="TacticalMapGeometry"/> had no caller in the engine, so
/// combat ran on an unbounded empty grid regardless of the map bound to the encounter. Everything
/// here is deterministic and consults only authored geometry — the engine adjudicates, the model
/// narrates, and nothing in this file calls a model.
/// </para>
/// </summary>
public sealed partial class GameEngine
{
    /// <summary>
    /// The tactical map bound to an encounter, or <c>null</c> when the encounter has no map or the
    /// binding points at a map that is no longer in the campaign. A null map is a supported state:
    /// every consumer falls back to the pre-map behaviour so an unbound encounter still plays.
    /// </summary>
    public static TacticalMap? ResolveEncounterMap(CampaignState campaign, string? encounterId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (string.IsNullOrWhiteSpace(encounterId)) return null;
        if (campaign.TacticalMaps is not { Count: > 0 }) return null;

        var bindings = campaign.EncounterMapBindings;
        if (bindings is null || bindings.Count == 0) return null;

        // The declared case-insensitive comparer does not survive deserialization. Persistence
        // restores it, but a campaign built in memory by a caller that assigned a fresh dictionary
        // may not have it, so the fallback scan is not redundant.
        if (!bindings.TryGetValue(encounterId, out var mapId))
        {
            mapId = bindings.FirstOrDefault(pair => pair.Key.Equals(encounterId, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(mapId)) return null;
        }

        return campaign.TacticalMaps.FirstOrDefault(map => map.Id.Equals(mapId, StringComparison.OrdinalIgnoreCase))
            ?? campaign.TacticalMaps.FirstOrDefault(map => map.Key.Equals(mapId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks the starting square for a combatant joining an encounter.
    /// <para>
    /// With a bound map, authored spawn points for the combatant's side are consumed in declaration
    /// order — the first that is inside the map, walkable, and unoccupied wins — so the party lands
    /// at the entrance the map author drew rather than in row 0. When no map is bound, or its spawn
    /// points for that side are exhausted or unusable, this falls back to the original
    /// index-derived placement so unbound encounters behave exactly as before.
    /// </para>
    /// </summary>
    private static (int GridX, int GridY) ChooseStartingSquare(
        TacticalMap? map, EncounterState encounter, string side, int fallbackRow)
    {
        if (map is not null)
        {
            var canonical = CombatSide.Normalize(side, CombatSide.Opposition);
            foreach (var spawn in map.SpawnPoints)
            {
                if (CombatSide.TryNormalize(spawn.Side) != canonical) continue;
                if (!TacticalMapGeometry.IsCellWalkable(map, spawn.X, spawn.Y)) continue;
                if (IsSquareOccupied(encounter, spawn.X, spawn.Y)) continue;
                return (spawn.X, spawn.Y);
            }

            // Spawn points are exhausted or unusable. Stay on the map: scan its cells in a stable
            // order for the first walkable, unoccupied square rather than placing the combatant
            // outside the authored bounds where no geometry rule applies to it.
            for (var y = 0; y < map.HeightSquares; y++)
                for (var x = 0; x < map.WidthSquares; x++)
                {
                    if (!TacticalMapGeometry.IsCellWalkable(map, x, y)) continue;
                    if (IsSquareOccupied(encounter, x, y)) continue;
                    return (x, y);
                }
        }

        return (FreePlacementColumn(encounter, fallbackRow), fallbackRow);
    }

    private static bool IsSquareOccupied(EncounterState encounter, int gridX, int gridY) =>
        encounter.Combatants.Any(c => c.Positioned && c.GridX == gridX && c.GridY == gridY);

    /// <summary>
    /// Rejects a placement that the bound map's geometry forbids. Called only where a square is
    /// chosen outright (initial placement and DM repositioning); step-by-step movement is checked
    /// by <see cref="ValidateMapMovementPath"/> instead, because a move must also honour the walls
    /// and doors on the edges it crosses.
    /// </summary>
    private static void EnsureMapSquarePlaceable(TacticalMap? map, int gridX, int gridY)
    {
        if (map is null) return;
        if (!TacticalMapGeometry.IsInside(map, gridX, gridY))
            throw new InvalidOperationException($"Grid ({gridX}, {gridY}) is outside the bound tactical map '{map.Name}' ({map.WidthSquares}x{map.HeightSquares} squares).");
        if (!TacticalMapGeometry.IsCellWalkable(map, gridX, gridY))
            throw new InvalidOperationException($"Grid ({gridX}, {gridY}) is not walkable on tactical map '{map.Name}'.");
    }

    /// <summary>
    /// Validates a traced movement path against the bound map: every square entered must be
    /// walkable, and every edge crossed must not be a wall or a closed door.
    /// </summary>
    private static void ValidateMapMovementPath(TacticalMap? map, int fromX, int fromY, IReadOnlyList<(int X, int Y)> path)
    {
        if (map is null) return;

        var previous = new TacticalMapCell(fromX, fromY);
        foreach (var (x, y) in path)
        {
            var next = new TacticalMapCell(x, y);
            if (!TacticalMapGeometry.IsInside(map, x, y))
                throw new InvalidOperationException($"Grid ({x}, {y}) is outside the bound tactical map '{map.Name}'.");
            if (!TacticalMapGeometry.IsCellWalkable(map, x, y))
                throw new InvalidOperationException($"Movement is blocked by tactical map geometry at grid ({x}, {y}).");
            if (!TacticalMapGeometry.CanTraverseStep(map, previous, next))
                throw new InvalidOperationException($"Movement from grid ({previous.X}, {previous.Y}) to ({x}, {y}) is blocked by a wall or closed door on '{map.Name}'.");
            previous = next;
        }
    }
}
