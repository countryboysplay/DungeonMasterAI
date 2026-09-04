using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingRollRequest RequestSearchActionRoll(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string skill,
        int dc) =>
        RequestCombatSkillActionRoll(
            campaign,
            encounterId,
            combatantId,
            "Search",
            "wisdom",
            skill,
            dc,
            ["insight", "medicine", "perception", "survival"]);

    public PendingRollRequest RequestStudyActionRoll(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string skill,
        int dc) =>
        RequestCombatSkillActionRoll(
            campaign,
            encounterId,
            combatantId,
            "Study",
            "intelligence",
            skill,
            dc,
            ["arcana", "history", "investigation", "nature", "religion"]);

    public PendingRollRequest RequestInfluenceActionRoll(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string skill,
        int dc)
    {
        var normalized = (skill ?? "").Trim().ToLowerInvariant();
        var ability = normalized == "animal handling" ? "wisdom" : "charisma";
        return RequestCombatSkillActionRoll(
            campaign,
            encounterId,
            combatantId,
            "Influence",
            ability,
            normalized,
            dc,
            ["animal handling", "deception", "intimidation", "performance", "persuasion"]);
    }

    public CombatSkillActionResult ResolvePendingCombatSkillActionRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && rollTwo.Value is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollTwo));

        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals("combat_skill_action", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not a combat skill action.");
        if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending combat skill action is missing encounter context.");

        var encounter = RequireEncounter(campaign, pending.EncounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, pending.CombatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!character.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending combat skill actor no longer matches the active combatant.");
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending combat skill action no longer belongs to a player character.");

        if (!pending.Context.TryGetValue("action_name", out var actionName) || string.IsNullOrWhiteSpace(actionName))
            throw new InvalidOperationException("The pending combat skill action is missing its action name.");
        if (!pending.Context.TryGetValue("ability", out var ability) || string.IsNullOrWhiteSpace(ability))
            throw new InvalidOperationException("The pending combat skill action is missing its ability.");
        if (!pending.Context.TryGetValue("skill", out var skill) || string.IsNullOrWhiteSpace(skill))
            throw new InvalidOperationException("The pending combat skill action is missing its skill.");
        var proficient = ContextBool(pending, "proficient");
        var circumstanceModifier = ContextInt(pending, "circumstance_modifier", 0);
        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This {actionName} check requires two d20 results because it has {mode}.");
        var difficultyClass = pending.TargetNumber
            ?? throw new InvalidOperationException("The pending combat skill action is missing its DC.");

        // The action is committed only after the supplied dice have passed validation.
        var check = CharacterMechanics.ResolveD20Test(
            character,
            ability,
            difficultyClass,
            rollOne,
            rollTwo,
            mode,
            proficient,
            circumstanceModifier);
        ConsumeAction(combatant, character, actionName);
        ConsumePendingCombatSkillHelp(campaign, pending);

        campaign.PendingPlayerRoll = null;
        var summary = $"{character.Name} took the {actionName} action using {ability} ({skill}): {check.Summary}";
        Touch(campaign);
        Log(campaign, $"{actionName.ToLowerInvariant()}_action", summary);
        Log(campaign, "player_roll_resolved", $"{character.Name}'s player-supplied {actionName} d20 resolved as {check.ChosenRoll} ({check.Total} vs DC {difficultyClass}).", dmOnly: true);
        return new CombatSkillActionResult(
            encounter.Id,
            combatant.Id,
            character.Id,
            actionName,
            ability,
            skill,
            check,
            summary);
    }

    private PendingRollRequest RequestCombatSkillActionRoll(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string actionName,
        string ability,
        string skill,
        int dc,
        IReadOnlyCollection<string> allowedSkills)
    {
        Guard.NotNull(campaign, nameof(campaign));
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{actionName} player-roll requests are only created for player characters.");
        if (CharacterMechanics.IsIncapacitated(character))
            throw new InvalidOperationException($"{character.Name} is Incapacitated and cannot take the {actionName} action.");
        if (!combatant.ActionAvailable)
            throw new InvalidOperationException($"{character.Name} has already spent the action for this turn.");

        var normalizedAbility = CharacterMechanics.NormalizeAbility(ability);
        var normalizedSkill = (skill ?? "").Trim().ToLowerInvariant();
        if (!allowedSkills.Contains(normalizedSkill, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{skill} is not a valid skill for the {actionName} action.", nameof(skill));
        if (dc < 1) throw new ArgumentOutOfRangeException(nameof(dc), "DC must be at least 1.");

        var helper = FindHelpAbilityCheckHelper(campaign, character.Id, normalizedSkill);
        var requestedMode = helper is null ? D20RollMode.Normal : D20RollMode.Advantage;
        var mode = CombineAdvantage(requestedMode, AbilityCheckModeFromConditions(character));
        var proficient = ContainsIgnoreCase(character.SkillProficiencies, normalizedSkill);
        var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, normalizedAbility));
        var proficiencyModifier = proficient ? Math.Max(0, character.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
        var modifier = abilityModifier + proficiencyModifier - exhaustionPenalty;
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var skillLabel = normalizedSkill.Length == 0 ? normalizedAbility : $"{normalizedAbility} ({normalizedSkill})";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = character.Id,
            EncounterId = encounter.Id,
            CombatantId = combatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{character.Name} takes the {actionName} action using {skillLabel}. Roll the d20{modeText} against DC {dc}.",
            ResolutionKey = "combat_skill_action",
            Modifier = modifier,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action_name"] = actionName,
                ["ability"] = normalizedAbility,
                ["skill"] = normalizedSkill,
                ["circumstance_modifier"] = "0",
                ["proficient"] = proficient ? "true" : "false",
                ["helper_combatant_id"] = helper?.Id ?? ""
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    private void ConsumePendingCombatSkillHelp(CampaignState campaign, PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("helper_combatant_id", out var helperCombatantId)
            || string.IsNullOrWhiteSpace(helperCombatantId))
            return;

        foreach (var encounter in campaign.Encounters.Where(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)))
        {
            var helper = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(helperCombatantId, StringComparison.OrdinalIgnoreCase));
            if (helper is null) continue;
            var helperCharacter = RequireCharacter(campaign, helper.CharacterId);
            helper.HelpAbilityTargetCharacterId = null;
            helper.HelpAbilityProficiency = null;
            Log(campaign, "help_ability_consumed", $"{helperCharacter.Name}'s Help supplied Advantage to the combat skill action.");
            return;
        }
    }
}
