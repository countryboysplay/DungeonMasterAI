namespace DungeonMasterAI.Domain;

/// <summary>
/// Versioning and shape normalization for persisted tactical map state.
/// <para>
/// Tactical maps carry their own <see cref="TacticalMap.SchemaVersion"/> because map geometry
/// evolves independently of the application state file. That per-record version is still owned
/// by the central migration pipeline in the data layer: this type is the one place that knows how
/// to bring a deserialized map up to <see cref="CurrentMapSchemaVersion"/>, so map upgrades are
/// covered by migration tests instead of happening lazily inside an editor interaction.
/// </para>
/// </summary>
public static class TacticalMapSchema
{
    /// <summary>
    /// Current tactical map record schema version.
    /// <list type="bullet">
    /// <item><description>1 — r53 tactical map schema.</description></item>
    /// <item><description>
    /// 2 — r62 spawn-side unification. Spawn point <c>side</c> values are stored using the
    /// canonical <see cref="CombatSide"/> vocabulary. Version 1 records may hold any of the three
    /// disagreeing vocabularies that shipped before it (<c>player</c>, <c>enemy</c>, <c>ally</c>,
    /// …) and are rewritten on load by <see cref="NormalizeMap"/>.
    /// </description></item>
    /// </list>
    /// </summary>
    public const int CurrentMapSchemaVersion = 2;

    /// <summary>
    /// Normalizes every tactical map on a campaign and repairs the encounter binding lookup.
    /// Non-destructive by design: bindings that point at a missing map or encounter are left in
    /// place for the rehearsal/readiness pass to report rather than being silently deleted here.
    /// </summary>
    /// <returns>The number of maps whose persisted shape had to be repaired.</returns>
    public static int NormalizeCampaign(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        campaign.TacticalMaps ??= [];

        var repaired = 0;
        foreach (var map in campaign.TacticalMaps)
        {
            if (NormalizeMap(map)) repaired++;
        }

        campaign.EncounterMapBindings = NormalizeBindings(campaign.EncounterMapBindings);
        return repaired;
    }

    /// <summary>
    /// Brings a single deserialized map record up to the current map schema version and
    /// guarantees non-null collections.
    /// </summary>
    /// <returns><c>true</c> when any stored value had to be changed.</returns>
    public static bool NormalizeMap(TacticalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var changed = false;

        if (map.SchemaVersion > CurrentMapSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Tactical map '{map.Name}' reports schema version {map.SchemaVersion}, which is newer than " +
                $"the supported version {CurrentMapSchemaVersion}. Refusing to reinterpret unknown map geometry.");
        }

        // Maps serialized before the map schema was versioned report 0.
        if (map.SchemaVersion < CurrentMapSchemaVersion)
        {
            map.SchemaVersion = CurrentMapSchemaVersion;
            changed = true;
        }

        // GenerationSeed records the seed that produced the authoritative geometry, while Seed
        // only selects art variants and may be rerolled freely. Records written before the two
        // were separated stored the geometry seed in Seed alone.
        if (map.GenerationSeed == 0 && map.Seed != 0)
        {
            map.GenerationSeed = map.Seed;
            changed = true;
        }

        var provenance = CampaignProvenance.Normalize(map.SourceKind, CampaignProvenance.SourceCanon);
        if (!string.Equals(provenance, map.SourceKind, StringComparison.Ordinal))
        {
            map.SourceKind = provenance;
            changed = true;
        }

        if (map.Rooms is null) { map.Rooms = []; changed = true; }
        if (map.Walls is null) { map.Walls = []; changed = true; }
        if (map.Doors is null) { map.Doors = []; changed = true; }
        if (map.Terrain is null) { map.Terrain = []; changed = true; }
        if (map.Props is null) { map.Props = []; changed = true; }
        if (map.Lights is null) { map.Lights = []; changed = true; }
        if (map.SpawnPoints is null) { map.SpawnPoints = []; changed = true; }
        if (map.Zones is null) { map.Zones = []; changed = true; }

        if (map.Visibility is null)
        {
            map.Visibility = new TacticalMapVisibility();
            changed = true;
        }
        else
        {
            if (map.Visibility.RevealedRoomIds is null) { map.Visibility.RevealedRoomIds = []; changed = true; }
            if (map.Visibility.RevealedCells is null) { map.Visibility.RevealedCells = []; changed = true; }
        }

        // v1 -> v2: spawn sides were written in whichever of the three pre-r62 vocabularies the
        // writer happened to use. Collapse them onto the canonical CombatSide set so every reader
        // — engine placement, readiness validation, the renderer — sees the same value. An
        // unrecognized side has no safe canonical meaning, so it is left verbatim for the
        // readiness validator to report rather than being guessed into a side that fights.
        foreach (var spawn in map.SpawnPoints)
        {
            var canonical = CombatSide.TryNormalize(spawn.Side);
            if (canonical is null || string.Equals(canonical, spawn.Side, StringComparison.Ordinal)) continue;
            spawn.Side = canonical;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Rebuilds the encounter binding dictionary with a case-insensitive comparer.
    /// <para>
    /// Encounter bindings are one of four dictionaries that lose their declared comparer during
    /// deserialization; see <see cref="CaseInsensitiveMap"/> for why. This wrapper is retained
    /// because encounter-to-map binding is a map-schema concern and callers reference it as such.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> NormalizeBindings(Dictionary<string, string>? bindings)
        => CaseInsensitiveMap.Normalize(bindings);
}
