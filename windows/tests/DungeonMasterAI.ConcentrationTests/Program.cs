using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player damage creates a required Concentration roll without auto-resolving it", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    var damage = engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);

    Equal(12, pc.CurrentHp, "HP after damage");
    True(damage.Concentration is null, "PC Concentration should wait for the player roll");
    Equal("Bless", pc.ConcentrationEffect, "Concentration should remain until the save resolves");
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");
    Equal("concentration_check", pending.ResolutionKey, "resolution key");
    Equal(10, pending.TargetNumber, "Concentration DC");
    Equal(4, pending.Modifier, "static Constitution save modifier");
    True(pending.Required, "Concentration roll should be required");
});

Run("supplied player d20 is authoritative and can break Concentration", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");

    var result = engine.ResolvePendingConcentrationCheckRoll(campaign, pending.Id, 5, null, dice);
    Equal(5, result.SavingThrow.ChosenRoll, "authoritative Concentration d20");
    Equal(9, result.SavingThrow.Total, "Concentration total");
    True(!result.Maintained, "Concentration should fail at 9 vs DC 10");
    True(string.IsNullOrWhiteSpace(pc.ConcentrationEffect), "Concentration effect should end");
    True(campaign.PendingPlayerRoll is null, "pending Concentration roll should clear");
});

Run("supplied player d20 can maintain Concentration", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");

    var result = engine.ResolvePendingConcentrationCheckRoll(campaign, pending.Id, 15, null, dice);
    Equal(19, result.SavingThrow.Total, "Concentration total");
    True(result.Maintained, "Concentration should succeed");
    Equal("Bless", pc.ConcentrationEffect, "Concentration effect should remain");
    True(campaign.PendingPlayerRoll is null, "pending Concentration roll should clear");
});

Run("another required player roll blocks damage before state mutates", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    engine.RequestAbilityCheckRoll(campaign, pc.Id, "wisdom", 10);
    var beforeHp = pc.CurrentHp;
    var threw = false;
    try { engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "damage should be blocked while another required roll is unresolved");
    Equal(beforeHp, pc.CurrentHp, "HP must remain unchanged when damage is blocked");
    Equal("Bless", pc.ConcentrationEffect, "Concentration must remain unchanged when damage is blocked");
});

Run("NPC Concentration still resolves immediately", () =>
{
    var engine = new GameEngine();
    var npc = new CharacterSheet
    {
        Id = "npc-caster",
        Name = "Watcher Mage",
        CharacterType = "npc",
        MaxHp = 20,
        CurrentHp = 20,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 14 },
        SavingThrowProficiencies = ["constitution"]
    };
    var campaign = new CampaignState { Id = "campaign-npc-concentration", Name = "NPC Concentration", Characters = [npc] };
    engine.BeginConcentration(campaign, npc.Id, "Fog Cloud");
    var dice = new DiceService((min, max) => max - 1);

    var damage = engine.ApplyDamageWithConcentration(campaign, npc.Id, 8, dice);
    True(damage.Concentration is not null, "NPC Concentration should resolve immediately");
    True(damage.Concentration!.Maintained, "NPC high deterministic roll should maintain Concentration");
    True(campaign.PendingPlayerRoll is null, "NPC Concentration must not create a player roll");
});

Run("an unparseable healing expression is rejected before any cost is committed", () =>
{
    var (engine, campaign, caster, ally, encounter, casterCombatant, spell) = CreateBlessedHealerFixture();
    var dice = new DiceService((min, max) => min);

    var threw = false;
    try { engine.CastSpell(campaign, caster.Id, spell.Id, dice, ally.Id, 1); }
    catch (InvalidOperationException) { threw = true; }

    True(threw, "an unparseable healing expression must throw");
    Equal(3, caster.SpellSlots[1].Remaining, "the level 1 slot must not be spent");
    True(casterCombatant.ActionAvailable, "the Action must still be available");
    Equal("Bless", caster.ConcentrationEffect, "Bless must survive the failed cast");
    Equal(2, campaign.ActiveEffects.Count(e => e.ConcentrationName.Equals("Bless", StringComparison.OrdinalIgnoreCase)), "Bless must remain on all its targets");
});

Run("a required Concentration save carries its encounter and blocks the turn advance", () =>
{
    var (engine, campaign, pc, encounter, pcCombatant, npcCombatant) = CreateHazardTurnFixture();
    var dice = new DiceService((min, max) => min);
    engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");

    Equal(encounter.Id, pending.EncounterId, "the Concentration save must carry its encounter");
    Equal(pcCombatant.Id, pending.CombatantId, "the Concentration save must carry its combatant");
    var threw = false;
    try { engine.NextTurn(campaign, encounter.Id, dice); }
    catch (InvalidOperationException) { threw = true; }

    True(threw, "the turn must not advance while the Concentration save is outstanding");
    Equal(0, encounter.TurnIndex, "turn index must not advance");
    Equal(1, encounter.Round, "round must not advance");
});

Run("a turn-start hazard that cannot resolve leaves the turn exactly where it was", () =>
{
    var (engine, campaign, pc, encounter, pcCombatant, npcCombatant) = CreateHazardTurnFixture();
    var dice = new DiceService((min, max) => min);
    engine.RequestAbilityCheckRoll(campaign, pc.Id, "wisdom", 10);

    var threw = false;
    try { engine.NextTurn(campaign, encounter.Id, dice); }
    catch (InvalidOperationException) { threw = true; }

    True(threw, "the turn-start hazard must be pre-checked before the turn advances");
    Equal(0, encounter.TurnIndex, "turn index must not advance");
    Equal(1, encounter.Round, "round must not advance");
    True(npcCombatant.ActionAvailable, "the action economy must not be zeroed by the refused transition");
    True(campaign.PendingPlayerRoll?.Required == true, "the outstanding required roll must survive");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Concentration roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}

Console.WriteLine($"Concentration roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Pc) CreateConcentratingPc()
{
    var engine = new GameEngine();
    var pc = new CharacterSheet
    {
        Id = "pc-caster",
        Name = "Aric",
        CharacterType = "pc",
        MaxHp = 20,
        CurrentHp = 20,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
  ["constitution"] = 14,
  ["wisdom"] = 12
        },
        SavingThrowProficiencies = ["constitution"]
    };
    var campaign = new CampaignState { Id = "campaign-concentration", Name = "Concentration Campaign", Characters = [pc] };
    engine.BeginConcentration(campaign, pc.Id, "Bless");
    return (engine, campaign, pc);
}

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Caster, CharacterSheet Ally, EncounterState Encounter, CombatantState CasterCombatant, SpellDefinition Spell) CreateBlessedHealerFixture()
{
    var engine = new GameEngine();
    var caster = new CharacterSheet
    {
        Id = "pc-cleric",
        Name = "Mera",
        CharacterType = "pc",
        Level = 5,
        MaxHp = 30,
        CurrentHp = 30,
        ProficiencyBonus = 3,
        SpellcastingAbility = "wisdom",
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["constitution"] = 14,
            ["wisdom"] = 16
        }
    };
    caster.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
    var ally = new CharacterSheet
    {
        Id = "pc-fighter",
        Name = "Doran",
        CharacterType = "pc",
        Level = 5,
        MaxHp = 40,
        CurrentHp = 20,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 14 }
    };
    // The campaign file supplies the healing formula as raw text the dice parser rejects.
    var spell = new SpellDefinition
    {
        Id = "spell-mending-word",
        Key = "mending_word",
        Name = "Mending Word",
        Level = 1,
        CastingTime = "Action",
        RangeKind = "distance",
        RangeFeet = 60,
        RequiresTarget = true,
        RequiresConcentration = true,
        Resolution = "healing",
        HealingExpression = "2d8 + WIS"
    };
    caster.PreparedSpellIds.Add(spell.Id);
    var campaign = new CampaignState
    {
        Id = "campaign-concentration-casting",
        Name = "Concentration Casting Campaign",
        Characters = [caster, ally],
        Spells = [spell]
    };
    var encounter = engine.StartEncounter(campaign, "Chapel Skirmish");
    var casterCombatant = engine.AddCombatant(campaign, encounter.Id, caster.Id, side: "party");
    var allyCombatant = engine.AddCombatant(campaign, encounter.Id, ally.Id, side: "party");
    engine.SetInitiative(campaign, encounter.Id, casterCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, allyCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    engine.BeginConcentration(campaign, caster.Id, "Bless");
    foreach (var blessed in new[] { caster, ally })
        campaign.ActiveEffects.Add(new ActiveEffectState
        {
            Name = "Bless",
            SourceCharacterId = caster.Id,
            TargetCharacterId = blessed.Id,
            SourceSpellId = "spell-bless",
            ConcentrationName = "Bless",
            RequiresSourceConcentration = true,
            AttackRollBonusExpression = "1d4"
        });
    return (engine, campaign, caster, ally, encounter, casterCombatant, spell);
}

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Pc, EncounterState Encounter, CombatantState PcCombatant, CombatantState NpcCombatant) CreateHazardTurnFixture()
{
    var engine = new GameEngine();
    var pc = new CharacterSheet
    {
        Id = "pc-caster",
        Name = "Aric",
        CharacterType = "pc",
        MaxHp = 20,
        CurrentHp = 20,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["constitution"] = 14,
            ["wisdom"] = 12
        },
        SavingThrowProficiencies = ["constitution"]
    };
    var npc = new CharacterSheet
    {
        Id = "npc-brute",
        Name = "Ash Brute",
        CharacterType = "npc",
        MaxHp = 25,
        CurrentHp = 25
    };
    var campaign = new CampaignState { Id = "campaign-hazard-turn", Name = "Hazard Turn Campaign", Characters = [pc, npc] };
    var encounter = engine.StartEncounter(campaign, "Burning Chapel");
    var npcCombatant = engine.AddCombatant(campaign, encounter.Id, npc.Id, side: "opposition");
    var pcCombatant = engine.AddCombatant(campaign, encounter.Id, pc.Id, side: "party");
    engine.SetInitiative(campaign, encounter.Id, npcCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, pcCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    engine.AddBattlefieldEffect(campaign, encounter.Id, new BattlefieldEffectState
    {
        Name = "Burning Floor",
        Shape = "sphere",
        SizeFeet = 5,
        OriginX = pcCombatant.GridX,
        OriginY = pcCombatant.GridY,
        Trigger = "start_or_enter",
        DamageExpression = "2d6",
        DamageType = "Fire"
    });
    engine.BeginConcentration(campaign, pc.Id, "Bless");
    return (engine, campaign, pc, encounter, pcCombatant, npcCombatant);
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
