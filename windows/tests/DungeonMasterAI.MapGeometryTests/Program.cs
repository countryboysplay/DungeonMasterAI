using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.MapGeometryTests;

/// <summary>
/// r53's map-schema and geometry assertions, carried over verbatim from
/// <c>DungeonMasterAI.MapRendererTests</c> when the WPF front end was removed in r63. The renderer
/// half of that suite -- constructing <c>TacticalMapControl</c> and writing a 1280x720 PNG -- went
/// with WPF. Everything below is Domain/Engine and never had a UI dependency; the fixture map,
/// the checks and their messages are unchanged.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var failures = new List<string>();
        try
        {
            var map = BuildRuinedCrypt();
            var report = TacticalMapGeometry.Validate(map);
            Check(report.IsValid, $"Prototype map validates with no errors. Found {report.Errors} error(s).", failures);

            var door = map.Doors.Single(d => d.Name == "Crypt Gate");
            Check(!TacticalMapGeometry.CanMoveBetween(map, new TacticalMapCell(13, 8), new TacticalMapCell(14, 8)),
                "Closed authored door blocks movement across its wall edge.", failures);
            door.State = "open";
            Check(TacticalMapGeometry.CanMoveBetween(map, new TacticalMapCell(13, 8), new TacticalMapCell(14, 8)),
                "Opening the authored door makes the same edge traversable.", failures);
            door.State = "closed";

            Check(!TacticalMapGeometry.IsCellWalkable(map, 10, 6), "Blocking pillar occupies authoritative movement geometry.", failures);
            Check(TacticalMapGeometry.IsDifficultTerrain(map, 18, 5), "Flooded crypt is difficult terrain.", failures);
            Check(TacticalMapGeometry.MovementCostFeet(map, 18, 5) == 10, "Difficult terrain doubles 5-foot movement cost.", failures);
            Check(!TacticalMapGeometry.HasLineOfSight(map, new TacticalMapCell(8, 6), new TacticalMapCell(12, 6)),
                "Blocking pillar interrupts line of sight.", failures);
            Check(TacticalMapGeometry.HasLineOfSight(map, new TacticalMapCell(4, 4), new TacticalMapCell(8, 4)),
                "Open chamber preserves line of sight.", failures);

            var campaign = new CampaignState { Name = "Map Prototype" };
            campaign.TacticalMaps.Add(map);
            var encounter = new EncounterState { Name = "Ruined Crypt Encounter", Status = "active", Round = 2, TurnIndex = 0 };
            campaign.Encounters.Add(encounter);
            campaign.EncounterMapBindings[encounter.Id] = map.Id;

            var hero = new CharacterSheet { Name = "Aeliana", CharacterType = "pc", CurrentHp = 28, MaxHp = 34 };
            var enemy = new CharacterSheet { Name = "Crypt Warden", CharacterType = "npc", CurrentHp = 31, MaxHp = 31 };
            campaign.Characters.Add(hero);
            campaign.Characters.Add(enemy);
            encounter.Combatants.Add(new CombatantState { CharacterId = hero.Id, Side = "player", Positioned = true, GridX = 6, GridY = 6, Initiative = 18 });
            encounter.Combatants.Add(new CombatantState { CharacterId = enemy.Id, Side = "enemy", Positioned = true, GridX = 20, GridY = 8, Initiative = 14 });

            var json = JsonSerializer.Serialize(campaign);
            var restored = JsonSerializer.Deserialize<CampaignState>(json);
            Check(restored?.TacticalMaps.Count == 1, "Tactical map survives campaign JSON serialization.", failures);
            Check(restored?.EncounterMapBindings.TryGetValue(encounter.Id, out var restoredMapId) == true && restoredMapId == map.Id,
                "Encounter-to-map binding survives campaign JSON serialization.", failures);

            if (failures.Count == 0)
            {
                Console.WriteLine("MAP GEOMETRY PASS");
                Console.WriteLine("Schema validation, movement, line of sight, and campaign serialization verified.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled map geometry exception: {ex}");
        }

        Console.Error.WriteLine($"MAP GEOMETRY FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static TacticalMap BuildRuinedCrypt()
    {
        var map = new TacticalMap
        {
            Name = "The Ruined Crypt of Saint Veyra",
            Key = "ruined_crypt_veyra",
            MapType = "dungeon",
            Theme = "abandoned_crypt",
            AssetSetId = "core.fantasy.crypt",
            WidthSquares = 28,
            HeightSquares = 18,
            FeetPerSquare = 5,
            Seed = 784211,
            FogOfWarEnabled = false
        };

        map.Rooms.Add(new TacticalMapRoom
        {
            Name = "Upper Reliquary",
            X = 2,
            Y = 2,
            WidthSquares = 24,
            HeightSquares = 14,
            FloorAssetKey = "floor.stone.crypt_flagstone",
            WallAssetKey = "wall.stone.crypt_block"
        });

        map.Walls.AddRange([
            new TacticalMapWall { FromX = 2, FromY = 2, ToX = 26, ToY = 2, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 26, FromY = 2, ToX = 26, ToY = 16, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 26, FromY = 16, ToX = 2, ToY = 16, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 2, FromY = 16, ToX = 2, ToY = 2, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 14, FromY = 2, ToX = 14, ToY = 16, AssetKey = "wall.stone.crypt_block" },
            new TacticalMapWall { FromX = 2, FromY = 10, ToX = 14, ToY = 10, AssetKey = "wall.stone.crypt_block" }
        ]);

        map.Doors.AddRange([
            new TacticalMapDoor { Name = "Crypt Gate", X = 14, Y = 8, Orientation = "vertical", State = "closed", AssetKey = "door.wood.ironbound" },
            new TacticalMapDoor { Name = "Broken South Door", X = 8, Y = 10, Orientation = "horizontal", State = "open", AssetKey = "door.wood.broken" },
            new TacticalMapDoor { Name = "Reliquary Secret", X = 14, Y = 13, Orientation = "vertical", State = "closed", Secret = true, Discovered = false, AssetKey = "door.stone.secret" }
        ]);

        map.Terrain.AddRange([
            new TacticalMapTerrain { Name = "Flooded Crypt", TerrainType = "water", X = 17, Y = 4, WidthSquares = 5, HeightSquares = 3, AssetKey = "terrain.water.crypt_shallow", DifficultTerrain = true },
            new TacticalMapTerrain { Name = "Collapsed Masonry", TerrainType = "rubble", X = 5, Y = 11, WidthSquares = 4, HeightSquares = 3, AssetKey = "terrain.rubble.stone", DifficultTerrain = true }
        ]);

        map.Props.AddRange([
            new TacticalMapProp { Name = "Central Pillar", AssetKey = "prop.pillar.stone_round", X = 10, Y = 6, BlocksMovement = true, BlocksLineOfSight = true, Cover = "three_quarters" },
            new TacticalMapProp { Name = "Broken Pillar", AssetKey = "prop.rubble.pillar", X = 7, Y = 12, DifficultTerrain = true, Cover = "half" },
            new TacticalMapProp { Name = "Saint Veyra's Altar", AssetKey = "prop.altar.stone_crypt", X = 20, Y = 11, WidthSquares = 2, HeightSquares = 1, BlocksMovement = true, Cover = "half" },
            new TacticalMapProp { Name = "Stone Sarcophagus", AssetKey = "prop.sarcophagus.stone", X = 22, Y = 7, WidthSquares = 2, HeightSquares = 1, BlocksMovement = true, Cover = "half" }
        ]);

        map.Lights.AddRange([
            new TacticalMapLight { Name = "Western Torch", AssetKey = "light.torch.wall", X = 4, Y = 4, BrightRadiusFeet = 20, DimRadiusFeet = 20 },
            new TacticalMapLight { Name = "Altar Brazier", AssetKey = "light.brazier", X = 21, Y = 10.5, BrightRadiusFeet = 20, DimRadiusFeet = 20, Color = "#E39A52" }
        ]);

        map.SpawnPoints.AddRange([
            new TacticalMapSpawnPoint { Name = "Party Entrance", Side = "player", X = 5, Y = 5, DmOnly = true },
            new TacticalMapSpawnPoint { Name = "Crypt Warden", Side = "enemy", X = 20, Y = 8, CharacterKey = "crypt_warden", DmOnly = true }
        ]);
        map.Zones.Add(new TacticalMapZone { Name = "Warden Awakens", ZoneType = "encounter", X = 18, Y = 7, WidthSquares = 6, HeightSquares = 5, ReferenceId = "crypt_warden_encounter", DmOnly = true });

        return map;
    }

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
