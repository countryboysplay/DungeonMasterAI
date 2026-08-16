using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DmToolRouter
{
    private static object ResolveOpportunityAttackTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var reactorCombatantId = RequiredString(arguments, "reactor_combatant_id");
        var attackName = OptionalString(arguments, "attack_name");
        if (IsPlayerCombatant(campaign, encounterId, reactorCombatantId))
        {
            var decision = campaign.PendingPlayerDecision;
            if (decision?.Required == true
                && decision.DecisionType.Equals("opportunity_attack_reaction", StringComparison.OrdinalIgnoreCase)
                && decision.CombatantId?.Equals(reactorCombatantId, StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException($"Player decision required: {decision.Prompt}");

            throw new InvalidOperationException("A player character must personally choose whether to spend the Reaction for an Opportunity Attack. The DM runtime cannot make that choice.");
        }

        return engine.ResolveOpportunityAttack(campaign, encounterId, reactorCombatantId, attackName, dice);
    }
}
