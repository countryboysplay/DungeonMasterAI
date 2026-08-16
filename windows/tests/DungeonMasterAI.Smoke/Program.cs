using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("SMOKE TEST FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}

var engine = new GameEngine();
var dice = new DiceService((minimumInclusive, maximumExclusive) =>
{
    var span = maximumExclusive - minimumInclusive;
    return minimumInclusive + Math.Max(0, (span - 1) / 2);
});
var campaign = engine.CreateCampaign("Smoke Test");
Assert(campaign.Locations.Count == 1 && campaign.Locations[0].Discovered, "new campaign has a discovered starting area");

for (var i = 0; i < 100; i++)
{
    var roll = dice.Roll("1d20+2");
    Assert(roll.Total is >= 3 and <= 22, "d20 roll remains in valid range");
}

try { dice.Roll("9999999999d6"); Assert(false, "oversized dice count is rejected"); }
catch (ArgumentOutOfRangeException) { Assert(true, "oversized dice count is rejected as an argument error, not an overflow"); }
try { dice.Roll("1d99999999999"); Assert(false, "oversized die sides are rejected"); }
catch (ArgumentOutOfRangeException) { Assert(true, "oversized die sides are rejected as an argument error, not an overflow"); }
try { dice.Roll("99999999999999"); Assert(false, "oversized fixed roll is rejected"); }
catch (ArgumentOutOfRangeException) { Assert(true, "oversized fixed roll is rejected as an argument error, not an overflow"); }
try { dice.Roll("2d8+99999999999"); Assert(false, "oversized modifier is rejected"); }
catch (ArgumentOutOfRangeException) { Assert(true, "oversized modifier is rejected as an argument error, not an overflow"); }
Assert(dice.RollDamage("60d6", critical: true) > 0, "a critical hit can double a legal damage expression above 50 dice without throwing");
try { dice.Roll("120d6"); Assert(false, "direct rolls above 100 dice are still rejected"); }
catch (ArgumentOutOfRangeException) { Assert(true, "direct rolls above 100 dice are still rejected"); }

var hero = engine.AddCharacter(campaign, new CharacterSheet { Name = "Test Hero", CharacterType = "pc", MaxHp = 12, CurrentHp = 12, ArmorClass = 15, Gold = 30, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["strength"] = 16 }, Attacks = [new AttackProfile { Name = "Longsword", AttackBonus = 5, DamageExpression = "1d8+3", DamageType = "Slashing" }] });
engine.ApplyDamage(campaign, hero.Id, 5);
Assert(hero.CurrentHp == 7, "damage changes deterministic HP");
engine.Heal(campaign, hero.Id, 3);
Assert(hero.CurrentHp == 10, "healing changes deterministic HP");
engine.AdvanceTime(campaign, 1500);
Assert(campaign.Day == 2 && campaign.MinuteOfDay == 540, "campaign clock crosses day boundary correctly");

hero.Abilities["constitution"] = 14;
hero.Abilities["wisdom"] = 12;
hero.SkillProficiencies.Add("perception");
hero.ProficiencyBonus = 2;
var check = engine.ResolveAbilityCheck(campaign, hero.Id, "wisdom", 14, 15, skill: "perception");
Assert(check.Success && check.Total == 18, "ability checks apply ability and skill proficiency modifiers");
engine.SetExhaustion(campaign, hero.Id, 2);
var exhaustedCheck = engine.ResolveAbilityCheck(campaign, hero.Id, "wisdom", 15, 15, skill: "perception");
Assert(!exhaustedCheck.Success && exhaustedCheck.Total == 14 && exhaustedCheck.ExhaustionPenalty == 4, "Exhaustion reduces D20 Tests by twice the Exhaustion level");


var resilient = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Resilient Hero", CharacterType = "pc", MaxHp = 20, CurrentHp = 20, TempHp = 5,
    DamageResistances = ["Fire"], DamageVulnerabilities = ["Cold"], HitDiceMaximum = 2, HitDiceRemaining = 1, HitDieSides = 10
});
var resisted = engine.ApplyDamageDetailed(campaign, resilient.Id, 11, "Fire");
Assert(resisted.EffectiveDamage == 5 && resilient.TempHp == 0 && resilient.CurrentHp == 20, "Resistance halves damage before Temporary HP is consumed");
var vulnerable = engine.ApplyDamageDetailed(campaign, resilient.Id, 6, "Cold");
Assert(vulnerable.EffectiveDamage == 12 && resilient.CurrentHp == 8, "Vulnerability doubles typed damage");
engine.ApplyDamageDetailed(campaign, resilient.Id, 8, "Slashing");
Assert(resilient.CurrentHp == 0 && resilient.Conditions.Contains("Unconscious") && resilient.Conditions.Contains("Prone") && !resilient.Dead, "a PC reduced to 0 HP becomes Unconscious and Prone rather than automatically dying");
var deathOne = engine.ResolveDeathSavingThrow(campaign, resilient.Id, 1);
Assert(deathOne.Failures == 2 && !deathOne.Dead, "natural 1 on a Death Saving Throw causes two failures");
var deathSuccess = engine.ResolveDeathSavingThrow(campaign, resilient.Id, 20);
Assert(deathSuccess.CurrentHp == 1 && resilient.DeathSaveFailures == 0 && !resilient.Conditions.Contains("Unconscious") && resilient.Conditions.Contains("Prone"), "natural 20 on a Death Saving Throw restores 1 HP and resets death saves while the creature remains Prone");
resilient.ExhaustionLevel = 2;
resilient.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 1 };
resilient.Resources.Add(new ResourcePool { Name = "Test Feature", Maximum = 2, Remaining = 0, RechargeOnLongRest = true });
var longRest = engine.LongRest(campaign, resilient.Id);
Assert(resilient.CurrentHp == resilient.MaxHp && resilient.ExhaustionLevel == 1 && resilient.SpellSlots[1].Remaining == 3 && resilient.Resources[0].Remaining == 2, "Long Rest restores HP, slots and long-rest resources and reduces Exhaustion");
Assert(longRest.Minutes == 480, "Long Rest advances campaign time by eight hours");

var concentrationHero = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Focused Mage", CharacterType = "pc", MaxHp = 40, CurrentHp = 40,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 30 },
    SavingThrowProficiencies = ["constitution"]
});
engine.BeginConcentration(campaign, concentrationHero.Id, "Test Ward");
Assert(concentrationHero.ConcentrationEffect == "Test Ward", "a character can begin Concentration on one named effect");
engine.BeginConcentration(campaign, concentrationHero.Id, "Second Test Ward");
Assert(concentrationHero.ConcentrationEffect == "Second Test Ward", "starting a new Concentration effect replaces the previous effect");
var concentrationBypassRejected = false;
try { engine.ApplyDamageDetailed(campaign, concentrationHero.Id, 1, "Force"); }
catch (InvalidOperationException) { concentrationBypassRejected = true; }
Assert(concentrationBypassRejected, "the engine rejects damage paths that would bypass a required Concentration save");
var maintainedConcentration = engine.ApplyDamageWithConcentration(campaign, concentrationHero.Id, 2, dice, "Force");
Assert(maintainedConcentration.Concentration is { Maintained: true, DifficultyClass: 10 } && concentrationHero.ConcentrationEffect == "Second Test Ward", "damage triggers an automatic Constitution save and preserves Concentration on success");
engine.AddCondition(campaign, concentrationHero.Id, "Incapacitated");
Assert(concentrationHero.ConcentrationEffect is null, "becoming Incapacitated automatically ends Concentration");

var fragileConcentrator = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Fragile Concentrator", CharacterType = "npc", MaxHp = 200, CurrentHp = 200, ExhaustionLevel = 5,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 1 }
});
engine.BeginConcentration(campaign, fragileConcentrator.Id, "Test Barrier");
var lostConcentration = engine.ApplyDamageWithConcentration(campaign, fragileConcentrator.Id, 100, dice, "Force");
Assert(lostConcentration.Concentration is { Maintained: false, DifficultyClass: 30 } && fragileConcentrator.ConcentrationEffect is null, "Concentration damage DC is capped at 30 and a failed save ends the effect");

var spellTester = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Spell Tester", CharacterType = "pc", MaxHp = 18, CurrentHp = 10, ArmorClass = 12,
    SpellcastingAbility = "intelligence", ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 16, ["constitution"] = 12 }
});
spellTester.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
spellTester.SpellSlots[2] = new SpellSlotPool { Maximum = 2, Remaining = 2 };
var practiceMend = new SpellDefinition
{
    Key = "spell.practice_mend", Name = "Practice Mend", Level = 1, Resolution = "healing", RequiresTarget = true,
    HealingExpression = "4", ExtraHealingPerSlotExpression = "2", RequiresVerbal = true, RequiresSomatic = true
};
var focusWard = new SpellDefinition
{
    Key = "spell.focus_ward", Name = "Focus Ward", Level = 1, Resolution = "utility", RequiresConcentration = true,
    RequiresVerbal = true
};
var studyRite = new SpellDefinition
{
    Key = "spell.study_rite", Name = "Study Rite", Level = 1, Resolution = "utility", Ritual = true, CastingTime = "Action"
};
var practiceRay = new SpellDefinition
{
    Key = "spell.practice_ray", Name = "Practice Ray", Level = 0, Resolution = "attack", RequiresTarget = true,
    DamageExpression = "1", DamageType = "Force", RangeKind = "distance", RangeFeet = 30
};
var practiceReaction = new SpellDefinition
{
    Key = "spell.practice_reaction", Name = "Practice Reaction", Level = 0, Resolution = "utility", CastingTime = "Reaction"
};
var practiceBonus = new SpellDefinition
{
    Key = "spell.practice_bonus", Name = "Practice Bonus", Level = 0, Resolution = "utility", CastingTime = "Bonus Action"
};
var practiceBonusSlot = new SpellDefinition
{
    Key = "spell.practice_bonus_slot", Name = "Practice Bonus Slot", Level = 1, Resolution = "utility", CastingTime = "Bonus Action"
};
var catalogOnlySpell = new SpellDefinition
{
    Key = "spell.catalog_only", Name = "Catalog Only Spell", Level = 1, Resolution = "unsupported", SourceKind = "srd_5_2_1"
};
campaign.Spells.AddRange([practiceMend, focusWard, studyRite, practiceRay, practiceReaction, practiceBonus, practiceBonusSlot, catalogOnlySpell]);
spellTester.PreparedSpellIds.AddRange([practiceMend.Id, focusWard.Id, studyRite.Id, practiceRay.Id, practiceReaction.Id, practiceBonus.Id, practiceBonusSlot.Id, catalogOnlySpell.Id]);
Assert(engine.SpellSaveDc(spellTester) == 13 && engine.SpellAttackModifier(spellTester) == 5, "spell save DC and spell attack modifier use spellcasting ability plus Proficiency Bonus");
var healedBySpell = engine.CastSpell(campaign, spellTester.Id, practiceMend.Id, dice, spellTester.Id, slotLevel: 2);
Assert(healedBySpell.UsedSpellSlot && healedBySpell.CastAtLevel == 2 && healedBySpell.Healing == 6 && spellTester.CurrentHp == 16 && spellTester.SpellSlots[2].Remaining == 1, "leveled healing spells spend the selected slot and apply deterministic upcast healing");
var beforeRitualMinute = campaign.MinuteOfDay;
var beforeRitualDay = campaign.Day;
var ritualCast = engine.CastSpell(campaign, spellTester.Id, studyRite.Id, dice, asRitual: true);
var ritualElapsed = ((campaign.Day - beforeRitualDay) * 1440) + campaign.MinuteOfDay - beforeRitualMinute;
Assert(ritualCast.Ritual && !ritualCast.UsedSpellSlot && ritualElapsed == 10, "Ritual casting adds ten minutes and does not expend a spell slot");
var concentrationCast = engine.CastSpell(campaign, spellTester.Id, focusWard.Id, dice, slotLevel: 1);
Assert(concentrationCast.ConcentrationStarted && spellTester.ConcentrationEffect == "Focus Ward", "configured Concentration spells start deterministic Concentration state");
engine.EndConcentration(campaign, spellTester.Id);
var cantripSlotsBefore = spellTester.SpellSlots[1].Remaining + spellTester.SpellSlots[2].Remaining;
var rayTarget = engine.AddCharacter(campaign, new CharacterSheet { Name = "Ray Target", CharacterType = "monster", MaxHp = 20, CurrentHp = 20, ArmorClass = 100 });
var ray = engine.CastSpell(campaign, spellTester.Id, practiceRay.Id, dice, rayTarget.Id);
Assert(!ray.UsedSpellSlot && spellTester.SpellSlots[1].Remaining + spellTester.SpellSlots[2].Remaining == cantripSlotsBefore, "cantrips cast without expending a spell slot");
spellTester.CanProvideVerbalComponents = false;
var componentRejected = false;
try { engine.CastSpell(campaign, spellTester.Id, focusWard.Id, dice, slotLevel: 1); }
catch (InvalidOperationException) { componentRejected = true; }
Assert(componentRejected, "spellcasting rejects a spell when the caster cannot provide a required component");
spellTester.CanProvideVerbalComponents = true;
var slotBeforeUnsupported = spellTester.SpellSlots[1].Remaining;
var unsupportedRejected = false;
try { engine.CastSpell(campaign, spellTester.Id, catalogOnlySpell.Id, dice, slotLevel: 1); }
catch (InvalidOperationException) { unsupportedRejected = true; }
Assert(unsupportedRejected && spellTester.SpellSlots[1].Remaining == slotBeforeUnsupported, "unsupported rules-catalog spells are rejected before spending a spell slot");

// Source-verified spell primitives used by the SRD catalog implementations.
var spellModifierHealing = new SpellDefinition
{
    Key = "spell.test_modifier_heal", Name = "Modifier Heal", Level = 1, Resolution = "healing", RequiresTarget = true,
    HealingExpression = "4", ExtraHealingPerSlotExpression = "2", AddSpellcastingAbilityModifierToHealing = true
};
var scaledCantrip = new SpellDefinition
{
    Key = "spell.test_scaled_cantrip", Name = "Scaled Cantrip", Level = 0, Resolution = "save", RequiresTarget = true,
    SaveAbility = "dexterity", DamageExpression = "1", DamageType = "Radiant", CantripDamageScaling = true, IgnoreHalfAndThreeQuartersCoverOnSave = true
};
var stabilizingCantrip = new SpellDefinition
{
    Key = "spell.test_stabilize", Name = "Stabilizing Cantrip", Level = 0, Resolution = "stabilize", RequiresTarget = true,
    RangeKind = "distance", RangeFeet = 15, CantripRangeDoubling = true
};
campaign.Spells.AddRange([spellModifierHealing, scaledCantrip, stabilizingCantrip]);
spellTester.PreparedSpellIds.AddRange([spellModifierHealing.Id, scaledCantrip.Id, stabilizingCantrip.Id]);
spellTester.Level = 5;
spellTester.CurrentHp = 1;
spellTester.SpellSlots[1].Remaining = Math.Max(1, spellTester.SpellSlots[1].Remaining);
var modifierHeal = engine.CastSpell(campaign, spellTester.Id, spellModifierHealing.Id, dice, spellTester.Id, slotLevel: 1);
Assert(modifierHeal.Healing == 7 && spellTester.CurrentHp == 8, "healing spells can add the caster's spellcasting ability modifier exactly once");
var cantripTarget = engine.AddCharacter(campaign, new CharacterSheet { Name = "Cantrip Target", CharacterType = "monster", MaxHp = 50, CurrentHp = 50, ArmorClass = 10, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 1 } });
engine.AddCondition(campaign, cantripTarget.Id, "Paralyzed"); // Force the Dexterity save to fail so this test isolates damage scaling instead of random save outcome.
var scaledCantripCast = engine.CastSpell(campaign, spellTester.Id, scaledCantrip.Id, dice, cantripTarget.Id);
Assert(scaledCantripCast.Damage?.Damage.RequestedDamage == 2, "a level 5 cantrip configured for standard damage scaling rolls two copies of its base damage expression");
var dyingSpellTarget = engine.AddCharacter(campaign, new CharacterSheet { Name = "Dying Spell Target", CharacterType = "pc", MaxHp = 12, CurrentHp = 12, Stable = false });
engine.ApplyDamageDetailed(campaign, dyingSpellTarget.Id, 12, "Bludgeoning");
Assert(dyingSpellTarget.CurrentHp == 0 && dyingSpellTarget.Conditions.Contains("Unconscious", StringComparer.OrdinalIgnoreCase), "spell stabilization setup creates a living creature at 0 HP through normal damage resolution");
var stabilizeCast = engine.CastSpell(campaign, spellTester.Id, stabilizingCantrip.Id, dice, dyingSpellTarget.Id);
Assert(dyingSpellTarget.Stable && dyingSpellTarget.CurrentHp == 0 && stabilizeCast.Summary.Contains("Stable", StringComparison.OrdinalIgnoreCase), "stabilizing spells make a living creature at 0 HP Stable without restoring HP");
// Isolate the combat action-economy tests from earlier spell-slot consumption.
// Two level-1 slots are required here: one on the current turn and one after NextTurn.
spellTester.SpellSlots[1].Remaining = Math.Max(2, spellTester.SpellSlots[1].Remaining);
var spellEncounter = engine.StartEncounter(campaign, "Spell Turn Limit");
var spellCombatant = engine.AddCombatant(campaign, spellEncounter.Id, spellTester.Id);
engine.SetInitiative(campaign, spellEncounter.Id, spellCombatant.Id, 20);
engine.FinalizeInitiative(campaign, spellEncounter.Id);
var ritualTimeBeforeCombat = (campaign.Day * 1440) + campaign.MinuteOfDay;
var ritualInCombatRejected = false;
try { engine.CastSpell(campaign, spellTester.Id, studyRite.Id, dice, asRitual: true, encounterId: spellEncounter.Id); }
catch (InvalidOperationException) { ritualInCombatRejected = true; }
Assert(ritualInCombatRejected && ((campaign.Day * 1440) + campaign.MinuteOfDay) == ritualTimeBeforeCombat, "Ritual casting is rejected inside active combat before changing campaign time or spell state");
engine.CastSpell(campaign, spellTester.Id, focusWard.Id, dice, slotLevel: 1, encounterId: spellEncounter.Id);
Assert(!spellCombatant.ActionAvailable && spellCombatant.BonusActionAvailable, "an Action spell consumes the combatant's action but leaves its Bonus Action available");
var reactionCast = engine.CastSpell(campaign, spellTester.Id, practiceReaction.Id, dice, encounterId: spellEncounter.Id);
Assert(!reactionCast.UsedSpellSlot && !spellCombatant.ReactionAvailable, "Reaction spells consume the combatant's single Reaction without spending a slot when they are cantrips");
var secondReactionRejected = false;
try { engine.CastSpell(campaign, spellTester.Id, practiceReaction.Id, dice, encounterId: spellEncounter.Id); }
catch (InvalidOperationException) { secondReactionRejected = true; }
Assert(secondReactionRejected, "a combatant cannot cast a second Reaction spell before its Reaction refreshes");
var secondSlottedSpellRejected = false;
try { engine.CastSpell(campaign, spellTester.Id, practiceBonusSlot.Id, dice, slotLevel: 1, encounterId: spellEncounter.Id); }
catch (InvalidOperationException) { secondSlottedSpellRejected = true; }
Assert(secondSlottedSpellRejected, "the 2024 one-spell-slot-per-turn rule rejects a second slotted spell even when it would use the still-available Bonus Action");
engine.NextTurn(campaign, spellEncounter.Id);
Assert(spellCombatant.ReactionAvailable, "a combatant's Reaction refreshes at the start of its next turn");
engine.EndConcentration(campaign, spellTester.Id);
var nextTurnCast = engine.CastSpell(campaign, spellTester.Id, practiceMend.Id, dice, spellTester.Id, slotLevel: 1, encounterId: spellEncounter.Id);
Assert(nextTurnCast.UsedSpellSlot, "the one-slot-spell limit resets when the combat turn advances");
var bonusCantrip = engine.CastSpell(campaign, spellTester.Id, practiceBonus.Id, dice, encounterId: spellEncounter.Id);
Assert(!bonusCantrip.UsedSpellSlot && !spellCombatant.BonusActionAvailable, "a Bonus Action cantrip can be cast after an Action spell and consumes the single Bonus Action");
var secondBonusRejected = false;
try { engine.CastSpell(campaign, spellTester.Id, practiceBonus.Id, dice, encounterId: spellEncounter.Id); }
catch (InvalidOperationException) { secondBonusRejected = true; }
Assert(secondBonusRejected, "a combatant cannot take a second Bonus Action on the same turn");
engine.EndEncounter(campaign, spellEncounter.Id);

// Ongoing spell effects use source-aware state instead of leaving conditions or attack bonuses stuck on a character.
var effectCaster = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Effect Caster", CharacterType = "pc", CreatureType = "Humanoid", Level = 5, MaxHp = 30, CurrentHp = 30,
    SpellcastingAbility = "intelligence", ProficiencyBonus = 6,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 30 }
});
effectCaster.SpellSlots[2] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
var holdTest = new SpellDefinition
{
    Key = "spell.test_hold_person", Name = "Test Hold Person", Level = 2, CastingTime = "Action", RangeKind = "distance", RangeFeet = 60,
    Resolution = "save", RequiresTarget = true, SaveAbility = "wisdom", RequiresConcentration = true,
    RequiredTargetCreatureType = "Humanoid", ConditionOnFailedSave = "Paralyzed", RepeatSaveAtEndOfTurn = true
};
var guidingTest = new SpellDefinition
{
    Key = "spell.test_guiding_bolt", Name = "Test Guiding Bolt", Level = 0, CastingTime = "Action", RangeKind = "distance", RangeFeet = 120,
    Resolution = "attack", RequiresTarget = true, DamageExpression = "1", DamageType = "Radiant",
    NextAttackAgainstTargetHasAdvantage = true, EffectExpiresAtEndOfCasterNextTurn = true
};
campaign.Spells.AddRange([holdTest, guidingTest]);
effectCaster.PreparedSpellIds.AddRange([holdTest.Id, guidingTest.Id]);
var holdTarget = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Hold Target", CharacterType = "npc", CreatureType = "Humanoid", MaxHp = 40, CurrentHp = 40, ArmorClass = 10,
    ProficiencyBonus = 2, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 1 }
});
var effectAlly = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Effect Ally", CharacterType = "pc", CreatureType = "Humanoid", MaxHp = 30, CurrentHp = 30,
    Attacks = [new AttackProfile { Name = "Test Strike", AttackBonus = 50, DamageExpression = "1", DamageType = "Force", ReachFeet = 5 }]
});
var effectEncounter = engine.StartEncounter(campaign, "Ongoing Spell Effects");
var effectCasterCombatant = engine.AddCombatant(campaign, effectEncounter.Id, effectCaster.Id, side: "party");
var effectAllyCombatant = engine.AddCombatant(campaign, effectEncounter.Id, effectAlly.Id, side: "party");
var holdTargetCombatant = engine.AddCombatant(campaign, effectEncounter.Id, holdTarget.Id, side: "opposition");
engine.SetCombatantPosition(campaign, effectEncounter.Id, effectCasterCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, effectEncounter.Id, effectAllyCombatant.Id, 0, 1);
engine.SetCombatantPosition(campaign, effectEncounter.Id, holdTargetCombatant.Id, 0, 2);
engine.SetInitiative(campaign, effectEncounter.Id, effectCasterCombatant.Id, 30);
engine.SetInitiative(campaign, effectEncounter.Id, effectAllyCombatant.Id, 20);
engine.SetInitiative(campaign, effectEncounter.Id, holdTargetCombatant.Id, 10);
engine.FinalizeInitiative(campaign, effectEncounter.Id);
var holdCast = engine.CastSpell(campaign, effectCaster.Id, holdTest.Id, dice, holdTarget.Id, slotLevel: 2, encounterId: effectEncounter.Id);
Assert(!holdCast.TargetSavingThrow!.Success && holdTarget.Conditions.Contains("Paralyzed") && campaign.ActiveEffects.Any(e => e.TargetCharacterId == holdTarget.Id && e.Condition == "Paralyzed"), "a failed Hold-Person-style save creates a source-aware ongoing Paralyzed effect");
engine.NextTurn(campaign, effectEncounter.Id, dice); // ally
engine.NextTurn(campaign, effectEncounter.Id, dice); // target
holdTarget.Abilities["wisdom"] = 30;
holdTarget.ProficiencyBonus = 20;
holdTarget.SavingThrowProficiencies.Add("wisdom");
engine.NextTurn(campaign, effectEncounter.Id, dice); // target end-of-turn repeat save, back to caster
Assert(!holdTarget.Conditions.Contains("Paralyzed") && !campaign.ActiveEffects.Any(e => e.TargetCharacterId == holdTarget.Id && e.Condition == "Paralyzed"), "a successful repeated end-of-turn save removes the ongoing spell condition without ending unrelated state");
engine.EndConcentration(campaign, effectCaster.Id);

// A creature-type restriction is validated before a spell slot can be spent.
var beastTarget = engine.AddCharacter(campaign, new CharacterSheet { Name = "Beast Target", CharacterType = "monster", CreatureType = "Beast", MaxHp = 20, CurrentHp = 20 });
var slotBeforeWrongType = effectCaster.SpellSlots[2].Remaining;
var wrongTypeRejected = false;
try { engine.CastSpell(campaign, effectCaster.Id, holdTest.Id, dice, beastTarget.Id, slotLevel: 2, encounterId: effectEncounter.Id); }
catch (InvalidOperationException) { wrongTypeRejected = true; }
Assert(wrongTypeRejected && effectCaster.SpellSlots[2].Remaining == slotBeforeWrongType, "creature-type spell restrictions reject an invalid target before spending a slot");

// Guiding-Bolt-style next-attack Advantage is consumed by exactly the next attack roll against the target.
EncounterAttackResult? advantageAttack = null;
var guidingApplied = false;
for (var attempt = 0; attempt < 20 && !guidingApplied; attempt++)
{
    var cast = engine.CastSpell(campaign, effectCaster.Id, guidingTest.Id, dice, holdTarget.Id, encounterId: effectEncounter.Id);
    guidingApplied = cast.SpellAttack?.Hit == true;
    if (!guidingApplied)
    {
        engine.NextTurn(campaign, effectEncounter.Id, dice); // ally
        engine.NextTurn(campaign, effectEncounter.Id, dice); // target
        engine.NextTurn(campaign, effectEncounter.Id, dice); // caster
    }
}
Assert(guidingApplied && campaign.ActiveEffects.Any(e => e.TargetCharacterId == holdTarget.Id && e.NextAttackAgainstTargetHasAdvantage), "a successful Guiding-Bolt-style hit creates the next-attack Advantage marker");
engine.NextTurn(campaign, effectEncounter.Id, dice); // ally
advantageAttack = engine.ResolveEncounterAttack(campaign, effectEncounter.Id, effectAllyCombatant.Id, holdTargetCombatant.Id, "Test Strike", dice);
Assert(advantageAttack.Attack.Summary.Contains("Advantage", StringComparison.OrdinalIgnoreCase) && !campaign.ActiveEffects.Any(e => e.TargetCharacterId == holdTarget.Id && e.NextAttackAgainstTargetHasAdvantage), "the next attack against a Guiding-Bolt-style target gains Advantage and consumes the marker even if the attack misses");
engine.EndEncounter(campaign, effectEncounter.Id);

// 2024 condition interactions are derived even when only the named condition is stored.
var conditionSubject = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Condition Subject", CharacterType = "pc", MaxHp = 100, CurrentHp = 100, Speed = 30, ArmorClass = 10,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 30, ["dexterity"] = 30, ["constitution"] = 14, ["wisdom"] = 10
    }
});
engine.AddCondition(campaign, conditionSubject.Id, "Paralyzed");
var paralyzedSave = engine.ResolveSavingThrow(campaign, conditionSubject.Id, "dexterity", 5, 20);
Assert(!paralyzedSave.Success, "Paralyzed automatically fails Dexterity saving throws even when the supplied d20 would otherwise succeed");
Assert(CharacterMechanics.EffectiveSpeed(conditionSubject) == 0, "Paralyzed sets effective Speed to 0");
engine.RemoveCondition(campaign, conditionSubject.Id, "Paralyzed");
engine.AddCondition(campaign, conditionSubject.Id, "Restrained");
var restrainedSave = engine.ResolveSavingThrow(campaign, conditionSubject.Id, "dexterity", 30, 20, 1);
Assert(restrainedSave.ChosenRoll == 1, "Restrained applies Disadvantage to Dexterity saving throws");
Assert(CharacterMechanics.EffectiveSpeed(conditionSubject) == 0, "Restrained sets effective Speed to 0");
engine.RemoveCondition(campaign, conditionSubject.Id, "Restrained");
engine.AddCondition(campaign, conditionSubject.Id, "Poisoned");
var poisonedCheck = engine.ResolveAbilityCheck(campaign, conditionSubject.Id, "wisdom", 30, 20, 1);
Assert(poisonedCheck.ChosenRoll == 1, "Poisoned applies Disadvantage to ability checks");
engine.RemoveCondition(campaign, conditionSubject.Id, "Poisoned");
engine.BeginConcentration(campaign, conditionSubject.Id, "Condition Focus");
engine.AddCondition(campaign, conditionSubject.Id, "Stunned");
Assert(conditionSubject.ConcentrationEffect is null && CharacterMechanics.EffectiveSpeed(conditionSubject) == 30, "Stunned implies Incapacitated and breaks Concentration without incorrectly setting Speed to 0");
var stunnedEncounter = engine.StartEncounter(campaign, "Stunned Action Gate");
var stunnedCombatant = engine.AddCombatant(campaign, stunnedEncounter.Id, conditionSubject.Id, side: "party");
engine.SetInitiative(campaign, stunnedEncounter.Id, stunnedCombatant.Id, 20);
engine.FinalizeInitiative(campaign, stunnedEncounter.Id);
var stunnedActionRejected = false;
try { engine.TakeDash(campaign, stunnedEncounter.Id, stunnedCombatant.Id); }
catch (InvalidOperationException) { stunnedActionRejected = true; }
Assert(stunnedActionRejected, "Stunned creatures cannot take actions because the condition implies Incapacitated");
engine.EndEncounter(campaign, stunnedEncounter.Id);
engine.RemoveCondition(campaign, conditionSubject.Id, "Stunned");

var criticalAttacker = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Critical Tester", CharacterType = "pc", MaxHp = 30, CurrentHp = 30, ArmorClass = 15, AttacksPerAction = 20,
    Attacks = [new AttackProfile { Name = "Test Blade", AttackBonus = 50, DamageExpression = "1d4+3", DamageType = "Slashing", ReachFeet = 5 }]
});
var criticalTarget = engine.AddCharacter(campaign, new CharacterSheet { Name = "Paralyzed Target", CharacterType = "monster", MaxHp = 10000, CurrentHp = 10000, ArmorClass = 10 });
engine.AddCondition(campaign, criticalTarget.Id, "Paralyzed");
var criticalEncounter = engine.StartEncounter(campaign, "Automatic Critical");
var criticalAttackerCombatant = engine.AddCombatant(campaign, criticalEncounter.Id, criticalAttacker.Id, side: "party");
var criticalTargetCombatant = engine.AddCombatant(campaign, criticalEncounter.Id, criticalTarget.Id, side: "opposition");
engine.SetCombatantPosition(campaign, criticalEncounter.Id, criticalAttackerCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, criticalEncounter.Id, criticalTargetCombatant.Id, 0, 1);
engine.SetInitiative(campaign, criticalEncounter.Id, criticalAttackerCombatant.Id, 20);
engine.SetInitiative(campaign, criticalEncounter.Id, criticalTargetCombatant.Id, 10);
engine.FinalizeInitiative(campaign, criticalEncounter.Id);
EncounterAttackResult? automaticCriticalAttack = null;
for (var attempt = 0; attempt < 20 && automaticCriticalAttack is null; attempt++)
{
    var attackAttempt = engine.ResolveEncounterAttack(campaign, criticalEncounter.Id, criticalAttackerCombatant.Id, criticalTargetCombatant.Id, "Test Blade", dice);
    if (attackAttempt.Attack.Hit) automaticCriticalAttack = attackAttempt;
}
Assert(automaticCriticalAttack?.Attack.Critical == true, "a hit against a Paralyzed target from within 5 feet becomes an automatic Critical Hit");
engine.EndEncounter(campaign, criticalEncounter.Id);
engine.RemoveCondition(campaign, criticalTarget.Id, "Paralyzed");

// Readying a spell spends resources now, holds the spell with Concentration, and releases it with a Reaction.
spellTester.SpellSlots[1].Remaining = Math.Max(2, spellTester.SpellSlots[1].Remaining);
engine.ApplyDamage(campaign, rayTarget.Id, 5);
var readySpellEncounter = engine.StartEncounter(campaign, "Ready Spell");
var readySpellCaster = engine.AddCombatant(campaign, readySpellEncounter.Id, spellTester.Id, side: "party");
var readySpellTarget = engine.AddCombatant(campaign, readySpellEncounter.Id, rayTarget.Id, side: "party");
engine.SetCombatantPosition(campaign, readySpellEncounter.Id, readySpellCaster.Id, 0, 0);
engine.SetCombatantPosition(campaign, readySpellEncounter.Id, readySpellTarget.Id, 0, 1);
engine.SetInitiative(campaign, readySpellEncounter.Id, readySpellCaster.Id, 20);
engine.SetInitiative(campaign, readySpellEncounter.Id, readySpellTarget.Id, 10);
engine.FinalizeInitiative(campaign, readySpellEncounter.Id);
var slotsBeforeReadySpell = spellTester.SpellSlots[1].Remaining;
var readySpell = engine.TakeReadySpell(campaign, readySpellEncounter.Id, readySpellCaster.Id, practiceMend.Id, "If my ally is wounded", 1);
Assert(!readySpellCaster.ActionAvailable && readySpellCaster.ReactionAvailable && readySpellCaster.ReadiedAction is { Kind: "spell" } && spellTester.SpellSlots[1].Remaining == slotsBeforeReadySpell - 1 && spellTester.ConcentrationEffect == "Readied spell: Practice Mend", "Ready Spell casts and spends the slot now, consumes the Action, and holds the spell with Concentration");
engine.NextTurn(campaign, readySpellEncounter.Id);
var hpBeforeReadyRelease = rayTarget.CurrentHp;
var readySpellRelease = engine.TriggerReadiedSpell(campaign, readySpellEncounter.Id, readySpellCaster.Id, dice, readySpellTarget.Id);
Assert(!readySpellCaster.ReactionAvailable && readySpellCaster.ReadiedAction is null && spellTester.ConcentrationEffect is null && rayTarget.CurrentHp > hpBeforeReadyRelease && readySpellRelease.Healing > 0, "a readied non-Concentration spell releases with the Reaction and ends the temporary holding Concentration");
engine.NextTurn(campaign, readySpellEncounter.Id);
spellTester.SpellSlots[1].Remaining = Math.Max(1, spellTester.SpellSlots[1].Remaining);
engine.TakeReadySpell(campaign, readySpellEncounter.Id, readySpellCaster.Id, practiceMend.Id, "If the spell is still needed", 1);
engine.AddCondition(campaign, spellTester.Id, "Stunned");
Assert(readySpellCaster.ReadiedAction is null && spellTester.ConcentrationEffect is null, "breaking Concentration while holding a readied spell makes the held spell dissipate");
engine.RemoveCondition(campaign, spellTester.Id, "Stunned");
engine.EndEncounter(campaign, readySpellEncounter.Id);

var item = new ItemDefinition { Name = "Test Sword", PriceGp = 10 };
campaign.Items.Add(item);
var merchant = new Merchant { Name = "Test Merchant", Gold = 50, LocationId = campaign.PartyLocationId };
merchant.Stock.Add(new MerchantStockEntry { ItemId = item.Id, Quantity = 2 });
campaign.Merchants.Add(merchant);
var purchase = engine.Purchase(campaign, hero.Id, merchant.Id, item.Id);
Assert(purchase.Success && hero.Gold == 20 && merchant.Stock[0].Quantity == 1 && hero.Inventory.Any(i => i.ItemId == item.Id), "purchase atomically updates gold, stock, and inventory");

var foe = engine.AddCharacter(campaign, new CharacterSheet { Name = "Test Raider", CharacterType = "monster", MaxHp = 50, CurrentHp = 50, ArmorClass = 12, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 12 }, Attacks = [new AttackProfile { Name = "Club", AttackBonus = 3, DamageExpression = "1d4+1", DamageType = "Bludgeoning" }] });
hero.ExhaustionLevel = 0;
var encounter = engine.StartEncounter(campaign, "Smoke Combat");
var heroCombatant = engine.AddCombatant(campaign, encounter.Id, hero.Id);
var foeCombatant = engine.AddCombatant(campaign, encounter.Id, foe.Id, surprised: true);
engine.SetInitiative(campaign, encounter.Id, heroCombatant.Id, 16);
engine.SetInitiative(campaign, encounter.Id, foeCombatant.Id, 11);
var initiative = engine.FinalizeInitiative(campaign, encounter.Id);
Assert(initiative.Count == 2 && initiative[0].CharacterId == hero.Id, "combat initiative orders combatants deterministically");
Assert(heroCombatant.MovementRemainingFeet == 30, "the current combatant begins its turn with its effective Speed available as movement");
var outOfRangeRejected = false;
try { engine.ResolveEncounterAttack(campaign, encounter.Id, heroCombatant.Id, foeCombatant.Id, "Longsword", dice); }
catch (InvalidOperationException) { outOfRangeRejected = true; }
Assert(outOfRangeRejected, "tactical combat rejects a melee attack when the target is outside reach");
var move = engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 2, 5);
Assert(move.DistanceFeet == 25 && move.MovementRemainingFeet == 5 && heroCombatant.GridX == 2 && heroCombatant.GridY == 5, "tactical movement spends 5-foot grid movement including diagonal squares");
var rangedNowValid = engine.ResolveEncounterAttack(campaign, encounter.Id, heroCombatant.Id, foeCombatant.Id, "Longsword", dice);
Assert(rangedNowValid.AttackerName == hero.Name && rangedNowValid.TargetName == foe.Name && !heroCombatant.ActionAvailable, "a melee attack resolves in reach and consumes the Attack action");
var secondAttackSameTurnRejected = false;
try { engine.ResolveEncounterAttack(campaign, encounter.Id, heroCombatant.Id, foeCombatant.Id, "Longsword", dice); }
catch (InvalidOperationException) { secondAttackSameTurnRejected = true; }
Assert(secondAttackSameTurnRejected, "a character with one attack per Attack action cannot make a second normal attack on the same turn");
var nextCombatant = engine.NextTurn(campaign, encounter.Id);
Assert(nextCombatant.CharacterId == foe.Id && encounter.Round == 1, "combat turn order advances through initiative");
var outOfTurnMoveRejected = false;
try { engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 2, 4); }
catch (InvalidOperationException) { outOfTurnMoveRejected = true; }
Assert(outOfTurnMoveRejected, "normal tactical movement is rejected outside the combatant's current turn");
engine.NextTurn(campaign, encounter.Id);
Assert(encounter.Round == 2 && encounter.TurnIndex == 0 && heroCombatant.ActionAvailable && heroCombatant.BonusActionAvailable, "combat turn order advances the round and refreshes the current combatant's action and Bonus Action");
var pendingMove = engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 2, 3);
Assert(!pendingMove.Committed && pendingMove.OpportunityAttacks.Count == 1 && encounter.PendingMove is not null, "leaving an enemy's reach pauses movement for an Opportunity Attack reaction window");
var opportunity = engine.ResolveOpportunityAttack(campaign, encounter.Id, foeCombatant.Id, "Club", dice);
Assert(opportunity.UsedReaction && !foeCombatant.ReactionAvailable && encounter.PendingMove is null && heroCombatant.GridX == 2 && heroCombatant.GridY == 3, "Opportunity Attack uses the reactor's Reaction and the pending move commits after reaction resolution");
var difficultTerrain = engine.AddTerrainFeature(campaign, encounter.Id, new TerrainFeature { Name = "Loose Rubble", GridX = 2, GridY = 4, DifficultTerrain = true });
var difficultMove = engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 2, 5);
Assert(difficultMove.Committed && difficultMove.DistanceFeet == 10 && difficultMove.MovementCostFeet == 15, "Difficult Terrain adds one extra foot of movement per foot crossed");
var halfCover = engine.AddTerrainFeature(campaign, encounter.Id, new TerrainFeature { Name = "Low Wall", GridX = 2, GridY = 6, Cover = "half" });
foe.MaxHp = 50;
foe.CurrentHp = 50;
var coveredAttack = engine.ResolveEncounterAttack(campaign, encounter.Id, heroCombatant.Id, foeCombatant.Id, "Longsword", dice);
Assert(coveredAttack.CoverBonus == 2, "Half Cover adds +2 AC to deterministic attack resolution");
engine.NextTurn(campaign, encounter.Id);
engine.NextTurn(campaign, encounter.Id);
engine.TakeDisengage(campaign, encounter.Id, heroCombatant.Id);
Assert(!heroCombatant.ActionAvailable, "Disengage consumes the combatant's action");
var attackAfterDisengageRejected = false;
try { engine.ResolveEncounterAttack(campaign, encounter.Id, heroCombatant.Id, foeCombatant.Id, "Longsword", dice); }
catch (InvalidOperationException) { attackAfterDisengageRejected = true; }
Assert(attackAfterDisengageRejected, "a combatant cannot take the Attack action after spending its action on Disengage");
var disengagedMove = engine.MoveCombatant(campaign, encounter.Id, heroCombatant.Id, 2, 3);
Assert(disengagedMove.Committed && disengagedMove.OpportunityAttacks.Count == 0 && encounter.PendingMove is null, "Disengage prevents Opportunity Attack triggers for the rest of the turn");
engine.NextTurn(campaign, encounter.Id);
engine.NextTurn(campaign, encounter.Id);
var movementBeforeDash = heroCombatant.MovementRemainingFeet;
engine.TakeDash(campaign, encounter.Id, heroCombatant.Id);
Assert(!heroCombatant.ActionAvailable && heroCombatant.MovementRemainingFeet == movementBeforeDash + CharacterMechanics.EffectiveSpeed(hero), "Dash consumes the action and grants extra movement equal to effective Speed");
engine.NextTurn(campaign, encounter.Id);
engine.NextTurn(campaign, encounter.Id);
engine.TakeDodge(campaign, encounter.Id, heroCombatant.Id);
Assert(heroCombatant.Dodging && !heroCombatant.ActionAvailable, "Dodge consumes the action and activates the deterministic Dodge benefit");
engine.SetCombatantPosition(campaign, encounter.Id, foeCombatant.Id, heroCombatant.GridX, heroCombatant.GridY + 1);
engine.NextTurn(campaign, encounter.Id);
var attackAgainstDodge = engine.ResolveEncounterAttack(campaign, encounter.Id, foeCombatant.Id, heroCombatant.Id, "Club", dice);
Assert(attackAgainstDodge.Attack.Summary.Contains("Disadvantage", StringComparison.OrdinalIgnoreCase), "Dodge imposes Disadvantage on deterministic attack rolls against the dodging combatant");
engine.NextTurn(campaign, encounter.Id);
Assert(!heroCombatant.Dodging, "Dodge ends at the start of the combatant's next turn");
engine.EndEncounter(campaign, encounter.Id);
Assert(encounter.Status == "completed", "combat encounters can be completed and persisted");

hero.AttacksPerAction = 2;
var extraAttackDummy = engine.AddCharacter(campaign, new CharacterSheet { Name = "Extra Attack Dummy", CharacterType = "monster", MaxHp = 100, CurrentHp = 100, ArmorClass = 10 });
var extraAttackEncounter = engine.StartEncounter(campaign, "Extra Attack Budget");
var extraHeroCombatant = engine.AddCombatant(campaign, extraAttackEncounter.Id, hero.Id);
var extraDummyCombatant = engine.AddCombatant(campaign, extraAttackEncounter.Id, extraAttackDummy.Id);
engine.SetCombatantPosition(campaign, extraAttackEncounter.Id, extraHeroCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, extraAttackEncounter.Id, extraDummyCombatant.Id, 0, 1);
engine.SetInitiative(campaign, extraAttackEncounter.Id, extraHeroCombatant.Id, 20);
engine.SetInitiative(campaign, extraAttackEncounter.Id, extraDummyCombatant.Id, 1);
engine.FinalizeInitiative(campaign, extraAttackEncounter.Id);
engine.ResolveEncounterAttack(campaign, extraAttackEncounter.Id, extraHeroCombatant.Id, extraDummyCombatant.Id, "Longsword", dice);
Assert(extraHeroCombatant.AttackActionInProgress && extraHeroCombatant.AttacksRemainingInAction == 1 && !extraHeroCombatant.ActionAvailable, "Extra Attack keeps the same Attack action open with one configured attack remaining");
engine.ResolveEncounterAttack(campaign, extraAttackEncounter.Id, extraHeroCombatant.Id, extraDummyCombatant.Id, "Longsword", dice);
Assert(!extraHeroCombatant.AttackActionInProgress && extraHeroCombatant.AttacksRemainingInAction == 0, "the second Extra Attack consumes the remaining attack without granting another Action");
var thirdExtraAttackRejected = false;
try { engine.ResolveEncounterAttack(campaign, extraAttackEncounter.Id, extraHeroCombatant.Id, extraDummyCombatant.Id, "Longsword", dice); }
catch (InvalidOperationException) { thirdExtraAttackRejected = true; }
Assert(thirdExtraAttackRejected, "a third normal attack is rejected when AttacksPerAction is two");
engine.EndEncounter(campaign, extraAttackEncounter.Id);
hero.AttacksPerAction = 1;

var helpActor = engine.AddCharacter(campaign, new CharacterSheet { Name = "Help Actor", CharacterType = "pc", MaxHp = 20, CurrentHp = 20 });
var helpAlly = engine.AddCharacter(campaign, new CharacterSheet { Name = "Help Ally", CharacterType = "pc", MaxHp = 20, CurrentHp = 20, Attacks = [new AttackProfile { Name = "Spear", AttackBonus = 5, DamageExpression = "1d6+3", DamageType = "Piercing" }] });
var helpEnemy = engine.AddCharacter(campaign, new CharacterSheet { Name = "Help Enemy", CharacterType = "monster", MaxHp = 50, CurrentHp = 50, ArmorClass = 12 });
var helpEncounter = engine.StartEncounter(campaign, "Help Action");
var helpActorCombatant = engine.AddCombatant(campaign, helpEncounter.Id, helpActor.Id, side: "party");
var helpAllyCombatant = engine.AddCombatant(campaign, helpEncounter.Id, helpAlly.Id, side: "party");
var helpEnemyCombatant = engine.AddCombatant(campaign, helpEncounter.Id, helpEnemy.Id, side: "opposition");
engine.SetCombatantPosition(campaign, helpEncounter.Id, helpActorCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, helpEncounter.Id, helpAllyCombatant.Id, 1, 0);
engine.SetCombatantPosition(campaign, helpEncounter.Id, helpEnemyCombatant.Id, 0, 1);
engine.SetInitiative(campaign, helpEncounter.Id, helpActorCombatant.Id, 20);
engine.SetInitiative(campaign, helpEncounter.Id, helpAllyCombatant.Id, 15);
engine.SetInitiative(campaign, helpEncounter.Id, helpEnemyCombatant.Id, 1);
engine.FinalizeInitiative(campaign, helpEncounter.Id);
engine.TakeHelpAttack(campaign, helpEncounter.Id, helpActorCombatant.Id, helpEnemyCombatant.Id);
Assert(!helpActorCombatant.ActionAvailable && helpActorCombatant.HelpAttackTargetCombatantId == helpEnemyCombatant.Id, "the Help attack option consumes the helper's action and records the distracted enemy");
engine.NextTurn(campaign, helpEncounter.Id);
var helpedAttack = engine.ResolveEncounterAttack(campaign, helpEncounter.Id, helpAllyCombatant.Id, helpEnemyCombatant.Id, "Spear", dice);
Assert(helpedAttack.Summary.Contains("Help supplied Advantage", StringComparison.OrdinalIgnoreCase) && helpActorCombatant.HelpAttackTargetCombatantId is null, "the next allied attack against the distracted enemy receives and consumes Help Advantage");
engine.EndEncounter(campaign, helpEncounter.Id);

var checkHelper = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Check Helper", CharacterType = "pc", MaxHp = 12, CurrentHp = 12, SkillProficiencies = ["perception"],
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 14 }
});
var checkAlly = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Check Ally", CharacterType = "pc", MaxHp = 12, CurrentHp = 12,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 12 }
});
var helpCheckEncounter = engine.StartEncounter(campaign, "Help Ability Check");
var checkHelperCombatant = engine.AddCombatant(campaign, helpCheckEncounter.Id, checkHelper.Id, side: "party");
var checkAllyCombatant = engine.AddCombatant(campaign, helpCheckEncounter.Id, checkAlly.Id, side: "party");
engine.SetInitiative(campaign, helpCheckEncounter.Id, checkHelperCombatant.Id, 20);
engine.SetInitiative(campaign, helpCheckEncounter.Id, checkAllyCombatant.Id, 10);
engine.FinalizeInitiative(campaign, helpCheckEncounter.Id);
engine.TakeHelpAbilityCheck(campaign, helpCheckEncounter.Id, checkHelperCombatant.Id, checkAllyCombatant.Id, "perception");
Assert(!checkHelperCombatant.ActionAvailable && checkHelperCombatant.HelpAbilityTargetCharacterId == checkAlly.Id && checkHelperCombatant.HelpAbilityProficiency == "perception", "Help can reserve Advantage for an ally's next check using a proficiency the helper actually has");
var helpedCheck = engine.ResolveAbilityCheckWithDice(campaign, checkAlly.Id, "wisdom", 1, dice, D20RollMode.Normal, "perception");
Assert(helpedCheck.RollTwo.HasValue && checkHelperCombatant.HelpAbilityTargetCharacterId is null, "the matching helped ability check rolls with Advantage and consumes the Help benefit");
engine.EndEncounter(campaign, helpCheckEncounter.Id);

var medic = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Medic", CharacterType = "pc", MaxHp = 12, CurrentHp = 12, ProficiencyBonus = 6, SkillProficiencies = ["medicine"],
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 30 }
});
var fallen = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Fallen Ally", CharacterType = "pc", MaxHp = 12, CurrentHp = 12, Stable = false,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 12 }
});
engine.ApplyDamageDetailed(campaign, fallen.Id, 12, "Bludgeoning");
Assert(fallen.CurrentHp == 0 && fallen.Conditions.Contains("Unconscious", StringComparer.OrdinalIgnoreCase), "First Aid setup creates a living creature at 0 HP through normal damage resolution");
var aidEncounter = engine.StartEncounter(campaign, "First Aid");
var medicCombatant = engine.AddCombatant(campaign, aidEncounter.Id, medic.Id, side: "party");
var fallenCombatant = engine.AddCombatant(campaign, aidEncounter.Id, fallen.Id, side: "party");
engine.SetCombatantPosition(campaign, aidEncounter.Id, medicCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, aidEncounter.Id, fallenCombatant.Id, 0, 1);
engine.SetInitiative(campaign, aidEncounter.Id, medicCombatant.Id, 20);
engine.SetInitiative(campaign, aidEncounter.Id, fallenCombatant.Id, 10);
engine.FinalizeInitiative(campaign, aidEncounter.Id);
var aidResult = engine.TakeFirstAid(campaign, aidEncounter.Id, medicCombatant.Id, fallenCombatant.Id, dice);
Assert(aidResult.Stabilized && fallen.Stable && fallen.DeathSaveFailures == 0 && fallen.DeathSaveSuccesses == 0 && !medicCombatant.ActionAvailable, "first aid uses the helper's action and a DC 10 Wisdom (Medicine) check to stabilize a living creature at 0 HP");
engine.EndEncounter(campaign, aidEncounter.Id);

var deathSavePc = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Death Save PC", CharacterType = "pc", MaxHp = 10, CurrentHp = 10
});
var deathSaveEnemy = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Death Save Enemy", CharacterType = "monster", MaxHp = 10, CurrentHp = 10
});
engine.ApplyDamageDetailed(campaign, deathSavePc.Id, 10, "Slashing");
var deathSaveEncounter = engine.StartEncounter(campaign, "Player Death Save Turn");
var deathSavePcCombatant = engine.AddCombatant(campaign, deathSaveEncounter.Id, deathSavePc.Id, side: "party");
var deathSaveEnemyCombatant = engine.AddCombatant(campaign, deathSaveEncounter.Id, deathSaveEnemy.Id, side: "opposition");
engine.SetInitiative(campaign, deathSaveEncounter.Id, deathSavePcCombatant.Id, 20);
engine.SetInitiative(campaign, deathSaveEncounter.Id, deathSaveEnemyCombatant.Id, 10);
engine.FinalizeInitiative(campaign, deathSaveEncounter.Id);
Assert(deathSavePcCombatant.DeathSaveRequiredThisTurn && !deathSavePcCombatant.DeathSaveResolvedThisTurn, "a PC that starts its combat turn at 0 HP is flagged for a player-controlled Death Saving Throw");
var skippedRequiredDeathSaveRejected = false;
try { engine.NextTurn(campaign, deathSaveEncounter.Id, dice); }
catch (InvalidOperationException) { skippedRequiredDeathSaveRejected = true; }
Assert(skippedRequiredDeathSaveRejected, "combat cannot advance past a required unresolved Death Saving Throw");
var combatDeathSave = engine.ResolveCombatDeathSavingThrow(campaign, deathSaveEncounter.Id, deathSavePcCombatant.Id, dice);
Assert(combatDeathSave.Successes == 1 && deathSavePcCombatant.DeathSaveResolvedThisTurn && !deathSavePcCombatant.ActionAvailable && deathSavePcCombatant.MovementRemainingFeet == 0, "combat Death Save resolves exactly once and leaves an unconscious 0-HP PC without normal turn actions");
var duplicateCombatDeathSaveRejected = false;
try { engine.ResolveCombatDeathSavingThrow(campaign, deathSaveEncounter.Id, deathSavePcCombatant.Id, dice); }
catch (InvalidOperationException) { duplicateCombatDeathSaveRejected = true; }
Assert(duplicateCombatDeathSaveRejected, "a player cannot roll multiple Death Saving Throws on the same turn");
engine.NextTurn(campaign, deathSaveEncounter.Id, dice);
engine.NextTurn(campaign, deathSaveEncounter.Id, dice);
Assert(deathSavePcCombatant.DeathSaveRequiredThisTurn && !deathSavePcCombatant.DeathSaveResolvedThisTurn, "the next turn at 0 HP requires a fresh Death Saving Throw");
engine.EndEncounter(campaign, deathSaveEncounter.Id);

var searcher = engine.AddCharacter(campaign, new CharacterSheet { Name = "Searcher", CharacterType = "pc", MaxHp = 10, CurrentHp = 10, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 16 }, SkillProficiencies = ["perception"] });
var scholar = engine.AddCharacter(campaign, new CharacterSheet { Name = "Scholar", CharacterType = "pc", MaxHp = 10, CurrentHp = 10, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 18 }, SkillProficiencies = ["investigation"] });
var diplomat = engine.AddCharacter(campaign, new CharacterSheet { Name = "Diplomat", CharacterType = "pc", MaxHp = 10, CurrentHp = 10, Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["charisma"] = 20 }, SkillProficiencies = ["persuasion"] });
var skillEncounter = engine.StartEncounter(campaign, "Skill Actions");
var searcherCombatant = engine.AddCombatant(campaign, skillEncounter.Id, searcher.Id, side: "party");
var scholarCombatant = engine.AddCombatant(campaign, skillEncounter.Id, scholar.Id, side: "party");
var diplomatCombatant = engine.AddCombatant(campaign, skillEncounter.Id, diplomat.Id, side: "party");
engine.SetInitiative(campaign, skillEncounter.Id, searcherCombatant.Id, 30);
engine.SetInitiative(campaign, skillEncounter.Id, scholarCombatant.Id, 20);
engine.SetInitiative(campaign, skillEncounter.Id, diplomatCombatant.Id, 10);
engine.FinalizeInitiative(campaign, skillEncounter.Id);
var searchAction = engine.TakeSearchAction(campaign, skillEncounter.Id, searcherCombatant.Id, "perception", 1, dice);
Assert(!searcherCombatant.ActionAvailable && searchAction.Ability == "wisdom" && searchAction.Skill == "perception", "Search consumes the action and uses a Wisdom Search skill");
engine.NextTurn(campaign, skillEncounter.Id);
var studyAction = engine.TakeStudyAction(campaign, skillEncounter.Id, scholarCombatant.Id, "investigation", 1, dice);
Assert(!scholarCombatant.ActionAvailable && studyAction.Ability == "intelligence" && studyAction.Skill == "investigation", "Study consumes the action and uses an Intelligence Study skill");
engine.NextTurn(campaign, skillEncounter.Id);
var influenceAction = engine.TakeInfluenceAction(campaign, skillEncounter.Id, diplomatCombatant.Id, "persuasion", 1, dice);
Assert(!diplomatCombatant.ActionAvailable && influenceAction.Ability == "charisma" && influenceAction.Skill == "persuasion", "Influence consumes the action and uses Charisma for Persuasion");
engine.EndEncounter(campaign, skillEncounter.Id);

var stealthHero = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Stealth Hero", CharacterType = "pc", MaxHp = 30, CurrentHp = 30, ArmorClass = 14, ProficiencyBonus = 6,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 30 },
    SkillProficiencies = ["stealth"],
    Attacks = [new AttackProfile { Name = "Dagger", AttackBonus = 8, DamageExpression = "1d4+4", DamageType = "Piercing", ReachFeet = 5 }]
});
var stealthSeeker = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Stealth Seeker", CharacterType = "monster", MaxHp = 60, CurrentHp = 60, ArmorClass = 12, ProficiencyBonus = 6,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 30 }, SkillProficiencies = ["perception"],
    Attacks = [new AttackProfile { Name = "Claw", AttackBonus = 4, DamageExpression = "1d4+2", DamageType = "Slashing", ReachFeet = 5 }]
});
var stealthEncounter = engine.StartEncounter(campaign, "Hide and Search");
var stealthHeroCombatant = engine.AddCombatant(campaign, stealthEncounter.Id, stealthHero.Id, side: "party");
var stealthSeekerCombatant = engine.AddCombatant(campaign, stealthEncounter.Id, stealthSeeker.Id, side: "opposition");
engine.SetCombatantPosition(campaign, stealthEncounter.Id, stealthHeroCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, stealthEncounter.Id, stealthSeekerCombatant.Id, 0, 1);
engine.AddTerrainFeature(campaign, stealthEncounter.Id, new TerrainFeature { Name = "Dense Smoke", GridX = 0, GridY = 0, HeavilyObscured = true, BlocksLineOfSight = true });
engine.SetInitiative(campaign, stealthEncounter.Id, stealthHeroCombatant.Id, 20);
engine.SetInitiative(campaign, stealthEncounter.Id, stealthSeekerCombatant.Id, 10);
engine.FinalizeInitiative(campaign, stealthEncounter.Id);
var hideResult = engine.TakeHide(campaign, stealthEncounter.Id, stealthHeroCombatant.Id, dice);
Assert(hideResult.Hidden && stealthHeroCombatant.IsHidden && hideResult.PerceptionDc >= 15 && !stealthHeroCombatant.ActionAvailable, "Hide consumes the action, enforces DC 15 Stealth, and records the successful check as the Perception DC");
engine.NextTurn(campaign, stealthEncounter.Id);
stealthHeroCombatant.HideCheckTotal = 1;
var foundHidden = engine.SearchForHiddenCombatant(campaign, stealthEncounter.Id, stealthSeekerCombatant.Id, stealthHeroCombatant.Id, dice);
Assert(foundHidden.Found && !stealthHeroCombatant.IsHidden && !stealthSeekerCombatant.ActionAvailable, "Search with Wisdom (Perception) can find a hidden combatant and end its hidden state");
engine.EndEncounter(campaign, stealthEncounter.Id);

var hiddenAttackEncounter = engine.StartEncounter(campaign, "Hidden Attack");
var hiddenAttacker = engine.AddCombatant(campaign, hiddenAttackEncounter.Id, stealthHero.Id, side: "party");
var hiddenTarget = engine.AddCombatant(campaign, hiddenAttackEncounter.Id, stealthSeeker.Id, side: "opposition");
engine.SetCombatantPosition(campaign, hiddenAttackEncounter.Id, hiddenAttacker.Id, 0, 0);
engine.SetCombatantPosition(campaign, hiddenAttackEncounter.Id, hiddenTarget.Id, 0, 1);
engine.AddTerrainFeature(campaign, hiddenAttackEncounter.Id, new TerrainFeature { Name = "Dark Alcove", GridX = 0, GridY = 0, HeavilyObscured = true, BlocksLineOfSight = true });
engine.SetInitiative(campaign, hiddenAttackEncounter.Id, hiddenAttacker.Id, 20);
engine.SetInitiative(campaign, hiddenAttackEncounter.Id, hiddenTarget.Id, 10);
engine.FinalizeInitiative(campaign, hiddenAttackEncounter.Id);
engine.TakeHide(campaign, hiddenAttackEncounter.Id, hiddenAttacker.Id, dice);
engine.NextTurn(campaign, hiddenAttackEncounter.Id);
engine.NextTurn(campaign, hiddenAttackEncounter.Id);
var hiddenAttack = engine.ResolveEncounterAttack(campaign, hiddenAttackEncounter.Id, hiddenAttacker.Id, hiddenTarget.Id, "Dagger", dice);
Assert(hiddenAttack.Attack.Summary.Contains("Advantage", StringComparison.OrdinalIgnoreCase) && !hiddenAttacker.IsHidden, "a hidden combatant attacks with Advantage and stops being hidden immediately after making the attack roll");
engine.EndEncounter(campaign, hiddenAttackEncounter.Id);

var readyActor = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Ready Actor", CharacterType = "pc", MaxHp = 20, CurrentHp = 20, ArmorClass = 14,
    Attacks = [new AttackProfile { Name = "Spear", AttackBonus = 50, DamageExpression = "1", DamageType = "Piercing", ReachFeet = 5 }]
});
var readyTarget = engine.AddCharacter(campaign, new CharacterSheet { Name = "Ready Target", CharacterType = "monster", MaxHp = 100, CurrentHp = 100, ArmorClass = 10 });
var readyEncounter = engine.StartEncounter(campaign, "Ready Attack");
var readyActorCombatant = engine.AddCombatant(campaign, readyEncounter.Id, readyActor.Id, side: "party");
var readyTargetCombatant = engine.AddCombatant(campaign, readyEncounter.Id, readyTarget.Id, side: "opposition");
engine.SetCombatantPosition(campaign, readyEncounter.Id, readyActorCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, readyEncounter.Id, readyTargetCombatant.Id, 0, 1);
engine.SetInitiative(campaign, readyEncounter.Id, readyActorCombatant.Id, 20);
engine.SetInitiative(campaign, readyEncounter.Id, readyTargetCombatant.Id, 10);
engine.FinalizeInitiative(campaign, readyEncounter.Id);
var readiedAttack = engine.TakeReadyAttack(campaign, readyEncounter.Id, readyActorCombatant.Id, readyTargetCombatant.Id, "If the target begins to move", "Spear");
Assert(!readyActorCombatant.ActionAvailable && readyActorCombatant.ReadiedAction is { Kind: "attack" } && readyActorCombatant.ReactionAvailable, "Ready Attack spends the action now but preserves the Reaction until the trigger occurs");
engine.NextTurn(campaign, readyEncounter.Id);
var triggeredAttack = engine.TriggerReadiedAttack(campaign, readyEncounter.Id, readyActorCombatant.Id, dice);
Assert(triggeredAttack.UsedReaction && !readyActorCombatant.ReactionAvailable && readyActorCombatant.ReadiedAction is null, "a readied attack resolves off-turn with the creature's Reaction and clears the prepared action");
engine.EndEncounter(campaign, readyEncounter.Id);

var readyMover = engine.AddCharacter(campaign, new CharacterSheet { Name = "Ready Mover", CharacterType = "pc", MaxHp = 20, CurrentHp = 20, Speed = 30 });
var readyAlly = engine.AddCharacter(campaign, new CharacterSheet { Name = "Ready Ally", CharacterType = "pc", MaxHp = 20, CurrentHp = 20 });
var readyMoveEncounter = engine.StartEncounter(campaign, "Ready Movement");
var readyMoverCombatant = engine.AddCombatant(campaign, readyMoveEncounter.Id, readyMover.Id, side: "party");
var readyAllyCombatant = engine.AddCombatant(campaign, readyMoveEncounter.Id, readyAlly.Id, side: "party");
engine.SetCombatantPosition(campaign, readyMoveEncounter.Id, readyMoverCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, readyMoveEncounter.Id, readyAllyCombatant.Id, 6, 6);
engine.SetInitiative(campaign, readyMoveEncounter.Id, readyMoverCombatant.Id, 20);
engine.SetInitiative(campaign, readyMoveEncounter.Id, readyAllyCombatant.Id, 10);
engine.FinalizeInitiative(campaign, readyMoveEncounter.Id);
engine.TakeReadyMove(campaign, readyMoveEncounter.Id, readyMoverCombatant.Id, "If my ally opens the gate");
var normalMovementBeforeReaction = readyMoverCombatant.MovementRemainingFeet;
engine.NextTurn(campaign, readyMoveEncounter.Id);
var readiedMove = engine.TriggerReadiedMove(campaign, readyMoveEncounter.Id, readyMoverCombatant.Id, 3, 0);
Assert(readiedMove.Committed && readyMoverCombatant.GridX == 3 && readyMoverCombatant.GridY == 0 && !readyMoverCombatant.ReactionAvailable && readyMoverCombatant.MovementRemainingFeet == 0, "readied movement uses the Reaction and its separate Speed allowance without creating or consuming a normal-turn movement budget while off-turn");
engine.NextTurn(campaign, readyMoveEncounter.Id);
Assert(readyMoverCombatant.MovementRemainingFeet == normalMovementBeforeReaction && readyMoverCombatant.ReactionAvailable, "the creature's normal movement budget and Reaction refresh normally when its next turn begins after readied movement");
engine.TakeReadyMove(campaign, readyMoveEncounter.Id, readyMoverCombatant.Id, "If no one acts before my next turn");
engine.NextTurn(campaign, readyMoveEncounter.Id);
engine.NextTurn(campaign, readyMoveEncounter.Id);
Assert(readyMoverCombatant.ReadiedAction is null && readyMoverCombatant.ReactionAvailable, "an unused readied action expires when the creature's next turn begins and its Reaction refreshes");
engine.EndEncounter(campaign, readyMoveEncounter.Id);

var grappleHero = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Grapple Hero", CharacterType = "pc", MaxHp = 30, CurrentHp = 30, ArmorClass = 14,
    Size = "Medium", FreeHands = 1, AttacksPerAction = 1, ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["strength"] = 16, ["dexterity"] = 10 }
});
var grappleTarget = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Grapple Target", CharacterType = "monster", MaxHp = 30, CurrentHp = 30, ArmorClass = 10,
    Size = "Medium", ExhaustionLevel = 5, ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["strength"] = 1, ["dexterity"] = 1 }
});
var grappleEncounter = engine.StartEncounter(campaign, "Grapple Rules");
var grappleHeroCombatant = engine.AddCombatant(campaign, grappleEncounter.Id, grappleHero.Id);
var grappleTargetCombatant = engine.AddCombatant(campaign, grappleEncounter.Id, grappleTarget.Id);
engine.SetCombatantPosition(campaign, grappleEncounter.Id, grappleHeroCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, grappleEncounter.Id, grappleTargetCombatant.Id, 0, 1);
engine.SetInitiative(campaign, grappleEncounter.Id, grappleHeroCombatant.Id, 20);
engine.SetInitiative(campaign, grappleEncounter.Id, grappleTargetCombatant.Id, 1);
engine.FinalizeInitiative(campaign, grappleEncounter.Id);
var grappleResult = engine.ResolveUnarmedGrapple(campaign, grappleEncounter.Id, grappleHeroCombatant.Id, grappleTargetCombatant.Id, dice, "strength");
Assert(grappleResult.Grappled && grappleEncounter.Grapples.Count == 1 && grappleTarget.Conditions.Contains("Grappled") && CharacterMechanics.EffectiveSpeed(grappleTarget) == 0, "the Unarmed Strike Grapple option creates persistent grapple state and reduces the target's Speed to 0 on a failed Strength/Dexterity save");
engine.NextTurn(campaign, grappleEncounter.Id);
grappleTarget.ExhaustionLevel = 0;
grappleTarget.Abilities["strength"] = 30;
grappleTarget.ProficiencyBonus = 6;
grappleTarget.SkillProficiencies.Add("athletics");
var escapeResult = engine.EscapeGrapple(campaign, grappleEncounter.Id, grappleTargetCombatant.Id, grappleHeroCombatant.Id, "athletics", dice);
Assert(escapeResult.Escaped && grappleEncounter.Grapples.Count == 0 && !grappleTarget.Conditions.Contains("Grappled"), "a grappled creature can spend its action and escape with Athletics against the stored escape DC");
engine.EndEncounter(campaign, grappleEncounter.Id);

var shoveHero = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Shove Hero", CharacterType = "pc", MaxHp = 30, CurrentHp = 30, Size = "Medium", ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["strength"] = 16 }
});
var shoveTarget = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Shove Target", CharacterType = "monster", MaxHp = 30, CurrentHp = 30, Size = "Medium", ExhaustionLevel = 5,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["strength"] = 1, ["dexterity"] = 1 }
});
var shoveEncounter = engine.StartEncounter(campaign, "Shove Rules");
var shoveHeroCombatant = engine.AddCombatant(campaign, shoveEncounter.Id, shoveHero.Id);
var shoveTargetCombatant = engine.AddCombatant(campaign, shoveEncounter.Id, shoveTarget.Id);
engine.SetCombatantPosition(campaign, shoveEncounter.Id, shoveHeroCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, shoveEncounter.Id, shoveTargetCombatant.Id, 0, 1);
engine.SetInitiative(campaign, shoveEncounter.Id, shoveHeroCombatant.Id, 20);
engine.SetInitiative(campaign, shoveEncounter.Id, shoveTargetCombatant.Id, 1);
engine.FinalizeInitiative(campaign, shoveEncounter.Id);
var proneResult = engine.ResolveUnarmedShove(campaign, shoveEncounter.Id, shoveHeroCombatant.Id, shoveTargetCombatant.Id, "prone", dice, "dexterity");
Assert(proneResult.Succeeded && shoveTarget.Conditions.Contains("Prone"), "the Unarmed Strike Shove option can impose Prone after a failed Strength/Dexterity save");
engine.NextTurn(campaign, shoveEncounter.Id);
shoveTarget.ExhaustionLevel = 0;
shoveTargetCombatant.MovementRemainingFeet = CharacterMechanics.EffectiveSpeed(shoveTarget);
var crawlMove = engine.MoveCombatant(campaign, shoveEncounter.Id, shoveTargetCombatant.Id, 1, 1);
Assert(crawlMove.Committed && crawlMove.MovementCostFeet == 10, "a Prone creature crawls at one extra foot of movement per foot traveled");
var beforeStand = shoveTargetCombatant.MovementRemainingFeet;
engine.StandFromProne(campaign, shoveEncounter.Id, shoveTargetCombatant.Id);
Assert(!shoveTarget.Conditions.Contains("Prone") && shoveTargetCombatant.MovementRemainingFeet == beforeStand - (CharacterMechanics.EffectiveSpeed(shoveTarget) / 2), "standing from Prone spends half Speed worth of movement without consuming the action");
engine.EndEncounter(campaign, shoveEncounter.Id);

var unarmed = CharacterMechanics.UnarmedStrikeProfile(hero);
Assert(unarmed.AttackBonus == 5 && unarmed.DamageExpression == "4" && unarmed.DamageType == "Bludgeoning", "unarmed strike profile uses Strength modifier plus Proficiency and fixed 1+Strength damage");
var fixedDamage = dice.Roll("4");
Assert(fixedDamage.Total == 4 && fixedDamage.Rolls.Count == 0, "fixed damage expressions are supported without inventing damage dice");

var projectileCaster = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Projectile Mage", CharacterType = "pc", MaxHp = 20, CurrentHp = 20,
    SpellcastingAbility = "intelligence", ProficiencyBonus = 6,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 30 }
});
projectileCaster.SpellSlots[1] = new SpellSlotPool { Maximum = 2, Remaining = 2 };
projectileCaster.SpellSlots[2] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
var projectileTargetA = engine.AddCharacter(campaign, new CharacterSheet { Name = "Projectile Target A", CharacterType = "npc", MaxHp = 200, CurrentHp = 200, ArmorClass = 1 });
var projectileTargetB = engine.AddCharacter(campaign, new CharacterSheet { Name = "Projectile Target B", CharacterType = "npc", MaxHp = 200, CurrentHp = 200, ArmorClass = 1 });
var practiceMissiles = new SpellDefinition
{
    Key = "spell.practice_missiles", Name = "Practice Missiles", Level = 1, Resolution = "projectile_auto", RequiresTarget = true,
    DamageExpression = "1d4+1", DamageType = "Force", BaseProjectiles = 3, ExtraProjectilesPerSlot = 1, RangeKind = "distance", RangeFeet = 120
};
var practiceRays = new SpellDefinition
{
    Key = "spell.practice_rays", Name = "Practice Rays", Level = 2, Resolution = "projectile_attack", RequiresTarget = true,
    DamageExpression = "2d6", DamageType = "Fire", BaseProjectiles = 3, ExtraProjectilesPerSlot = 1, RangeKind = "distance", RangeFeet = 120
};
campaign.Spells.AddRange([practiceMissiles, practiceRays]);
projectileCaster.PreparedSpellIds.AddRange([practiceMissiles.Id, practiceRays.Id]);
var missileHpBefore = projectileTargetA.CurrentHp + projectileTargetB.CurrentHp;
var missileCast = engine.CastProjectileSpell(campaign, projectileCaster.Id, practiceMissiles.Id, dice,
    [projectileTargetA.Id, projectileTargetB.Id, projectileTargetA.Id, projectileTargetB.Id], slotLevel: 2);
Assert(missileCast.TargetResults is { Count: 4 } && projectileCaster.SpellSlots[2].Remaining == 2,
    "an upcast auto-hit projectile spell resolves exactly one declared target allocation per projectile and spends one spell slot");
Assert(projectileTargetA.CurrentHp + projectileTargetB.CurrentHp < missileHpBefore,
    "auto-hit projectile damage is applied to all declared projectile targets");
var slotBeforeBadAllocation = projectileCaster.SpellSlots[2].Remaining;
var badProjectileAllocationRejected = false;
try { engine.CastProjectileSpell(campaign, projectileCaster.Id, practiceRays.Id, dice, [projectileTargetA.Id, projectileTargetB.Id], slotLevel: 2); }
catch (InvalidOperationException) { badProjectileAllocationRejected = true; }
Assert(badProjectileAllocationRejected && projectileCaster.SpellSlots[2].Remaining == slotBeforeBadAllocation,
    "an invalid projectile allocation is rejected before a spell slot is spent");
var rayCast = engine.CastProjectileSpell(campaign, projectileCaster.Id, practiceRays.Id, dice,
    [projectileTargetA.Id, projectileTargetB.Id, projectileTargetA.Id], slotLevel: 2);
Assert(rayCast.TargetResults is { Count: 3 } && rayCast.TargetResults.All(r => r.SpellAttack is not null),
    "multi-ray projectile spells make a separate spell attack roll for every ray");


// Deterministic multi-target buffs and tactical area spell geometry.
var blessCaster = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Bless Caster", CharacterType = "pc", MaxHp = 30, CurrentHp = 30, ArmorClass = 14,
    SpellcastingAbility = "wisdom", ProficiencyBonus = 3,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 16 }
});
blessCaster.SpellSlots[1] = new SpellSlotPool { Maximum = 3, Remaining = 3 };
var blessedAlly = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Blessed Ally", CharacterType = "pc", MaxHp = 30, CurrentHp = 30, ArmorClass = 14,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 10 },
    Attacks = [new AttackProfile { Name = "Practice Blade", AttackBonus = 0, DamageExpression = "1", DamageType = "Slashing", ReachFeet = 5 }]
});
var blessDummy = engine.AddCharacter(campaign, new CharacterSheet { Name = "Bless Dummy", CharacterType = "monster", MaxHp = 50, CurrentHp = 50, ArmorClass = 100 });
var blessSpell = new SpellDefinition
{
    Key = "spell.test_bless", Name = "Test Bless", Level = 1, Resolution = "multi_buff", CastingTime = "Action",
    RangeKind = "distance", RangeFeet = 30, RequiresConcentration = true, RequiresVerbal = true,
    BaseTargets = 3, ExtraTargetsPerSlot = 1, AttackRollBonusExpression = "1d4", SavingThrowBonusExpression = "1d4"
};
campaign.Spells.Add(blessSpell);
blessCaster.PreparedSpellIds.Add(blessSpell.Id);
var blessEncounter = engine.StartEncounter(campaign, "Bless Rules");
var blessCasterCombatant = engine.AddCombatant(campaign, blessEncounter.Id, blessCaster.Id, side: "party");
var blessedAllyCombatant = engine.AddCombatant(campaign, blessEncounter.Id, blessedAlly.Id, side: "party");
var blessDummyCombatant = engine.AddCombatant(campaign, blessEncounter.Id, blessDummy.Id, side: "opposition");
engine.SetCombatantPosition(campaign, blessEncounter.Id, blessCasterCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, blessEncounter.Id, blessedAllyCombatant.Id, 0, 1);
engine.SetCombatantPosition(campaign, blessEncounter.Id, blessDummyCombatant.Id, 0, 2);
engine.SetInitiative(campaign, blessEncounter.Id, blessCasterCombatant.Id, 20);
engine.SetInitiative(campaign, blessEncounter.Id, blessedAllyCombatant.Id, 10);
engine.SetInitiative(campaign, blessEncounter.Id, blessDummyCombatant.Id, 1);
engine.FinalizeInitiative(campaign, blessEncounter.Id);
var blessCast = engine.CastMultiTargetSpell(campaign, blessCaster.Id, blessSpell.Id, dice, [blessCaster.Id, blessedAlly.Id], slotLevel: 1, encounterId: blessEncounter.Id);
Assert(blessCast.TargetResults is { Count: 2 } && blessCaster.ConcentrationEffect == "Test Bless" && campaign.ActiveEffects.Count(e => e.SourceSpellId == blessSpell.Id) == 2,
    "multi-target Concentration buffs validate all targets and create one persistent effect per target");
engine.NextTurn(campaign, blessEncounter.Id);
var blessedAttack = engine.ResolveEncounterAttack(campaign, blessEncounter.Id, blessedAllyCombatant.Id, blessDummyCombatant.Id, "Practice Blade", dice);
Assert(blessedAttack.Attack.Modifier is >= 1 and <= 4, "Bless-style attack bonuses roll a fresh 1d4 and are included in deterministic attack totals");
var blessedSave = engine.ResolveSavingThrowWithDice(campaign, blessedAlly.Id, "dexterity", 10, dice);
var blessSaveBonus = blessedSave.Total - blessedSave.ChosenRoll - blessedSave.AbilityModifier - blessedSave.ProficiencyModifier + blessedSave.ExhaustionPenalty;
Assert(blessSaveBonus is >= 1 and <= 4, "Bless-style saving throw bonuses roll a fresh 1d4 and are included in deterministic saving throws");
engine.EndConcentration(campaign, blessCaster.Id, "smoke test complete");
Assert(!campaign.ActiveEffects.Any(e => e.SourceSpellId == blessSpell.Id), "ending Concentration removes every Bless-style target effect created by that caster");
engine.EndEncounter(campaign, blessEncounter.Id);

// Fixed AC and temporary Speed modifiers are persisted as deterministic active effects.
var modifierCaster = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Modifier Caster", CharacterType = "pc", MaxHp = 20, CurrentHp = 20, SpellcastingAbility = "wisdom", ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wisdom"] = 16 }
});
modifierCaster.SpellSlots[1] = new SpellSlotPool { Maximum = 1, Remaining = 1 };
var modifierAlly = engine.AddCharacter(campaign, new CharacterSheet { Name = "Modifier Ally", CharacterType = "pc", MaxHp = 20, CurrentHp = 20, ArmorClass = 15, Speed = 30 });
var shieldOfFaithTest = new SpellDefinition
{
    Key = "spell.test_shield_of_faith", Name = "Test Shield of Faith", Level = 1, Resolution = "multi_buff", CastingTime = "Bonus Action",
    RangeKind = "distance", RangeFeet = 60, RequiresConcentration = true, BaseTargets = 1, ArmorClassBonus = 2
};
campaign.Spells.Add(shieldOfFaithTest);
modifierCaster.PreparedSpellIds.Add(shieldOfFaithTest.Id);
var modifierEncounter = engine.StartEncounter(campaign, "Fixed Modifier Effects");
var modifierCasterCombatant = engine.AddCombatant(campaign, modifierEncounter.Id, modifierCaster.Id, side: "party");
var modifierAllyCombatant = engine.AddCombatant(campaign, modifierEncounter.Id, modifierAlly.Id, side: "party");
engine.SetCombatantPosition(campaign, modifierEncounter.Id, modifierCasterCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, modifierEncounter.Id, modifierAllyCombatant.Id, 0, 1);
engine.SetInitiative(campaign, modifierEncounter.Id, modifierCasterCombatant.Id, 20);
engine.SetInitiative(campaign, modifierEncounter.Id, modifierAllyCombatant.Id, 10);
engine.FinalizeInitiative(campaign, modifierEncounter.Id);
engine.CastMultiTargetSpell(campaign, modifierCaster.Id, shieldOfFaithTest.Id, dice, [modifierAlly.Id], slotLevel: 1, encounterId: modifierEncounter.Id);
Assert(campaign.ActiveEffects.Any(e => e.SourceSpellId == shieldOfFaithTest.Id && e.TargetCharacterId == modifierAlly.Id && e.ArmorClassBonus == 2)
    && modifierCaster.ConcentrationEffect == shieldOfFaithTest.Name, "Shield-of-Faith-style buffs persist a fixed AC bonus and bind it to source Concentration");
engine.EndConcentration(campaign, modifierCaster.Id, "fixed modifier smoke test");
Assert(!campaign.ActiveEffects.Any(e => e.SourceSpellId == shieldOfFaithTest.Id), "ending Concentration removes fixed AC spell effects");

var speedEffect = new ActiveEffectState
{
    Name = "Test Ray of Frost", SourceCharacterId = modifierCaster.Id, TargetCharacterId = modifierAlly.Id, SourceSpellId = "spell.test_ray_of_frost",
    SpeedModifierFeet = -10, ExpireAtStartOfSourceNextTurn = true
};
campaign.ActiveEffects.Add(speedEffect);
Assert(CharacterMechanics.EffectiveSpeed(modifierAlly, campaign.ActiveEffects) == 20, "active spell effects can reduce effective Speed without mutating the character's base Speed");
engine.NextTurn(campaign, modifierEncounter.Id, dice);
engine.NextTurn(campaign, modifierEncounter.Id, dice);
Assert(CharacterMechanics.EffectiveSpeed(modifierAlly, campaign.ActiveEffects) == 30 && !campaign.ActiveEffects.Contains(speedEffect),
    "start-of-source-next-turn effects expire before the next turn's movement budget is calculated");
engine.EndEncounter(campaign, modifierEncounter.Id);

var areaCaster = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Area Caster", CharacterType = "pc", MaxHp = 100, CurrentHp = 100, ArmorClass = 14,
    SpellcastingAbility = "intelligence", ProficiencyBonus = 3,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["intelligence"] = 18 }
});
areaCaster.SpellSlots[1] = new SpellSlotPool { Maximum = 4, Remaining = 4 };
areaCaster.SpellSlots[3] = new SpellSlotPool { Maximum = 2, Remaining = 2 };
var areaVictim = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Area Victim", CharacterType = "monster", MaxHp = 200, CurrentHp = 200, ArmorClass = 10, ExhaustionLevel = 5,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 1, ["constitution"] = 1 }
});
var areaOutside = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Area Outside", CharacterType = "monster", MaxHp = 200, CurrentHp = 200, ArmorClass = 10,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dexterity"] = 30, ["constitution"] = 30 }
});
var burningHands = new SpellDefinition
{
    Key = "spell.test_burning_hands", Name = "Test Burning Hands", Level = 1, Resolution = "area_save", CastingTime = "Action",
    RangeKind = "self", SaveAbility = "dexterity", DamageExpression = "3d6", DamageType = "Fire", HalfDamageOnSuccessfulSave = true,
    ExtraDamagePerSlotExpression = "1d6", AreaShape = "cone", AreaSizeFeet = 15, AreaOrigin = "self"
};
var thunderwave = new SpellDefinition
{
    Key = "spell.test_thunderwave", Name = "Test Thunderwave", Level = 1, Resolution = "area_save", CastingTime = "Action",
    RangeKind = "self", SaveAbility = "constitution", DamageExpression = "2d8", DamageType = "Thunder", HalfDamageOnSuccessfulSave = true,
    ExtraDamagePerSlotExpression = "1d8", AreaShape = "cube", AreaSizeFeet = 15, AreaOrigin = "self", PushFeetOnFailedSave = 10
};
var fireball = new SpellDefinition
{
    Key = "spell.test_fireball", Name = "Test Fireball", Level = 3, Resolution = "area_save", CastingTime = "Action",
    RangeKind = "distance", RangeFeet = 150, SaveAbility = "dexterity", DamageExpression = "8d6", DamageType = "Fire", HalfDamageOnSuccessfulSave = true,
    ExtraDamagePerSlotExpression = "1d6", AreaShape = "sphere", AreaSizeFeet = 20, AreaOrigin = "point"
};
var shatter = new SpellDefinition
{
    Key = "spell.test_shatter", Name = "Test Shatter", Level = 2, Resolution = "area_save", CastingTime = "Action",
    RangeKind = "distance", RangeFeet = 60, SaveAbility = "constitution", DamageExpression = "3d8", DamageType = "Thunder", HalfDamageOnSuccessfulSave = true,
    ExtraDamagePerSlotExpression = "1d8", AreaShape = "sphere", AreaSizeFeet = 10, AreaOrigin = "point", SaveDisadvantageCreatureType = "Construct"
};
campaign.Spells.AddRange([burningHands, thunderwave, fireball, shatter]);
areaCaster.PreparedSpellIds.AddRange([burningHands.Id, thunderwave.Id, fireball.Id, shatter.Id]);
areaCaster.SpellSlots[2] = new SpellSlotPool { Maximum = 1, Remaining = 1 };

var coneEncounter = engine.StartEncounter(campaign, "Cone Geometry");
var coneCasterC = engine.AddCombatant(campaign, coneEncounter.Id, areaCaster.Id);
var coneVictimC = engine.AddCombatant(campaign, coneEncounter.Id, areaVictim.Id);
var coneOutsideC = engine.AddCombatant(campaign, coneEncounter.Id, areaOutside.Id);
engine.SetCombatantPosition(campaign, coneEncounter.Id, coneCasterC.Id, 0, 0);
engine.SetCombatantPosition(campaign, coneEncounter.Id, coneVictimC.Id, 0, -2);
engine.SetCombatantPosition(campaign, coneEncounter.Id, coneOutsideC.Id, 3, 0);
engine.SetInitiative(campaign, coneEncounter.Id, coneCasterC.Id, 20);
engine.SetInitiative(campaign, coneEncounter.Id, coneVictimC.Id, 10);
engine.SetInitiative(campaign, coneEncounter.Id, coneOutsideC.Id, 1);
engine.FinalizeInitiative(campaign, coneEncounter.Id);
var outsideHpBeforeCone = areaOutside.CurrentHp;
var victimHpBeforeCone = areaVictim.CurrentHp;
var coneCast = engine.CastAreaSpell(campaign, areaCaster.Id, burningHands.Id, dice, direction: "north", slotLevel: 1, encounterId: coneEncounter.Id);
Assert(coneCast.TargetResults?.Any(r => r.TargetId == areaVictim.Id) == true && areaVictim.CurrentHp < victimHpBeforeCone && areaOutside.CurrentHp == outsideHpBeforeCone,
    "self-origin cone geometry affects creatures in the chosen direction without hitting creatures outside the cone");
engine.EndEncounter(campaign, coneEncounter.Id);

areaVictim.CurrentHp = 200;
areaVictim.ExhaustionLevel = 5;
var cubeEncounter = engine.StartEncounter(campaign, "Cube Geometry");
var cubeCasterC = engine.AddCombatant(campaign, cubeEncounter.Id, areaCaster.Id);
var cubeVictimC = engine.AddCombatant(campaign, cubeEncounter.Id, areaVictim.Id);
engine.SetCombatantPosition(campaign, cubeEncounter.Id, cubeCasterC.Id, 0, 0);
engine.SetCombatantPosition(campaign, cubeEncounter.Id, cubeVictimC.Id, 1, 0);
engine.SetInitiative(campaign, cubeEncounter.Id, cubeCasterC.Id, 20);
engine.SetInitiative(campaign, cubeEncounter.Id, cubeVictimC.Id, 1);
engine.FinalizeInitiative(campaign, cubeEncounter.Id);
var cubeCast = engine.CastAreaSpell(campaign, areaCaster.Id, thunderwave.Id, dice, direction: "east", slotLevel: 1, encounterId: cubeEncounter.Id);
Assert(cubeCast.TargetResults?.Single().TargetId == areaVictim.Id && cubeVictimC.GridX == 3,
    "self-origin cube spells resolve failed saves and deterministic forced movement without consuming the target's movement");
engine.EndEncounter(campaign, cubeEncounter.Id);

areaVictim.CurrentHp = 200;
var sphereEncounter = engine.StartEncounter(campaign, "Sphere Geometry");
var sphereCasterC = engine.AddCombatant(campaign, sphereEncounter.Id, areaCaster.Id);
var sphereVictimC = engine.AddCombatant(campaign, sphereEncounter.Id, areaVictim.Id);
var sphereOutsideC = engine.AddCombatant(campaign, sphereEncounter.Id, areaOutside.Id);
engine.SetCombatantPosition(campaign, sphereEncounter.Id, sphereCasterC.Id, 0, 0);
engine.SetCombatantPosition(campaign, sphereEncounter.Id, sphereVictimC.Id, 6, 0);
engine.SetCombatantPosition(campaign, sphereEncounter.Id, sphereOutsideC.Id, 11, 0);
engine.SetInitiative(campaign, sphereEncounter.Id, sphereCasterC.Id, 20);
engine.SetInitiative(campaign, sphereEncounter.Id, sphereVictimC.Id, 10);
engine.SetInitiative(campaign, sphereEncounter.Id, sphereOutsideC.Id, 1);
engine.FinalizeInitiative(campaign, sphereEncounter.Id);
var victimHpBeforeSphere = areaVictim.CurrentHp;
var outsideHpBeforeSphere = areaOutside.CurrentHp;
var sphereCast = engine.CastAreaSpell(campaign, areaCaster.Id, fireball.Id, dice, centerX: 6, centerY: 0, slotLevel: 3, encounterId: sphereEncounter.Id);
Assert(sphereCast.TargetResults?.Any(r => r.TargetId == areaVictim.Id) == true && areaVictim.CurrentHp < victimHpBeforeSphere && areaOutside.CurrentHp == outsideHpBeforeSphere,
    "point-origin sphere geometry resolves creatures inside the radius while excluding creatures outside it");
engine.EndEncounter(campaign, sphereEncounter.Id);

var constructTarget = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Shatter Construct", CharacterType = "monster", CreatureType = "Construct", MaxHp = 100, CurrentHp = 100, ArmorClass = 10,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 30 }
});
var shatterEncounter = engine.StartEncounter(campaign, "Construct Save Disadvantage");
var shatterCasterC = engine.AddCombatant(campaign, shatterEncounter.Id, areaCaster.Id);
var constructCombatant = engine.AddCombatant(campaign, shatterEncounter.Id, constructTarget.Id);
engine.SetCombatantPosition(campaign, shatterEncounter.Id, shatterCasterC.Id, 0, 0);
engine.SetCombatantPosition(campaign, shatterEncounter.Id, constructCombatant.Id, 4, 0);
engine.SetInitiative(campaign, shatterEncounter.Id, shatterCasterC.Id, 20);
engine.SetInitiative(campaign, shatterEncounter.Id, constructCombatant.Id, 1);
engine.FinalizeInitiative(campaign, shatterEncounter.Id);
var shatterCast = engine.CastAreaSpell(campaign, areaCaster.Id, shatter.Id, dice, centerX: 4, centerY: 0, slotLevel: 2, encounterId: shatterEncounter.Id);
Assert(shatterCast.TargetResults?.Single(r => r.TargetId == constructTarget.Id).TargetSavingThrow?.RollTwo is not null,
    "creature-type-specific save metadata can impose Disadvantage, enabling Shatter's Construct saving throw rule");
engine.EndEncounter(campaign, shatterEncounter.Id);

// Area line of effect uses the spell's point of origin rather than the caster as the cover reference.
areaCaster.SpellSlots[3].Remaining = 1;
areaVictim.CurrentHp = 200;
areaOutside.CurrentHp = 200;
var lineEncounter = engine.StartEncounter(campaign, "Area Line Of Effect");
var lineCaster = engine.AddCombatant(campaign, lineEncounter.Id, areaCaster.Id);
var lineVictim = engine.AddCombatant(campaign, lineEncounter.Id, areaVictim.Id);
var lineOpenTarget = engine.AddCombatant(campaign, lineEncounter.Id, areaOutside.Id);
engine.SetCombatantPosition(campaign, lineEncounter.Id, lineCaster.Id, 0, 0);
engine.SetCombatantPosition(campaign, lineEncounter.Id, lineVictim.Id, 5, 0);
engine.SetCombatantPosition(campaign, lineEncounter.Id, lineOpenTarget.Id, 3, 1);
engine.AddTerrainFeature(campaign, lineEncounter.Id, new TerrainFeature { Name = "Stone Wall", GridX = 4, GridY = 0, BlocksMovement = true, BlocksLineOfSight = true, Cover = "total" });
engine.SetInitiative(campaign, lineEncounter.Id, lineCaster.Id, 20);
engine.SetInitiative(campaign, lineEncounter.Id, lineVictim.Id, 10);
engine.SetInitiative(campaign, lineEncounter.Id, lineOpenTarget.Id, 1);
engine.FinalizeInitiative(campaign, lineEncounter.Id);
var blockedHpBefore = areaVictim.CurrentHp;
var lineCast = engine.CastAreaSpell(campaign, areaCaster.Id, fireball.Id, dice, centerX: 3, centerY: 0, slotLevel: 3, encounterId: lineEncounter.Id);
Assert(lineCast.TargetResults?.Any(r => r.TargetId == areaOutside.Id) == true
    && lineCast.TargetResults.All(r => r.TargetId != areaVictim.Id)
    && areaVictim.CurrentHp == blockedHpBefore,
    "Total Cover blocks an area spell location along the straight line from the spell's point of origin");
engine.EndEncounter(campaign, lineEncounter.Id);

var malformedArea = new SpellDefinition
{
    Key = "spell.invalid_area", Name = "Invalid Area", Level = 2, Resolution = "area_save",
    SaveAbility = "dexterity", DamageExpression = "2d6", AreaShape = "triangle", AreaSizeFeet = 0, AreaOrigin = "somewhere", PushFeetOnFailedSave = 7
};
campaign.Spells.Add(malformedArea);
var malformedIssues = new CampaignReadinessValidator().Validate(campaign).Where(i => i.EntityKey == malformedArea.Key).ToArray();
Assert(malformedIssues.Count(i => i.Severity == ReadinessSeverity.Error) >= 3,
    "campaign readiness catches unsupported area geometry, missing area size/origin, and invalid forced-movement increments");
campaign.Spells.Remove(malformedArea);


// Persistent battlefield effects share the same tactical geometry as area spells and resolve through application-owned state.
var zoneCaster = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Zone Caster", CharacterType = "pc", MaxHp = 24, CurrentHp = 24, Speed = 30,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 18 }
});
var zoneTarget = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Zone Target", CharacterType = "monster", MaxHp = 30, CurrentHp = 30, Speed = 30, ArmorClass = 12
});
var zoneEncounter = engine.StartEncounter(campaign, "Persistent Battlefield Effects");
var zoneCasterCombatant = engine.AddCombatant(campaign, zoneEncounter.Id, zoneCaster.Id, side: "party");
var zoneTargetCombatant = engine.AddCombatant(campaign, zoneEncounter.Id, zoneTarget.Id, side: "opposition");
engine.SetCombatantPosition(campaign, zoneEncounter.Id, zoneCasterCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 4, 0);
engine.SetInitiative(campaign, zoneEncounter.Id, zoneCasterCombatant.Id, 20);
engine.SetInitiative(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 10);
engine.FinalizeInitiative(campaign, zoneEncounter.Id);


zoneCaster.SpellcastingAbility = "wisdom";
zoneCaster.ProficiencyBonus = 2;
zoneCaster.SpellSlots[2] = new SpellSlotPool { Maximum = 1, Remaining = 1 };
var fogCloudTest = new SpellDefinition
{
    Key = "spell.test_fog_cloud", Name = "Test Fog Cloud", Level = 1, CastingTime = "Action", RangeKind = "distance", RangeFeet = 120,
    RequiresVerbal = true, RequiresSomatic = true, RequiresConcentration = true, Resolution = "persistent_area",
    AreaShape = "sphere", AreaSizeFeet = 20, ExtraAreaSizePerSlotFeet = 20, AreaOrigin = "point",
    BattlefieldTrigger = "none", BattlefieldHeavilyObscured = true, BattlefieldBlocksLineOfSight = true
};
campaign.Spells.Add(fogCloudTest);
zoneCaster.PreparedSpellIds.Add(fogCloudTest.Id);
var fogSlotBeforeReject = zoneCaster.SpellSlots[2].Remaining;
var fogRangeRejected = false;
try { engine.CastPersistentAreaSpell(campaign, zoneCaster.Id, fogCloudTest.Id, dice, 100, 100, "north", 2, zoneEncounter.Id); }
catch (InvalidOperationException) { fogRangeRejected = true; }
Assert(fogRangeRejected && zoneCaster.SpellSlots[2].Remaining == fogSlotBeforeReject && zoneCasterCombatant.ActionAvailable, "persistent-area spells reject an out-of-range origin before spending a slot or action");
var fogCast = engine.CastPersistentAreaSpell(campaign, zoneCaster.Id, fogCloudTest.Id, dice, 2, 2, "north", 2, zoneEncounter.Id);
var fogZone = zoneEncounter.BattlefieldEffects.Single(e => e.SourceSpellId == fogCloudTest.Id);
Assert(fogCast.UsedSpellSlot && fogCast.CastAtLevel == 2 && fogZone.SizeFeet == 40 && fogZone.HeavilyObscured && fogZone.BlocksLineOfSight, "upcast persistent-area spells create deterministic battlefield geometry with configured obscurement");
Assert(zoneCaster.ConcentrationEffect == fogCloudTest.Name && zoneCaster.SpellSlots[2].Remaining == 0, "persistent-area Concentration spells spend the selected slot and bind the created zone to Concentration");
engine.EndConcentration(campaign, zoneCaster.Id, "persistent spell smoke test");
Assert(!zoneEncounter.BattlefieldEffects.Any(e => e.Id == fogZone.Id), "ending Concentration removes a persistent spell-created battlefield zone");

var startTurnZone = engine.AddBattlefieldEffect(campaign, zoneEncounter.Id, new BattlefieldEffectState
{
    Name = "Fixed Flame Zone", Shape = "sphere", SizeFeet = 5, OriginX = 4, OriginY = 0,
    Trigger = "start_turn", DamageExpression = "4", DamageType = "Fire", DurationRounds = 1
});
engine.NextTurn(campaign, zoneEncounter.Id, dice);
Assert(zoneTarget.CurrentHp == 26, "a persistent battlefield effect automatically resolves fixed start-of-turn damage on a creature inside its geometry");
Assert(campaign.Events.Any(e => e.Type == "battlefield_effect_trigger" && e.Summary.Contains("Fixed Flame Zone", StringComparison.OrdinalIgnoreCase)), "battlefield hazard triggers are recorded in campaign history");

var difficultZone = engine.AddBattlefieldEffect(campaign, zoneEncounter.Id, new BattlefieldEffectState
{
    Name = "Grasping Ground", Shape = "sphere", SizeFeet = 5, OriginX = 5, OriginY = 0,
    Trigger = "none", DifficultTerrain = true
});
var difficultZoneMove = engine.MoveCombatant(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 6, 0);
Assert(difficultZoneMove.MovementCostFeet == 20 && difficultZoneMove.MovementRemainingFeet == 10, "battlefield-effect Difficult Terrain increases tactical movement cost through affected grid cells");

var enterZone = engine.AddBattlefieldEffect(campaign, zoneEncounter.Id, new BattlefieldEffectState
{
    Name = "Arcane Threshold", Shape = "sphere", SizeFeet = 5, OriginX = 8, OriginY = 0,
    Trigger = "enter", DamageExpression = "3", DamageType = "Force", OncePerTurn = true
});
var firstEnter = engine.MoveCombatant(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 8, 0);
Assert(firstEnter.Committed && zoneTarget.CurrentHp == 23, "enter-trigger battlefield effects resolve when movement crosses into their geometry");
engine.TakeDash(campaign, zoneEncounter.Id, zoneTargetCombatant.Id);
engine.MoveCombatant(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 6, 0);
engine.MoveCombatant(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 8, 0);
Assert(zoneTarget.CurrentHp == 23, "once-per-turn battlefield effects do not damage the same creature twice during one turn after it leaves and re-enters");

// Movement hazards such as Spike Growth resolve once for every 5 feet actually traveled inside the area.
zoneTargetCombatant.MovementRemainingFeet = 30;
var stepHazard = engine.AddBattlefieldEffect(campaign, zoneEncounter.Id, new BattlefieldEffectState
{
    Name = "Test Spike Ground", Shape = "sphere", SizeFeet = 5, OriginX = 9, OriginY = 0,
    Trigger = "move_within", DamageExpression = "2", DamageType = "Piercing", OncePerTurn = false, DifficultTerrain = true
});
var stepHpBefore = zoneTarget.CurrentHp;
var stepMove = engine.MoveCombatant(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 10, 0);
Assert(stepMove.Committed && stepMove.DistanceFeet == 10 && zoneTarget.CurrentHp == stepHpBefore - 4,
    "move-within battlefield hazards apply one deterministic damage instance for every 5 feet traveled inside their geometry");

zoneTarget.CurrentHp = 3;
zoneTarget.Dead = false;
zoneTarget.Conditions.RemoveAll(c => c.Equals("Dead", StringComparison.OrdinalIgnoreCase) || c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase));
zoneTargetCombatant.MovementRemainingFeet = 30;
var lethalStepHazard = engine.AddBattlefieldEffect(campaign, zoneEncounter.Id, new BattlefieldEffectState
{
    Name = "Lethal Step Hazard", Shape = "sphere", SizeFeet = 10, OriginX = 12, OriginY = 0,
    Trigger = "move_within", DamageExpression = "4", DamageType = "Piercing", OncePerTurn = false
});
var interruptedMove = engine.MoveCombatant(campaign, zoneEncounter.Id, zoneTargetCombatant.Id, 14, 0);
Assert(zoneTarget.Dead && interruptedMove.DistanceFeet == 5 && zoneTargetCombatant.GridX == 11,
    "stepwise movement stops at the grid square where a movement hazard incapacitates or kills the mover instead of teleporting it to the requested destination");
engine.RemoveBattlefieldEffect(campaign, zoneEncounter.Id, stepHazard.Id, "smoke test complete");
engine.RemoveBattlefieldEffect(campaign, zoneEncounter.Id, lethalStepHazard.Id, "smoke test complete");

engine.BeginConcentration(campaign, zoneCaster.Id, "Obscuring Cloud");
var concentrationZone = engine.AddBattlefieldEffect(campaign, zoneEncounter.Id, new BattlefieldEffectState
{
    Name = "Obscuring Cloud", SourceCharacterId = zoneCaster.Id, Shape = "sphere", SizeFeet = 10,
    OriginX = 0, OriginY = 0, Trigger = "none", HeavilyObscured = true, BlocksLineOfSight = true,
    RequiresSourceConcentration = true, ConcentrationName = "Obscuring Cloud"
});
Assert(zoneEncounter.BattlefieldEffects.Any(e => e.Id == concentrationZone.Id), "Concentration-bound battlefield effects can be created while their source is Concentrating");
engine.EndConcentration(campaign, zoneCaster.Id, "smoke test");
Assert(!zoneEncounter.BattlefieldEffects.Any(e => e.Id == concentrationZone.Id), "ending Concentration automatically removes battlefield effects bound to that Concentration");

engine.NextTurn(campaign, zoneEncounter.Id, dice);
Assert(!zoneEncounter.BattlefieldEffects.Any(e => e.Id == startTurnZone.Id), "round-limited battlefield effects expire automatically when their configured round window ends");
engine.EndEncounter(campaign, zoneEncounter.Id);
Assert(zoneEncounter.BattlefieldEffects.Count == 0, "ending an encounter clears runtime battlefield effects");

var temp = Path.Combine(Path.GetTempPath(), "dmai-smoke-" + Guid.NewGuid().ToString("N"));
var store = new AppDataStore(temp);
var state = new AppState { SelectedCampaignId = campaign.Id, Campaigns = [campaign] };
await store.SaveAsync(state);
var restored = await store.LoadAsync();
Assert(restored.Campaigns.Single().Characters.Single(c => c.Id == hero.Id).CurrentHp == hero.CurrentHp, "campaign state persists and reloads");

var cloner = new CampaignCloneService();
var isolated = cloner.Clone(campaign);
isolated.Characters.Single(c => c.Id == hero.Id).CurrentHp = 1;
Assert(campaign.Characters.Single(c => c.Id == hero.Id).CurrentHp != 1, "campaign turn clones isolate uncommitted AI tool mutations");

state.Campaigns[0].Name = "Safe Previous State";
await store.SaveAsync(state);
state.Campaigns[0].Name = "Newest State";
await store.SaveAsync(state);
await File.WriteAllTextAsync(store.StatePath, "{ this is not valid json");
var recovered = await store.LoadAsync();
Assert(recovered.Campaigns.Single().Name == "Safe Previous State", "unreadable current state recovers the previous safe copy");
Assert(!string.IsNullOrWhiteSpace(store.LastRecoveryMessage), "state recovery is surfaced to the application");
Assert(Directory.Exists(store.RecoveryDirectory) && Directory.EnumerateFiles(store.RecoveryDirectory).Any(), "unreadable state is preserved for recovery diagnostics");
Directory.Delete(temp, true);

var rulesPath = Path.Combine(Path.GetTempPath(), "dmai-rules-" + Guid.NewGuid().ToString("N") + ".jsonl");
await File.WriteAllTextAsync(rulesPath, "{\"chunk_key\":\"test.attack\",\"page\":7,\"section\":\"Combat\",\"heading\":\"Attack Rolls\",\"text\":\"An attack roll hits if the total equals or exceeds Armor Class.\"}\n");
var rules = new RulesSearchService();
await rules.LoadAsync(rulesPath);
Assert(rules.Search("attack armor class").Count > 0, "local rules index returns matching chunks");
File.Delete(rulesPath);

var sample = args.FirstOrDefault();
if (!string.IsNullOrWhiteSpace(sample) && File.Exists(sample))
{
    var importer = new CampaignImportService();
    var imported = await importer.ImportManifestAsync(sample);
    Assert(imported.Campaign.Locations.Count >= 5, "structured sample campaign imports locations");
    Assert(imported.Campaign.Characters.Any(c => c.CharacterType == "pc"), "structured sample campaign imports a PC");
    Assert(imported.Campaign.Merchants.Count > 0, "structured sample campaign imports merchants");
    Assert(imported.Campaign.Spells.Count >= 2, "structured sample campaign imports deterministic spell definitions");
    var importedAric = imported.Campaign.Characters.First(c => c.Key == "character.aric");
    Assert(importedAric.PreparedSpellIds.Count >= 2 && importedAric.SpellSlots.TryGetValue(1, out var aricLevelOneSlots) && aricLevelOneSlots.Maximum == 2, "structured sample resolves prepared spell keys into character spell state");
    Assert(imported.Campaign.Locations.Any(l => l.DmOnly), "structured sample retains DM-only locations");
    Assert(imported.Campaign.Factions.Count > 0, "structured sample imports factions");
    Assert(imported.Campaign.Relationships.Count >= 3, "structured sample imports cross-entity relationships");
    Assert(imported.Campaign.Secrets.Count > 0 && !imported.Campaign.Secrets[0].Revealed, "structured sample imports unrevealed campaign secrets");
    Assert(imported.Campaign.Timeline.Count > 0 && !imported.Campaign.Timeline[0].Resolved, "structured sample imports scheduled world events");
    var readiness = new CampaignReadinessValidator().Validate(imported.Campaign);
    Assert(readiness.All(i => i.Severity != ReadinessSeverity.Error), "structured sample has no campaign-readiness errors");
    imported.Campaign.Day = 3;
    imported.Campaign.MinuteOfDay = 1310;
    engine.AdvanceTime(imported.Campaign, 20);
    Assert(imported.Campaign.Timeline[0].Resolved, "crossing a scheduled campaign time resolves the timeline event deterministically");
    Assert(imported.Campaign.Events.Any(e => e.Type == "timeline_event" && e.DmOnly), "timeline consequences are recorded in DM-only campaign history");
    Assert(imported.Campaign.Encounters.Count > 0, "structured sample imports encounter templates");
    var planned = imported.Campaign.Encounters.First();
    Assert(planned.Combatants.Count == 2, "encounter quantities expand into persistent combatants");
    Assert(planned.Combatants.All(c => imported.Campaign.Characters.First(x => x.Id == c.CharacterId).Attacks.Count > 0), "encounter member attack metadata compiles into deterministic attack profiles");
    engine.ActivateEncounter(imported.Campaign, planned.Id, includeParty: true);
    Assert(planned.Status == "active" && planned.Combatants.Any(c => imported.Campaign.Characters.First(x => x.Id == c.CharacterId).CharacterType == "pc"), "planned encounters activate and add the party");
}


var expansionCampaign = engine.CreateCampaign("Expansion Smoke");
expansionCampaign.Locations[0].SourceKind = "source_canon";
var canonMerchant = new Merchant { Key = "merchant.smith", Name = "Canon Smith", LocationId = expansionCampaign.PartyLocationId, SourceKind = "source_canon" };
expansionCampaign.Merchants.Add(canonMerchant);
var canonQuest = new Quest { Key = "quest.bridge", Name = "The Broken Bridge", Summary = "Repair the bridge before the caravan arrives.", SourceKind = "source_canon" };
expansionCampaign.Quests.Add(canonQuest);
var canonSecret = new CampaignSecret { Key = "secret.bridge", Title = "Sabotage", Truth = "The bridge was deliberately weakened.", SourceKind = "source_canon" };
expansionCampaign.Secrets.Add(canonSecret);
var expansionPatch = """
{
  "items": [
    {"key":"item.iron_nails","name":"Iron Nails","category":"gear","description":"A small bundle of locally forged nails.","price_gp":1,"source_kind":"ai_expanded"}
  ],
  "merchant_stock_additions": [
    {"merchant_key":"merchant.smith","item_key":"item.iron_nails","quantity":8,"price_gp":1,"source_kind":"ai_expanded"}
  ],
  "characters": [
    {"key":"npc.bridge_carpenter","name":"Mara Reed","character_type":"npc","location_key":"starting-area","public_knowledge":"A practical carpenter willing to inspect damaged timber.","source_kind":"ai_expanded"}
  ],
  "supplements": [
    {"target_key":"quest.bridge","category":"quest_objective","content":"Inspect the damaged bridge supports before choosing a repair method.","dm_only":false,"source_kind":"ai_expanded"},
    {"target_key":"secret.bridge","category":"secret_reveal_condition","content":"Reveal after a successful inspection of the cut support rope.","dm_only":true,"source_kind":"ai_expanded"}
  ]
}
""";
var expansionApplied = new CampaignExpansionApplyService().Apply(expansionCampaign, expansionPatch);
Assert(expansionApplied.AddedObjects == 5, "campaign expansion applies generated objects and supplements without overwriting canon");
Assert(canonMerchant.SourceKind == "source_canon" && canonMerchant.Stock.Single().SourceKind == "ai_expanded", "generated merchant stock is provenance-marked while the canon merchant remains unchanged");
Assert(expansionCampaign.Characters.Single(c => c.Key == "npc.bridge_carpenter").SourceKind == "ai_expanded", "generated NPCs retain ai_expanded provenance");
Assert(expansionCampaign.Supplements.Count == 2 && expansionCampaign.Supplements.All(x => x.SourceKind == "ai_expanded"), "generated supplements retain ai_expanded provenance");
var expandedReadiness = new CampaignReadinessValidator().Validate(expansionCampaign);
Assert(!expandedReadiness.Any(i => i.EntityKey == "quest.bridge" && i.Message.Contains("no structured objectives", StringComparison.OrdinalIgnoreCase)), "generated quest-objective supplements satisfy the readiness gap without mutating canon objectives");
Assert(!expandedReadiness.Any(i => i.EntityKey == "secret.bridge" && i.Message.Contains("no structured reveal condition", StringComparison.OrdinalIgnoreCase)), "generated secret reveal supplements satisfy the readiness gap without mutating canon secret data");
var rehearsalService = new CampaignRehearsalService();
var generatedLocation = new WorldLocation { Key = "location.unreachable", Name = "Unreachable Camp", Discovered = true, SourceKind = "ai_expanded" };
expansionCampaign.Locations.Add(generatedLocation);
var rehearsalWithGap = rehearsalService.Run(expansionCampaign);
Assert(rehearsalWithGap.Findings.Any(f => f.Severity == RehearsalSeverity.Error && f.Scenario == "exploration" && f.EntityKey == "location.unreachable"), "campaign rehearsal detects a player-visible location with no reachable travel path");
expansionCampaign.Connections.Add(new LocationConnection { FromLocationId = expansionCampaign.PartyLocationId!, ToLocationId = generatedLocation.Id, Label = "Road", TravelMinutes = 10, SourceKind = "ai_expanded" });
var rehearsalAfterRepair = rehearsalService.Run(expansionCampaign);
Assert(!rehearsalAfterRepair.Findings.Any(f => f.Severity == RehearsalSeverity.Error && f.Scenario == "exploration" && f.EntityKey == "location.unreachable"), "campaign rehearsal recognizes a repaired travel graph");

Console.WriteLine("ALL NATIVE CORE SMOKE TESTS PASSED");
