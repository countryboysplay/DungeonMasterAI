using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DmToolRouter
{
    private static object ResolveReadiedSpellTriggerTool(
        GameEngine engine,
        DiceService dice,
        CampaignState campaign,
        JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var combatantId = RequiredString(arguments, "combatant_id");
        var targetCombatantId = OptionalString(arguments, "target_combatant_id");
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Encounter '{encounterId}' was not found.");
        var combatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(combatantId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Combatant '{combatantId}' was not found in encounter '{encounter.Name}'.");
        var caster = campaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Character '{combatant.CharacterId}' was not found.");

        if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            return engine.RequestReadiedSpellDecision(campaign, encounter.Id, combatant.Id, targetCombatantId);

        return engine.TriggerReadiedSpell(campaign, encounter.Id, combatant.Id, dice, targetCombatantId);
    }
}
