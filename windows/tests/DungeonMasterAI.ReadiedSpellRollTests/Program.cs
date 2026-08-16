using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("PC readied attack spell spends its slot once and waits for the player attack and damage rolls", () =>
{
    var f = CreateFixture("pc", "monster", CreateAttackSpell());
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[1].Remaining;
    var hpBefore = f.Target.CurrentHp;

    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the foe advances", 1);
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "Ready spends the spell slot immediately");
    Equal($"Readied spell: {f.Spell.Name}", f.Caster.ConcentrationEffect, "Ready holds the spell with Concentration");
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var attackPending = accepted.FollowUpRoll ?? throw new Exception("readied spell attack roll was not requested");
    Equal("spell_attack", attackPending.ResolutionKey, "readied spell attack key");
    Equal("true", attackPending.Context["readied_reaction"], "readied attack is marked as off-turn Reaction work");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "release does not spend the slot a second time");
    True(!f.CasterCombatant.ReactionAvailable, "accepting the trigger spends the Reaction");
    True(f.CasterCombatant.ReadiedAction is null, "accepted trigger consumes the readied spell");
    Equal(hpBefore, f.Target.CurrentHp, "target HP waits for player attack and damage rolls");

    var attack = f.Engine.ResolvePendingSpellAttackRoll(f.Campaign, attackPending.Id, 15, null, dice);
    True(attack.SpellAttack is { Hit: true, D20: 15 }, "supplied off-turn spell attack d20 is authoritative");
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("readied spell damage roll was not requested");
    Equal("spell_attack_damage", damagePending.ResolutionKey, "readied spell damage key");
    Equal("true", damagePending.Context["readied_reaction"], "readied marker propagates to damage");

    f.Engine.ResolvePendingSpellAttackDamageRoll(f.Campaign, damagePending.Id, 9, dice);
    Equal(hpBefore - 9, f.Target.CurrentHp, "exact supplied readied spell damage is authoritative");
    True(f.Campaign.PendingPlayerRoll is null, "readied attack-spell pipeline finishes cleanly");
    True(string.IsNullOrWhiteSpace(f.Caster.ConcentrationEffect), "non-Concentration spell is no longer held after release");
});

Run("declining a readied spell trigger preserves the Reaction, held spell, and already-spent slot", () =>
{
    var f = CreateFixture("pc", "monster", CreateAttackSpell());
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[1].Remaining;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the foe advances", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "decline_trigger", dice);

    True(f.CasterCombatant.ReactionAvailable, "decline keeps Reaction available");
    True(f.CasterCombatant.ReadiedAction is not null, "decline keeps the readied spell");
    Equal($"Readied spell: {f.Spell.Name}", f.Caster.ConcentrationEffect, "decline keeps holding Concentration");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "decline does not refund or spend another slot");
    True(f.Campaign.PendingPlayerDecision is null && f.Campaign.PendingPlayerRoll is null, "decline clears only the trigger decision");
});

Run("DM trigger tool cannot choose or roll a PC readied spell Reaction", () =>
{
    var f = CreateFixture("pc", "monster", CreateAttackSpell());
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the foe advances", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var router = new DmToolRouter(f.Engine, dice, new RulesSearchService());

    var tool = router.Execute(
        f.Campaign,
        "trigger_readied_spell",
        $"{{\"encounter_id\":\"{f.Encounter.Id}\",\"combatant_id\":\"{f.CasterCombatant.Id}\",\"target_combatant_id\":\"{f.TargetCombatant.Id}\"}}");

    True(tool.Ok, $"DM trigger tool failed: {tool.Error}");
    var decision = tool.Result as PendingPlayerDecision ?? throw new Exception("DM trigger did not return a player decision");
    Equal("readied_spell_reaction", decision.DecisionType, "DM tool routes PC release to a player choice");
    True(f.CasterCombatant.ReactionAvailable, "DM tool does not spend the PC Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "DM tool does not release the PC spell");
    True(f.Campaign.PendingPlayerRoll is null, "DM tool does not roll the PC spell attack");
});

Run("PC readied save spell rolls the NPC save automatically but leaves damage to the PC caster", () =>
{
    var f = CreateFixture("pc", "monster", CreateSaveSpell());
    var dice = MinimumDice();
    var hpBefore = f.Target.CurrentHp;
    var slotsBefore = f.Caster.SpellSlots[1].Remaining;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the foe enters the ward", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var damagePending = accepted.FollowUpRoll ?? throw new Exception("PC caster did not receive readied save-spell damage roll");
    Equal("spell_save_damage", damagePending.ResolutionKey, "readied save-spell damage key");
    Equal(f.Caster.Id, damagePending.ActorCharacterId, "PC caster owns damage dice after NPC save");
    Equal("true", damagePending.Context["readied_reaction"], "save-spell damage is marked for off-turn resolution");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "readied save spell releases without another slot expenditure");
    Equal(hpBefore, f.Target.CurrentHp, "NPC target damage waits for PC damage roll");

    f.Engine.ResolvePendingSpellSaveDamageRoll(f.Campaign, damagePending.Id, 8, dice);
    Equal(hpBefore - 8, f.Target.CurrentHp, "exact PC readied save-spell damage is applied");
    True(f.Campaign.PendingPlayerRoll is null, "readied save-spell damage pipeline completes");
});

Run("NPC readied save spell asks the targeted PC for the saving throw off turn", () =>
{
    var f = CreateFixture("monster", "pc", CreateSaveSpell());
    var dice = MaximumDice();
    var hpBefore = f.Target.CurrentHp;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the hero steps forward", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var result = f.Engine.TriggerReadiedSpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, dice, f.TargetCombatant.Id);
    True(result.SavingThrow is null, "NPC readied spell waits for the PC save instead of auto-rolling it");
    var savePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("PC saving throw request missing");
    Equal("spell_saving_throw", savePending.ResolutionKey, "readied spell saving throw key");
    Equal(f.Target.Id, savePending.ActorCharacterId, "target PC owns the readied spell save");
    Equal("true", savePending.Context["readied_reaction"], "readied spell save is allowed off caster turn");
    True(!f.CasterCombatant.ReactionAvailable, "NPC release spends its Reaction");

    var resolved = f.Engine.ResolvePendingSpellSavingThrowRoll(f.Campaign, savePending.Id, 20, null, dice);
    True(resolved.SavingThrow is { Success: true, ChosenRoll: 20 }, "supplied PC save d20 is authoritative");
    Equal(hpBefore, f.Target.CurrentHp, "successful configured save takes no damage");
    True(f.Campaign.PendingPlayerRoll is null, "successful readied spell save completes cleanly");
});

Run("PC versus PC readied save spell hands off target save then caster damage", () =>
{
    var f = CreateFixture("pc", "pc", CreateSaveSpell());
    var dice = MinimumDice();
    var hpBefore = f.Target.CurrentHp;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the rival crosses the rune", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var savePending = accepted.FollowUpRoll ?? throw new Exception("target PC save request missing");
    Equal("spell_saving_throw", savePending.ResolutionKey, "first handoff is target PC save");
    Equal(f.Target.Id, savePending.ActorCharacterId, "target owns first handoff");

    f.Engine.ResolvePendingSpellSavingThrowRoll(f.Campaign, savePending.Id, 2, null, dice);
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("caster PC damage request missing after failed target save");
    Equal("spell_save_damage", damagePending.ResolutionKey, "second handoff is caster damage");
    Equal(f.Caster.Id, damagePending.ActorCharacterId, "caster owns second handoff");
    Equal("true", damagePending.Context["readied_reaction"], "second handoff stays readied/off-turn");

    f.Engine.ResolvePendingSpellSaveDamageRoll(f.Campaign, damagePending.Id, 6, dice);
    Equal(hpBefore - 6, f.Target.CurrentHp, "caster's supplied damage resolves after target's supplied save");
    True(f.Campaign.PendingPlayerRoll is null, "PC-to-PC readied save spell finishes cleanly");
});

Run("natural miss on PC readied attack spell ends without a damage request", () =>
{
    var f = CreateFixture("pc", "monster", CreateAttackSpell());
    var dice = MinimumDice();
    var hpBefore = f.Target.CurrentHp;
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the foe advances", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var pending = accepted.FollowUpRoll ?? throw new Exception("attack pending missing");

    var result = f.Engine.ResolvePendingSpellAttackRoll(f.Campaign, pending.Id, 1, null, dice);
    True(result.SpellAttack is { Hit: false, D20: 1 }, "natural 1 is an authoritative miss");
    Equal(hpBefore, f.Target.CurrentHp, "miss causes no damage");
    True(f.Campaign.PendingPlayerRoll is null, "miss does not request damage");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied spell player-control tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied spell player-control tests passed: {passed}");

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

static Fixture CreateFixture(string casterType, string targetType, SpellDefinition spell)
{
    var engine = new GameEngine();
    var caster = NewCharacter("caster", casterType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "Aric" : "Ashen Mage", casterType);
    caster.SpellcastingAbility = "intelligence";
    caster.Abilities["intelligence"] = 16;
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
    caster.PreparedSpellIds.Add(spell.Id);

    var target = NewCharacter("target", targetType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "Mira" : "Ashen Watcher", targetType);
    target.ArmorClass = 12;
    target.Abilities["dexterity"] = 10;

    var campaign = new CampaignState
    {
        Id = "readied-spell-campaign",
        Name = "Readied Spell Campaign",
        Characters = [caster, target],
        Spells = [spell]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Spell Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 2);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, caster, target, spell, encounter, casterCombatant, targetCombatant);
}

static SpellDefinition CreateAttackSpell() => new()
{
    Id = "readied-arc-bolt",
    Key = "readied_arc_bolt",
    Name = "Arc Bolt",
    Level = 1,
    CastingTime = "Action",
    RangeKind = "distance",
    RangeFeet = 120,
    RequiresTarget = true,
    Resolution = "attack",
    DamageExpression = "2d6",
    DamageType = "Force"
};

static SpellDefinition CreateSaveSpell() => new()
{
    Id = "readied-rune-burst",
    Key = "readied_rune_burst",
    Name = "Rune Burst",
    Level = 1,
    CastingTime = "Action",
    RangeKind = "distance",
    RangeFeet = 120,
    RequiresTarget = true,
    Resolution = "save",
    SaveAbility = "dexterity",
    DamageExpression = "2d6",
    DamageType = "Force",
    HalfDamageOnSuccessfulSave = false
};

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    Level = 3,
    MaxHp = 40,
    CurrentHp = 40,
    ArmorClass = 14,
    Speed = 30,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 10,
        ["dexterity"] = 10,
        ["constitution"] = 12,
        ["intelligence"] = 10,
        ["wisdom"] = 10,
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
    CharacterSheet Caster,
    CharacterSheet Target,
    SpellDefinition Spell,
    EncounterState Encounter,
    CombatantState CasterCombatant,
    CombatantState TargetCombatant);
