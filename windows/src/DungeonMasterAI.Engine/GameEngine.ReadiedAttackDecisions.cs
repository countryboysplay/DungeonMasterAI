using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingPlayerDecision RequestReadiedAttackDecision(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");
        if (campaign.PendingPlayerDecision?.Required == true)
            throw new InvalidOperationException($"Resolve the required player decision first: {campaign.PendingPlayerDecision.Prompt}");

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var reactorCombatant = RequireCombatant(encounter, reactorCombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can receive a readied attack reaction decision.");
        var readied = RequireReadiedAction(reactorCombatant, "attack");
        if (!reactorCombatant.ReactionAvailable)
            throw new InvalidOperationException($"{reactor.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(reactor))
            throw new InvalidOperationException($"{reactor.Name} cannot take a Reaction right now.");

        var targetCombatant = RequireCombatant(encounter, readied.TargetCombatantId ?? "");
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (target.Dead)
            throw new InvalidOperationException($"{target.Name} is already dead.");
        var profile = SelectAttackProfile(reactor, readied.AttackName);
        ValidateAttackRange(reactorCombatant, targetCombatant, reactor, target, profile);
        var coverBonus = GetCoverBonus(encounter, reactorCombatant, targetCombatant);
        if (coverBonus >= 100)
            throw new InvalidOperationException($"{target.Name} has Total Cover from {reactor.Name}; the readied attack cannot be released at this trigger.");

        var decision = new PendingPlayerDecision
        {
            ActorCharacterId = reactor.Id,
            EncounterId = encounter.Id,
            CombatantId = reactorCombatant.Id,
            DecisionType = "readied_attack_reaction",
            Prompt = $"The trigger occurred for {reactor.Name}'s readied {profile.Name} attack against {target.Name}. Use the Reaction now?",
            Required = true,
            Options =
            [
                new PlayerDecisionOption
                {
                    Id = "use_reaction",
                    Label = $"Release {profile.Name}",
                    Description = "Spend the Reaction, then roll the attack and damage if it hits.",
                    Value = profile.Name,
                    Emphasis = "primary"
                },
                new PlayerDecisionOption
                {
                    Id = "decline_trigger",
                    Label = "Ignore this trigger",
                    Description = "Keep the Reaction and the readied action available in case the trigger occurs again before it expires.",
                    Value = "decline",
                    Emphasis = "secondary"
                }
            ],
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target_combatant_id"] = targetCombatant.Id,
                ["attack_name"] = profile.Name
            }
        };
        campaign.PendingPlayerDecision = decision;
        Touch(campaign);
        Log(campaign, "player_decision_requested", decision.Prompt, dmOnly: true);
        return decision;
    }

    private PlayerDecisionResolution ResolveReadiedAttackDecision(
        CampaignState campaign,
        PendingPlayerDecision decision,
        PlayerDecisionOption option)
    {
        if (string.IsNullOrWhiteSpace(decision.EncounterId) || string.IsNullOrWhiteSpace(decision.CombatantId))
            throw new InvalidOperationException("The readied attack decision is missing combat context.");
        var encounter = RequireEncounter(campaign, decision.EncounterId);
        var combatant = RequireCombatant(encounter, decision.CombatantId);
        var reactor = RequireCharacter(campaign, combatant.CharacterId);
        if (!reactor.Id.Equals(decision.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The readied attack decision actor no longer matches the reacting combatant.");
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The readied attack decision no longer belongs to a player character.");
        _ = RequireReadiedAction(combatant, "attack");

        campaign.PendingPlayerDecision = null;
        if (option.Id.Equals("decline_trigger", StringComparison.OrdinalIgnoreCase))
        {
            var summary = $"{reactor.Name} ignored this readied-attack trigger. The Reaction and readied action remain available until they expire or another legal trigger is accepted.";
            Log(campaign, "readied_attack_declined", summary, dmOnly: true);
            Log(campaign, "player_decision_resolved", $"{reactor.Name}: {option.Label}.", dmOnly: true);
            Touch(campaign);
            return new PlayerDecisionResolution(
                decision.Id,
                decision.DecisionType,
                reactor.Id,
                option.Id,
                option.Label,
                summary,
                null);
        }

        if (!option.Id.Equals("use_reaction", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected readied attack decision option is not supported.");

        var pendingRoll = RequestReadiedAttackRoll(campaign, encounter.Id, combatant.Id);
        var accepted = $"{reactor.Name} accepted the trigger and committed the Reaction. {pendingRoll.Purpose}";
        Log(campaign, "player_decision_resolved", $"{reactor.Name}: {option.Label}.", dmOnly: true);
        Touch(campaign);
        return new PlayerDecisionResolution(
            decision.Id,
            decision.DecisionType,
            reactor.Id,
            option.Id,
            option.Label,
            accepted,
            pendingRoll);
    }
}
