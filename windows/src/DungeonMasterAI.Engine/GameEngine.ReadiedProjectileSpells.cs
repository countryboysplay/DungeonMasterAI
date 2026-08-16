using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private SpellCastResult BeginReadiedProjectileSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        EncounterState encounter,
        DiceService dice)
    {
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        var projectileCount = checked(spell.BaseProjectiles + (upcastLevels * spell.ExtraProjectilesPerSlot));
        if (projectileCount < 1)
            throw new InvalidOperationException($"{spell.Name} resolved to an invalid projectile count.");

        ValidateSpellTargetType(target, spell);
        ValidateSpellRange(campaign, encounter, caster, target, spell);
        var allocations = Enumerable.Repeat(target.Id, projectileCount).ToArray();
        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();
        var playerCaster = caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase);

        return resolution switch
        {
            "projectile_attack" when playerCaster => BeginPlayerProjectileSpellSequence(
                campaign,
                caster,
                spell,
                castAtLevel,
                usedSlot,
                spell.RequiresConcentration,
                encounter,
                allocations,
                dice,
                readiedReaction: true),
            "projectile_attack" => BeginAutomaticReadiedProjectileSpellSequence(
                campaign,
                caster,
                spell,
                castAtLevel,
                usedSlot,
                spell.RequiresConcentration,
                encounter,
                allocations,
                dice),
            "projectile_auto" when playerCaster => BeginPlayerAutoProjectileSpellSequence(
                campaign,
                caster,
                spell,
                castAtLevel,
                usedSlot,
                spell.RequiresConcentration,
                encounter,
                allocations,
                dice,
                readiedReaction: true),
            "projectile_auto" => BeginAutomaticReadiedAutoProjectileSpellSequence(
                campaign,
                caster,
                spell,
                castAtLevel,
                usedSlot,
                spell.RequiresConcentration,
                encounter,
                allocations,
                dice),
            _ => throw new InvalidOperationException($"{spell.Name} is not configured as a projectile spell.")
        };
    }
}
