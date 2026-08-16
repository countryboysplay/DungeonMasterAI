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

Run("player combat attack creates a required pending d20 roll", () =>
{
    var (engine, campaign, encounter, attacker, target, _, _) = CreatePlayerAttackTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);

    var pending = engine.RequestEncounterAttackRoll(campaign, encounter.Id, attacker.Id, target.Id);
    Equal("combat_attack", pending.ResolutionKey, "resolution key");
    Equal("1d20", pending.Formula, "formula");
    Equal(13, pending.TargetNumber, "target AC");
    Equal(5, pending.Modifier, "attack modifier");
    Equal(target.Id, pending.Context["target_combatant_id"], "target combatant context");
    True(pending.Required, "attack roll should be required");
});

Run("generic required player roll blocks combat turn advancement", () =>
{
    var (engine, campaign, encounter, attacker, target, _, _) = CreatePlayerAttackTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);
    engine.RequestEncounterAttackRoll(campaign, encounter.Id, attacker.Id, target.Id);

    var threw = false;
    try { engine.NextTurn(campaign, encounter.Id); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "NextTurn should reject an unresolved required attack roll");
});

Run("supplied player d20 is authoritative for combat attack", () =>
{
    var (engine, campaign, encounter, attacker, target, _, targetCharacter) = CreatePlayerAttackTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);
    var pending = engine.RequestEncounterAttackRoll(campaign, encounter.Id, attacker.Id, target.Id);
    var deterministicDamage = new DiceService((minimumInclusive, maximumExclusive) => minimumInclusive);

    var result = engine.ResolvePendingEncounterAttackRoll(campaign, pending.Id, 15, null, deterministicDamage);
    Equal(15, result.Attack.D20, "authoritative attack d20");
    Equal(20, result.Attack.Total, "attack total");
    True(result.Attack.Hit, "attack should hit AC 13");
    Equal(4, result.Attack.Damage, "1d8+3 deterministic damage");
    Equal(16, targetCharacter.CurrentHp, "target hp after exact damage");
    True(campaign.PendingPlayerRoll is null, "pending attack should clear after resolution");
});

Run("supplied player miss does not damage target", () =>
{
    var (engine, campaign, encounter, attacker, target, _, targetCharacter) = CreatePlayerAttackTurn();
    engine.FinalizeInitiative(campaign, encounter.Id);
    var pending = engine.RequestEncounterAttackRoll(campaign, encounter.Id, attacker.Id, target.Id);
    var deterministicDice = new DiceService((minimumInclusive, maximumExclusive) => minimumInclusive);

    var result = engine.ResolvePendingEncounterAttackRoll(campaign, pending.Id, 2, null, deterministicDice);
    Equal(2, result.Attack.D20, "authoritative miss d20");
    True(!result.Attack.Hit, "attack should miss");
    Equal(20, targetCharacter.CurrentHp, "miss should not change hp");
    True(campaign.PendingPlayerRoll is null, "pending attack should clear after miss");
});

Run("advantage attack request requires and uses two player d20 results", () =>
{
    var (engine, campaign, encounter, attacker, target, _, targetCharacter) = CreatePlayerAttackTurn();
    targetCharacter.Conditions.Add("Prone");
    engine.FinalizeInitiative(campaign, encounter.Id);
    var pending = engine.RequestEncounterAttackRoll(campaign, encounter.Id, attacker.Id, target.Id);
    Equal("advantage", pending.RollMode, "pending roll mode");
    var deterministicDamage = new DiceService((minimumInclusive, maximumExclusive) => minimumInclusive);

    var result = engine.ResolvePendingEncounterAttackRoll(campaign, pending.Id, 4, 16, deterministicDamage);
    Equal(16, result.Attack.D20, "advantage chosen d20");
    True(result.Attack.Hit, "advantage roll should hit");
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


static (GameEngine Engine, CampaignState Campaign, EncounterState Encounter, CombatantState Attacker, CombatantState Target, CharacterSheet AttackerCharacter, CharacterSheet TargetCharacter) CreatePlayerAttackTurn()
{
    var engine = new GameEngine();
    var pc = new CharacterSheet
    {
        Id = "pc-attacker",
        Name = "Aric",
        CharacterType = "pc",
        MaxHp = 20,
        CurrentHp = 20,
        ArmorClass = 15,
        Attacks = [new AttackProfile { Name = "Longsword", AttackBonus = 5, DamageExpression = "1d8+3", DamageType = "Slashing", ReachFeet = 5 }]
    };
    var enemy = new CharacterSheet
    {
        Id = "npc-target",
        Name = "Ashen Watcher",
        CharacterType = "npc",
        MaxHp = 20,
        CurrentHp = 20,
        ArmorClass = 13
    };
    var attacker = new CombatantState
    {
        Id = "combatant-attacker",
        CharacterId = pc.Id,
        Initiative = 20,
        Positioned = true,
        GridX = 0,
        GridY = 0,
        Side = "party"
    };
    var target = new CombatantState
    {
        Id = "combatant-target",
        CharacterId = enemy.Id,
        Initiative = 10,
        Positioned = true,
        GridX = 1,
        GridY = 0,
        Side = "opposition"
    };
    var encounter = new EncounterState
    {
        Id = "encounter-attack-test",
        Name = "Attack Roll Test",
        Status = "active",
        Round = 1,
        TurnIndex = 0,
        Combatants = [attacker, target]
    };
    var campaign = new CampaignState
    {
        Id = "campaign-attack-test",
        Name = "Attack Roll Test Campaign",
        Characters = [pc, enemy],
        Encounters = [encounter]
    };
    return (engine, campaign, encounter, attacker, target, pc, enemy);
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
