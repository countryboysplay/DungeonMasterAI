using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("provoking movement creates a required player Opportunity Attack decision", () =>
{
    var f = CreateFixture(twoPlayerReactors: false);
    var move = f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 4);

    True(!move.Committed, "provoking movement pauses");
    True(f.Encounter.PendingMove is not null, "pending move exists");
    True(f.Campaign.PendingPlayerRoll is null, "no die is rolled before the player chooses a reaction");
    var decision = f.Campaign.PendingPlayerDecision ?? throw new Exception("player decision missing");
    Equal("opportunity_attack_reaction", decision.DecisionType, "decision type");
    Equal(f.ReactorOne.Id, decision.ActorCharacterId, "reacting player owns decision");
    True(decision.Required, "reaction choice is required before movement can continue");
    True(decision.Options.Any(o => o.Id == "use_reaction"), "use reaction option exists");
    True(decision.Options.Any(o => o.Id == "decline"), "decline option exists");
    True(f.ReactorOneCombatant.ReactionAvailable, "reaction is not spent merely because the choice appeared");
    Equal(1, f.MoverCombatant.GridY, "mover stays at origin while choice is pending");
});

Run("choosing Opportunity Attack spends Reaction and creates the attack roll", () =>
{
    var f = CreateFixture(twoPlayerReactors: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 4);
    var decision = f.Campaign.PendingPlayerDecision ?? throw new Exception("decision missing");

    var result = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction");
    True(f.Campaign.PendingPlayerDecision is null, "decision clears after choice");
    var pending = result.FollowUpRoll ?? f.Campaign.PendingPlayerRoll ?? throw new Exception("follow-up attack roll missing");
    Equal("opportunity_attack", pending.ResolutionKey, "reaction choice creates authoritative attack roll");
    Equal(f.ReactorOne.Id, pending.ActorCharacterId, "reacting player owns follow-up attack die");
    True(!f.ReactorOneCombatant.ReactionAvailable, "Reaction is spent only after player chooses to use it");
    True(f.Encounter.PendingMove is not null, "movement stays frozen while attack roll is pending");
    Equal(1, f.MoverCombatant.GridY, "mover remains at origin while attack roll is pending");
});

Run("declining Opportunity Attack preserves Reaction and completes movement", () =>
{
    var f = CreateFixture(twoPlayerReactors: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 4);
    var decision = f.Campaign.PendingPlayerDecision ?? throw new Exception("decision missing");

    var result = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline");
    Equal("decline", result.OptionId, "decline option recorded");
    True(f.Campaign.PendingPlayerDecision is null, "decline clears decision");
    True(f.Campaign.PendingPlayerRoll is null, "decline creates no die roll");
    True(f.ReactorOneCombatant.ReactionAvailable, "declining does not spend Reaction");
    True(f.Encounter.PendingMove is null, "movement completes after final reaction window is declined");
    Equal(4, f.MoverCombatant.GridY, "mover reaches destination after decline");
});

Run("multiple player reactors are offered in deterministic reaction-window order", () =>
{
    var f = CreateFixture(twoPlayerReactors: true);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 5);
    var first = f.Campaign.PendingPlayerDecision ?? throw new Exception("first player decision missing");
    Equal(f.ReactorOne.Id, first.ActorCharacterId, "first unresolved reaction window owns first decision");

    f.Engine.ResolvePendingPlayerDecision(f.Campaign, first.Id, "decline");
    var second = f.Campaign.PendingPlayerDecision ?? throw new Exception("second player decision missing");
    Equal(f.ReactorTwo!.Id, second.ActorCharacterId, "second player decision appears only after first window resolves");
    True(f.Encounter.PendingMove is not null, "movement remains paused between player reaction decisions");

    f.Engine.ResolvePendingPlayerDecision(f.Campaign, second.Id, "decline");
    True(f.Campaign.PendingPlayerDecision is null, "all decisions clear after both windows resolve");
    True(f.Encounter.PendingMove is null, "movement completes after both reactions resolve");
    Equal(5, f.MoverCombatant.GridY, "mover reaches destination after ordered declines");
});

Run("player decision survives campaign JSON serialization", () =>
{
    var f = CreateFixture(twoPlayerReactors: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 4);
    var original = f.Campaign.PendingPlayerDecision ?? throw new Exception("decision missing before serialization");

    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var decision = restored.PendingPlayerDecision ?? throw new Exception("decision missing after restore");
    Equal(original.Id, decision.Id, "decision id survives restore");
    Equal(2, decision.Options.Count, "decision options survive restore");
    Equal("opportunity_attack_reaction", decision.DecisionType, "decision type survives restore");

    var restoredEncounter = restored.Encounters.Single(e => e.Id == f.Encounter.Id);
    var restoredMover = restoredEncounter.Combatants.Single(c => c.Id == f.MoverCombatant.Id);
    f.Engine.ResolvePendingPlayerDecision(restored, decision.Id, "decline");
    True(restored.PendingPlayerDecision is null, "restored decision resolves");
    True(restoredEncounter.PendingMove is null, "restored movement completes after decline");
    Equal(4, restoredMover.GridY, "restored mover reaches destination");
});

Run("invalid player decision option cannot mutate reaction or movement", () =>
{
    var f = CreateFixture(twoPlayerReactors: false);
    f.Engine.MoveCombatant(f.Campaign, f.Encounter.Id, f.MoverCombatant.Id, 0, 4);
    var decision = f.Campaign.PendingPlayerDecision ?? throw new Exception("decision missing");

    var rejected = false;
    try { f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "invented_option"); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "invalid decision option is rejected");
    Equal(decision.Id, f.Campaign.PendingPlayerDecision?.Id, "valid decision remains pending");
    True(f.ReactorOneCombatant.ReactionAvailable, "Reaction remains available");
    True(f.Encounter.PendingMove is not null, "movement remains frozen");
    Equal(1, f.MoverCombatant.GridY, "mover position remains unchanged");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Player decision tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Player decision tests passed: {passed}");

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

static Fixture CreateFixture(bool twoPlayerReactors)
{
    var engine = new GameEngine();
    var mover = Character("mover", "Raider", "monster");
    mover.Speed = 30;

    var reactorOne = Character("reactor-one", "Aric", "pc");
    reactorOne.Attacks.Add(new AttackProfile
    {
        Name = "Longsword",
        AttackBonus = 6,
        DamageExpression = "1d8+3",
        DamageType = "Slashing",
        ReachFeet = 5
    });

    CharacterSheet? reactorTwo = null;
    if (twoPlayerReactors)
    {
        reactorTwo = Character("reactor-two", "Bryn", "pc");
        reactorTwo.Attacks.Add(new AttackProfile
        {
            Name = "Spear",
            AttackBonus = 5,
            DamageExpression = "1d6+3",
            DamageType = "Piercing",
            ReachFeet = 5
        });
    }

    var characters = new List<CharacterSheet> { mover, reactorOne };
    if (reactorTwo is not null) characters.Add(reactorTwo);
    var campaign = new CampaignState
    {
        Id = "decision-campaign",
        Name = "Decision Campaign",
        Characters = characters
    };
    var encounter = engine.StartEncounter(campaign, "Decision Encounter");
    var moverCombatant = engine.AddCombatant(campaign, encounter.Id, mover.Id, side: "opposition");
    var reactorOneCombatant = engine.AddCombatant(campaign, encounter.Id, reactorOne.Id, side: "party");
    engine.SetCombatantPosition(campaign, encounter.Id, moverCombatant.Id, 0, 1);
    engine.SetCombatantPosition(campaign, encounter.Id, reactorOneCombatant.Id, 0, 0);
    engine.SetInitiative(campaign, encounter.Id, moverCombatant.Id, 30);
    engine.SetInitiative(campaign, encounter.Id, reactorOneCombatant.Id, 20);

    CombatantState? reactorTwoCombatant = null;
    if (reactorTwo is not null)
    {
        reactorTwoCombatant = engine.AddCombatant(campaign, encounter.Id, reactorTwo.Id, side: "party");
        engine.SetCombatantPosition(campaign, encounter.Id, reactorTwoCombatant.Id, 1, 1);
        engine.SetInitiative(campaign, encounter.Id, reactorTwoCombatant.Id, 10);
    }

    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(
        engine,
        campaign,
        mover,
        reactorOne,
        reactorTwo,
        encounter,
        moverCombatant,
        reactorOneCombatant,
        reactorTwoCombatant);
}

static CharacterSheet Character(string id, string name, string type) => new()
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
        ["strength"] = 14,
        ["dexterity"] = 12,
        ["constitution"] = 12,
        ["wisdom"] = 10,
        ["intelligence"] = 10,
        ["charisma"] = 10
    }
};

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
    CharacterSheet ReactorOne,
    CharacterSheet? ReactorTwo,
    EncounterState Encounter,
    CombatantState MoverCombatant,
    CombatantState ReactorOneCombatant,
    CombatantState? ReactorTwoCombatant);
