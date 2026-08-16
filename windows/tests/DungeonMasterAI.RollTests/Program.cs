using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("start-of-turn death save creates a required pending roll", () =>
{
    var (engine, campaign, encounter, combatant, _) = CreateDeathSaveTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);

    var pending = campaign.PendingPlayerRoll ?? throw new Exception("PendingPlayerRoll was null.");
    Equal("combat_death_save", pending.ResolutionKey, "resolution key");
    Equal("1d20", pending.Formula, "formula");
    Equal(combatant.Id, pending.CombatantId, "combatant id");
    Equal(10, pending.TargetNumber, "target number");
    True(pending.Required, "roll should be required");
});

Run("combat cannot advance while required roll is unresolved", () =>
{
    var (engine, campaign, encounter, _, _) = CreateDeathSaveTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);

    var threw = false;
    try { engine.NextTurn(campaign, encounter.Id); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "NextTurn should reject an unresolved required death save");
});

Run("the exact d20 result supplied by the UI resolves the pending death save", () =>
{
    var (engine, campaign, encounter, combatant, pc) = CreateDeathSaveTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);

    var result = engine.ResolveCombatDeathSavingThrow(campaign, encounter.Id, combatant.Id, 15, new DiceService());
    Equal(15, result.Roll, "resolved d20 roll");
    Equal(1, pc.DeathSaveSuccesses, "death save successes");
    True(combatant.DeathSaveResolvedThisTurn, "turn death save should be marked resolved");
    True(campaign.PendingPlayerRoll is null, "pending roll should be cleared after resolution");
});

Run("natural 20 from the supplied d20 result restores one hit point", () =>
{
    var (engine, campaign, encounter, combatant, pc) = CreateDeathSaveTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);

    var result = engine.ResolveCombatDeathSavingThrow(campaign, encounter.Id, combatant.Id, 20, new DiceService());
    Equal(20, result.Roll, "resolved d20 roll");
    Equal(1, pc.CurrentHp, "current hp");
    True(!pc.Conditions.Contains("Unconscious", StringComparer.OrdinalIgnoreCase), "Unconscious should be removed");
    True(campaign.PendingPlayerRoll is null, "pending roll should be cleared");
});

Run("pending roll can be reconstructed from authoritative combat state", () =>
{
    var (engine, campaign, encounter, combatant, _) = CreateDeathSaveTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);
    campaign.PendingPlayerRoll = null;

    var rebuilt = engine.EnsurePendingPlayerRollForActiveCombat(campaign);
    True(rebuilt is not null, "pending roll should be rebuilt");
    Equal(combatant.Id, rebuilt!.CombatantId, "rebuilt combatant id");
});

Console.WriteLine();
Console.WriteLine($"Roll-state tests: {passed} passed, {failures.Count} failed.");
foreach (var failure in failures) Console.WriteLine($"FAIL: {failure}");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

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
        Console.WriteLine($"FAIL: {name}: {ex.Message}");
    }
}

static (GameEngine Engine, CampaignState Campaign, EncounterState Encounter, CombatantState Combatant, CharacterSheet Pc) CreateDeathSaveTurn()
{
    var engine = new GameEngine();
    var pc = new CharacterSheet
    {
        Id = "pc-aric",
        Name = "Aric",
        CharacterType = "pc",
        MaxHp = 14,
        CurrentHp = 0,
        Conditions = ["Unconscious"]
    };
    var combatant = new CombatantState
    {
        Id = "combatant-aric",
        CharacterId = pc.Id,
        Initiative = 20,
        Positioned = true
    };
    var encounter = new EncounterState
    {
        Id = "encounter-test",
        Name = "Roll Test",
        Status = "active",
        Round = 1,
        TurnIndex = 0,
        Combatants = [combatant]
    };
    var campaign = new CampaignState
    {
        Id = "campaign-test",
        Name = "Roll Test Campaign",
        Characters = [pc],
        Encounters = [encounter]
    };
    return (engine, campaign, encounter, combatant, pc);
}

static void True(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected '{expected}', got '{actual}'.");
}
