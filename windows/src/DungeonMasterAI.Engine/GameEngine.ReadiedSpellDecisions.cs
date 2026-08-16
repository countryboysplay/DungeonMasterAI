using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingPlayerDecision RequestReadiedSpellDecision(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        string? targetCombatantId = null,
        int? areaCenterX = null,
        int? areaCenterY = null,
        string? areaDirection = null,
        IReadOnlyList<string>? targetCombatantIds = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");
        if (campaign.PendingPlayerDecision?.Required == true)
            throw new InvalidOperationException($"Resolve the required player decision first: {campaign.PendingPlayerDecision.Prompt}");

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, reactorCombatantId);
        var caster = RequireCharacter(campaign, combatant.CharacterId);
        if (!caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can receive a readied spell Reaction decision.");

        var readied = RequireReadiedAction(combatant, "spell");
        if (!combatant.ReactionAvailable)
            throw new InvalidOperationException($"{caster.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(caster))
            throw new InvalidOperationException($"{caster.Name} cannot take a Reaction right now.");

        var spell = campaign.Spells.FirstOrDefault(s => s.Id.Equals(readied.SpellId ?? "", StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The spell stored in the readied action is no longer in the campaign spell catalog.");
        var heldEffect = ReadiedSpellConcentrationLabel(spell);
        if (!string.Equals(caster.ConcentrationEffect, heldEffect, StringComparison.OrdinalIgnoreCase))
        {
            combatant.ReadiedAction = null;
            throw new InvalidOperationException($"{caster.Name} is no longer Concentrating on the readied {spell.Name}; the held spell has dissipated.");
        }

        CharacterSheet? target = null;
        CombatantState? targetCombatant = null;
        if (!string.IsNullOrWhiteSpace(targetCombatantId))
        {
            targetCombatant = RequireCombatant(encounter, targetCombatantId);
            target = RequireCharacter(campaign, targetCombatant.CharacterId);
        }
        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();
        var projectileResolution = resolution is "projectile_attack" or "projectile_auto";
        var areaResolution = resolution == "area_save";
        var multiBuffResolution = resolution == "multi_buff";
        ReadiedAreaSpellPlan? areaPlan = null;
        ReadiedMultiBuffPlan? multiBuffPlan = null;
        if (areaResolution)
            areaPlan = PlanReadiedAreaSpell(campaign, caster, spell, encounter, areaCenterX, areaCenterY, areaDirection);
        if (multiBuffResolution)
        {
            IReadOnlyList<string>? effectiveTargets = targetCombatantIds is { Count: > 0 }
                ? targetCombatantIds
                : targetCombatant is null ? null : [targetCombatant.Id];
            var castAtLevel = spell.Level == 0 ? 0 : Math.Max(spell.Level, readied.CastAtLevel);
            multiBuffPlan = PlanReadiedMultiBuffSpell(campaign, caster, spell, encounter, castAtLevel, effectiveTargets);
        }
        if ((spell.RequiresTarget || projectileResolution) && !areaResolution && !multiBuffResolution && target is null)
            throw new InvalidOperationException($"{spell.Name} requires a target when its trigger is accepted.");
        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");
        if (!multiBuffResolution)
        {
            ValidateSpellTargetType(target, spell);
            ValidateSpellRange(campaign, encounter, caster, target, spell);
        }

        var decision = new PendingPlayerDecision
        {
            ActorCharacterId = caster.Id,
            EncounterId = encounter.Id,
            CombatantId = combatant.Id,
            DecisionType = "readied_spell_reaction",
            Prompt = multiBuffPlan is not null
                ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Proposed targets: {string.Join(", ", multiBuffPlan.TargetNames)}. Use the Reaction to release it now?"
                : areaPlan is not null
                    ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Proposed area origin: ({areaPlan.PointX}, {areaPlan.PointY}), direction {areaPlan.Direction}; affected: {string.Join(", ", areaPlan.TargetNames)}. Use the Reaction to release it now?"
                : target is null
                    ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Use the Reaction to release it now?"
                    : $"The trigger occurred for {caster.Name}'s readied {spell.Name} at {target.Name}. Use the Reaction to release it now?",
            Required = true,
            Options =
            [
                new PlayerDecisionOption
                {
                    Id = "use_reaction",
                    Label = $"Release {spell.Name}",
                    Description = "Spend the Reaction and resolve the held spell. Any player-owned attack, save, or damage dice will be requested before resolution continues.",
                    Value = spell.Name,
                    Emphasis = "primary"
                },
                new PlayerDecisionOption
                {
                    Id = "decline_trigger",
                    Label = "Ignore this trigger",
                    Description = "Keep the Reaction and continue holding the readied spell while its Ready window remains valid.",
                    Value = "decline",
                    Emphasis = "secondary"
                }
            ],
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["spell_id"] = spell.Id
            }
        };
        if (targetCombatant is not null)
            decision.Context["target_combatant_id"] = targetCombatant.Id;
        if (areaPlan is not null)
        {
            decision.Context["area_center_x"] = areaPlan.PointX.ToString(System.Globalization.CultureInfo.InvariantCulture);
            decision.Context["area_center_y"] = areaPlan.PointY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            decision.Context["area_direction"] = areaPlan.Direction;
        }
        if (multiBuffPlan is not null)
            decision.Context["target_combatant_ids_json"] = System.Text.Json.JsonSerializer.Serialize(multiBuffPlan.TargetCombatantIds);

        campaign.PendingPlayerDecision = decision;
        Touch(campaign);
        Log(campaign, "player_decision_requested", decision.Prompt, dmOnly: true);
        return decision;
    }

    private PlayerDecisionResolution ResolveReadiedSpellDecision(
        CampaignState campaign,
        PendingPlayerDecision decision,
        PlayerDecisionOption option,
        DiceService dice)
    {
        if (string.IsNullOrWhiteSpace(decision.EncounterId) || string.IsNullOrWhiteSpace(decision.CombatantId))
            throw new InvalidOperationException("The readied spell decision is missing combat context.");
        var encounter = RequireEncounter(campaign, decision.EncounterId);
        var combatant = RequireCombatant(encounter, decision.CombatantId);
        var caster = RequireCharacter(campaign, combatant.CharacterId);
        if (!caster.Id.Equals(decision.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The readied spell decision actor no longer matches the reacting combatant.");
        if (!caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The readied spell decision no longer belongs to a player character.");
        _ = RequireReadiedAction(combatant, "spell");

        campaign.PendingPlayerDecision = null;
        if (option.Id.Equals("decline_trigger", StringComparison.OrdinalIgnoreCase))
        {
            var summary = $"{caster.Name} ignored this readied-spell trigger. The Reaction and held spell remain available while the Ready window and Concentration remain valid.";
            Log(campaign, "readied_spell_declined", summary, dmOnly: true);
            Log(campaign, "player_decision_resolved", $"{caster.Name}: {option.Label}.", dmOnly: true);
            Touch(campaign);
            return new PlayerDecisionResolution(decision.Id, decision.DecisionType, caster.Id, option.Id, option.Label, summary, null);
        }

        if (!option.Id.Equals("use_reaction", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected readied spell decision option is not supported.");

        decision.Context.TryGetValue("target_combatant_id", out var targetCombatantId);
        var centerX = DecisionOptionalInt(decision, "area_center_x");
        var centerY = DecisionOptionalInt(decision, "area_center_y");
        decision.Context.TryGetValue("area_direction", out var areaDirection);
        var targetCombatantIds = DecisionOptionalStringArrayJson(decision, "target_combatant_ids_json");
        var result = TriggerReadiedSpell(
            campaign,
            encounter.Id,
            combatant.Id,
            dice,
            string.IsNullOrWhiteSpace(targetCombatantId) ? null : targetCombatantId,
            centerX,
            centerY,
            areaDirection,
            targetCombatantIds);
        var followUp = campaign.PendingPlayerRoll?.Required == true ? campaign.PendingPlayerRoll : null;
        Log(campaign, "player_decision_resolved", $"{caster.Name}: {option.Label}.", dmOnly: true);
        Touch(campaign);
        return new PlayerDecisionResolution(
            decision.Id,
            decision.DecisionType,
            caster.Id,
            option.Id,
            option.Label,
            result.Summary,
            followUp);
    }

    private static IReadOnlyList<string>? DecisionOptionalStringArrayJson(PendingPlayerDecision decision, string key)
    {
        if (!decision.Context.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(raw)
                ?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"The readied spell decision contains invalid '{key}' JSON.", ex);
        }
    }

    private static int? DecisionOptionalInt(PendingPlayerDecision decision, string key)
    {
        if (!decision.Context.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"The readied spell decision contains an invalid '{key}' value.");
        return value;
    }

    private static bool IsReadiedSpellPending(PendingRollRequest pending)
        => pending.Context.TryGetValue("readied_reaction", out var value)
           && bool.TryParse(value, out var parsed)
           && parsed;

    private static void MarkReadiedSpellPending(CampaignState campaign)
    {
        if (campaign.PendingPlayerRoll?.Required == true)
            campaign.PendingPlayerRoll.Context["readied_reaction"] = "true";
    }
}
