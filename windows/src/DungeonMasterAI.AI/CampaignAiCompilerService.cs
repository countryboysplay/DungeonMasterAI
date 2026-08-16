using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.AI;

public sealed record CampaignCompileProgress(int CompletedChunks, int TotalChunks, string Message);
public sealed record CampaignAiCompileResult(string ManifestJson, int ChunkCount, IReadOnlyList<string> Warnings);

public sealed class CampaignAiCompilerService(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly string[] MergeSections =
    [
        "locations", "connections", "characters", "items", "spells", "merchants", "quests",
        "factions", "relationships", "secrets", "timeline", "encounters", "discoveries"
    ];

    public async Task<CampaignAiCompileResult> CompileAsync(
        string sourceName,
        string sourceText,
        AppSettings settings,
        IProgress<CampaignCompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) throw new ArgumentException("Campaign source text is empty.", nameof(sourceText));
        var chunks = ChunkSource(sourceText, 14_000, 900).Take(120).ToArray();
        if (chunks.Length == 0) throw new InvalidDataException("No campaign text chunks could be produced.");

        var warnings = new List<string>();
        var manifest = CreateManifest(sourceName);
        for (var index = 0; index < chunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CampaignCompileProgress(index, chunks.Length, $"Analyzing campaign source chunk {index + 1} of {chunks.Length}..."));
            try
            {
                var candidate = await ExtractChunkAsync(sourceName, chunks[index], index + 1, chunks.Length, settings, cancellationToken);
                MergeChunk(manifest, candidate);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or HttpRequestException or TaskCanceledException)
            {
                if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested) throw;
                warnings.Add($"Chunk {index + 1} could not be compiled: {ex.Message}");
            }
        }

        if (manifest["campaign"] is JsonObject campaign)
        {
            if (IsBlank(campaign["name"])) campaign["name"] = Path.GetFileNameWithoutExtension(sourceName);
            if (IsBlank(campaign["system"])) campaign["system"] = "D&D 5E compatible / SRD 5.2.1";
        }
        EnsureSourceKind(manifest);
        progress?.Report(new CampaignCompileProgress(chunks.Length, chunks.Length, "Campaign source extraction complete. Validating compiled world..."));
        return new CampaignAiCompileResult(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), chunks.Length, warnings);
    }

    private async Task<JsonObject> ExtractChunkAsync(
        string sourceName,
        string chunk,
        int chunkNumber,
        int totalChunks,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(NormalizeBase(settings.LlamaServerUrl)), "v1/chat/completions");
        var payload = new
        {
            model = settings.ModelName,
            temperature = 0.1,
            max_tokens = 3000,
            messages = new object[]
            {
                new { role = "system", content = ExtractionPrompt() },
                new
                {
                    role = "user",
                    content = $"SOURCE FILE: {sourceName}\nCHUNK: {chunkNumber}/{totalChunks}\n\nExtract only facts explicitly supported by this chunk. Return one JSON object and nothing else.\n\n{chunk}"
                }
            }
        };

        using var response = await _http.PostAsJsonAsync(endpoint, payload, _json, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("The local compiler returned an empty response.");
        var jsonText = ExtractJsonObject(content);
        var node = JsonNode.Parse(jsonText) as JsonObject;
        return node ?? throw new InvalidDataException("The local compiler did not return a JSON object.");
    }

    private static JsonObject CreateManifest(string sourceName)
    {
        var root = new JsonObject
        {
            ["manifest_version"] = "native-ai-0.1",
            ["campaign"] = new JsonObject
            {
                ["name"] = Path.GetFileNameWithoutExtension(sourceName),
                ["system"] = "D&D 5E compatible / SRD 5.2.1",
                ["summary"] = "",
                ["tone"] = ""
            },
            ["party"] = new JsonObject { ["name"] = "Adventuring Party" }
        };
        foreach (var section in MergeSections) root[section] = new JsonArray();
        return root;
    }

    private static void MergeChunk(JsonObject destination, JsonObject incoming)
    {
        if (incoming["campaign"] is JsonObject incomingCampaign && destination["campaign"] is JsonObject campaign)
            MergeObject(campaign, incomingCampaign);
        if (incoming["party"] is JsonObject incomingParty && destination["party"] is JsonObject party)
            MergeObject(party, incomingParty);

        foreach (var section in MergeSections)
        {
            if (incoming[section] is not JsonArray incomingArray) continue;
            var destinationArray = destination[section] as JsonArray ?? new JsonArray();
            destination[section] = destinationArray;
            MergeArray(section, destinationArray, incomingArray);
        }
    }

    private static void MergeArray(string section, JsonArray destination, JsonArray incoming)
    {
        foreach (var node in incoming)
        {
            if (node is not JsonObject candidate) continue;
            var clone = candidate.DeepClone().AsObject();
            if (!clone.ContainsKey("source_kind") && section is not "discoveries")
                clone["source_kind"] = "source_canon";

            var identity = Identity(section, clone);
            JsonObject? existing = null;
            if (!string.IsNullOrWhiteSpace(identity))
            {
                existing = destination
                    .OfType<JsonObject>()
                    .FirstOrDefault(item => string.Equals(Identity(section, item), identity, StringComparison.OrdinalIgnoreCase));
            }

            if (existing is null) destination.Add(clone);
            else MergeObject(existing, clone);
        }
    }

    private static void MergeObject(JsonObject destination, JsonObject incoming)
    {
        foreach (var pair in incoming)
        {
            if (pair.Value is null) continue;
            if (!destination.TryGetPropertyValue(pair.Key, out var current) || IsBlank(current))
            {
                destination[pair.Key] = pair.Value.DeepClone();
                continue;
            }
            if (current is JsonObject currentObject && pair.Value is JsonObject incomingObject)
            {
                MergeObject(currentObject, incomingObject);
                continue;
            }
            if (current is JsonArray currentArray && pair.Value is JsonArray incomingArray)
            {
                var seen = currentArray.Select(n => n?.ToJsonString() ?? "null").ToHashSet(StringComparer.Ordinal);
                foreach (var value in incomingArray)
                {
                    var serialized = value?.ToJsonString() ?? "null";
                    if (seen.Add(serialized)) currentArray.Add(value?.DeepClone());
                }
            }
        }
    }

    private static string? Identity(string section, JsonObject item)
    {
        string? Get(string name) => item[name]?.GetValue<string?>()?.Trim();
        var key = Get("key");
        if (!string.IsNullOrWhiteSpace(key)) return key;
        return section switch
        {
            "connections" => $"{Get("from_key")}|{Get("to_key")}|{Get("label")}",
            "relationships" => $"{Get("source_key")}|{Get("target_key")}|{Get("relation")}",
            "discoveries" => $"{Get("subject")}|{Get("location_key")}|{Get("character_key")}",
            "secrets" => Get("title") ?? Get("name"),
            _ => Get("name") ?? Get("title")
        };
    }

    private static bool IsBlank(JsonNode? node)
    {
        if (node is null) return true;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return string.IsNullOrWhiteSpace(text);
        if (node is JsonArray array) return array.Count == 0;
        if (node is JsonObject obj) return obj.Count == 0;
        return false;
    }

    private static void EnsureSourceKind(JsonObject manifest)
    {
        foreach (var section in MergeSections)
        {
            if (section is "discoveries") continue;
            if (manifest[section] is not JsonArray array) continue;
            foreach (var obj in array.OfType<JsonObject>())
                if (!obj.ContainsKey("source_kind")) obj["source_kind"] = "source_canon";
        }
    }

    private static IEnumerable<string> ChunkSource(string text, int chunkSize, int overlap)
    {
        var normalized = text.Replace("\0", " ").Replace("\r\n", "\n");
        var start = 0;
        while (start < normalized.Length)
        {
            var end = Math.Min(normalized.Length, start + chunkSize);
            if (end < normalized.Length)
            {
                var newline = normalized.LastIndexOf('\n', end - 1, Math.Min(1800, end - start));
                if (newline > start + chunkSize / 2) end = newline + 1;
            }
            var chunk = normalized[start..end].Trim();
            if (chunk.Length > 0) yield return chunk;
            if (end >= normalized.Length) yield break;
            start = Math.Max(start + 1, end - Math.Clamp(overlap, 0, chunkSize / 3));
        }
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
        if (first < 0 || last <= first) throw new InvalidDataException("No JSON object was found in the compiler response.");
        return trimmed[first..(last + 1)];
    }

    private static string NormalizeBase(string url) => url.TrimEnd('/') + "/";

    private static string ExtractionPrompt() => """
You are the canon extraction stage of a local tabletop campaign compiler.
Your job is extraction, not creative writing. Never invent a missing NPC, location, clue, merchant item, monster, relationship, secret, rule, price, statistic, quest step, or map connection during this pass.
Preserve uncertainty rather than guessing. Omit fields and entities that are not supported by the supplied source chunk.
Use stable lowercase dotted keys when the source supplies enough identity, such as location.greenhaven or character.selene_voss. Reuse the same key when an entity reappears.
Mark extracted entities source_kind as source_canon. Do not mark invented or inferred information as canon.
Return a single JSON object. It may contain these arrays when supported: locations, connections, characters, items, spells, merchants, quests, factions, relationships, secrets, timeline, encounters, discoveries.
Useful fields:
locations: key,name,type/area_type,description,parent_key,dm_only,x,y
connections: from_key,to_key,label,travel_minutes,hidden
characters: key,name,character_type,creature_type,level,size,free_hands,armor_class,max_hp,current_hp,location_key,abilities,wallet,public_knowledge,secret_knowledge,saving_throw_proficiencies,skill_proficiencies,tool_proficiencies,attacks,spellcasting_ability,spell_slots,prepared_spells
items: key,name,category,description,price,consumable,equippable,equipment_slot
spells: key,name,level,school,casting_time,range_kind,range_feet,requires_verbal,requires_somatic,requires_material,material_description,duration,requires_concentration,ritual,requires_target,resolution,save_ability,damage_expression,damage_type,half_damage_on_successful_save,healing_expression,extra_damage_per_slot_expression,extra_healing_per_slot_expression,add_spellcasting_ability_modifier_to_healing,cantrip_damage_scaling,cantrip_range_doubling,ignore_half_and_three_quarters_cover_on_save,required_target_creature_type,condition_on_failed_save,repeat_save_at_end_of_turn,next_attack_against_target_has_advantage,effect_expires_at_end_of_caster_next_turn,effect_expires_at_start_of_caster_next_turn,speed_modifier_feet,armor_class_bonus,save_disadvantage_creature_type,base_projectiles,extra_projectiles_per_slot,base_targets,extra_targets_per_slot,attack_roll_bonus_expression,saving_throw_bonus_expression,area_shape,area_size_feet,extra_area_size_per_slot_feet,area_origin,push_feet_on_failed_save,environmental_effect,battlefield_trigger,battlefield_difficult_terrain,battlefield_heavily_obscured,battlefield_blocks_line_of_sight,battlefield_duration_rounds,requires_visible_target
merchants: key,name,location_key,npc_key,wallet,stock[{item_key,quantity,price}]
quests: key,name,status,summary,dm_notes,dm_only,objectives,rewards
factions: key,name,summary,public_knowledge,secret_knowledge
relationships: source_key,target_key,relation,strength,public
secrets: key,title,truth,known_by,reveal_conditions,revealed
timeline: key,name,trigger_type,trigger{campaign_day,minute_of_day},effect{quest,consequence},dm_notes
encounters: key,name,summary,status,dm_only,location_key,members[{key,name,quantity,character_type,creature_type,size,free_hands,armor_class,max_hp,initiative_modifier,attacks,metadata}]
discoveries: subject,location_key,character_key
campaign: name,system,summary,tone
party: name
For spells, extract mechanical fields only when the supplied source explicitly states them. Do not reconstruct a known spell from memory. If a source merely names a spell, keep only the name/key and omit unsupported mechanics.
Return only JSON. Do not include markdown fences or commentary.
""";
}
