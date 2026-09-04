using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DungeonMasterAI.AI;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.R62MapCombatTests;

/// <summary>
/// r62 regression suite. Every check here fails on the parent commit, because these are the two
/// defects this revision exists to close:
/// <list type="number">
/// <item><description>
/// The AI map generator, the engine, and the campaign readiness validator each had a private and
/// mutually incompatible spawn-side vocabulary, so a generated map could never pass readiness.
/// </description></item>
/// <item><description>
/// Nothing in the engine consulted <see cref="TacticalMapGeometry"/>, so combat placed and moved
/// combatants as if the authored map did not exist.
/// </description></item>
/// </list>
/// A build succeeding proves nothing about either, which is why none of these checks are
/// construction checks.
///
/// r63 note: this suite originally also rendered <c>CombatGridControl</c> offscreen and asserted
/// that attaching a map changed the pixels it drew. That check went with the WPF front end; the
/// engine assertions below are unchanged and still fail on r62's parent commit.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var failures = new List<string>();
        try
        {
            GeneratedMapPassesCampaignReadiness(failures).GetAwaiter().GetResult();
            PersistedLegacySpawnSidesStillLoad(failures);
            UnrecognizedSpawnSidesAreStillRejected(failures);
            EngineReadsGeometryFromTheBoundMap(failures);
            UnboundEncountersKeepTheirOriginalBehaviour(failures);
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled r62 test exception: {ex}");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("R62 MAP COMBAT PASS");
            Console.WriteLine("Spawn-side unification, legacy map migration, and engine geometry consumption verified.");
            return 0;
        }

        Console.Error.WriteLine($"R62 MAP COMBAT FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
        return 1;
    }

    // -----------------------------------------------------------------------
    // Defect 1: the three spawn vocabularies
    // -----------------------------------------------------------------------

    /// <summary>
    /// The end-to-end shape of defect 1: run the real generator against a stubbed local model that
    /// emits the vocabulary the r55 prompt actually asked for ("player" / "enemy"), put the
    /// accepted map into a campaign, and run the real readiness validator over it. On the parent
    /// commit the generator required "player" and the validator rejected it, so this campaign was
    /// unshippable the moment a map was generated.
    /// </summary>
    private static async Task GeneratedMapPassesCampaignReadiness(ICollection<string> failures)
    {
        var handler = new QueuedCompletionHandler([LegacyVocabularyMapJson]);
        using var http = new HttpClient(handler);
        var generator = new TacticalMapAiGeneratorService(http);
        var request = new TacticalMapGenerationRequest
        {
            Description = "A small crypt antechamber with a party entrance and one warden.",
            MapType = "dungeon",
            Theme = "abandoned_crypt",
            AssetSetId = "core.fantasy.crypt",
            WidthSquares = 20,
            HeightSquares = 14,
            FeetPerSquare = 5,
            Seed = 620001,
            FogOfWarEnabled = false,
            AllowedAssetKeys = TacticalMapAiGeneratorService.DefaultAssetKeys.ToList()
        };
        var settings = new AppSettings { LlamaServerUrl = "http://127.0.0.1:8080", ModelName = "test-local-model", MaxTokens = 700 };

        var result = await generator.GenerateAsync(request, settings);

        Check(result.Attempts == 1,
            "A map whose only irregularity is legacy spawn wording is accepted without a repair round trip.", failures);
        Check(result.Map.SpawnPoints.All(spawn => CombatSide.All.Contains(spawn.Side, StringComparer.Ordinal)),
            "Every accepted spawn side is stored in the canonical vocabulary.", failures);

        var campaign = BuildPlayableCampaign();
        campaign.TacticalMaps.Add(result.Map);

        var issues = new CampaignReadinessValidator().Validate(campaign);
        var spawnErrors = issues
            .Where(i => i.Severity == ReadinessSeverity.Error && i.Category.StartsWith("map", StringComparison.Ordinal))
            .ToArray();
        Check(spawnErrors.Length == 0,
            "A generated map passes campaign readiness with no map errors. Got: "
                + string.Join(" | ", spawnErrors.Select(i => $"{i.Category}/{i.Message}")), failures);
        Check(!issues.Any(i => i.Category == "map_spawn" && i.Message.Contains("none is marked for the party", StringComparison.Ordinal)),
            "Readiness recognises the generated party entrance rather than warning that none exists.", failures);
    }

    /// <summary>
    /// Maps written by earlier rounds are on disk with the old wording. They must still load, and
    /// they must come back canonical so the engine can read them, without any editor interaction.
    /// </summary>
    private static void PersistedLegacySpawnSidesStillLoad(ICollection<string> failures)
    {
        var persisted = """
        {
          "schemaVersion": 1,
          "id": "legacy-map",
          "key": "legacy.crypt",
          "name": "Legacy Crypt",
          "widthSquares": 12,
          "heightSquares": 10,
          "feetPerSquare": 5,
          "seed": 4242,
          "spawnPoints": [
            { "id": "s1", "name": "Party Entrance", "side": "player", "x": 1, "y": 1 },
            { "id": "s2", "name": "Warden", "side": "enemy", "x": 9, "y": 8 },
            { "id": "s3", "name": "Shrine Keeper", "side": "ally", "x": 2, "y": 6 },
            { "id": "s4", "name": "Beggar", "side": "neutral", "x": 4, "y": 4 }
          ]
        }
        """;

        var map = JsonSerializer.Deserialize<TacticalMap>(persisted, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Legacy map fixture failed to deserialize.");
        Check(map.SpawnPoints.Count == 4, "Legacy persisted map still deserializes with all its spawn points.", failures);

        var repaired = TacticalMapSchema.NormalizeMap(map);
        Check(repaired, "Loading a v1 map with legacy spawn wording reports a repair.", failures);
        Check(map.SchemaVersion == TacticalMapSchema.CurrentMapSchemaVersion,
            $"Migrated map reports the current schema version (got {map.SchemaVersion}).", failures);
        Check(map.SpawnPoints[0].Side == CombatSide.Party, "Legacy 'player' migrates to 'party'.", failures);
        Check(map.SpawnPoints[1].Side == CombatSide.Opposition, "Legacy 'enemy' migrates to 'opposition'.", failures);
        Check(map.SpawnPoints[2].Side == CombatSide.Party, "Legacy 'ally' migrates to 'party'.", failures);
        Check(map.SpawnPoints[3].Side == CombatSide.Neutral, "'neutral' is already canonical and is left alone.", failures);
        Check(map.Seed == 4242 && map.GenerationSeed == 4242,
            "Spawn-side migration does not disturb the r57 seed provenance migration.", failures);
        Check(!TacticalMapSchema.NormalizeMap(map), "Re-normalizing a migrated map is a no-op.", failures);

        Check(TacticalMapGeometry.Validate(map).Issues.All(i => !i.Path.EndsWith(".side", StringComparison.Ordinal)),
            "Geometry validation accepts a migrated map's spawn sides.", failures);
    }

    /// <summary>
    /// Widening the accepted vocabulary must not turn the check into a rubber stamp: a side that
    /// resolves onto nothing is still an error everywhere it is read.
    /// </summary>
    private static void UnrecognizedSpawnSidesAreStillRejected(ICollection<string> failures)
    {
        Check(CombatSide.TryNormalize("wombat") is null, "An unrecognized side resolves to null.", failures);
        Check(CombatSide.TryNormalize("  Player ") == CombatSide.Party, "Normalization trims and ignores case.", failures);
        Check(CombatSide.TryNormalize("") is null && CombatSide.TryNormalize(null) is null,
            "A blank side resolves to null rather than to a default that fights.", failures);

        var campaign = BuildPlayableCampaign();
        var map = BuildCryptMap();
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Name = "Nonsense", Side = "wombat", X = 3, Y = 3 });
        campaign.TacticalMaps.Add(map);

        var issues = new CampaignReadinessValidator().Validate(campaign);
        Check(issues.Any(i => i.Severity == ReadinessSeverity.Error && i.Category == "map_spawn"
                              && i.Message.Contains("wombat", StringComparison.Ordinal)),
            "Readiness still errors on a spawn side that means nothing.", failures);

        // Migration must not invent a meaning for it either.
        TacticalMapSchema.NormalizeMap(map);
        Check(map.SpawnPoints.Last().Side == "wombat",
            "Migration leaves an unrecognized side verbatim for readiness to report.", failures);
    }

    // -----------------------------------------------------------------------
    // Defect 2: the engine never consulted the map
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves the engine reads geometry from a supplied map: placement lands on authored spawn
    /// points, walls and closed doors stop movement, blocking props are unwalkable, and map
    /// difficult terrain costs the extra five feet. All of this is deterministic and none of it
    /// touches a model.
    /// </summary>
    private static void EngineReadsGeometryFromTheBoundMap(ICollection<string> failures)
    {
        var engine = new GameEngine();
        var hero = Character("hero", "Aeliana", "pc");
        // 60 feet of Speed so a single turn can afford both the control move and the
        // difficult-terrain move below without the movement budget masking the cost difference.
        hero.Speed = 60;
        var warden = Character("warden", "Crypt Warden", "monster");
        var campaign = BuildPlayableCampaign();
        campaign.Characters.Add(hero);
        campaign.Characters.Add(warden);

        var map = BuildCryptMap();
        campaign.TacticalMaps.Add(map);

        var encounter = engine.StartEncounter(campaign, "Warden Fight");
        campaign.EncounterMapBindings[encounter.Id] = map.Id;

        Check(ReferenceEquals(GameEngine.ResolveEncounterMap(campaign, encounter.Id), map),
            "The engine resolves the map bound to the encounter.", failures);

        engine.ActivateEncounter(campaign, encounter.Id, includeParty: true);
        var heroCombatant = encounter.Combatants.Single(c => c.CharacterId == hero.Id);
        Check(heroCombatant.GridX == 2 && heroCombatant.GridY == 2,
            $"The party is placed on the authored party spawn point, not row 0 (got {heroCombatant.GridX},{heroCombatant.GridY}).", failures);
        Check(heroCombatant.Side == CombatSide.Party, "Party placement uses the canonical side.", failures);

        var wardenCombatant = engine.AddCombatant(campaign, encounter.Id, warden.Id, side: "enemy");
        Check(wardenCombatant.Side == CombatSide.Opposition,
            "A legacy 'enemy' side supplied by a tool call normalizes onto 'opposition'.", failures);
        Check(wardenCombatant.GridX == 9 && wardenCombatant.GridY == 3,
            $"An opposing combatant is placed on the authored opposition spawn point (got {wardenCombatant.GridX},{wardenCombatant.GridY}).", failures);

        // A second party member must not stack on the first spawn point.
        var squire = Character("squire", "Squire", "pc");
        campaign.Characters.Add(squire);
        var squireCombatant = engine.AddCombatant(campaign, encounter.Id, squire.Id, side: CombatSide.Party);
        Check(!(squireCombatant.GridX == heroCombatant.GridX && squireCombatant.GridY == heroCombatant.GridY),
            "A second party member does not share an occupied spawn square.", failures);
        Check(TacticalMapGeometry.IsCellWalkable(map, squireCombatant.GridX, squireCombatant.GridY),
            "The overflow placement still lands on a walkable map square.", failures);

        // Placement onto geometry the map forbids is refused.
        CheckThrows(() => engine.SetCombatantPosition(campaign, encounter.Id, squireCombatant.Id, 5, 5),
            "Placing a combatant on a blocking prop is refused.", failures);
        CheckThrows(() => engine.SetCombatantPosition(campaign, encounter.Id, squireCombatant.Id, 40, 40),
            "Placing a combatant outside the map bounds is refused.", failures);

        engine.SetCombatantPosition(campaign, encounter.Id, squireCombatant.Id, 2, 4);
        engine.SetInitiative(campaign, encounter.Id, heroCombatant.Id, 20);
        engine.SetInitiative(campaign, encounter.Id, squireCombatant.Id, 15);
        engine.SetInitiative(campaign, encounter.Id, wardenCombatant.Id, 10);
        engine.FinalizeInitiative(campaign, encounter.Id);

        // The hero is in the west chamber; the warden is east of the dividing wall whose only gap
        // is a closed door. Crossing the wall must be refused by map geometry, not permitted by an
        // empty grid.
        var blocked = CheckThrows(() => engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 7, 2),
            "Movement through a wall on the bound map is refused.", failures);
        Check(blocked?.Contains("blocked", StringComparison.OrdinalIgnoreCase) == true,
            $"The refusal names the map obstruction (got: {blocked}).", failures);

        CheckThrows(() => engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 5, 5),
            "Movement onto a blocking prop is refused.", failures);

        // Opening the door makes the same move legal: the engine is reading live door state, not a
        // snapshot taken when the map was authored.
        var door = map.Doors.Single();
        door.State = "open";
        var flatBefore = heroCombatant.MovementRemainingFeet;
        var through = engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 6, 2);
        var flatCost = flatBefore - heroCombatant.MovementRemainingFeet;
        Check(through.Committed && heroCombatant.GridX == 6 && heroCombatant.GridY == 2,
            "Opening the door makes the previously-blocked move legal.", failures);
        Check(flatCost == 20,
            $"Four ordinary squares cost 20 feet (got {flatCost}). This is the control for the difficult-terrain check below.", failures);

        // The flooded column at x=8 is authored difficult terrain on the map only; nothing in
        // EncounterState knows about it. Crossing it must still cost double. The wall-crossing move
        // above deliberately stops short of it, so this is the only check that can be paying for it.
        Check(TacticalMapGeometry.IsDifficultTerrain(map, 8, 2) && !TacticalMapGeometry.IsDifficultTerrain(map, 7, 2),
            "The fixture's flood is difficult terrain at x=8 only.", failures);
        var floodBefore = heroCombatant.MovementRemainingFeet;
        engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 8, 2);
        var floodCost = floodBefore - heroCombatant.MovementRemainingFeet;
        Check(floodCost == 15,
            $"Two squares, the second of them map difficult terrain, cost 15 feet rather than 10 (got {floodCost}).", failures);
    }

    /// <summary>
    /// The map is additive. An encounter with no map bound must place and move exactly as it did
    /// before r62, or this change breaks every campaign that has no maps at all.
    /// </summary>
    private static void UnboundEncountersKeepTheirOriginalBehaviour(ICollection<string> failures)
    {
        var engine = new GameEngine();
        var hero = Character("hero", "Aeliana", "pc");
        var campaign = BuildPlayableCampaign();
        campaign.Characters.Add(hero);

        var encounter = engine.StartEncounter(campaign, "Unbound Fight");
        Check(GameEngine.ResolveEncounterMap(campaign, encounter.Id) is null,
            "An encounter with no binding resolves to no map.", failures);

        engine.ActivateEncounter(campaign, encounter.Id, includeParty: true);
        var combatant = encounter.Combatants.Single(c => c.CharacterId == hero.Id);
        Check(combatant.GridY == 0, "Unbound placement still uses the original row-0 fallback.", failures);

        engine.SetInitiative(campaign, encounter.Id, combatant.Id, 20);
        engine.FinalizeInitiative(campaign, encounter.Id);
        var move = engine.MoveCombatant(campaign, encounter.Id, combatant.Id, combatant.GridX + 2, 2);
        Check(move.Committed, "Unbound movement is unrestricted by map geometry.", failures);

        // A binding that points at a map the campaign no longer holds must not resurrect the
        // restriction or throw; readiness reports the dangling binding separately.
        campaign.EncounterMapBindings[encounter.Id] = "map-that-was-deleted";
        Check(GameEngine.ResolveEncounterMap(campaign, encounter.Id) is null,
            "A dangling map binding resolves to no map rather than throwing.", failures);
    }

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    /// <summary>
    /// A 12x10 crypt: two chambers divided by a solid wall whose only opening is a closed door at
    /// (6,2), a blocking pillar at (5,5), a flooded difficult-terrain strip in the east chamber,
    /// and one authored spawn point per side.
    /// </summary>
    private static TacticalMap BuildCryptMap()
    {
        var map = new TacticalMap
        {
            Id = "r62-crypt",
            Key = "r62.crypt",
            Name = "R62 Crypt Antechamber",
            SchemaVersion = TacticalMapSchema.CurrentMapSchemaVersion,
            WidthSquares = 12,
            HeightSquares = 10,
            FeetPerSquare = 5,
            Seed = 620001,
            GenerationSeed = 620001,
            SourceKind = CampaignProvenance.TestFixture
        };
        map.Rooms.Add(new TacticalMapRoom { Id = "west", Name = "West Chamber", X = 0, Y = 0, WidthSquares = 6, HeightSquares = 10 });
        map.Rooms.Add(new TacticalMapRoom { Id = "east", Name = "East Chamber", X = 6, Y = 0, WidthSquares = 6, HeightSquares = 10 });
        // The divider sits on the grid line x=6 and runs the full height, so every step from the
        // west chamber into the east chamber crosses it.
        map.Walls.Add(new TacticalMapWall { Id = "divider", FromX = 6, FromY = 0, ToX = 6, ToY = 10 });
        map.Doors.Add(new TacticalMapDoor { Id = "gate", Name = "Iron Gate", X = 6, Y = 2, Orientation = "vertical", State = "closed" });
        map.Props.Add(new TacticalMapProp { Id = "pillar", Name = "Pillar", X = 5, Y = 5, BlocksMovement = true, BlocksLineOfSight = true });
        map.Terrain.Add(new TacticalMapTerrain
        {
            Id = "flood",
            Name = "Shallow Flood",
            TerrainType = "water",
            X = 8,
            Y = 0,
            WidthSquares = 1,
            HeightSquares = 10,
            AssetKey = "terrain.water.crypt_shallow",
            DifficultTerrain = true
        });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Id = "spawn-party", Name = "Party Entrance", Side = CombatSide.Party, X = 2, Y = 2 });
        map.SpawnPoints.Add(new TacticalMapSpawnPoint { Id = "spawn-warden", Name = "Warden", Side = CombatSide.Opposition, X = 9, Y = 3 });
        return map;
    }

    private static CampaignState BuildPlayableCampaign()
    {
        var start = new WorldLocation
        {
            Id = "loc-start",
            Key = "crypt-entrance",
            Name = "Crypt Entrance",
            Type = "area",
            Discovered = true
        };
        return new CampaignState
        {
            Id = "r62-campaign",
            Name = "R62 Campaign",
            Locations = [start],
            PartyLocationId = start.Id
        };
    }

    private static CharacterSheet Character(string id, string name, string type) => new()
    {
        Id = id,
        Key = id,
        Name = name,
        CharacterType = type,
        MaxHp = 40,
        CurrentHp = 40,
        ArmorClass = 14,
        Speed = 30,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["strength"] = 14,
            ["dexterity"] = 12,
            ["constitution"] = 12,
            ["wisdom"] = 10,
            ["intelligence"] = 10,
            ["charisma"] = 10
        }
    };

    private const string LegacyVocabularyMapJson = """
    {
      "name": "Warden Antechamber",
      "key": "warden_antechamber",
      "rooms": [
        { "name": "Antechamber", "kind": "room", "x": 1, "y": 1, "widthSquares": 17, "heightSquares": 11, "floorAssetKey": "floor.stone.crypt_flagstone", "wallAssetKey": "wall.stone.crypt_block" }
      ],
      "walls": [
        { "fromX": 1, "fromY": 1, "toX": 18, "toY": 1, "assetKey": "wall.stone.crypt_block" },
        { "fromX": 18, "fromY": 1, "toX": 18, "toY": 12, "assetKey": "wall.stone.crypt_block" },
        { "fromX": 18, "fromY": 12, "toX": 1, "toY": 12, "assetKey": "wall.stone.crypt_block" },
        { "fromX": 1, "fromY": 12, "toX": 1, "toY": 1, "assetKey": "wall.stone.crypt_block" }
      ],
      "props": [
        { "name": "Round Pillar", "x": 9, "y": 6, "assetKey": "prop.pillar.stone_round", "blocksMovement": true, "blocksLineOfSight": true, "cover": "three_quarters" }
      ],
      "spawnPoints": [
        { "name": "Party Entrance", "side": "player", "x": 3, "y": 6, "dmOnly": true },
        { "name": "Warden Spawn", "side": "enemy", "x": 15, "y": 6, "dmOnly": true }
      ],
      "visibility": { "revealAll": true, "revealedRoomIds": [], "revealedCells": [] }
    }
    """;

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }

    /// <summary>Asserts the action throws, and returns the message so callers can assert on it.</summary>
    private static string? CheckThrows(Action action, string message, ICollection<string> failures)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            failures.Add($"{message} (threw the wrong exception type: {ex.GetType().Name}: {ex.Message})");
            return null;
        }

        failures.Add(message);
        return null;
    }

    private sealed class QueuedCompletionHandler(IEnumerable<string> completions) : HttpMessageHandler
    {
        private readonly Queue<string> _completions = new(completions);
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_completions.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("No queued completion remains.") };

            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = _completions.Dequeue() } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
