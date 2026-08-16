using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player spell attack waits for the supplied attack and damage rolls", () =>
{
    var (engine, campaign, caster, target, spell) = CreateSpellAttackFixture();
    var dice = new DiceService((min, max) => min);
    var beforeSlots = caster.SpellSlots[1].Remaining;
    var beforeHp = target.CurrentHp;

    var cast = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    Equal(beforeSlots - 1, caster.SpellSlots[1].Remaining, "spell slot should be committed when the cast starts");
    Equal(beforeHp, target.CurrentHp, "target HP must not change before the player attack roll");
    True(cast.SpellAttack is null, "cast result should wait for the player attack roll");
    var attackPending = campaign.PendingPlayerRoll ?? throw new Exception("spell attack pending roll missing");
    Equal("spell_attack", attackPending.ResolutionKey, "spell attack resolution key");
    Equal(5, attackPending.Modifier, "spell attack static modifier");
    Equal(13, attackPending.TargetNumber, "target AC");

    var attackResult = engine.ResolvePendingSpellAttackRoll(campaign, attackPending.Id, 12, null, dice);
    True(attackResult.SpellAttack is { Hit: true, D20: 12, Total: 17 }, "supplied d20 should be authoritative for the spell attack");
    Equal(beforeHp, target.CurrentHp, "target HP must not change before damage is rolled");
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("spell damage pending roll missing");
    Equal("spell_attack_damage", damagePending.ResolutionKey, "spell damage resolution key");

    var final = engine.ResolvePendingSpellAttackDamageRoll(campaign, damagePending.Id, 7, dice);
    Equal(beforeHp - 7, target.CurrentHp, "supplied spell damage should be authoritative");
    True(final.SpellAttack is { Hit: true, Damage: 7 }, "final attack should carry supplied damage");
    True(campaign.PendingPlayerRoll is null, "spell roll pipeline should finish with no pending roll");
});

Run("a supplied miss spends the committed slot but never requests damage", () =>
{
    var (engine, campaign, caster, target, spell) = CreateSpellAttackFixture();
    var dice = new DiceService((min, max) => min);
    var beforeSlots = caster.SpellSlots[1].Remaining;
    var beforeHp = target.CurrentHp;
    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("spell attack pending roll missing");

    var result = engine.ResolvePendingSpellAttackRoll(campaign, pending.Id, 2, null, dice);
    True(result.SpellAttack is { Hit: false, D20: 2 }, "supplied low d20 should miss");
    Equal(beforeSlots - 1, caster.SpellSlots[1].Remaining, "missed spell still spends the slot");
    Equal(beforeHp, target.CurrentHp, "miss should not change HP");
    True(campaign.PendingPlayerRoll is null, "miss should not create a damage roll");
});

Run("natural 20 marks pending spell damage as critical", () =>
{
    var (engine, campaign, caster, target, spell) = CreateSpellAttackFixture();
    var dice = new DiceService((min, max) => min);
    engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    var attackPending = campaign.PendingPlayerRoll ?? throw new Exception("spell attack pending roll missing");
    var result = engine.ResolvePendingSpellAttackRoll(campaign, attackPending.Id, 20, null, dice);
    True(result.SpellAttack is { Hit: true, Critical: true }, "natural 20 should be a critical spell attack");
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("critical spell damage pending roll missing");
    Equal("true", damagePending.Context["critical"], "critical damage context");
    True(damagePending.Formula.Contains("critical", StringComparison.OrdinalIgnoreCase), "critical damage prompt should be explicit");
});

Run("NPC spell attacks keep automatic deterministic resolution", () =>
{
    var (engine, campaign, caster, target, spell) = CreateSpellAttackFixture();
    caster.CharacterType = "npc";
    var dice = new DiceService((min, max) => max - 1);
    var beforeHp = target.CurrentHp;
    var result = engine.CastSpell(campaign, caster.Id, spell.Id, dice, target.Id, 1);
    True(result.SpellAttack is not null, "NPC spell attack should resolve immediately");
    True(campaign.PendingPlayerRoll is null, "NPC spell attack should not create a player roll");
    True(target.CurrentHp < beforeHp, "NPC automatic hit should apply damage immediately");
});

Run("spell damage can hand off directly to a target player's Concentration roll", () =>
{
    var (engine, campaign, caster, _, spell) = CreateSpellAttackFixture();
    var concentratingTarget = new CharacterSheet
    {
        Id = "pc-target",
        Name = "Mira",
        CharacterType = "pc",
        ArmorClass = 12,
        MaxHp = 30,
        CurrentHp = 30,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 14 },
        SavingThrowProficiencies = ["constitution"]
    };
    campaign.Characters.Add(concentratingTarget);
    engine.BeginConcentration(campaign, concentratingTarget.Id, "Bless");
    var dice = new DiceService((min, max) => min);

    engine.CastSpell(campaign, caster.Id, spell.Id, dice, concentratingTarget.Id, 1);
    var attackPending = campaign.PendingPlayerRoll ?? throw new Exception("spell attack pending roll missing");
    engine.ResolvePendingSpellAttackRoll(campaign, attackPending.Id, 15, null, dice);
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("spell damage pending roll missing");
    engine.ResolvePendingSpellAttackDamageRoll(campaign, damagePending.Id, 8, dice);

    var concentrationPending = campaign.PendingPlayerRoll ?? throw new Exception("target Concentration pending roll missing");
    Equal("concentration_check", concentrationPending.ResolutionKey, "damage should hand off to target Concentration request");
    Equal(concentratingTarget.Id, concentrationPending.ActorCharacterId, "Concentration actor");
    Equal("Bless", concentratingTarget.ConcentrationEffect, "Concentration should wait for the target player's d20");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Spell roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Spell roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Caster, CharacterSheet Target, SpellDefinition Spell) CreateSpellAttackFixture()
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = "pc-caster",
        Name = "Aric",
        CharacterType = "pc",
        Level = 3,
        MaxHp = 20,
        CurrentHp = 20,
        ProficiencyBonus = 2,
        SpellcastingAbility = "intelligence",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 16 }
    };
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
    var target = new CharacterSheet
    {
        Id = "npc-target",
        Name = "Ashen Watcher",
        CharacterType = "npc",
        ArmorClass = 13,
        MaxHp = 30,
        CurrentHp = 30
    };
    var spell = new SpellDefinition
    {
        Id = "spell-test-bolt",
        Key = "test_bolt",
        Name = "Test Bolt",
        Level = 1,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 120,
        RequiresTarget = true,
        Resolution = "attack",
        DamageExpression = "2d6",
        DamageType = "Force"
    };
    caster.PreparedSpellIds.Add(spell.Id);
    var campaign = new CampaignState
    {
        Id = "campaign-spell-rolls",
        Name = "Spell Roll Campaign",
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
