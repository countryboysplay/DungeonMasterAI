using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

internal sealed class PlayerProjectileSpellSequenceState
{
    public string CasterId { get; set; } = "";
    public string SpellId { get; set; } = "";
    public int CastAtLevel { get; set; }
    public bool UsedSpellSlot { get; set; }
    public bool ConcentrationStarted { get; set; }
    public string? EncounterId { get; set; }
    public List<string> TargetIds { get; set; } = [];
    public int NextProjectileIndex { get; set; }
    public List<SpellTargetResolution> Results { get; set; } = [];
}

public sealed partial class GameEngine
{
    private SpellCastResult BeginPlayerProjectileSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        bool concentrationStarted,
        EncounterState? encounter,
        IReadOnlyList<string> allocations,
        DiceService dice)
    {
        var state = new PlayerProjectileSpellSequenceState
        {
            CasterId = caster.Id,
            SpellId = spell.Id,
            CastAtLevel = castAtLevel,
            UsedSpellSlot = usedSlot,
            ConcentrationStarted = concentrationStarted,
            EncounterId = encounter?.Id,
            TargetIds = allocations.ToList(),
            NextProjectileIndex = 0,
            Results = []
        };
        return AdvancePlayerProjectileSpellSequence(campaign, state, dice);
    }

    public SpellCastResult ResolvePendingProjectileSpellAttackRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && rollTwo.Value is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollTwo));

        var pending = RequireProjectilePending(campaign, pendingRollId, "projectile_spell_attack");
        var state = ReadProjectileSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireProjectileSpell(campaign, state.SpellId);
        var projectileIndex = RequireCurrentProjectileIndex(state, pending);
        var target = RequireCharacter(campaign, state.TargetIds[projectileIndex]);
        var encounter = RequireProjectileEncounterIfAny(campaign, state, caster);
        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));

        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This projectile spell attack requires two d20 results because it has {mode}.");
        var chosen = mode switch
        {
            D20RollMode.Advantage => Math.Max(rollOne, rollTwo!.Value),
            D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo!.Value),
            _ => rollOne
        };

        var armorClass = pending.TargetNumber ?? EffectiveArmorClass(campaign, target);
        var effectAttackBonus = RollActiveAttackBonus(campaign, caster.Id, dice);
        var totalModifier = SpellAttackModifier(caster) + effectAttackBonus;
        var total = chosen + totalModifier;
        var naturalCritical = chosen == 20;
        var automaticCritical = ProjectileContextBool(pending, "automatic_critical");
        var hit = naturalCritical || (chosen != 1 && total >= armorClass);
        var critical = hit && (naturalCritical || automaticCritical);
        var helpUsed = encounter is not null && casterCombatant is not null && targetCombatant is not null
            && ConsumeHelpAttackAdvantage(encounter, casterCombatant, targetCombatant);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        if (encounter is not null && casterCombatant is not null)
            BreakHidden(campaign, encounter, casterCombatant, "making a projectile spell attack roll");

        var coverBonus = ProjectileContextInt(pending, "cover_bonus");
        var attackSummary = BuildProjectileAttackSummary(
            projectileIndex + 1,
            target.Name,
            mode,
            total,
            armorClass,
            coverBonus,
            effectAttackBonus,
            hit,
            critical,
            helpUsed);
        var attack = new AttackResult(chosen, totalModifier, total, hit, critical, 0, attackSummary);

        if (!hit)
        {
            state.Results.Add(new SpellTargetResolution(target.Id, target.Name, projectileIndex + 1, attack, null, null, 0, attackSummary));
            state.NextProjectileIndex++;
            campaign.PendingPlayerRoll = null;
            return AdvancePlayerProjectileSpellSequence(campaign, state, dice);
        }

        var damagePending = new PendingRollRequest
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter?.Id,
            CombatantId = casterCombatant?.Id,
            Formula = critical ? $"{spell.DamageExpression} (critical dice)" : spell.DamageExpression,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"Projectile {projectileIndex + 1} from {caster.Name}'s {spell.Name} hit {target.Name}{(critical ? " critically" : "")}. Roll {spell.DamageExpression}{(critical ? " with critical dice" : "")} damage.",
            ResolutionKey = "projectile_spell_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
                ["projectile_index"] = projectileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["target_id"] = target.Id,
                ["base_damage_expression"] = spell.DamageExpression,
                ["damage_type"] = spell.DamageType,
                ["critical"] = critical ? "true" : "false",
                ["attack_d20"] = chosen.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_modifier"] = totalModifier.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["armor_class"] = armorClass.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_mode"] = mode.ToString().ToLowerInvariant(),
                ["effect_attack_bonus"] = effectAttackBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["help_used"] = helpUsed ? "true" : "false"
            }
        };
        campaign.PendingPlayerRoll = damagePending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", damagePending.Purpose, dmOnly: true);
        return BuildProjectileSequenceResult(campaign, state, $"{attackSummary} {damagePending.Purpose}");
    }

    public SpellCastResult ResolvePendingProjectileSpellDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int damageAmount,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (damageAmount is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(damageAmount));

        var pending = RequireProjectilePending(campaign, pendingRollId, "projectile_spell_damage");
        var state = ReadProjectileSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireProjectileSpell(campaign, state.SpellId);
        var projectileIndex = RequireCurrentProjectileIndex(state, pending);
        var target = RequireCharacter(campaign, state.TargetIds[projectileIndex]);
        _ = RequireProjectileEncounterIfAny(campaign, state, caster);

        var critical = ProjectileContextBool(pending, "critical");
        var attackD20 = ProjectileContextInt(pending, "attack_d20");
        var attackModifier = ProjectileContextInt(pending, "attack_modifier");
        var attackTotal = ProjectileContextInt(pending, "attack_total");
        var armorClass = ProjectileContextInt(pending, "armor_class");
        var coverBonus = ProjectileContextInt(pending, "cover_bonus");
        var mode = ParsePendingRollMode(pending.Context.TryGetValue("attack_mode", out var attackMode) ? attackMode : null);
        var effectAttackBonus = ProjectileContextInt(pending, "effect_attack_bonus");
        var helpUsed = ProjectileContextBool(pending, "help_used");
        var damageType = pending.Context.TryGetValue("damage_type", out var storedType) ? storedType : spell.DamageType;

        campaign.PendingPlayerRoll = null;
        DamageResolutionResult? damage = null;
        if (!target.Dead)
            damage = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, damageType, critical);

        var attackSummary = BuildProjectileAttackSummary(
            projectileIndex + 1,
            target.Name,
            mode,
            attackTotal,
            armorClass,
            coverBonus,
            effectAttackBonus,
            true,
            critical,
            helpUsed,
            damageAmount,
            damageType);
        var attack = new AttackResult(attackD20, attackModifier, attackTotal, true, critical, damageAmount, attackSummary);
        var projectileSummary = target.Dead && damage is null
            ? $"Projectile {projectileIndex + 1} was already allocated to {target.Name}; the target had been reduced to death by an earlier declared projectile before this damage instance was applied."
            : attackSummary + (damage?.Concentration is null ? "" : $" {damage.Concentration.Summary}");
        state.Results.Add(new SpellTargetResolution(target.Id, target.Name, projectileIndex + 1, attack, null, damage, 0, projectileSummary));
        state.NextProjectileIndex++;

        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
        {
            campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "projectile_spell_sequence";
            campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);
            Touch(campaign);
            var waitSummary = $"{projectileSummary} Resolve {target.Name}'s Concentration save before projectile {state.NextProjectileIndex + 1} can continue.";
            return BuildProjectileSequenceResult(campaign, state, waitSummary);
        }

        return AdvancePlayerProjectileSpellSequence(campaign, state, dice);
    }

    private SpellCastResult AdvancePlayerProjectileSpellSequence(
        CampaignState campaign,
        PlayerProjectileSpellSequenceState state,
        DiceService dice)
    {
        if (state.NextProjectileIndex >= state.TargetIds.Count)
            return FinalizePlayerProjectileSpellSequence(campaign, state);

        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireProjectileSpell(campaign, state.SpellId);
        var target = RequireCharacter(campaign, state.TargetIds[state.NextProjectileIndex]);
        var encounter = RequireProjectileEncounterIfAny(campaign, state, caster);
        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var coverBonus = GetSpellCoverBonus(encounter, caster.Id, target.Id);
        var armorClass = EffectiveArmorClass(campaign, target) + coverBonus;
        var mode = encounter is not null && casterCombatant is not null && targetCombatant is not null
            ? AttackRollMode(campaign, encounter, casterCombatant, targetCombatant, caster, target)
            : D20RollMode.Normal;
        var automaticCritical = casterCombatant is not null && targetCombatant is not null
            && IsAutomaticCriticalHitTarget(casterCombatant, targetCombatant, target);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var coverText = coverBonus > 0 ? $" including {CoverLabel(coverBonus)} Cover" : "";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter?.Id,
            CombatantId = casterCombatant?.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"Projectile {state.NextProjectileIndex + 1} of {caster.Name}'s {spell.Name}: roll the spell attack d20{modeText} against {target.Name}, AC {armorClass}{coverText}.",
            ResolutionKey = "projectile_spell_attack",
            Modifier = SpellAttackModifier(caster),
            TargetNumber = armorClass,
            TargetLabel = $"AC {armorClass}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
                ["projectile_index"] = state.NextProjectileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["target_id"] = target.Id,
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automatic_critical"] = automaticCritical ? "true" : "false"
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return BuildProjectileSequenceResult(campaign, state, pending.Purpose);
    }

    private SpellCastResult FinalizePlayerProjectileSpellSequence(CampaignState campaign, PlayerProjectileSpellSequenceState state)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireProjectileSpell(campaign, state.SpellId);
        var distinctTargets = state.TargetIds
            .Select(id => RequireCharacter(campaign, id).Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var slotText = spell.Level == 0 ? "as a cantrip" : $"using a level {state.CastAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}, resolving {state.TargetIds.Count} projectile{(state.TargetIds.Count == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. "
            + string.Join(" ", state.Results.Select(r => r.Summary));
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return BuildProjectileSequenceResult(campaign, state, summary);
    }

    private SpellCastResult BuildProjectileSequenceResult(
        CampaignState campaign,
        PlayerProjectileSpellSequenceState state,
        string summary)
    {
        var spell = RequireProjectileSpell(campaign, state.SpellId);
        var uniqueTargetIds = state.TargetIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            state.CasterId,
            uniqueTargetIds.Length == 1 ? uniqueTargetIds[0] : null,
            state.CastAtLevel,
            state.UsedSpellSlot,
            false,
            null,
            null,
            null,
            0,
            state.ConcentrationStarted,
            summary,
            state.Results.ToArray());
    }

    private string? ResumePlayerProjectileSpellSequenceAfterConcentration(
        CampaignState campaign,
        IReadOnlyDictionary<string, string> continuationContext,
        DiceService dice)
    {
        if (!continuationContext.TryGetValue("continuation_sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The projectile spell continuation is missing its sequence state.");
        var state = JsonSerializer.Deserialize<PlayerProjectileSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The projectile spell continuation could not be restored.");
        var result = AdvancePlayerProjectileSpellSequence(campaign, state, dice);
        return result.Summary;
    }

    private static PendingRollRequest RequireProjectilePending(CampaignState campaign, string pendingRollId, string resolutionKey)
    {
        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals(resolutionKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not '{resolutionKey}'.");
        return pending;
    }

    private static PlayerProjectileSpellSequenceState ReadProjectileSequence(PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The pending projectile roll is missing its sequence state.");
        return JsonSerializer.Deserialize<PlayerProjectileSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The pending projectile sequence could not be restored.");
    }

    private static int RequireCurrentProjectileIndex(PlayerProjectileSpellSequenceState state, PendingRollRequest pending)
    {
        var pendingIndex = ProjectileContextInt(pending, "projectile_index", -1);
        if (pendingIndex < 0 || pendingIndex >= state.TargetIds.Count || pendingIndex != state.NextProjectileIndex)
            throw new InvalidOperationException("The pending projectile index no longer matches the saved sequence.");
        return pendingIndex;
    }

    private EncounterState? RequireProjectileEncounterIfAny(CampaignState campaign, PlayerProjectileSpellSequenceState state, CharacterSheet caster)
    {
        if (string.IsNullOrWhiteSpace(state.EncounterId)) return null;
        var encounter = RequireEncounter(campaign, state.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The projectile spell's encounter is no longer active.");
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The projectile spellcaster is no longer in the encounter.");
        EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
    }

    private static SpellDefinition RequireProjectileSpell(CampaignState campaign, string spellId)
    {
        return campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The spell for the pending projectile sequence no longer exists.");
    }

    private static string BuildProjectileAttackSummary(
        int sequence,
        string targetName,
        D20RollMode mode,
        int total,
        int armorClass,
        int coverBonus,
        int effectAttackBonus,
        bool hit,
        bool critical,
        bool helpUsed,
        int? damageAmount = null,
        string? damageType = null)
    {
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var coverText = coverBonus > 0 ? $" with {CoverLabel(coverBonus)} Cover (+{coverBonus} AC)" : "";
        var effectText = effectAttackBonus == 0 ? "" : $" +{effectAttackBonus} from active effects";
        var helpText = helpUsed ? " Help supplied Advantage." : "";
        if (!hit)
            return $"Projectile {sequence} against {targetName}: spell attack{modeText} {total} vs AC {armorClass}{coverText}{effectText}: miss.{helpText}";
        var damageText = damageAmount.HasValue
            ? $" for {damageAmount.Value}{(string.IsNullOrWhiteSpace(damageType) ? "" : $" {damageType}")} damage"
            : "";
        return $"Projectile {sequence} against {targetName}: spell attack{modeText} {total} vs AC {armorClass}{coverText}{effectText}: hit{damageText}{(critical ? " (critical)" : "")}.{helpText}";
    }

    private static int ProjectileContextInt(PendingRollRequest pending, string key, int fallback = 0)
    {
        return pending.Context.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool ProjectileContextBool(PendingRollRequest pending, string key)
    {
        return pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
    }
}
