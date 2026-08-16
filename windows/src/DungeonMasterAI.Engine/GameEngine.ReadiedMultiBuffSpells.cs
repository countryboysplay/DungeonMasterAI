using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private sealed record ReadiedMultiBuffPlan(
        IReadOnlyList<string> TargetCombatantIds,
        IReadOnlyList<string> TargetCharacterIds,
        IReadOnlyList<string> TargetNames);

    private ReadiedMultiBuffPlan PlanReadiedMultiBuffSpell(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        EncounterState encounter,
        int castAtLevel,
        IReadOnlyList<string>? targetCombatantIds)
    {
        if ((spell.Resolution ?? "").Trim().Equals("multi_buff", StringComparison.OrdinalIgnoreCase) is false)
            throw new InvalidOperationException($"{spell.Name} is not configured as a multi-target buff spell.");
        if (spell.BaseTargets < 1)
            throw new InvalidOperationException($"{spell.Name} has no configured target count.");
        if (targetCombatantIds is null || targetCombatantIds.Count == 0)
            throw new InvalidOperationException($"{spell.Name} requires at least one release target when its readied trigger occurs.");

        var maximumTargets = checked(spell.BaseTargets + Math.Max(0, castAtLevel - spell.Level) * spell.ExtraTargetsPerSlot);
        var distinctCombatantIds = targetCombatantIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctCombatantIds.Length < 1 || distinctCombatantIds.Length > maximumTargets)
            throw new InvalidOperationException($"{spell.Name} can target from 1 to {maximumTargets} creature{(maximumTargets == 1 ? "" : "s")} at this slot level.");

        var characterIds = new List<string>(distinctCombatantIds.Length);
        var names = new List<string>(distinctCombatantIds.Length);
        foreach (var combatantId in distinctCombatantIds)
        {
            var targetCombatant = RequireCombatant(encounter, combatantId);
            var target = RequireCharacter(campaign, targetCombatant.CharacterId);
            if (target.Dead)
                throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for {spell.Name}.");
            ValidateSpellTargetType(target, spell);
            ValidateSpellRange(campaign, encounter, caster, target, spell);
            characterIds.Add(target.Id);
            names.Add(target.Name);
        }

        return new ReadiedMultiBuffPlan(distinctCombatantIds, characterIds, names);
    }

    private SpellCastResult ReleaseReadiedMultiBuffSpell(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSpellSlot,
        ReadiedMultiBuffPlan plan)
    {
        var targets = plan.TargetCharacterIds.Select(id => RequireCharacter(campaign, id)).ToArray();
        foreach (var target in targets)
            ApplyD20BonusEffect(campaign, caster, target, spell);

        var targetResults = targets.Select((target, index) =>
        {
            var benefits = new List<string>();
            if (!string.IsNullOrWhiteSpace(spell.AttackRollBonusExpression)) benefits.Add($"attack rolls +{spell.AttackRollBonusExpression}");
            if (!string.IsNullOrWhiteSpace(spell.SavingThrowBonusExpression)) benefits.Add($"saving throws +{spell.SavingThrowBonusExpression}");
            if (spell.ArmorClassBonus != 0) benefits.Add($"AC {(spell.ArmorClassBonus > 0 ? "+" : "")}{spell.ArmorClassBonus}");
            if (spell.SpeedModifierFeet != 0) benefits.Add($"Speed {(spell.SpeedModifierFeet > 0 ? "+" : "")}{spell.SpeedModifierFeet} ft");
            var detail = benefits.Count == 0 ? "configured deterministic benefits" : string.Join(", ", benefits);
            return new SpellTargetResolution(target.Id, target.Name, index + 1, null, null, null, 0, $"{target.Name} gained {spell.Name}: {detail}.");
        }).ToArray();

        var summary = $"{caster.Name} released {spell.Name} on {string.Join(", ", targets.Select(t => t.Name))}.";
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            caster.Id,
            targets.Length == 1 ? targets[0].Id : null,
            castAtLevel,
            usedSpellSlot,
            false,
            null,
            null,
            null,
            0,
            spell.RequiresConcentration,
            summary,
            targetResults);
    }
}
