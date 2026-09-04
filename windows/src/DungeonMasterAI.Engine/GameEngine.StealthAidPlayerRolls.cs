using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingRollRequest RequestHideRoll(
        CampaignState campaign,
        string encounterId,
        string combatantId)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        EnsurePlayerCharacter(character, "Hide");
        ValidateHideAttempt(campaign, encounter, combatant, character);
        EnsureActionAvailable(combatant, character, "Hide");

        var mode = AbilityCheckModeFromConditions(character);
        var proficient = ContainsIgnoreCase(character.SkillProficiencies, "stealth");
        var modifier = AbilityCheckModifier(character, "dexterity", proficient);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var pending = new PendingRollRequest
        {
            ActorCharacterId = character.Id,
            EncounterId = encounter.Id,
            CombatantId = combatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{character.Name} is attempting to Hide. Roll Dexterity (Stealth){modeText} against DC 15.",
            ResolutionKey = "hide_check",
            Modifier = modifier,
            TargetNumber = 15,
            TargetLabel = "DC 15",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["proficient"] = proficient ? "true" : "false"
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public HideResult ResolvePendingHideRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "hide_check");
        var encounter = RequirePendingEncounter(campaign, pending);
        var combatant = RequirePendingCombatant(encounter, pending);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        EnsurePendingActor(pending, character);
        EnsurePlayerCharacter(character, "Hide");
        ValidateHideAttempt(campaign, encounter, combatant, character);
        EnsureActionAvailable(combatant, character, "Hide");

        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "Hide check");
        var proficient = PendingContextBool(pending, "proficient");
        var dc = pending.TargetNumber ?? 15;
        var check = CharacterMechanics.ResolveD20Test(
            character,
            "dexterity",
            dc,
            rollOne,
            rollTwo,
            mode,
            proficient,
            0);

        ConsumeAction(combatant, character, "Hide");
        combatant.IsHidden = check.Success;
        combatant.HideCheckTotal = check.Success ? check.Total : 0;
        campaign.PendingPlayerRoll = null;
        Touch(campaign);

        var summary = check.Success
            ? $"{character.Name} succeeded on the Hide action with Stealth {check.Total}. While hidden, the creature is treated as Invisible; Wisdom (Perception) DC {check.Total} finds it."
            : $"{character.Name} failed the DC 15 Hide check and is not hidden.";
        Log(campaign, "ability_check", $"{character.Name}: {check.Summary}");
        Log(campaign, "hide", summary);
        Log(campaign, "player_roll_resolved", $"{character.Name}'s player-supplied Hide d20 resolved as {check.ChosenRoll} ({check.Total} vs DC {dc}).", dmOnly: true);
        return new HideResult(encounter.Id, combatant.Id, character.Id, check, combatant.IsHidden, combatant.HideCheckTotal, summary);
    }

    public PendingRollRequest RequestHiddenSearchRoll(
        CampaignState campaign,
        string encounterId,
        string searcherCombatantId,
        string targetCombatantId)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var searcherCombatant = RequireCombatant(encounter, searcherCombatantId);
        EnsureCurrentTurn(encounter, searcherCombatant.Id);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        ValidateHiddenSearchTarget(searcherCombatant, targetCombatant);
        var searcher = RequireCharacter(campaign, searcherCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePlayerCharacter(searcher, "Search");
        EnsureActionAvailable(searcherCombatant, searcher, "Search");

        var helper = FindHelpAbilityCheckHelper(campaign, searcher.Id, "perception");
        var requestedMode = helper is null ? D20RollMode.Normal : D20RollMode.Advantage;
        var mode = CombineAdvantage(requestedMode, AbilityCheckModeFromConditions(searcher));
        var proficient = ContainsIgnoreCase(searcher.SkillProficiencies, "perception");
        var modifier = AbilityCheckModifier(searcher, "wisdom", proficient);
        var dc = Math.Max(1, targetCombatant.HideCheckTotal);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var pending = new PendingRollRequest
        {
            ActorCharacterId = searcher.Id,
            EncounterId = encounter.Id,
            CombatantId = searcherCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{searcher.Name} is searching for hidden {target.Name}. Roll Wisdom (Perception){modeText} against DC {dc}.",
            ResolutionKey = "search_hidden_check",
            Modifier = modifier,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target_combatant_id"] = targetCombatant.Id,
                ["proficient"] = proficient ? "true" : "false",
                ["helper_combatant_id"] = helper?.Id ?? ""
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public HiddenSearchResult ResolvePendingHiddenSearchRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "search_hidden_check");
        var encounter = RequirePendingEncounter(campaign, pending);
        var searcherCombatant = RequirePendingCombatant(encounter, pending);
        EnsureCurrentTurn(encounter, searcherCombatant.Id);
        var searcher = RequireCharacter(campaign, searcherCombatant.CharacterId);
        EnsurePendingActor(pending, searcher);
        EnsurePlayerCharacter(searcher, "Search");
        EnsureActionAvailable(searcherCombatant, searcher, "Search");

        if (!pending.Context.TryGetValue("target_combatant_id", out var targetCombatantId) || string.IsNullOrWhiteSpace(targetCombatantId))
            throw new InvalidOperationException("The pending hidden Search is missing its target.");
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        ValidateHiddenSearchTarget(searcherCombatant, targetCombatant);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        var dc = pending.TargetNumber ?? Math.Max(1, targetCombatant.HideCheckTotal);
        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "hidden Search check");
        var proficient = PendingContextBool(pending, "proficient");
        var check = CharacterMechanics.ResolveD20Test(
            searcher,
            "wisdom",
            dc,
            rollOne,
            rollTwo,
            mode,
            proficient,
            0);

        ConsumeAction(searcherCombatant, searcher, "Search");
        ConsumePendingCombatSkillHelp(campaign, pending);
        if (check.Success)
            BreakHidden(campaign, encounter, targetCombatant, $"being found by {searcher.Name}'s Wisdom (Perception) check");
        campaign.PendingPlayerRoll = null;
        Touch(campaign);

        var summary = check.Success
            ? $"{searcher.Name} found hidden {target.Name} with Perception {check.Total} vs DC {dc}."
            : $"{searcher.Name} failed to find hidden {target.Name}: Perception {check.Total} vs DC {dc}.";
        Log(campaign, "ability_check", $"{searcher.Name}: {check.Summary}");
        Log(campaign, "search_hidden", summary);
        Log(campaign, "player_roll_resolved", $"{searcher.Name}'s player-supplied hidden Search d20 resolved as {check.ChosenRoll} ({check.Total} vs DC {dc}).", dmOnly: true);
        return new HiddenSearchResult(encounter.Id, searcherCombatant.Id, targetCombatant.Id, check, check.Success, summary);
    }

    public PendingRollRequest RequestFirstAidRoll(
        CampaignState campaign,
        string encounterId,
        string helperCombatantId,
        string targetCombatantId)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var helperCombatant = RequireCombatant(encounter, helperCombatantId);
        EnsureCurrentTurn(encounter, helperCombatant.Id);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        var helper = RequireCharacter(campaign, helperCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsurePlayerCharacter(helper, "First Aid");
        ValidateFirstAidAttempt(helperCombatant, targetCombatant, helper, target);
        EnsureActionAvailable(helperCombatant, helper, "Help (First Aid)");

        var skill = "medicine";
        var helperForCheck = FindHelpAbilityCheckHelper(campaign, helper.Id, skill);
        var requestedMode = helperForCheck is null ? D20RollMode.Normal : D20RollMode.Advantage;
        var mode = CombineAdvantage(requestedMode, AbilityCheckModeFromConditions(helper));
        var proficient = ContainsIgnoreCase(helper.SkillProficiencies, skill);
        var modifier = AbilityCheckModifier(helper, "wisdom", proficient);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var pending = new PendingRollRequest
        {
            ActorCharacterId = helper.Id,
            EncounterId = encounter.Id,
            CombatantId = helperCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{helper.Name} is administering first aid to {target.Name}. Roll Wisdom (Medicine){modeText} against DC 10.",
            ResolutionKey = "first_aid_check",
            Modifier = modifier,
            TargetNumber = 10,
            TargetLabel = "DC 10",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target_combatant_id"] = targetCombatant.Id,
                ["proficient"] = proficient ? "true" : "false",
                ["helper_combatant_id"] = helperForCheck?.Id ?? ""
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public FirstAidResult ResolvePendingFirstAidRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "first_aid_check");
        var encounter = RequirePendingEncounter(campaign, pending);
        var helperCombatant = RequirePendingCombatant(encounter, pending);
        EnsureCurrentTurn(encounter, helperCombatant.Id);
        var helper = RequireCharacter(campaign, helperCombatant.CharacterId);
        EnsurePendingActor(pending, helper);
        EnsurePlayerCharacter(helper, "First Aid");
        EnsureActionAvailable(helperCombatant, helper, "Help (First Aid)");

        if (!pending.Context.TryGetValue("target_combatant_id", out var targetCombatantId) || string.IsNullOrWhiteSpace(targetCombatantId))
            throw new InvalidOperationException("The pending first-aid check is missing its target.");
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        ValidateFirstAidAttempt(helperCombatant, targetCombatant, helper, target);
        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "first-aid check");
        var proficient = PendingContextBool(pending, "proficient");
        var dc = pending.TargetNumber ?? 10;
        var check = CharacterMechanics.ResolveD20Test(
            helper,
            "wisdom",
            dc,
            rollOne,
            rollTwo,
            mode,
            proficient,
            0);

        ConsumeAction(helperCombatant, helper, "Help (First Aid)");
        ConsumePendingCombatSkillHelp(campaign, pending);
        var stabilized = false;
        var awakened = false;
        var unconscious = target.Conditions.Any(c => c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase));
        if (check.Success)
        {
            if (target.CurrentHp == 0)
            {
                target.Stable = true;
                target.DeathSaveSuccesses = 0;
                target.DeathSaveFailures = 0;
                stabilized = true;
            }
            else if (unconscious)
            {
                RemoveConditionInternal(target, "Unconscious");
                awakened = true;
            }
        }

        campaign.PendingPlayerRoll = null;
        var summary = check.Success
            ? stabilized
                ? $"{helper.Name} administered first aid to {target.Name}; the DC 10 Wisdom (Medicine) check succeeded and {target.Name} is Stable."
                : $"{helper.Name} administered first aid to {target.Name}; the DC 10 Wisdom (Medicine) check succeeded and the Unconscious condition ended."
            : $"{helper.Name} administered first aid to {target.Name}, but the DC 10 Wisdom (Medicine) check failed.";
        Touch(campaign);
        Log(campaign, "ability_check", $"{helper.Name}: {check.Summary}");
        Log(campaign, "first_aid", summary);
        Log(campaign, "player_roll_resolved", $"{helper.Name}'s player-supplied first-aid d20 resolved as {check.ChosenRoll} ({check.Total} vs DC {dc}).", dmOnly: true);
        return new FirstAidResult(encounter.Id, helperCombatant.Id, target.Id, check, stabilized, awakened, summary);
    }

    private static void EnsureNoRequiredPlayerRoll(CampaignState campaign)
    {
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");
    }

    private static PendingRollRequest RequirePendingRoll(CampaignState campaign, string pendingRollId, string expectedResolutionKey)
    {
        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals(expectedResolutionKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not '{expectedResolutionKey}'.");
        return pending;
    }

    private static EncounterState RequirePendingEncounter(CampaignState campaign, PendingRollRequest pending)
    {
        if (string.IsNullOrWhiteSpace(pending.EncounterId))
            throw new InvalidOperationException("The pending player roll is missing encounter context.");
        var encounter = RequireEncounter(campaign, pending.EncounterId);
        EnsureEncounterActionReady(encounter);
        return encounter;
    }

    private static CombatantState RequirePendingCombatant(EncounterState encounter, PendingRollRequest pending)
    {
        if (string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending player roll is missing combatant context.");
        return RequireCombatant(encounter, pending.CombatantId);
    }

    private static void EnsurePendingActor(PendingRollRequest pending, CharacterSheet character)
    {
        if (!character.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending player-roll actor no longer matches the authoritative combatant.");
    }

    private static void EnsurePlayerCharacter(CharacterSheet character, string actionName)
    {
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{actionName} player-roll requests are only created for player characters.");
    }

    private static void EnsureActionAvailable(CombatantState combatant, CharacterSheet character, string actionName)
    {
        if (CharacterMechanics.IsIncapacitated(character))
            throw new InvalidOperationException($"{character.Name} is Incapacitated and cannot take the {actionName} action.");
        if (!combatant.ActionAvailable)
            throw new InvalidOperationException($"{character.Name} has already used their action this turn and cannot take the {actionName} action.");
    }

    private static void ValidateD20Inputs(int rollOne, int? rollTwo)
    {
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && rollTwo.Value is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollTwo));
    }

    private static void RequireSecondD20WhenNeeded(D20RollMode mode, int? rollTwo, string label)
    {
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This {label} requires two d20 results because it has {mode}.");
    }

    private static bool PendingContextBool(PendingRollRequest pending, string key)
        => pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static int AbilityCheckModifier(CharacterSheet character, string ability, bool proficient)
    {
        var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, ability));
        var proficiencyModifier = proficient ? Math.Max(0, character.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
        return abilityModifier + proficiencyModifier - exhaustionPenalty;
    }

    private static void ValidateHideAttempt(
        CampaignState campaign,
        EncounterState encounter,
        CombatantState combatant,
        CharacterSheet character)
    {
        if (!combatant.Positioned)
            throw new InvalidOperationException($"{character.Name} must be positioned on the tactical grid before using Hide.");
        if (character.Dead || CharacterMechanics.IsIncapacitated(character) || character.Conditions.Any(c => c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{character.Name} cannot take the Hide action right now.");
        if (!HasHideCoverOrObscurity(encounter, combatant))
            throw new InvalidOperationException("Hide requires the creature to be Heavily Obscured or behind Three-Quarters or Total Cover.");

        var visibleEnemy = encounter.Combatants.FirstOrDefault(other =>
            IsEnemy(combatant, other)
            && CanSeeCombatant(campaign, encounter, other, combatant));
        if (visibleEnemy is not null)
        {
            var observer = RequireCharacter(campaign, visibleEnemy.CharacterId);
            throw new InvalidOperationException($"{character.Name} cannot Hide while still in {observer.Name}'s line of sight.");
        }
    }

    private static void ValidateHiddenSearchTarget(CombatantState searcherCombatant, CombatantState targetCombatant)
    {
        if (!targetCombatant.IsHidden)
            throw new InvalidOperationException("The selected target is not currently hidden.");
        if (searcherCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot search for itself.");
    }

    private static void ValidateFirstAidAttempt(
        CombatantState helperCombatant,
        CombatantState targetCombatant,
        CharacterSheet helper,
        CharacterSheet target)
    {
        if (helperCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot administer first aid to itself with this action.");
        if (target.Dead)
            throw new InvalidOperationException($"{target.Name} is dead and cannot be stabilized with first aid.");
        var unconscious = target.Conditions.Any(c => c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase));
        if (target.CurrentHp > 0 && !unconscious)
            throw new InvalidOperationException($"{target.Name} does not currently need combat first aid.");
        if (target.CurrentHp == 0 && target.Stable)
            throw new InvalidOperationException($"{target.Name} is already Stable.");
        if (helperCombatant.Positioned && targetCombatant.Positioned
            && GridDistanceFeet(helperCombatant.GridX, helperCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY) > 5)
            throw new InvalidOperationException($"{helper.Name} must be adjacent to {target.Name} to administer first aid on the tactical grid.");
    }
}
