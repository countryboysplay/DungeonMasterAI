namespace DungeonMasterAI.Domain;

public sealed partial class CampaignState
{
    /// <summary>Authored tactical maps available to encounters in this campaign.</summary>
    public List<TacticalMap> TacticalMaps { get; set; } = [];

    /// <summary>
    /// Encounter ID -> tactical map ID. Keeping this association on CampaignState preserves
    /// compatibility with older serialized EncounterState objects.
    /// <para>
    /// The case-insensitive comparer declared here does not survive deserialization, because
    /// System.Text.Json builds a fresh dictionary for settable properties. Persistence restores it
    /// via <see cref="TacticalMapSchema.NormalizeBindings"/>; do not rely on the initializer alone.
    /// </para>
    /// </summary>
    public Dictionary<string, string> EncounterMapBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Authoritative tactical-map definition. Coordinates use zero-based grid cells.
/// Walls and doors live on grid edges/intersections. Artwork keys are presentation hints;
/// gameplay geometry remains authoritative even when an asset is missing.
/// </summary>
public sealed class TacticalMap
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Name { get; set; } = "Tactical Map";
    public string MapType { get; set; } = "dungeon";
    public string Theme { get; set; } = "stone_dungeon";
    public string AssetSetId { get; set; } = "core.fantasy.stone";
    public int WidthSquares { get; set; } = 30;
    public int HeightSquares { get; set; } = 20;
    public int FeetPerSquare { get; set; } = 5;

    /// <summary>
    /// Renderer seed. Asset variants are selected from this seed, so it may be rerolled without
    /// changing authoritative geometry.
    /// </summary>
    public int Seed { get; set; }

    /// <summary>
    /// Original seed supplied to the structured map generator, recorded at generation time so the
    /// authoritative geometry stays reproducible after <see cref="Seed"/> is rerolled for art.
    /// Zero only on maps serialized before the two seeds were separated; those are backfilled from
    /// <see cref="Seed"/> by <see cref="TacticalMapSchema"/> during state migration.
    /// </summary>
    public int GenerationSeed { get; set; }

    public bool FogOfWarEnabled { get; set; }
    public string SourceKind { get; set; } = CampaignProvenance.SourceCanon;
    public List<TacticalMapRoom> Rooms { get; set; } = [];
    public List<TacticalMapWall> Walls { get; set; } = [];
    public List<TacticalMapDoor> Doors { get; set; } = [];
    public List<TacticalMapTerrain> Terrain { get; set; } = [];
    public List<TacticalMapProp> Props { get; set; } = [];
    public List<TacticalMapLight> Lights { get; set; } = [];
    public List<TacticalMapSpawnPoint> SpawnPoints { get; set; } = [];
    public List<TacticalMapZone> Zones { get; set; } = [];
    public TacticalMapVisibility Visibility { get; set; } = new();
}

public sealed class TacticalMapRoom
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Room";
    public string Kind { get; set; } = "room"; // room, corridor, cave, exterior
    public int X { get; set; }
    public int Y { get; set; }
    public int WidthSquares { get; set; } = 1;
    public int HeightSquares { get; set; } = 1;
    public string FloorAssetKey { get; set; } = "floor.stone.flagstone";
    public string WallAssetKey { get; set; } = "wall.stone.block";
    public bool DmOnly { get; set; }
}

/// <summary>Wall segment in grid-line coordinates, normally axis-aligned.</summary>
public sealed class TacticalMapWall
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int FromX { get; set; }
    public int FromY { get; set; }
    public int ToX { get; set; }
    public int ToY { get; set; }
    public string AssetKey { get; set; } = "wall.stone.block";
    public bool BlocksMovement { get; set; } = true;
    public bool BlocksLineOfSight { get; set; } = true;
    public int HeightFeet { get; set; } = 10;
    public bool DmOnly { get; set; }
}

/// <summary>
/// Door anchored to a one-square grid edge. Vertical means the edge from (X,Y) to (X,Y+1);
/// horizontal means the edge from (X,Y) to (X+1,Y).
/// </summary>
public sealed class TacticalMapDoor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Door";
    public int X { get; set; }
    public int Y { get; set; }
    public string Orientation { get; set; } = "vertical"; // vertical, horizontal
    public string State { get; set; } = "closed"; // open, closed, locked, barred
    public bool Secret { get; set; }
    public bool Discovered { get; set; }
    public string AssetKey { get; set; } = "door.wood.ironbound";
    public bool BlocksMovementWhenClosed { get; set; } = true;
    public bool BlocksLineOfSightWhenClosed { get; set; } = true;
    public bool DmOnly { get; set; }
}

public sealed class TacticalMapTerrain
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Terrain";
    public string TerrainType { get; set; } = "floor";
    public int X { get; set; }
    public int Y { get; set; }
    public int WidthSquares { get; set; } = 1;
    public int HeightSquares { get; set; } = 1;
    public string AssetKey { get; set; } = "terrain.stone";
    public bool DifficultTerrain { get; set; }
    public bool BlocksMovement { get; set; }
    public bool BlocksLineOfSight { get; set; }
    public bool HeavilyObscured { get; set; }
    public string Cover { get; set; } = "none";
    public int ElevationFeet { get; set; }
    public bool DmOnly { get; set; }
}

public sealed class TacticalMapProp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Prop";
    public string AssetKey { get; set; } = "prop.rubble.small";
    public int X { get; set; }
    public int Y { get; set; }
    public int WidthSquares { get; set; } = 1;
    public int HeightSquares { get; set; } = 1;
    public int RotationDegrees { get; set; }
    public bool BlocksMovement { get; set; }
    public bool BlocksLineOfSight { get; set; }
    public bool DifficultTerrain { get; set; }
    public string Cover { get; set; } = "none";
    public bool DmOnly { get; set; }
}

public sealed class TacticalMapLight
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Light";
    public string AssetKey { get; set; } = "light.torch.wall";
    public double X { get; set; }
    public double Y { get; set; }
    public int BrightRadiusFeet { get; set; } = 20;
    public int DimRadiusFeet { get; set; } = 20;
    public string Color { get; set; } = "#F2B35F";
    public bool DmOnly { get; set; }
}

public sealed class TacticalMapSpawnPoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Spawn";

    /// <summary>
    /// Which side the creature placed here fights on. Values are the canonical
    /// <see cref="CombatSide"/> vocabulary; legacy and model-emitted synonyms are collapsed onto
    /// it by <see cref="TacticalMapSchema.NormalizeMap"/> when the record is loaded.
    /// </summary>
    public string Side { get; set; } = CombatSide.Opposition;
    public int X { get; set; }
    public int Y { get; set; }
    public string CharacterKey { get; set; } = "";
    public bool DmOnly { get; set; } = true;
}

public sealed class TacticalMapZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Zone";
    public string ZoneType { get; set; } = "encounter"; // encounter, trap, loot, quest, trigger
    public int X { get; set; }
    public int Y { get; set; }
    public int WidthSquares { get; set; } = 1;
    public int HeightSquares { get; set; } = 1;
    public string ReferenceId { get; set; } = "";
    public bool DmOnly { get; set; } = true;
}

public sealed class TacticalMapVisibility
{
    public bool RevealAll { get; set; } = true;
    public List<string> RevealedRoomIds { get; set; } = [];
    public List<TacticalMapCell> RevealedCells { get; set; } = [];
}

public readonly record struct TacticalMapCell(int X, int Y);
