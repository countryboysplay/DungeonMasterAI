using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

public sealed class SrdSpellCatalogService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private IReadOnlyList<SpellDefinition> _spells = [];

    public int Count => _spells.Count;
    public IReadOnlyList<SpellDefinition> Spells => _spells;

    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            _spells = [];
            return;
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("spells", out var spellsNode) || spellsNode.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SRD spell catalog does not contain a spells array.");

        var spells = new List<SpellDefinition>();
        foreach (var node in spellsNode.EnumerateArray())
        {
            var key = String(node, "key");
            var name = String(node, "name");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) continue;
            spells.Add(new SpellDefinition
            {
                Id = key,
                Key = key,
                Name = name,
                Level = Math.Clamp(Int(node, "level"), 0, 9),
                School = String(node, "school") ?? "",
                CastingTime = String(node, "casting_time") ?? "Action",
                RangeKind = String(node, "range_kind") ?? "distance",
                RangeFeet = Math.Max(0, Int(node, "range_feet")),
                RequiresVerbal = Bool(node, "requires_verbal"),
                RequiresSomatic = Bool(node, "requires_somatic"),
                RequiresMaterial = Bool(node, "requires_material"),
                MaterialDescription = String(node, "material_description") ?? "",
                Duration = String(node, "duration") ?? "Instantaneous",
                RequiresConcentration = Bool(node, "requires_concentration"),
                Ritual = Bool(node, "ritual"),
                RequiresTarget = Bool(node, "requires_target"),
                Resolution = String(node, "resolution") ?? "unsupported",
                SaveAbility = String(node, "save_ability") ?? "",
                DamageExpression = String(node, "damage_expression") ?? "",
                DamageType = String(node, "damage_type") ?? "",
                HalfDamageOnSuccessfulSave = Bool(node, "half_damage_on_successful_save"),
                HealingExpression = String(node, "healing_expression") ?? "",
                ExtraDamagePerSlotExpression = String(node, "extra_damage_per_slot_expression") ?? String(node, "extra_damage_per_slot") ?? "",
                ExtraHealingPerSlotExpression = String(node, "extra_healing_per_slot_expression") ?? String(node, "extra_healing_per_slot") ?? "",
                AddSpellcastingAbilityModifierToHealing = Bool(node, "add_spellcasting_ability_modifier_to_healing"),
                CantripDamageScaling = Bool(node, "cantrip_damage_scaling"),
                CantripRangeDoubling = Bool(node, "cantrip_range_doubling"),
                IgnoreHalfAndThreeQuartersCoverOnSave = Bool(node, "ignore_half_and_three_quarters_cover_on_save"),
                RequiredTargetCreatureType = String(node, "required_target_creature_type") ?? "",
                ConditionOnFailedSave = String(node, "condition_on_failed_save") ?? "",
                RepeatSaveAtEndOfTurn = Bool(node, "repeat_save_at_end_of_turn"),
                NextAttackAgainstTargetHasAdvantage = Bool(node, "next_attack_against_target_has_advantage"),
                EffectExpiresAtEndOfCasterNextTurn = Bool(node, "effect_expires_at_end_of_caster_next_turn"),
                EffectExpiresAtStartOfCasterNextTurn = Bool(node, "effect_expires_at_start_of_caster_next_turn"),
                SpeedModifierFeet = Int(node, "speed_modifier_feet"),
                ArmorClassBonus = Int(node, "armor_class_bonus"),
                SaveDisadvantageCreatureType = String(node, "save_disadvantage_creature_type") ?? "",
                BaseProjectiles = Math.Max(0, Int(node, "base_projectiles")),
                ExtraProjectilesPerSlot = Math.Max(0, Int(node, "extra_projectiles_per_slot")),
                BaseTargets = Math.Max(0, Int(node, "base_targets")),
                ExtraTargetsPerSlot = Math.Max(0, Int(node, "extra_targets_per_slot")),
                AttackRollBonusExpression = String(node, "attack_roll_bonus_expression") ?? "",
                SavingThrowBonusExpression = String(node, "saving_throw_bonus_expression") ?? "",
                AreaShape = String(node, "area_shape") ?? "",
                AreaSizeFeet = Math.Max(0, Int(node, "area_size_feet")),
                ExtraAreaSizePerSlotFeet = Math.Max(0, Int(node, "extra_area_size_per_slot_feet")),
                AreaOrigin = String(node, "area_origin") ?? "",
                PushFeetOnFailedSave = Math.Max(0, Int(node, "push_feet_on_failed_save")),
                EnvironmentalEffect = String(node, "environmental_effect") ?? "",
                BattlefieldTrigger = String(node, "battlefield_trigger") ?? "none",
                BattlefieldDifficultTerrain = Bool(node, "battlefield_difficult_terrain"),
                BattlefieldHeavilyObscured = Bool(node, "battlefield_heavily_obscured"),
                BattlefieldBlocksLineOfSight = Bool(node, "battlefield_blocks_line_of_sight"),
                BattlefieldDurationRounds = Math.Max(0, Int(node, "battlefield_duration_rounds")),
                RequiresVisibleTarget = Bool(node, "requires_visible_target"),
                SourceKind = String(node, "source_kind") ?? "srd_5_2_1",
                SourcePage = Math.Max(0, Int(node, "source_page")),
                SourceReference = String(node, "source_reference") ?? "SRD 5.2.1"
            });
        }

        _spells = spells
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.Level)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int MergeInto(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var changed = 0;
        foreach (var source in _spells)
        {
            var index = campaign.Spells.FindIndex(s => !string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(source.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                campaign.Spells.Add(Clone(source));
                changed++;
                continue;
            }

            // Refresh previously imported SRD metadata so newly implemented deterministic
            // mechanics become available in existing campaigns without overwriting a
            // campaign-authored spell that intentionally reused the same display name.
            var existing = campaign.Spells[index];
            if (existing.SourceKind.Equals("srd_5_2_1", StringComparison.OrdinalIgnoreCase)
                || existing.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase))
            {
                campaign.Spells[index] = Clone(source);
                changed++;
            }
        }
        return changed;
    }

    private SpellDefinition Clone(SpellDefinition source) =>
        JsonSerializer.Deserialize<SpellDefinition>(JsonSerializer.Serialize(source, _json), _json)
        ?? throw new InvalidDataException("SRD spell metadata could not be cloned.");

    private static string? String(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Int(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static bool Bool(JsonElement node, string name) => node.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();
}
