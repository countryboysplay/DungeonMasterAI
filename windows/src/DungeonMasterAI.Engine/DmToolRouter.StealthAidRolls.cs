using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DmToolRouter
{
    private static object ResolveHideTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var combatantId = RequiredString(arguments, "combatant_id");
        return IsPlayerCombatant(campaign, encounterId, combatantId)
            ? engine.RequestHideRoll(campaign, encounterId, combatantId)
            : engine.TakeHide(campaign, encounterId, combatantId, dice);
    }

    private static object ResolveHiddenSearchTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var searcherCombatantId = RequiredString(arguments, "searcher_combatant_id");
        var targetCombatantId = RequiredString(arguments, "target_combatant_id");
        return IsPlayerCombatant(campaign, encounterId, searcherCombatantId)
            ? engine.RequestHiddenSearchRoll(campaign, encounterId, searcherCombatantId, targetCombatantId)
            : engine.SearchForHiddenCombatant(campaign, encounterId, searcherCombatantId, targetCombatantId, dice);
    }

    private static object ResolveFirstAidTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var helperCombatantId = RequiredString(arguments, "helper_combatant_id");
        var targetCombatantId = RequiredString(arguments, "target_combatant_id");
        return IsPlayerCombatant(campaign, encounterId, helperCombatantId)
            ? engine.RequestFirstAidRoll(campaign, encounterId, helperCombatantId, targetCombatantId)
            : engine.TakeFirstAid(campaign, encounterId, helperCombatantId, targetCombatantId, dice);
    }
}
