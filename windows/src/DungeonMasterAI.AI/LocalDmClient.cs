using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.AI;

public sealed record LocalAiStatus(bool Online, string Message, string? Model = null);
public sealed record DmTurnResult(string Narration, int ToolCalls, IReadOnlyList<string> Audit);

public sealed class LocalDmClient(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<LocalAiStatus> CheckAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(new Uri(NormalizeBase(settings.LlamaServerUrl)), "v1/models"), cancellationToken);
            if (!response.IsSuccessStatusCode) return new LocalAiStatus(false, $"Local AI returned HTTP {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var model = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                ? data[0].TryGetProperty("id", out var id) ? id.GetString() : null
                : null;
            return new LocalAiStatus(true, "Local AI is online.", model);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return new LocalAiStatus(false, ex.Message);
        }
    }

    public async Task<LocalAiStatus> TestInferenceAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                model = settings.ModelName,
                messages = new object[]
                {
                    new { role = "system", content = "You are a local inference health check. Follow the user instruction exactly." },
                    new { role = "user", content = "Reply with exactly: READY" }
                },
                temperature = 0,
                max_tokens = 16
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(NormalizeBase(settings.LlamaServerUrl)), "v1/chat/completions"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new LocalAiStatus(false, $"Inference test returned HTTP {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").TryGetProperty("content", out var contentNode)
                ? contentNode.GetString() ?? ""
                : "";
            return string.Equals(content.Trim(), "READY", StringComparison.OrdinalIgnoreCase)
                ? new LocalAiStatus(true, "Local AI completed a chat inference successfully.", settings.ModelName)
                : new LocalAiStatus(true, $"Local AI completed inference and replied: {content.Trim()}", settings.ModelName);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
        {
            return new LocalAiStatus(false, ex.Message);
        }
    }

    public async Task<DmTurnResult> RunTurnAsync(
        CampaignState campaign,
        string playerInput,
        AppSettings settings,
        DmToolRouter tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (string.IsNullOrWhiteSpace(playerInput)) throw new ArgumentException("Player input is required.", nameof(playerInput));

        // Some llama.cpp chat templates, including the configured Qwen template, allow
        // exactly one system message and require it to be the first message. Keep all
        // authoritative application context inside that single leading system message.
        var systemContext = string.Join("\n\n", new[]
        {
            SystemPrompt(settings.PlayerSafeMode),
            BuildCampaignContext(campaign),
            BuildDmOnlyContext(campaign)
        });
        var messages = new List<object>
        {
            new { role = "system", content = systemContext }
        };

        // Persisted application notices use the "system" role for the UI, but they are
        // not conversational model turns. Sending a later system role breaks templates
        // that require the system message to be first, so only user/assistant history
        // is replayed to the model.
        foreach (var existing in campaign.Chat
                     .Where(x => x.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                              || x.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                     .TakeLast(20))
            messages.Add(new { role = existing.Role.ToLowerInvariant(), content = existing.Content });

        messages.Add(new { role = "user", content = playerInput.Trim() });

        var audit = new List<string>();
        var toolCount = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            var payload = new
            {
                model = settings.ModelName,
                messages,
                tools = tools.ToOpenAiToolSchema(),
                tool_choice = "auto",
                temperature = settings.Temperature,
                max_tokens = settings.MaxTokens
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(NormalizeBase(settings.LlamaServerUrl)), "v1/chat/completions"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Local AI returned HTTP {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var contentNode) && contentNode.ValueKind == JsonValueKind.String
                ? contentNode.GetString() ?? ""
                : "";

            if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
            {
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Local AI returned neither narration nor tool calls.");
                return new DmTurnResult(content.Trim(), toolCount, audit);
            }

            var serializedCalls = JsonSerializer.Deserialize<object>(calls.GetRawText(), _json)!;
            messages.Add(new { role = "assistant", content = string.IsNullOrWhiteSpace(content) ? null : content, tool_calls = serializedCalls });

            foreach (var call in calls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? throw new InvalidDataException("Tool call missing a function name.");
                var arguments = function.TryGetProperty("arguments", out var argsNode)
                    ? argsNode.ValueKind == JsonValueKind.String ? argsNode.GetString() ?? "{}" : argsNode.GetRawText()
                    : "{}";
                var result = tools.Execute(campaign, name, arguments);
                toolCount++;
                audit.Add($"{name}: {(result.Ok ? "ok" : "error")} {(result.Error ?? "")}".Trim());
                var resultJson = JsonSerializer.Serialize(result, _json);
                messages.Add(new { role = "tool", tool_call_id = id, name, content = resultJson });
            }
        }

        throw new InvalidOperationException("The local DM exceeded the maximum tool-call loop count.");
    }

    private static string NormalizeBase(string url) => url.TrimEnd('/') + "/";

    private static string SystemPrompt(bool safeMode) => $$"""
You are the Dungeon Master narrator for a local tabletop role-playing application.
The application, not you, is the authority for deterministic game state.
Preserve player agency. Never decide a player character's voluntary action, feelings, dialogue, or intent for them.
Never invent a dice result, HP change, inventory change, currency change, location change, quest status change, or passage of time. Use the provided tools for those changes.
Use search_rules when an uncertain rules question materially affects resolution.
You may receive a separate DM-ONLY context so you can run the adventure correctly. Never reveal, quote, paraphrase, hint at, or let a player infer a DM-only fact until player actions and verified game state justify its reveal.
When portraying an NPC, use only that NPC's public knowledge plus that NPC's own relevant secret knowledge. Do not let one NPC speak from another NPC's private knowledge or from omniscient DM knowledge.
Narrate only after any required tool calls have returned. If a tool rejects an action, narrate the constraint rather than pretending it succeeded.
Run non-player creatures yourself. When the active combatant is an NPC or hostile creature, choose a reasonable action from verified state, resolve it with tools, advance the turn as needed, and continue through NPC turns until a player character must decide what to do. Never ask the player what an enemy or NPC should do.
A player character does NOT make a Death Saving Throw immediately when they drop to 0 HP. Continue the current creature's turn and any intervening NPC turns normally. When a player character STARTS their turn at 0 HP and is not Stable or Dead, STOP before resolving that turn. Never call death_save for a player character. The Game Table will require the player to roll the Death Saving Throw themselves.
When tactical combat begins, ensure every participating combatant has a sensible initial grid position using the positioning tools before the first player decision so the live battlefield can render immediately.
If the player says "next turn", "continue", or ends a player turn, advance combat and autonomously resolve intervening NPC turns until the next player-character decision point, including stopping at a required player-controlled Death Saving Throw.
Keep live-play narration immersive and compact: normally 2 to 5 short paragraphs. Do not dump tool names, raw coordinates, action-economy flags, JSON, audit text, or full stat blocks into narration unless the player explicitly asks for mechanics.
Do not use markdown headings or decorative bold markers in ordinary narration. State an important roll/damage outcome in one short natural-language sentence, then return to the fiction.
End with a brief clear choice or "What do you do?" only when a player character genuinely needs to act.
{{(safeMode ? "Player-safe information boundaries are strictly enabled." : "A DM may reveal secrets only when the game state or player action justifies it.")}}
""";

    private static string BuildCampaignContext(CampaignState campaign)
    {
        var location = campaign.Locations.FirstOrDefault(x => x.Id == campaign.PartyLocationId && x.Discovered && !x.DmOnly);
        var pcs = campaign.Characters.Where(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)).Select(c => new
        {
            c.Id, c.Name, c.CreatureType, c.Level, c.ArmorClass, c.MaxHp, c.CurrentHp, c.TempHp, c.Gold, c.PublicKnowledge,
            c.Abilities, c.Speed, effective_speed = CharacterMechanics.EffectiveSpeed(c, campaign.ActiveEffects), c.ProficiencyBonus,
            c.Conditions, c.ExhaustionLevel, c.DeathSaveSuccesses, c.DeathSaveFailures, c.Stable, c.Dead,
            c.HitDiceRemaining, c.HitDiceMaximum, c.ConcentrationEffect,
            c.SpellcastingAbility,
            spell_save_dc = 8 + CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(c, c.SpellcastingAbility)) + Math.Max(0, c.ProficiencyBonus),
            spell_attack_modifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(c, c.SpellcastingAbility)) + Math.Max(0, c.ProficiencyBonus),
            prepared_spells = campaign.Spells.Where(s => c.PreparedSpellIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(s.Key) && c.PreparedSpellIds.Contains(s.Key, StringComparer.OrdinalIgnoreCase))).Select(s => new { s.Id, s.Name, s.Level, s.CastingTime, s.RequiresConcentration, s.Ritual, s.Resolution }),
            spell_slots = c.SpellSlots.ToDictionary(x => x.Key, x => new { x.Value.Remaining, x.Value.Maximum }),
            resources = c.Resources.Select(r => new { r.Name, r.Remaining, r.Maximum }),
            ongoing_effects = campaign.ActiveEffects.Where(e => e.TargetCharacterId.Equals(c.Id, StringComparison.OrdinalIgnoreCase)).Select(e => new { e.Name, e.Condition, e.SourceCharacterId, e.RepeatSaveAbility, e.SaveDc, e.NextAttackAgainstTargetHasAdvantage })
        });
        var visibleLocationIds = campaign.Locations.Where(l => l.Discovered && !l.DmOnly).Select(l => l.Id).ToHashSet();
        var nearbyPublicNpcs = campaign.Characters
            .Where(c => !c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) && c.LocationId == campaign.PartyLocationId)
            .Select(c => new { c.Id, c.Name, c.CharacterType, c.CreatureType, c.PublicKnowledge });
        var visibleConnections = campaign.Connections
            .Where(c => !c.Hidden && visibleLocationIds.Contains(c.FromLocationId) && visibleLocationIds.Contains(c.ToLocationId))
            .Select(c => new { c.FromLocationId, c.ToLocationId, c.Label, c.TravelMinutes });
        var quests = campaign.Quests.Where(q => !q.DmOnly).Select(q => new { q.Id, q.Name, q.Status, q.Summary, q.Objectives });
        var publicFactions = campaign.Factions
            .Where(f => !string.IsNullOrWhiteSpace(f.PublicKnowledge) && !f.PublicKnowledge.TrimStart().StartsWith("None", StringComparison.OrdinalIgnoreCase))
            .Select(f => new { f.Id, f.Name, f.Summary, f.PublicKnowledge });
        var publicRelationships = campaign.Relationships.Where(r => r.Public).Select(r => new { r.SourceKey, r.TargetKey, r.Relation, r.Strength });
        var revealedSecrets = campaign.Secrets.Where(secret => secret.Revealed).Select(secret => new { secret.Id, secret.Title, secret.Truth });
        var publicSupplements = campaign.Supplements.Where(s => !s.DmOnly).Select(s => new { s.TargetKey, s.Category, s.Content, s.SourceKind });
        var recent = campaign.Events.Where(e => !e.DmOnly).TakeLast(8).Select(e => new { e.Type, e.Summary });
        var activeEncounter = campaign.Encounters.LastOrDefault(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
        var encounterContext = activeEncounter is null ? null : new
        {
            activeEncounter.Id,
            activeEncounter.Name,
            activeEncounter.Round,
            activeEncounter.TurnIndex,
            spell_slot_casters_this_turn = activeEncounter.SpellSlotCasterIdsThisTurn,
            combatants = activeEncounter.Combatants.Select(c =>
            {
                var character = campaign.Characters.FirstOrDefault(x => x.Id == c.CharacterId);
                return new
                {
                    combatant_id = c.Id,
                    character_id = c.CharacterId,
                    name = character?.Name ?? "Unknown",
                    character_type = character?.CharacterType ?? "",
                    armor_class = character?.ArmorClass ?? 0,
                    current_hp = character?.CurrentHp ?? 0,
                    max_hp = character?.MaxHp ?? 0,
                    temp_hp = character?.TempHp ?? 0,
                    c.Initiative,
                    c.Surprised,
                    c.Positioned,
                    c.GridX,
                    c.GridY,
                    c.MovementRemainingFeet,
                    c.ActionAvailable,
                    c.BonusActionAvailable,
                    c.AttackActionInProgress,
                    c.AttacksRemainingInAction,
                    c.ReactionAvailable,
                    c.Disengaging,
                    c.Dodging,
                    c.DeathSaveRequiredThisTurn,
                    c.DeathSaveResolvedThisTurn,
                    c.IsHidden,
                    c.HideCheckTotal,
                    readied_action = c.ReadiedAction is null ? null : new { c.ReadiedAction.Kind, c.ReadiedAction.Trigger, c.ReadiedAction.TargetCombatantId, c.ReadiedAction.AttackName, c.ReadiedAction.SpellId, c.ReadiedAction.CastAtLevel, c.ReadiedAction.UsedSpellSlot },
                    speed = character is null ? 0 : CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects),
                    attacks = character is null ? Array.Empty<object>() : AvailableAttacks(character)
                };
            }).ToArray()
        };
        return "PLAYER-SAFE CAMPAIGN CONTEXT:\n" + JsonSerializer.Serialize(new
        {
            campaign = campaign.Name,
            campaign.Summary,
            campaign.Tone,
            time = GameEngine.FormatCampaignTime(campaign),
            location = location is null ? null : new { location.Id, location.Name, location.Description },
            player_characters = pcs,
            nearby_public_npcs = nearbyPublicNpcs,
            visible_connections = visibleConnections,
            quests,
            public_factions = publicFactions,
            public_relationships = publicRelationships,
            revealed_secrets = revealedSecrets,
            generated_public_details = publicSupplements,
            recent_events = recent,
            active_encounter = encounterContext
        });
    }

    private static object[] AvailableAttacks(CharacterSheet character)
    {
        IEnumerable<AttackProfile> attacks = character.Attacks.Count == 0
            ? new[] { CharacterMechanics.UnarmedStrikeProfile(character) }
            : character.Attacks;
        return attacks.Select(a => (object)new { a.Name, a.AttackBonus, a.DamageExpression, a.DamageType, a.ReachFeet, a.RangeFeet }).ToArray();
    }

    private static string BuildDmOnlyContext(CampaignState campaign)
    {
        var currentLocationId = campaign.PartyLocationId;
        var nearbySecrets = campaign.Characters
            .Where(c => !c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                && c.LocationId == currentLocationId
                && !string.IsNullOrWhiteSpace(c.SecretKnowledge))
            .Select(c => new { c.Id, c.Name, secret_knowledge = c.SecretKnowledge });

        var questNotes = campaign.Quests
            .Where(q => !string.IsNullOrWhiteSpace(q.DmNotes))
            .Select(q => new { q.Id, q.Name, q.Status, dm_notes = q.DmNotes, q.DmOnly });

        var hiddenConnections = campaign.Connections
            .Where(c => c.Hidden && (c.FromLocationId == currentLocationId || c.ToLocationId == currentLocationId))
            .Select(c => new { c.FromLocationId, c.ToLocationId, c.Label, c.TravelMinutes });

        var plannedEncounters = campaign.Encounters
            .Where(e => e.Status.Equals("planned", StringComparison.OrdinalIgnoreCase)
                && (e.LocationId is null || e.LocationId == currentLocationId))
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Summary,
                e.DmOnly,
                members = e.Combatants.Select(c =>
                {
                    var character = campaign.Characters.FirstOrDefault(x => x.Id == c.CharacterId);
                    return new
                    {
                        combatant_id = c.Id,
                        character_id = c.CharacterId,
                        name = character?.Name ?? "Unknown",
                        armor_class = character?.ArmorClass ?? 0,
                        max_hp = character?.MaxHp ?? 0,
                        secret_knowledge = character?.SecretKnowledge ?? "",
                        attacks = character is null ? Array.Empty<object>() : AvailableAttacks(character)
                    };
                }).ToArray()
            });

        var nearbyHiddenLocations = campaign.Connections
            .Where(c => c.FromLocationId == currentLocationId || c.ToLocationId == currentLocationId)
            .Select(c => c.FromLocationId == currentLocationId ? c.ToLocationId : c.FromLocationId)
            .Distinct()
            .Select(id => campaign.Locations.FirstOrDefault(l => l.Id == id))
            .Where(l => l is not null && (!l.Discovered || l.DmOnly))
            .Select(l => new { l!.Id, l.Key, l.Name, l.Type, l.Description, l.DmOnly });

        var factionSecrets = campaign.Factions
            .Where(f => !string.IsNullOrWhiteSpace(f.SecretKnowledge))
            .Select(f => new { f.Id, f.Key, f.Name, f.Summary, f.SecretKnowledge });
        var privateRelationships = campaign.Relationships
            .Where(r => !r.Public)
            .Select(r => new { r.SourceKey, r.TargetKey, r.Relation, r.Strength });
        var unrevealedSecrets = campaign.Secrets
            .Where(secret => !secret.Revealed)
            .Select(secret => new { secret.Id, secret.Key, secret.Title, secret.Truth, secret.KnownByKeys, secret.RevealConditions });
        var generatedDmDetails = campaign.Supplements.Where(s => s.DmOnly).Select(s => new { s.TargetKey, s.Category, s.Content, s.SourceKind });
        var pendingTimeline = campaign.Timeline
            .Where(evt => !evt.Resolved)
            .OrderBy(evt => evt.CampaignDay)
            .ThenBy(evt => evt.MinuteOfDay)
            .Take(12)
            .Select(evt => new { evt.Id, evt.Key, evt.Name, evt.TriggerType, evt.CampaignDay, evt.MinuteOfDay, evt.EffectQuestKey, evt.Consequence, evt.DmNotes });

        return "DM-ONLY CONTEXT. NEVER DISCLOSE ANY ITEM BELOW UNTIL PLAYER ACTIONS AND VERIFIED STATE JUSTIFY IT:\n"
            + JsonSerializer.Serialize(new
            {
                nearby_npc_secrets = nearbySecrets,
                quest_dm_notes = questNotes,
                hidden_connections = hiddenConnections,
                nearby_hidden_locations = nearbyHiddenLocations,
                faction_secrets = factionSecrets,
                private_relationships = privateRelationships,
                unrevealed_secrets = unrevealedSecrets,
                generated_dm_details = generatedDmDetails,
                pending_timeline = pendingTimeline,
                planned_encounters = plannedEncounters
            });
    }

}
