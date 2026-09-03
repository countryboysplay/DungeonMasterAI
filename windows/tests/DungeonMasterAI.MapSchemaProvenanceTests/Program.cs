using System.Text.Json;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;

// Independent regression tests for tactical map state versioning and unified SourceKind
// provenance. Every failure is reported in one run rather than stopping at the first.

var failures = new List<string>();
var passed = 0;

// ---------------------------------------------------------------------------
// Provenance normalization
// ---------------------------------------------------------------------------

Run("legacy ai_generated provenance collapses onto ai_expanded", () =>
{
    Equal(CampaignProvenance.AiExpanded, CampaignProvenance.Normalize("ai_generated"), "normalized alias");
    Equal(CampaignProvenance.AiExpanded, CampaignProvenance.Normalize("AI_Generated"), "alias is case-insensitive");
    Equal(CampaignProvenance.AiExpanded, CampaignProvenance.Normalize("  ai_generated  "), "alias tolerates padding");
});

Run("IsAiGenerated recognizes both the canonical value and the legacy alias", () =>
{
    True(CampaignProvenance.IsAiGenerated("ai_expanded"), "canonical ai_expanded is AI generated");
    True(CampaignProvenance.IsAiGenerated("ai_generated"), "legacy alias is AI generated");
    True(!CampaignProvenance.IsAiGenerated("source_canon"), "source canon is not AI generated");
    True(!CampaignProvenance.IsAiGenerated("runtime_generated"), "runtime generated is not AI invented");
    True(!CampaignProvenance.IsAiGenerated(null), "null is not AI generated");
});

Run("blank provenance resolves to the caller's documented default, never a silent promotion", () =>
{
    Equal(CampaignProvenance.SourceCanon, CampaignProvenance.Normalize(""), "blank uses default fallback");
    Equal(CampaignProvenance.RuntimeGenerated,
        CampaignProvenance.Normalize("   ", CampaignProvenance.RuntimeGenerated),
        "blank honors an explicit fallback");
});

Run("unknown provenance is preserved verbatim and reported as unrecognized", () =>
{
    Equal("something_new", CampaignProvenance.Normalize("something_new"), "unknown value is not rewritten");
    True(!CampaignProvenance.IsRecognized("something_new"), "unknown value is not recognized");
    True(!CampaignProvenance.IsSourceCanon("something_new"), "unknown value is never treated as canon");
    True(CampaignProvenance.IsRecognized("ai_generated"), "legacy alias is recognized after normalization");
    foreach (var value in CampaignProvenance.All)
        True(CampaignProvenance.IsRecognized(value), $"canonical value {value} is recognized");
});

// ---------------------------------------------------------------------------
// Tactical map record normalization
// ---------------------------------------------------------------------------

Run("unversioned map records are raised to the current map schema version", () =>
{
    var map = new TacticalMap { SchemaVersion = 0 };
    True(TacticalMapSchema.NormalizeMap(map), "normalizing an unversioned map reports a repair");
    Equal(TacticalMapSchema.CurrentMapSchemaVersion, map.SchemaVersion, "map schema version");
});

Run("geometry seed is backfilled from the art seed only when it is missing", () =>
{
    var legacy = new TacticalMap { Seed = 4242, GenerationSeed = 0 };
    TacticalMapSchema.NormalizeMap(legacy);
    Equal(4242, legacy.GenerationSeed, "backfilled geometry seed");
    Equal(4242, legacy.Seed, "art seed is untouched by backfill");

    var current = new TacticalMap { Seed = 99, GenerationSeed = 4242 };
    TacticalMapSchema.NormalizeMap(current);
    Equal(4242, current.GenerationSeed, "existing geometry seed is never overwritten");
    Equal(99, current.Seed, "rerolled art seed is preserved");
});

Run("normalization repairs missing map collections", () =>
{
    var map = new TacticalMap
    {
        Rooms = null!, Walls = null!, Doors = null!, Terrain = null!,
        Props = null!, Lights = null!, SpawnPoints = null!, Zones = null!, Visibility = null!
    };
    TacticalMapSchema.NormalizeMap(map);
    True(map.Rooms is not null && map.Walls is not null && map.Doors is not null, "structural collections");
    True(map.Terrain is not null && map.Props is not null && map.Lights is not null, "content collections");
    True(map.SpawnPoints is not null && map.Zones is not null, "encounter collections");
    True(map.Visibility is not null, "visibility record");
    True(map.Visibility!.RevealedRoomIds is not null && map.Visibility.RevealedCells is not null, "visibility collections");
});

Run("an already-current map normalizes to a no-op", () =>
{
    var map = new TacticalMap
    {
        SchemaVersion = TacticalMapSchema.CurrentMapSchemaVersion,
        Seed = 7, GenerationSeed = 7, SourceKind = CampaignProvenance.SourceCanon
    };
    True(!TacticalMapSchema.NormalizeMap(map), "no repair is reported for a current record");
});

Run("a map from a newer schema is refused rather than reinterpreted", () =>
{
    var map = new TacticalMap { Name = "Future Vault", SchemaVersion = TacticalMapSchema.CurrentMapSchemaVersion + 1 };
    var threw = false;
    try { TacticalMapSchema.NormalizeMap(map); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "unknown newer map geometry must not be silently accepted");
});

Run("encounter binding lookups are rebuilt case-insensitively without losing colliding keys", () =>
{
    var caseSensitive = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Enc-1"] = "map-a",
        ["enc-1"] = "map-b",
        ["enc-2"] = "map-c"
    };
    var rebuilt = TacticalMapSchema.NormalizeBindings(caseSensitive);
    True(rebuilt.ContainsKey("ENC-2"), "rebuilt lookup is case-insensitive");
    Equal("map-c", rebuilt["enc-2"], "non-colliding binding survives");
    True(rebuilt.Count == 2, $"colliding keys collapse without throwing (got {rebuilt.Count})");

    var empty = TacticalMapSchema.NormalizeBindings(null);
    True(empty.Count == 0, "null bindings become an empty case-insensitive map");
    empty["Abc"] = "x";
    True(empty.ContainsKey("abc"), "rebuilt empty lookup is case-insensitive");
});

// ---------------------------------------------------------------------------
// Persistence: migration, round trip, and provenance rewrite on load
// ---------------------------------------------------------------------------

Run("the state schema version accounts for persisted tactical map state", () =>
{
    // v3 is the migration slot that gave tactical map state a versioned home. Later rounds add
    // their own slots, so this asserts the floor rather than pinning a literal that every future
    // migration would have to come back and edit.
    True(AppDataStore.CurrentSchemaVersion >= 3, "map state must have a versioned migration slot");
});

Run("a v2 state file carrying legacy map data migrates to the current version on load", () =>
{
    using var dir = new TempDirectory();
    File.WriteAllText(Path.Combine(dir.Path, "state.json"), """
    {
      "schemaVersion": 2,
      "campaigns": [
        {
          "id": "campaign-1",
          "name": "Greenhaven",
          "tacticalMaps": [
            {
              "id": "map-1",
              "key": "crypt",
              "name": "Flooded Crypt",
              "seed": 4242,
              "sourceKind": "ai_generated",
              "rooms": [ { "id": "room-1", "x": 2, "y": 3, "widthSquares": 6, "heightSquares": 4 } ]
            }
          ],
          "encounterMapBindings": { "Encounter-7": "map-1" }
        }
      ]
    }
    """);

    var state = new AppDataStore(dir.Path).LoadAsync().GetAwaiter().GetResult();
    Equal(AppDataStore.CurrentSchemaVersion, state.SchemaVersion, "migrated state schema version");

    var campaign = state.Campaigns.Single();
    var map = campaign.TacticalMaps.Single();
    Equal(TacticalMapSchema.CurrentMapSchemaVersion, map.SchemaVersion, "migrated map schema version");
    Equal(CampaignProvenance.AiExpanded, map.SourceKind, "legacy map provenance is rewritten");
    Equal(4242, map.GenerationSeed, "geometry seed recovered from the art seed");
    Equal(1, map.Rooms.Count, "room geometry survives migration");
    Equal(2, map.Rooms[0].X, "room X survives migration");

    True(campaign.EncounterMapBindings.ContainsKey("encounter-7"),
        "binding lookup is case-insensitive after deserialization");
});

Run("a pre-schema v1 state file migrates all the way to the current version", () =>
{
    using var dir = new TempDirectory();
    File.WriteAllText(Path.Combine(dir.Path, "state.json"),
        """{ "campaigns": [ { "id": "c", "name": "Legacy" } ] }""");

    var state = new AppDataStore(dir.Path).LoadAsync().GetAwaiter().GetResult();
    Equal(AppDataStore.CurrentSchemaVersion, state.SchemaVersion, "state schema version after full migration chain");
    True(state.Campaigns.Single().TacticalMaps.Count == 0, "map collection is present and empty");
    var bindings = state.Campaigns.Single().EncounterMapBindings;
    bindings["Enc"] = "m";
    True(bindings.ContainsKey("enc"), "binding lookup is case-insensitive on a fresh migration");
});

Run("tactical map state survives a save and load round trip", () =>
{
    using var dir = new TempDirectory();
    var store = new AppDataStore(dir.Path);

    var map = new TacticalMap
    {
        Id = "map-round-trip",
        Key = "vault",
        Name = "Sunken Vault",
        Seed = 11,
        GenerationSeed = 4242,
        SourceKind = CampaignProvenance.AiExpanded
    };
    map.Rooms.Add(new TacticalMapRoom { Id = "room-1", X = 4, Y = 5, WidthSquares = 3, HeightSquares = 2 });
    map.Doors.Add(new TacticalMapDoor { Id = "door-1", X = 7, Y = 5, Orientation = "vertical", Secret = true });

    var campaign = new CampaignState { Id = "campaign-1", Name = "Round Trip" };
    campaign.TacticalMaps.Add(map);
    campaign.EncounterMapBindings["Encounter-9"] = "map-round-trip";

    store.SaveAsync(new AppState { Campaigns = [campaign] }).GetAwaiter().GetResult();
    var reloaded = store.LoadAsync().GetAwaiter().GetResult();

    var reloadedCampaign = reloaded.Campaigns.Single();
    Equal(1, reloadedCampaign.TacticalMaps.Count, "map count after round trip");
    var reloadedMap = reloadedCampaign.TacticalMaps.Single();
    Equal("Sunken Vault", reloadedMap.Name, "map name");
    Equal(4242, reloadedMap.GenerationSeed, "geometry seed");
    Equal(11, reloadedMap.Seed, "art seed");
    Equal(CampaignProvenance.AiExpanded, reloadedMap.SourceKind, "provenance");
    Equal(4, reloadedMap.Rooms.Single().X, "room geometry");
    True(reloadedMap.Doors.Single().Secret, "secret door flag");
    True(reloadedCampaign.EncounterMapBindings.ContainsKey("ENCOUNTER-9"),
        "binding lookup stays case-insensitive across a full round trip");
});

Run("saving normalizes legacy provenance so it is written once, not re-read forever", () =>
{
    using var dir = new TempDirectory();
    var store = new AppDataStore(dir.Path);
    var campaign = new CampaignState { Id = "c", Name = "Rewrite" };
    campaign.TacticalMaps.Add(new TacticalMap { Id = "m", Seed = 5, SourceKind = CampaignProvenance.LegacyAiGenerated });

    store.SaveAsync(new AppState { Campaigns = [campaign] }).GetAwaiter().GetResult();

    var raw = File.ReadAllText(Path.Combine(dir.Path, "state.json"));
    True(!raw.Contains("ai_generated", StringComparison.Ordinal),
        "the legacy alias must not be persisted after normalization");
    True(raw.Contains("ai_expanded", StringComparison.Ordinal), "canonical provenance is persisted");

    using var document = JsonDocument.Parse(raw);
    Equal(AppDataStore.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32(), "persisted schema version");
});

Run("a map from an unsupported future schema fails the load loudly", () =>
{
    using var dir = new TempDirectory();
    File.WriteAllText(Path.Combine(dir.Path, "state.json"), $$"""
    {
      "schemaVersion": 3,
      "campaigns": [
        {
          "id": "c",
          "name": "Future",
          "tacticalMaps": [ { "id": "m", "name": "Future Map", "schemaVersion": {{TacticalMapSchema.CurrentMapSchemaVersion + 1}} } ]
        }
      ]
    }
    """);

    var threw = false;
    try { new AppDataStore(dir.Path).LoadAsync().GetAwaiter().GetResult(); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "loading unknown newer map geometry must not silently succeed");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Map schema and provenance tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}

Console.WriteLine($"Map schema and provenance tests passed: {passed}");

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
    }
}

static void True(bool value, string label)
{
    if (!value) throw new Exception(label);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dmai-map-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
