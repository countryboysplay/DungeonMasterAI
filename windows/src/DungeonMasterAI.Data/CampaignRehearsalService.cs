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

    private static string Normalize(string? value) => string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static void Add(ISet<string> keys, string? key) { if (!string.IsNullOrWhiteSpace(key)) keys.Add(key); }
}
