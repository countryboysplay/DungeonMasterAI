using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player Opportunity Attack pauses movement for supplied attack and damage rolls", () =>
{
    var f = CreateFixture(playerReactor: true, playerMover: false);
    var move = f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    True(!move.Committed && f.Encounter.PendingMove is not null, "provoking movement should pause");
    Equal(1, f.Encounter.PendingMove!.OpportunityAttacks.Count, "one reaction window expected");
    Equal(1, f.MoverCombatant.GridY, "mover stays at origin while reaction unresolved");

    var pending = f.Engine.RequestOpportunityAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade");
    Equal("opportunity_attack", pending.ResolutionKey, "Opportunity Attack pending key");
    Equal(f.Reactor.Id, pending.ActorCharacterId, "reactor owns attack roll");
    True(!f.ReactorCombatant.ReactionAvailable, "declaring Opportunity Attack commits the Reaction");
    Equal(1, f.MoverCombatant.GridY, "movement remains frozen while attack d20 is pending");

    var attack = f.Engine.ResolvePendingOpportunityAttackRoll(f.Campaign, pending.Id, 19, null, MinimumDice());
    True(attack.Attack.Hit, "supplied attack d20 hits");
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("damage pending missing");
    Equal("opportunity_attack_damage", damagePending.ResolutionKey, "hit requests player damage");
    Equal(1, f.MoverCombatant.GridY, "movement remains frozen while damage is pending");
    var hpBefore = f.Mover.CurrentHp;

    var complete = f.Engine.ResolvePendingOpportunityAttackDamageRoll(f.Campaign, damagePending.Id, 7, MinimumDice());
    Equal(hpBefore - 7, f.Mover.CurrentHp, "supplied damage total is authoritative");
    Equal(7, complete.Damage?.RequestedDamage, "result preserves supplied damage");
    True(f.Campaign.PendingPlayerRoll is null, "damage completion clears pending player roll");
    True(f.Encounter.PendingMove is null, "reaction completion resumes movement");
    Equal(3, f.MoverCombatant.GridY, "mover reaches destination after Opportunity Attack resolves");
});

Run("supplied Opportunity Attack miss immediately resumes pending movement", () =>
{
    var f = CreateFixture(playerReactor: true, playerMover: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    var pending = f.Engine.RequestOpportunityAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade");
    var result = f.Engine.ResolvePendingOpportunityAttackRoll(f.Campaign, pending.Id, 1, null, MinimumDice());
    True(!result.Attack.Hit, "natural 1 misses");
    True(f.Campaign.PendingPlayerRoll is null, "miss clears pending player roll");
    True(f.Encounter.PendingMove is null, "miss finishes reaction window");
    Equal(3, f.MoverCombatant.GridY, "pending movement resumes after miss");
});

Run("Opportunity Attack Disadvantage requires two supplied d20 results", () =>
{
    var f = CreateFixture(playerReactor: true, playerMover: false);
    f.Reactor.Conditions.Add("Poisoned");
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    var pending = f.Engine.RequestOpportunityAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade");
    Equal("disadvantage", pending.RollMode, "Poisoned attack roll mode");

    var rejected = false;
    try { f.Engine.ResolvePendingOpportunityAttackRoll(f.Campaign, pending.Id, 18, null, MinimumDice()); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "one d20 cannot satisfy Opportunity Attack Disadvantage");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "invalid roll remains pending");
    True(!f.ReactorCombatant.ReactionAvailable, "committed Reaction stays spent after invalid dice submission");

    var result = f.Engine.ResolvePendingOpportunityAttackRoll(f.Campaign, pending.Id, 18, 3, MinimumDice());
    Equal(3, result.Attack.D20, "lower supplied d20 is authoritative for Disadvantage");
});

Run("natural 20 Opportunity Attack creates critical player damage formula", () =>
{
    var f = CreateFixture(playerReactor: true, playerMover: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    var pending = f.Engine.RequestOpportunityAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade");
    var result = f.Engine.ResolvePendingOpportunityAttackRoll(f.Campaign, pending.Id, 20, null, MinimumDice());
    True(result.Attack.Hit && result.Attack.Critical, "natural 20 is critical");
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("critical damage pending missing");
    Equal("opportunity_attack_damage", damagePending.ResolutionKey, "critical hit requests damage");
    True(damagePending.Formula.StartsWith("2d6", StringComparison.OrdinalIgnoreCase), "critical formula doubles Blade damage dice");
});

Run("Opportunity Attack damage hands off to player Concentration before movement resumes", () =>
{
    var f = CreateFixture(playerReactor: true, playerMover: true);
    f.Engine.BeginConcentration(f.Campaign, f.Mover.Id, "Bless");
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    var pending = f.Engine.RequestOpportunityAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade");
    f.Engine.ResolvePendingOpportunityAttackRoll(f.Campaign, pending.Id, 19, null, MinimumDice());
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("damage pending missing");
    f.Engine.ResolvePendingOpportunityAttackDamageRoll(f.Campaign, damagePending.Id, 4, MinimumDice());

    var concentrationPending = f.Campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing");
    Equal("concentration_check", concentrationPending.ResolutionKey, "damage hands off to Concentration");
    Equal("opportunity_attack_move", concentrationPending.Context["continuation_resolution_key"], "Concentration stores movement continuation");
    True(f.Encounter.PendingMove is not null, "movement remains frozen during Concentration save");
    Equal(1, f.MoverCombatant.GridY, "mover remains at trigger origin during Concentration save");

    f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentrationPending.Id, 20, null, MinimumDice());
    True(f.Campaign.PendingPlayerRoll is null, "Concentration completion clears pending roll");
    True(f.Encounter.PendingMove is null, "Concentration continuation completes reaction movement");
    Equal(3, f.MoverCombatant.GridY, "movement resumes only after Concentration resolves");
});

Run("NPC Opportunity Attack keeps automatic deterministic resolution", () =>
{
    var f = CreateFixture(playerReactor: false, playerMover: true);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    var result = f.Engine.ResolveOpportunityAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade", MaximumDice());
    True(result.UsedReaction, "NPC used Reaction");
    True(f.Campaign.PendingPlayerRoll is null, "NPC Opportunity Attack creates no player pending roll");
    True(f.Encounter.PendingMove is null, "automatic NPC reaction completes movement window");
});

Run("pending player Opportunity Attack survives campaign JSON serialization", () =>
{
    var f = CreateFixture(playerReactor: true, playerMover: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 3);
    var pending = f.Engine.RequestOpportunityAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, "Blade");
    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredPending = restored.PendingPlayerRoll ?? throw new Exception("Opportunity Attack pending roll missing after restore");
    Equal(pending.Id, restoredPending.Id, "pending Opportunity Attack id survives restore");
    True(restored.Encounters.Single(e => e.Id == f.Encounter.Id).PendingMove is not null, "provoking movement survives restore");

    var result = f.Engine.ResolvePendingOpportunityAttackRoll(restored, restoredPending.Id, 1, null, MinimumDice());
    True(!result.Attack.Hit, "restored supplied natural 1 resolves as miss");
    True(restored.Encounters.Single(e => e.Id == f.Encounter.Id).PendingMove is null, "restored movement resumes after reaction resolves");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Opportunity Attack roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Opportunity Attack roll tests passed: {passed}");

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

static Fixture CreateFixture(bool playerReactor, bool playerMover)
{
    var engine = new GameEngine();
    var mover = NewCharacter("mover", playerMover ? "Aric" : "Raider", playerMover ? "pc" : "monster");
    mover.ArmorClass = 12;
    mover.Speed = 30;
    mover.Abilities["constitution"] = 14;

    var reactor = NewCharacter("reactor", playerReactor ? "Sentinel" : "Guard", playerReactor ? "pc" : "monster");
    reactor.Attacks.Add(new AttackProfile
    {
        Name = "Blade",
        AttackBonus = 6,
        DamageExpression = "1d6+3",
        DamageType = "Slashing",
        ReachFeet = 5
    });

    var campaign = new CampaignState { Id = "oa-campaign", Name = "Opportunity Attack Campaign", Characters = [mover, reactor] };
    var encounter = engine.StartEncounter(campaign, "Opportunity Attack Encounter");
    var moverCombatant = engine.AddCombatant(campaign, encounter.Id, mover.Id, side: "party");
    var reactorCombatant = engine.AddCombatant(campaign, encounter.Id, reactor.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, moverCombatant.Id, 0, 1);
    engine.SetCombatantPosition(campaign, encounter.Id, reactorCombatant.Id, 0, 0);
    engine.SetInitiative(campaign, encounter.Id, moverCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, reactorCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, mover, reactor, encounter, moverCombatant, reactorCombatant);
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
    CharacterSheet Mover,
    CharacterSheet Reactor,
    EncounterState Encounter,
    CombatantState MoverCombatant,
    CombatantState ReactorCombatant);
