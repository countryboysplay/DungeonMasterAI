using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

/// <summary>
/// Creates an isolated copy of campaign state so AI-driven turns can be resolved
/// transactionally. Tool calls mutate the copy and the caller commits it only
/// after the DM turn finishes successfully.
/// </summary>
public sealed class CampaignCloneService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public CampaignState Clone(CampaignState source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, _json);
        return JsonSerializer.Deserialize<CampaignState>(bytes, _json)
            ?? throw new InvalidDataException("Campaign state could not be cloned.");
    }
}
