using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player auto-hit projectiles wait for each supplied damage roll", () =>
{
    var (engine, campaign, caster, targetA, targetB, spell) = CreateFixture();
    var dice = new DiceService((min, max) => min);
    var beforeSlots = caster.SpellSlots[1].Remaining;
    var hpA = targetA.CurrentHp;
    var hpB = targetB.CurrentHp;

    var cast = engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetB.Id, targetA.Id], 1);
    Equal(beforeSlots - 1, caster.SpellSlots[1].Remaining, "slot committed once at cast start");
    Equal(0, cast.TargetResults?.Count ?? -1, "no dart resolves before player damage");

    var first = campaign.PendingPlayerRoll ?? throw new Exception("first dart damage pending missing");
    Equal("projectile_auto_damage", first.ResolutionKey, "first dart key");
    engine.ResolvePendingAutoProjectileSpellDamageRoll(campaign, first.Id, 4, dice);
    Equal(hpA - 4, targetA.CurrentHp, "first supplied damage");

    var second = campaign.PendingPlayerRoll ?? throw new Exception("second dart damage pending missing");
    engine.ResolvePendingAutoProjectileSpellDamageRoll(campaign, second.Id, 5, dice);
    Equal(hpB - 5, targetB.CurrentHp, "second supplied damage");

    var third = campaign.PendingPlayerRoll ?? throw new Exception("third dart damage pending missing");
    var complete = engine.ResolvePendingAutoProjectileSpellDamageRoll(campaign, third.Id, 6, dice);
    Equal(hpA - 10, targetA.CurrentHp, "first target total damage");
    Equal(3, complete.TargetResults?.Count ?? 0, "all dart results retained");
    True(campaign.PendingPlayerRoll is null, "sequence finishes with no pending roll");
});

Run("Concentration pauses the auto-hit projectile sequence and resumes after the player save", () =>
{
    var (engine, campaign, caster, _, _, spell) = CreateFixture();
    var target = new CharacterSheet
    {
        Id = "pc-concentrating",
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

    engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [target.Id, target.Id, target.Id], 1);
    var firstDamage = campaign.PendingPlayerRoll ?? throw new Exception("first dart pending missing");
    engine.ResolvePendingAutoProjectileSpellDamageRoll(campaign, firstDamage.Id, 8, dice);

    var concentration = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing");
    Equal("concentration_check", concentration.ResolutionKey, "Concentration handoff key");
    Equal("auto_projectile_spell_sequence", concentration.Context["continuation_resolution_key"], "continuation key");
    engine.ResolvePendingConcentrationCheckRoll(campaign, concentration.Id, 15, null, dice);

    var secondDamage = campaign.PendingPlayerRoll ?? throw new Exception("second dart did not resume after Concentration");
    Equal("projectile_auto_damage", secondDamage.ResolutionKey, "resumed dart damage key");
    Equal("1", secondDamage.Context["projectile_index"], "resumed at dart 2");
});

Run("pending auto-projectile sequence survives campaign JSON serialization", () =>
{
    var (engine, campaign, caster, targetA, _, spell) = CreateFixture();
    var dice = new DiceService((min, max) => min);
    engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetA.Id, targetA.Id], 1);
    var json = System.Text.Json.JsonSerializer.Serialize(campaign);
    var restored = System.Text.Json.JsonSerializer.Deserialize<CampaignState>(json)
        ?? throw new Exception("campaign JSON restore failed");
    var pending = restored.PendingPlayerRoll ?? throw new Exception("pending dart roll was not restored");
    Equal("projectile_auto_damage", pending.ResolutionKey, "restored pending key");
    True(pending.Context.ContainsKey("sequence_json"), "serialized sequence context retained");
});

Run("NPC auto-hit projectile spells remain automatic", () =>
{
    var (engine, campaign, caster, targetA, targetB, spell) = CreateFixture();
    caster.CharacterType = "npc";
    var dice = new DiceService((min, max) => min);
    var beforeA = targetA.CurrentHp;
    var result = engine.CastProjectileSpell(campaign, caster.Id, spell.Id, dice, [targetA.Id, targetB.Id, targetA.Id], 1);
    True(campaign.PendingPlayerRoll is null, "NPC auto-hit spell must not create a player roll");
    Equal(3, result.TargetResults?.Count ?? 0, "NPC darts resolve immediately");
    True(targetA.CurrentHp < beforeA, "NPC darts apply damage immediately");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Auto-projectile roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Auto-projectile roll tests passed: {passed}");

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
        Id = "pc-auto-caster",
        Name = "Aric",
        CharacterType = "pc",
        Level = 3,
        MaxHp = 24,
        CurrentHp = 24,
        ProficiencyBonus = 2,
        SpellcastingAbility = "intelligence",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 16 }
    };
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 4, Remaining = 4 };
    var targetA = new CharacterSheet { Id = "target-a", Name = "Watcher A", CharacterType = "npc", ArmorClass = 13, MaxHp = 40, CurrentHp = 40 };
    var targetB = new CharacterSheet { Id = "target-b", Name = "Watcher B", CharacterType = "npc", ArmorClass = 14, MaxHp = 40, CurrentHp = 40 };
    var spell = new SpellDefinition
    {
        Id = "spell-test-darts",
        Key = "test_darts",
        Name = "Test Darts",
        Level = 1,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 120,
        Resolution = "projectile_auto",
        DamageExpression = "1d4+1",
        DamageType = "Force",
        BaseProjectiles = 3,
        ExtraProjectilesPerSlot = 1
    };
    caster.PreparedSpellIds.Add(spell.Id);
    var campaign = new CampaignState
    {
        Id = "campaign-auto-projectiles",
        Name = "Auto Projectile Campaign",
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
