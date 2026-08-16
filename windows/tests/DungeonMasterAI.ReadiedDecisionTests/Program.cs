using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("readied trigger asks PC before spending Reaction", () =>
{
    var f = CreateFixture(playerReactor: true);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target advances", "Blade");

    var decision = f.Engine.RequestReadiedAttackDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    Equal("readied_attack_reaction", decision.DecisionType, "decision type");
    True(f.ReactorCombatant.ReactionAvailable, "asking does not spend Reaction");
    True(f.ReactorCombatant.ReadiedAction is not null, "asking does not clear readied action");
    True(f.Campaign.PendingPlayerRoll is null, "asking creates no dice before choice");
});

Run("declining one trigger preserves ready and Reaction", () =>
{
    var f = CreateFixture(playerReactor: true);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target advances", "Blade");
    var decision = f.Engine.RequestReadiedAttackDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);

    var resolution = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline_trigger");
    Equal("decline_trigger", resolution.OptionId, "decline option");
    True(f.Campaign.PendingPlayerDecision is null, "decline clears decision");
    True(f.Campaign.PendingPlayerRoll is null, "decline creates no roll");
    True(f.ReactorCombatant.ReactionAvailable, "decline preserves Reaction");
    True(f.ReactorCombatant.ReadiedAction is not null, "decline preserves readied action");
});

Run("accepting trigger commits Reaction and hands off to player d20", () =>
{
    var f = CreateFixture(playerReactor: true);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target advances", "Blade");
    var decision = f.Engine.RequestReadiedAttackDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);

    var resolution = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction");
    var pending = resolution.FollowUpRoll ?? throw new Exception("accepted readied attack did not create player roll");
    Equal("readied_attack", pending.ResolutionKey, "follow-up roll key");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "follow-up roll becomes authoritative pending roll");
    True(!f.ReactorCombatant.ReactionAvailable, "accepted trigger spends Reaction");
    True(f.ReactorCombatant.ReadiedAction is null, "accepted trigger consumes readied action");
    True(f.Campaign.PendingPlayerDecision is null, "choice clears before dice request");
});

Run("DM trigger tool cannot choose PC Reaction", () =>
{
    var f = CreateFixture(playerReactor: true);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target advances", "Blade");
    var router = new DmToolRouter(f.Engine, MinimumDice(), new RulesSearchService());
    var result = router.Execute(
        f.Campaign,
        "trigger_readied_attack",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.ReactorCombatant.Id}\"}}");

    True(result.Ok, $"router trigger failed: {result.Error}");
    var decision = result.Result as PendingPlayerDecision ?? throw new Exception("DM trigger did not return a player decision");
    Equal("readied_attack_reaction", decision.DecisionType, "DM tool routes PC trigger to decision");
    True(f.ReactorCombatant.ReactionAvailable, "DM tool cannot spend PC Reaction");
    True(f.Campaign.PendingPlayerRoll is null, "DM tool cannot roll PC attack");
});

Run("NPC trigger tool still resolves automatically", () =>
{
    var f = CreateFixture(playerReactor: false);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target advances", "Blade");
    var router = new DmToolRouter(f.Engine, MaximumDice(), new RulesSearchService());
    var result = router.Execute(
        f.Campaign,
        "trigger_readied_attack",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.ReactorCombatant.Id}\"}}");

    True(result.Ok, $"NPC router trigger failed: {result.Error}");
    True(result.Result is EncounterAttackResult, "NPC trigger returns attack result");
    True(!f.ReactorCombatant.ReactionAvailable, "NPC automatic trigger spends Reaction");
    True(f.Campaign.PendingPlayerDecision is null, "NPC trigger creates no player choice");
    True(f.Campaign.PendingPlayerRoll is null, "NPC trigger creates no player attack roll");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied reaction decision tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied reaction decision tests passed: {passed}");

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

static Fixture CreateFixture(bool playerReactor)
{
    var engine = new GameEngine();
    var reactor = NewCharacter("reactor", playerReactor ? "Sentinel" : "Guard", playerReactor ? "pc" : "monster");
    reactor.Attacks.Add(new AttackProfile
    {
        Name = "Blade",
        AttackBonus = 6,
        DamageExpression = "1d6+3",
        DamageType = "Slashing",
        ReachFeet = 5
    });
    var target = NewCharacter("target", "Raider", "monster");
    target.ArmorClass = 12;

    var campaign = new CampaignState
    {
        Id = "readied-decision-campaign",
        Name = "Readied Decision Campaign",
        Characters = [reactor, target]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Decision Encounter");
    var reactorCombatant = engine.AddCombatant(campaign, encounter.Id, reactor.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, reactorCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 1);
    engine.SetInitiative(campaign, encounter.Id, reactorCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, reactor, target, encounter, reactorCombatant, targetCombatant);
}

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    MaxHp = 40,
    CurrentHp = 40,
    ArmorClass = 14,
    Speed = 30,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 12,
        ["dexterity"] = 12,
        ["constitution"] = 12,
        ["wisdom"] = 10,
        ["intelligence"] = 10,
        ["charisma"] = 10
    }
};

static DiceService MinimumDice() => new((min, max) => min);
static DiceService MaximumDice() => new((min, max) => max - 1);

static void True(bool value, string label)
{
    if (!value) throw new Exception(label);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

sealed record Fixture(
    GameEngine Engine,
    CampaignState Campaign,
    CharacterSheet Reactor,
    CharacterSheet Target,
    EncounterState Encounter,
    CombatantState ReactorCombatant,
    CombatantState TargetCombatant);
