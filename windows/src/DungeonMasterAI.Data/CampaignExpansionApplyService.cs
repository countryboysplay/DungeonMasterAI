using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

public sealed record CampaignExpansionApplyResult(int AddedObjects, IReadOnlyList<string> Warnings);

public sealed class CampaignExpansionApplyService
{
    public CampaignExpansionApplyResult Apply(CampaignState campaign, string patchJson)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var document = JsonDocument.Parse(patchJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Campaign expansion patch must be a JSON object.");

        var root = document.RootElement;
        var warnings = new List<string>();
        var added = 0;

        var locations = campaign.Locations.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var characters = campaign.Characters.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var items = campaign.Items.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var merchants = campaign.Merchants.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var quests = campaign.Quests.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var factions = campaign.Factions.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        // Locations are added first so subsequent generated entities can resolve against them.
        if (Array(root, "locations") is { } generatedLocations)
        {
            foreach (var node in generatedLocations.EnumerateArray())
            {
                var key = Key(node, "key");
                if (!CanAddKey(key, locations.Keys, "location", warnings)) continue;
                var location = new WorldLocation
                {
                    Key = key!,
                    Name = String(node, "name") ?? key!,
                    Type = String(node, "type") ?? String(node, "area_type") ?? "area",
                    Description = String(node, "description") ?? "",
                    DmOnly = Bool(node, "dm_only"),
                    X = Clamp(Double(node, "x", 0.5), 0, 1),
                    Y = Clamp(Double(node, "y", 0.5), 0, 1),
                    Discovered = false,
                    SourceKind = "ai_expanded"
                };
                var parentKey = String(node, "parent_key");
                if (!string.IsNullOrWhiteSpace(parentKey))
                {
                    if (locations.TryGetValue(parentKey, out var parent)) location.ParentId = parent.Id;
                    else warnings.Add($"Generated location '{key}' references unresolved parent '{parentKey}'.");
                }
                campaign.Locations.Add(location);
                locations[key!] = location;
                added++;
            }
        }

        if (Array(root, "items") is { } generatedItems)
        {
            foreach (var node in generatedItems.EnumerateArray())
            {
                var key = Key(node, "key");
                if (!CanAddKey(key, items.Keys, "item", warnings)) continue;
                var item = new ItemDefinition
                {
                    Key = key!,
                    Name = String(node, "name") ?? key!,
                    Category = String(node, "category") ?? "gear",
                    Description = String(node, "description") ?? "",
                    PriceGp = Math.Max(0, Int(node, "price_gp", 0)),
                    Consumable = Bool(node, "consumable"),
                    Equippable = Bool(node, "equippable"),
                    EquipmentSlot = String(node, "equipment_slot") ?? "",
                    SourceKind = "ai_expanded"
                };
                campaign.Items.Add(item);
                items[key!] = item;
                added++;
            }
        }

        if (Array(root, "characters") is { } generatedCharacters)
        {
            foreach (var node in generatedCharacters.EnumerateArray())
            {
                var key = Key(node, "key");
                if (!CanAddKey(key, characters.Keys, "character", warnings)) continue;
                var characterType = String(node, "character_type") ?? "npc";
                if (characterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Skipped generated player character '{key}'. Expansion may not create PCs.");
                    continue;
                }

                var maxHp = Math.Max(1, Int(node, "max_hp", 4));
                var character = new CharacterSheet
                {
                    Key = key!,
                    Name = String(node, "name") ?? key!,
                    CharacterType = characterType,
                    Level = Math.Max(1, Int(node, "level", 1)),
                    ArmorClass = Math.Max(1, Int(node, "armor_class", 10)),
                    MaxHp = maxHp,
                    CurrentHp = Math.Clamp(Int(node, "current_hp", maxHp), 0, maxHp),
                    Speed = Math.Max(0, Int(node, "speed", 30)),
                    PublicKnowledge = String(node, "public_knowledge") ?? "",
                    SecretKnowledge = String(node, "secret_knowledge") ?? "",
                    ProficiencyBonus = Math.Max(0, Int(node, "proficiency_bonus", 2)),
                    SourceKind = "ai_expanded"
                };
                var locationKey = String(node, "location_key");
                if (!string.IsNullOrWhiteSpace(locationKey))
                {
                    if (locations.TryGetValue(locationKey, out var location)) character.LocationId = location.Id;
                    else warnings.Add($"Generated character '{key}' references unresolved location '{locationKey}'.");
                }
                if (node.TryGetProperty("abilities", out var abilities) && abilities.ValueKind == JsonValueKind.Object)
                    foreach (var ability in abilities.EnumerateObject()) if (ability.Value.TryGetInt32(out var score)) character.Abilities[ability.Name] = Math.Clamp(score, 1, 30);
                ParseAttacks(node, character);
                campaign.Characters.Add(character);
                characters[key!] = character;
                added++;
            }
        }

        if (Array(root, "merchants") is { } generatedMerchants)
        {
            foreach (var node in generatedMerchants.EnumerateArray())
            {
                var key = Key(node, "key");
                if (!CanAddKey(key, merchants.Keys, "merchant", warnings)) continue;
                var merchant = new Merchant
                {
                    Key = key!,
                    Name = String(node, "name") ?? key!,
                    Gold = Math.Max(0, Int(node, "gold", 50)),
                    SourceKind = "ai_expanded"
                };
                var locationKey = String(node, "location_key");
                if (!string.IsNullOrWhiteSpace(locationKey))
                {
                    if (locations.TryGetValue(locationKey, out var location)) merchant.LocationId = location.Id;
                    else warnings.Add($"Generated merchant '{key}' references unresolved location '{locationKey}'.");
                }
                var npcKey = String(node, "npc_key");
                if (!string.IsNullOrWhiteSpace(npcKey))
                {
                    if (characters.TryGetValue(npcKey, out var npc)) merchant.NpcId = npc.Id;
                    else warnings.Add($"Generated merchant '{key}' references unresolved NPC '{npcKey}'.");
                }
                campaign.Merchants.Add(merchant);
                merchants[key!] = merchant;
                added++;
            }
        }

        if (Array(root, "quests") is { } generatedQuests)
        {
            foreach (var node in generatedQuests.EnumerateArray())
            {
                var key = Key(node, "key");
                if (!CanAddKey(key, quests.Keys, "quest", warnings)) continue;
                var quest = new Quest
                {
                    Key = key!,
                    Name = String(node, "name") ?? key!,
                    Status = String(node, "status") ?? "available",
                    Summary = String(node, "summary") ?? "",
                    DmNotes = String(node, "dm_notes") ?? "",
                    DmOnly = Bool(node, "dm_only"),
                    RewardGp = Math.Max(0, Int(node, "reward_gp", 0)),
                    SourceKind = "ai_expanded"
                };
                AddStrings(node, "objectives", quest.Objectives);
                campaign.Quests.Add(quest);
                quests[key!] = quest;
                added++;
            }
        }

        if (Array(root, "factions") is { } generatedFactions)
        {
            foreach (var node in generatedFactions.EnumerateArray())
            {
                var key = Key(node, "key");
                if (!CanAddKey(key, factions.Keys, "faction", warnings)) continue;
                var faction = new Faction
                {
                    Key = key!,
                    Name = String(node, "name") ?? key!,
                    Summary = String(node, "summary") ?? "",
                    PublicKnowledge = String(node, "public_knowledge") ?? "",
                    SecretKnowledge = String(node, "secret_knowledge") ?? "",
                    SourceKind = "ai_expanded"
                };
                campaign.Factions.Add(faction);
                factions[key!] = faction;
                added++;
            }
        }

        var entityKeys = BuildEntityKeys(campaign);
        if (Array(root, "relationships") is { } generatedRelationships)
        {
            foreach (var node in generatedRelationships.EnumerateArray())
            {
                var sourceKey = String(node, "source_key") ?? "";
                var targetKey = String(node, "target_key") ?? "";
                if (!entityKeys.Contains(sourceKey) || !entityKeys.Contains(targetKey))
                {
                    warnings.Add($"Skipped generated relationship '{sourceKey}' -> '{targetKey}' because an entity key is unresolved.");
                    continue;
                }
                var relation = String(node, "relation") ?? "related_to";
                if (campaign.Relationships.Any(r => r.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)
                    && r.TargetKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase)
                    && r.Relation.Equals(relation, StringComparison.OrdinalIgnoreCase))) continue;
                campaign.Relationships.Add(new EntityRelationship
                {
                    SourceKey = sourceKey,
                    TargetKey = targetKey,
                    Relation = relation,
                    Strength = Clamp(Double(node, "strength", 1.0), -1, 1),
                    Public = Bool(node, "public"),
                    SourceKind = "ai_expanded"
                });
                added++;
            }
        }

        if (Array(root, "connections") is { } generatedConnections)
        {
            foreach (var node in generatedConnections.EnumerateArray())
            {
                var fromKey = String(node, "from_key");
                var toKey = String(node, "to_key");
                if (fromKey is null || toKey is null || !locations.TryGetValue(fromKey, out var from) || !locations.TryGetValue(toKey, out var to))
                {
                    warnings.Add($"Skipped generated connection '{fromKey ?? "?"}' -> '{toKey ?? "?"}' because a location is unresolved.");
                    continue;
                }
                if (campaign.Connections.Any(c =>
                    ((c.FromLocationId == from.Id && c.ToLocationId == to.Id) || (c.FromLocationId == to.Id && c.ToLocationId == from.Id))
                    && c.Label.Equals(String(node, "label") ?? "Path", StringComparison.OrdinalIgnoreCase))) continue;
                campaign.Connections.Add(new LocationConnection
                {
                    FromLocationId = from.Id,
                    ToLocationId = to.Id,
                    Label = String(node, "label") ?? "Path",
                    TravelMinutes = Math.Max(0, Int(node, "travel_minutes", 5)),
                    Hidden = Bool(node, "hidden"),
                    SourceKind = "ai_expanded"
                });
                added++;
            }
        }

        if (Array(root, "merchant_stock_additions") is { } stockAdditions)
        {
            foreach (var node in stockAdditions.EnumerateArray())
            {
                var merchantKey = String(node, "merchant_key");
                var itemKey = String(node, "item_key");
                if (merchantKey is null || itemKey is null || !merchants.TryGetValue(merchantKey, out var merchant) || !items.TryGetValue(itemKey, out var item))
                {
                    warnings.Add($"Skipped generated merchant stock '{merchantKey ?? "?"}' / '{itemKey ?? "?"}' because a reference is unresolved.");
                    continue;
                }
                var existing = merchant.Stock.FirstOrDefault(s => s.ItemId == item.Id);
                if (existing is not null) continue; // Never alter compiled stock, regardless of provenance.
                merchant.Stock.Add(new MerchantStockEntry
                {
                    ItemId = item.Id,
                    Quantity = Math.Max(0, Int(node, "quantity", 1)),
                    PriceGp = node.TryGetProperty("price_gp", out var price) && price.TryGetInt32(out var gp) ? Math.Max(0, gp) : null,
                    SourceKind = "ai_expanded"
                });
                added++;
            }
        }

        if (Array(root, "secrets") is { } generatedSecrets)
        {
            foreach (var node in generatedSecrets.EnumerateArray())
            {
                var key = Key(node, "key");
                if (string.IsNullOrWhiteSpace(key) || campaign.Secrets.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add($"Skipped generated secret with duplicate or blank key '{key ?? "?"}'.");
                    continue;
                }
                var secret = new CampaignSecret
                {
                    Key = key,
                    Title = String(node, "title") ?? key,
                    Truth = String(node, "truth") ?? "",
                    Revealed = Bool(node, "revealed"),
                    SourceKind = "ai_expanded"
                };
                AddStrings(node, "known_by", secret.KnownByKeys);
                AddStrings(node, "reveal_conditions", secret.RevealConditions);
                campaign.Secrets.Add(secret);
                added++;
            }
        }

        if (Array(root, "timeline") is { } generatedTimeline)
        {
            foreach (var node in generatedTimeline.EnumerateArray())
            {
                var key = Key(node, "key");
                if (string.IsNullOrWhiteSpace(key) || campaign.Timeline.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                campaign.Timeline.Add(new TimelineEvent
                {
                    Key = key,
                    Name = String(node, "name") ?? key,
                    TriggerType = String(node, "trigger_type") ?? "time",
                    CampaignDay = Math.Max(1, Int(node, "campaign_day", campaign.Day)),
                    MinuteOfDay = Math.Clamp(Int(node, "minute_of_day", campaign.MinuteOfDay), 0, 1439),
                    EffectQuestKey = String(node, "effect_quest_key") ?? "",
                    Consequence = String(node, "consequence") ?? "",
                    DmNotes = String(node, "dm_notes") ?? "",
                    SourceKind = "ai_expanded"
                });
                added++;
            }
        }

        if (Array(root, "encounters") is { } generatedEncounters)
        {
            foreach (var node in generatedEncounters.EnumerateArray())
            {
                var key = Key(node, "key");
                if (string.IsNullOrWhiteSpace(key) || campaign.Encounters.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                var encounter = new EncounterState
                {
                    Key = key,
                    Name = String(node, "name") ?? key,
                    Summary = String(node, "summary") ?? "",
                    Status = String(node, "status") ?? "planned",
                    DmOnly = Bool(node, "dm_only"),
                    SourceKind = "ai_expanded"
                };
                var locationKey = String(node, "location_key");
                if (!string.IsNullOrWhiteSpace(locationKey) && locations.TryGetValue(locationKey, out var encounterLocation)) encounter.LocationId = encounterLocation.Id;
                else if (!string.IsNullOrWhiteSpace(locationKey)) warnings.Add($"Generated encounter '{key}' references unresolved location '{locationKey}'.");

                if (node.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        var quantity = Math.Clamp(Int(member, "quantity", 1), 1, 30);
                        for (var copy = 1; copy <= quantity; copy++)
                        {
                            var memberKey = String(member, "key") ?? $"member.{encounter.Combatants.Count + 1}";
                            var monsterKey = $"{encounter.Key}.{memberKey}.{copy}";
                            if (characters.ContainsKey(monsterKey)) continue;
                            var maxHp = Math.Max(1, Int(member, "max_hp", 4));
                            var monster = new CharacterSheet
                            {
                                Key = monsterKey,
                                Name = quantity == 1 ? String(member, "name") ?? memberKey : $"{String(member, "name") ?? memberKey} {copy}",
                                CharacterType = String(member, "character_type") ?? "monster",
                                Level = Math.Max(1, Int(member, "level", 1)),
                                ArmorClass = Math.Max(1, Int(member, "armor_class", 10)),
                                MaxHp = maxHp,
                                CurrentHp = maxHp,
                                LocationId = encounter.LocationId,
                                Speed = Math.Max(0, Int(member, "speed", 30)),
                                SourceKind = "ai_expanded"
                            };
                            var initiativeModifier = Int(member, "initiative_modifier", 0);
                            monster.Abilities["dexterity"] = Math.Clamp(10 + 2 * initiativeModifier, 1, 30);
                            ParseAttacks(member, monster);
                            campaign.Characters.Add(monster);
                            characters[monster.Key] = monster;
                            encounter.Combatants.Add(new CombatantState { CharacterId = monster.Id, TieBreaker = encounter.Combatants.Count });
                            added++;
                        }
                    }
                }
                campaign.Encounters.Add(encounter);
                added++;
            }
        }

        if (Array(root, "supplements") is { } supplements)
        {
            entityKeys = BuildEntityKeys(campaign);
            foreach (var node in supplements.EnumerateArray())
            {
                var targetKey = String(node, "target_key") ?? "";
                var category = String(node, "category") ?? "detail";
                var content = String(node, "content") ?? "";
                if (string.IsNullOrWhiteSpace(targetKey) || string.IsNullOrWhiteSpace(content) || !entityKeys.Contains(targetKey))
                {
                    warnings.Add($"Skipped generated supplement for unresolved target '{targetKey}'.");
                    continue;
                }
                if (campaign.Supplements.Any(s => s.TargetKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase)
                    && s.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
                    && s.Content.Equals(content, StringComparison.OrdinalIgnoreCase))) continue;
                campaign.Supplements.Add(new CampaignSupplement
                {
                    TargetKey = targetKey,
                    Category = category,
                    Content = content,
                    DmOnly = !node.TryGetProperty("dm_only", out var dmOnly) || dmOnly.ValueKind != JsonValueKind.False,
                    SourceKind = "ai_expanded"
                });
                added++;
            }
        }

        if (added > 0)
        {
            campaign.Events.Add(new CampaignEvent
            {
                Type = "campaign_ai_expanded",
                Summary = $"Added {added} AI-expanded campaign object(s) without modifying source canon.",
                DmOnly = true
            });
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return new CampaignExpansionApplyResult(added, warnings);
    }

    private static JsonElement? Array(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;
    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    private static string? Key(JsonElement element, string name) => String(element, name);
    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();
    private static int Int(JsonElement element, string name, int fallback) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static double Double(JsonElement element, string name, double fallback) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : fallback;
    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    private static bool CanAddKey(string? key, IEnumerable<string> existing, string type, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(key)) { warnings.Add($"Skipped generated {type} with blank key."); return false; }
        if (existing.Contains(key, StringComparer.OrdinalIgnoreCase)) { warnings.Add($"Skipped generated {type} '{key}' because that key already exists. Existing canon was preserved."); return false; }
        return true;
    }

    private static void AddStrings(JsonElement element, string name, ICollection<string> target)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var value in array.EnumerateArray()) if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) target.Add(value.GetString()!.Trim());
    }

    private static void ParseAttacks(JsonElement node, CharacterSheet character)
    {
        if (!node.TryGetProperty("attacks", out var attacks) || attacks.ValueKind != JsonValueKind.Array) return;
        foreach (var attack in attacks.EnumerateArray())
        {
            var damage = String(attack, "damage_expression") ?? String(attack, "damage") ?? "1";
            character.Attacks.Add(new AttackProfile
            {
                Name = String(attack, "name") ?? "Attack",
                AttackBonus = Int(attack, "attack_bonus", 0),
                DamageExpression = damage,
                DamageType = String(attack, "damage_type") ?? "",
                ReachFeet = Math.Max(0, Int(attack, "reach_feet", 5)),
                RangeFeet = Math.Max(0, Int(attack, "range_feet", 0))
            });
        }
    }

    private static HashSet<string> BuildEntityKeys(CampaignState campaign)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in campaign.Locations.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Characters.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Items.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Merchants.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Quests.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Factions.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Secrets.Select(x => x.Key)) AddKey(keys, key);
        foreach (var key in campaign.Encounters.Select(x => x.Key)) AddKey(keys, key);
        return keys;
    }

    private static void AddKey(ISet<string> keys, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
    }
}
