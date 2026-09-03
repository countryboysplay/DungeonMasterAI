using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.App;

/// <summary>
/// Inverts a boolean so a view can disable input while a busy flag is set.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool flag || !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool flag || !flag;
}

/// <summary>
/// Resolves one ability score for a character sheet through the engine's own lookup.
///
/// Binding straight to <c>Abilities[strength]</c> was wrong twice over. The importer copies
/// ability keys through verbatim, and the shipped sample writes them abbreviated
/// (<c>str</c>, <c>dex</c>, ...), so the long-name key is simply absent; and the
/// <see cref="Dictionary{TKey,TValue}"/> indexer throws <see cref="KeyNotFoundException"/>
/// rather than returning null, which fails the binding outright — <c>TargetNullValue</c> cannot
/// rescue an errored binding, so all six tiles rendered blank.
///
/// <see cref="CharacterMechanics.AbilityScore"/> is the rule the engine itself applies: try the
/// canonical name, then the three-letter form, then fall back to 10. Calling it here keeps the
/// sheet the player reads showing the same score the dice will actually use.
/// </summary>
public sealed class AbilityScoreConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is CharacterSheet character && parameter is string ability)
        {
            try
            {
                return CharacterMechanics.AbilityScore(character, ability).ToString(culture);
            }
            catch (ArgumentException)
            {
                // NormalizeAbility rejects anything that is not one of the six abilities.
                return "—";
            }
        }

        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps the live <c>LocalAiStatus</c> string onto a status brush. The three brushes are supplied
/// from the theme dictionary so the palette stays defined in one place.
/// </summary>
public sealed class LocalAiStatusBrushConverter : IValueConverter
{
    public Brush? Online { get; set; }

    public Brush? Pending { get; set; }

    public Brush? Offline { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string ?? string.Empty;

        if (status.StartsWith("Online", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Inference ready", StringComparison.OrdinalIgnoreCase))
            return Online;

        if (status.StartsWith("Starting", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Runtime installed", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Not checked", StringComparison.OrdinalIgnoreCase))
            return Pending;

        return Offline;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
