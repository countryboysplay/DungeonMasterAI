using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DmToolRouter
{
    private static object ResolveReadiedMoveTriggerTool(
        GameEngine engine,
        CampaignState campaign,
        JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var combatantId = RequiredString(arguments, "combatant_id");
        var gridX = RequiredInt(arguments, "grid_x");
        var gridY = RequiredInt(arguments, "grid_y");
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Encounter '{encounterId}' was not found.");
        var combatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(combatantId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Combatant '{combatantId}' was not found in encounter '{encounter.Name}'.");
        var character = campaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Character '{combatant.CharacterId}' was not found.");

        if (character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            return engine.RequestReadiedMoveDecision(campaign, encounter.Id, combatant.Id, gridX, gridY);

        return engine.TriggerReadiedMove(campaign, encounter.Id, combatant.Id, gridX, gridY);
    }
}
