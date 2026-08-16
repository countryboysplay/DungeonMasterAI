using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("PC readied area spell freezes proposed geometry then hands PC saves and caster damage to players off turn", () =>
{
    var f = CreateFixture(casterType: "pc", firstTargetType: "pc");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[3].Remaining;
    var pcHpBefore = f.FirstTarget.CurrentHp;
    var npcHpBefore = f.SecondTarget.CurrentHp;

    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the pair enter the blast zone", 3);
    Equal(slotsBefore - 1, f.Caster.SpellSlots[3].Remaining, "Ready spends slot immediately");
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(
        f.Campaign,
        f.Encounter.Id,
        f.CasterCombatant.Id,
        null,
        3,
        0,
        "north");
    Equal("readied_spell_reaction", decision.DecisionType, "area release decision type");
    Equal("3", decision.Context["area_center_x"], "center X stored in player decision");
    Equal("0", decision.Context["area_center_y"], "center Y stored in player decision");
    True(decision.Prompt.Contains(f.FirstTarget.Name, StringComparison.OrdinalIgnoreCase), "decision names affected PC");
    True(decision.Prompt.Contains(f.SecondTarget.Name, StringComparison.OrdinalIgnoreCase), "decision names affected NPC");
    True(f.CasterCombatant.ReactionAvailable, "proposing area does not spend Reaction");

    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var savePending = accepted.FollowUpRoll ?? throw new Exception("affected PC save was not requested");
    Equal("area_spell_saving_throw", savePending.ResolutionKey, "first area handoff is PC save");
    Equal(f.FirstTarget.Id, savePending.ActorCharacterId, "affected PC owns area save");
    True(!f.CasterCombatant.ReactionAvailable, "accepting area release spends Reaction");
    True(f.CasterCombatant.ReadiedAction is null, "accepted area release consumes ready");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[3].Remaining, "release does not spend slot twice");

    f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, savePending.Id, 2, null, dice);
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("PC caster shared area damage was not requested");
    Equal("area_spell_damage", damagePending.ResolutionKey, "shared area damage key");
    Equal(f.Caster.Id, damagePending.ActorCharacterId, "PC caster owns shared damage roll");

    f.Engine.ResolvePendingAreaSpellDamageRoll(f.Campaign, damagePending.Id, 10, dice);
    Equal(pcHpBefore - 10, f.FirstTarget.CurrentHp, "failed PC save takes exact supplied shared damage");
    Equal(npcHpBefore - 10, f.SecondTarget.CurrentHp, "failed automatic NPC save takes same shared damage");
    True(f.Campaign.PendingPlayerRoll is null, "readied area sequence finishes cleanly");
});

Run("declining readied area trigger preserves Reaction held spell and spent slot", () =>
{
    var f = CreateFixture(casterType: "pc", firstTargetType: "monster");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[3].Remaining;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the pair enter the blast zone", 3);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, null, 3, 0, "north");
    f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline_trigger", dice);

    True(f.CasterCombatant.ReactionAvailable, "decline preserves Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "decline preserves readied area spell");
    Equal($"Readied spell: {f.Spell.Name}", f.Caster.ConcentrationEffect, "decline preserves held spell Concentration");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[3].Remaining, "decline neither refunds nor spends another slot");
    True(f.Campaign.PendingPlayerDecision is null && f.Campaign.PendingPlayerRoll is null, "decline clears only the trigger decision");
});

Run("DM area trigger cannot choose PC Reaction and exposes proposed geometry", () =>
{
    var f = CreateFixture(casterType: "pc", firstTargetType: "monster");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the pair enter the blast zone", 3);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var router = new DmToolRouter(f.Engine, dice, new RulesSearchService());

    var tool = router.Execute(
        f.Campaign,
        "trigger_readied_spell",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.CasterCombatant.Id}\",\"center_x\":3,\"center_y\":0,\"direction\":\"north\"}}");

    True(tool.Ok, $"DM readied area trigger failed: {tool.Error}");
    var decision = tool.Result as PendingPlayerDecision ?? throw new Exception("DM area trigger did not create player decision");
    Equal("3", decision.Context["area_center_x"], "DM proposal center X preserved");
    True(f.CasterCombatant.ReactionAvailable, "DM cannot spend PC Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "DM cannot release PC held area spell");
    True(f.Campaign.PendingPlayerRoll is null, "DM cannot roll PC area damage or target saves");
});

Run("invalid proposed area geometry is rejected before PC Reaction is spent", () =>
{
    var f = CreateFixture(casterType: "pc", firstTargetType: "monster");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the pair enter the blast zone", 3);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var rejected = false;
    try
    {
        f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, null, 30, 30, "north");
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }

    True(rejected, "out-of-range area center must be rejected");
    True(f.CasterCombatant.ReactionAvailable, "invalid geometry does not spend Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "invalid geometry preserves readied spell");
    True(f.Campaign.PendingPlayerDecision is null, "invalid geometry creates no player decision");
});

Run("NPC readied area spell automatically releases but affected PC owns its save", () =>
{
    var f = CreateFixture(casterType: "monster", firstTargetType: "pc");
    var dice = MinimumDice();
    var hpBefore = f.FirstTarget.CurrentHp;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the heroes bunch together", 3);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var router = new DmToolRouter(f.Engine, dice, new RulesSearchService());

    var tool = router.Execute(
        f.Campaign,
        "trigger_readied_spell",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.CasterCombatant.Id}\",\"center_x\":3,\"center_y\":0,\"direction\":\"north\"}}");
    True(tool.Ok, $"NPC area trigger failed: {tool.Error}");
    var savePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("affected PC save missing from NPC readied area spell");
    Equal("area_spell_saving_throw", savePending.ResolutionKey, "NPC area spell hands save to PC");
    Equal(f.FirstTarget.Id, savePending.ActorCharacterId, "affected PC owns save");
    True(!f.CasterCombatant.ReactionAvailable, "NPC area release spends its Reaction");

    f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, savePending.Id, 20, null, dice);
    Equal(hpBefore, f.FirstTarget.CurrentHp, "successful PC save avoids configured no-half damage");
    True(f.Campaign.PendingPlayerRoll is null, "NPC area spell completes after PC save");
});

Run("readied area sequence survives a target Concentration interruption and resumes off turn", () =>
{
    var f = CreateFixture(casterType: "pc", firstTargetType: "pc");
    var dice = MinimumDice();
    f.Engine.BeginConcentration(f.Campaign, f.FirstTarget.Id, "Bless");
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the pair enter the blast zone", 3);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, null, 3, 0, "north");
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var savePending = accepted.FollowUpRoll ?? throw new Exception("PC save missing");
    f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, savePending.Id, 2, null, dice);
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("area damage missing");
    f.Engine.ResolvePendingAreaSpellDamageRoll(f.Campaign, damagePending.Id, 8, dice);

    var concentration = f.Campaign.PendingPlayerRoll ?? throw new Exception("damaged PC Concentration save missing");
    Equal("concentration_check", concentration.ResolutionKey, "area damage hands off Concentration");
    Equal("area_spell_sequence", concentration.Context["continuation_resolution_key"], "area continuation key");
    f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentration.Id, 15, null, dice);
    True(f.Campaign.PendingPlayerRoll is null, "area sequence resumes and completes after Concentration");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied area spell tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied area spell tests passed: {passed}");

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

static Fixture CreateFixture(string casterType, string firstTargetType)
{
    var engine = new GameEngine();
    var caster = NewCharacter("area-caster", casterType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "Aric" : "Ashen Mage", casterType);
    caster.SpellcastingAbility = "intelligence";
    caster.Abilities["intelligence"] = 16;
    caster.SpellSlots[3] = new SpellSlotPool { Maximum = 2, Remaining = 2 };

    var first = NewCharacter("area-first", firstTargetType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "Mira" : "Raider", firstTargetType);
    var second = NewCharacter("area-second", "Watcher", "monster");
    var spell = new SpellDefinition
    {
        Id = "readied-burst",
        Key = "readied_burst",
        Name = "Readied Burst",
        Level = 3,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 60,
        RequiresTarget = false,
        Resolution = "area_save",
        SaveAbility = "dexterity",
        DamageExpression = "4d6",
        DamageType = "Fire",
        HalfDamageOnSuccessfulSave = false,
        AreaShape = "sphere",
        AreaSizeFeet = 10,
        AreaOrigin = "point"
    };
    caster.PreparedSpellIds.Add(spell.Id);

    var campaign = new CampaignState
    {
        Id = "readied-area-campaign",
        Name = "Readied Area Campaign",
        Characters = [caster, first, second],
        Spells = [spell]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Area Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var firstCombatant = engine.AddCombatant(campaign, encounter.Id, first.Id, side: "opposition");
    var secondCombatant = engine.AddCombatant(campaign, encounter.Id, second.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, firstCombatant.Id, 3, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, secondCombatant.Id, 3, 1);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, firstCombatant.Id, 10);
    engine.SetInitiative(campaign, encounter.Id, secondCombatant.Id, 5);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, caster, first, second, spell, encounter, casterCombatant, firstCombatant, secondCombatant);
}

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    Level = 5,
    MaxHp = 40,
    CurrentHp = 40,
    ArmorClass = 14,
    Speed = 30,
    ProficiencyBonus = 3,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 10,
        ["dexterity"] = 10,
        ["constitution"] = 14,
        ["intelligence"] = 10,
        ["wisdom"] = 10,
        ["charisma"] = 10
    },
    SavingThrowProficiencies = ["constitution"]
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
    CharacterSheet Caster,
    CharacterSheet FirstTarget,
    CharacterSheet SecondTarget,
    SpellDefinition Spell,
    EncounterState Encounter,
    CombatantState CasterCombatant,
    CombatantState FirstTargetCombatant,
    CombatantState SecondTargetCombatant);
