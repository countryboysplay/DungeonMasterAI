using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("DM trigger asks PC before spending Reaction or moving", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    var router = new DmToolRouter(f.Engine, MinimumDice(), new RulesSearchService());

    var tool = router.Execute(
        f.Campaign,
        "trigger_readied_move",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.ReactorCombatant.Id}\",\"grid_x\":0,\"grid_y\":2}}");

    True(tool.Ok, $"DM trigger failed: {tool.Error}");
    var decision = tool.Result as PendingPlayerDecision ?? throw new Exception("DM trigger did not return a player decision");
    Equal("readied_move_reaction", decision.DecisionType, "decision type");
    Equal(0, f.ReactorCombatant.GridX, "DM trigger does not move PC X");
    Equal(0, f.ReactorCombatant.GridY, "DM trigger does not move PC Y");
    True(f.ReactorCombatant.ReactionAvailable, "DM trigger does not spend PC Reaction");
    True(f.ReactorCombatant.ReadiedAction is not null, "DM trigger does not consume readied movement");
    True(f.Encounter.PendingMove is null, "DM trigger creates no movement before the player chooses");
});

Run("declining a readied movement trigger preserves Reaction and ready", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);

    var result = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline_trigger", MinimumDice());

    Equal("decline_trigger", result.OptionId, "decline option");
    Equal(0, f.ReactorCombatant.GridY, "decline leaves PC in place");
    True(f.ReactorCombatant.ReactionAvailable, "decline preserves Reaction");
    True(f.ReactorCombatant.ReadiedAction is not null, "decline preserves readied movement");
    True(f.Campaign.PendingPlayerDecision is null, "decline clears the trigger decision");
});

Run("accepting a readied movement trigger commits the exact destination", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);

    var result = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", MinimumDice());

    Equal("use_reaction", result.OptionId, "accept option");
    Equal(0, f.ReactorCombatant.GridX, "accepted destination X");
    Equal(2, f.ReactorCombatant.GridY, "accepted destination Y");
    True(!f.ReactorCombatant.ReactionAvailable, "accepted movement spends Reaction");
    True(f.ReactorCombatant.ReadiedAction is null, "accepted movement consumes readied action");
    True(f.Campaign.PendingPlayerDecision is null, "accepted movement clears decision");
    True(f.Encounter.PendingMove is null, "non-provoking readied movement commits immediately");
});

Run("direct readied movement cannot bypass an active PC decision", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);

    var blocked = false;
    try
    {
        f.Engine.TriggerReadiedMove(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);
    }
    catch (InvalidOperationException)
    {
        blocked = true;
    }

    True(blocked, "direct trigger must be blocked while player decision is pending");
    Equal(decision.Id, f.Campaign.PendingPlayerDecision?.Id, "decision remains authoritative after blocked bypass");
    Equal(0, f.ReactorCombatant.GridY, "blocked bypass does not move PC");
    True(f.ReactorCombatant.ReactionAvailable, "blocked bypass does not spend Reaction");
    True(f.ReactorCombatant.ReadiedAction is not null, "blocked bypass does not consume ready");
});

Run("accepted readied movement that provokes pauses for Opportunity Attacks before movement commits", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: true);
    ReadyAndAdvance(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);

    f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", MinimumDice());

    var pendingMove = f.Encounter.PendingMove ?? throw new Exception("provoking readied move did not create a pending movement window");
    Equal(f.ReactorCombatant.Id, pendingMove.CombatantId, "pending move belongs to player reactor");
    True(pendingMove.ReadiedReactionMove, "pending movement is marked as readied Reaction movement");
    True(pendingMove.OpportunityAttacks.Count > 0, "Opportunity Attack window exists");
    Equal(0, f.ReactorCombatant.GridY, "movement has not committed before Opportunity Attacks resolve");
    True(!f.ReactorCombatant.ReactionAvailable, "accepting the trigger already spent the mover's Reaction");
    True(f.ReactorCombatant.ReadiedAction is null, "accepting the trigger consumed readied movement");
});

Run("NPC readied movement still resolves automatically through DM tool", () =>
{
    var f = CreateFixture(playerReactor: false, provokingEnemy: false);
    ReadyAndAdvance(f);
    var router = new DmToolRouter(f.Engine, MinimumDice(), new RulesSearchService());

    var tool = router.Execute(
        f.Campaign,
        "trigger_readied_move",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.ReactorCombatant.Id}\",\"grid_x\":0,\"grid_y\":2}}");

    True(tool.Ok, $"NPC trigger failed: {tool.Error}");
    True(tool.Result is CombatMoveResult, "NPC trigger returns movement result");
    Equal(2, f.ReactorCombatant.GridY, "NPC movement commits automatically");
    True(!f.ReactorCombatant.ReactionAvailable, "NPC automatic movement spends Reaction");
    True(f.ReactorCombatant.ReadiedAction is null, "NPC automatic movement consumes ready");
    True(f.Campaign.PendingPlayerDecision is null, "NPC movement creates no player decision");
});

Run("pending readied movement decision survives campaign JSON serialization", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);

    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredDecision = restored.PendingPlayerDecision ?? throw new Exception("pending readied move decision missing after restore");
    Equal(decision.Id, restoredDecision.Id, "decision id survives restore");
    Equal("readied_move_reaction", restoredDecision.DecisionType, "decision type survives restore");
    Equal("2", restoredDecision.Context["grid_y"], "destination survives restore");
});

Run("a readied move whose path cannot resolve leaves the Reaction and the ready untouched", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    AddUnrollableHazard(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);

    var threw = false;
    try { f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", MinimumDice()); }
    catch (InvalidOperationException) { threw = true; }

    True(threw, "a path that cannot resolve must throw");
    Equal(0, f.ReactorCombatant.GridY, "the failed trigger must not move the PC");
    True(f.ReactorCombatant.ReactionAvailable, "the failed trigger must not spend the Reaction");
    True(f.ReactorCombatant.ReadiedAction is not null, "the failed trigger must not consume the readied movement");
    Equal(decision.Id, f.Campaign.PendingPlayerDecision?.Id, "the decision must remain pending");

    var declined = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline_trigger", MinimumDice());
    Equal("decline_trigger", declined.OptionId, "the restored decision can still be declined");
    True(declined.Summary.Contains("remain available"), "the ignore summary reports the Reaction that is genuinely still available");
    True(f.ReactorCombatant.ReactionAvailable, "declining preserves the Reaction");
});

Run("a restored readied-move decision can still be accepted once the path resolves", () =>
{
    var f = CreateFixture(playerReactor: true, provokingEnemy: false);
    ReadyAndAdvance(f);
    var hazard = AddUnrollableHazard(f);
    var decision = f.Engine.RequestReadiedMoveDecision(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, 0, 2);
    try { f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", MinimumDice()); }
    catch (InvalidOperationException) { }
    f.Engine.RemoveBattlefieldEffect(f.Campaign, f.Encounter.Id, hazard.Id, "dispelled");

    var result = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", MinimumDice());

    Equal("use_reaction", result.OptionId, "the restored decision can still be accepted");
    Equal(2, f.ReactorCombatant.GridY, "the accepted movement commits");
    True(!f.ReactorCombatant.ReactionAvailable, "the accepted movement spends the Reaction");
    True(f.ReactorCombatant.ReadiedAction is null, "the accepted movement consumes the ready");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied movement decision tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied movement decision tests passed: {passed}");

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

static void ReadyAndAdvance(Fixture f)
{
    f.Engine.TakeReadyMove(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "the enemy advances");
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, MinimumDice());
}

static BattlefieldEffectState AddUnrollableHazard(Fixture f) =>
    f.Engine.AddBattlefieldEffect(f.Campaign, f.Encounter.Id, new BattlefieldEffectState
    {
        Name = "Cinder Bloom",
        Shape = "sphere",
        SizeFeet = 5,
        OriginX = 0,
        OriginY = 2,
        Trigger = "enter",
        // The campaign file supplies a damage string the dice parser rejects.
        DamageExpression = "2d6 + STR",
        DamageType = "Fire"
    });

static Fixture CreateFixture(bool playerReactor, bool provokingEnemy)
{
    var engine = new GameEngine();
    var reactor = NewCharacter("reactor", playerReactor ? "Aric" : "Scout", playerReactor ? "pc" : "monster");
    var enemy = NewCharacter("enemy", playerReactor ? "Raider" : "Aric", playerReactor ? "monster" : "pc");
    enemy.Attacks.Add(new AttackProfile
    {
        Name = "Blade",
        AttackBonus = 5,
        DamageExpression = "1d6+2",
        DamageType = "Slashing",
        ReachFeet = 5
    });

    var campaign = new CampaignState
    {
        Id = "readied-move-campaign",
        Name = "Readied Move Campaign",
        Characters = [reactor, enemy]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Move Encounter");
    var reactorCombatant = engine.AddCombatant(campaign, encounter.Id, reactor.Id, side: "party");
    var enemyCombatant = engine.AddCombatant(campaign, encounter.Id, enemy.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, reactorCombatant.Id, 0, 0);
    if (provokingEnemy)
        engine.SetCombatantPosition(campaign, encounter.Id, enemyCombatant.Id, 1, 0);
    else
        engine.SetCombatantPosition(campaign, encounter.Id, enemyCombatant.Id, 6, 6);
    engine.SetInitiative(campaign, encounter.Id, reactorCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, enemyCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, reactor, enemy, encounter, reactorCombatant, enemyCombatant);
}

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    MaxHp = 30,
    CurrentHp = 30,
    ArmorClass = 14,
    Speed = 30,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 12,
        ["dexterity"] = 14,
        ["constitution"] = 12,
        ["intelligence"] = 10,
        ["wisdom"] = 10,
        ["charisma"] = 10
    }
};

static DiceService MinimumDice() => new((min, max) => min);

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
    CharacterSheet Enemy,
    EncounterState Encounter,
    CombatantState ReactorCombatant,
    CombatantState EnemyCombatant);
