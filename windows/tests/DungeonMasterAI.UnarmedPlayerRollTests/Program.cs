using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("grapple against a player pauses for the target saving throw", () =>
{
    var f = CreateUnarmedFixture(playerTarget: true);
    var pending = f.Engine.RequestUnarmedGrappleSaveRoll(f.Campaign, f.Encounter.Id, f.AttackerCombatant.Id, f.TargetCombatant.Id, "strength");
    Equal("unarmed_grapple_save", pending.ResolutionKey, "grapple pending key");
    Equal(f.Target.Id, pending.ActorCharacterId, "target owns grapple save");
    True(f.AttackerCombatant.AttacksMadeThisAction == 1, "declared grapple spends one attack before target save");
    True(f.Encounter.Grapples.Count == 0, "grapple state is not applied before save");

    var result = f.Engine.ResolvePendingUnarmedGrappleSaveRoll(f.Campaign, pending.Id, 20, null, MinimumDice());
    Equal(20, result.SavingThrow.ChosenRoll, "supplied grapple save d20 is authoritative");
    True(!result.Grappled && f.Encounter.Grapples.Count == 0, "successful player save resists grapple");
    True(f.Campaign.PendingPlayerRoll is null, "grapple save clears pending roll");
});

Run("failed supplied grapple save creates persistent grapple state", () =>
{
    var f = CreateUnarmedFixture(playerTarget: true);
    var pending = f.Engine.RequestUnarmedGrappleSaveRoll(f.Campaign, f.Encounter.Id, f.AttackerCombatant.Id, f.TargetCombatant.Id, "strength");
    var result = f.Engine.ResolvePendingUnarmedGrappleSaveRoll(f.Campaign, pending.Id, 1, null, MinimumDice());
    True(result.Grappled, "low supplied save fails grapple");
    Equal(1, f.Encounter.Grapples.Count, "grapple state persisted");
    True(f.Target.Conditions.Contains("Grappled", StringComparer.OrdinalIgnoreCase), "Grappled condition applied");
    Equal(0, f.TargetCombatant.MovementRemainingFeet, "grapple reduces current movement to zero");
});

Run("Restrained player Dexterity grapple save requires two supplied d20s", () =>
{
    var f = CreateUnarmedFixture(playerTarget: true);
    f.Target.Conditions.Add("Restrained");
    var pending = f.Engine.RequestUnarmedGrappleSaveRoll(f.Campaign, f.Encounter.Id, f.AttackerCombatant.Id, f.TargetCombatant.Id, "dexterity");
    Equal("disadvantage", pending.RollMode, "Restrained Dexterity save mode");

    var rejected = false;
    try { f.Engine.ResolvePendingUnarmedGrappleSaveRoll(f.Campaign, pending.Id, 18, null, MinimumDice()); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "one d20 cannot satisfy grapple Disadvantage");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "invalid save remains pending");

    var result = f.Engine.ResolvePendingUnarmedGrappleSaveRoll(f.Campaign, pending.Id, 18, 4, MinimumDice());
    Equal(4, result.SavingThrow.ChosenRoll, "lower supplied d20 is authoritative for Disadvantage");
});

Run("shove against a player pauses and applies Prone only after failed supplied save", () =>
{
    var f = CreateUnarmedFixture(playerTarget: true);
    var pending = f.Engine.RequestUnarmedShoveSaveRoll(f.Campaign, f.Encounter.Id, f.AttackerCombatant.Id, f.TargetCombatant.Id, "prone", "strength");
    Equal("unarmed_shove_save", pending.ResolutionKey, "shove pending key");
    True(!f.Target.Conditions.Contains("Prone", StringComparer.OrdinalIgnoreCase), "Prone not applied while save pending");

    var result = f.Engine.ResolvePendingUnarmedShoveSaveRoll(f.Campaign, pending.Id, 1, null, MinimumDice());
    True(result.Succeeded && f.Target.Conditions.Contains("Prone", StringComparer.OrdinalIgnoreCase), "failed player save applies Prone");
});

Run("player escape grapple waits for supplied check before spending action", () =>
{
    var f = CreateEscapeFixture();
    var pending = f.Engine.RequestEscapeGrappleRoll(f.Campaign, f.Encounter.Id, f.TargetCombatant.Id, f.AttackerCombatant.Id, "athletics");
    Equal("escape_grapple_check", pending.ResolutionKey, "escape pending key");
    True(f.TargetCombatant.ActionAvailable, "escape action remains available while check pending");
    Equal(1, f.Encounter.Grapples.Count, "grapple remains while escape roll pending");

    var result = f.Engine.ResolvePendingEscapeGrappleRoll(f.Campaign, pending.Id, 20);
    Equal(20, result.AbilityCheck.ChosenRoll, "supplied escape d20 is authoritative");
    True(result.Escaped && f.Encounter.Grapples.Count == 0, "successful escape removes grapple");
    True(!f.TargetCombatant.ActionAvailable, "escape spends action after valid resolution");
    True(!f.Target.Conditions.Contains("Grappled", StringComparer.OrdinalIgnoreCase), "Grappled condition removed");
});

Run("failed player escape spends action and leaves grapple active", () =>
{
    var f = CreateEscapeFixture();
    f.Target.Abilities["strength"] = 1;
    f.Target.SkillProficiencies.Clear();
    var pending = f.Engine.RequestEscapeGrappleRoll(f.Campaign, f.Encounter.Id, f.TargetCombatant.Id, f.AttackerCombatant.Id, "athletics");
    var result = f.Engine.ResolvePendingEscapeGrappleRoll(f.Campaign, pending.Id, 1);
    True(!result.Escaped && f.Encounter.Grapples.Count == 1, "failed escape leaves grapple active");
    True(!f.TargetCombatant.ActionAvailable, "failed but valid escape spends action");
});

Run("NPC grapple target keeps automatic deterministic save", () =>
{
    var f = CreateUnarmedFixture(playerTarget: false);
    var result = f.Engine.ResolveUnarmedGrapple(f.Campaign, f.Encounter.Id, f.AttackerCombatant.Id, f.TargetCombatant.Id, MinimumDice(), "strength");
    True(result.Grappled, "NPC low deterministic save fails grapple");
    True(f.Campaign.PendingPlayerRoll is null, "NPC grapple save creates no player pending roll");
});

Run("pending grapple save survives campaign JSON serialization", () =>
{
    var f = CreateUnarmedFixture(playerTarget: true);
    var pending = f.Engine.RequestUnarmedGrappleSaveRoll(f.Campaign, f.Encounter.Id, f.AttackerCombatant.Id, f.TargetCombatant.Id, "strength");
    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredPending = restored.PendingPlayerRoll ?? throw new Exception("pending grapple save missing after restore");
    Equal(pending.Id, restoredPending.Id, "pending grapple id survives restore");
    var result = f.Engine.ResolvePendingUnarmedGrappleSaveRoll(restored, restoredPending.Id, 20, null, MinimumDice());
    True(!result.Grappled, "restored pending grapple save resolves from supplied d20");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Unarmed player roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Unarmed player roll tests passed: {passed}");

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

static Fixture CreateUnarmedFixture(bool playerTarget)
{
    var engine = new GameEngine();
    var attacker = NewCharacter("attacker", "Grappler", "monster");
    attacker.Size = "Medium";
    attacker.FreeHands = 1;
    attacker.AttacksPerAction = 1;
    attacker.ProficiencyBonus = 2;
    attacker.Abilities["strength"] = 16;

    var target = NewCharacter("target", playerTarget ? "Aric" : "Training Target", playerTarget ? "pc" : "monster");
    target.Size = "Medium";
    target.Abilities["strength"] = 10;
    target.Abilities["dexterity"] = 10;
    if (!playerTarget) target.ExhaustionLevel = 5;

    var campaign = new CampaignState { Id = "unarmed-campaign", Name = "Unarmed Campaign", Characters = [attacker, target] };
    var encounter = engine.StartEncounter(campaign, "Unarmed Encounter");
    var attackerCombatant = engine.AddCombatant(campaign, encounter.Id, attacker.Id, side: "opposition");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "party");
    engine.SetCombatantPosition(campaign, encounter.Id, attackerCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 1);
    engine.SetInitiative(campaign, encounter.Id, attackerCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, attacker, target, encounter, attackerCombatant, targetCombatant);
}

static Fixture CreateEscapeFixture()
{
    var engine = new GameEngine();
    var grappler = NewCharacter("grappler", "Grappler", "monster");
    grappler.Size = "Medium";
    var target = NewCharacter("escapee", "Aric", "pc");
    target.Size = "Medium";
    target.Abilities["strength"] = 18;
    target.SkillProficiencies.Add("athletics");
    var campaign = new CampaignState { Id = "escape-campaign", Name = "Escape Campaign", Characters = [grappler, target] };
    var encounter = engine.StartEncounter(campaign, "Escape Encounter");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "party");
    var grapplerCombatant = engine.AddCombatant(campaign, encounter.Id, grappler.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, grapplerCombatant.Id, 0, 1);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, grapplerCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    encounter.Grapples.Add(new GrappleState
    {
        GrapplerCombatantId = grapplerCombatant.Id,
        TargetCombatantId = targetCombatant.Id,
        EscapeDc = 13,
        ReachFeet = 5
    });
    target.Conditions.Add("Grappled");
    return new Fixture(engine, campaign, grappler, target, encounter, grapplerCombatant, targetCombatant);
}

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    MaxHp = 40,
    CurrentHp = 40,
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
    CharacterSheet Attacker,
    CharacterSheet Target,
    EncounterState Encounter,
    CombatantState AttackerCombatant,
    CombatantState TargetCombatant);
