using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DungeonMasterAI.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DungeonMasterAI.Data;

public sealed record CampaignImportResult(CampaignState Campaign, IReadOnlyList<string> Warnings, string SourceFile);
public sealed record CampaignSourceDocument(string SourceFile, string Text);

public sealed class CampaignImportService
{
    public async Task<CampaignSourceDocument> ExtractSourceAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Campaign source was not found.", path);
        var sourceFile = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = extension switch
        {
            ".txt" or ".md" or ".markdown" => await File.ReadAllTextAsync(path, cancellationToken),
            ".pdf" => ExtractPdf(path),
            ".docx" => ExtractDocx(path),
            _ => throw new NotSupportedException("AI campaign compilation supports TXT, Markdown, PDF, and DOCX source documents.")
        };
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The campaign source did not contain extractable text.");
        return new CampaignSourceDocument(sourceFile, text);
    }

    public async Task<CampaignImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Campaign source was not found.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".json" => await ImportManifestAsync(path, cancellationToken),
            ".txt" or ".md" or ".markdown" => CompileText(await File.ReadAllTextAsync(path, cancellationToken), Path.GetFileName(path)),
            ".pdf" => CompileText(ExtractPdf(path), Path.GetFileName(path)),
            ".docx" => CompileText(ExtractDocx(path), Path.GetFileName(path)),
            _ => throw new NotSupportedException("Supported campaign sources are JSON, TXT, Markdown, PDF, and DOCX.")
        };
    }

    public async Task<CampaignImportResult> ImportManifestAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ImportManifestDocument(doc.RootElement, Path.GetFileName(path));
    }

    public CampaignImportResult ImportManifestJson(string manifestJson, string sourceName)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        return ImportManifestDocument(doc.RootElement, sourceName);
    }

    private CampaignImportResult ImportManifestDocument(JsonElement root, string sourceFile)
    {
        var warnings = new List<string>();
        var campaignNode = root.TryGetProperty("campaign", out var c) ? c : root;
        var campaign = new CampaignState
        {
            Name = String(campaignNode, "name") ?? Path.GetFileNameWithoutExtension(sourceFile),
            System = String(campaignNode, "system") ?? "D&D 5E compatible / SRD 5.2.1",
            Summary = String(campaignNode, "summary") ?? "",
            Tone = String(campaignNode, "tone") ?? "",
            PartyName = root.TryGetProperty("party", out var party) ? String(party, "name") ?? "Adventuring Party" : "Adventuring Party"
        };

        var locationByKey = new Dictionary<string, WorldLocation>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var node in locations.EnumerateArray())
            {
                var key = String(node, "key") ?? $"location.{index + 1}";
                var location = new WorldLocation
                {
                    Key = key,
                    Name = String(node, "name") ?? key,
                    Type = String(node, "area_type") ?? String(node, "type") ?? "area",
                    Description = String(node, "description") ?? "",
                    DmOnly = Bool(node, "dm_only"),
                    SourceKind = String(node, "source_kind") ?? "source_canon",
                    X = Coordinate(node, "x", index, isY: false),
                    Y = Coordinate(node, "y", index, isY: true)
                };
                campaign.Locations.Add(location);
                locationByKey[key] = location;
                index++;
            }

            index = 0;
            foreach (var node in locations.EnumerateArray())
            {
                var key = String(node, "key") ?? $"location.{index + 1}";
                var parentKey = String(node, "parent_key");
                if (parentKey is not null && locationByKey.TryGetValue(parentKey, out var parent)) locationByKey[key].ParentId = parent.Id;
                else if (parentKey is not null) warnings.Add($"Location '{key}' references missing parent '{parentKey}'.");
                index++;
            }
        }

        if (root.TryGetProperty("discoveries", out var discoveries) && discoveries.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in discoveries.EnumerateArray())
            {
                if (!string.Equals(String(d, "subject"), "party", StringComparison.OrdinalIgnoreCase)) continue;
                var locationKey = String(d, "location_key");
                if (locationKey is not null && locationByKey.TryGetValue(locationKey, out var location)) location.Discovered = true;
            }
        }
        if (campaign.Locations.Count > 0 && !campaign.Locations.Any(l => l.Discovered && !l.DmOnly))
        {
            var firstPublic = campaign.Locations.FirstOrDefault(l => !l.DmOnly);
            if (firstPublic is not null) firstPublic.Discovered = true;
            else warnings.Add("All imported locations are marked DM-only; no player starting location could be selected.");
        }

        if (root.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in connections.EnumerateArray())
            {
                var from = String(n, "from_key"); var to = String(n, "to_key");
                if (from is null || to is null || !locationByKey.TryGetValue(from, out var fromLoc) || !locationByKey.TryGetValue(to, out var toLoc))
                {
                    warnings.Add($"Skipped a connection with an unresolved location reference: {from ?? "?"} -> {to ?? "?"}.");
                    continue;
                }
                campaign.Connections.Add(new LocationConnection
                {
                    FromLocationId = fromLoc.Id,
                    ToLocationId = toLoc.Id,
                    Label = String(n, "label") ?? "Path",
                    TravelMinutes = Int(n, "travel_minutes", 5),
                    Hidden = Bool(n, "hidden"),
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                });
            }
        }

        var spellByKey = new Dictionary<string, SpellDefinition>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("spells", out var spells) && spells.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in spells.EnumerateArray())
            {
                var key = String(n, "key") ?? Guid.NewGuid().ToString("N");
                var spell = new SpellDefinition
                {
                    Key = key,
                    Name = String(n, "name") ?? key,
                    Level = Math.Clamp(Int(n, "level", 0), 0, 9),
                    School = String(n, "school") ?? "",
                    CastingTime = String(n, "casting_time") ?? "Action",
                    RangeKind = String(n, "range_kind") ?? "distance",
                    RangeFeet = Math.Max(0, Int(n, "range_feet", Int(n, "range", 0))),
                    RequiresVerbal = Bool(n, "requires_verbal"),
                    RequiresSomatic = Bool(n, "requires_somatic"),
                    RequiresMaterial = Bool(n, "requires_material"),
                    MaterialDescription = String(n, "material_description") ?? "",
                    Duration = String(n, "duration") ?? "Instantaneous",
                    RequiresConcentration = Bool(n, "requires_concentration") || Bool(n, "concentration"),
                    Ritual = Bool(n, "ritual"),
                    RequiresTarget = Bool(n, "requires_target"),
                    Resolution = String(n, "resolution") ?? "utility",
                    SaveAbility = String(n, "save_ability") ?? "",
                    DamageExpression = String(n, "damage_expression") ?? String(n, "damage") ?? "",
                    DamageType = String(n, "damage_type") ?? "",
                    HalfDamageOnSuccessfulSave = Bool(n, "half_damage_on_successful_save") || Bool(n, "half_damage_on_save"),
                    HealingExpression = String(n, "healing_expression") ?? String(n, "healing") ?? "",
                    ExtraDamagePerSlotExpression = String(n, "extra_damage_per_slot_expression") ?? String(n, "extra_damage_per_slot") ?? "",
                    ExtraHealingPerSlotExpression = String(n, "extra_healing_per_slot_expression") ?? String(n, "extra_healing_per_slot") ?? "",
                    AddSpellcastingAbilityModifierToHealing = Bool(n, "add_spellcasting_ability_modifier_to_healing"),
                    CantripDamageScaling = Bool(n, "cantrip_damage_scaling"),
                    CantripRangeDoubling = Bool(n, "cantrip_range_doubling"),
                    IgnoreHalfAndThreeQuartersCoverOnSave = Bool(n, "ignore_half_and_three_quarters_cover_on_save"),
                    RequiredTargetCreatureType = String(n, "required_target_creature_type") ?? "",
                    ExcludedTargetCreatureType = String(n, "excluded_target_creature_type") ?? "",
                    ConditionsEndedOnTarget = String(n, "conditions_ended_on_target") ?? "",
                    ConditionOnFailedSave = String(n, "condition_on_failed_save") ?? "",
                    RepeatSaveAtEndOfTurn = Bool(n, "repeat_save_at_end_of_turn"),
                    NextAttackAgainstTargetHasAdvantage = Bool(n, "next_attack_against_target_has_advantage"),
                    EffectExpiresAtEndOfCasterNextTurn = Bool(n, "effect_expires_at_end_of_caster_next_turn"),
                    EffectExpiresAtStartOfCasterNextTurn = Bool(n, "effect_expires_at_start_of_caster_next_turn"),
                    SpeedModifierFeet = Int(n, "speed_modifier_feet", 0),
                    ArmorClassBonus = Int(n, "armor_class_bonus", 0),
                    SaveDisadvantageCreatureType = String(n, "save_disadvantage_creature_type") ?? "",
                    BaseProjectiles = Math.Max(0, Int(n, "base_projectiles", 0)),
                    ExtraProjectilesPerSlot = Math.Max(0, Int(n, "extra_projectiles_per_slot", 0)),
                    BaseTargets = Math.Max(0, Int(n, "base_targets", 0)),
                    ExtraTargetsPerSlot = Math.Max(0, Int(n, "extra_targets_per_slot", 0)),
                    AttackRollBonusExpression = String(n, "attack_roll_bonus_expression") ?? "",
                    SavingThrowBonusExpression = String(n, "saving_throw_bonus_expression") ?? "",
                    AreaShape = String(n, "area_shape") ?? "",
                    AreaSizeFeet = Math.Max(0, Int(n, "area_size_feet", 0)),
                    AreaWidthFeet = Math.Max(SpellAreaGeometry.DefaultLineWidthFeet, Int(n, "area_width_feet", SpellAreaGeometry.DefaultLineWidthFeet)),
                    ExtraAreaSizePerSlotFeet = Math.Max(0, Int(n, "extra_area_size_per_slot_feet", 0)),
                    AreaOrigin = String(n, "area_origin") ?? "",
                    PushFeetOnFailedSave = Math.Max(0, Int(n, "push_feet_on_failed_save", 0)),
                    EnvironmentalEffect = String(n, "environmental_effect") ?? "",
                    BattlefieldTrigger = String(n, "battlefield_trigger") ?? "none",
                    BattlefieldDifficultTerrain = Bool(n, "battlefield_difficult_terrain"),
                    BattlefieldHeavilyObscured = Bool(n, "battlefield_heavily_obscured"),
                    BattlefieldBlocksLineOfSight = Bool(n, "battlefield_blocks_line_of_sight"),
                    BattlefieldDurationRounds = Math.Max(0, Int(n, "battlefield_duration_rounds", 0)),
                    RequiresVisibleTarget = Bool(n, "requires_visible_target"),
                    SourceKind = String(n, "source_kind") ?? "source_canon",
                    SourcePage = Math.Max(0, Int(n, "source_page", 0)),
                    SourceReference = String(n, "source_reference") ?? ""
                };
                campaign.Spells.Add(spell);
                spellByKey[key] = spell;
            }
        }

        var characterByKey = new Dictionary<string, CharacterSheet>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("characters", out var characters) && characters.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in characters.EnumerateArray())
            {
                var key = String(n, "key") ?? Guid.NewGuid().ToString("N");
                var locationKey = String(n, "location_key");
                var character = new CharacterSheet
                {
                    Key = key,
                    Name = String(n, "name") ?? key,
                    CharacterType = String(n, "character_type") ?? "npc",
                    CreatureType = String(n, "creature_type") ?? "",
                    Level = Int(n, "level", 1),
                    ArmorClass = Int(n, "armor_class", 10),
                    MaxHp = Int(n, "max_hp", 10),
                    CurrentHp = Int(n, "current_hp", Int(n, "max_hp", 10)),
                    TempHp = Int(n, "temp_hp", 0),
                    Gold = WalletGp(n),
                    LocationId = locationKey is not null && locationByKey.TryGetValue(locationKey, out var loc) ? loc.Id : null,
                    PublicKnowledge = String(n, "public_knowledge") ?? "",
                    SecretKnowledge = String(n, "secret_knowledge") ?? "",
                    Speed = Int(n, "speed", 30),
                    Size = String(n, "size") ?? "Medium",
                    FreeHands = Math.Clamp(Int(n, "free_hands", 1), 0, 4),
                    ProficiencyBonus = Int(n, "proficiency_bonus", ProficiencyBonusForLevel(Int(n, "level", 1))),
                    SpellcastingAbility = String(n, "spellcasting_ability") ?? "intelligence",
                    CanProvideVerbalComponents = !n.TryGetProperty("can_provide_verbal_components", out var verbalComponents) || verbalComponents.ValueKind != JsonValueKind.False,
                    CanProvideSomaticComponents = !n.TryGetProperty("can_provide_somatic_components", out var somaticComponents) || somaticComponents.ValueKind != JsonValueKind.False,
                    CanProvideMaterialComponents = !n.TryGetProperty("can_provide_material_components", out var materialComponents) || materialComponents.ValueKind != JsonValueKind.False,
                    ExhaustionLevel = Math.Clamp(Int(n, "exhaustion", Int(n, "exhaustion_level", 0)), 0, 6),
                    HitDieSides = Math.Max(2, Int(n, "hit_die_sides", 8)),
                    HitDiceMaximum = Math.Max(1, Int(n, "hit_dice_maximum", Int(n, "level", 1))),
                    HitDiceRemaining = Math.Max(0, Int(n, "hit_dice_remaining", Int(n, "hit_dice_maximum", Int(n, "level", 1)))),
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                character.HitDiceRemaining = Math.Min(character.HitDiceRemaining, character.HitDiceMaximum);
                if (n.TryGetProperty("abilities", out var abilities) && abilities.ValueKind == JsonValueKind.Object)
                    foreach (var a in abilities.EnumerateObject()) if (a.Value.TryGetInt32(out var v)) character.Abilities[a.Name] = v;
                AddStrings(n, "saving_throw_proficiencies", character.SavingThrowProficiencies);
                AddStrings(n, "skill_proficiencies", character.SkillProficiencies);
                AddStrings(n, "tool_proficiencies", character.ToolProficiencies);
                AddStrings(n, "conditions", character.Conditions);
                AddStrings(n, "damage_resistances", character.DamageResistances);
                AddStrings(n, "damage_vulnerabilities", character.DamageVulnerabilities);
                AddStrings(n, "damage_immunities", character.DamageImmunities);
                ParseSpellSlots(n, character);
                ParsePreparedSpells(n, character, spellByKey, warnings);
                ParseResources(n, character);
                ParseAttacks(n, character);
                campaign.Characters.Add(character);
                characterByKey[key] = character;
            }
        }

        var itemByKey = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in items.EnumerateArray())
            {
                var key = String(n, "key") ?? Guid.NewGuid().ToString("N");
                var item = new ItemDefinition
                {
                    Key = key,
                    Name = String(n, "name") ?? key,
                    Category = String(n, "category") ?? "gear",
                    Description = String(n, "description") ?? "",
                    PriceGp = PriceGp(n),
                    Consumable = Bool(n, "consumable"),
                    Equippable = Bool(n, "equippable"),
                    EquipmentSlot = String(n, "equipment_slot") ?? "",
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                campaign.Items.Add(item); itemByKey[key] = item;
            }
        }

        if (root.TryGetProperty("characters", out var inventoryCharacters) && inventoryCharacters.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in inventoryCharacters.EnumerateArray())
            {
                var key = String(n, "key");
                if (key is null || !characterByKey.TryGetValue(key, out var character) || !n.TryGetProperty("inventory", out var inventory) || inventory.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in inventory.EnumerateArray())
                {
                    var itemKey = String(entry, "item_key");
                    if (itemKey is null || !itemByKey.TryGetValue(itemKey, out var item))
                    {
                        warnings.Add($"Character '{character.Name}' references missing inventory item '{itemKey ?? "?"}'.");
                        continue;
                    }
                    character.Inventory.Add(new InventoryEntry
                    {
                        ItemId = item.Id,
                        Quantity = Math.Max(1, Int(entry, "quantity", 1)),
                        Equipped = Bool(entry, "equipped")
                    });
                }
            }
        }

        var merchantByKey = new Dictionary<string, Merchant>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("merchants", out var merchants) && merchants.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in merchants.EnumerateArray())
            {
                var locKey = String(n, "location_key"); var npcKey = String(n, "npc_key");
                var merchant = new Merchant
                {
                    Key = String(n, "key") ?? Guid.NewGuid().ToString("N"),
                    Name = String(n, "name") ?? "Merchant",
                    Gold = WalletGp(n),
                    LocationId = locKey is not null && locationByKey.TryGetValue(locKey, out var loc) ? loc.Id : null,
                    NpcId = npcKey is not null && characterByKey.TryGetValue(npcKey, out var npc) ? npc.Id : null,
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                if (n.TryGetProperty("stock", out var stock) && stock.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in stock.EnumerateArray())
                    {
                        var itemKey = String(s, "item_key");
                        if (itemKey is null || !itemByKey.TryGetValue(itemKey, out var item)) { warnings.Add($"Merchant '{merchant.Name}' references missing item '{itemKey ?? "?"}'."); continue; }
                        merchant.Stock.Add(new MerchantStockEntry { ItemId = item.Id, Quantity = Int(s, "quantity", 1), PriceGp = s.TryGetProperty("price", out var price) ? NestedGp(price) : null, SourceKind = String(s, "source_kind") ?? String(n, "source_kind") ?? "source_canon" });
                    }
                }
                campaign.Merchants.Add(merchant);
                merchantByKey[merchant.Key] = merchant;
            }
        }

        if (root.TryGetProperty("quests", out var quests) && quests.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in quests.EnumerateArray())
            {
                var quest = new Quest
                {
                    Key = String(n, "key") ?? Guid.NewGuid().ToString("N"),
                    Name = String(n, "name") ?? "Quest",
                    Status = String(n, "status") ?? "available",
                    Summary = String(n, "summary") ?? "",
                    DmNotes = String(n, "dm_notes") ?? "",
                    RewardGp = n.TryGetProperty("rewards", out var rewards) ? NestedGp(rewards) ?? 0 : 0,
                    DmOnly = Bool(n, "dm_only") || Bool(n, "hidden"),
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                if (n.TryGetProperty("objectives", out var objectives) && objectives.ValueKind == JsonValueKind.Array)
                    quest.Objectives.AddRange(objectives.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                campaign.Quests.Add(quest);
            }
        }

        var factionByKey = new Dictionary<string, Faction>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("factions", out var factions) && factions.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in factions.EnumerateArray())
            {
                var faction = new Faction
                {
                    Key = String(n, "key") ?? Guid.NewGuid().ToString("N"),
                    Name = String(n, "name") ?? "Faction",
                    Summary = String(n, "summary") ?? "",
                    PublicKnowledge = String(n, "public_knowledge") ?? "",
                    SecretKnowledge = String(n, "secret_knowledge") ?? "",
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                campaign.Factions.Add(faction);
                factionByKey[faction.Key] = faction;
            }
        }

        var validEntityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in characterByKey.Keys) validEntityKeys.Add(key);
        foreach (var key in factionByKey.Keys) validEntityKeys.Add(key);
        foreach (var key in merchantByKey.Keys) validEntityKeys.Add(key);
        foreach (var quest in campaign.Quests) if (!string.IsNullOrWhiteSpace(quest.Key)) validEntityKeys.Add(quest.Key);
        foreach (var location in locationByKey.Keys) validEntityKeys.Add(location);
        foreach (var item in itemByKey.Keys) validEntityKeys.Add(item);

        if (root.TryGetProperty("relationships", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in relationships.EnumerateArray())
            {
                var sourceKey = String(n, "source_key") ?? "";
                var targetKey = String(n, "target_key") ?? "";
                if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
                {
                    warnings.Add("Skipped a relationship missing source_key or target_key.");
                    continue;
                }
                if (!validEntityKeys.Contains(sourceKey)) warnings.Add($"Relationship references missing source '{sourceKey}'.");
                if (!validEntityKeys.Contains(targetKey)) warnings.Add($"Relationship references missing target '{targetKey}'.");
                campaign.Relationships.Add(new EntityRelationship
                {
                    SourceKey = sourceKey,
                    TargetKey = targetKey,
                    Relation = String(n, "relation") ?? "related_to",
                    Strength = Double(n, "strength", 1.0),
                    Public = Bool(n, "public"),
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                });
            }
        }

        if (root.TryGetProperty("secrets", out var secrets) && secrets.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in secrets.EnumerateArray())
            {
                var secret = new CampaignSecret
                {
                    Key = String(n, "key") ?? Guid.NewGuid().ToString("N"),
                    Title = String(n, "title") ?? String(n, "name") ?? "Secret",
                    Truth = String(n, "truth") ?? String(n, "summary") ?? "",
                    Revealed = Bool(n, "revealed"),
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                AddStrings(n, "known_by", secret.KnownByKeys);
                AddStrings(n, "reveal_conditions", secret.RevealConditions);
                foreach (var key in secret.KnownByKeys.Where(k => !validEntityKeys.Contains(k)))
                    warnings.Add($"Secret '{secret.Title}' says unknown entity '{key}' knows it.");
                campaign.Secrets.Add(secret);
            }
        }

        if (root.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in timeline.EnumerateArray())
            {
                var evt = new TimelineEvent
                {
                    Key = String(n, "key") ?? Guid.NewGuid().ToString("N"),
                    Name = String(n, "name") ?? "World Event",
                    TriggerType = String(n, "trigger_type") ?? "time",
                    DmNotes = String(n, "dm_notes") ?? "",
                    Resolved = Bool(n, "resolved"),
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                if (n.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
                {
                    evt.CampaignDay = Math.Max(1, Int(trigger, "campaign_day", 1));
                    evt.MinuteOfDay = Math.Clamp(Int(trigger, "minute_of_day", 0), 0, 1439);
                }
                if (n.TryGetProperty("effect", out var effect) && effect.ValueKind == JsonValueKind.Object)
                {
                    evt.EffectQuestKey = String(effect, "quest") ?? "";
                    evt.Consequence = String(effect, "consequence") ?? "";
                }
                if (!string.IsNullOrWhiteSpace(evt.EffectQuestKey) && !validEntityKeys.Contains(evt.EffectQuestKey))
                    warnings.Add($"Timeline event '{evt.Name}' references missing quest '{evt.EffectQuestKey}'.");
                if (!evt.TriggerType.Equals("time", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Timeline event '{evt.Name}' uses trigger type '{evt.TriggerType}', which is preserved but not automatically fired in the current alpha.");
                campaign.Timeline.Add(evt);
            }
        }

        if (root.TryGetProperty("encounters", out var encounters) && encounters.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in encounters.EnumerateArray())
            {
                var locationKey = String(n, "location_key");
                var encounter = new EncounterState
                {
                    Key = String(n, "key") ?? Guid.NewGuid().ToString("N"),
                    Name = String(n, "name") ?? "Encounter",
                    Summary = String(n, "summary") ?? "",
                    Status = String(n, "status") ?? "planned",
                    DmOnly = Bool(n, "dm_only") || Bool(n, "hidden"),
                    LocationId = locationKey is not null && locationByKey.TryGetValue(locationKey, out var encounterLocation) ? encounterLocation.Id : null,
                    SourceKind = String(n, "source_kind") ?? "source_canon"
                };
                if (locationKey is not null && encounter.LocationId is null)
                    warnings.Add($"Encounter '{encounter.Name}' references missing location '{locationKey}'.");

                if (n.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        var quantity = Math.Clamp(Int(member, "quantity", 1), 1, 50);
                        for (var copy = 1; copy <= quantity; copy++)
                        {
                            var memberKey = String(member, "key") ?? $"member.{encounter.Combatants.Count + 1}";
                            var baseName = String(member, "name") ?? memberKey;
                            var name = quantity == 1 ? baseName : $"{baseName} {copy}";
                            var initiativeModifier = Int(member, "initiative_modifier", 0);
                            var monster = new CharacterSheet
                            {
                                Key = $"{encounter.Key}.{memberKey}.{copy}",
                                Name = name,
                                CharacterType = String(member, "character_type") ?? "monster",
                                CreatureType = String(member, "creature_type") ?? "",
                                Level = Math.Max(1, Int(member, "level", 1)),
                                ArmorClass = Math.Max(1, Int(member, "armor_class", 10)),
                                MaxHp = Math.Max(1, Int(member, "max_hp", 1)),
                                CurrentHp = Math.Max(1, Int(member, "current_hp", Int(member, "max_hp", 1))),
                                Speed = Math.Max(0, Int(member, "speed", 30)),
                                Size = String(member, "size") ?? "Medium",
                                FreeHands = Math.Clamp(Int(member, "free_hands", 1), 0, 4),
                                LocationId = encounter.LocationId,
                                PublicKnowledge = String(member, "public_knowledge") ?? "",
                                SecretKnowledge = String(member, "secret_knowledge") ?? "",
                                ProficiencyBonus = Math.Max(0, Int(member, "proficiency_bonus", 2)),
                                SourceKind = String(n, "source_kind") ?? "source_canon",
                                Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["dexterity"] = Math.Clamp(10 + (2 * initiativeModifier), 1, 30)
                                }
                            };
                            monster.CurrentHp = Math.Min(monster.CurrentHp, monster.MaxHp);
                            ParseAttacks(member, monster);
                            if (member.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                            {
                                var attackBonus = Int(metadata, "attack_bonus", int.MinValue);
                                var damage = String(metadata, "damage");
                                if (attackBonus != int.MinValue && !string.IsNullOrWhiteSpace(damage))
                                {
                                    monster.Attacks.Add(new AttackProfile
                                    {
                                        Name = String(metadata, "attack_name") ?? "Attack",
                                        AttackBonus = attackBonus,
                                        DamageExpression = damage,
                                        DamageType = String(metadata, "damage_type") ?? ""
                                    });
                                }
                            }
                            campaign.Characters.Add(monster);
                            characterByKey[monster.Key] = monster;
                            encounter.Combatants.Add(new CombatantState { CharacterId = monster.Id, TieBreaker = encounter.Combatants.Count });
                        }
                    }
                }
                campaign.Encounters.Add(encounter);
            }
        }

        campaign.PartyLocationId = campaign.Characters.FirstOrDefault(x => x.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))?.LocationId
            ?? campaign.Locations.FirstOrDefault(x => x.Discovered && !x.DmOnly)?.Id
            ?? campaign.Locations.FirstOrDefault(x => !x.DmOnly)?.Id;
        campaign.Events.Add(new CampaignEvent { Type = "campaign_imported", Summary = $"Imported campaign from {sourceFile}." });
        return new CampaignImportResult(campaign, warnings, sourceFile);
    }

    public CampaignImportResult CompileText(string text, string sourceName)
    {
        var warnings = new List<string>();
        var name = FirstHeading(text) ?? Path.GetFileNameWithoutExtension(sourceName);
        var campaign = new CampaignState { Name = string.IsNullOrWhiteSpace(name) ? "Imported Campaign" : name.Trim(), Summary = FirstParagraph(text) };
        var locations = ExtractLabeled(text, new[] { "Location", "Area", "Place", "Town", "City", "Village", "Dungeon", "Room" });
        var npcs = ExtractLabeled(text, new[] { "NPC", "Character" });
        var quests = ExtractLabeled(text, new[] { "Quest", "Mission", "Objective" });

        var index = 0;
        foreach (var entry in locations.Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
        {
            campaign.Locations.Add(new WorldLocation
            {
                Key = $"inferred.location.{index + 1}", Name = entry, Type = "area", SourceKind = "inferred",
                X = 0.12 + (index % 5) * 0.18, Y = 0.18 + (index / 5) * 0.14, Discovered = index == 0
            });
            index++;
        }
        if (campaign.Locations.Count == 0)
        {
            campaign.Locations.Add(new WorldLocation { Key = "inferred.start", Name = "Starting Area", Type = "area", Description = "Imported source did not explicitly label locations. Review and expand this campaign before play.", X = .5, Y = .5, Discovered = true, SourceKind = "inferred" });
            warnings.Add("No explicit labeled locations were detected. A Starting Area placeholder was created.");
        }

        foreach (var entry in npcs.Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
            campaign.Characters.Add(new CharacterSheet { Key = $"inferred.npc.{campaign.Characters.Count + 1}", Name = entry, CharacterType = "npc", PublicKnowledge = SourceKnowledgeMarker(), SourceKind = "inferred" });

        foreach (var entry in quests.Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
            campaign.Quests.Add(new Quest { Key = $"inferred.quest.{campaign.Quests.Count + 1}", Name = entry, Status = "available", Summary = "Extracted from a labeled source heading. Review details before play.", SourceKind = "inferred" });

        campaign.PartyLocationId = campaign.Locations[0].Id;
        campaign.Events.Add(new CampaignEvent { Type = "campaign_compiled", Summary = $"Compiled campaign structure from {sourceName}." });
        return new CampaignImportResult(campaign, warnings, sourceName);
    }

    private static string ExtractPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        return string.Join("\n\n", document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
    }

    private static string ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX does not contain word/document.xml.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join("\n", doc.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string? FirstHeading(string text) => text.Replace("\r", "").Split('\n').Select(x => x.Trim().TrimStart('#').Trim()).FirstOrDefault(x => x.Length is > 2 and < 100);
    private static string FirstParagraph(string text) => string.Join(" ", text.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 20).Take(3)).Trim();

    private static IEnumerable<string> ExtractLabeled(string text, IEnumerable<string> labels)
    {
        var joined = string.Join("|", labels.Select(Regex.Escape));
        var rx = new Regex($@"(?im)^\s*(?:#+\s*)?(?:{joined})\s*[:\-]\s*(?<name>[^\r\n]{{2,100}})$");
        return rx.Matches(text).Select(m => m.Groups["name"].Value.Trim()).Where(x => x.Length > 1);
    }

    private static string? String(JsonElement node, string name) => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) && v.GetBoolean();
    private static int Int(JsonElement node, string name, int fallback = 0) => node.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;
    private static double Double(JsonElement node, string name, double fallback = 0) => node.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : fallback;
    private static int WalletGp(JsonElement node) => node.TryGetProperty("wallet", out var wallet) ? NestedGp(wallet) ?? 0 : 0;
    private static int PriceGp(JsonElement node) => node.TryGetProperty("price", out var price) ? NestedGp(price) ?? 0 : 0;
    private static int? NestedGp(JsonElement node) => node.TryGetProperty("gp", out var gp) && gp.TryGetInt32(out var i) ? i : null;
    private static double Coordinate(JsonElement node, string name, int index, bool isY)
    {
        if (node.TryGetProperty("map", out var map) && map.TryGetProperty(name, out var m) && m.TryGetDouble(out var md)) return md;
        if (node.TryGetProperty(name, out var direct) && direct.TryGetDouble(out var dd)) return dd;
        return isY ? 0.18 + (index / 5) * 0.14 : 0.12 + (index % 5) * 0.18;
    }

    private static int ProficiencyBonusForLevel(int level) => Math.Clamp(2 + (Math.Max(1, level) - 1) / 4, 2, 6);

    private static void AddStrings(JsonElement node, string name, ICollection<string> destination)
    {
        if (!node.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && !destination.Contains(text, StringComparer.OrdinalIgnoreCase)) destination.Add(text);
        }
    }

    private static void ParseSpellSlots(JsonElement node, CharacterSheet character)
    {
        if (!node.TryGetProperty("spell_slots", out var slots) || slots.ValueKind != JsonValueKind.Object) return;
        foreach (var property in slots.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var level) || level is < 1 or > 9) continue;
            var maximum = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var direct)
                ? direct
                : Int(property.Value, "maximum", Int(property.Value, "max", 0));
            var remaining = property.Value.ValueKind == JsonValueKind.Number
                ? maximum
                : Int(property.Value, "remaining", maximum);
            maximum = Math.Max(0, maximum);
            remaining = Math.Clamp(remaining, 0, maximum);
            character.SpellSlots[level] = new SpellSlotPool { Maximum = maximum, Remaining = remaining };
        }
    }

    private static void ParsePreparedSpells(JsonElement node, CharacterSheet character, IReadOnlyDictionary<string, SpellDefinition> spellByKey, ICollection<string> warnings)
    {
        if (!node.TryGetProperty("prepared_spells", out var prepared) || prepared.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in prepared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var key = entry.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!spellByKey.TryGetValue(key, out var spell))
            {
                warnings.Add($"Character '{character.Name}' references missing prepared spell '{key}'.");
                continue;
            }
            if (!character.PreparedSpellIds.Contains(spell.Id, StringComparer.OrdinalIgnoreCase))
                character.PreparedSpellIds.Add(spell.Id);
        }
    }

    private static void ParseResources(JsonElement node, CharacterSheet character)
    {
        if (!node.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array) return;
        foreach (var resource in resources.EnumerateArray())
        {
            var name = String(resource, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var maximum = Math.Max(0, Int(resource, "maximum", Int(resource, "max", 0)));
            character.Resources.Add(new ResourcePool
            {
                Name = name,
                Maximum = maximum,
                Remaining = Math.Clamp(Int(resource, "remaining", maximum), 0, maximum),
                RechargeOnShortRest = Bool(resource, "recharge_on_short_rest"),
                RechargeOnLongRest = !resource.TryGetProperty("recharge_on_long_rest", out var longRest) || longRest.ValueKind != JsonValueKind.False
            });
        }
    }

    private static void ParseAttacks(JsonElement node, CharacterSheet character)
    {
        if (!node.TryGetProperty("attacks", out var attacks) || attacks.ValueKind != JsonValueKind.Array) return;
        foreach (var attack in attacks.EnumerateArray())
        {
            var name = String(attack, "name") ?? "Attack";
            var damage = String(attack, "damage_expression") ?? String(attack, "damage");
            if (string.IsNullOrWhiteSpace(damage)) continue;
            character.Attacks.Add(new AttackProfile
            {
                Name = name,
                AttackBonus = Int(attack, "attack_bonus", Int(attack, "to_hit", 0)),
                DamageExpression = damage,
                DamageType = String(attack, "damage_type") ?? "",
                ReachFeet = Math.Max(0, Int(attack, "reach_feet", Int(attack, "reach", 5))),
                RangeFeet = Math.Max(0, Int(attack, "range_feet", Int(attack, "range", 0)))
            });
        }
    }

    private static string SourceKnowledgeMarker() => "Extracted from source label; semantic details not yet compiled.";
}
