using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DmToolRouter
{
    private static object ResolveSearchActionTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var combatantId = RequiredString(arguments, "combatant_id");
        var skill = RequiredString(arguments, "skill");
        var dc = RequiredInt(arguments, "dc");
        return IsPlayerCombatant(campaign, encounterId, combatantId)
            ? engine.RequestSearchActionRoll(campaign, encounterId, combatantId, skill, dc)
            : engine.TakeSearchAction(campaign, encounterId, combatantId, skill, dc, dice);
    }

    private static object ResolveStudyActionTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var combatantId = RequiredString(arguments, "combatant_id");
        var skill = RequiredString(arguments, "skill");
        var dc = RequiredInt(arguments, "dc");
        return IsPlayerCombatant(campaign, encounterId, combatantId)
            ? engine.RequestStudyActionRoll(campaign, encounterId, combatantId, skill, dc)
            : engine.TakeStudyAction(campaign, encounterId, combatantId, skill, dc, dice);
    }

    private static object ResolveInfluenceActionTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var combatantId = RequiredString(arguments, "combatant_id");
        var skill = RequiredString(arguments, "skill");
        var dc = RequiredInt(arguments, "dc");
        return IsPlayerCombatant(campaign, encounterId, combatantId)
            ? engine.RequestInfluenceActionRoll(campaign, encounterId, combatantId, skill, dc)
            : engine.TakeInfluenceAction(campaign, encounterId, combatantId, skill, dc, dice);
    }

    private static bool IsPlayerCombatant(CampaignState campaign, string encounterId, string combatantId)
    {
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase));
        var combatant = encounter?.Combatants.FirstOrDefault(c => c.Id.Equals(combatantId, StringComparison.OrdinalIgnoreCase));
        if (combatant is null) return false;
        var character = campaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase));
        return character?.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) == true;
    }
}
