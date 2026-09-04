using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingPlayerDecision RequestReadiedMoveDecision(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        int gridX,
        int gridY)
    {
        Guard.NotNull(campaign, nameof(campaign));
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");
        if (campaign.PendingPlayerDecision?.Required == true)
            throw new InvalidOperationException($"Resolve the required player decision first: {campaign.PendingPlayerDecision.Prompt}");

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve the pending movement reaction window before triggering readied movement.");
        var combatant = RequireCombatant(encounter, reactorCombatantId);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can receive a readied movement Reaction decision.");
        _ = RequireReadiedAction(combatant, "move");
        if (!combatant.ReactionAvailable)
            throw new InvalidOperationException($"{character.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(character))
            throw new InvalidOperationException($"{character.Name} cannot take a Reaction right now.");
        if (!combatant.Positioned)
            throw new InvalidOperationException($"{character.Name} must be positioned before using readied movement.");

        EnsureSquareAvailable(encounter, combatant.Id, gridX, gridY);
        var map = ResolveEncounterMap(campaign, encounter.Id);
        var path = TraceGridPath(combatant.GridX, combatant.GridY, gridX, gridY);
        ValidateMovementPath(encounter, combatant.Id, path);
        ValidateMapMovementPath(map, combatant.GridX, combatant.GridY, path);
        var distanceFeet = GridDistanceFeet(combatant.GridX, combatant.GridY, gridX, gridY);
        var movementCostFeet = MovementCostFeet(encounter, map, path, character);
        var speed = CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects);
        if (movementCostFeet > speed)
            throw new InvalidOperationException($"That readied move costs {movementCostFeet} feet, exceeding {character.Name}'s Speed of {speed} feet.");

        var decision = new PendingPlayerDecision
        {
            ActorCharacterId = character.Id,
            EncounterId = encounter.Id,
            CombatantId = combatant.Id,
            DecisionType = "readied_move_reaction",
            Prompt = $"The trigger occurred for {character.Name}'s readied movement. Use the Reaction to move from ({combatant.GridX}, {combatant.GridY}) to ({gridX}, {gridY}) for {distanceFeet} feet?",
            Required = true,
            Options =
            [
                new PlayerDecisionOption
                {
                    Id = "use_reaction",
                    Label = "Use Reaction and move",
                    Description = "Commit the readied movement to the shown destination. Opportunity Attacks, hazards, and difficult terrain still resolve through the authoritative engine.",
                    Value = $"{gridX},{gridY}",
                    Emphasis = "primary"
                },
                new PlayerDecisionOption
                {
                    Id = "decline_trigger",
                    Label = "Ignore this trigger",
                    Description = "Keep the Reaction and the readied movement available in case the trigger occurs again before it expires.",
                    Value = "decline",
                    Emphasis = "secondary"
                }
            ],
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["grid_x"] = gridX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["grid_y"] = gridY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["distance_feet"] = distanceFeet.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["movement_cost_feet"] = movementCostFeet.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        campaign.PendingPlayerDecision = decision;
        Touch(campaign);
        Log(campaign, "player_decision_requested", decision.Prompt, dmOnly: true);
        return decision;
    }

    private PlayerDecisionResolution ResolveReadiedMoveDecision(
        CampaignState campaign,
        PendingPlayerDecision decision,
        PlayerDecisionOption option)
    {
        if (string.IsNullOrWhiteSpace(decision.EncounterId) || string.IsNullOrWhiteSpace(decision.CombatantId))
            throw new InvalidOperationException("The readied movement decision is missing combat context.");
        var encounter = RequireEncounter(campaign, decision.EncounterId);
        var combatant = RequireCombatant(encounter, decision.CombatantId);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!character.Id.Equals(decision.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The readied movement decision actor no longer matches the reacting combatant.");
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The readied movement decision no longer belongs to a player character.");
        _ = RequireReadiedAction(combatant, "move");

        if (option.Id.Equals("decline_trigger", StringComparison.OrdinalIgnoreCase))
        {
            campaign.PendingPlayerDecision = null;
            var reactionText = combatant.ReactionAvailable
                ? "The Reaction and readied movement remain available until they expire or another legal trigger is accepted."
                : "The readied movement remains, but the Reaction has already been spent and cannot be used again until the start of the next turn.";
            var summary = $"{character.Name} ignored this readied-movement trigger. {reactionText}";
            Log(campaign, "readied_move_declined", summary, dmOnly: true);
            Log(campaign, "player_decision_resolved", $"{character.Name}: {option.Label}.", dmOnly: true);
            Touch(campaign);
            return new PlayerDecisionResolution(decision.Id, decision.DecisionType, character.Id, option.Id, option.Label, summary, null);
        }

        if (!option.Id.Equals("use_reaction", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected readied movement decision option is not supported.");

        var gridX = RequiredDecisionInt(decision, "grid_x");
        var gridY = RequiredDecisionInt(decision, "grid_y");
        campaign.PendingPlayerDecision = null;
        try
        {
            var result = TriggerReadiedMove(campaign, encounter.Id, combatant.Id, gridX, gridY);
            Log(campaign, "player_decision_resolved", $"{character.Name}: {option.Label}.", dmOnly: true);
            Touch(campaign);
            return new PlayerDecisionResolution(decision.Id, decision.DecisionType, character.Id, option.Id, option.Label, result.Summary, null);
        }
        catch
        {
            campaign.PendingPlayerDecision = decision;
            throw;
        }
    }

    private static int RequiredDecisionInt(PendingPlayerDecision decision, string key)
    {
        if (!decision.Context.TryGetValue(key, out var raw)
            || !int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"The readied movement decision is missing a valid '{key}' value.");
        return value;
    }
}
