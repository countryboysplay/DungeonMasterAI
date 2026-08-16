using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DmToolRouter
{
    private static object ResolveUnarmedGrappleTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var attackerCombatantId = RequiredString(arguments, "attacker_combatant_id");
        var targetCombatantId = RequiredString(arguments, "target_combatant_id");
        var saveAbility = OptionalString(arguments, "save_ability");
        return PlayerUnarmedTargetNeedsRoll(campaign, encounterId, targetCombatantId, saveAbility)
            ? engine.RequestUnarmedGrappleSaveRoll(campaign, encounterId, attackerCombatantId, targetCombatantId, saveAbility)
            : engine.ResolveUnarmedGrapple(campaign, encounterId, attackerCombatantId, targetCombatantId, dice, saveAbility);
    }

    private static object ResolveUnarmedShoveTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var attackerCombatantId = RequiredString(arguments, "attacker_combatant_id");
        var targetCombatantId = RequiredString(arguments, "target_combatant_id");
        var effect = RequiredString(arguments, "effect");
        var saveAbility = OptionalString(arguments, "save_ability");
        return PlayerUnarmedTargetNeedsRoll(campaign, encounterId, targetCombatantId, saveAbility)
            ? engine.RequestUnarmedShoveSaveRoll(campaign, encounterId, attackerCombatantId, targetCombatantId, effect, saveAbility)
            : engine.ResolveUnarmedShove(campaign, encounterId, attackerCombatantId, targetCombatantId, effect, dice, saveAbility);
    }

    private static object ResolveEscapeGrappleTool(GameEngine engine, DiceService dice, CampaignState campaign, JsonElement arguments)
    {
        var encounterId = RequiredString(arguments, "encounter_id");
        var targetCombatantId = RequiredString(arguments, "target_combatant_id");
        var grapplerCombatantId = RequiredString(arguments, "grappler_combatant_id");
        var skill = RequiredString(arguments, "skill");
        return IsPlayerCombatant(campaign, encounterId, targetCombatantId)
            ? engine.RequestEscapeGrappleRoll(campaign, encounterId, targetCombatantId, grapplerCombatantId, skill)
            : engine.EscapeGrapple(campaign, encounterId, targetCombatantId, grapplerCombatantId, skill, dice);
    }

    private static bool PlayerUnarmedTargetNeedsRoll(
        CampaignState campaign,
        string encounterId,
        string targetCombatantId,
        string? requestedAbility)
    {
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase));
        var combatant = encounter?.Combatants.FirstOrDefault(c => c.Id.Equals(targetCombatantId, StringComparison.OrdinalIgnoreCase));
        if (combatant is null) return false;
        var target = campaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase));
        if (target?.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) != true) return false;

        var normalized = (requestedAbility ?? "").Trim().ToLowerInvariant();
        if (normalized is "strength" or "str")
            return !CharacterMechanics.AutomaticallyFailsSavingThrow(target, "strength");
        if (normalized is "dexterity" or "dex")
            return !CharacterMechanics.AutomaticallyFailsSavingThrow(target, "dexterity");

        // When the target chooses the better save, no player die is needed only if both legal saves fail automatically.
        return !(CharacterMechanics.AutomaticallyFailsSavingThrow(target, "strength")
            && CharacterMechanics.AutomaticallyFailsSavingThrow(target, "dexterity"));
    }
}
