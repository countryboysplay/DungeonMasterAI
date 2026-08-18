namespace DungeonMasterAI.Domain;

/// <summary>
/// Canonical <c>SourceKind</c> provenance values plus the predicates that interpret them.
/// <para>
/// Provenance decisions must route through this type. Before it existed, "is this record
/// AI-invented?" was expressed as an inline <c>SourceKind.Equals("ai_expanded", ...)</c>
/// comparison repeated across a dozen collections, so a newly introduced provenance value
/// could silently fall out of every generated-content filter while still looking correct.
/// </para>
/// </summary>
public static class CampaignProvenance
{
    /// <summary>Extracted from a user-supplied campaign source document. Protected from overwrite.</summary>
    public const string SourceCanon = "source_canon";

    /// <summary>AI-invented content added to close a readiness gap. Never treated as canon.</summary>
    public const string AiExpanded = "ai_expanded";

    /// <summary>Deterministically derived from canon during import rather than authored.</summary>
    public const string Inferred = "inferred";

    /// <summary>Created deterministically by the engine during play.</summary>
    public const string RuntimeGenerated = "runtime_generated";

    /// <summary>Authored by a test fixture.</summary>
    public const string TestFixture = "test_fixture";

    /// <summary>
    /// Legacy alias written by the r55-r57 tactical map generator before provenance was unified.
    /// It is not a distinct provenance class: a generated map is AI-invented content exactly like
    /// a generated NPC, so it normalizes onto <see cref="AiExpanded"/>. Retained as a constant so
    /// migrations and older serialized state can still be recognized.
    /// </summary>
    public const string LegacyAiGenerated = "ai_generated";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [LegacyAiGenerated] = AiExpanded
    };

    private static readonly HashSet<string> Recognized = new(StringComparer.OrdinalIgnoreCase)
    {
        SourceCanon,
        AiExpanded,
        Inferred,
        RuntimeGenerated,
        TestFixture
    };

    /// <summary>All canonical provenance values, excluding aliases.</summary>
    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        SourceCanon,
        AiExpanded,
        Inferred,
        RuntimeGenerated,
        TestFixture
    };

    /// <summary>
    /// Collapses aliases onto their canonical provenance value. A blank value resolves to
    /// <paramref name="fallback"/> so each model keeps its own documented default instead of
    /// being relabeled. An unrecognized non-blank value is preserved verbatim: unknown
    /// provenance must never be silently promoted to canon.
    /// </summary>
    public static string Normalize(string? sourceKind, string fallback = SourceCanon)
    {
        if (string.IsNullOrWhiteSpace(sourceKind)) return fallback;
        var trimmed = sourceKind.Trim();
        return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    /// <summary>True when the normalized value is one this build knows how to reason about.</summary>
    public static bool IsRecognized(string? sourceKind) => Recognized.Contains(Normalize(sourceKind));

    /// <summary>
    /// True when the record was invented by the local model and must stay visibly separate from
    /// source canon. This is the single predicate every generated-content filter should use.
    /// </summary>
    public static bool IsAiGenerated(string? sourceKind) =>
        string.Equals(Normalize(sourceKind), AiExpanded, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the record is protected source canon.</summary>
    public static bool IsSourceCanon(string? sourceKind) =>
        string.Equals(Normalize(sourceKind), SourceCanon, StringComparison.OrdinalIgnoreCase);
}
