using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player Hide waits for supplied d20 before spending the action", () =>
{
    var f = CreateHideFixture(playerHider: true);
    var pending = f.Engine.RequestHideRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id);
    Equal("hide_check", pending.ResolutionKey, "Hide resolution key");
    Equal(f.Actor.Id, pending.ActorCharacterId, "Hide actor");
    True(f.ActorCombatant.ActionAvailable, "Hide action stays available while roll is pending");
    True(!f.ActorCombatant.IsHidden, "character must not become hidden before roll resolves");

    var result = f.Engine.ResolvePendingHideRoll(f.Campaign, pending.Id, 20);
    Equal(20, result.StealthCheck.ChosenRoll, "supplied Hide d20 is authoritative");
    True(result.Hidden && f.ActorCombatant.IsHidden, "successful Hide sets hidden state");
    True(!f.ActorCombatant.ActionAvailable, "Hide spends the action after resolution");
    True(f.Campaign.PendingPlayerRoll is null, "Hide pending roll clears");
});

Run("Poisoned player Hide requires two supplied d20 results", () =>
{
    var f = CreateHideFixture(playerHider: true);
    f.Actor.Conditions.Add("Poisoned");
    var pending = f.Engine.RequestHideRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id);
    Equal("disadvantage", pending.RollMode, "Poisoned Hide roll mode");

    var rejected = false;
    try { f.Engine.ResolvePendingHideRoll(f.Campaign, pending.Id, 18); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "one d20 cannot satisfy Hide Disadvantage");
    True(f.ActorCombatant.ActionAvailable, "invalid Hide roll does not spend action");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "invalid Hide roll remains pending");

    var result = f.Engine.ResolvePendingHideRoll(f.Campaign, pending.Id, 18, 4);
    Equal(4, result.StealthCheck.ChosenRoll, "lower supplied d20 is authoritative for Disadvantage");
});

Run("player hidden Search uses supplied Perception d20", () =>
{
    var f = CreateHiddenSearchFixture();
    var pending = f.Engine.RequestHiddenSearchRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, f.TargetCombatant.Id);
    Equal("search_hidden_check", pending.ResolutionKey, "hidden Search key");
    Equal(12, pending.TargetNumber, "hidden target's stored Hide total is the Search DC");
    True(f.ActorCombatant.ActionAvailable, "Search action stays available while roll is pending");
    True(f.TargetCombatant.IsHidden, "target stays hidden while search roll is pending");

    var result = f.Engine.ResolvePendingHiddenSearchRoll(f.Campaign, pending.Id, 20);
    Equal(20, result.PerceptionCheck.ChosenRoll, "supplied Perception d20 is authoritative");
    True(result.Found && !f.TargetCombatant.IsHidden, "successful Search reveals hidden target");
    True(!f.ActorCombatant.ActionAvailable, "Search spends action on resolution");
});

Run("Help Advantage on hidden Search survives until supplied roll resolves", () =>
{
    var engine = new GameEngine();
    var helper = NewCharacter("helper", "Helper", "pc");
    helper.SkillProficiencies.Add("perception");
    var searcher = NewCharacter("searcher", "Searcher", "pc");
    searcher.SkillProficiencies.Add("perception");
    var target = NewCharacter("target", "Hidden Target", "monster");
    var campaign = new CampaignState { Id = "hidden-help", Name = "Hidden Help", Characters = [helper, searcher, target] };
    var encounter = engine.StartEncounter(campaign, "Hidden Help");
    var helperCombatant = engine.AddCombatant(campaign, encounter.Id, helper.Id, side: "party");
    var searcherCombatant = engine.AddCombatant(campaign, encounter.Id, searcher.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "opposition");
    engine.SetInitiative(campaign, encounter.Id, helperCombatant.Id, 30);
    engine.SetInitiative(campaign, encounter.Id, searcherCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    targetCombatant.IsHidden = true;
    targetCombatant.HideCheckTotal = 14;

    engine.TakeHelpAbilityCheck(campaign, encounter.Id, helperCombatant.Id, searcherCombatant.Id, "perception");
    engine.NextTurn(campaign, encounter.Id);
    var pending = engine.RequestHiddenSearchRoll(campaign, encounter.Id, searcherCombatant.Id, targetCombatant.Id);
    Equal("advantage", pending.RollMode, "Help supplies Advantage to hidden Search");
    Equal(searcher.Id, helperCombatant.HelpAbilityTargetCharacterId, "Help remains reserved while Search is pending");

    var result = engine.ResolvePendingHiddenSearchRoll(campaign, pending.Id, 3, 19);
    Equal(19, result.PerceptionCheck.ChosenRoll, "higher supplied d20 is used with Help Advantage");
    True(helperCombatant.HelpAbilityTargetCharacterId is null && helperCombatant.HelpAbilityProficiency is null,
        "Help is consumed only after Search roll resolves");
});

Run("player First Aid waits for supplied Medicine d20 and stabilizes on success", () =>
{
    var f = CreateFirstAidFixture(playerHelper: true);
    var pending = f.Engine.RequestFirstAidRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, f.TargetCombatant.Id);
    Equal("first_aid_check", pending.ResolutionKey, "First Aid key");
    True(f.ActorCombatant.ActionAvailable, "First Aid does not spend action while pending");
    True(!f.Target.Stable, "target is not stabilized before roll resolves");

    var result = f.Engine.ResolvePendingFirstAidRoll(f.Campaign, pending.Id, 20);
    Equal(20, result.MedicineCheck.ChosenRoll, "supplied Medicine d20 is authoritative");
    True(result.Stabilized && f.Target.Stable, "successful First Aid stabilizes target");
    Equal(0, f.Target.DeathSaveFailures, "First Aid clears death-save failures on stabilization");
    Equal(0, f.Target.DeathSaveSuccesses, "First Aid clears death-save successes on stabilization");
    True(!f.ActorCombatant.ActionAvailable, "First Aid spends action after valid resolution");
});

Run("failed player First Aid spends the action but does not stabilize", () =>
{
    var f = CreateFirstAidFixture(playerHelper: true);
    f.Actor.Abilities["wisdom"] = 1;
    f.Actor.SkillProficiencies.Clear();
    var pending = f.Engine.RequestFirstAidRoll(f.Campaign, f.Encounter.Id, f.ActorCombatant.Id, f.TargetCombatant.Id);
    var result = f.Engine.ResolvePendingFirstAidRoll(f.Campaign, pending.Id, 1);
    True(!result.MedicineCheck.Success && !result.Stabilized, "failed First Aid remains a failure");
    True(!f.Target.Stable, "failed First Aid does not stabilize target");
    True(!f.ActorCombatant.ActionAvailable, "failed but valid First Aid spends action");
});

Run("NPC Hide and First Aid remain automatic", () =>
{
    var hide = CreateHideFixture(playerHider: false);
    var dice = new DiceService((min, max) => max - 1);
    var hideResult = hide.Engine.TakeHide(hide.Campaign, hide.Encounter.Id, hide.ActorCombatant.Id, dice);
    True(hideResult.Hidden, "NPC Hide resolves automatically");
    True(hide.Campaign.PendingPlayerRoll is null, "NPC Hide does not create player roll");

    var aid = CreateFirstAidFixture(playerHelper: false);
    var aidResult = aid.Engine.TakeFirstAid(aid.Campaign, aid.Encounter.Id, aid.ActorCombatant.Id, aid.TargetCombatant.Id, dice);
    True(aidResult.Stabilized && aid.Target.Stable, "NPC First Aid resolves automatically");
    True(aid.Campaign.PendingPlayerRoll is null, "NPC First Aid does not create player roll");
});

Run("pending stealth and aid rolls survive campaign JSON serialization", () =>
{
    var hide = CreateHideFixture(playerHider: true);
    var pending = hide.Engine.RequestHideRoll(hide.Campaign, hide.Encounter.Id, hide.ActorCombatant.Id);
    var json = JsonSerializer.Serialize(hide.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredPending = restored.PendingPlayerRoll ?? throw new Exception("pending Hide roll missing after restore");
    Equal(pending.Id, restoredPending.Id, "pending Hide id survives restore");
    var result = hide.Engine.ResolvePendingHideRoll(restored, restoredPending.Id, 20);
    True(result.Hidden, "restored Hide request resolves normally");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Stealth and first-aid roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Stealth and first-aid roll tests passed: {passed}");

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

static Fixture CreateHideFixture(bool playerHider)
{
    var engine = new GameEngine();
    var actor = NewCharacter("hider", playerHider ? "Aric" : "Watcher Scout", playerHider ? "pc" : "npc");
    actor.Abilities["dexterity"] = 18;
    actor.SkillProficiencies.Add("stealth");
    var enemy = NewCharacter("observer", "Observer", "monster");
    var campaign = new CampaignState { Id = "hide-campaign", Name = "Hide Campaign", Characters = [actor, enemy] };
    var encounter = engine.StartEncounter(campaign, "Hide Encounter");
    var actorCombatant = engine.AddCombatant(campaign, encounter.Id, actor.Id, side: playerHider ? "party" : "opposition");
    var enemyCombatant = engine.AddCombatant(campaign, encounter.Id, enemy.Id, side: playerHider ? "opposition" : "party");
    engine.SetCombatantPosition(campaign, encounter.Id, actorCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, enemyCombatant.Id, 0, 2);
    engine.AddTerrainFeature(campaign, encounter.Id, new TerrainFeature
    {
        Name = "Dense Smoke",
        GridX = 0,
        GridY = 0,
        HeavilyObscured = true,
        BlocksLineOfSight = true
    });
    engine.SetInitiative(campaign, encounter.Id, actorCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, enemyCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, actor, enemy, encounter, actorCombatant, enemyCombatant);
}

static Fixture CreateHiddenSearchFixture()
{
    var engine = new GameEngine();
    var searcher = NewCharacter("searcher", "Aric", "pc");
    searcher.Abilities["wisdom"] = 16;
    searcher.SkillProficiencies.Add("perception");
    var target = NewCharacter("target", "Hidden Watcher", "monster");
    var campaign = new CampaignState { Id = "search-hidden", Name = "Search Hidden", Characters = [searcher, target] };
    var encounter = engine.StartEncounter(campaign, "Search Hidden");
    var searcherCombatant = engine.AddCombatant(campaign, encounter.Id, searcher.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "opposition");
    engine.SetInitiative(campaign, encounter.Id, searcherCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    targetCombatant.IsHidden = true;
    targetCombatant.HideCheckTotal = 12;
    return new Fixture(engine, campaign, searcher, target, encounter, searcherCombatant, targetCombatant);
}

static Fixture CreateFirstAidFixture(bool playerHelper)
{
    var engine = new GameEngine();
    var helper = NewCharacter("medic", playerHelper ? "Aric" : "Field Medic", playerHelper ? "pc" : "npc");
    helper.Abilities["wisdom"] = 16;
    helper.SkillProficiencies.Add("medicine");
    var target = NewCharacter("fallen", "Fallen Ally", "pc");
    target.MaxHp = 12;
    target.CurrentHp = 12;
    var campaign = new CampaignState { Id = "aid-campaign", Name = "Aid Campaign", Characters = [helper, target] };
    engine.ApplyDamageDetailed(campaign, target.Id, 12, "Bludgeoning");
    var encounter = engine.StartEncounter(campaign, "Aid Encounter");
    var helperCombatant = engine.AddCombatant(campaign, encounter.Id, helper.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "party");
    engine.SetCombatantPosition(campaign, encounter.Id, helperCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 1);
    engine.SetInitiative(campaign, encounter.Id, helperCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, helper, target, encounter, helperCombatant, targetCombatant);
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
        ["strength"] = 10,
        ["dexterity"] = 10,
        ["constitution"] = 10,
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
    CharacterSheet Actor,
    CharacterSheet Target,
    EncounterState Encounter,
    CombatantState ActorCombatant,
    CombatantState TargetCombatant);
