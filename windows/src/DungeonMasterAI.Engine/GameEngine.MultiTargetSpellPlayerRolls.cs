using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

internal sealed class PlayerMultiTargetSpellSequenceState
{
    public string CasterId { get; set; } = "";
    public string SpellId { get; set; } = "";
    public int CastAtLevel { get; set; }
    public bool UsedSpellSlot { get; set; }
    public bool Ritual { get; set; }
    public bool ConcentrationStarted { get; set; }
    public string? EncounterId { get; set; }
    public List<string> TargetIds { get; set; } = [];
    public int NextTargetIndex { get; set; }
    public List<SpellTargetResolution> Results { get; set; } = [];
}

public sealed partial class GameEngine
{
    private SpellCastResult BeginPlayerMultiTargetSaveSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        bool ritual,
        bool concentrationStarted,
        EncounterState? encounter,
        IReadOnlyList<string> targetIds,
        DiceService dice)
    {
        var state = new PlayerMultiTargetSpellSequenceState
        {
            CasterId = caster.Id,
            SpellId = spell.Id,
            CastAtLevel = castAtLevel,
            UsedSpellSlot = usedSlot,
            Ritual = ritual,
            ConcentrationStarted = concentrationStarted,
            EncounterId = encounter?.Id,
            TargetIds = targetIds.ToList(),
            NextTargetIndex = 0,
            Results = []
        };
        return AdvancePlayerMultiTargetSaveSpellSequence(campaign, state, dice);
    }

    public SpellCastResult ResolvePendingMultiTargetSpellSavingThrowRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && rollTwo.Value is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollTwo));

        var pending = RequireMultiTargetPending(campaign, pendingRollId, "multi_spell_saving_throw");
        var state = ReadMultiTargetSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireMultiTargetSpell(campaign, state.SpellId);
        var targetIndex = RequireCurrentMultiTargetIndex(state, pending);
        var target = RequireCharacter(campaign, state.TargetIds[targetIndex]);
        var encounter = RequireMultiTargetEncounterIfAny(campaign, state, caster);
        var ability = MultiTargetContextString(pending, "save_ability", CharacterMechanics.NormalizeAbility(spell.SaveAbility));
        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This saving throw requires two d20 results because it has {mode}.");
        var proficient = MultiTargetContextBool(pending, "proficient");
        var coverBonus = MultiTargetContextInt(pending, "cover_bonus");
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
        var dc = pending.TargetNumber ?? SpellSaveDc(caster);
        var save = CharacterMechanics.ResolveD20Test(
            target,
            ability,
            dc,
            rollOne,
            rollTwo,
            mode,
            proficient,
            coverBonus + effectSaveBonus);

        campaign.PendingPlayerRoll = null;
        return ContinuePlayerMultiTargetAfterSave(campaign, state, targetIndex, save, dice, encounter);
    }

    public SpellCastResult ResolvePendingMultiTargetSpellDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int rolledDamage,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (rolledDamage is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(rolledDamage));

        var pending = RequireMultiTargetPending(campaign, pendingRollId, "multi_spell_damage");
        var state = ReadMultiTargetSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireMultiTargetSpell(campaign, state.SpellId);
        var targetIndex = RequireCurrentMultiTargetIndex(state, pending);
        var target = RequireCharacter(campaign, state.TargetIds[targetIndex]);
        var encounter = RequireMultiTargetEncounterIfAny(campaign, state, caster);
        var saveJson = MultiTargetContextString(pending, "save_json", "");
        var save = JsonSerializer.Deserialize<D20TestResult>(saveJson)
            ?? throw new InvalidOperationException("The stored saving throw result could not be restored.");

        var appliedDamage = save.Success && spell.HalfDamageOnSuccessfulSave ? rolledDamage / 2 : rolledDamage;
        campaign.PendingPlayerRoll = null;
        DamageResolutionResult? damage = null;
        if (appliedDamage > 0 && !target.Dead)
            damage = ApplyDamageWithConcentration(campaign, target.Id, appliedDamage, dice, spell.DamageType);

        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var dc = save.DifficultyClass;
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave) && !target.Dead)
            ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, dc);

        var summary = BuildMultiTargetSaveSummary(target, spell, save, appliedDamage, rolledDamage);
        if (damage?.Concentration is not null) summary += $" {damage.Concentration.Summary}";
        state.Results.Add(new SpellTargetResolution(target.Id, target.Name, targetIndex + 1, null, save, damage, 0, summary));
        state.NextTargetIndex++;

        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
        {
            campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "multi_target_spell_sequence";
            campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);
            Touch(campaign);
            var waitSummary = state.NextTargetIndex < state.TargetIds.Count
                ? $"{summary} Resolve {target.Name}'s Concentration save before the next target can continue."
                : $"{summary} Resolve {target.Name}'s Concentration save before the spell finishes resolving.";
            return BuildMultiTargetSequenceResult(campaign, state, waitSummary);
        }

        return AdvancePlayerMultiTargetSaveSpellSequence(campaign, state, dice);
    }

    private SpellCastResult AdvancePlayerMultiTargetSaveSpellSequence(
        CampaignState campaign,
        PlayerMultiTargetSpellSequenceState state,
        DiceService dice)
    {
        if (state.NextTargetIndex >= state.TargetIds.Count)
            return FinalizePlayerMultiTargetSaveSpellSequence(campaign, state);

        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireMultiTargetSpell(campaign, state.SpellId);
        var target = RequireCharacter(campaign, state.TargetIds[state.NextTargetIndex]);
        var encounter = RequireMultiTargetEncounterIfAny(campaign, state, caster);
        if (target.Dead)
        {
            var summary = $"{target.Name} is already dead and is skipped while resolving {spell.Name}.";
            state.Results.Add(new SpellTargetResolution(target.Id, target.Name, state.NextTargetIndex + 1, null, null, null, 0, summary));
            state.NextTargetIndex++;
            return AdvancePlayerMultiTargetSaveSpellSequence(campaign, state, dice);
        }

        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        if (target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
            && !CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
        {
            var pending = CreateMultiTargetSavingThrowRequest(campaign, state, caster, target, spell, encounter);
            return BuildMultiTargetSequenceResult(campaign, state, pending.Purpose);
        }

        var save = ResolveSpellSavingThrowOnly(campaign, caster, target, spell, dice, encounter);
        return ContinuePlayerMultiTargetAfterSave(campaign, state, state.NextTargetIndex, save, dice, encounter);
    }

    private SpellCastResult ContinuePlayerMultiTargetAfterSave(
        CampaignState campaign,
        PlayerMultiTargetSpellSequenceState state,
        int targetIndex,
        D20TestResult save,
        DiceService dice,
        EncounterState? encounter)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireMultiTargetSpell(campaign, state.SpellId);
        var target = RequireCharacter(campaign, state.TargetIds[targetIndex]);
        var needsDamage = PlayerSaveSpellNeedsDamageRoll(spell, save);

        if (needsDamage && caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            var pending = CreateMultiTargetDamageRequest(campaign, state, caster, target, spell, save, encounter);
            return BuildMultiTargetSequenceResult(campaign, state, pending.Purpose);
        }

        var rolledDamage = 0;
        var appliedDamage = 0;
        DamageResolutionResult? damage = null;
        if (needsDamage)
        {
            rolledDamage = RollMultiTargetSpellDamage(caster, spell, state.CastAtLevel, dice);
            appliedDamage = save.Success && spell.HalfDamageOnSuccessfulSave ? rolledDamage / 2 : rolledDamage;
            if (appliedDamage > 0 && !target.Dead)
                damage = ApplyDamageWithConcentration(campaign, target.Id, appliedDamage, dice, spell.DamageType);
        }

        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave) && !target.Dead)
            ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, save.DifficultyClass);
        var summary = BuildMultiTargetSaveSummary(target, spell, save, appliedDamage, rolledDamage);
        if (damage?.Concentration is not null) summary += $" {damage.Concentration.Summary}";
        state.Results.Add(new SpellTargetResolution(target.Id, target.Name, targetIndex + 1, null, save, damage, 0, summary));
        state.NextTargetIndex++;

        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
        {
            campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "multi_target_spell_sequence";
            campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);
            Touch(campaign);
            var waitSummary = state.NextTargetIndex < state.TargetIds.Count
                ? $"{summary} Resolve {target.Name}'s Concentration save before the next target can continue."
                : $"{summary} Resolve {target.Name}'s Concentration save before the spell finishes resolving.";
            return BuildMultiTargetSequenceResult(campaign, state, waitSummary);
        }

        return AdvancePlayerMultiTargetSaveSpellSequence(campaign, state, dice);
    }

    private PendingRollRequest CreateMultiTargetSavingThrowRequest(
        CampaignState campaign,
        PlayerMultiTargetSpellSequenceState state,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        EncounterState? encounter)
    {
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var dc = SpellSaveDc(caster);
        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var dodgeMode = ability == "dexterity" && targetCombatant is not null && IsDodgeActive(campaign, targetCombatant, target)
            ? D20RollMode.Advantage
            : D20RollMode.Normal;
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(target, ability);
        var typeMode = !string.IsNullOrWhiteSpace(spell.SaveDisadvantageCreatureType)
            && target.CreatureType.Equals(spell.SaveDisadvantageCreatureType, StringComparison.OrdinalIgnoreCase)
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
        var mode = CombineAdvantage(CombineAdvantage(dodgeMode, conditionMode), typeMode);
        var proficient = target.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase) || x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase));
        var coverBonus = ability == "dexterity" && !spell.IgnoreHalfAndThreeQuartersCoverOnSave
            ? GetSpellCoverBonus(encounter, caster.Id, target.Id)
            : 0;
        var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, ability));
        var proficiencyModifier = proficient ? Math.Max(0, target.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(target.ExhaustionLevel, 0, 6);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = target.Id,
            EncounterId = encounter?.Id,
            CombatantId = targetCombatant?.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"Target {state.NextTargetIndex + 1} of {caster.Name}'s {spell.Name}: {target.Name} must make a {ability} saving throw{modeText} against DC {dc}.",
            ResolutionKey = "multi_spell_saving_throw",
            Modifier = abilityModifier + proficiencyModifier + coverBonus - exhaustionPenalty,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
                ["target_index"] = state.NextTargetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["target_id"] = target.Id,
                ["save_ability"] = ability,
                ["proficient"] = proficient ? "true" : "false",
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    private PendingRollRequest CreateMultiTargetDamageRequest(
        CampaignState campaign,
        PlayerMultiTargetSpellSequenceState state,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        D20TestResult save,
        EncounterState? encounter)
    {
        var upcastLevels = Math.Max(0, state.CastAtLevel - spell.Level);
        var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
        var extraRolls = !string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression) ? upcastLevels : 0;
        var formula = BuildSaveSpellDamageFormula(spell, baseRolls, extraRolls);
        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        var outcomeText = save.Success && spell.HalfDamageOnSuccessfulSave
            ? $"{target.Name} succeeded. Roll {formula}; half will be applied."
            : $"{target.Name} failed. Roll {formula} damage.";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter?.Id,
            CombatantId = casterCombatant?.Id,
            Formula = formula,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"Target {targetIndexDisplay(state)} of {caster.Name}'s {spell.Name}: {outcomeText}",
            ResolutionKey = "multi_spell_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
                ["target_index"] = state.NextTargetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["target_id"] = target.Id,
                ["save_json"] = JsonSerializer.Serialize(save),
                ["base_damage_expression"] = spell.DamageExpression,
                ["base_rolls"] = baseRolls.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["extra_damage_expression"] = spell.ExtraDamagePerSlotExpression,
                ["extra_rolls"] = extraRolls.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    private static int targetIndexDisplay(PlayerMultiTargetSpellSequenceState state) => state.NextTargetIndex + 1;

    private static int RollMultiTargetSpellDamage(CharacterSheet caster, SpellDefinition spell, int castAtLevel, DiceService dice)
    {
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
        var total = 0;
        for (var i = 0; i < baseRolls; i++) total += dice.RollDamage(spell.DamageExpression);
        if (!string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression))
            for (var i = 0; i < upcastLevels; i++) total += dice.RollDamage(spell.ExtraDamagePerSlotExpression);
        return total;
    }

    private static string BuildMultiTargetSaveSummary(CharacterSheet target, SpellDefinition spell, D20TestResult save, int appliedDamage, int rolledDamage)
    {
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        if (string.IsNullOrWhiteSpace(spell.DamageExpression))
            return save.Success
                ? $"{target.Name} succeeded on the {ability} save."
                : $"{target.Name} failed the {ability} save.";
        if (save.Success)
            return spell.HalfDamageOnSuccessfulSave
                ? $"{target.Name} succeeded on the {ability} save and took {appliedDamage} {spell.DamageType} damage from a roll of {rolledDamage}."
                : $"{target.Name} succeeded on the {ability} save and took no damage.";
        return $"{target.Name} failed the {ability} save and took {appliedDamage} {spell.DamageType} damage from a roll of {rolledDamage}.";
    }

    private SpellCastResult FinalizePlayerMultiTargetSaveSpellSequence(CampaignState campaign, PlayerMultiTargetSpellSequenceState state)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireMultiTargetSpell(campaign, state.SpellId);
        var slotText = spell.Level == 0
            ? "as a cantrip"
            : state.Ritual
                ? "as a Ritual without expending a spell slot"
                : $"using a level {state.CastAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText} against {state.TargetIds.Count} target{(state.TargetIds.Count == 1 ? "" : "s")}. "
            + string.Join(" ", state.Results.Select(r => r.Summary));
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return BuildMultiTargetSequenceResult(campaign, state, summary);
    }

    private SpellCastResult BuildMultiTargetSequenceResult(CampaignState campaign, PlayerMultiTargetSpellSequenceState state, string summary)
    {
        var spell = RequireMultiTargetSpell(campaign, state.SpellId);
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            state.CasterId,
            null,
            state.CastAtLevel,
            state.UsedSpellSlot,
            state.Ritual,
            null,
            null,
            null,
            0,
            state.ConcentrationStarted,
            summary,
            state.Results.ToArray());
    }

    private string? ResumePlayerMultiTargetSpellSequenceAfterConcentration(
        CampaignState campaign,
        IReadOnlyDictionary<string, string> continuationContext,
        DiceService dice)
    {
        if (!continuationContext.TryGetValue("continuation_sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The multi-target spell continuation is missing its sequence state.");
        var state = JsonSerializer.Deserialize<PlayerMultiTargetSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The multi-target spell continuation could not be restored.");
        return AdvancePlayerMultiTargetSaveSpellSequence(campaign, state, dice).Summary;
    }

    private static PendingRollRequest RequireMultiTargetPending(CampaignState campaign, string pendingRollId, string resolutionKey)
    {
        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals(resolutionKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not '{resolutionKey}'.");
        return pending;
    }

    private static PlayerMultiTargetSpellSequenceState ReadMultiTargetSequence(PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The pending multi-target spell roll is missing its sequence state.");
        return JsonSerializer.Deserialize<PlayerMultiTargetSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The pending multi-target spell sequence could not be restored.");
    }

    private static int RequireCurrentMultiTargetIndex(PlayerMultiTargetSpellSequenceState state, PendingRollRequest pending)
    {
        var pendingIndex = MultiTargetContextInt(pending, "target_index", -1);
        if (pendingIndex < 0 || pendingIndex >= state.TargetIds.Count || pendingIndex != state.NextTargetIndex)
            throw new InvalidOperationException("The pending target index no longer matches the saved multi-target sequence.");
        return pendingIndex;
    }

    private EncounterState? RequireMultiTargetEncounterIfAny(CampaignState campaign, PlayerMultiTargetSpellSequenceState state, CharacterSheet caster)
    {
        if (string.IsNullOrWhiteSpace(state.EncounterId)) return null;
        var encounter = RequireEncounter(campaign, state.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The multi-target spell's encounter is no longer active.");
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The multi-target spellcaster is no longer in the encounter.");
        EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
    }

    private static SpellDefinition RequireMultiTargetSpell(CampaignState campaign, string spellId)
    {
        return campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The spell for the pending multi-target sequence no longer exists.");
    }

    private static int MultiTargetContextInt(PendingRollRequest pending, string key, int fallback = 0)
        => pending.Context.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool MultiTargetContextBool(PendingRollRequest pending, string key)
        => pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static string MultiTargetContextString(PendingRollRequest pending, string key, string fallback)
        => pending.Context.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
