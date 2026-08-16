using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    internal void SyncOpportunityAttackPlayerDecision(CampaignState campaign, EncounterState encounter)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(encounter);

        // A mechanical roll already owned by the player always has priority over a new choice.
        if (campaign.PendingPlayerRoll?.Required == true)
            return;

        var existing = campaign.PendingPlayerDecision;
        if (existing?.Required == true
            && !existing.DecisionType.Equals("opportunity_attack_reaction", StringComparison.OrdinalIgnoreCase))
            return;

        var pendingMove = encounter.PendingMove;
        if (pendingMove is null)
        {
            ClearOpportunityAttackDecision(campaign);
            return;
        }

        if (existing?.Required == true
            && OpportunityAttackDecisionStillValid(campaign, encounter, pendingMove, existing))
            return;

        ClearOpportunityAttackDecision(campaign);


        var window = pendingMove.OpportunityAttacks.FirstOrDefault(x => !x.Resolved);
        if (window is null)
        {
            FinalizePendingMoveIfReady(campaign, encounter);
            return;
        }

        var reactorCombatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(window.ReactorCombatantId, StringComparison.OrdinalIgnoreCase));
        if (reactorCombatant is null)
        {
            window.Resolved = true;
            window.Declined = true;
            window.ResolutionSummary = "Opportunity Attack window was discarded because the reactor no longer exists.";
            Log(campaign, "opportunity_attack_unavailable", window.ResolutionSummary, dmOnly: true);
            SyncOpportunityAttackPlayerDecision(campaign, encounter);
            return;
        }

        var reactor = campaign.Characters.FirstOrDefault(c => c.Id.Equals(reactorCombatant.CharacterId, StringComparison.OrdinalIgnoreCase));
        if (reactor is null)
        {
            window.Resolved = true;
            window.Declined = true;
            window.ResolutionSummary = "Opportunity Attack window was discarded because the reactor character no longer exists.";
            Log(campaign, "opportunity_attack_unavailable", window.ResolutionSummary, dmOnly: true);
            SyncOpportunityAttackPlayerDecision(campaign, encounter);
            return;
        }

        // Reaction order is deterministic. A later PC window never jumps ahead of an earlier NPC window.
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)) return;

        if (!reactorCombatant.ReactionAvailable || !CanTakeReaction(reactor))
        {
            window.Resolved = true;
            window.Declined = true;
            window.ResolutionSummary = $"{reactor.Name} could not take the Opportunity Attack Reaction.";
            Log(campaign, "opportunity_attack_unavailable", window.ResolutionSummary, dmOnly: true);
            SyncOpportunityAttackPlayerDecision(campaign, encounter);
            return;
        }

        var moverCombatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(pendingMove.CombatantId, StringComparison.OrdinalIgnoreCase));
        var mover = moverCombatant is null ? null : campaign.Characters.FirstOrDefault(c => c.Id.Equals(moverCombatant.CharacterId, StringComparison.OrdinalIgnoreCase));
        if (mover is null)
        {
            window.Resolved = true;
            window.Declined = true;
            window.ResolutionSummary = "Opportunity Attack window was discarded because the moving creature no longer exists.";
            Log(campaign, "opportunity_attack_unavailable", window.ResolutionSummary, dmOnly: true);
            SyncOpportunityAttackPlayerDecision(campaign, encounter);
            return;
        }

        AttackProfile profile;
        try { profile = SelectMeleeAttack(reactor, null); }
        catch (InvalidOperationException)
        {
            window.Resolved = true;
            window.Declined = true;
            window.ResolutionSummary = $"{reactor.Name} had no legal melee attack for the Opportunity Attack.";
            Log(campaign, "opportunity_attack_unavailable", window.ResolutionSummary, dmOnly: true);
            SyncOpportunityAttackPlayerDecision(campaign, encounter);
            return;
        }

        campaign.PendingPlayerDecision = new PendingPlayerDecision
        {
            ActorCharacterId = reactor.Id,
            EncounterId = encounter.Id,
            CombatantId = reactorCombatant.Id,
            DecisionType = "opportunity_attack_reaction",
            Prompt = $"{mover.Name} is leaving {reactor.Name}'s reach. Use {reactor.Name}'s Reaction to make an Opportunity Attack with {profile.Name}?",
            Required = true,
            Options =
            [
                new PlayerDecisionOption { Id = "use_reaction", Label = $"Opportunity Attack • {profile.Name}", Description = "Spend the Reaction, then roll the attack and damage if it hits.", Value = profile.Name, Emphasis = "primary" },
                new PlayerDecisionOption { Id = "decline", Label = "Let them move", Description = "Do not spend the Reaction.", Value = "decline", Emphasis = "secondary" }
            ],
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reactor_combatant_id"] = reactorCombatant.Id,
                ["mover_combatant_id"] = pendingMove.CombatantId,
                ["attack_name"] = profile.Name,
                ["trigger_x"] = window.TriggerX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trigger_y"] = window.TriggerY.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        Touch(campaign);
        Log(campaign, "player_decision_requested", campaign.PendingPlayerDecision.Prompt, dmOnly: true);
        return;

        // Any impossible PC windows above have been auto-declined. If no unresolved window remains,
        // finish the move now. Otherwise the remaining windows belong to NPCs and stay available to the DM runtime.
        if (pendingMove.OpportunityAttacks.All(x => x.Resolved))
            FinalizePendingMoveIfReady(campaign, encounter);
    }

    public PlayerDecisionResolution ResolvePendingPlayerDecision(
        CampaignState campaign,
        string decisionId,
        string optionId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var decision = campaign.PendingPlayerDecision
            ?? throw new InvalidOperationException("There is no required player decision to resolve.");
        if (!decision.Required)
            throw new InvalidOperationException("The pending player decision is no longer required.");
        if (!decision.Id.Equals(decisionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied choice does not match the active player decision.");

        var option = decision.Options.FirstOrDefault(o => o.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("That option is not valid for the active player decision.");

        return decision.DecisionType.Trim().ToLowerInvariant() switch
        {
            "opportunity_attack_reaction" => ResolveOpportunityAttackDecision(campaign, decision, option),
            _ => throw new InvalidOperationException($"Player decision type '{decision.DecisionType}' is not supported.")
        };
    }

    private PlayerDecisionResolution ResolveOpportunityAttackDecision(
        CampaignState campaign,
        PendingPlayerDecision decision,
        PlayerDecisionOption option)
    {
        if (string.IsNullOrWhiteSpace(decision.EncounterId)
            || string.IsNullOrWhiteSpace(decision.CombatantId))
            throw new InvalidOperationException("The Opportunity Attack decision is missing combat context.");

        var encounter = RequireEncounter(campaign, decision.EncounterId);
        var pendingMove = encounter.PendingMove
            ?? throw new InvalidOperationException("The provoking movement no longer exists.");
        var window = pendingMove.OpportunityAttacks.FirstOrDefault(x =>
            x.ReactorCombatantId.Equals(decision.CombatantId, StringComparison.OrdinalIgnoreCase)
            && !x.Resolved)
            ?? throw new InvalidOperationException("The Opportunity Attack reaction window is no longer available.");
        var reactorCombatant = RequireCombatant(encounter, decision.CombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        if (!reactor.Id.Equals(decision.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The player decision actor no longer matches the reacting combatant.");
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Opportunity Attack decision no longer belongs to a player character.");

        campaign.PendingPlayerDecision = null;

        if (option.Id.Equals("decline", StringComparison.OrdinalIgnoreCase))
        {
            window.Resolved = true;
            window.Declined = true;
            window.ResolutionSummary = $"{reactor.Name} declined the Opportunity Attack.";
            Log(campaign, "opportunity_attack_declined", window.ResolutionSummary, dmOnly: true);
            Log(campaign, "player_decision_resolved", $"{reactor.Name}: {option.Label}.", dmOnly: true);
            FinalizePendingMoveIfReady(campaign, encounter);
            SyncOpportunityAttackPlayerDecision(campaign, encounter);
            Touch(campaign);
            return new PlayerDecisionResolution(
                decision.Id,
                decision.DecisionType,
                reactor.Id,
                option.Id,
                option.Label,
                window.ResolutionSummary,
                null);
        }

        if (!option.Id.Equals("use_reaction", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected Opportunity Attack decision option is not supported.");

        var attackName = string.IsNullOrWhiteSpace(option.Value)
            ? decision.Context.TryGetValue("attack_name", out var storedAttack) ? storedAttack : null
            : option.Value;
        var pendingRoll = RequestOpportunityAttackRoll(
            campaign,
            encounter.Id,
            reactorCombatant.Id,
            attackName);
        var summary = $"{reactor.Name} chose to spend the Reaction. {pendingRoll.Purpose}";
        Log(campaign, "player_decision_resolved", $"{reactor.Name}: {option.Label}.", dmOnly: true);
        Touch(campaign);
        return new PlayerDecisionResolution(
            decision.Id,
            decision.DecisionType,
            reactor.Id,
            option.Id,
            option.Label,
            summary,
            pendingRoll);
    }

    private static bool OpportunityAttackDecisionStillValid(
        CampaignState campaign,
        EncounterState encounter,
        PendingCombatMove pendingMove,
        PendingPlayerDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.CombatantId)
            || !decision.Context.TryGetValue("mover_combatant_id", out var moverCombatantId)
            || !pendingMove.CombatantId.Equals(moverCombatantId, StringComparison.OrdinalIgnoreCase))
            return false;
        var window = pendingMove.OpportunityAttacks.FirstOrDefault(x =>
            x.ReactorCombatantId.Equals(decision.CombatantId, StringComparison.OrdinalIgnoreCase)
            && !x.Resolved);
        if (window is null) return false;
        var combatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(decision.CombatantId, StringComparison.OrdinalIgnoreCase));
        var character = combatant is null
            ? null
            : campaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase));
        return character is not null
            && character.Id.Equals(decision.ActorCharacterId, StringComparison.OrdinalIgnoreCase)
            && character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
            && combatant!.ReactionAvailable
            && CanTakeReaction(character);
    }

    internal static void ClearOpportunityAttackDecision(CampaignState campaign)
    {
        if (campaign.PendingPlayerDecision?.DecisionType.Equals("opportunity_attack_reaction", StringComparison.OrdinalIgnoreCase) == true)
            campaign.PendingPlayerDecision = null;
    }
}
