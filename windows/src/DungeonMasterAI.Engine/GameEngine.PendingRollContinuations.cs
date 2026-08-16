using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private string? ResumePendingRollContinuation(
        CampaignState campaign,
        IReadOnlyDictionary<string, string> context,
        DiceService dice)
    {
        if (!context.TryGetValue("continuation_resolution_key", out var resolutionKey)
            || string.IsNullOrWhiteSpace(resolutionKey))
            return null;

        return resolutionKey.Trim().ToLowerInvariant() switch
        {
            "projectile_spell_sequence" => ResumePlayerProjectileSpellSequenceAfterConcentration(campaign, context, dice),
            "auto_projectile_spell_sequence" => ResumePlayerAutoProjectileSpellSequenceAfterConcentration(campaign, context, dice),
            _ => null
        };
    }
}