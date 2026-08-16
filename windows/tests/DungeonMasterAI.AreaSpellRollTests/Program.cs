using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player caster supplies one shared area damage roll", () =>
{
    var f = CreateFixture(casterIsPlayer: true, firstTargetIsPlayer: false, includeSecondNpc: false);
    var dice = MinimumDice();
    var hpBefore = f.TargetOne.CurrentHp;

    var cast = f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    True(cast.TargetResults?.Count == 0, "area spell should pause before applying damage");
    Equal(hpBefore, f.TargetOne.CurrentHp, "HP must not change before player damage roll");
    var pending = f.Campaign.PendingPlayerRoll ?? throw new Exception("area damage pending missing");
    Equal("area_spell_damage", pending.ResolutionKey, "area damage key");
    Equal(f.Caster.Id, pending.ActorCharacterId, "caster owns shared damage roll");

    var result = f.Engine.ResolvePendingAreaSpellDamageRoll(f.Campaign, pending.Id, 10, dice);
    Equal(hpBefore - 10, f.TargetOne.CurrentHp, "supplied shared damage must be authoritative");
    Equal(10, result.TargetResults?.Single().Damage?.Damage.RequestedDamage, "result should preserve supplied damage");
    True(f.Campaign.PendingPlayerRoll is null, "single target area sequence should finish");
});

Run("player target supplies authoritative area saving throw", () =>
{
    var f = CreateFixture(casterIsPlayer: false, firstTargetIsPlayer: true, includeSecondNpc: false);
    var dice = MinimumDice();
    var hpBefore = f.TargetOne.CurrentHp;

    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    var pending = f.Campaign.PendingPlayerRoll ?? throw new Exception("area save pending missing");
    Equal("area_spell_saving_throw", pending.ResolutionKey, "area save key");
    Equal(f.TargetOne.Id, pending.ActorCharacterId, "target owns area save");

    var result = f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, pending.Id, 20, null, dice);
    var targetResult = result.TargetResults?.Single() ?? throw new Exception("target result missing");
    True(targetResult.TargetSavingThrow is { ChosenRoll: 20, Success: true }, "supplied d20 should decide area save");
    Equal(hpBefore - 1, f.TargetOne.CurrentHp, "NPC caster deterministic 2 damage should be halved to 1");
});

Run("one player damage roll is shared across multiple affected targets", () =>
{
    var f = CreateFixture(casterIsPlayer: true, firstTargetIsPlayer: true, includeSecondNpc: true);
    var dice = MinimumDice();
    var oneBefore = f.TargetOne.CurrentHp;
    var twoBefore = f.TargetTwo!.CurrentHp;

    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    var savePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("player target save pending missing");
    Equal("area_spell_saving_throw", savePending.ResolutionKey, "first pending should be player save");
    f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, savePending.Id, 1, null, dice);

    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("shared area damage pending missing");
    Equal("area_spell_damage", damagePending.ResolutionKey, "damage follows all saves");
    var complete = f.Engine.ResolvePendingAreaSpellDamageRoll(f.Campaign, damagePending.Id, 8, dice);
    Equal(oneBefore - 8, f.TargetOne.CurrentHp, "player target should use shared 8 damage");
    Equal(twoBefore - 8, f.TargetTwo.CurrentHp, "NPC target should use same shared 8 damage");
    Equal(2, complete.TargetResults?.Count, "both targets should resolve");
    True(complete.TargetResults!.All(r => r.Damage?.Damage.RequestedDamage == 8), "all failed saves should reference one shared damage total");
});

Run("Restrained player area Dexterity save requires two d20 results", () =>
{
    var f = CreateFixture(casterIsPlayer: false, firstTargetIsPlayer: true, includeSecondNpc: false);
    f.TargetOne.Conditions.Add("Restrained");
    var dice = MinimumDice();
    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    var pending = f.Campaign.PendingPlayerRoll ?? throw new Exception("area save pending missing");
    Equal("disadvantage", pending.RollMode, "Restrained should impose Disadvantage");

    var rejected = false;
    try { f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, pending.Id, 18, null, dice); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "one d20 should be rejected for Disadvantage");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "failed resolution preserves pending roll");

    var result = f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, pending.Id, 18, 4, dice);
    Equal(4, result.TargetResults?.Single().TargetSavingThrow?.ChosenRoll, "lower supplied d20 should be authoritative");
});

Run("automatic-failure area save does not ask player for meaningless d20", () =>
{
    var f = CreateFixture(casterIsPlayer: true, firstTargetIsPlayer: true, includeSecondNpc: false);
    f.TargetOne.Conditions.Add("Paralyzed");
    var dice = MinimumDice();

    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    var pending = f.Campaign.PendingPlayerRoll ?? throw new Exception("damage pending missing");
    Equal("area_spell_damage", pending.ResolutionKey, "automatic failed save should skip player save and move to caster damage");
});

Run("Concentration interruption resumes frozen area sequence", () =>
{
    var f = CreateFixture(casterIsPlayer: false, firstTargetIsPlayer: true, includeSecondNpc: true);
    f.Engine.BeginConcentration(f.Campaign, f.TargetOne.Id, "Bless");
    var dice = MinimumDice();
    var secondBefore = f.TargetTwo!.CurrentHp;

    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    var savePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("area save pending missing");
    f.Engine.ResolvePendingAreaSpellSavingThrowRoll(f.Campaign, savePending.Id, 1, null, dice);

    var concentrationPending = f.Campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing");
    Equal("concentration_check", concentrationPending.ResolutionKey, "damage should hand off to Concentration");
    Equal("area_spell_sequence", concentrationPending.Context["continuation_resolution_key"], "area continuation should be attached");
    Equal(secondBefore, f.TargetTwo.CurrentHp, "later target must wait for Concentration resolution");

    f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentrationPending.Id, 20, null, dice);
    True(f.Campaign.PendingPlayerRoll is null, "area sequence should finish after Concentration");
    Equal(secondBefore - 2, f.TargetTwo.CurrentHp, "sequence should resume and resolve later NPC target");
});

Run("pending area sequence survives campaign JSON serialization", () =>
{
    var f = CreateFixture(casterIsPlayer: false, firstTargetIsPlayer: true, includeSecondNpc: false);
    var dice = MinimumDice();
    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, f.Spell.Id, dice, centerX: 4, centerY: 0, slotLevel: 1, encounterId: f.Encounter.Id);
    var originalPending = f.Campaign.PendingPlayerRoll ?? throw new Exception("area save pending missing");

    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredPending = restored.PendingPlayerRoll ?? throw new Exception("restored pending missing");
    Equal(originalPending.Id, restoredPending.Id, "pending roll id survives serialization");

    var restoredTarget = restored.Characters.Single(c => c.Id == f.TargetOne.Id);
    var before = restoredTarget.CurrentHp;
    var result = f.Engine.ResolvePendingAreaSpellSavingThrowRoll(restored, restoredPending.Id, 1, null, dice);
    True(result.TargetResults?.Count == 1, "restored sequence should resolve to completion");
    Equal(before - 2, restoredTarget.CurrentHp, "restored sequence should apply deterministic area damage");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Area spell roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Area spell roll tests passed: {passed}");

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

static DiceService MinimumDice() => new((min, max) => min);

static Fixture CreateFixture(bool casterIsPlayer, bool firstTargetIsPlayer, bool includeSecondNpc)
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = "area-caster",
        Name = "Area Caster",
        CharacterType = casterIsPlayer ? "pc" : "npc",
        Level = 3,
        MaxHp = 40,
        CurrentHp = 40,
        ArmorClass = 14,
        ProficiencyBonus = 2,
        SpellcastingAbility = "intelligence",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 16 }
    };
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 4, Remaining = 4 };

    var targetOne = new CharacterSheet
    {
        Id = "area-target-one",
        Name = firstTargetIsPlayer ? "Aric" : "Watcher One",
        CharacterType = firstTargetIsPlayer ? "pc" : "npc",
        MaxHp = 40,
        CurrentHp = 40,
        ArmorClass = 12,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["dexterity"] = 10,
            ["constitution"] = 10
        }
    };

    CharacterSheet? targetTwo = null;
    if (includeSecondNpc)
    {
        targetTwo = new CharacterSheet
        {
            Id = "area-target-two",
            Name = "Watcher Two",
            CharacterType = "npc",
            MaxHp = 40,
            CurrentHp = 40,
            ArmorClass = 12,
            ProficiencyBonus = 2,
            Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["dexterity"] = 10,
                ["constitution"] = 10
            }
        };
    }

    var spell = new SpellDefinition
    {
        Id = "area-test-spell",
        Key = "area_test_spell",
        Name = "Test Fireburst",
        Level = 1,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 60,
        Resolution = "area_save",
        SaveAbility = "dexterity",
        DamageExpression = "2d6",
        DamageType = "Fire",
        HalfDamageOnSuccessfulSave = true,
        AreaShape = "sphere",
        AreaSizeFeet = 10,
        AreaOrigin = "point"
    };
    caster.PreparedSpellIds.Add(spell.Id);

    var characters = new List<CharacterSheet> { caster, targetOne };
    if (targetTwo is not null) characters.Add(targetTwo);
    var campaign = new CampaignState
    {
        Id = "area-roll-campaign",
        Name = "Area Roll Campaign",
        Characters = characters,
        Spells = [spell]
    };

    var encounter = engine.StartEncounter(campaign, "Area Roll Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var targetOneCombatant = engine.AddCombatant(campaign, encounter.Id, targetOne.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetOneCombatant.Id, 4, 0);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 30);
    engine.SetInitiative(campaign, encounter.Id, targetOneCombatant.Id, 20);
    if (targetTwo is not null)
    {
        var targetTwoCombatant = engine.AddCombatant(campaign, encounter.Id, targetTwo.Id, side: "opposition");
        engine.SetCombatantPosition(campaign, encounter.Id, targetTwoCombatant.Id, 4, 1);
        engine.SetInitiative(campaign, encounter.Id, targetTwoCombatant.Id, 10);
    }
    engine.FinalizeInitiative(campaign, encounter.Id);

    return new Fixture(engine, campaign, caster, targetOne, targetTwo, spell, encounter);
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

sealed record Fixture(
    GameEngine Engine,
    CampaignState Campaign,
    CharacterSheet Caster,
    CharacterSheet TargetOne,
    CharacterSheet? TargetTwo,
    SpellDefinition Spell,
    EncounterState Encounter);
