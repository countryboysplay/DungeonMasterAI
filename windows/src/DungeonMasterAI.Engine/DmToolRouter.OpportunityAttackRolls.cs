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
        return IsPlayerCombatant(campaign, encounterId, reactorCombatantId)
            ? engine.RequestOpportunityAttackRoll(campaign, encounterId, reactorCombatantId, attackName)
            : engine.ResolveOpportunityAttack(campaign, encounterId, reactorCombatantId, attackName, dice);
    }
}
