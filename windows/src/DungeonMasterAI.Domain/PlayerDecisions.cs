namespace DungeonMasterAI.Domain;

public sealed partial class CampaignState
{
    public PendingPlayerDecision? PendingPlayerDecision { get; set; }
}

public sealed class PendingPlayerDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ActorCharacterId { get; set; } = "";
    public string? EncounterId { get; set; }
    public string? CombatantId { get; set; }
    public string DecisionType { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool Required { get; set; } = true;
    public List<PlayerDecisionOption> Options { get; set; } = [];
    public Dictionary<string, string> Context { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlayerDecisionOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Value { get; set; } = "";
    public string Emphasis { get; set; } = "normal";
}

public sealed record PlayerDecisionResolution(
    string DecisionId,
    string DecisionType,
    string ActorCharacterId,
    string OptionId,
    string OptionLabel,
    string Summary,
    PendingRollRequest? FollowUpRoll);
