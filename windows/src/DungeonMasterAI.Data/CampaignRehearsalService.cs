using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

public enum RehearsalSeverity
{
    Info,
    Warning,
    Error
}

public sealed record CampaignRehearsalFinding(RehearsalSeverity Severity, string Scenario, string EntityKey, string Message);
public sealed record CampaignRehearsalReport(DateTimeOffset RanAt, IReadOnlyList<CampaignRehearsalFinding> Findings)
{
    public int Errors => Findings.Count(x => x.Severity == RehearsalSeverity.Error);
    public int Warnings => Findings.Count(x => x.Severity == RehearsalSeverity.Warning);
    public int Info => Findings.Count(x => x.Severity == RehearsalSeverity.Info);
    public bool Passed => Errors == 0;
}

/// <summary>
/// Deterministic pre-play rehearsal. This deliberately does not ask the LLM to
/// decide whether a campaign is valid. It probes the compiled graph and state
/// for common failure modes that would strand or leak information to players.
/// </summary>
public sealed class CampaignRehearsalService
{
    public CampaignRehearsalReport Run(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var findings = new List<CampaignRehearsalFinding>();
        CheckDuplicateKeys(campaign, findings);
        CheckTravelGraph(campaign, findings);
        CheckMerchants(campaign, findings);
        CheckEncounters(campaign, findings);
        CheckSecrets(campaign, findings);
        CheckTimeline(campaign, findings);
        CheckSupplements(campaign, findings);
        CheckTacticalMaps(campaign, findings);

        if (findings.Count == 0)
            findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Info, "baseline", campaign.Name, "No deterministic rehearsal findings were detected."));

        return new CampaignRehearsalReport(
            DateTimeOffset.UtcNow,
            findings.OrderByDescending(x => x.Severity).ThenBy(x => x.Scenario, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.EntityKey, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void CheckDuplicateKeys(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        var all = new List<(string Type, string Key)>();
        all.AddRange(campaign.Locations.Select(x => ("location", x.Key)));
        all.AddRange(campaign.Characters.Select(x => ("character", x.Key)));
        all.AddRange(campaign.Items.Select(x => ("item", x.Key)));
        all.AddRange(campaign.Merchants.Select(x => ("merchant", x.Key)));
        all.AddRange(campaign.Quests.Select(x => ("quest", x.Key)));
        all.AddRange(campaign.Factions.Select(x => ("faction", x.Key)));
        all.AddRange(campaign.Secrets.Select(x => ("secret", x.Key)));
        all.AddRange(campaign.Encounters.Select(x => ("encounter", x.Key)));
        all.AddRange(campaign.TacticalMaps.Select(x => ("tactical_map", x.Key)));
        foreach (var duplicate in all.Where(x => !string.IsNullOrWhiteSpace(x.Key)).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var types = string.Join(", ", duplicate.Select(x => x.Type).Distinct(StringComparer.OrdinalIgnoreCase));
            findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "identity", duplicate.Key, $"Stable key is reused across compiled entities ({types}). Tool and relationship resolution may become ambiguous."));
        }
    }

    private static void CheckTravelGraph(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        if (campaign.PartyLocationId is null) return;
        var publicLocations = campaign.Locations.Where(x => !x.DmOnly).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (!publicLocations.ContainsKey(campaign.PartyLocationId)) return;

        var adjacency = publicLocations.Keys.ToDictionary(x => x, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var connection in campaign.Connections.Where(x => !x.Hidden))
        {
            if (!adjacency.ContainsKey(connection.FromLocationId) || !adjacency.ContainsKey(connection.ToLocationId)) continue;
            adjacency[connection.FromLocationId].Add(connection.ToLocationId);
            adjacency[connection.ToLocationId].Add(connection.FromLocationId);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { campaign.PartyLocationId };
        var queue = new Queue<string>();
        queue.Enqueue(campaign.PartyLocationId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in adjacency[current]) if (visited.Add(next)) queue.Enqueue(next);
        }

        foreach (var location in campaign.Locations.Where(x => x.Discovered && !x.DmOnly && !visited.Contains(x.Id)))
            findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "exploration", location.Key, $"Player-visible location '{location.Name}' is unreachable from the current party location through known non-hidden travel links."));

        foreach (var location in campaign.Locations.Where(x => !x.DmOnly && x.Id != campaign.PartyLocationId && adjacency.TryGetValue(x.Id, out var links) && links.Count == 0))
            findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "exploration", location.Key, $"Location '{location.Name}' has no non-hidden travel connection. Players may have no deterministic route to reach it."));
    }

    private static void CheckMerchants(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        foreach (var merchant in campaign.Merchants)
        {
            if (merchant.Stock.Count == 0)
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "shopping", merchant.Key, $"Merchant '{merchant.Name}' cannot currently support a purchase because compiled stock is empty."));
            foreach (var stock in merchant.Stock)
            {
                var item = campaign.Items.FirstOrDefault(x => x.Id == stock.ItemId);
                if (item is null) continue;
                var price = stock.PriceGp ?? item.PriceGp;
                if (price <= 0)
                    findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "shopping", merchant.Key, $"Stock item '{item.Name}' has no positive purchase price. Buying it would not represent a meaningful economy transaction."));
            }
        }
    }

    private static void CheckEncounters(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        foreach (var encounter in campaign.Encounters.Where(x => !x.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)))
        {
            if (encounter.Combatants.Count == 0)
            {
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "combat", encounter.Key, $"Encounter '{encounter.Name}' has no participants and cannot be activated meaningfully."));
                continue;
            }
            foreach (var combatant in encounter.Combatants)
            {
                var character = campaign.Characters.FirstOrDefault(x => x.Id == combatant.CharacterId);
                if (character is null) continue;
                if (character.CharacterType.Equals("monster", StringComparison.OrdinalIgnoreCase) && character.Attacks.Count == 0)
                    findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Info, "combat", encounter.Key, $"{character.Name} has no compiled attack profile and will fall back to Unarmed Strike until an explicit attack is defined."));
            }
        }
    }

    private static void CheckSecrets(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        var publicText = new List<(string Key, string Text)>();
        publicText.AddRange(campaign.Locations.Where(x => !x.DmOnly).Select(x => (x.Key, x.Description)));
        publicText.AddRange(campaign.Characters.Select(x => (x.Key, x.PublicKnowledge)));
        publicText.AddRange(campaign.Factions.Select(x => (x.Key, x.PublicKnowledge)));
        publicText.AddRange(campaign.Quests.Where(x => !x.DmOnly).Select(x => (x.Key, x.Summary)));
        publicText.AddRange(campaign.Supplements.Where(x => !x.DmOnly).Select(x => (x.TargetKey, x.Content)));

        foreach (var secret in campaign.Secrets.Where(x => !x.Revealed && x.Truth.Trim().Length >= 24))
        {
            var truth = Normalize(secret.Truth);
            if (truth.Length < 24) continue;
            foreach (var surface in publicText)
            {
                if (Normalize(surface.Text).Contains(truth, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "secret-leak", secret.Key, $"Unrevealed secret '{secret.Title}' appears verbatim in player-visible content attached to '{surface.Key}'."));
            }
        }
    }

    private static void CheckTimeline(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        var currentMinute = (long)(campaign.Day - 1) * 1440 + campaign.MinuteOfDay;
        foreach (var evt in campaign.Timeline.Where(x => !x.Resolved && x.TriggerType.Equals("time", StringComparison.OrdinalIgnoreCase)))
        {
            var eventMinute = (long)(evt.CampaignDay - 1) * 1440 + evt.MinuteOfDay;
            if (eventMinute < currentMinute)
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "timeline", evt.Key, $"Unresolved time event '{evt.Name}' is scheduled in the campaign past. It may have been imported after its trigger point."));
        }
    }

    private static void CheckSupplements(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in campaign.Locations.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Characters.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Items.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Merchants.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Quests.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Factions.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Secrets.Select(x => x.Key)) Add(keys, key);
        foreach (var key in campaign.Encounters.Select(x => x.Key)) Add(keys, key);
        foreach (var supplement in campaign.Supplements.Where(x => !keys.Contains(x.TargetKey)))
            findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "generated-detail", supplement.TargetKey, $"AI-expanded supplement '{supplement.Category}' is attached to a missing target key."));
    }

    /// <summary>
    /// Probes tactical maps for conditions that would strand combat or leak DM-only geometry.
    /// Reachability is deliberately generous: every door is treated as openable and diagonal steps
    /// are permitted whenever either adjacent orthogonal cell is passable, so a reported failure
    /// means starting positions are separated by genuinely impassable geometry.
    /// </summary>
    private static void CheckTacticalMaps(CampaignState campaign, ICollection<CampaignRehearsalFinding> findings)
    {
        if (campaign.TacticalMaps.Count == 0) return;

        var boundMapIds = campaign.EncounterMapBindings.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var map in campaign.TacticalMaps.Where(m => !boundMapIds.Contains(m.Id)))
        {
            findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Info, "tactical-map", MapKey(map),
                $"Tactical map '{map.Name}' is not bound to any encounter and will never be shown during play."));
        }

        foreach (var map in campaign.TacticalMaps)
        {
            var mapKey = MapKey(map);
            foreach (var zone in map.Zones.Where(z => z.DmOnly))
            {
                if (map.Visibility.RevealAll || map.Visibility.RevealedCells.Any(c => c.X >= zone.X && c.X < zone.X + zone.WidthSquares && c.Y >= zone.Y && c.Y < zone.Y + zone.HeightSquares))
                    findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "secret-leak", mapKey,
                        $"DM-only {zone.ZoneType} zone '{zone.Name}' on '{map.Name}' sits in revealed territory and would be exposed to players."));
            }
        }

        foreach (var binding in campaign.EncounterMapBindings)
        {
            var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(binding.Key, StringComparison.OrdinalIgnoreCase));
            var map = campaign.TacticalMaps.FirstOrDefault(m => m.Id.Equals(binding.Value, StringComparison.OrdinalIgnoreCase));
            if (encounter is null || map is null) continue; // readiness validation reports unresolved bindings
            if (map.WidthSquares < 1 || map.HeightSquares < 1) continue;
            if (encounter.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)) continue;

            var mapKey = MapKey(map);
            var blocked = BuildBlockedCells(map);

            var origins = new List<(string Label, TacticalMapCell Cell)>();
            foreach (var combatant in encounter.Combatants.Where(c => c.Positioned))
            {
                var name = campaign.Characters.FirstOrDefault(x => x.Id == combatant.CharacterId)?.Name ?? combatant.CharacterId;
                origins.Add((name, new TacticalMapCell(combatant.GridX, combatant.GridY)));
            }
            if (origins.Count == 0)
                origins.AddRange(map.SpawnPoints.Select(s => (s.Name, new TacticalMapCell(s.X, s.Y))));

            origins = origins
                .Where(o => o.Cell.X >= 0 && o.Cell.Y >= 0 && o.Cell.X < map.WidthSquares && o.Cell.Y < map.HeightSquares)
                .ToList();

            if (origins.Count == 0)
            {
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "tactical-combat", encounter.Key,
                    $"Encounter '{encounter.Name}' is bound to map '{map.Name}' but neither positioned combatants nor spawn points define where anyone starts."));
                continue;
            }

            foreach (var duplicate in origins.GroupBy(o => o.Cell).Where(g => g.Count() > 1))
            {
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "tactical-combat", encounter.Key,
                    $"Encounter '{encounter.Name}' starts {duplicate.Count()} combatants on the same square ({duplicate.Key.X},{duplicate.Key.Y}) of '{map.Name}': {string.Join(", ", duplicate.Select(x => x.Label))}."));
            }

            foreach (var origin in origins.Where(o => blocked.Contains(o.Cell)))
            {
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "tactical-combat", encounter.Key,
                    $"'{origin.Label}' starts at ({origin.Cell.X},{origin.Cell.Y}) on '{map.Name}', a square blocked by terrain or props. That combatant would be unable to move."));
            }

            var start = origins.FirstOrDefault(o => !blocked.Contains(o.Cell));
            if (start.Label is null) continue;

            var reachable = FloodFill(map, blocked, start.Cell);
            foreach (var stranded in origins.Where(o => !blocked.Contains(o.Cell) && !reachable.Contains(o.Cell)))
            {
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Error, "tactical-combat", encounter.Key,
                    $"On map '{map.Name}', '{stranded.Label}' at ({stranded.Cell.X},{stranded.Cell.Y}) cannot reach '{start.Label}' at ({start.Cell.X},{start.Cell.Y}) by any route, even with every door opened. Melee combat could never be resolved."));
            }

            var openArea = map.WidthSquares * map.HeightSquares - blocked.Count;
            if (openArea > 0 && reachable.Count * 2 < openArea)
                findings.Add(new CampaignRehearsalFinding(RehearsalSeverity.Warning, "tactical-map", mapKey,
                    $"Only {reachable.Count} of {openArea} passable squares on '{map.Name}' are reachable from the encounter start. Most of the map would be unusable during '{encounter.Name}'."));
        }
    }

    private static string MapKey(TacticalMap map) => string.IsNullOrWhiteSpace(map.Key) ? map.Id : map.Key;

    private static HashSet<TacticalMapCell> BuildBlockedCells(TacticalMap map)
    {
        var blocked = new HashSet<TacticalMapCell>();
        void Fill(int x, int y, int w, int h)
        {
            for (var cx = Math.Max(0, x); cx < Math.Min(map.WidthSquares, x + Math.Max(1, w)); cx++)
                for (var cy = Math.Max(0, y); cy < Math.Min(map.HeightSquares, y + Math.Max(1, h)); cy++)
                    blocked.Add(new TacticalMapCell(cx, cy));
        }
        foreach (var terrain in map.Terrain.Where(t => t.BlocksMovement))
            Fill(terrain.X, terrain.Y, terrain.WidthSquares, terrain.HeightSquares);
        foreach (var prop in map.Props.Where(p => p.BlocksMovement))
            Fill(prop.X, prop.Y, prop.WidthSquares, prop.HeightSquares);
        return blocked;
    }

    /// <summary>
    /// Edges blocked by walls, keyed as an unordered cell pair. Doors are never treated as blocking
    /// here: rehearsal asks whether a route exists at all, and a closed or locked door is a route a
    /// party can open or force.
    /// </summary>
    private static HashSet<(TacticalMapCell, TacticalMapCell)> BuildBlockedEdges(TacticalMap map)
    {
        var edges = new HashSet<(TacticalMapCell, TacticalMapCell)>();
        var doorCells = map.Doors.Select(d => (d.X, d.Y, Vertical: d.Orientation.Equals("vertical", StringComparison.OrdinalIgnoreCase))).ToHashSet();

        foreach (var wall in map.Walls.Where(w => w.BlocksMovement))
        {
            if (wall.FromX == wall.ToX && wall.FromY != wall.ToY)
            {
                var x = wall.FromX;
                for (var y = Math.Min(wall.FromY, wall.ToY); y < Math.Max(wall.FromY, wall.ToY); y++)
                {
                    if (doorCells.Contains((x, y, true))) continue;
                    AddEdge(edges, new TacticalMapCell(x - 1, y), new TacticalMapCell(x, y));
                }
            }
            else if (wall.FromY == wall.ToY && wall.FromX != wall.ToX)
            {
                var y = wall.FromY;
                for (var x = Math.Min(wall.FromX, wall.ToX); x < Math.Max(wall.FromX, wall.ToX); x++)
                {
                    if (doorCells.Contains((x, y, false))) continue;
                    AddEdge(edges, new TacticalMapCell(x, y - 1), new TacticalMapCell(x, y));
                }
            }
        }
        return edges;
    }

    private static void AddEdge(ISet<(TacticalMapCell, TacticalMapCell)> edges, TacticalMapCell a, TacticalMapCell b)
    {
        edges.Add(Order(a, b));
    }

    private static (TacticalMapCell, TacticalMapCell) Order(TacticalMapCell a, TacticalMapCell b) =>
        (a.X, a.Y).CompareTo((b.X, b.Y)) <= 0 ? (a, b) : (b, a);

    private static HashSet<TacticalMapCell> FloodFill(TacticalMap map, HashSet<TacticalMapCell> blocked, TacticalMapCell start)
    {
        var edges = BuildBlockedEdges(map);
        var visited = new HashSet<TacticalMapCell> { start };
        var queue = new Queue<TacticalMapCell>();
        queue.Enqueue(start);

        bool Passable(TacticalMapCell cell) =>
            cell.X >= 0 && cell.Y >= 0 && cell.X < map.WidthSquares && cell.Y < map.HeightSquares && !blocked.Contains(cell);

        bool Step(TacticalMapCell from, TacticalMapCell to) =>
            Passable(to) && !edges.Contains(Order(from, to));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var next = new TacticalMapCell(current.X + dx, current.Y + dy);
                    if (!Passable(next)) continue;

                    if (dx == 0 || dy == 0)
                    {
                        if (!Step(current, next)) continue;
                    }
                    else
                    {
                        // Diagonal: allow it when either orthogonal elbow is itself traversable.
                        var elbowA = new TacticalMapCell(current.X + dx, current.Y);
                        var elbowB = new TacticalMapCell(current.X, current.Y + dy);
                        var viaA = Step(current, elbowA) && Step(elbowA, next);
                        var viaB = Step(current, elbowB) && Step(elbowB, next);
                        if (!viaA && !viaB) continue;
                    }

                    if (visited.Add(next)) queue.Enqueue(next);
                }
            }
        }
        return visited;
    }

    private static string Normalize(string? value) => string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static void Add(ISet<string> keys, string? key) { if (!string.IsNullOrWhiteSpace(key)) keys.Add(key); }
}
