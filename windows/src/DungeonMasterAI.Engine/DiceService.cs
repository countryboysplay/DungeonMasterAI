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

    private const int MaxDiceCount = 100;

    public DiceRoll Roll(string expression) => RollCore(expression, MaxDiceCount);

    /// <summary>
    /// Reports whether an expression would roll. Callers validate campaign-authored and
    /// model-supplied strings with this before they mutate state a mid-resolution roll would strand.
    /// </summary>
    public static bool TryValidateExpression(string? expression)
    {
        var raw = expression ?? "";
        var fixedMatch = FixedExpressionRegex().Match(raw);
        if (fixedMatch.Success)
            return int.TryParse(fixedMatch.Groups["fixed"].Value, out var fixedValue) && fixedValue <= 1_000_000;

        var match = DiceExpressionRegex().Match(raw);
        if (!match.Success) return false;
        var countText = match.Groups["count"].Value;
        var count = 1;
        if (!string.IsNullOrWhiteSpace(countText) && !int.TryParse(countText, out count)) return false;
        if (!int.TryParse(match.Groups["sides"].Value, out var sides)) return false;
        if (match.Groups["modifier"].Success && !int.TryParse(match.Groups["modifier"].Value, out _)) return false;
        return count is >= 1 and <= MaxDiceCount && sides is >= 2 and <= 1000;
    }

    private DiceRoll RollCore(string expression, int maxDiceCount)
    {
        var raw = expression ?? "";
        var fixedMatch = FixedExpressionRegex().Match(raw);
        if (fixedMatch.Success)
        {
            // TryParse instead of Parse: digit strings longer than int.MaxValue would
            // otherwise escape as an uncontrolled OverflowException. Dice expressions
            // can originate from imported campaign files and local-model tool calls,
            // so parsing failures must surface as ordinary argument errors.
            if (!int.TryParse(fixedMatch.Groups["fixed"].Value, out var value) || value > 1_000_000)
                throw new ArgumentOutOfRangeException(nameof(expression), "Fixed roll values must not exceed 1,000,000.");
            return new DiceRoll(raw.Trim(), Array.Empty<int>(), value, value);
        }

        var match = DiceExpressionRegex().Match(raw);
        if (!match.Success)
            throw new ArgumentException($"Invalid dice expression: {expression}", nameof(expression));

        var countText = match.Groups["count"].Value;
        var count = 1;
        if (!string.IsNullOrWhiteSpace(countText) && !int.TryParse(countText, out count))
            throw new ArgumentOutOfRangeException(nameof(expression), $"Dice count must be between 1 and {maxDiceCount}.");
        if (!int.TryParse(match.Groups["sides"].Value, out var sides))
            throw new ArgumentOutOfRangeException(nameof(expression), "Die sides must be between 2 and 1000.");
        var modifier = 0;
        if (match.Groups["modifier"].Success && !int.TryParse(match.Groups["modifier"].Value, out modifier))
            throw new ArgumentOutOfRangeException(nameof(expression), "Dice modifiers must fit in a 32-bit integer.");

        if (count < 1 || count > maxDiceCount) throw new ArgumentOutOfRangeException(nameof(expression), $"Dice count must be between 1 and {maxDiceCount}.");
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
        return Math.Max(0, RollCore(expression, critical ? MaxDiceCount * 2 : MaxDiceCount).Total);
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
            damage = Math.Max(0, RollCore(expression, critical ? MaxDiceCount * 2 : MaxDiceCount).Total);
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
        var countText = match.Groups["count"].Value;
        var count = 1;
        if (!string.IsNullOrWhiteSpace(countText) && !int.TryParse(countText, out count))
            throw new ArgumentOutOfRangeException(nameof(expression), $"Dice count must be between 1 and {MaxDiceCount}.");
        if (count < 1 || count > MaxDiceCount)
            throw new ArgumentOutOfRangeException(nameof(expression), $"Dice count must be between 1 and {MaxDiceCount}.");
        if (!int.TryParse(match.Groups["sides"].Value, out var sides))
            throw new ArgumentOutOfRangeException(nameof(expression), "Die sides must be between 2 and 1000.");
        var modifier = match.Groups["modifier"].Success ? match.Groups["modifier"].Value : "";
        return $"{count * 2}d{sides}{modifier}";
    }
}
