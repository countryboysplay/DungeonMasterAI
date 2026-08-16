using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public enum D20RollMode
{
    Normal,
    Advantage,
    Disadvantage
}

public static class CharacterMechanics
{
    public static int AbilityModifier(int score) => (int)Math.Floor((score - 10) / 2.0);

    public static int ProficiencyBonusForLevel(int level) => Math.Clamp(2 + (Math.Max(1, level) - 1) / 4, 2, 6);

    public static string NormalizeAbility(string ability)
    {
        var value = (ability ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "str" or "strength" => "strength",
            "dex" or "dexterity" => "dexterity",
            "con" or "constitution" => "constitution",
            "int" or "intelligence" => "intelligence",
            "wis" or "wisdom" => "wisdom",
            "cha" or "charisma" => "charisma",
            _ => throw new ArgumentException($"Unknown ability '{ability}'.")
        };
    }

    public static int AbilityScore(CharacterSheet character, string ability)
    {
        var normalized = NormalizeAbility(ability);
        if (character.Abilities.TryGetValue(normalized, out var score)) return score;
        var shortName = normalized[..3];
        if (character.Abilities.TryGetValue(shortName, out score)) return score;
        return 10;
    }

    public static bool HasCondition(CharacterSheet character, string condition)
    {
        ArgumentNullException.ThrowIfNull(character);
        return character.Conditions.Any(c => c.Equals(condition, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsIncapacitated(CharacterSheet character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return character.Dead
            || HasCondition(character, "Incapacitated")
            || HasCondition(character, "Unconscious")
            || HasCondition(character, "Paralyzed")
            || HasCondition(character, "Stunned");
    }

    public static bool AutomaticallyFailsSavingThrow(CharacterSheet character, string ability)
    {
        ArgumentNullException.ThrowIfNull(character);
        var normalized = NormalizeAbility(ability);
        if (normalized is not ("strength" or "dexterity")) return false;
        return HasCondition(character, "Paralyzed")
            || HasCondition(character, "Stunned")
            || HasCondition(character, "Unconscious");
    }

    public static D20RollMode SavingThrowModeFromConditions(CharacterSheet character, string ability)
    {
        ArgumentNullException.ThrowIfNull(character);
        var normalized = NormalizeAbility(ability);
        return normalized == "dexterity" && HasCondition(character, "Restrained")
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
    }

    public static int EffectiveSpeed(CharacterSheet character, IEnumerable<ActiveEffectState>? activeEffects = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.Dead
            || HasCondition(character, "Grappled")
            || HasCondition(character, "Unconscious")
            || HasCondition(character, "Paralyzed")
            || HasCondition(character, "Restrained"))
            return 0;
        var effectModifier = activeEffects?
            .Where(e => e.TargetCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.SpeedModifierFeet) ?? 0;
        return Math.Max(0, character.Speed + effectModifier - (5 * Math.Clamp(character.ExhaustionLevel, 0, 6)));
    }

    public static AttackProfile UnarmedStrikeProfile(CharacterSheet character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var strengthModifier = AbilityModifier(AbilityScore(character, "strength"));
        return new AttackProfile
        {
            Name = "Unarmed Strike",
            AttackBonus = strengthModifier + Math.Max(0, character.ProficiencyBonus),
            DamageExpression = Math.Max(0, 1 + strengthModifier).ToString(System.Globalization.CultureInfo.InvariantCulture),
            DamageType = "Bludgeoning",
            ReachFeet = 5
        };
    }

    public static D20TestResult ResolveD20Test(
        CharacterSheet character,
        string ability,
        int difficultyClass,
        int rollOne,
        int? rollTwo = null,
        D20RollMode mode = D20RollMode.Normal,
        bool proficient = false,
        int circumstanceModifier = 0)
    {
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && (rollTwo.Value < 1 || rollTwo.Value > 20)) throw new ArgumentOutOfRangeException(nameof(rollTwo));
        if (difficultyClass < 0) throw new ArgumentOutOfRangeException(nameof(difficultyClass));

        var chosen = rollOne;
        if (rollTwo.HasValue)
        {
            chosen = mode switch
            {
                D20RollMode.Advantage => Math.Max(rollOne, rollTwo.Value),
                D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo.Value),
                _ => rollOne
            };
        }

        var abilityModifier = AbilityModifier(AbilityScore(character, ability));
        var proficiencyModifier = proficient ? Math.Max(0, character.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
        var total = chosen + abilityModifier + proficiencyModifier + circumstanceModifier - exhaustionPenalty;
        var success = total >= difficultyClass;
        var label = NormalizeAbility(ability);
        var summary = $"{label} D20 Test {total} vs DC {difficultyClass}: {(success ? "success" : "failure")}.";
        if (exhaustionPenalty > 0) summary += $" Exhaustion applied -{exhaustionPenalty}.";
        return new D20TestResult(rollOne, rollTwo, chosen, abilityModifier, proficiencyModifier, exhaustionPenalty, total, difficultyClass, success, summary);
    }
}
