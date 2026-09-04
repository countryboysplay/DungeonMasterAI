using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private (D20TestResult Save, string Summary) ResolveSaveForPlayerCasterBeforeDamage(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        bool ritual,
        bool concentrationStarted,
        DiceService dice,
        EncounterState? encounter)
    {
        var save = ResolveSpellSavingThrowOnly(campaign, caster, target, spell, dice, encounter);
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        if (PlayerSaveSpellNeedsDamageRoll(spell, save))
        {
            var pending = CreatePendingSaveSpellDamageRequest(
                campaign,
                caster,
                target,
                spell,
                save,
                castAtLevel,
                upcastLevels,
                usedSlot,
                ritual,
                concentrationStarted,
                encounter);
            return (save, pending.Purpose);
        }

        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var dc = SpellSaveDc(caster);
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave))
            ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, dc);
        var summary = BuildSaveSpellNoDamageSummary(target, spell, save, ability);
        return (save, summary);
    }

    private PendingRollRequest CreatePendingSaveSpellDamageRequest(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        D20TestResult save,
        int castAtLevel,
        int upcastLevels,
        bool usedSlot,
        bool ritual,
        bool concentrationStarted,
        EncounterState? encounter)
    {
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
        var extraRolls = !string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression) ? upcastLevels : 0;
        var formula = BuildSaveSpellDamageFormula(spell, baseRolls, extraRolls);
        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        var resultText = save.Success && spell.HalfDamageOnSuccessfulSave
            ? $"{target.Name} succeeded on the saving throw. Roll {formula}; half the rolled damage will be applied."
            : $"{target.Name} failed the saving throw. Roll {formula} damage.";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter?.Id,
            CombatantId = casterCombatant?.Id,
            Formula = formula,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"{caster.Name}'s {spell.Name}: {resultText}",
            ResolutionKey = "spell_save_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["spell_id"] = spell.Id,
                ["target_id"] = target.Id,
                ["cast_at_level"] = castAtLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["upcast_levels"] = upcastLevels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["used_slot"] = usedSlot ? "true" : "false",
                ["ritual"] = ritual ? "true" : "false",
                ["concentration_started"] = concentrationStarted ? "true" : "false",
                ["save_json"] = JsonSerializer.Serialize(save),
                ["base_damage_expression"] = spell.DamageExpression,
                ["base_rolls"] = baseRolls.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["extra_damage_expression"] = spell.ExtraDamagePerSlotExpression,
                ["extra_rolls"] = extraRolls.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["half_on_success"] = spell.HalfDamageOnSuccessfulSave ? "true" : "false",
                ["save_ability"] = CharacterMechanics.NormalizeAbility(spell.SaveAbility)
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public SpellCastResult ResolvePendingSpellSaveDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int rolledDamage,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        if (rolledDamage is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(rolledDamage));

        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied damage roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals("spell_save_damage", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not saving-throw spell damage.");

        var caster = RequireCharacter(campaign, pending.ActorCharacterId);
        if (!caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending spell damage no longer belongs to a player character.");
        if (!pending.Context.TryGetValue("target_id", out var targetId) || string.IsNullOrWhiteSpace(targetId))
            throw new InvalidOperationException("The pending spell damage is missing its target.");
        if (!pending.Context.TryGetValue("spell_id", out var spellId) || string.IsNullOrWhiteSpace(spellId))
            throw new InvalidOperationException("The pending spell damage is missing its spell.");
        if (!pending.Context.TryGetValue("save_json", out var saveJson) || string.IsNullOrWhiteSpace(saveJson))
            throw new InvalidOperationException("The pending spell damage is missing its saving throw result.");

        var target = RequireCharacter(campaign, targetId);
        var spell = campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The spell for the pending damage roll no longer exists.");
        var save = JsonSerializer.Deserialize<D20TestResult>(saveJson)
            ?? throw new InvalidOperationException("The stored saving throw result could not be restored.");

        EncounterState? encounter = null;
        if (!string.IsNullOrWhiteSpace(pending.EncounterId))
        {
            encounter = RequireEncounter(campaign, pending.EncounterId);
            if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The encounter is no longer active.");
            var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The spellcaster is no longer in the active encounter.");
            if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn(encounter, casterCombatant.Id);
        }
        ValidateSpellTargetType(target, spell);
        ValidateSpellRange(campaign, encounter, caster, target, spell);

        var halfOnSuccess = PendingSaveDamageBool(pending, "half_on_success");
        var appliedDamage = save.Success && halfOnSuccess ? rolledDamage / 2 : rolledDamage;
        campaign.PendingPlayerRoll = null;
        DamageResolutionResult? damage = null;
        if (appliedDamage > 0 && !target.Dead)
            damage = ApplyDamageWithConcentration(campaign, target.Id, appliedDamage, dice, spell.DamageType);

        var ability = pending.Context.TryGetValue("save_ability", out var abilityText) && !string.IsNullOrWhiteSpace(abilityText)
            ? CharacterMechanics.NormalizeAbility(abilityText)
            : CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var dc = save.DifficultyClass;
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave) && !target.Dead)
            ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, dc);

        var castAtLevel = PendingSaveDamageInt(pending, "cast_at_level", spell.Level);
        var usedSlot = PendingSaveDamageBool(pending, "used_slot");
        var ritual = PendingSaveDamageBool(pending, "ritual");
        var concentrationStarted = PendingSaveDamageBool(pending, "concentration_started");
        var resultText = save.Success
            ? halfOnSuccess
                ? $"{target.Name} succeeded on the {ability} save and took {appliedDamage} {spell.DamageType} damage from a player roll of {rolledDamage}."
                : $"{target.Name} succeeded on the {ability} save and took no damage."
            : $"{target.Name} failed the {ability} save and took {appliedDamage} {spell.DamageType} damage from a player roll of {rolledDamage}.";
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave) && !target.Dead)
            resultText += $" {target.Name} gained the {spell.ConditionOnFailedSave.Trim()} condition.";
        if (damage?.Concentration is not null) resultText += $" {damage.Concentration.Summary}";

        var slotText = spell.Level == 0
            ? "as a cantrip"
            : ritual
                ? "as a Ritual without expending a spell slot"
                : $"using a level {castAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}. {resultText}".Trim();
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            caster.Id,
            target.Id,
            castAtLevel,
            usedSlot,
            ritual,
            null,
            save,
            damage,
            0,
            concentrationStarted,
            summary);
    }

    private D20TestResult ResolveSpellSavingThrowOnly(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        DiceService dice,
        EncounterState? encounter)
    {
        if (string.IsNullOrWhiteSpace(spell.SaveAbility))
            throw new InvalidOperationException($"{spell.Name} is configured as a saving-throw spell but has no save ability.");
        var dc = SpellSaveDc(caster);
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var dodgeMode = ability == "dexterity" && targetCombatant is not null && IsDodgeActive(campaign, targetCombatant, target)
            ? D20RollMode.Advantage
            : D20RollMode.Normal;
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(target, ability);
        var typeMode = !string.IsNullOrWhiteSpace(spell.SaveDisadvantageCreatureType)
            && target.CreatureType.Equals(spell.SaveDisadvantageCreatureType, StringComparison.OrdinalIgnoreCase)
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
        var saveMode = CombineAdvantage(CombineAdvantage(dodgeMode, conditionMode), typeMode);
        var roll = dice.RollD20(saveMode);
        var proficient = target.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase) || x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase));
        var coverBonus = ability == "dexterity" && !spell.IgnoreHalfAndThreeQuartersCoverOnSave
            ? GetSpellCoverBonus(encounter, caster.Id, target.Id)
            : 0;
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
        {
            var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, ability));
            var proficiencyModifier = proficient ? Math.Max(0, target.ProficiencyBonus) : 0;
            var exhaustionPenalty = 2 * Math.Clamp(target.ExhaustionLevel, 0, 6);
            var total = roll.ChosenRoll + abilityModifier + proficiencyModifier + coverBonus + effectSaveBonus - exhaustionPenalty;
            return new D20TestResult(roll.RollOne, roll.RollTwo, roll.ChosenRoll, abilityModifier, proficiencyModifier, exhaustionPenalty, total, dc, false,
                $"{ability} saving throw automatically failed because {target.Name}'s condition causes automatic failure on Strength and Dexterity saving throws.");
        }
        return CharacterMechanics.ResolveD20Test(target, ability, dc, roll.RollOne, roll.RollTwo, saveMode, proficient, coverBonus + effectSaveBonus);
    }

    private static bool PlayerSaveSpellNeedsDamageRoll(SpellDefinition spell, D20TestResult save)
    {
        if (string.IsNullOrWhiteSpace(spell.DamageExpression)) return false;
        return !save.Success || spell.HalfDamageOnSuccessfulSave;
    }

    private static string BuildSaveSpellDamageFormula(SpellDefinition spell, int baseRolls, int extraRolls)
    {
        var parts = new List<string>();
        for (var i = 0; i < baseRolls; i++) parts.Add(spell.DamageExpression);
        for (var i = 0; i < extraRolls; i++)
            if (!string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression)) parts.Add(spell.ExtraDamagePerSlotExpression);
        return string.Join(" + ", parts);
    }

    private static string BuildSaveSpellNoDamageSummary(CharacterSheet target, SpellDefinition spell, D20TestResult save, string ability)
    {
        var result = save.Success
            ? $"{target.Name} succeeded on the {ability} save and took no damage."
            : $"{target.Name} failed the {ability} save.";
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave))
            result += $" {target.Name} gained the {spell.ConditionOnFailedSave.Trim()} condition.";
        return result;
    }

    private static int PendingSaveDamageInt(PendingRollRequest pending, string key, int fallback = 0)
        => pending.Context.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool PendingSaveDamageBool(PendingRollRequest pending, string key)
        => pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
}
