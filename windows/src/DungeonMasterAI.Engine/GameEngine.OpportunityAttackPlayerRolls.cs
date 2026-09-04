using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingRollRequest RequestOpportunityAttackRoll(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        string? attackName = null)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);

        var encounter = RequireEncounter(campaign, encounterId);
        var pendingMove = encounter.PendingMove
            ?? throw new InvalidOperationException("There is no pending movement that can provoke an Opportunity Attack.");
        var window = pendingMove.OpportunityAttacks.FirstOrDefault(x =>
            x.ReactorCombatantId.Equals(reactorCombatantId, StringComparison.OrdinalIgnoreCase) && !x.Resolved)
            ?? throw new InvalidOperationException("That combatant has no unresolved Opportunity Attack for the pending move.");
        var reactorCombatant = RequireCombatant(encounter, reactorCombatantId);
        var moverCombatant = RequireCombatant(encounter, pendingMove.CombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        var mover = RequireCharacter(campaign, moverCombatant.CharacterId);

        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can create a player-controlled Opportunity Attack roll request.");
        if (!reactorCombatant.ReactionAvailable)
            throw new InvalidOperationException($"{reactor.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(reactor))
            throw new InvalidOperationException($"{reactor.Name} cannot take a Reaction right now.");
        if (mover.Dead)
            throw new InvalidOperationException($"{mover.Name} is already dead.");

        var profile = SelectMeleeAttack(reactor, attackName);
        var currentDistance = GridDistanceFeet(reactorCombatant.GridX, reactorCombatant.GridY, window.TriggerX, window.TriggerY);
        var reach = Math.Max(5, profile.ReachFeet);
        if (currentDistance > reach)
            throw new InvalidOperationException($"{mover.Name} is not within {reactor.Name}'s {profile.Name} reach at the Opportunity Attack trigger point.");

        var armorClass = EffectiveArmorClass(campaign, mover);
        var mode = AttackRollMode(campaign, encounter, reactorCombatant, moverCombatant, reactor, mover);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var automaticCritical = IsAutomaticCriticalHitTarget(reactorCombatant, moverCombatant, mover);

        // Choosing to make the Opportunity Attack commits the Reaction before the attack roll.
        // The pending d20 represents the player's die, not a reversible reaction decision.
        reactorCombatant.ReactionAvailable = false;
        var pending = new PendingRollRequest
        {
            ActorCharacterId = reactor.Id,
            EncounterId = encounter.Id,
            CombatantId = reactorCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{reactor.Name} uses a Reaction for an Opportunity Attack against {mover.Name} with {profile.Name}. Roll the attack d20{modeText} against AC {armorClass}.",
            ResolutionKey = "opportunity_attack",
            Modifier = profile.AttackBonus,
            TargetNumber = armorClass,
            TargetLabel = $"AC {armorClass}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mover_combatant_id"] = moverCombatant.Id,
                ["attack_name"] = profile.Name,
                ["automatic_critical"] = automaticCritical ? "true" : "false",
                ["trigger_x"] = window.TriggerX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trigger_y"] = window.TriggerY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reaction_committed"] = "true"
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public EncounterAttackResult ResolvePendingOpportunityAttackRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        ValidateD20Inputs(rollOne, rollTwo);
        var pending = RequirePendingRoll(campaign, pendingRollId, "opportunity_attack");
        var encounter = RequireOpportunityEncounter(campaign, pending);
        var reactorCombatant = RequirePendingCombatant(encounter, pending);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        EnsurePendingActor(pending, reactor);
        EnsurePlayerCharacter(reactor, "Opportunity Attack");
        if (!CanTakeReaction(reactor))
            throw new InvalidOperationException($"{reactor.Name} can no longer complete the Opportunity Attack.");

        var moverCombatantId = RequiredPendingContext(pending, "mover_combatant_id");
        var pendingMove = encounter.PendingMove
            ?? throw new InvalidOperationException("The movement that provoked this Opportunity Attack no longer exists.");
        if (!pendingMove.CombatantId.Equals(moverCombatantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending Opportunity Attack no longer matches the provoking movement.");
        var window = RequireOpportunityWindow(pendingMove, reactorCombatant.Id);
        var moverCombatant = RequireCombatant(encounter, moverCombatantId);
        var mover = RequireCharacter(campaign, moverCombatant.CharacterId);
        if (mover.Dead) throw new InvalidOperationException($"{mover.Name} is already dead.");

        pending.Context.TryGetValue("attack_name", out var attackName);
        var profile = SelectMeleeAttack(reactor, attackName);
        ValidateOpportunityReach(reactorCombatant, mover, window, profile, reactor);
        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "Opportunity Attack");
        var chosen = mode switch
        {
            D20RollMode.Advantage => Math.Max(rollOne, rollTwo!.Value),
            D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo!.Value),
            _ => rollOne
        };
        var armorClass = pending.TargetNumber ?? EffectiveArmorClass(campaign, mover);
        var effectAttackBonus = RollActiveAttackBonus(campaign, reactor.Id, dice);
        var totalModifier = profile.AttackBonus + effectAttackBonus;
        var total = chosen + totalModifier;
        var naturalCritical = chosen == 20;
        var automaticCritical = pending.Context.TryGetValue("automatic_critical", out var criticalText)
            && bool.TryParse(criticalText, out var parsedAutomaticCritical)
            && parsedAutomaticCritical;
        var hit = naturalCritical || (chosen != 1 && total >= armorClass);
        var critical = hit && (naturalCritical || automaticCritical);

        var helpUsed = ConsumeHelpAttackAdvantage(encounter, reactorCombatant, moverCombatant);
        ConsumeNextAttackAdvantageEffect(campaign, mover.Id);
        BreakHidden(campaign, encounter, reactorCombatant, "making an Opportunity Attack");
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = hit
            ? $"Attack{modeText} {total} vs AC {armorClass}: hit{(critical ? " (critical)" : "")}{effectText}; damage roll required."
            : $"Attack{modeText} {total} vs AC {armorClass}: miss{effectText}.";
        var attack = new AttackResult(chosen, totalModifier, total, hit, critical, 0, attackSummary);
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";

        if (!hit)
        {
            var summary = $"{reactor.Name} used a Reaction for an Opportunity Attack with {profile.Name} against {mover.Name}: miss ({total} vs AC {armorClass}).{helpText}";
            campaign.PendingPlayerRoll = null;
            CompleteOpportunityWindow(campaign, encounter, window, summary);
            Touch(campaign);
            Log(campaign, "opportunity_attack", summary);
            Log(campaign, "player_roll_resolved", $"{reactor.Name}'s player-supplied Opportunity Attack d20 resolved as {chosen} ({total} vs AC {armorClass}).", dmOnly: true);
            return new EncounterAttackResult(encounter.Id, reactor.Name, mover.Name, profile.Name, attack, null, summary, null, true, 0);
        }

        var damageFormula = BuildPendingDamageFormula(profile.DamageExpression, critical);
        var damagePending = new PendingRollRequest
        {
            ActorCharacterId = reactor.Id,
            EncounterId = encounter.Id,
            CombatantId = reactorCombatant.Id,
            Formula = damageFormula,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"{reactor.Name}'s Opportunity Attack hit {mover.Name} with {profile.Name}{(critical ? " critically" : "")}. Roll {damageFormula} damage.",
            ResolutionKey = "opportunity_attack_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mover_combatant_id"] = moverCombatant.Id,
                ["attack_name"] = profile.Name,
                ["damage_type"] = profile.DamageType,
                ["base_damage_expression"] = profile.DamageExpression,
                ["critical"] = critical ? "true" : "false",
                ["attack_d20"] = chosen.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_modifier"] = totalModifier.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["armor_class"] = armorClass.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_mode"] = mode.ToString().ToLowerInvariant(),
                ["effect_attack_bonus"] = effectAttackBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["help_used"] = helpUsed ? "true" : "false"
            }
        };

        campaign.PendingPlayerRoll = damagePending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", damagePending.Purpose, dmOnly: true);
        Log(campaign, "player_roll_resolved", $"{reactor.Name}'s player-supplied Opportunity Attack d20 resolved as {chosen} ({total} vs AC {armorClass}).", dmOnly: true);
        var hitSummary = $"{reactor.Name} used a Reaction for an Opportunity Attack with {profile.Name} against {mover.Name}: hit ({total} vs AC {armorClass}){(critical ? " critical" : "")}.{helpText} Roll {damageFormula} damage.";
        return new EncounterAttackResult(encounter.Id, reactor.Name, mover.Name, profile.Name, attack, null, hitSummary, null, true, 0);
    }

    public EncounterAttackResult ResolvePendingOpportunityAttackDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int damageAmount,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        if (damageAmount is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(damageAmount));
        var pending = RequirePendingRoll(campaign, pendingRollId, "opportunity_attack_damage");
        var encounter = RequireOpportunityEncounter(campaign, pending);
        var reactorCombatant = RequirePendingCombatant(encounter, pending);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        EnsurePendingActor(pending, reactor);
        EnsurePlayerCharacter(reactor, "Opportunity Attack damage");

        var moverCombatantId = RequiredPendingContext(pending, "mover_combatant_id");
        var pendingMove = encounter.PendingMove
            ?? throw new InvalidOperationException("The movement that provoked this Opportunity Attack no longer exists.");
        if (!pendingMove.CombatantId.Equals(moverCombatantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending Opportunity Attack damage no longer matches the provoking movement.");
        var window = RequireOpportunityWindow(pendingMove, reactorCombatant.Id);
        var moverCombatant = RequireCombatant(encounter, moverCombatantId);
        var mover = RequireCharacter(campaign, moverCombatant.CharacterId);
        if (mover.Dead) throw new InvalidOperationException($"{mover.Name} is already dead.");

        pending.Context.TryGetValue("attack_name", out var attackName);
        var profile = SelectMeleeAttack(reactor, attackName);
        var damageType = pending.Context.TryGetValue("damage_type", out var storedDamageType) && !string.IsNullOrWhiteSpace(storedDamageType)
            ? storedDamageType
            : profile.DamageType;
        var critical = ContextBool(pending, "critical");
        var attackD20 = ContextInt(pending, "attack_d20");
        var attackModifier = ContextInt(pending, "attack_modifier");
        var attackTotal = ContextInt(pending, "attack_total");
        var armorClass = ContextInt(pending, "armor_class");
        var mode = ParsePendingRollMode(pending.Context.TryGetValue("attack_mode", out var attackMode) ? attackMode : null);
        var effectAttackBonus = ContextInt(pending, "effect_attack_bonus", 0);
        var helpUsed = ContextBool(pending, "help_used");
        var concentrationBefore = mover.ConcentrationEffect;

        // The player's damage roll is satisfied before damage is applied so a required
        // Concentration save can replace it without allowing the movement to resume early.
        campaign.PendingPlayerRoll = null;
        var resolution = ApplyDamageWithConcentration(campaign, mover.Id, damageAmount, dice, damageType, critical);
        var damage = resolution.Damage;
        var concentration = resolution.Concentration;
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = $"Attack{modeText} {attackTotal} vs AC {armorClass}: hit for {damageAmount} damage{(critical ? " (critical)" : "")}{effectText}.";
        var attack = new AttackResult(attackD20, attackModifier, attackTotal, true, critical, damageAmount, attackSummary);
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var summary = $"{reactor.Name} used a Reaction for an Opportunity Attack with {profile.Name} against {mover.Name}: {attack.Summary}{helpText}";
        if (concentration is not null) summary += $" {concentration.Summary}";
        else if (!string.IsNullOrWhiteSpace(concentrationBefore) && string.IsNullOrWhiteSpace(mover.ConcentrationEffect) && damage.EffectiveDamage > 0)
            summary += $" {mover.Name} lost Concentration on {concentrationBefore}.";

        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
        {
            campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "opportunity_attack_move";
            campaign.PendingPlayerRoll.Context["opportunity_attack_encounter_id"] = encounter.Id;
            campaign.PendingPlayerRoll.Context["opportunity_attack_reactor_combatant_id"] = reactorCombatant.Id;
            campaign.PendingPlayerRoll.Context["opportunity_attack_summary"] = summary;
        }
        else
        {
            CompleteOpportunityWindow(campaign, encounter, window, summary);
        }

        Touch(campaign);
        Log(campaign, "opportunity_attack", summary);
        Log(campaign, "player_roll_resolved", $"{reactor.Name} supplied {damageAmount} Opportunity Attack damage for {profile.Name}.", dmOnly: true);
        return new EncounterAttackResult(encounter.Id, reactor.Name, mover.Name, profile.Name, attack, damage, summary, concentration, true, 0);
    }

    private string? ResumeOpportunityAttackAfterConcentration(
        CampaignState campaign,
        IReadOnlyDictionary<string, string> continuationContext,
        DiceService dice)
    {
        if (!continuationContext.TryGetValue("opportunity_attack_encounter_id", out var encounterId)
            || string.IsNullOrWhiteSpace(encounterId))
            throw new InvalidOperationException("The Opportunity Attack continuation is missing its encounter.");
        if (!continuationContext.TryGetValue("opportunity_attack_reactor_combatant_id", out var reactorCombatantId)
            || string.IsNullOrWhiteSpace(reactorCombatantId))
            throw new InvalidOperationException("The Opportunity Attack continuation is missing its reactor.");
        continuationContext.TryGetValue("opportunity_attack_summary", out var summary);

        var encounter = RequireEncounter(campaign, encounterId);
        var pendingMove = encounter.PendingMove
            ?? throw new InvalidOperationException("The movement for the Opportunity Attack continuation no longer exists.");
        var window = RequireOpportunityWindow(pendingMove, reactorCombatantId);
        CompleteOpportunityWindow(campaign, encounter, window, summary ?? $"{window.ReactorName}'s Opportunity Attack resolved.");
        Touch(campaign);
        return encounter.PendingMove is null
            ? "The Opportunity Attack reaction window is complete and the pending movement resumed."
            : "The Opportunity Attack is complete; another reaction to the pending movement still awaits resolution.";
    }

    private static EncounterState RequireOpportunityEncounter(CampaignState campaign, PendingRollRequest pending)
    {
        if (string.IsNullOrWhiteSpace(pending.EncounterId))
            throw new InvalidOperationException("The pending Opportunity Attack is missing encounter context.");
        var encounter = RequireEncounter(campaign, pending.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is no longer active.");
        return encounter;
    }

    private static OpportunityAttackWindow RequireOpportunityWindow(PendingCombatMove pendingMove, string reactorCombatantId)
        => pendingMove.OpportunityAttacks.FirstOrDefault(x =>
            x.ReactorCombatantId.Equals(reactorCombatantId, StringComparison.OrdinalIgnoreCase) && !x.Resolved)
            ?? throw new InvalidOperationException("That Opportunity Attack reaction window is no longer unresolved.");

    private static void ValidateOpportunityReach(
        CombatantState reactorCombatant,
        CharacterSheet mover,
        OpportunityAttackWindow window,
        AttackProfile profile,
        CharacterSheet reactor)
    {
        var currentDistance = GridDistanceFeet(reactorCombatant.GridX, reactorCombatant.GridY, window.TriggerX, window.TriggerY);
        var reach = Math.Max(5, profile.ReachFeet);
        if (currentDistance > reach)
            throw new InvalidOperationException($"{mover.Name} is not within {reactor.Name}'s {profile.Name} reach at the Opportunity Attack trigger point.");
    }

    private void CompleteOpportunityWindow(
        CampaignState campaign,
        EncounterState encounter,
        OpportunityAttackWindow window,
        string summary)
    {
        window.Resolved = true;
        window.Declined = false;
        window.ResolutionSummary = summary;
        FinalizePendingMoveIfReady(campaign, encounter);
    }
}
