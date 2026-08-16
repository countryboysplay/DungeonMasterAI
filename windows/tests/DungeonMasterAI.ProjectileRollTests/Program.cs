using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player projectile spell pauses for every attack and damage roll", () =>
{
    var (engine, campaign, caster, targetA, targetB, spell) = CreateFixture();
    var dice = new DiceService((min, max) => min);
    var beforeSlots = caster.SpellSlots[2].Remaining;
    var hpA = targetA.CurrentHp;
    var hpB = targetB.CurrentHp;

    var cast = engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetB.Id, targetA.Id], 2);
    Equal(beforeSlots - 1, caster.SpellSlots[2].Remaining, "slot committed once at cast start");
    True(cast.TargetResults is { Count: 0 }, "no projectile should resolve before the first player d20");
    var p1 = campaign.PendingPlayerRoll ?? throw new Exception("projectile 1 attack pending missing");
    Equal("projectile_spell_attack", p1.ResolutionKey, "projectile 1 attack key");

    engine.ResolvePendingProjectileSpellAttackRoll(campaign, p1.Id, 15, null, dice);
    True(campaign.PendingPlayerRoll?.ResolutionKey == "projectile_spell_damage", "projectile 1 hit should request damage");
    Equal(hpA, targetA.CurrentHp, "HP unchanged before projectile 1 damage");
    var p1Damage = campaign.PendingPlayerRoll!;
    engine.ResolvePendingProjectileSpellDamageRoll(campaign, p1Damage.Id, 4, dice);
    Equal(hpA - 4, targetA.CurrentHp, "projectile 1 supplied damage");
    var p2 = campaign.PendingPlayerRoll ?? throw new Exception("projectile 2 attack pending missing");
    Equal("projectile_spell_attack", p2.ResolutionKey, "projectile 2 attack key");

    engine.ResolvePendingProjectileSpellAttackRoll(campaign, p2.Id, 1, null, dice);
    Equal(hpB, targetB.CurrentHp, "projectile 2 miss should not damage target B");
    var p3 = campaign.PendingPlayerRoll ?? throw new Exception("projectile 3 attack pending missing");
    Equal("projectile_spell_attack", p3.ResolutionKey, "projectile 3 attack key");

    engine.ResolvePendingProjectileSpellAttackRoll(campaign, p3.Id, 18, null, dice);
    var p3Damage = campaign.PendingPlayerRoll ?? throw new Exception("projectile 3 damage pending missing");
    var complete = engine.ResolvePendingProjectileSpellDamageRoll(campaign, p3Damage.Id, 6, dice);
    Equal(hpA - 10, targetA.CurrentHp, "both supplied damages applied to target A");
    Equal(hpB, targetB.CurrentHp, "target B remains undamaged after miss");
    Equal(beforeSlots - 1, caster.SpellSlots[2].Remaining, "multi-projectile spell spends only one slot");
    True(campaign.PendingPlayerRoll is null, "sequence should finish with no pending roll");
    Equal(3, complete.TargetResults?.Count ?? 0, "all projectile results retained");
    True(complete.TargetResults![0].SpellAttack is { D20: 15, Hit: true }, "projectile 1 supplied d20 retained");
    True(complete.TargetResults![1].SpellAttack is { D20: 1, Hit: false }, "projectile 2 supplied d20 retained");
    True(complete.TargetResults![2].SpellAttack is { D20: 18, Hit: true, Damage: 6 }, "projectile 3 supplied d20 and damage retained");
});

Run("natural 20 creates critical projectile damage request", () =>
{
    var (engine, campaign, caster, targetA, _, spell) = CreateFixture();
    var dice = new DiceService((min, max) => min);
    engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetA.Id, targetA.Id], 2);
    var attackPending = campaign.PendingPlayerRoll ?? throw new Exception("projectile attack pending missing");
    engine.ResolvePendingProjectileSpellAttackRoll(campaign, attackPending.Id, 20, null, dice);
    var damagePending = campaign.PendingPlayerRoll ?? throw new Exception("critical projectile damage pending missing");
    Equal("true", damagePending.Context["critical"], "critical context");
    True(damagePending.Formula.Contains("critical", StringComparison.OrdinalIgnoreCase), "critical prompt");
});

Run("NPC projectile attack spell keeps automatic deterministic resolution", () =>
{
    var (engine, campaign, caster, targetA, targetB, spell) = CreateFixture();
    caster.CharacterType = "npc";
    var dice = new DiceService((min, max) => max - 1);
    var beforeA = targetA.CurrentHp;
    var result = engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetB.Id, targetA.Id], 2);
    True(campaign.PendingPlayerRoll is null, "NPC projectile spell should not create a player roll");
    Equal(3, result.TargetResults?.Count ?? 0, "NPC projectiles resolve immediately");
    True(targetA.CurrentHp < beforeA, "NPC automatic projectiles apply damage");
});

Run("player target Concentration pauses and then resumes the projectile sequence", () =>
{
    var (engine, campaign, caster, _, _, spell) = CreateFixture();
    var target = new CharacterSheet
    {
        Id = "pc-concentrating-target",
        Name = "Mira",
        CharacterType = "pc",
        ArmorClass = 12,
        MaxHp = 40,
        CurrentHp = 40,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 14 },
        SavingThrowProficiencies = ["constitution"]
    };
    campaign.Characters.Add(target);
    engine.BeginConcentration(campaign, target.Id, "Bless");
    var dice = new DiceService((min, max) => min);

    engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [target.Id, target.Id, target.Id], 2);
    var attack1 = campaign.PendingPlayerRoll ?? throw new Exception("projectile attack pending missing");
    engine.ResolvePendingProjectileSpellAttackRoll(campaign, attack1.Id, 15, null, dice);
    var damage1 = campaign.PendingPlayerRoll ?? throw new Exception("projectile damage pending missing");
    engine.ResolvePendingProjectileSpellDamageRoll(campaign, damage1.Id, 8, dice);

    var concentration = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing");
    Equal("concentration_check", concentration.ResolutionKey, "Concentration handoff key");
    Equal("projectile_spell_sequence", concentration.Context["continuation_resolution_key"], "continuation key");
    engine.ResolvePendingConcentrationCheckRoll(campaign, concentration.Id, 15, null, dice);

    var attack2 = campaign.PendingPlayerRoll ?? throw new Exception("projectile sequence did not resume after Concentration");
    Equal("projectile_spell_attack", attack2.ResolutionKey, "resumed projectile attack key");
    Equal("1", attack2.Context["projectile_index"], "resumed at projectile 2");
});

Run("pending projectile sequence survives campaign JSON serialization", () =>
{
    var (engine, campaign, caster, targetA, _, spell) = CreateFixture();
    var dice = new DiceService((min, max) => min);
    engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetA.Id, targetA.Id], 2);
    var json = System.Text.Json.JsonSerializer.Serialize(campaign);
    var restored = System.Text.Json.JsonSerializer.Deserialize<CampaignState>(json)
        ?? throw new Exception("campaign JSON restore failed");
    var pending = restored.PendingPlayerRoll ?? throw new Exception("pending projectile roll was not restored");
    Equal("projectile_spell_attack", pending.ResolutionKey, "restored pending key");
    True(pending.Context.ContainsKey("sequence_json"), "serialized sequence context retained");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Projectile roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Projectile roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Caster, CharacterSheet TargetA, CharacterSheet TargetB, SpellDefinition Spell) CreateFixture()
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = "pc-projectile-caster",
        Name = "Aric",
        CharacterType = "pc",
        Level = 5,
        MaxHp = 30,
        CurrentHp = 30,
        ProficiencyBonus = 3,
        SpellcastingAbility = "intelligence",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 16 }
    };
    caster.SpellSlots[2] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
    var targetA = new CharacterSheet { Id = "target-a", Name = "Watcher A", CharacterType = "npc", ArmorClass = 13, MaxHp = 40, CurrentHp = 40 };
    var targetB = new CharacterSheet { Id = "target-b", Name = "Watcher B", CharacterType = "npc", ArmorClass = 14, MaxHp = 40, CurrentHp = 40 };
    var spell = new SpellDefinition
    {
        Id = "spell-test-rays",
        Key = "test_rays",
        Name = "Test Rays",
        Level = 2,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 120,
        Resolution = "projectile_attack",
        DamageExpression = "2d6",
        DamageType = "Fire",
        BaseProjectiles = 3,
        ExtraProjectilesPerSlot = 1
    };
    caster.PreparedSpellIds.Add(spell.Id);
    var campaign = new CampaignState
    {
        Id = "campaign-projectile-rolls",
        Name = "Projectile Roll Campaign",
        Characters = [caster, targetA, targetB],
        Spells = [spell]
    };
    return (engine, campaign, caster, targetA, targetB, spell);
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
