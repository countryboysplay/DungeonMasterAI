using System.Net.Http.Json;
using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.AI;

public sealed record CampaignExpansionProgress(string Message);
public sealed record CampaignAiExpansionResult(string PatchJson, int SuggestedObjectCount, IReadOnlyList<string> Warnings);

public sealed class CampaignAiExpansionService(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<CampaignAiExpansionResult> ExpandAsync(
        CampaignState campaign,
        IReadOnlyList<string> readinessIssues,
        AppSettings settings,
        IProgress<CampaignExpansionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        progress?.Report(new CampaignExpansionProgress("Analyzing compiled campaign gaps..."));

        var endpoint = new Uri(new Uri(NormalizeBase(settings.LlamaServerUrl)), "v1/chat/completions");
        var context = BuildExpansionContext(campaign, readinessIssues);
        var payload = new
        {
            model = settings.ModelName,
            temperature = 0.35,
            max_tokens = 4500,
            messages = new object[]
            {
                new { role = "system", content = ExpansionPrompt() },
                new
                {
                    role = "user",
                    content = "CURRENT COMPILED CAMPAIGN:\n" + JsonSerializer.Serialize(context, _json)
                        + "\n\nReturn a single JSON patch containing only NEW ai_expanded material that improves playability."
                }
            }
        };

        progress?.Report(new CampaignExpansionProgress("Local AI is designing missing playable details..."));
        using var response = await _http.PostAsJsonAsync(endpoint, payload, _json, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("The local expansion model returned an empty response.");

        var patchJson = ExtractJsonObject(content);
        using var patchDocument = JsonDocument.Parse(patchJson);
        if (patchDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The local expansion model did not return a JSON object.");

        var count = CountSuggestedObjects(patchDocument.RootElement);
        progress?.Report(new CampaignExpansionProgress($"Expansion plan created with {count} structured addition(s)."));
        return new CampaignAiExpansionResult(patchJson, count, []);
    }

    private static object BuildExpansionContext(CampaignState campaign, IReadOnlyList<string> readinessIssues)
    {
        var locationKeyById = campaign.Locations.ToDictionary(x => x.Id, x => x.Key, StringComparer.OrdinalIgnoreCase);
        var characterKeyById = campaign.Characters.ToDictionary(x => x.Id, x => x.Key, StringComparer.OrdinalIgnoreCase);
        var itemKeyById = campaign.Items.ToDictionary(x => x.Id, x => x.Key, StringComparer.OrdinalIgnoreCase);

        string? LocationKey(string? id) => id is not null && locationKeyById.TryGetValue(id, out var key) ? key : null;
        string? CharacterKey(string? id) => id is not null && characterKeyById.TryGetValue(id, out var key) ? key : null;
        string? ItemKey(string? id) => id is not null && itemKeyById.TryGetValue(id, out var key) ? key : null;

        return new
        {
            campaign = new { campaign.Name, campaign.System, campaign.Summary, campaign.Tone, campaign.PartyName },
            readiness_issues = readinessIssues.Take(60).ToArray(),
            locations = campaign.Locations.Take(150).Select(x => new
            {
                x.Key, x.Name, x.Type, x.Description,
                parent_key = LocationKey(x.ParentId),
                x.DmOnly, x.Discovered, x.SourceKind
            }).ToArray(),
            connections = campaign.Connections.Take(250).Select(x => new
            {
                from_key = LocationKey(x.FromLocationId),
                to_key = LocationKey(x.ToLocationId),
                x.Label, x.TravelMinutes, x.Hidden, x.SourceKind
            }).ToArray(),
            characters = campaign.Characters.Take(250).Select(x => new
            {
                x.Key, x.Name, x.CharacterType, x.Level, x.ArmorClass, x.MaxHp,
                location_key = LocationKey(x.LocationId),
                x.PublicKnowledge, x.SecretKnowledge, x.SourceKind,
                attacks = x.Attacks.Select(a => new { a.Name, a.AttackBonus, a.DamageExpression, a.DamageType })
            }).ToArray(),
            items = campaign.Items.Take(250).Select(x => new { x.Key, x.Name, x.Category, x.Description, x.PriceGp, x.SourceKind }).ToArray(),
            merchants = campaign.Merchants.Take(100).Select(x => new
            {
                x.Key, x.Name,
                location_key = LocationKey(x.LocationId),
                npc_key = CharacterKey(x.NpcId),
                x.Gold, x.SourceKind,
                stock = x.Stock.Select(s => new { item_key = ItemKey(s.ItemId), s.Quantity, s.PriceGp, s.SourceKind })
            }).ToArray(),
            quests = campaign.Quests.Take(120).Select(x => new { x.Key, x.Name, x.Status, x.Summary, x.DmNotes, x.DmOnly, x.Objectives, x.SourceKind }).ToArray(),
            factions = campaign.Factions.Take(100).Select(x => new { x.Key, x.Name, x.Summary, x.PublicKnowledge, x.SecretKnowledge, x.SourceKind }).ToArray(),
            relationships = campaign.Relationships.Take(200).Select(x => new { x.SourceKey, x.TargetKey, x.Relation, x.Strength, x.Public, x.SourceKind }).ToArray(),
            secrets = campaign.Secrets.Take(120).Select(x => new { x.Key, x.Title, x.Truth, x.KnownByKeys, x.RevealConditions, x.Revealed, x.SourceKind }).ToArray(),
            timeline = campaign.Timeline.Take(120).Select(x => new { x.Key, x.Name, x.TriggerType, x.CampaignDay, x.MinuteOfDay, x.EffectQuestKey, x.Consequence, x.DmNotes, x.Resolved, x.SourceKind }).ToArray(),
            encounters = campaign.Encounters.Take(100).Select(x => new
            {
                x.Key, x.Name, x.Summary, x.Status, x.DmOnly, x.SourceKind,
                location_key = LocationKey(x.LocationId),
                members = x.Combatants.Select(c => CharacterKey(c.CharacterId)).Where(k => k is not null).ToArray()
            }).ToArray(),
            existing_ai_supplements = campaign.Supplements.Take(200).Select(x => new { x.TargetKey, x.Category, x.Content, x.DmOnly }).ToArray()
        };
    }

    private static int CountSuggestedObjects(JsonElement root)
    {
        var total = 0;
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array) total += property.Value.GetArrayLength();
        return total;
    }

    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline) trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }
        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first < 0 || last <= first) throw new InvalidDataException("No JSON object was found in the expansion response.");
        return trimmed[first..(last + 1)];
    }

    private static string NormalizeBase(string url) => url.TrimEnd('/') + "/";

    private static string ExpansionPrompt() => """
You are the playability-expansion stage of a local tabletop campaign compiler.
The campaign has already been canon-extracted. Existing source_canon facts are immutable. Never rewrite, contradict, replace, reinterpret, or silently complete a source_canon fact.
Your job is to add only missing material needed to make the compiled world practical to run at the table. Every generated object must use source_kind: ai_expanded.
Prefer conservative additions that fit the existing names, tone, geography, factions, quests, and secrets. Do not introduce a new major plot, villain, faction, deity, setting truth, or campaign objective unless necessary to repair an explicit playability gap.
Do not generate player characters.
Do not copy text from published rules or adventures. Use concise original descriptions.
If a gap cannot be safely filled without changing canon, create a supplement instead of modifying the canon entity.

Return one JSON object containing only arrays that have additions. Allowed arrays:
locations: new secondary locations only, fields key,name,type,description,parent_key,dm_only,x,y,source_kind
connections: new routes only, fields from_key,to_key,label,travel_minutes,hidden,source_kind
characters: new NPCs/background creatures only, fields key,name,character_type,level,armor_class,max_hp,current_hp,location_key,public_knowledge,secret_knowledge,abilities,attacks,source_kind
items: generated mundane/local items only when needed for merchants or treasure, fields key,name,category,description,price_gp,consumable,equippable,equipment_slot,source_kind
merchants: new merchants only when a location clearly needs one, fields key,name,location_key,npc_key,gold,source_kind
merchant_stock_additions: stock for existing or newly generated merchants, fields merchant_key,item_key,quantity,price_gp,source_kind
quests: new minor/supporting quests only when necessary, fields key,name,status,summary,dm_notes,dm_only,objectives,reward_gp,source_kind
factions: new minor factions only when clearly required, fields key,name,summary,public_knowledge,secret_knowledge,source_kind
relationships: new generated relationships, fields source_key,target_key,relation,strength,public,source_kind
secrets: new minor secrets/clues only when they support existing play, fields key,title,truth,known_by,reveal_conditions,revealed,source_kind
timeline: new supporting time events, fields key,name,trigger_type,campaign_day,minute_of_day,effect_quest_key,consequence,dm_notes,source_kind
encounters: new optional encounters only, fields key,name,summary,status,dm_only,location_key,members[{key,name,quantity,character_type,armor_class,max_hp,initiative_modifier,attacks}],source_kind
supplements: generated details that attach to immutable canon, fields target_key,category,content,dm_only,source_kind. Useful categories include quest_objective, quest_clue, location_detail, npc_detail, secret_reveal_condition, merchant_detail, encounter_tactic.

Rules:
- Never output an object using the key of an existing source_canon entity except merchant_stock_additions or supplements.
- Never change existing HP, AC, prices, quest status, secret truth, map visibility, or timeline facts.
- If an existing canon merchant has no stock, add stock through merchant_stock_additions.
- If an existing canon quest lacks objectives or clues, add them as supplements rather than altering the quest.
- If an existing canon secret lacks a reveal condition, add a secret_reveal_condition supplement rather than changing the secret.
- New combat statistics must be modest and internally coherent, but they are generated content and must remain clearly ai_expanded.
- Output JSON only, without markdown fences or commentary.
""";
}
