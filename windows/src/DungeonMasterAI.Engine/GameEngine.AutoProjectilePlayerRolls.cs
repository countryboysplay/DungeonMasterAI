using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

internal sealed class PlayerAutoProjectileSpellSequenceState
{
    public string CasterId { get; set; } = "";
    public string SpellId { get; set; } = "";
    public int CastAtLevel { get; set; }
    public bool UsedSpellSlot { get; set; }
    public bool ConcentrationStarted { get; set; }
    public string? EncounterId { get; set; }
    public bool ReadiedReaction { get; set; }
    public bool AutomaticCasterRolls { get; set; }
    public List<string> TargetIds { get; set; } = [];
    public int NextProjectileIndex { get; set; }
    public List<SpellTargetResolution> Results { get; set; } = [];
}

public sealed partial class GameEngine
{
    private SpellCastResult BeginPlayerAutoProjectileSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        bool concentrationStarted,
        EncounterState? encounter,
        IReadOnlyList<string> allocations,
        DiceService dice,
        bool readiedReaction = false)
    {
        var state = new PlayerAutoProjectileSpellSequenceState
        {
            CasterId = caster.Id,
            SpellId = spell.Id,
            CastAtLevel = castAtLevel,
            UsedSpellSlot = usedSlot,
            ConcentrationStarted = concentrationStarted,
            EncounterId = encounter?.Id,
            ReadiedReaction = readiedReaction,
            AutomaticCasterRolls = false,
            TargetIds = allocations.ToList(),
            NextProjectileIndex = 0,
            Results = []
        };
        return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);
    }

    private SpellCastResult BeginAutomaticReadiedAutoProjectileSpellSequence(
        CampaignState campaign,
        CharacterSheet caster,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        bool concentrationStarted,
        EncounterState encounter,
        IReadOnlyList<string> allocations,
        DiceService dice)
    {
        var state = new PlayerAutoProjectileSpellSequenceState
        {
            CasterId = caster.Id,
            SpellId = spell.Id,
            CastAtLevel = castAtLevel,
            UsedSpellSlot = usedSlot,
            ConcentrationStarted = concentrationStarted,
            EncounterId = encounter.Id,
            ReadiedReaction = true,
            AutomaticCasterRolls = true,
            TargetIds = allocations.ToList(),
            NextProjectileIndex = 0,
            Results = []
        };
        return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);
    }

    public SpellCastResult ResolvePendingAutoProjectileSpellDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int damageAmount,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (damageAmount is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(damageAmount));

        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied damage roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals("projectile_auto_damage", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not auto-projectile damage.");

        var state = ReadAutoProjectileSequence(pending);
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAutoProjectileSpell(campaign, state.SpellId);
        var projectileIndex = RequireCurrentAutoProjectileIndex(state, pending);
        var target = RequireCharacter(campaign, state.TargetIds[projectileIndex]);
        _ = RequireAutoProjectileEncounterIfAny(campaign, state, caster);
        var damageType = pending.Context.TryGetValue("damage_type", out var storedType) ? storedType : spell.DamageType;

        campaign.PendingPlayerRoll = null;
        DamageResolutionResult? damage = null;
        if (!target.Dead)
            damage = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, damageType);

        var summary = target.Dead && damage is null
            ? $"Projectile {projectileIndex + 1} was already allocated to {target.Name}; the target had been reduced to death by an earlier declared projectile before this damage instance was applied."
            : $"Projectile {projectileIndex + 1} struck {target.Name} for {damageAmount} {damageType} damage." + (damage?.Concentration is null ? "" : $" {damage.Concentration.Summary}");
        state.Results.Add(new SpellTargetResolution(target.Id, target.Name, projectileIndex + 1, null, null, damage, 0, summary));
        state.NextProjectileIndex++;

        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
        {
            campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "auto_projectile_spell_sequence";
            campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);
            Touch(campaign);
            var waitSummary = state.NextProjectileIndex < state.TargetIds.Count
                ? $"{summary} Resolve {target.Name}'s Concentration save before projectile {state.NextProjectileIndex + 1} can continue."
                : $"{summary} Resolve {target.Name}'s Concentration save before the spell finishes resolving.";
            return BuildAutoProjectileSequenceResult(campaign, state, waitSummary);
        }

        return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);
    }

    private SpellCastResult AdvancePlayerAutoProjectileSpellSequence(
        CampaignState campaign,
        PlayerAutoProjectileSpellSequenceState state,
        DiceService dice)
    {
        if (state.NextProjectileIndex >= state.TargetIds.Count)
            return FinalizePlayerAutoProjectileSpellSequence(campaign, state);

        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAutoProjectileSpell(campaign, state.SpellId);
        var target = RequireCharacter(campaign, state.TargetIds[state.NextProjectileIndex]);
        var encounter = RequireAutoProjectileEncounterIfAny(campaign, state, caster);
        if (state.AutomaticCasterRolls)
        {
            var projectileIndex = state.NextProjectileIndex;
            var damageAmount = dice.RollDamage(spell.DamageExpression);
            DamageResolutionResult? damage = null;
            if (!target.Dead)
                damage = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, spell.DamageType);
            var summary = target.Dead && damage is null
                ? $"Projectile {projectileIndex + 1} was already allocated to {target.Name}; the target had been reduced to death by an earlier declared projectile before this damage instance was applied."
                : $"Projectile {projectileIndex + 1} automatically struck {target.Name} for {damageAmount} {spell.DamageType} damage." + (damage?.Concentration is null ? "" : $" {damage.Concentration.Summary}");
            state.Results.Add(new SpellTargetResolution(target.Id, target.Name, projectileIndex + 1, null, null, damage, 0, summary));
            state.NextProjectileIndex++;

            if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)
            {
                campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "auto_projectile_spell_sequence";
                campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);
                Touch(campaign);
                var waitSummary = state.NextProjectileIndex < state.TargetIds.Count
                    ? $"{summary} Resolve {target.Name}'s Concentration save before projectile {state.NextProjectileIndex + 1} can continue."
                    : $"{summary} Resolve {target.Name}'s Concentration save before the spell finishes resolving.";
                return BuildAutoProjectileSequenceResult(campaign, state, waitSummary);
            }

            return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);
        }

        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));

        var pending = new PendingRollRequest
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter?.Id,
            CombatantId = casterCombatant?.Id,
            Formula = spell.DamageExpression,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"Projectile {state.NextProjectileIndex + 1} of {caster.Name}'s {spell.Name} automatically strikes {target.Name}. Roll {spell.DamageExpression} damage.",
            ResolutionKey = "projectile_auto_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sequence_json"] = JsonSerializer.Serialize(state),
                ["projectile_index"] = state.NextProjectileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["target_id"] = target.Id,
                ["base_damage_expression"] = spell.DamageExpression,
                ["damage_type"] = spell.DamageType
            }
        };
        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return BuildAutoProjectileSequenceResult(campaign, state, pending.Purpose);
    }

    private SpellCastResult FinalizePlayerAutoProjectileSpellSequence(
        CampaignState campaign,
        PlayerAutoProjectileSpellSequenceState state)
    {
        var caster = RequireCharacter(campaign, state.CasterId);
        var spell = RequireAutoProjectileSpell(campaign, state.SpellId);
        var distinctTargets = state.TargetIds
            .Select(id => RequireCharacter(campaign, id).Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var slotText = spell.Level == 0
            ? "as a cantrip"
            : state.ReadiedReaction
                ? $"from the level {state.CastAtLevel} slot expended when it was readied"
                : $"using a level {state.CastAtLevel} spell slot";
        var verb = state.ReadiedReaction ? "released readied" : "cast";
        var summary = $"{caster.Name} {verb} {spell.Name} {slotText}, resolving {state.TargetIds.Count} projectile{(state.TargetIds.Count == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. "
            + string.Join(" ", state.Results.Select(r => r.Summary));
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return BuildAutoProjectileSequenceResult(campaign, state, summary);
    }

    private SpellCastResult BuildAutoProjectileSequenceResult(
        CampaignState campaign,
        PlayerAutoProjectileSpellSequenceState state,
        string summary)
    {
        var spell = RequireAutoProjectileSpell(campaign, state.SpellId);
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

    private string? ResumePlayerAutoProjectileSpellSequenceAfterConcentration(
        CampaignState campaign,
        IReadOnlyDictionary<string, string> continuationContext,
        DiceService dice)
    {
        if (!continuationContext.TryGetValue("continuation_sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The auto-projectile spell continuation is missing its sequence state.");
        var state = JsonSerializer.Deserialize<PlayerAutoProjectileSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The auto-projectile spell continuation could not be restored.");
        var result = AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);
        return result.Summary;
    }

    private static PlayerAutoProjectileSpellSequenceState ReadAutoProjectileSequence(PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("sequence_json", out var json) || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The pending auto-projectile roll is missing its sequence state.");
        return JsonSerializer.Deserialize<PlayerAutoProjectileSpellSequenceState>(json)
            ?? throw new InvalidOperationException("The pending auto-projectile sequence could not be restored.");
    }

    private static int RequireCurrentAutoProjectileIndex(PlayerAutoProjectileSpellSequenceState state, PendingRollRequest pending)
    {
        var pendingIndex = pending.Context.TryGetValue("projectile_index", out var indexText) && int.TryParse(indexText, out var parsed)
            ? parsed
            : -1;
        if (pendingIndex < 0 || pendingIndex >= state.TargetIds.Count || pendingIndex != state.NextProjectileIndex)
            throw new InvalidOperationException("The pending auto-projectile index no longer matches the saved sequence.");
        return pendingIndex;
    }

    private EncounterState? RequireAutoProjectileEncounterIfAny(
        CampaignState campaign,
        PlayerAutoProjectileSpellSequenceState state,
        CharacterSheet caster)
    {
        if (string.IsNullOrWhiteSpace(state.EncounterId)) return null;
        var encounter = RequireEncounter(campaign, state.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The auto-projectile spell's encounter is no longer active.");
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The auto-projectile spellcaster is no longer in the encounter.");
        if (!state.ReadiedReaction)
            EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
    }

    private static SpellDefinition RequireAutoProjectileSpell(CampaignState campaign, string spellId)
    {
        return campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The spell for the pending auto-projectile sequence no longer exists.");
    }
}
