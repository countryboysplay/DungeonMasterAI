using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("PC readied multi-buff freezes target list behind the Reaction decision and applies every target on accept", () =>
{
    var f = CreateFixture("pc");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[1].Remaining;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the allies enter the ward", 1);
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "Ready spends the slot immediately");
    Equal($"Readied spell: {f.Spell.Name}", f.Caster.ConcentrationEffect, "Ready holds spell energy with Concentration");
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var targets = f.AllyCombatants.Take(3).Select(c => c.Id).ToArray();
    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, targetCombatantIds: targets);
    True(f.CasterCombatant.ReactionAvailable, "requesting the decision does not spend Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "requesting the decision does not consume Ready");
    True(f.AllyCharacters.Take(3).All(a => f.Campaign.ActiveEffects.All(e => !e.TargetCharacterId.Equals(a.Id, StringComparison.OrdinalIgnoreCase))), "buff is not applied before player accepts");
    True(f.AllyCharacters.Take(3).All(a => decision.Prompt.Contains(a.Name, StringComparison.OrdinalIgnoreCase)), "decision prompt shows every proposed target");

    var resolution = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    True(resolution.FollowUpRoll is null, "multi-buff release needs no player dice");
    True(!f.CasterCombatant.ReactionAvailable, "accepting spends Reaction");
    True(f.CasterCombatant.ReadiedAction is null, "accepting consumes the readied spell");
    Equal(f.Spell.Name, f.Caster.ConcentrationEffect, "held concentration becomes live spell concentration");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "release never spends the slot twice");
    foreach (var ally in f.AllyCharacters.Take(3))
        True(f.Campaign.ActiveEffects.Any(e => e.TargetCharacterId.Equals(ally.Id, StringComparison.OrdinalIgnoreCase) && e.SourceSpellId.Equals(f.Spell.Id, StringComparison.OrdinalIgnoreCase)), $"{ally.Name} received the buff");
});

Run("declining a readied multi-buff trigger preserves Reaction held spell slot and targets remain untouched", () =>
{
    var f = CreateFixture("pc");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[1].Remaining;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the allies gather", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var targets = f.AllyCombatants.Take(3).Select(c => c.Id).ToArray();
    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, targetCombatantIds: targets);
    f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline_trigger", dice);

    True(f.CasterCombatant.ReactionAvailable, "decline preserves Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "decline preserves Ready");
    Equal($"Readied spell: {f.Spell.Name}", f.Caster.ConcentrationEffect, "decline keeps held concentration");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "decline neither refunds nor spends another slot");
    True(f.Campaign.ActiveEffects.All(e => !e.SourceSpellId.Equals(f.Spell.Id, StringComparison.OrdinalIgnoreCase)), "decline applies no buff effects");
});

Run("readied multi-buff validates target count before a PC Reaction can be spent", () =>
{
    var f = CreateFixture("pc");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the squad forms up", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var fourTargets = f.AllyCombatants.Select(c => c.Id).ToArray();
    var rejected = false;
    try { f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, targetCombatantIds: fourTargets); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "level 1 spell rejects four release targets");
    True(f.CasterCombatant.ReactionAvailable, "invalid target count spends no Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "invalid target count preserves Ready");
    True(f.Campaign.PendingPlayerDecision is null, "invalid target count creates no player decision");
});

Run("upcast readied multi-buff accepts the expanded target count", () =>
{
    var f = CreateFixture("pc");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[2].Remaining;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the squad forms up", 2);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var fourTargets = f.AllyCombatants.Select(c => c.Id).ToArray();
    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, targetCombatantIds: fourTargets);
    f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);

    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "upcast slot spent once when readied");
    foreach (var ally in f.AllyCharacters)
        True(f.Campaign.ActiveEffects.Any(e => e.TargetCharacterId.Equals(ally.Id, StringComparison.OrdinalIgnoreCase) && e.SourceSpellId.Equals(f.Spell.Id, StringComparison.OrdinalIgnoreCase)), $"upcast includes {ally.Name}");
});

Run("out-of-range multi-buff proposal is rejected before PC Reaction spend", () =>
{
    var f = CreateFixture("pc");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the ally signals", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    f.Engine.SetCombatantPosition(f.Campaign, f.Encounter.Id, f.AllyCombatants[0].Id, 20, 20);
    var rejected = false;
    try { f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, targetCombatantIds: [f.AllyCombatants[0].Id]); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "out-of-range target is rejected");
    True(f.CasterCombatant.ReactionAvailable, "range failure spends no Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "range failure preserves held Ready");
});

Run("NPC readied multi-buff releases automatically without a player decision", () =>
{
    var f = CreateFixture("monster");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the squad forms up", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var targets = f.AllyCombatants.Take(3).Select(c => c.Id).ToArray();
    var result = f.Engine.TriggerReadiedSpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, dice, targetCombatantIds: targets);

    True(f.Campaign.PendingPlayerDecision is null && f.Campaign.PendingPlayerRoll is null, "NPC release is automatic");
    True(!f.CasterCombatant.ReactionAvailable, "NPC release spends Reaction");
    Equal(3, result.TargetResults?.Count ?? 0, "NPC release resolves three configured targets");
    foreach (var ally in f.AllyCharacters.Take(3))
        True(f.Campaign.ActiveEffects.Any(e => e.TargetCharacterId.Equals(ally.Id, StringComparison.OrdinalIgnoreCase) && e.SourceSpellId.Equals(f.Spell.Id, StringComparison.OrdinalIgnoreCase)), $"NPC buff reaches {ally.Name}");
});

Run("DM trigger tool passes multi-target release proposal into the PC Reaction decision", () =>
{
    var f = CreateFixture("pc");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the allies gather", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var router = new DmToolRouter(f.Engine, dice, new RulesSearchService());
    var ids = f.AllyCombatants.Take(3).Select(c => $"\"{c.Id}\"");
    var json = $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.CasterCombatant.Id}\",\"target_combatant_ids\":[{string.Join(',', ids)}]}}";
    var tool = router.Execute(f.Campaign, "trigger_readied_spell", json);

    True(tool.Ok, $"DM tool failed: {tool.Error}");
    var decision = tool.Result as PendingPlayerDecision ?? throw new Exception("DM tool did not return PC Reaction decision");
    True(f.AllyCharacters.Take(3).All(a => decision.Prompt.Contains(a.Name, StringComparison.OrdinalIgnoreCase)), "tool-fed decision names every target");
    True(f.CasterCombatant.ReactionAvailable, "DM tool cannot spend PC Reaction");
    True(f.Campaign.ActiveEffects.All(e => !e.SourceSpellId.Equals(f.Spell.Id, StringComparison.OrdinalIgnoreCase)), "DM tool cannot apply PC buff before acceptance");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied multi-buff tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied multi-buff tests passed: {passed}");

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

static Fixture CreateFixture(string casterType)
{
    var engine = new GameEngine();
    var spell = CreateMultiBuffSpell();
    var caster = NewCharacter("caster", casterType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "Aric" : "Ashen Priest", casterType);
    caster.SpellcastingAbility = "wisdom";
    caster.Abilities["wisdom"] = 16;
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
    caster.SpellSlots[2] = new SpellSlotPool { Maximum = 2, Remaining = 2 };
    caster.PreparedSpellIds.Add(spell.Id);

    var allies = new[]
    {
        NewCharacter("ally-1", "Mira", "npc"),
        NewCharacter("ally-2", "Borin", "npc"),
        NewCharacter("ally-3", "Selene", "npc"),
        NewCharacter("ally-4", "Thorn", "npc")
    };
    var campaign = new CampaignState
    {
        Id = "readied-multi-buff-campaign",
        Name = "Readied Multi Buff Campaign",
        Characters = [caster, .. allies],
        Spells = [spell]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Multi Buff Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var allyCombatants = allies.Select(a => engine.AddCombatant(campaign, encounter.Id, a.Id, side: "party")).ToArray();
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, allyCombatants[0].Id, 0, 2);
    engine.SetCombatantPosition(campaign, encounter.Id, allyCombatants[1].Id, 1, 2);
    engine.SetCombatantPosition(campaign, encounter.Id, allyCombatants[2].Id, -1, 2);
    engine.SetCombatantPosition(campaign, encounter.Id, allyCombatants[3].Id, 2, 2);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 20);
    for (var i = 0; i < allyCombatants.Length; i++) engine.SetInitiative(campaign, encounter.Id, allyCombatants[i].Id, 10 - i);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, caster, allies, spell, encounter, casterCombatant, allyCombatants);
}

static SpellDefinition CreateMultiBuffSpell() => new()
{
    Id = "readied-test-blessing",
    Key = "readied_test_blessing",
    Name = "Test Blessing",
    Level = 1,
    CastingTime = "Action",
    RangeKind = "distance",
    RangeFeet = 30,
    RequiresTarget = true,
    RequiresConcentration = true,
    Resolution = "multi_buff",
    BaseTargets = 3,
    ExtraTargetsPerSlot = 1,
    AttackRollBonusExpression = "1d4",
    SavingThrowBonusExpression = "1d4"
};

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
        ["constitution"] = 10,
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
    CharacterSheet Caster,
    IReadOnlyList<CharacterSheet> AllyCharacters,
    SpellDefinition Spell,
    EncounterState Encounter,
    CombatantState CasterCombatant,
    IReadOnlyList<CombatantState> AllyCombatants);
