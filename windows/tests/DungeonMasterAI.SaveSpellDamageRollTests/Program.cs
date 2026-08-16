using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player caster owns damage after an automatic NPC saving throw", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(casterIsPlayer: true, targetIsPlayer: false, halfOnSuccess: true);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;
    var beforeSlots = caster.SpellSlots[1].Remaining;

    var cast = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    Equal(beforeSlots - 1, caster.SpellSlots[1].Remaining, "slot committed at cast start");
    True(cast.TargetSavingThrow is { Success: false }, "NPC save should resolve before player damage");
    Equal(beforeHp, target.CurrentHp, "HP must wait for player damage");
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("save-spell damage pending missing");
    Equal("spell_save_damage", pending.ResolutionKey, "damage resolution key");
    Equal(caster.Id, pending.ActorCharacterId, "caster owns the damage roll");

    var result = engine.ResolvePendingSpellSaveDamageRoll(campaign, pending.Id, 7, dice);
    Equal(beforeHp - 7, target.CurrentHp, "supplied damage is authoritative");
    True(result.TargetSavingThrow is { Success: false }, "save result retained");
    True(campaign.PendingPlayerRoll is null, "pipeline finishes when no follow-up roll is required");
});

Run("player target save hands control to the player caster damage roll", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(casterIsPlayer: true, targetIsPlayer: true, halfOnSuccess: true);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;

    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var savePending = campaign.PendingPlayerRoll ?? throw new Exception("target save pending missing");
    Equal("spell_saving_throw", savePending.ResolutionKey, "first pending roll is target save");
    Equal(target.Id, savePending.ActorCharacterId, "target owns save d20");

    var saved = engine.ResolvePendingSpellSavingThrowRoll(campaign, savePending.Id, 5, null, dice);
    True(saved.TargetSavingThrow is { ChosenRoll: 5, Success: false }, "supplied target d20 retained");
    Equal(beforeHp, target.CurrentHp, "damage waits after target save");
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("caster damage pending missing");
    Equal("spell_save_damage", damagePending.ResolutionKey, "second pending roll is caster damage");
    Equal(caster.Id, damagePending.ActorCharacterId, "caster owns damage dice");

    engine.ResolvePendingSpellSaveDamageRoll(campaign, damagePending.Id, 9, dice);
    Equal(beforeHp - 9, target.CurrentHp, "supplied caster damage applied");
    True(campaign.PendingPlayerRoll is null, "two-player roll chain completed");
});

Run("successful save with half damage still asks caster for full damage roll", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(casterIsPlayer: true, targetIsPlayer: true, halfOnSuccess: true);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;

    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var savePending = campaign.PendingPlayerRoll ?? throw new Exception("target save pending missing");
    engine.ResolvePendingSpellSavingThrowRoll(campaign, savePending.Id, 20, null, dice);
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("half-damage roll pending missing");
    Equal("spell_save_damage", damagePending.ResolutionKey, "half-damage spell still requests damage");

    var result = engine.ResolvePendingSpellSaveDamageRoll(campaign, damagePending.Id, 9, dice);
    True(result.TargetSavingThrow is { Success: true }, "successful save retained");
    Equal(beforeHp - 4, target.CurrentHp, "engine halves the supplied full damage roll using integer division");
});

Run("successful save with no half damage creates no damage roll", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(casterIsPlayer: true, targetIsPlayer: true, halfOnSuccess: false);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;

    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var savePending = campaign.PendingPlayerRoll ?? throw new Exception("target save pending missing");
    var result = engine.ResolvePendingSpellSavingThrowRoll(campaign, savePending.Id, 20, null, dice);
    True(result.TargetSavingThrow is { Success: true }, "successful save retained");
    Equal(beforeHp, target.CurrentHp, "no damage on successful save");
    True(campaign.PendingPlayerRoll is null, "no meaningless damage roll requested");
});

Run("player damage hands off directly to target Concentration", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(casterIsPlayer: true, targetIsPlayer: true, halfOnSuccess: true);
    engine.BeginConcentration(campaign, target.Id, "Bless");
    var dice = new DiceService((min, max) => min);

    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var savePending = campaign.PendingPlayerRoll ?? throw new Exception("target save pending missing");
    engine.ResolvePendingSpellSavingThrowRoll(campaign, savePending.Id, 5, null, dice);
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("caster damage pending missing");
    engine.ResolvePendingSpellSaveDamageRoll(campaign, damagePending.Id, 8, dice);

    var concentrationPending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing");
    Equal("concentration_check", concentrationPending.ResolutionKey, "damage hands off to Concentration");
    Equal(target.Id, concentrationPending.ActorCharacterId, "damaged target owns Concentration save");
    Equal("Bless", target.ConcentrationEffect, "Concentration remains until target d20 resolves");
});

Run("NPC caster keeps automatic saving throw damage resolution", () =>
{
    var (engine, campaign, caster, target, spell) = CreateFixture(casterIsPlayer: false, targetIsPlayer: false, halfOnSuccess: true);
    var dice = new DiceService((min, max) => min);
    var beforeHp = target.CurrentHp;

    var result = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    True(result.TargetSavingThrow is not null, "NPC caster resolves target save automatically");
    True(result.Damage is not null, "NPC caster resolves damage automatically");
    True(target.CurrentHp < beforeHp, "NPC damage applies immediately");
    True(campaign.PendingPlayerRoll is null, "NPC caster creates no player damage roll");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Save-spell damage roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Save-spell damage roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Caster, CharacterSheet Target, SpellDefinition Spell) CreateFixture(
    bool casterIsPlayer,
    bool targetIsPlayer,
    bool halfOnSuccess)
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = casterIsPlayer ? "pc-caster" : "npc-caster",
        Name = casterIsPlayer ? "Aric" : "Watcher Mage",
        CharacterType = casterIsPlayer ? "pc" : "npc",
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
        Name = targetIsPlayer ? "Mira" : "Ashen Watcher",
        CharacterType = targetIsPlayer ? "pc" : "npc",
        ArmorClass = 13,
        MaxHp = 30,
        CurrentHp = 30,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["dexterity"] = 14,
            ["constitution"] = 14
        },
        SavingThrowProficiencies = ["constitution"]
    };
    var spell = new SpellDefinition
    {
        Id = "spell-test-save-damage",
        Key = "test_save_damage",
        Name = "Test Burst",
        Level = 1,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 120,
        RequiresTarget = true,
        Resolution = "save",
        SaveAbility = "dexterity",
        DamageExpression = "2d6",
        DamageType = "Fire",
        HalfDamageOnSuccessfulSave = halfOnSuccess
    };
    caster.PreparedSpellIds.Add(spell.Id);
    var campaign = new CampaignState
    {
        Id = "campaign-save-spell-damage",
        Name = "Save Spell Damage Campaign",
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
