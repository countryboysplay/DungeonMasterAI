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
        if (!caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Readied projectile spell sequences currently require a player-character caster so all projectile dice remain player-owned.");

        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        var projectileCount = checked(spell.BaseProjectiles + (upcastLevels * spell.ExtraProjectilesPerSlot));
        if (projectileCount < 1)
            throw new InvalidOperationException($"{spell.Name} resolved to an invalid projectile count.");

        ValidateSpellTargetType(target, spell);
        ValidateSpellRange(campaign, encounter, caster, target, spell);
        var allocations = Enumerable.Repeat(target.Id, projectileCount).ToArray();
        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();

        return resolution switch
        {
            "projectile_attack" => BeginPlayerProjectileSpellSequence(
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
            "projectile_auto" => BeginPlayerAutoProjectileSpellSequence(
                campaign,
                caster,
                spell,
                castAtLevel,
                usedSlot,
                spell.RequiresConcentration,
                encounter,
                allocations,
                readiedReaction: true),
            _ => throw new InvalidOperationException($"{spell.Name} is not configured as a projectile spell.")
        };
    }
}
