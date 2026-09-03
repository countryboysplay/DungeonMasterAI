namespace DungeonMasterAI.Domain;

/// <summary>
/// The one canonical vocabulary for "which side is this?" — shared by live combatants
/// (<see cref="CombatantState.Side"/>) and by authored map spawn points
/// (<see cref="TacticalMapSpawnPoint.Side"/>).
/// <para>
/// Before this type existed there were three disagreeing vocabularies for the same concept:
/// the AI map generator required <c>player</c>, the engine wrote and accepted only
/// <c>party</c>/<c>opposition</c>/<c>neutral</c>, and the campaign readiness validator admitted
/// only <c>party</c>/<c>enemy</c>/<c>ally</c>/<c>neutral</c>. The net effect was that every
/// AI-generated map failed campaign readiness on the very spawn value the generator was required
/// to emit, and no spawn point could ever be used to place a combatant.
/// </para>
/// <para>
/// The canonical set is the engine's, because the engine is what adjudicates: a spawn point
/// exists to place a combatant, and a combatant has exactly one of these three sides. Everything
/// else — the generator's <c>player</c>, the validator's <c>enemy</c>/<c>ally</c>, whatever the
/// local model emits — is a synonym resolved by <see cref="TryNormalize"/>. New comparisons must
/// route through this type rather than adding another inline string literal.
/// </para>
/// </summary>
public static class CombatSide
{
    /// <summary>The player characters and anyone fighting alongside them.</summary>
    public const string Party = "party";

    /// <summary>Creatures fighting against the party.</summary>
    public const string Opposition = "opposition";

    /// <summary>Creatures on no side. Excluded from side-based hostility checks.</summary>
    public const string Neutral = "neutral";

    /// <summary>All canonical side values, excluding synonyms.</summary>
    public static IReadOnlyList<string> All { get; } = [Party, Opposition, Neutral];

    /// <summary>
    /// Synonyms accepted from persisted state, imported campaigns, and local-model output.
    /// <para>
    /// <c>ally</c> collapses onto <see cref="Party"/> deliberately: an allied NPC is mechanically
    /// on the party's side for initiative, targeting, and stealth, and the engine has no fourth
    /// side to put it on. The distinction was presentational, and it only ever existed in the
    /// readiness validator's private list.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["party"] = Party,
        ["player"] = Party,
        ["players"] = Party,
        ["pc"] = Party,
        ["pcs"] = Party,
        ["ally"] = Party,
        ["allies"] = Party,
        ["allied"] = Party,
        ["friendly"] = Party,
        ["hero"] = Party,
        ["heroes"] = Party,

        ["opposition"] = Opposition,
        ["enemy"] = Opposition,
        ["enemies"] = Opposition,
        ["hostile"] = Opposition,
        ["hostiles"] = Opposition,
        ["foe"] = Opposition,
        ["foes"] = Opposition,
        ["monster"] = Opposition,
        ["monsters"] = Opposition,

        ["neutral"] = Neutral,
        ["neutrals"] = Neutral,
        ["bystander"] = Neutral,
        ["bystanders"] = Neutral
    };

    /// <summary>
    /// Resolves a stored or model-emitted value onto its canonical side, or returns
    /// <c>null</c> when the value is blank or means nothing to this build. Callers that must
    /// produce a side use <see cref="Normalize"/>; callers that must reject bad data — the
    /// readiness validator, the generator's acceptance gate — test for null here so an
    /// unrecognized side is reported rather than silently retitled.
    /// </summary>
    public static string? TryNormalize(string? side)
    {
        if (string.IsNullOrWhiteSpace(side)) return null;
        return Synonyms.TryGetValue(side.Trim(), out var canonical) ? canonical : null;
    }

    /// <summary>
    /// Resolves onto a canonical side, falling back to <paramref name="fallback"/> when the value
    /// is blank or unrecognized.
    /// </summary>
    public static string Normalize(string? side, string fallback = Opposition)
        => TryNormalize(side) ?? fallback;

    /// <summary>True when the value resolves onto a canonical side.</summary>
    public static bool IsRecognized(string? side) => TryNormalize(side) is not null;

    /// <summary>True when the value resolves onto <see cref="Party"/>.</summary>
    public static bool IsParty(string? side) => TryNormalize(side) == Party;

    /// <summary>True when the value resolves onto <see cref="Opposition"/>.</summary>
    public static bool IsOpposition(string? side) => TryNormalize(side) == Opposition;

    /// <summary>True when the value resolves onto <see cref="Neutral"/>.</summary>
    public static bool IsNeutral(string? side) => TryNormalize(side) == Neutral;

    /// <summary>
    /// The side a character defaults to when none was supplied. Player characters join the party;
    /// everything else opposes it until a caller says otherwise.
    /// </summary>
    public static string DefaultFor(CharacterSheet character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? Party : Opposition;
    }
}
