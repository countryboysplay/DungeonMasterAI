using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player target supplies the authoritative spell saving throw d20", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(targetIsPlayer: true);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;

    var cast = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    True(cast.SavingThrow is null, "spell should wait for the player save");
    Equal(beforeHp, target.CurrentHp, "HP must not change before the player save");
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("spell saving throw pending missing");
    Equal("spell_saving_throw", pending.ResolutionKey, "spell save key");
    Equal(target.Id, pending.ActorCharacterId, "target owns the saving throw");
    Equal(13, pending.TargetNumber, "spell save DC");

    var result = engine.ResolvePendingSpellSavingThrowRoll(campaign, pending.Id, 5, null, dice);
    True(result.SavingThrow is { ChosenRoll: 5, Success: false }, "supplied d20 should be authoritative");
    Equal(beforeHp - 2, target.CurrentHp, "failed save should apply deterministic spell damage after the player roll");
    True(campaign.PendingPlayerRoll is null, "spell save should finish without a pending roll when no follow-up is required");
});

Run("successful supplied save applies half damage", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(targetIsPlayer: true);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;
    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("spell saving throw pending missing");

    var result = engine.ResolvePendingSpellSavingThrowRoll(campaign, pending.Id, 20, null, dice);
    True(result.SavingThrow is { ChosenRoll: 20, Success: true }, "supplied high d20 should succeed");
    Equal(beforeHp - 1, target.CurrentHp, "successful save should take half of deterministic 2 damage");
});

Run("Restrained player Dexterity save requires two supplied d20 results", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(targetIsPlayer: true);
    target.Conditions.Add("Restrained");
    var dice = new DiceService((min, max) => min);
    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("spell saving throw pending missing");
    Equal("disadvantage", pending.RollMode, "Restrained Dexterity save mode");

    var rejected = false;
    try { engine.ResolvePendingSpellSavingThrowRoll(campaign, pending.Id, 18, null, dice); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "Disadvantage save should reject one supplied d20");
    Equal(pending.Id, campaign.PendingPlayerRoll?.Id, "failed resolution should preserve pending save");

    var result = engine.ResolvePendingSpellSavingThrowRoll(campaign, pending.Id, 18, 4, dice);
    Equal(4, result.SavingThrow?.ChosenRoll, "lower supplied d20 should be used for Disadvantage");
});

Run("spell damage hands off to a concentrating player target", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(targetIsPlayer: true);
    engine.BeginConcentration(campaign, target.Id, "Bless");
    var dice = new DiceService((min, max) => min);
    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var savePending = campaign.PendingPlayerRoll ?? throw new Exception("spell saving throw pending missing");

    engine.ResolvePendingSpellSavingThrowRoll(campaign, savePending.Id, 5, null, dice);
    var concentrationPending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing after spell damage");
    Equal("concentration_check", concentrationPending.ResolutionKey, "Concentration handoff key");
    Equal(target.Id, concentrationPending.ActorCharacterId, "damaged target owns Concentration save");
    Equal("Bless", target.ConcentrationEffect, "Concentration waits for the target player's d20");
});

Run("NPC spell targets keep automatic saving throw resolution", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(targetIsPlayer: false);
    var dice = new DiceService((min, max) => min);
    var result = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    True(result.SavingThrow is not null, "NPC target save should resolve immediately");
    True(campaign.PendingPlayerRoll is null, "NPC target save must not create a player roll");
});

Run("automatic-failure condition does not request a meaningless player d20", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(targetIsPlayer: true);
    target.Conditions.Add("Paralyzed");
    var dice = new DiceService((min, max) => min);
    var result = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    True(result.SavingThrow is { Success: false }, "Paralyzed Dexterity save should fail automatically");
    True(campaign.PendingPlayerRoll is null, "automatic failure should not request a player d20");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Spell saving throw roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Spell saving throw roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Caster, CharacterSheet Target, SpellDefinition Spell) CreateFixture(bool targetIsPlayer)
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = "npc-caster",
        Name = "Watcher Mage",
        CharacterType = "npc",
        Level = 3,
        MaxHp = 24,
        CurrentHp = 24,
        ProficiencyBonus = 2,
        SpellcastingAbility = "intelligence",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 16 }
    };
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 4, Remaining = 4 };
    var target = new CharacterSheet
    {
        Id = targetIsPlayer ? "pc-target" : "npc-target",
        Name = targetIsPlayer ? "Aric" : "Ashen Watcher",
        CharacterType = targetIsPlayer ? "pc" : "npc",
        ArmorClass = 13,
        MaxHp = 30,
        CurrentHp = 30,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 14, ["constitution"] = 14 }
    };
    var spell = new SpellDefinition
    {
        Id = "spell-test-save",
        Key = "test_save",
        Name = "Test Flame",
        Level = 1,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 120,
        RequiresTarget = true,
        Resolution = "save",
        SaveAbility = "dexterity",
        DamageExpression = "2d6",
        DamageType = "Fire",
        HalfDamageOnSuccessfulSave = true
    };
    caster.PreparedSpellIds.Add(spell.Id);
    var campaign = new CampaignState
    {
        Id = "campaign-spell-saves",
        Name = "Spell Save Campaign",
        Characters = [caster, target],
        Spells = [spell]
    };
    return (engine, campaign, caster, target, spell);
}

static void True(bool value, string label)
{
    if (!value) throw new Exception(label);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}
