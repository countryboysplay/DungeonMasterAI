using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player Search waits for supplied d20 before spending the action", () =>
{
    var f = CreateSingleActorFixture(player: true);
    var pending = f.Engine.RequestSearchActionRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, "perception", 10);

    Equal("combat_skill_action", pending.ResolutionKey, "pending key");
    Equal(f.Actor.Id, pending.ActorCharacterId, "player owns Search roll");
    Equal(10, pending.TargetNumber, "Search DC");
    True(f.ActorCombatant.ActionAvailable, "Search action must remain available while roll is pending");

    var result = f.Engine.ResolvePendingCombatSkillActionRoll(f.Campaign, pending.Id, 5);
    Equal(5, result.Check.ChosenRoll, "supplied Search d20 is authoritative");
    Equal(9, result.Check.Total, "Wisdom + Perception proficiency are applied after supplied d20");
    True(!f.ActorCombatant.ActionAvailable, "Search consumes the action only after roll resolution");
    True(f.Campaign.PendingPlayerRoll is null, "Search pending roll clears after resolution");
});

Run("Poisoned player Search requires two supplied d20 results", () =>
{
    var f = CreateSingleActorFixture(player: true);
    f.Actor.Conditions.Add("Poisoned");
    var pending = f.Engine.RequestSearchActionRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, "perception", 10);
    Equal("disadvantage", pending.RollMode, "Poisoned Search roll mode");

    var rejected = false;
    try { f.Engine.ResolvePendingCombatSkillActionRoll(f.Campaign, pending.Id, 18); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "Disadvantage Search rejects only one supplied d20");
    True(f.ActorCombatant.ActionAvailable, "rejected roll must not spend the action");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "rejected roll preserves pending request");

    var result = f.Engine.ResolvePendingCombatSkillActionRoll(f.Campaign, pending.Id, 18, 4);
    Equal(4, result.Check.ChosenRoll, "lower supplied d20 is used for Disadvantage");
});

Run("Study uses Intelligence and the selected Study skill", () =>
{
    var f = CreateSingleActorFixture(player: true);
    var pending = f.Engine.RequestStudyActionRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, "investigation", 15);
    Equal("intelligence", pending.Context["ability"], "Study ability");
    Equal("investigation", pending.Context["skill"], "Study skill");

    var result = f.Engine.ResolvePendingCombatSkillActionRoll(f.Campaign, pending.Id, 10);
    Equal("Study", result.ActionName, "Study action name");
    Equal("intelligence", result.Ability, "Study result ability");
    Equal(15, result.Check.Total, "Intelligence + Investigation proficiency applied to supplied roll");
    True(result.Check.Success, "supplied Study roll meets DC");
});

Run("Influence with Animal Handling uses Wisdom rather than Charisma", () =>
{
    var f = CreateSingleActorFixture(player: true);
    f.Actor.SkillProficiencies.Add("animal handling");
    var pending = f.Engine.RequestInfluenceActionRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, "animal handling", 12);
    Equal("wisdom", pending.Context["ability"], "Animal Handling Influence ability");

    var result = f.Engine.ResolvePendingCombatSkillActionRoll(f.Campaign, pending.Id, 8);
    Equal("wisdom", result.Ability, "Influence result ability");
    Equal(12, result.Check.Total, "Wisdom and Animal Handling proficiency apply to supplied roll");
});

Run("Help Advantage is preserved until the player's Search roll resolves", () =>
{
    var engine = new GameEngine();
    var helper = NewCharacter("helper", "Helper", "pc");
    helper.SkillProficiencies.Add("perception");
    var actor = NewCharacter("actor", "Searcher", "pc");
    var campaign = new CampaignState { Id = "help-campaign", Name = "Help Campaign", Characters = [helper, actor] };
    var encounter = engine.StartEncounter(campaign, "Help Search");
    var helperCombatant = engine.AddCombatant(campaign, encounter.Id, helper.Id, side: "party");
    var actorCombatant = engine.AddCombatant(campaign, encounter.Id, actor.Id, side: "party");
    engine.SetInitiative(campaign, encounter.Id, helperCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, actorCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);

    engine.TakeHelpAbilityCheck(campaign, encounter.Id, helperCombatant.Id, actorCombatant.Id, "perception");
    engine.NextTurn(campaign, encounter.Id);
    var pending = engine.RequestSearchActionRoll(campaign, encounter.Id, actorCombatant.Id, "perception", 10);
    Equal("advantage", pending.RollMode, "Help supplies Advantage to Search");
    Equal(actor.Id, helperCombatant.HelpAbilityTargetCharacterId, "Help remains reserved while roll is pending");

    var result = engine.ResolvePendingCombatSkillActionRoll(campaign, pending.Id, 3, 17);
    Equal(17, result.Check.ChosenRoll, "higher supplied d20 is used for Help Advantage");
    True(helperCombatant.HelpAbilityTargetCharacterId is null && helperCombatant.HelpAbilityProficiency is null,
        "Help is consumed only after successful player roll resolution");
});

Run("NPC Search remains automatic and never creates a player roll", () =>
{
    var f = CreateSingleActorFixture(player: false);
    var dice = new DiceService((min, max) => min);
    var result = f.Engine.TakeSearchAction(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, "perception", 10, dice);
    Equal(1, result.Check.ChosenRoll, "NPC Search uses application dice");
    True(f.Campaign.PendingPlayerRoll is null, "NPC Search does not create a player request");
    True(!f.ActorCombatant.ActionAvailable, "NPC Search consumes its action immediately");
});

Run("pending combat skill action survives campaign JSON serialization", () =>
{
    var f = CreateSingleActorFixture(player: true);
    var pending = f.Engine.RequestStudyActionRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, "investigation", 12);
    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredPending = restored.PendingPlayerRoll ?? throw new Exception("pending roll did not survive restore");
    Equal(pending.Id, restoredPending.Id, "pending id after restore");
    Equal("combat_skill_action", restoredPending.ResolutionKey, "pending resolution key after restore");

    var restoredCombatant = restored.Encounters.Single(e => e.Id == f.Encounter.Id).Combatants.Single(c => c.Id == f.ActorCombatant.Id);
    var result = f.Engine.ResolvePendingCombatSkillActionRoll(restored, restoredPending.Id, 12);
    Equal(12, result.Check.ChosenRoll, "restored request uses supplied d20");
    True(!restoredCombatant.ActionAvailable, "restored combatant spends action after resolution");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Combat skill roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Combat skill roll tests passed: {passed}");

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

static Fixture CreateSingleActorFixture(bool player)
{
    var engine = new GameEngine();
    var actor = NewCharacter("actor", player ? "Aric" : "Watcher Scout", player ? "pc" : "npc");
    var campaign = new CampaignState
    {
        Id = player ? "combat-skill-player" : "combat-skill-npc",
        Name = "Combat Skill Campaign",
        Characters = [actor]
    };
    var encounter = engine.StartEncounter(campaign, "Skill Encounter");
    var combatant = engine.AddCombatant(campaign, encounter.Id, actor.Id, side: player ? "party" : "opposition");
    engine.SetInitiative(campaign, encounter.Id, combatant.Id, 20);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, actor, encounter, combatant);
}

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    MaxHp = 30,
    CurrentHp = 30,
    ArmorClass = 14,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["wisdom"] = 14,
        ["intelligence"] = 16,
        ["charisma"] = 12
    },
    SkillProficiencies = ["perception", "investigation"]
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
    CharacterSheet Actor,
    EncounterState Encounter,
    CombatantState ActorCombatant);
