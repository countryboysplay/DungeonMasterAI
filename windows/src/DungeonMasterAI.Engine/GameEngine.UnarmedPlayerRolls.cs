using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingRollRequest RequestUnarmedGrappleSaveRoll(
        CampaignState campaign,
        string encounterId,
        string attackerCombatantId,
        string targetCombatantId,
        string? targetSaveAbility = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var attackerCombatant = RequireCombatant(encounter, attackerCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        if (attackerCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot grapple itself.");

        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePlayerCharacter(target, "Grapple saving throw");
        EnsureCanTakeAttackOption(attacker);
        EnsureAttackAvailableWithoutConsuming(attackerCombatant, attacker);
        EnsureWithinUnarmedRange(attackerCombatant, targetCombatant, attacker, target);
        if (SizeRank(target.Size) > SizeRank(attacker.Size) + 1)
            throw new InvalidOperationException($"{target.Name} is too large for {attacker.Name} to grapple with an Unarmed Strike.");
        if (AvailableGrappleHands(encounter, attackerCombatant, attacker) <= 0)
            throw new InvalidOperationException($"{attacker.Name} needs a free hand to grapple another creature.");
        if (encounter.Grapples.Any(g => g.GrapplerCombatantId == attackerCombatant.Id && g.TargetCombatantId == targetCombatant.Id))
            throw new InvalidOperationException($"{attacker.Name} is already grappling {target.Name}.");

        var dc = UnarmedSaveDc(attacker);
        var saveAbility = ChooseStrengthOrDexteritySave(target, targetSaveAbility);
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, saveAbility))
            throw new InvalidOperationException($"{target.Name} automatically fails this {saveAbility} saving throw; no player d20 is required.");
        var mode = CharacterMechanics.SavingThrowModeFromConditions(target, saveAbility);
        var proficient = IsSavingThrowProficient(target, saveAbility);
        var modifier = SavingThrowBaseModifier(target, saveAbility, proficient);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";

        // Declaring Grapple spends one attack immediately. The target's saving throw then gates the result.
        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var pending = new PendingRollRequest
        {
            ActorCharacterId = target.Id,
            EncounterId = encounter.Id,
            CombatantId = targetCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{attacker.Name} attempts to grapple {target.Name}. {target.Name} must roll a {saveAbility} saving throw{modeText} against DC {dc}.",
            ResolutionKey = "unarmed_grapple_save",
            Modifier = modifier,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["attacker_combatant_id"] = attackerCombatant.Id,
                ["save_ability"] = saveAbility,
                ["proficient"] = proficient ? "true" : "false"
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public GrappleResult ResolvePendingUnarmedGrappleSaveRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "unarmed_grapple_save");
        var encounter = RequirePendingEncounter(campaign, pending);
        var targetCombatant = RequirePendingCombatant(encounter, pending);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePendingActor(pending, target);
        EnsurePlayerCharacter(target, "Grapple saving throw");
        var attackerCombatant = RequireContextCombatant(encounter, pending, "attacker_combatant_id", "grapple attacker");
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        EnsureWithinUnarmedRange(attackerCombatant, targetCombatant, attacker, target);
        if (encounter.Grapples.Any(g => g.GrapplerCombatantId == attackerCombatant.Id && g.TargetCombatantId == targetCombatant.Id))
            throw new InvalidOperationException($"{attacker.Name} is already grappling {target.Name}.");

        var saveAbility = RequiredPendingContext(pending, "save_ability");
        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "grapple saving throw");
        var proficient = PendingContextBool(pending, "proficient");
        var dc = pending.TargetNumber ?? UnarmedSaveDc(attacker);
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
        var save = CharacterMechanics.ResolveD20Test(target, saveAbility, dc, rollOne, rollTwo, mode, proficient, effectSaveBonus);
        var grappled = !save.Success;
        if (grappled)
        {
            encounter.Grapples.Add(new GrappleState
            {
                GrapplerCombatantId = attackerCombatant.Id,
                TargetCombatantId = targetCombatant.Id,
                EscapeDc = dc,
                ReachFeet = 5
            });
            AddConditionInternal(target, "Grappled");
            targetCombatant.MovementRemainingFeet = 0;
        }

        campaign.PendingPlayerRoll = null;
        var summary = grappled
            ? $"{attacker.Name} grappled {target.Name}. {target.Name} failed the {saveAbility} save against DC {dc}."
            : $"{target.Name} resisted {attacker.Name}'s grapple with a successful {saveAbility} save against DC {dc}.";
        Touch(campaign);
        Log(campaign, "unarmed_grapple", summary);
        Log(campaign, "player_roll_resolved", $"{target.Name}'s player-supplied grapple save d20 resolved as {save.ChosenRoll} ({save.Total} vs DC {dc}).", dmOnly: true);
        return new GrappleResult(encounter.Id, attackerCombatant.Id, targetCombatant.Id, dc, saveAbility, save, grappled, summary);
    }

    public PendingRollRequest RequestUnarmedShoveSaveRoll(
        CampaignState campaign,
        string encounterId,
        string attackerCombatantId,
        string targetCombatantId,
        string effect,
        string? targetSaveAbility = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var attackerCombatant = RequireCombatant(encounter, attackerCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        if (attackerCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot shove itself.");

        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePlayerCharacter(target, "Shove saving throw");
        EnsureCanTakeAttackOption(attacker);
        EnsureAttackAvailableWithoutConsuming(attackerCombatant, attacker);
        EnsureWithinUnarmedRange(attackerCombatant, targetCombatant, attacker, target);
        if (SizeRank(target.Size) > SizeRank(attacker.Size) + 1)
            throw new InvalidOperationException($"{target.Name} is too large for {attacker.Name} to shove with an Unarmed Strike.");
        var normalizedEffect = (effect ?? "prone").Trim().ToLowerInvariant();
        if (normalizedEffect is not ("prone" or "push"))
            throw new ArgumentException("Shove effect must be 'prone' or 'push'.", nameof(effect));

        var dc = UnarmedSaveDc(attacker);
        var saveAbility = ChooseStrengthOrDexteritySave(target, targetSaveAbility);
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, saveAbility))
            throw new InvalidOperationException($"{target.Name} automatically fails this {saveAbility} saving throw; no player d20 is required.");
        var mode = CharacterMechanics.SavingThrowModeFromConditions(target, saveAbility);
        var proficient = IsSavingThrowProficient(target, saveAbility);
        var modifier = SavingThrowBaseModifier(target, saveAbility, proficient);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";

        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var pending = new PendingRollRequest
        {
            ActorCharacterId = target.Id,
            EncounterId = encounter.Id,
            CombatantId = targetCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{attacker.Name} attempts to shove {target.Name} ({normalizedEffect}). {target.Name} must roll a {saveAbility} saving throw{modeText} against DC {dc}.",
            ResolutionKey = "unarmed_shove_save",
            Modifier = modifier,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["attacker_combatant_id"] = attackerCombatant.Id,
                ["save_ability"] = saveAbility,
                ["effect"] = normalizedEffect,
                ["proficient"] = proficient ? "true" : "false"
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public ShoveResult ResolvePendingUnarmedShoveSaveRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "unarmed_shove_save");
        var encounter = RequirePendingEncounter(campaign, pending);
        var targetCombatant = RequirePendingCombatant(encounter, pending);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePendingActor(pending, target);
        EnsurePlayerCharacter(target, "Shove saving throw");
        var attackerCombatant = RequireContextCombatant(encounter, pending, "attacker_combatant_id", "shove attacker");
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        EnsureWithinUnarmedRange(attackerCombatant, targetCombatant, attacker, target);

        var saveAbility = RequiredPendingContext(pending, "save_ability");
        var effect = RequiredPendingContext(pending, "effect");
        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "shove saving throw");
        var proficient = PendingContextBool(pending, "proficient");
        var dc = pending.TargetNumber ?? UnarmedSaveDc(attacker);
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
        var save = CharacterMechanics.ResolveD20Test(target, saveAbility, dc, rollOne, rollTwo, mode, proficient, effectSaveBonus);
        var succeeded = !save.Success;
        if (succeeded && effect == "prone")
        {
            AddConditionInternal(target, "Prone");
        }
        else if (succeeded)
        {
            PushTargetOneSquare(encounter, attackerCombatant, targetCombatant, attacker, target);
            EndInvalidGrapples(campaign, encounter);
        }

        campaign.PendingPlayerRoll = null;
        var effectText = effect == "prone" ? "knocked Prone" : "pushed 5 feet away";
        var summary = succeeded
            ? $"{attacker.Name} shoved {target.Name}; {target.Name} failed the {saveAbility} save against DC {dc} and was {effectText}."
            : $"{target.Name} resisted {attacker.Name}'s shove with a successful {saveAbility} save against DC {dc}.";
        Touch(campaign);
        Log(campaign, "unarmed_shove", summary);
        Log(campaign, "player_roll_resolved", $"{target.Name}'s player-supplied shove save d20 resolved as {save.ChosenRoll} ({save.Total} vs DC {dc}).", dmOnly: true);
        return new ShoveResult(encounter.Id, attackerCombatant.Id, targetCombatant.Id, dc, saveAbility, save, succeeded, effect, summary);
    }

    public PendingRollRequest RequestEscapeGrappleRoll(
        CampaignState campaign,
        string encounterId,
        string targetCombatantId,
        string grapplerCombatantId,
        string skill)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, targetCombatant.Id);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePlayerCharacter(target, "Escape Grapple");
        EnsureActionAvailable(targetCombatant, target, "escape grapple");
        var grapple = encounter.Grapples.FirstOrDefault(g => g.TargetCombatantId == targetCombatant.Id && g.GrapplerCombatantId == grapplerCombatantId)
            ?? throw new InvalidOperationException($"{target.Name} is not grappled by that combatant.");
        var normalizedSkill = (skill ?? "athletics").Trim().ToLowerInvariant();
        var ability = normalizedSkill switch
        {
            "athletics" => "strength",
            "acrobatics" => "dexterity",
            _ => throw new ArgumentException("Escaping a grapple uses Athletics or Acrobatics.", nameof(skill))
        };
        var helper = FindHelpAbilityCheckHelper(campaign, target.Id, normalizedSkill);
        var requestedMode = helper is null ? D20RollMode.Normal : D20RollMode.Advantage;
        var mode = CombineAdvantage(requestedMode, AbilityCheckModeFromConditions(target));
        var proficient = ContainsIgnoreCase(target.SkillProficiencies, normalizedSkill);
        var modifier = AbilityCheckModifier(target, ability, proficient);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var grappler = RequireCharacter(campaign, RequireCombatant(encounter, grapplerCombatantId).CharacterId);

        var pending = new PendingRollRequest
        {
            ActorCharacterId = target.Id,
            EncounterId = encounter.Id,
            CombatantId = targetCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{target.Name} attempts to escape {grappler.Name}'s grapple using {normalizedSkill}. Roll {ability} ({normalizedSkill}){modeText} against DC {grapple.EscapeDc}.",
            ResolutionKey = "escape_grapple_check",
            Modifier = modifier,
            TargetNumber = grapple.EscapeDc,
            TargetLabel = $"DC {grapple.EscapeDc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["grappler_combatant_id"] = grapplerCombatantId,
                ["skill"] = normalizedSkill,
                ["ability"] = ability,
                ["proficient"] = proficient ? "true" : "false",
                ["helper_combatant_id"] = helper?.Id ?? ""
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public EscapeGrappleResult ResolvePendingEscapeGrappleRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "escape_grapple_check");
        var encounter = RequirePendingEncounter(campaign, pending);
        var targetCombatant = RequirePendingCombatant(encounter, pending);
        EnsureCurrentTurn(encounter, targetCombatant.Id);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePendingActor(pending, target);
        EnsurePlayerCharacter(target, "Escape Grapple");
        EnsureActionAvailable(targetCombatant, target, "escape grapple");
        var grapplerCombatantId = RequiredPendingContext(pending, "grappler_combatant_id");
        var grapple = encounter.Grapples.FirstOrDefault(g => g.TargetCombatantId == targetCombatant.Id && g.GrapplerCombatantId == grapplerCombatantId)
            ?? throw new InvalidOperationException($"{target.Name} is no longer grappled by that combatant.");
        var skill = RequiredPendingContext(pending, "skill");
        var ability = RequiredPendingContext(pending, "ability");
        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "escape-grapple check");
        var proficient = PendingContextBool(pending, "proficient");
        var dc = pending.TargetNumber ?? grapple.EscapeDc;
        var check = CharacterMechanics.ResolveD20Test(target, ability, dc, rollOne, rollTwo, mode, proficient, 0);

        ConsumeAction(targetCombatant, target, "escape grapple");
        ConsumePendingCombatSkillHelp(campaign, pending);
        var escaped = check.Success;
        if (escaped)
        {
            encounter.Grapples.Remove(grapple);
            RefreshGrappledCondition(campaign, encounter, targetCombatant.Id);
        }
        campaign.PendingPlayerRoll = null;
        var grappler = RequireCharacter(campaign, RequireCombatant(encounter, grapplerCombatantId).CharacterId);
        var summary = escaped
            ? $"{target.Name} escaped {grappler.Name}'s grapple with {skill}."
            : $"{target.Name} failed to escape {grappler.Name}'s grapple with {skill}.";
        Touch(campaign);
        Log(campaign, "ability_check", $"{target.Name}: {check.Summary}");
        Log(campaign, "grapple_escape", summary);
        Log(campaign, "player_roll_resolved", $"{target.Name}'s player-supplied escape d20 resolved as {check.ChosenRoll} ({check.Total} vs DC {dc}).", dmOnly: true);
        return new EscapeGrappleResult(encounter.Id, grapplerCombatantId, targetCombatant.Id, dc, skill, check, escaped, summary);
    }

    private static int UnarmedSaveDc(CharacterSheet attacker)
        => 8 + CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(attacker, "strength")) + Math.Max(0, attacker.ProficiencyBonus);

    private static bool IsSavingThrowProficient(CharacterSheet character, string ability)
        => character.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase)
            || (ability.Length >= 3 && x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase)));

    private static int SavingThrowBaseModifier(CharacterSheet character, string ability, bool proficient)
    {
        var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, ability));
        var proficiencyModifier = proficient ? Math.Max(0, character.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
        return abilityModifier + proficiencyModifier - exhaustionPenalty;
    }

    private static CombatantState RequireContextCombatant(
        EncounterState encounter,
        PendingRollRequest pending,
        string key,
        string label)
    {
        if (!pending.Context.TryGetValue(key, out var combatantId) || string.IsNullOrWhiteSpace(combatantId))
            throw new InvalidOperationException($"The pending player roll is missing its {label} context.");
        return RequireCombatant(encounter, combatantId);
    }

    private static string RequiredPendingContext(PendingRollRequest pending, string key)
    {
        if (!pending.Context.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"The pending player roll is missing '{key}'.");
        return value;
    }
}
