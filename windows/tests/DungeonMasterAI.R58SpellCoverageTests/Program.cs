using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

// r58 spell coverage: line-shaped areas, multi-target healing, and the eight
// newly authored SRD 5.2.1 spells. Every expected value below is taken from the
// SRD text, not from the generated catalog, so the catalog cannot silently drift.

var failures = new List<string>();
var passed = 0;

// ---------------------------------------------------------------------------
// Line area geometry
// ---------------------------------------------------------------------------

Run("line area covers its full length along the cast direction", () =>
{
    // The engine uses screen coordinates, so "south" is +Y. A 100-foot line
    // reaches 20 cells; the 21st is out of range.
    True(SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 0, 1, "south", 5), "first cell in the line is affected");
    True(SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 0, 20, "south", 5), "the 100-foot cell is affected");
    True(!SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 0, 21, "south", 5), "beyond 100 feet is unaffected");
});

Run("line width does not grow with distance the way a cone does", () =>
{
    // A cone widens as it travels; a 5-foot-wide line never does.
    True(!SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 3, 15, "south", 5), "a far off-axis cell is outside a line");
    True(SpellAreaGeometry.ContainsCell("cone", 100, 0, 0, 3, 15, "south"), "the same cell is inside an equivalent cone");
});

Run("line ignores cells behind and beside the origin", () =>
{
    True(!SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 0, 0, "south", 5), "the origin cell itself is not in the line");
    True(!SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 0, -4, "south", 5), "cells behind the caster are unaffected");
    True(!SpellAreaGeometry.ContainsCell("line", 100, 0, 0, 2, 0, "south", 5), "cells directly beside the caster are unaffected");
});

Run("wider lines admit proportionally more lateral cells", () =>
{
    True(!SpellAreaGeometry.ContainsCell("line", 60, 0, 0, 1, 6, "south", 5), "a 5-foot line stays one column wide");
    True(SpellAreaGeometry.ContainsCell("line", 60, 0, 0, 1, 6, "south", 15), "a 15-foot line reaches one cell to the side");
    True(!SpellAreaGeometry.ContainsCell("line", 60, 0, 0, 2, 6, "south", 15), "a 15-foot line stops at one cell to the side");
});

Run("line enumeration matches per-cell containment", () =>
{
    var cells = SpellAreaGeometry.EnumerateCells("line", 60, 0, 0, "east", 5).ToList();
    True(cells.Count > 0, "a line should enumerate at least one cell");
    foreach (var (x, y) in cells)
        True(SpellAreaGeometry.ContainsCell("line", 60, 0, 0, x, y, "east", 5), $"enumerated cell ({x},{y}) must also be contained");
    True(cells.All(c => c.X > 0), "an east-facing line only extends east of the origin");
    True(cells.All(c => c.Y == 0), "a 5-foot east-facing line stays on one row");
});

// ---------------------------------------------------------------------------
// Lightning Bolt: the first line-shaped area spell in the engine
// ---------------------------------------------------------------------------

Run("Lightning Bolt damages every creature along the line", () =>
{
    var f = LineFixture();
    // Near target is 3 cells away, far target is 18 cells away: both inside a 100-foot line.
    var nearBefore = f.Near.CurrentHp;
    var farBefore = f.Far.CurrentHp;
    var offBefore = f.OffAxis.CurrentHp;

    var result = f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, "spell.lightning_bolt", MinimumDice(),
        direction: "south", slotLevel: 3, encounterId: f.Encounter.Id);

    Equal(2, result.TargetResults?.Count, "both on-axis creatures should be caught in the line");
    True(f.Near.CurrentHp < nearBefore, "the near creature takes Lightning damage");
    True(f.Far.CurrentHp < farBefore, "the far creature takes Lightning damage");
    Equal(offBefore, f.OffAxis.CurrentHp, "a creature beside the line is untouched");
});

Run("Lightning Bolt halves damage on a successful save", () =>
{
    var f = LineFixture();
    var spell = f.Campaign.Spells.Single(s => s.Key == "spell.lightning_bolt");
    // Save DC is 8 + proficiency 3 + Wisdom modifier 5 = 16. With a maximum d20 the
    // Dexterous creature clears it and the clumsy one cannot, while damage dice stay
    // at their minimum so 8d6 is a predictable 8 Lightning damage.
    f.Near.Abilities["dexterity"] = 20;  // +5, saves on a 20 for a total of 25
    f.Far.Abilities["dexterity"] = 1;    // -5, totals 15 and still fails
    var nearBefore = f.Near.CurrentHp;
    var farBefore = f.Far.CurrentHp;

    f.Engine.CastAreaSpell(f.Campaign, f.Caster.Id, spell.Id, MaxSaveDice(),
        direction: "south", slotLevel: 3, encounterId: f.Encounter.Id);

    var nearLoss = nearBefore - f.Near.CurrentHp;
    var farLoss = farBefore - f.Far.CurrentHp;
    Equal(8, farLoss, "a failed save takes the full minimum 8d6 total");
    Equal(4, nearLoss, "a successful save takes exactly half");
});

// ---------------------------------------------------------------------------
// Multi-target healing
// ---------------------------------------------------------------------------

Run("Mass Cure Wounds heals every target from one shared roll", () =>
{
    var f = HealFixture();
    foreach (var ally in f.Allies) ally.CurrentHp = 1;

    var result = f.Engine.CastMultiTargetHealingSpell(f.Campaign, f.Caster.Id, "spell.mass_cure_wounds",
        MinimumDice(), f.Allies.Select(a => a.Id).ToList(), slotLevel: 5, encounterId: f.Encounter.Id);

    Equal(f.Allies.Count, result.TargetResults?.Count, "every requested target resolves");
    var amounts = result.TargetResults!.Select(r => r.Healing).Distinct().ToList();
    Equal(1, amounts.Count, "SRD wording heals each target the same amount from one roll");
    True(amounts[0] > 0, "healing must actually restore Hit Points");
    foreach (var ally in f.Allies) Equal(1 + amounts[0], ally.CurrentHp, $"{ally.Name} should gain the shared healing total");
});

Run("Mass Cure Wounds target cap does not increase when upcast", () =>
{
    var f = HealFixture();
    var spell = f.Campaign.Spells.Single(s => s.Key == "spell.mass_cure_wounds");
    Equal(6, spell.BaseTargets, "SRD 5.2.1 allows up to six creatures");
    Equal(0, spell.ExtraTargetsPerSlot, "upcasting Mass Cure Wounds adds dice, never targets");
});

Run("multi-target healing rejects more targets than the spell allows", () =>
{
    var f = HealFixture();
    var spell = f.Campaign.Spells.Single(s => s.Key == "spell.mass_cure_wounds");
    var tooMany = f.Allies.Select(a => a.Id).ToList();
    while (tooMany.Count <= spell.BaseTargets)
    {
        var extra = new CharacterSheet
        {
            Id = $"filler-{tooMany.Count}",
            Name = $"Filler {tooMany.Count}",
            CharacterType = "npc",
            MaxHp = 20,
            CurrentHp = 5
        };
        f.Campaign.Characters.Add(extra);
        tooMany.Add(extra.Id);
    }

    var rejected = false;
    try
    {
        f.Engine.CastMultiTargetHealingSpell(f.Campaign, f.Caster.Id, spell.Id, MinimumDice(),
            tooMany, slotLevel: 5, encounterId: f.Encounter.Id);
    }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "exceeding the target cap must be refused");
    Equal(1, f.Caster.SpellSlots[5].Remaining, "a refused cast must not spend the spell slot");
});

Run("Mass Healing Word costs a Bonus Action and only a Verbal component", () =>
{
    var f = HealFixture();
    var spell = f.Campaign.Spells.Single(s => s.Key == "spell.mass_healing_word");
    Equal("Bonus Action", spell.CastingTime, "SRD 5.2.1 casts Mass Healing Word as a Bonus Action");
    True(spell.RequiresVerbal, "Mass Healing Word has a Verbal component");
    True(!spell.RequiresSomatic, "Mass Healing Word has no Somatic component");
    True(!spell.RequiresMaterial, "Mass Healing Word has no Material component");

    foreach (var ally in f.Allies) ally.CurrentHp = 1;
    var result = f.Engine.CastMultiTargetHealingSpell(f.Campaign, f.Caster.Id, spell.Id, MinimumDice(),
        f.Allies.Take(3).Select(a => a.Id).ToList(), slotLevel: 3, encounterId: f.Encounter.Id);
    Equal(3, result.TargetResults?.Count, "three chosen targets should each be healed");
});

// ---------------------------------------------------------------------------
// Heal: flat healing plus condition removal
// ---------------------------------------------------------------------------

Run("Heal restores a flat 70 Hit Points and ends three conditions", () =>
{
    var f = HealFixture();
    var target = f.Allies[0];
    target.MaxHp = 200;
    target.CurrentHp = 10;
    target.Conditions.Add("Blinded");
    target.Conditions.Add("Deafened");
    target.Conditions.Add("Poisoned");
    target.Conditions.Add("Prone"); // untouched by Heal

    var result = f.Engine.CastSpell(f.Campaign, f.Caster.Id, "spell.heal", MinimumDice(),
        targetId: target.Id, slotLevel: 6, encounterId: f.Encounter.Id);

    Equal(80, target.CurrentHp, "Heal restores exactly 70 Hit Points with no dice and no ability modifier");
    True(!target.Conditions.Contains("Blinded"), "Heal ends the Blinded condition");
    True(!target.Conditions.Contains("Deafened"), "Heal ends the Deafened condition");
    True(!target.Conditions.Contains("Poisoned"), "Heal ends the Poisoned condition");
    True(target.Conditions.Contains("Prone"), "Heal does not end unrelated conditions");
    True(result.Summary.Contains("Blinded", StringComparison.Ordinal), "the cast summary should report the conditions it ended");
});

Run("Heal upcasts by a flat 10 Hit Points per slot level", () =>
{
    var f = HealFixture();
    var target = f.Allies[0];
    target.MaxHp = 300;
    target.CurrentHp = 10;

    f.Engine.CastSpell(f.Campaign, f.Caster.Id, "spell.heal", MinimumDice(),
        targetId: target.Id, slotLevel: 8, encounterId: f.Encounter.Id);
    Equal(100, target.CurrentHp, "a level 8 Heal restores 70 + 10 + 10 Hit Points");
});

// ---------------------------------------------------------------------------
// Catalog contract for all eight newly supported spells
// ---------------------------------------------------------------------------

Run("the shipped SRD catalog carries the verified mechanics for every new spell", () =>
{
    var catalog = LoadCatalog();

    Check(catalog, "spell.lightning_bolt", s =>
    {
        Equal(3, s.Level, "Lightning Bolt is level 3");
        Equal("area_save", s.Resolution, "Lightning Bolt resolution");
        Equal("line", s.AreaShape, "Lightning Bolt is a Line");
        Equal(100, s.AreaSizeFeet, "Lightning Bolt is 100 feet long");
        Equal(5, s.AreaWidthFeet, "Lightning Bolt is 5 feet wide");
        Equal("self", s.AreaOrigin, "Lightning Bolt originates on the caster");
        Equal("dexterity", s.SaveAbility, "Lightning Bolt uses a Dexterity save");
        Equal("8d6", s.DamageExpression, "Lightning Bolt damage");
        Equal("Lightning", s.DamageType, "Lightning Bolt damage type");
        Equal("1d6", s.ExtraDamagePerSlotExpression, "Lightning Bolt upcast");
        True(s.HalfDamageOnSuccessfulSave, "Lightning Bolt halves on a save");
        Equal(144, s.SourcePage, "Lightning Bolt page provenance");
    });

    Check(catalog, "spell.cone_of_cold", s =>
    {
        Equal(5, s.Level, "Cone of Cold is level 5");
        Equal("area_save", s.Resolution, "Cone of Cold resolution");
        Equal("cone", s.AreaShape, "Cone of Cold is a Cone");
        Equal(60, s.AreaSizeFeet, "Cone of Cold is 60 feet");
        Equal("constitution", s.SaveAbility, "Cone of Cold uses a Constitution save");
        Equal("8d8", s.DamageExpression, "Cone of Cold damage");
        Equal("Cold", s.DamageType, "Cone of Cold damage type");
        Equal("1d8", s.ExtraDamagePerSlotExpression, "Cone of Cold upcast");
        True(s.EnvironmentalEffect.Contains("frozen statue", StringComparison.OrdinalIgnoreCase), "Cone of Cold records the frozen statue clause");
        Equal(117, s.SourcePage, "Cone of Cold page provenance");
    });

    Check(catalog, "spell.circle_of_death", s =>
    {
        Equal(6, s.Level, "Circle of Death is level 6");
        Equal("sphere", s.AreaShape, "Circle of Death is a Sphere");
        Equal(60, s.AreaSizeFeet, "Circle of Death has a 60-foot radius");
        Equal("point", s.AreaOrigin, "Circle of Death originates at a chosen point");
        Equal("8d8", s.DamageExpression, "Circle of Death damage");
        Equal("Necrotic", s.DamageType, "Circle of Death damage type");
        Equal("2d8", s.ExtraDamagePerSlotExpression, "Circle of Death upcasts by 2d8, not 1d8");
        Equal(115, s.SourcePage, "Circle of Death page provenance");
    });

    Check(catalog, "spell.inflict_wounds", s =>
    {
        Equal(1, s.Level, "Inflict Wounds is level 1");
        Equal("save", s.Resolution, "Inflict Wounds is a single-target save, not an attack roll");
        Equal("touch", s.RangeKind, "Inflict Wounds has Touch range");
        Equal("constitution", s.SaveAbility, "Inflict Wounds uses a Constitution save");
        Equal("2d10", s.DamageExpression, "Inflict Wounds damage");
        Equal("Necrotic", s.DamageType, "Inflict Wounds damage type");
        Equal("1d10", s.ExtraDamagePerSlotExpression, "Inflict Wounds upcast");
        True(s.HalfDamageOnSuccessfulSave, "Inflict Wounds halves on a save");
        Equal(143, s.SourcePage, "Inflict Wounds page provenance");
    });

    Check(catalog, "spell.longstrider", s =>
    {
        Equal(1, s.Level, "Longstrider is level 1");
        Equal("multi_buff", s.Resolution, "Longstrider resolution");
        Equal(10, s.SpeedModifierFeet, "Longstrider grants +10 feet of Speed");
        Equal(1, s.BaseTargets, "Longstrider targets one creature at its base level");
        Equal(1, s.ExtraTargetsPerSlot, "Longstrider adds one target per higher slot level");
        Equal("1 hour", s.Duration, "Longstrider lasts 1 hour");
        True(!s.RequiresConcentration, "Longstrider does not require Concentration");
        Equal(145, s.SourcePage, "Longstrider page provenance");
    });

    Check(catalog, "spell.mass_cure_wounds", s =>
    {
        Equal(5, s.Level, "Mass Cure Wounds is level 5");
        Equal("Abjuration", s.School, "Mass Cure Wounds is Abjuration in SRD 5.2.1");
        Equal("multi_heal", s.Resolution, "Mass Cure Wounds resolution");
        Equal("5d8", s.HealingExpression, "Mass Cure Wounds healing");
        Equal("1d8", s.ExtraHealingPerSlotExpression, "Mass Cure Wounds upcast");
        True(s.AddSpellcastingAbilityModifierToHealing, "Mass Cure Wounds adds the spellcasting modifier");
        Equal(6, s.BaseTargets, "Mass Cure Wounds heals up to six creatures");
        Equal(0, s.ExtraTargetsPerSlot, "Mass Cure Wounds never gains targets when upcast");
        Equal(30, s.AreaSizeFeet, "Mass Cure Wounds targets a 30-foot-radius Sphere");
        Equal(147, s.SourcePage, "Mass Cure Wounds page provenance");
    });

    Check(catalog, "spell.mass_healing_word", s =>
    {
        Equal(3, s.Level, "Mass Healing Word is level 3");
        Equal("Abjuration", s.School, "Mass Healing Word is Abjuration in SRD 5.2.1");
        Equal("multi_heal", s.Resolution, "Mass Healing Word resolution");
        Equal("2d4", s.HealingExpression, "Mass Healing Word healing");
        Equal("1d4", s.ExtraHealingPerSlotExpression, "Mass Healing Word upcast");
        Equal(6, s.BaseTargets, "Mass Healing Word heals up to six creatures");
        Equal(0, s.ExtraTargetsPerSlot, "Mass Healing Word never gains targets when upcast");
        Equal("Bonus Action", s.CastingTime, "Mass Healing Word is a Bonus Action");
        Equal(148, s.SourcePage, "Mass Healing Word page provenance");
    });

    Check(catalog, "spell.heal", s =>
    {
        Equal(6, s.Level, "Heal is level 6");
        Equal("healing", s.Resolution, "Heal resolution");
        Equal("70", s.HealingExpression, "Heal restores a flat 70 Hit Points");
        Equal("10", s.ExtraHealingPerSlotExpression, "Heal upcasts by a flat 10");
        True(!s.AddSpellcastingAbilityModifierToHealing, "Heal does not add the spellcasting modifier");
        Equal("Blinded,Deafened,Poisoned", s.ConditionsEndedOnTarget, "Heal ends exactly the three SRD conditions");
        Equal(139, s.SourcePage, "Heal page provenance");
    });
});

Run("no newly supported spell is left marked unsupported", () =>
{
    var catalog = LoadCatalog();
    foreach (var key in NewSpellKeys())
    {
        var spell = catalog.FirstOrDefault(s => s.Key == key) ?? throw new Exception($"{key} missing from catalog");
        True(spell.Resolution != "unsupported", $"{key} should now have a deterministic resolution");
        Equal("srd_5_2_1", spell.SourceKind, $"{key} must keep its SRD provenance");
        True(spell.SourcePage > 0, $"{key} must keep a real page number");
    }
});

Run("spells whose riders the engine cannot enforce stay unsupported", () =>
{
    // Darkness, Silence, Freezing Sphere, and Prayer of Healing all depend on rules
    // the engine does not model (light levels, component suppression, deferred
    // globes, rest benefits). They must not be half-implemented.
    var catalog = LoadCatalog();
    foreach (var key in new[] { "spell.darkness", "spell.silence", "spell.freezing_sphere", "spell.prayer_of_healing" })
    {
        var spell = catalog.FirstOrDefault(s => s.Key == key) ?? throw new Exception($"{key} missing from catalog");
        Equal("unsupported", spell.Resolution, $"{key} must stay unsupported until its full rider can be enforced");
    }
});

Run("catalog totals and licensing metadata are unchanged", () =>
{
    var catalog = LoadCatalog();
    Equal(316, catalog.Count, "the SRD catalog still holds every parsed spell");
    var supported = catalog.Count(s => s.Resolution != "unsupported");
    Equal(26, supported, "r58 raises deterministic spell coverage from 18 to 26");
});

// ---------------------------------------------------------------------------
// Reporting
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failures.Count} failed");
if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"  {failure}");
    return 1;
}
return 0;

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

static void True(bool value, string label)
{
    if (!value) throw new Exception(label);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

static void Check(IReadOnlyList<SpellDefinition> catalog, string key, Action<SpellDefinition> assert)
{
    var spell = catalog.FirstOrDefault(s => s.Key == key) ?? throw new Exception($"{key} missing from the SRD catalog");
    assert(spell);
}

static string[] NewSpellKeys() =>
[
    "spell.lightning_bolt", "spell.cone_of_cold", "spell.circle_of_death", "spell.inflict_wounds",
    "spell.longstrider", "spell.mass_cure_wounds", "spell.mass_healing_word", "spell.heal"
];

static DiceService MinimumDice() => new((min, max) => min);

// Maximizes only d20 rolls so saving throws succeed while damage dice stay minimal
// and therefore predictable. The callback follows Random.Next semantics, so a d20
// arrives as an exclusive upper bound of 21.
static DiceService MaxSaveDice() => new((min, max) => max == 21 ? max - 1 : min);

static IReadOnlyList<SpellDefinition> LoadCatalog()
{
    var path = FindCatalogPath();
    var service = new SrdSpellCatalogService();
    service.LoadAsync(path).GetAwaiter().GetResult();
    return service.Spells;
}

static string FindCatalogPath()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 12 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "content", "Rules", "srd_spells.json");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    throw new FileNotFoundException("Could not locate srd_spells.json from the test output directory.");
}

// Fixture with a caster at (0,0) plus two creatures south along +Y and one beside the line.
static LineFixtureState LineFixture()
{
    var engine = new GameEngine();
    var catalog = LoadCatalog();
    var caster = NewCaster("bolt-caster", "Storm Caller");
    var near = NewTarget("bolt-near", "Near Sentry");
    var far = NewTarget("bolt-far", "Far Sentry");
    var offAxis = NewTarget("bolt-off", "Flanking Sentry");

    var campaign = new CampaignState
    {
        Id = "r58-line-campaign",
        Name = "R58 Line Campaign",
        Characters = [caster, near, far, offAxis],
        Spells = [.. catalog]
    };
    foreach (var spell in campaign.Spells) caster.PreparedSpellIds.Add(spell.Id);

    var encounter = engine.StartEncounter(campaign, "R58 Line Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var nearCombatant = engine.AddCombatant(campaign, encounter.Id, near.Id, side: "opposition");
    var farCombatant = engine.AddCombatant(campaign, encounter.Id, far.Id, side: "opposition");
    var offCombatant = engine.AddCombatant(campaign, encounter.Id, offAxis.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, nearCombatant.Id, 0, 3);
    engine.SetCombatantPosition(campaign, encounter.Id, farCombatant.Id, 0, 18);
    engine.SetCombatantPosition(campaign, encounter.Id, offCombatant.Id, 4, 8);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 30);
    engine.SetInitiative(campaign, encounter.Id, nearCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, farCombatant.Id, 15);
    engine.SetInitiative(campaign, encounter.Id, offCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);

    return new LineFixtureState(engine, campaign, caster, near, far, offAxis, encounter);
}

// Fixture with a healer and four wounded allies within 60 feet.
static HealFixtureState HealFixture()
{
    var engine = new GameEngine();
    var catalog = LoadCatalog();
    var caster = NewCaster("heal-caster", "Field Cleric");
    var allies = new List<CharacterSheet>
    {
        NewTarget("heal-ally-one", "Ally One"),
        NewTarget("heal-ally-two", "Ally Two"),
        NewTarget("heal-ally-three", "Ally Three"),
        NewTarget("heal-ally-four", "Ally Four")
    };

    var characters = new List<CharacterSheet> { caster };
    characters.AddRange(allies);
    var campaign = new CampaignState
    {
        Id = "r58-heal-campaign",
        Name = "R58 Heal Campaign",
        Characters = characters,
        Spells = [.. catalog]
    };
    foreach (var spell in campaign.Spells) caster.PreparedSpellIds.Add(spell.Id);

    var encounter = engine.StartEncounter(campaign, "R58 Heal Encounter");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    engine.SetCombatantPosition(campaign, encounter.Id, casterCombatant.Id, 0, 0);
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 30);
    var initiative = 20;
    var offset = 1;
    foreach (var ally in allies)
    {
        var combatant = engine.AddCombatant(campaign, encounter.Id, ally.Id, side: "party");
        engine.SetCombatantPosition(campaign, encounter.Id, combatant.Id, offset++, 1);
        engine.SetInitiative(campaign, encounter.Id, combatant.Id, initiative--);
    }
    engine.FinalizeInitiative(campaign, encounter.Id);

    return new HealFixtureState(engine, campaign, caster, allies, encounter);
}

static CharacterSheet NewCaster(string id, string name) => new()
{
    Id = id,
    Name = name,
    CharacterType = "npc",
    MaxHp = 60,
    CurrentHp = 60,
    ArmorClass = 14,
    ProficiencyBonus = 3,
    SpellcastingAbility = "wisdom",
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 10,
        ["dexterity"] = 12,
        ["constitution"] = 12,
        ["intelligence"] = 12,
        ["wisdom"] = 20,
        ["charisma"] = 10
    },
    SpellSlots = new Dictionary<int, SpellSlotPool>
    {
        [1] = new SpellSlotPool { Maximum = 1, Remaining = 1 },
        [3] = new SpellSlotPool { Maximum = 1, Remaining = 1 },
        [5] = new SpellSlotPool { Maximum = 1, Remaining = 1 },
        [6] = new SpellSlotPool { Maximum = 1, Remaining = 1 },
        [8] = new SpellSlotPool { Maximum = 1, Remaining = 1 }
    }
};

static CharacterSheet NewTarget(string id, string name) => new()
{
    Id = id,
    Name = name,
    CharacterType = "npc",
    MaxHp = 120,
    CurrentHp = 120,
    ArmorClass = 13,
    ProficiencyBonus = 2,
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

sealed record LineFixtureState(
    GameEngine Engine,
    CampaignState Campaign,
    CharacterSheet Caster,
    CharacterSheet Near,
    CharacterSheet Far,
    CharacterSheet OffAxis,
    EncounterState Encounter);

sealed record HealFixtureState(
    GameEngine Engine,
    CampaignState Campaign,
    CharacterSheet Caster,
    List<CharacterSheet> Allies,
    EncounterState Encounter);
