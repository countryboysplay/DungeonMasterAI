namespace DungeonMasterAI.Domain;

/// <summary>
/// Restores case-insensitive lookup on dictionaries that declare
/// <see cref="StringComparer.OrdinalIgnoreCase"/> in their property initializer.
/// <para>
/// Every such property in <see cref="CampaignState"/> has a public setter, so
/// <c>System.Text.Json</c> ignores the declared initializer entirely: it constructs a fresh
/// dictionary using the <em>default</em> ordinal comparer and assigns it. The case-insensitive
/// contract therefore survives only until the first save/load round trip, after which lookups
/// such as <c>character.Abilities["strength"]</c> silently miss a stored <c>"Strength"</c> key
/// and read as an absent ability rather than the persisted score.
/// </para>
/// <para>
/// Because this failure mode is a property of the serializer rather than of any single model,
/// normalization is centralized here and applied from
/// <c>AppDataStore.Normalize</c> on every load and before every save.
/// </para>
/// </summary>
public static class CaseInsensitiveMap
{
    /// <summary>
    /// Returns <paramref name="source"/> unchanged when it already compares case-insensitively,
    /// otherwise returns an equivalent dictionary that does. A null input yields a new empty map,
    /// which lets callers use this to satisfy a non-nullable property in one assignment.
    /// </summary>
    public static Dictionary<string, TValue> Normalize<TValue>(Dictionary<string, TValue>? source)
    {
        if (source is not null && ReferenceEquals(source.Comparer, StringComparer.OrdinalIgnoreCase))
            return source;

        var rebuilt = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return rebuilt;

        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;

            // Indexer assignment rather than Add. A case-sensitive source dictionary can legally
            // hold keys that collide once compared case-insensitively ("Strength" and "strength"),
            // and a load must not throw on data that was valid when it was written. Last writer
            // wins, matching the behavior a case-insensitive dictionary would have had on write.
            rebuilt[pair.Key.Trim()] = pair.Value;
        }

        return rebuilt;
    }
}
