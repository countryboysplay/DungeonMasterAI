using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("readied projectile attack keeps all player attack and damage rolls off turn", () =>
{
    var f = CreateFixture(CreateAttackProjectileSpell(), targetType: "monster");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[2].Remaining;
    var hpBefore = f.Target.CurrentHp;

    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the watcher advances", 2);
    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "Ready spends one slot");
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var pending = accepted.FollowUpRoll ?? throw new Exception("first projectile attack was not requested");
    Equal("projectile_spell_attack", pending.ResolutionKey, "first projectile attack key");
    True(!f.CasterCombatant.ReactionAvailable, "accepted trigger spends Reaction");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "release does not spend another slot");

    var suppliedDamage = new[] { 4, 5, 6 };
    for (var i = 0; i < suppliedDamage.Length; i++)
    {
        var attackPending = f.Campaign.PendingPlayerRoll ?? throw new Exception($"projectile {i + 1} attack missing");
        Equal("projectile_spell_attack", attackPending.ResolutionKey, $"projectile {i + 1} attack key");
        f.Engine.ResolvePendingProjectileSpellAttackRoll(f.Campaign, attackPending.Id, 15 + i, null, dice);
        var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception($"projectile {i + 1} damage missing");
        Equal("projectile_spell_damage", damagePending.ResolutionKey, $"projectile {i + 1} damage key");
        f.Engine.ResolvePendingProjectileSpellDamageRoll(f.Campaign, damagePending.Id, suppliedDamage[i], dice);
    }

    Equal(hpBefore - suppliedDamage.Sum(), f.Target.CurrentHp, "all supplied projectile damages apply");
    True(f.Campaign.PendingPlayerRoll is null, "projectile attack sequence finishes cleanly");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "sequence spends the slot exactly once");
});

Run("readied auto-hit projectiles keep every damage roll player-owned off turn", () =>
{
    var f = CreateFixture(CreateAutoProjectileSpell(), targetType: "monster");
    var dice = MinimumDice();
    var slotsBefore = f.Caster.SpellSlots[1].Remaining;
    var hpBefore = f.Target.CurrentHp;

    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the watcher advances", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    Equal("projectile_auto_damage", accepted.FollowUpRoll?.ResolutionKey, "first auto-projectile damage key");

    var suppliedDamage = new[] { 3, 4, 5 };
    for (var i = 0; i < suppliedDamage.Length; i++)
    {
        var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception($"auto projectile {i + 1} damage missing");
        Equal("projectile_auto_damage", damagePending.ResolutionKey, $"auto projectile {i + 1} key");
        f.Engine.ResolvePendingAutoProjectileSpellDamageRoll(f.Campaign, damagePending.Id, suppliedDamage[i], dice);
    }

    Equal(hpBefore - suppliedDamage.Sum(), f.Target.CurrentHp, "all supplied auto-projectile damages apply");
    True(f.Campaign.PendingPlayerRoll is null, "auto-projectile sequence finishes cleanly");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[1].Remaining, "auto-projectile Ready spends one slot total");
});

Run("Concentration interruption resumes a readied projectile sequence off turn", () =>
{
    var f = CreateFixture(CreateAttackProjectileSpell(), targetType: "pc");
    var dice = MinimumDice();
    f.Engine.BeginConcentration(f.Campaign, f.Target.Id, "Bless");
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "Mira advances", 2);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var decision = f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.TargetCombatant.Id);
    var accepted = f.Engine.ResolvePendingPlayerDecision(f.Campaign, decision.Id, "use_reaction", dice);
    var attack = accepted.FollowUpRoll ?? throw new Exception("projectile attack missing");
    f.Engine.ResolvePendingProjectileSpellAttackRoll(f.Campaign, attack.Id, 18, null, dice);
    var damage = f.Campaign.PendingPlayerRoll ?? throw new Exception("projectile damage missing");
    f.Engine.ResolvePendingProjectileSpellDamageRoll(f.Campaign, damage.Id, 8, dice);

    var concentration = f.Campaign.PendingPlayerRoll ?? throw new Exception("Concentration save missing");
    Equal("concentration_check", concentration.ResolutionKey, "Concentration handoff key");
    Equal("projectile_spell_sequence", concentration.Context["continuation_resolution_key"], "projectile continuation key");
    f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentration.Id, 15, null, dice);

    var resumed = f.Campaign.PendingPlayerRoll ?? throw new Exception("projectile sequence did not resume after Concentration");
    Equal("projectile_spell_attack", resumed.ResolutionKey, "off-turn sequence resumed with next attack");
    Equal("1", resumed.Context["projectile_index"], "resumed at projectile 2");
});

Run("readied projectile decision requires a release target", () =>
{
    var f = CreateFixture(CreateAttackProjectileSpell(), targetType: "monster");
    var dice = MinimumDice();
    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the watcher advances", 2);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);

    var rejected = false;
    try { f.Engine.RequestReadiedSpellDecision(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, null); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "projectile release without a target must be rejected");
    True(f.CasterCombatant.ReactionAvailable, "invalid target does not spend Reaction");
    True(f.CasterCombatant.ReadiedAction is not null, "invalid target preserves readied spell");
});

Run("NPC readied attack projectiles resolve automatically but pause for PC Concentration", () =>
{
    var f = CreateFixture(CreateAttackProjectileSpell(), targetType: "pc");
    f.Caster.CharacterType = "monster";
    var dice = MaximumDice();
    var slotsBefore = f.Caster.SpellSlots[2].Remaining;
    var hpBefore = f.Target.CurrentHp;
    f.Engine.BeginConcentration(f.Campaign, f.Target.Id, "Bless");

    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the hero advances", 2);
    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "NPC Ready spends its slot once");
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    f.Engine.TriggerReadiedSpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, dice, f.TargetCombatant.Id);

    for (var projectile = 1; projectile <= 3; projectile++)
    {
        var concentration = f.Campaign.PendingPlayerRoll ?? throw new Exception($"Concentration save missing after NPC projectile {projectile}");
        Equal("concentration_check", concentration.ResolutionKey, $"NPC projectile {projectile} Concentration handoff");
        Equal("projectile_spell_sequence", concentration.Context["continuation_resolution_key"], $"NPC projectile {projectile} continuation key");
        f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentration.Id, 20, null, dice);
    }

    True(f.Campaign.PendingPlayerRoll is null, "NPC attack-projectile sequence finishes after final Concentration save");
    True(f.Target.CurrentHp < hpBefore, "NPC attack projectiles dealt automatic damage");
    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "NPC projectile release never spends a second slot");
    True(!f.CasterCombatant.ReactionAvailable, "NPC projectile release spends Reaction");
});

Run("NPC readied auto-hit projectiles resolve damage automatically and preserve Concentration pauses", () =>
{
    var f = CreateFixture(CreateAutoProjectileSpell(), targetType: "pc");
    f.Caster.CharacterType = "monster";
    var dice = MaximumDice();
    var hpBefore = f.Target.CurrentHp;
    f.Engine.BeginConcentration(f.Campaign, f.Target.Id, "Bless");

    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the hero advances", 1);
    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);
    f.Engine.TriggerReadiedSpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, dice, f.TargetCombatant.Id);

    for (var projectile = 1; projectile <= 3; projectile++)
    {
        var concentration = f.Campaign.PendingPlayerRoll ?? throw new Exception($"Concentration save missing after NPC auto projectile {projectile}");
        Equal("concentration_check", concentration.ResolutionKey, $"NPC auto projectile {projectile} Concentration handoff");
        Equal("auto_projectile_spell_sequence", concentration.Context["continuation_resolution_key"], $"NPC auto projectile {projectile} continuation key");
        f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentration.Id, 20, null, dice);
    }

    True(f.Campaign.PendingPlayerRoll is null, "NPC auto-projectile sequence finishes cleanly");
    True(f.Target.CurrentHp < hpBefore, "NPC auto projectiles dealt automatic damage");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied projectile spell tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied projectile spell tests passed: {passed}");

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

static Fixture CreateFixture(SpellDefinition spell, string targetType)
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = "readied-projectile-caster",
        Name = "Aric",
        CharacterType = "pc",
        Level = 5,
        MaxHp = 36,
        CurrentHp = 36,
        ArmorClass = 15,
        Speed = 30,
        ProficiencyBonus = 3,
        SpellcastingAbility = "intelligence",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["intelligence"] = 16,
            ["constitution"] = 12,
            ["dexterity"] = 12
        }
    };
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 4, Remaining = 4 };
    caster.SpellSlots[2] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
    caster.PreparedSpellIds.Add(spell.Id);

    var target = new CharacterSheet
    {
        Id = "readied-projectile-target",
        Name = targetType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "Mira" : "Ashen Watcher",
        CharacterType = targetType,
        Level = 5,
        MaxHp = 60,
        CurrentHp = 60,
        ArmorClass = 12,
        Speed = 30,
        ProficiencyBonus = 3,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["constitution"] = 14,
            ["dexterity"] = 12
        },
        SavingThrowProficiencies = ["constitution"]
    };

    var campaign = new CampaignState
    {
        Id = "readied-projectile-campaign",
        Name = "Readied Projectile Campaign",
        Characters = [caster, target],
        Spells = [spell]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Projectile Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 3);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, caster, target, spell, encounter, casterCombatant, targetCombatant);
}

static SpellDefinition CreateAttackProjectileSpell() => new()
{
    Id = "readied-test-rays",
    Key = "readied_test_rays",
    Name = "Test Rays",
    Level = 2,
    CastingTime = "Action",
    RangeKind = "distance",
    RangeFeet = 120,
    RequiresTarget = true,
    Resolution = "projectile_attack",
    DamageExpression = "2d6",
    DamageType = "Fire",
    BaseProjectiles = 3,
    ExtraProjectilesPerSlot = 1
};

static SpellDefinition CreateAutoProjectileSpell() => new()
{
    Id = "readied-test-darts",
    Key = "readied_test_darts",
    Name = "Test Darts",
    Level = 1,
    CastingTime = "Action",
    RangeKind = "distance",
    RangeFeet = 120,
    RequiresTarget = true,
    Resolution = "projectile_auto",
    DamageExpression = "1d4+1",
    DamageType = "Force",
    BaseProjectiles = 3,
    ExtraProjectilesPerSlot = 1
};

static DiceService MinimumDice() => new((min, max) => min);
static DiceService MaximumDice() => new((min, max) => max);

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
