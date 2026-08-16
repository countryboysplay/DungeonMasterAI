using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("initiative pauses for each player and uses the supplied d20 results", () =>
{
    var (engine, campaign, encounter, aricCombatant, miraCombatant, watcherCombatant) = CreateEncounter();
    var deterministicDice = new DiceService((minimumInclusive, maximumExclusive) => 10);

    var started = engine.BeginInitiativeSequence(campaign, encounter.Id, deterministicDice);
    True(!started.Completed, "initiative should pause for the first PC");
    var firstPending = campaign.PendingPlayerRoll ?? throw new Exception("first player Initiative request was not created");
    Equal("initiative", firstPending.ResolutionKey, "first resolution key");
    Equal("pc-aric", firstPending.ActorCharacterId, "first Initiative actor");
    Equal(2, firstPending.Modifier, "Aric Initiative modifier");
    True(!watcherCombatant.Initiative.HasValue, "NPC should wait until preceding player rolls resolve");

    var firstResolved = engine.ResolvePendingInitiativeRoll(campaign, firstPending.Id, 12, null, deterministicDice);
    True(!firstResolved.Completed, "initiative should pause for the second PC");
    Equal(14, aricCombatant.Initiative, "Aric authoritative Initiative total");
    var secondPending = campaign.PendingPlayerRoll ?? throw new Exception("second player Initiative request was not created");
    Equal("pc-mira", secondPending.ActorCharacterId, "second Initiative actor");
    Equal("advantage", secondPending.RollMode, "Invisible PC Initiative mode");

    var missingSecondRollRejected = false;
    try { engine.ResolvePendingInitiativeRoll(campaign, secondPending.Id, 5, null, deterministicDice); }
    catch (InvalidOperationException) { missingSecondRollRejected = true; }
    True(missingSecondRollRejected, "Advantage Initiative should require two supplied d20 results");
    Equal(secondPending.Id, campaign.PendingPlayerRoll?.Id, "failed resolution should preserve pending Initiative");

    var completed = engine.ResolvePendingInitiativeRoll(campaign, secondPending.Id, 5, 17, deterministicDice);
    True(completed.Completed, "initiative sequence should complete after the final PC roll");
    True(campaign.PendingPlayerRoll is null, "initiative pending roll should clear");
    Equal(20, miraCombatant.Initiative, "Mira authoritative Initiative total");
    Equal(11, watcherCombatant.Initiative, "NPC deterministic Initiative total");
    Equal(3, completed.Order.Count, "initiative order count");
    Equal("pc-mira", completed.Order[0].CharacterId, "highest Initiative character");
    Equal("pc-aric", completed.Order[1].CharacterId, "second Initiative character");
    Equal("npc-watcher", completed.Order[2].CharacterId, "third Initiative character");
});

Run("surprise and invisibility cancel to a normal player Initiative roll", () =>
{
    var (engine, campaign, encounter, _, miraCombatant, _) = CreateEncounter();
    miraCombatant.Surprised = true;
    var deterministicDice = new DiceService((minimumInclusive, maximumExclusive) => 10);

    var first = engine.BeginInitiativeSequence(campaign, encounter.Id, deterministicDice);
    var aricPending = first.PendingRoll ?? throw new Exception("Aric pending Initiative missing");
    engine.ResolvePendingInitiativeRoll(campaign, aricPending.Id, 10, null, deterministicDice);
    var miraPending = campaign.PendingPlayerRoll ?? throw new Exception("Mira pending Initiative missing");
    Equal("normal", miraPending.RollMode, "Advantage and Disadvantage should cancel");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Initiative roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}

Console.WriteLine($"Initiative roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, EncounterState Encounter, CombatantState Aric, CombatantState Mira, CombatantState Watcher) CreateEncounter()
{
    var engine = new GameEngine();
    var aric = new CharacterSheet
    {
        Id = "pc-aric",
        Name = "Aric",
        CharacterType = "pc",
        MaxHp = 14,
        CurrentHp = 14,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 14 }
    };
    var mira = new CharacterSheet
    {
        Id = "pc-mira",
        Name = "Mira",
        CharacterType = "pc",
        MaxHp = 12,
        CurrentHp = 12,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 16 },
        Conditions = ["Invisible"]
    };
    var watcher = new CharacterSheet
    {
        Id = "npc-watcher",
        Name = "Ashen Watcher",
        CharacterType = "npc",
        MaxHp = 20,
        CurrentHp = 20,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 12 }
    };
    var campaign = new CampaignState
    {
        Id = "campaign-initiative",
        Name = "Initiative Campaign",
        Characters = [aric, mira, watcher]
    };
    var encounter = engine.StartEncounter(campaign, "Initiative Test");
    var aricCombatant = engine.AddCombatant(campaign, encounter.Id, aric.Id);
    var miraCombatant = engine.AddCombatant(campaign, encounter.Id, mira.Id);
    var watcherCombatant = engine.AddCombatant(campaign, encounter.Id, watcher.Id);
    return (engine, campaign, encounter, aricCombatant, miraCombatant, watcherCombatant);
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
