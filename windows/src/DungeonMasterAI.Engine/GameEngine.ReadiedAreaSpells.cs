using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private sealed record ReadiedAreaSpellPlan(
        int PointX,
        int PointY,
        string Direction,
        IReadOnlyList<string> TargetCombatantIds,
        IReadOnlyList<string> TargetNames);

    private ReadiedAreaSpellPlan PlanReadiedAreaSpell(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        EncounterState encounter,
        int? centerX,
        int? centerY,
        string? direction)
    {
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{caster.Name} is not a combatant in the active encounter.");
        if (!casterCombatant.Positioned)
            throw new InvalidOperationException($"{caster.Name} must be positioned before releasing readied {spell.Name}.");
        if (string.IsNullOrWhiteSpace(spell.AreaShape) || spell.AreaSizeFeet <= 0)
            throw new InvalidOperationException($"{spell.Name} has incomplete area geometry metadata.");
        if (string.IsNullOrWhiteSpace(spell.SaveAbility))
            throw new InvalidOperationException($"{spell.Name} has no configured saving throw ability.");

        var shape = spell.AreaShape.Trim().ToLowerInvariant();
        var origin = (spell.AreaOrigin ?? "self").Trim().ToLowerInvariant();
        var pointX = origin == "point"
            ? centerX ?? throw new InvalidOperationException($"{spell.Name} requires an area center X coordinate when its trigger occurs.")
            : casterCombatant.GridX;
        var pointY = origin == "point"
            ? centerY ?? throw new InvalidOperationException($"{spell.Name} requires an area center Y coordinate when its trigger occurs.")
            : casterCombatant.GridY;
        var normalizedDirection = NormalizeReadiedAreaDirection(direction);

        if (origin == "point")
        {
            var range = Math.Sqrt(Math.Pow(pointX - casterCombatant.GridX, 2) + Math.Pow(pointY - casterCombatant.GridY, 2)) * 5.0;
            if (spell.RangeFeet > 0 && range > spell.RangeFeet + 0.001)
                throw new InvalidOperationException($"The proposed center is {range:0.#} feet away, beyond {spell.Name}'s range of {spell.RangeFeet} feet.");
            if (GetAreaCoverBonus(encounter, casterCombatant.GridX, casterCombatant.GridY, pointX, pointY) >= 100)
                throw new InvalidOperationException($"{spell.Name}'s proposed point of origin is behind Total Cover or another line-of-effect blocker.");
        }

        var affected = encounter.Combatants
            .Where(c => c.Positioned)
            .Where(c => AreaContains(shape, spell.AreaSizeFeet, origin, casterCombatant, c, pointX, pointY, normalizedDirection, spell.AreaWidthFeet))
            .Where(c => GetAreaCoverBonus(encounter, pointX, pointY, c.GridX, c.GridY) < 100)
            .ToArray();
        if (affected.Length == 0)
            throw new InvalidOperationException($"No positioned creatures are inside the proposed {spell.Name} area with an unblocked line of effect.");

        var names = affected.Select(c => RequireCharacter(campaign, c.CharacterId).Name).ToArray();
        return new ReadiedAreaSpellPlan(pointX, pointY, normalizedDirection, affected.Select(c => c.Id).ToArray(), names);
    }

    private static string NormalizeReadiedAreaDirection(string? direction)
    {
        var normalized = (direction ?? "north").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        _ = SpellAreaGeometry.NormalizeDirection(normalized);
        return normalized switch
        {
            "n" => "north",
            "ne" or "northeast" => "north-east",
            "e" => "east",
            "se" or "southeast" => "south-east",
            "s" => "south",
            "sw" or "southwest" => "south-west",
            "w" => "west",
            "nw" or "northwest" => "north-west",
            _ => normalized
        };
    }

    private SpellCastResult BeginReadiedAreaSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSpellSlot,
        EncounterState encounter,
        int? centerX,
        int? centerY,
        string? direction,
        DiceService dice)
    {
        var plan = PlanReadiedAreaSpell(campaign, caster, spell, encounter, centerX, centerY, direction);
        return BeginPlayerAreaSpellSequence(
            campaign,
            caster,
            spell,
            castAtLevel,
            usedSpellSlot,
            spell.RequiresConcentration,
            encounter,
            plan.PointX,
            plan.PointY,
            plan.Direction,
            plan.TargetCombatantIds,
            dice,
            readiedReaction: true);
    }
}
