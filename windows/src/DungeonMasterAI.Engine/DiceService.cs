using System.Text.RegularExpressions;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class DiceService
{
    private readonly Func<int, int, int> _nextInt;

    public DiceService() : this((minimumInclusive, maximumExclusive) => Random.Shared.Next(minimumInclusive, maximumExclusive))
    {
    }

    public DiceService(Func<int, int, int> nextInt)
    {
        _nextInt = nextInt ?? throw new ArgumentNullException(nameof(nextInt));
    }

    [GeneratedRegex(@"^\s*(?<count>\d*)d(?<sides>\d+)(?<modifier>[+-]\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceExpressionRegex();

    [GeneratedRegex(@"^\s*(?<fixed>\d+)\s*$")]
    private static partial Regex FixedExpressionRegex();

    public DiceRoll Roll(string expression)
    {
        var raw = expression ?? "";
        var fixedMatch = FixedExpressionRegex().Match(raw);
        if (fixedMatch.Success)
        {
            var value = int.Parse(fixedMatch.Groups["fixed"].Value);
            if (value > 1_000_000) throw new ArgumentOutOfRangeException(nameof(expression), "Fixed roll values must not exceed 1,000,000.");
            return new DiceRoll(raw.Trim(), Array.Empty<int>(), value, value);
        }

        var match = DiceExpressionRegex().Match(raw);
        if (!match.Success)
            throw new ArgumentException($"Invalid dice expression: {expression}", nameof(expression));

        var count = string.IsNullOrWhiteSpace(match.Groups["count"].Value)
            ? 1
            : int.Parse(match.Groups["count"].Value);
        var sides = int.Parse(match.Groups["sides"].Value);
        var modifier = match.Groups["modifier"].Success
            ? int.Parse(match.Groups["modifier"].Value)
            : 0;

        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(expression), "Dice count must be between 1 and 100.");
        if (sides is < 2 or > 1000) throw new ArgumentOutOfRangeException(nameof(expression), "Die sides must be between 2 and 1000.");

        var rolls = new int[count];
        for (var i = 0; i < count; i++) rolls[i] = NextInt(1, sides + 1);
        return new DiceRoll(raw.Trim(), rolls, modifier, rolls.Sum() + modifier);
    }

    public (int RollOne, int? RollTwo, int ChosenRoll) RollD20(D20RollMode mode = D20RollMode.Normal)
    {
        var first = NextInt(1, 21);
        if (mode == D20RollMode.Normal) return (first, null, first);
        var second = NextInt(1, 21);
        var chosen = mode == D20RollMode.Advantage ? Math.Max(first, second) : Math.Min(first, second);
        return (first, second, chosen);
    }

    public int RollDamage(string damageExpression, bool critical = false)
    {
        if (string.IsNullOrWhiteSpace(damageExpression)) return 0;
        var expression = critical ? DoubleDamageDice(damageExpression) : damageExpression;
        return Math.Max(0, Roll(expression).Total);
    }

    public AttackResult Attack(
        int attackModifier,
        int armorClass,
        string damageExpression,
        D20RollMode mode = D20RollMode.Normal,
        bool criticalOnHit = false,
        int circumstanceBonus = 0)
    {
        var rolls = RollD20(mode);
        var d20 = rolls.ChosenRoll;
        var total = d20 + attackModifier + circumstanceBonus;
        var naturalCritical = d20 == 20;
        var hit = naturalCritical || (d20 != 1 && total >= armorClass);
        var critical = hit && (naturalCritical || criticalOnHit);
        var damage = 0;

        if (hit)
        {
            var expression = critical ? DoubleDamageDice(damageExpression) : damageExpression;
            damage = Math.Max(0, Roll(expression).Total);
        }

        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var bonusText = circumstanceBonus == 0 ? "" : $" (+{circumstanceBonus} effect bonus)";
        var summary = hit
            ? $"Attack{modeText} {total} vs AC {armorClass}: hit for {damage} damage{(critical ? " (critical)" : "")}{bonusText}."
            : $"Attack{modeText} {total} vs AC {armorClass}: miss{bonusText}.";
        return new AttackResult(d20, attackModifier + circumstanceBonus, total, hit, critical, damage, summary);
    }

    private int NextInt(int minimumInclusive, int maximumExclusive)
    {
        var value = _nextInt(minimumInclusive, maximumExclusive);
        if (value < minimumInclusive || value >= maximumExclusive)
            throw new InvalidOperationException($"The configured dice source returned {value}, outside the requested range [{minimumInclusive}, {maximumExclusive}).");
        return value;
    }

    private static string DoubleDamageDice(string expression)
    {
        var raw = expression ?? "";
        if (FixedExpressionRegex().IsMatch(raw)) return raw.Trim();
        var match = DiceExpressionRegex().Match(raw);
        if (!match.Success) throw new ArgumentException($"Invalid damage expression: {expression}", nameof(expression));
        var count = string.IsNullOrWhiteSpace(match.Groups["count"].Value) ? 1 : int.Parse(match.Groups["count"].Value);
        var sides = int.Parse(match.Groups["sides"].Value);
        var modifier = match.Groups["modifier"].Success ? match.Groups["modifier"].Value : "";
        return $"{count * 2}d{sides}{modifier}";
    }
}
