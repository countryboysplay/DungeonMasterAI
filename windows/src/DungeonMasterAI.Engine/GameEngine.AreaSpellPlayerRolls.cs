using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

internal sealed class PlayerAreaSpellTargetState
{
    public string CombatantId { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public D20TestResult? SavingThrow { get; set; }
    public bool Skipped { get; set; }
}

internal sealed class PlayerAreaSpellSequenceState
{
    public string CasterId { get; set; } = "";
    public string SpellId { get; set; } = "";
    public int CastAtLevel { get; set; }
    public bool UsedSpellSlot { get; set; }
    public bool ConcentrationStarted { get; set; }
    public bool ReadiedReaction { get; set; }
    public string EncounterId { get; set; } = "";
    public int PointX { get; set; }
    public int PointY { get; set; }
    public string Direction { get; set; } = "north";
    public List<PlayerAreaSpellTargetState> Targets { get; set; } = [];
    public int NextSaveIndex { get; set; }
    public int NextApplyIndex { get; set; }
    public int? SharedDamageRoll { get; set; }
    public List<SpellTargetResolution> Results { get; set; } = [];
}

public sealed partial class GameEngine
{
    private SpellCastResult BeginPlayerAreaSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSpellSlot,
        bool concentrationStarted,
        EncounterState encounter,
        int pointX,
        int pointY,
        string direction,
        IReadOnlyList<string> targetCombatantIds,
        DiceService dice,
        bool readiedReaction = false)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(targetCombatantIds);
        ArgumentNullException.ThrowIfNull(dice);

        var state = new PlayerAreaSpellSequenceState
        {
            CasterId = caster.Id,
            SpellId = spell.Id,
            CastAtLevel = castAtLevel,
            UsedSpellSlot = usedSpellSlot,
            ConcentrationStarted = concentrationStarted,
            ReadiedReaction = readiedReaction,
            EncounterId = encounter.Id,
            PointX = pointX,
            PointY = pointY,
            Direction = string.IsNullOrWhiteSpace(direction) ? "north" : direction,
            Targets = targetCombatantIds.Select(combatantId =>
            {
                var combatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(combatantId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("An affected area-spell combatant no longer exists.");
                return new PlayerAreaSpellTargetState
                {
                    CombatantId = combatant.Id,
                    CharacterId = combatant.CharacterId
                };
            }).ToList()
        };

        return AdvancePlayerAreaSpellSavingThrows(campaign, state, dice);
    }

    public SpellCastResult ResolvePendingAreaSpellSavingThrowRoll(
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

        var pending = RequireAreaSpellPending(campaign, pendingRollId, "area_spell_saving_throw");
        var state = ReadAreaSpellSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        var encounter = RequireAreaSequenceEncounter(campaign, state, caster);
        var targetIndex = RequireAreaTargetIndex(state, pending);
        var targetState = state.Targets[targetIndex];
        var combatant = RequireAreaTargetCombatant(encounter, targetState);
        var target = RequireCharacter(campaign, targetState.CharacterId);

        if (!target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending area saving throw no longer belongs to a player character.");
        if (target.Dead)
            throw new InvalidOperationException($"{target.Name} is already dead.");

        var ability = AreaContextString(pending, "save_ability", CharacterMechanics.NormalizeAbility(spell.SaveAbility));
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
            throw new InvalidOperationException($"{target.Name}'s current condition now causes this {ability} saving throw to fail automatically. Re-resolve the spell from authoritative state.");

        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This saving throw requires two d20 results because it has {mode}.");
        var proficient = AreaContextBool(pending, "proficient");
        var coverBonus = AreaContextInt(pending, "cover_bonus");
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

        targetState.SavingThrow = save;
        state.NextSaveIndex++;
        campaign.PendingPlayerRoll = null;
        Touch(campaign);
        Log(campaign, "player_roll_resolved", $"{target.Name} resolved {spell.Name}'s {ability} saving throw with player-supplied d20 result {save.ChosenRoll} ({save.Total} vs DC {dc}).", dmOnly: true);
        return AdvancePlayerAreaSpellSavingThrows(campaign, state, dice);
    }

    public SpellCastResult ResolvePendingAreaSpellDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int rolledDamage,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (rolledDamage is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(rolledDamage));

        var pending = RequireAreaSpellPending(campaign, pendingRollId, "area_spell_damage");
        var state = ReadAreaSpellSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        _ = RequireAreaSequenceEncounter(campaign, state, caster);
        if (!caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending area damage roll no longer belongs to a player character.");
        if (state.NextSaveIndex < state.Targets.Count)
            throw new InvalidOperationException("All area saving throws must resolve before the shared damage roll.");

        state.SharedDamageRoll = rolledDamage;
        campaign.PendingPlayerRoll = null;
        Touch(campaign);
        Log(campaign, "player_roll_resolved", $"{caster.Name} supplied {rolledDamage} total damage for {spell.Name}. The same damage roll will be used for every affected target.", dmOnly: true);
        return ApplyPlayerAreaSpellResults(campaign, state, dice);
    }

    private SpellCastResult AdvancePlayerAreaSpellSavingThrows(
        CampaignState campaign,
        PlayerAreaSpellSequenceState state,
        DiceService dice)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        var encounter = RequireAreaSequenceEncounter(campaign, state, caster);

        while (state.NextSaveIndex < state.Targets.Count)
        {
            var targetState = state.Targets[state.NextSaveIndex];
            var combatant = RequireAreaTargetCombatant(encounter, targetState);
            var target = RequireCharacter(campaign, targetState.CharacterId);

            if (target.Dead)
            {
                targetState.Skipped = true;
                state.NextSaveIndex++;
                continue;
            }

            var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
            if (target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                && !CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
            {
                var pending = CreatePendingAreaSpellSavingThrow(campaign, state, caster, target, combatant, spell, encounter);
                return BuildAreaSpellSequenceResult(campaign, state, pending.Purpose);
            }

            targetState.SavingThrow = ResolveAreaSpellSavingThrowAutomatically(campaign, state, caster, target, combatant, spell, encounter, dice);
            state.NextSaveIndex++;
        }

        return EnsureAreaSpellSharedDamage(campaign, state, dice);
    }

    private SpellCastResult EnsureAreaSpellSharedDamage(
        CampaignState campaign,
        PlayerAreaSpellSequenceState state,
        DiceService dice)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        var encounter = RequireAreaSequenceEncounter(campaign, state, caster);
        var needsDamage = !string.IsNullOrWhiteSpace(spell.DamageExpression)
            && state.Targets.Any(target => !target.Skipped
                && target.SavingThrow is not null
                && PlayerSaveSpellNeedsDamageRoll(spell, target.SavingThrow));

        if (!needsDamage)
        {
            state.SharedDamageRoll = 0;
            return ApplyPlayerAreaSpellResults(campaign, state, dice);
        }

        if (state.SharedDamageRoll.HasValue)
            return ApplyPlayerAreaSpellResults(campaign, state, dice);

        if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            var pending = CreatePendingAreaSpellDamage(campaign, state, caster, spell, encounter);
            return BuildAreaSpellSequenceResult(campaign, state, pending.Purpose);
        }

        state.SharedDamageRoll = RollAreaSpellDamage(caster, spell, state.CastAtLevel, dice);
        return ApplyPlayerAreaSpellResults(campaign, state, dice);
    }

    private SpellCastResult ApplyPlayerAreaSpellResults(
        CampaignState campaign,
        PlayerAreaSpellSequenceState state,
        DiceService dice)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        var encounter = RequireAreaSequenceEncounter(campaign, state, caster);
        var rolledDamage = state.SharedDamageRoll ?? 0;

        while (state.NextApplyIndex < state.Targets.Count)
        {
            var targetIndex = state.NextApplyIndex;
            var targetState = state.Targets[targetIndex];
            var combatant = RequireAreaTargetCombatant(encounter, targetState);
            var target = RequireCharacter(campaign, targetState.CharacterId);
            state.NextApplyIndex++;

            if (targetState.Skipped || targetState.SavingThrow is null)
            {
                var skippedSummary = $"{target.Name} was already dead when {spell.Name} resolved and was skipped.";
                state.Results.Add(new SpellTargetResolution(target.Id, target.Name, targetIndex + 1, null, null, null, 0, skippedSummary));
                continue;
            }

            var save = targetState.SavingThrow;
            var appliedDamage = save.Success
                ? spell.HalfDamageOnSuccessfulSave ? rolledDamage / 2 : 0
                : rolledDamage;

            DamageResolutionResult? damage = null;
            if (appliedDamage > 0 && !target.Dead)
                damage = ApplyDamageWithConcentration(campaign, target.Id, appliedDamage, dice, spell.DamageType);

            var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
            if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave) && !target.Dead)
                ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, save.DifficultyClass);

            var pushText = "";
            if (!save.Success && spell.PushFeetOnFailedSave > 0 && combatant.Positioned)
            {
                var moved = PushAreaTarget(encounter, state.PointX, state.PointY, combatant, spell.PushFeetOnFailedSave);
                if (moved > 0)
                    pushText = $" {target.Name} was pushed {moved} feet away from {spell.Name}'s point of origin.";
            }

            var summary = BuildAreaSpellTargetSummary(target, spell, save, appliedDamage, rolledDamage) + pushText;
            if (damage?.Concentration is not null)
                summary += $" {damage.Concentration.Summary}";
            state.Results.Add(new SpellTargetResolution(target.Id, target.Name, targetIndex + 1, null, save, damage, 0, summary));

            if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
            {
                campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "area_spell_sequence";
                campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);
                Touch(campaign);
                var waitSummary = state.NextApplyIndex < state.Targets.Count
                    ? $"{summary} Resolve {target.Name}'s Concentration save before {spell.Name} continues to the next affected creature."
                    : $"{summary} Resolve {target.Name}'s Concentration save before {spell.Name} finishes resolving.";
                return BuildAreaSpellSequenceResult(campaign, state, waitSummary);
            }
        }

        return FinalizePlayerAreaSpellSequence(campaign, state);
    }

    private PendingRollRequest CreatePendingAreaSpellSavingThrow(
        CampaignState campaign,
        PlayerAreaSpellSequenceState state,
        CharacterSheet caster,
        CharacterSheet target,
        CombatantState combatant,
        SpellDefinition spell,
        EncounterState encounter)
    {
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var dc = SpellSaveDc(caster);
        var dodgeMode = ability == "dexterity" && IsDodgeActive(campaign, combatant, target)
            ? D20RollMode.Advantage
            : D20RollMode.Normal;
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(target, ability);
        var typeMode = !string.IsNullOrWhiteSpace(spell.SaveDisadvantageCreatureType)
            && target.CreatureType.Equals(spell.SaveDisadvantageCreatureType, StringComparison.OrdinalIgnoreCase)
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
        var mode = CombineAdvantage(CombineAdvantage(dodgeMode, conditionMode), typeMode);
        var proficient = target.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase)
            || x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase));
        var coverBonus = ability == "dexterity" && !spell.IgnoreHalfAndThreeQuartersCoverOnSave
            ? Math.Min(5, GetAreaCoverBonus(encounter, state.PointX, state.PointY, combatant.GridX, combatant.GridY))
            : 0;
        var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, ability));
        var proficiencyModifier = proficient ? Math.Max(0, target.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(target.ExhaustionLevel, 0, 6);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var coverText = coverBonus > 0 ? $" including +{coverBonus} from area cover" : "";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = target.Id,
            EncounterId = encounter.Id,
            CombatantId = combatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{target.Name} must make a {ability} saving throw{modeText} against {caster.Name}'s {spell.Name}, DC {dc}{coverText}. ({state.NextSaveIndex + 1} of {state.Targets.Count} affected creatures)",
            ResolutionKey = "area_spell_saving_throw",
            Modifier = abilityModifier + proficiencyModifier + coverBonus - exhaustionPenalty,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
                ["target_index"] = state.NextSaveIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

    private PendingRollRequest CreatePendingAreaSpellDamage(
        CampaignState campaign,
        PlayerAreaSpellSequenceState state,
        CharacterSheet caster,
        SpellDefinition spell,
        EncounterState encounter)
    {
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var upcastLevels = Math.Max(0, state.CastAtLevel - spell.Level);
        var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
        var extraRolls = !string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression) ? upcastLevels : 0;
        var formula = BuildSaveSpellDamageFormula(spell, baseRolls, extraRolls);
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));

        var pending = new PendingRollRequest
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter.Id,
            CombatantId = casterCombatant?.Id,
            Formula = formula,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"Roll {formula} damage for {caster.Name}'s {spell.Name}. This single damage roll will be applied to every affected creature according to its saving throw.",
            ResolutionKey = "area_spell_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
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

    private D20TestResult ResolveAreaSpellSavingThrowAutomatically(
        CampaignState campaign,
        PlayerAreaSpellSequenceState state,
        CharacterSheet caster,
        CharacterSheet target,
        CombatantState combatant,
        SpellDefinition spell,
        EncounterState encounter,
        DiceService dice)
    {
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var dc = SpellSaveDc(caster);
        var dodgeMode = ability == "dexterity" && IsDodgeActive(campaign, combatant, target)
            ? D20RollMode.Advantage
            : D20RollMode.Normal;
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(target, ability);
        var typeMode = !string.IsNullOrWhiteSpace(spell.SaveDisadvantageCreatureType)
            && target.CreatureType.Equals(spell.SaveDisadvantageCreatureType, StringComparison.OrdinalIgnoreCase)
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
        var saveMode = CombineAdvantage(CombineAdvantage(dodgeMode, conditionMode), typeMode);
        var rolls = dice.RollD20(saveMode);
        var proficient = target.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase)
            || x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase));
        var coverBonus = ability == "dexterity" && !spell.IgnoreHalfAndThreeQuartersCoverOnSave
            ? Math.Min(5, GetAreaCoverBonus(encounter, state.PointX, state.PointY, combatant.GridX, combatant.GridY))
            : 0;
        var effectBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);

        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
        {
            var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, ability));
            var proficiencyModifier = proficient ? Math.Max(0, target.ProficiencyBonus) : 0;
            var exhaustionPenalty = 2 * Math.Clamp(target.ExhaustionLevel, 0, 6);
            var total = rolls.ChosenRoll + abilityModifier + proficiencyModifier + coverBonus + effectBonus - exhaustionPenalty;
            return new D20TestResult(
                rolls.RollOne,
                rolls.RollTwo,
                rolls.ChosenRoll,
                abilityModifier,
                proficiencyModifier,
                exhaustionPenalty,
                total,
                dc,
                false,
                $"{ability} saving throw automatically failed.");
        }

        return CharacterMechanics.ResolveD20Test(
            target,
            ability,
            dc,
            rolls.RollOne,
            rolls.RollTwo,
            saveMode,
            proficient,
            coverBonus + effectBonus);
    }

    private static int RollAreaSpellDamage(CharacterSheet caster, SpellDefinition spell, int castAtLevel, DiceService dice)
    {
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
        var total = 0;
        for (var i = 0; i < baseRolls; i++)
            total += dice.RollDamage(spell.DamageExpression);
        if (!string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression))
            for (var i = 0; i < upcastLevels; i++)
                total += dice.RollDamage(spell.ExtraDamagePerSlotExpression);
        return total;
    }

    private static string BuildAreaSpellTargetSummary(
        CharacterSheet target,
        SpellDefinition spell,
        D20TestResult save,
        int appliedDamage,
        int rolledDamage)
    {
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        if (string.IsNullOrWhiteSpace(spell.DamageExpression))
            return save.Success
                ? $"{target.Name} succeeded on the {ability} save."
                : $"{target.Name} failed the {ability} save.";
        if (save.Success)
            return spell.HalfDamageOnSuccessfulSave
                ? $"{target.Name} succeeded on the {ability} save and took {appliedDamage} {spell.DamageType} damage from the shared roll of {rolledDamage}."
                : $"{target.Name} succeeded on the {ability} save and took no damage.";
        return $"{target.Name} failed the {ability} save and took {appliedDamage} {spell.DamageType} damage from the shared roll of {rolledDamage}.";
    }

    private SpellCastResult FinalizePlayerAreaSpellSequence(CampaignState campaign, PlayerAreaSpellSequenceState state)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        var slotText = spell.Level == 0
            ? "as a cantrip"
            : state.ReadiedReaction
                ? $"from the level {state.CastAtLevel} slot expended when it was readied"
                : $"using a level {state.CastAtLevel} spell slot";
        var environmentText = string.IsNullOrWhiteSpace(spell.EnvironmentalEffect)
            ? ""
            : $" {spell.EnvironmentalEffect}";
        var verb = state.ReadiedReaction ? "released readied" : "cast";
        var summary = $"{caster.Name} {verb} {spell.Name} {slotText}, affecting {state.Results.Count} creature{(state.Results.Count == 1 ? "" : "s")}. {string.Join(" ", state.Results.Select(r => r.Summary))}{environmentText}".Trim();
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return BuildAreaSpellSequenceResult(campaign, state, summary);
    }

    private SpellCastResult BuildAreaSpellSequenceResult(CampaignState campaign, PlayerAreaSpellSequenceState state, string summary)
    {
        var spell = RequireAreaSequenceSpell(campaign, state.SpellId);
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            state.CasterId,
            null,
            state.CastAtLevel,
            state.UsedSpellSlot,
            false,
            null,
            null,
            null,
            0,
            state.ConcentrationStarted,
            summary,
            state.Results.ToArray());
    }

    private string? ResumePlayerAreaSpellSequenceAfterConcentration(
        CampaignState campaign,
        IReadOnlyDictionary<string, string> continuationContext,
        DiceService dice)
    {
        if (!continuationContext.TryGetValue("continuation_sequence_json", out var json)
            || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The area-spell continuation is missing its sequence state.");
        var state = JsonSerializer.Deserialize<PlayerAreaSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The area-spell continuation could not be restored.");
        return ApplyPlayerAreaSpellResults(campaign, state, dice).Summary;
    }

    private static PendingRollRequest RequireAreaSpellPending(CampaignState campaign, string pendingRollId, string resolutionKey)
    {
        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals(resolutionKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not '{resolutionKey}'.");
        return pending;
    }

    private static PlayerAreaSpellSequenceState ReadAreaSpellSequence(PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The pending area-spell roll is missing its sequence state.");
        return JsonSerializer.Deserialize<PlayerAreaSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The pending area-spell sequence could not be restored.");
    }

    private static int RequireAreaTargetIndex(PlayerAreaSpellSequenceState state, PendingRollRequest pending)
    {
        var pendingIndex = AreaContextInt(pending, "target_index", -1);
        if (pendingIndex < 0 || pendingIndex >= state.Targets.Count || pendingIndex != state.NextSaveIndex)
            throw new InvalidOperationException("The pending area-spell target index no longer matches the saved sequence.");
        return pendingIndex;
    }

    private EncounterState RequireAreaSequenceEncounter(CampaignState campaign, PlayerAreaSpellSequenceState state, CharacterSheet caster)
    {
        var encounter = RequireEncounter(campaign, state.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The area spell's encounter is no longer active.");
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The area spellcaster is no longer in the encounter.");
        if (!state.ReadiedReaction)
            EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
    }

    private static CombatantState RequireAreaTargetCombatant(EncounterState encounter, PlayerAreaSpellTargetState target)
    {
        return encounter.Combatants.FirstOrDefault(c => c.Id.Equals(target.CombatantId, StringComparison.OrdinalIgnoreCase)
            && c.CharacterId.Equals(target.CharacterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("An affected area-spell combatant no longer matches the frozen target sequence.");
    }

    private static SpellDefinition RequireAreaSequenceSpell(CampaignState campaign, string spellId)
    {
        return campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The spell for the pending area sequence no longer exists.");
    }

    private static int AreaContextInt(PendingRollRequest pending, string key, int fallback = 0)
        => pending.Context.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool AreaContextBool(PendingRollRequest pending, string key)
        => pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static string AreaContextString(PendingRollRequest pending, string key, string fallback)
        => pending.Context.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
