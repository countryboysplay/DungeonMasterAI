using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingRollRequest RequestReadiedAttackRoll(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId)
    {
        Guard.NotNull(campaign, nameof(campaign));
        EnsureNoRequiredPlayerRoll(campaign);

        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var reactorCombatant = RequireCombatant(encounter, reactorCombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can create a player-controlled readied attack roll request.");

        var readied = RequireReadiedAction(reactorCombatant, "attack");
        if (!reactorCombatant.ReactionAvailable)
            throw new InvalidOperationException($"{reactor.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(reactor))
            throw new InvalidOperationException($"{reactor.Name} cannot take a Reaction right now.");

        var targetCombatant = RequireCombatant(encounter, readied.TargetCombatantId ?? "");
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (target.Dead)
            throw new InvalidOperationException($"{target.Name} is already dead.");

        var profile = SelectAttackProfile(reactor, readied.AttackName);
        ValidateAttackRange(reactorCombatant, targetCombatant, reactor, target, profile);
        var coverBonus = GetCoverBonus(encounter, reactorCombatant, targetCombatant);
        if (coverBonus >= 100)
            throw new InvalidOperationException($"{target.Name} has Total Cover from {reactor.Name} and cannot be targeted directly by the readied attack.");

        var armorClass = EffectiveArmorClass(campaign, target) + coverBonus;
        var mode = AttackRollMode(campaign, encounter, reactorCombatant, targetCombatant, reactor, target);
        var automaticCritical = IsAutomaticCriticalHitTarget(reactorCombatant, targetCombatant, target);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var coverText = coverBonus > 0 ? $" including {CoverLabel(coverBonus)} Cover" : "";

        // Clicking the trigger is the player's decision to spend the Reaction. Once the
        // attack d20 is requested, the readied action cannot be taken back after seeing dice.
        reactorCombatant.ReactionAvailable = false;
        reactorCombatant.ReadiedAction = null;

        var pending = new PendingRollRequest
        {
            ActorCharacterId = reactor.Id,
            EncounterId = encounter.Id,
            CombatantId = reactorCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{reactor.Name} uses a Reaction to release the readied {profile.Name} attack against {target.Name}. Roll the attack d20{modeText} against AC {armorClass}{coverText}.",
            ResolutionKey = "readied_attack",
            Modifier = profile.AttackBonus,
            TargetNumber = armorClass,
            TargetLabel = $"AC {armorClass}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target_combatant_id"] = targetCombatant.Id,
                ["attack_name"] = profile.Name,
                ["automatic_critical"] = automaticCritical ? "true" : "false",
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reaction_committed"] = "true"
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public EncounterAttackResult ResolvePendingReadiedAttackRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        ValidateD20Inputs(rollOne, rollTwo);

        var pending = RequirePendingRoll(campaign, pendingRollId, "readied_attack");
        if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending readied attack is missing combat context.");
        var encounter = RequireEncounter(campaign, pending.EncounterId);
        var reactorCombatant = RequireCombatant(encounter, pending.CombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        if (!reactor.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending readied attack actor no longer matches the reacting combatant.");
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending readied attack no longer belongs to a player character.");
        if (!CanTakeReaction(reactor))
            throw new InvalidOperationException($"{reactor.Name} can no longer complete the readied attack.");

        var targetCombatantId = RequiredPendingContext(pending, "target_combatant_id");
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (target.Dead)
            throw new InvalidOperationException($"{target.Name} is already dead.");

        pending.Context.TryGetValue("attack_name", out var attackName);
        var profile = SelectAttackProfile(reactor, attackName);
        ValidateAttackRange(reactorCombatant, targetCombatant, reactor, target, profile);
        var coverBonus = pending.Context.TryGetValue("cover_bonus", out var coverText)
            && int.TryParse(coverText, out var parsedCover)
            ? parsedCover
            : GetCoverBonus(encounter, reactorCombatant, targetCombatant);
        if (coverBonus >= 100)
            throw new InvalidOperationException($"{target.Name} now has Total Cover and the readied attack cannot be completed.");

        var mode = ParsePendingRollMode(pending.RollMode);
        RequireSecondD20WhenNeeded(mode, rollTwo, "readied attack");
        var chosen = mode switch
        {
            D20RollMode.Advantage => Math.Max(rollOne, rollTwo!.Value),
            D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo!.Value),
            _ => rollOne
        };
        var armorClass = pending.TargetNumber ?? EffectiveArmorClass(campaign, target) + coverBonus;
        var effectAttackBonus = RollActiveAttackBonus(campaign, reactor.Id, dice);
        var totalModifier = profile.AttackBonus + effectAttackBonus;
        var total = chosen + totalModifier;
        var naturalCritical = chosen == 20;
        var automaticCritical = pending.Context.TryGetValue("automatic_critical", out var criticalText)
            && bool.TryParse(criticalText, out var parsedAutomaticCritical)
            && parsedAutomaticCritical;
        var hit = naturalCritical || (chosen != 1 && total >= armorClass);
        var critical = hit && (naturalCritical || automaticCritical);

        var helpUsed = ConsumeHelpAttackAdvantage(encounter, reactorCombatant, targetCombatant);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        BreakHidden(campaign, encounter, reactorCombatant, "making a readied attack roll");
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = hit
            ? $"Attack{modeText} {total} vs AC {armorClass}: hit{(critical ? " (critical)" : "")}{effectText}; damage roll required."
            : $"Attack{modeText} {total} vs AC {armorClass}: miss{effectText}.";
        var attack = new AttackResult(chosen, totalModifier, total, hit, critical, 0, attackSummary);
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var coverSummary = coverBonus > 0 ? $" {CoverLabel(coverBonus)} Cover contributed +{coverBonus} AC." : "";

        if (!hit)
        {
            var summary = $"{reactor.Name} used a Reaction for the readied {profile.Name} attack against {target.Name}: miss ({total} vs AC {armorClass}).{coverSummary}{helpText}";
            campaign.PendingPlayerRoll = null;
            Touch(campaign);
            Log(campaign, "ready_attack_triggered", summary);
            Log(campaign, "player_roll_resolved", $"{reactor.Name}'s player-supplied readied attack d20 resolved as {chosen} ({total} vs AC {armorClass}).", dmOnly: true);
            return new EncounterAttackResult(encounter.Id, reactor.Name, target.Name, profile.Name, attack, null, summary, null, true, coverBonus);
        }

        var damageFormula = BuildPendingDamageFormula(profile.DamageExpression, critical);
        var damagePending = new PendingRollRequest
        {
            ActorCharacterId = reactor.Id,
            EncounterId = encounter.Id,
            CombatantId = reactorCombatant.Id,
            Formula = damageFormula,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"{reactor.Name}'s readied {profile.Name} attack hit {target.Name}{(critical ? " critically" : "")}. Roll {damageFormula} damage.",
            ResolutionKey = "readied_attack_damage",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target_combatant_id"] = targetCombatant.Id,
                ["attack_name"] = profile.Name,
                ["damage_type"] = profile.DamageType,
                ["base_damage_expression"] = profile.DamageExpression,
                ["critical"] = critical ? "true" : "false",
                ["attack_d20"] = chosen.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_modifier"] = totalModifier.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["armor_class"] = armorClass.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_mode"] = mode.ToString().ToLowerInvariant(),
                ["effect_attack_bonus"] = effectAttackBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["help_used"] = helpUsed ? "true" : "false",
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        campaign.PendingPlayerRoll = damagePending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", damagePending.Purpose, dmOnly: true);
        Log(campaign, "player_roll_resolved", $"{reactor.Name}'s player-supplied readied attack d20 resolved as {chosen} ({total} vs AC {armorClass}).", dmOnly: true);
        var hitSummary = $"{reactor.Name} used a Reaction for the readied {profile.Name} attack against {target.Name}: hit ({total} vs AC {armorClass}){(critical ? " critical" : "")}.{coverSummary}{helpText} Roll {damageFormula} damage.";
        return new EncounterAttackResult(encounter.Id, reactor.Name, target.Name, profile.Name, attack, null, hitSummary, null, true, coverBonus);
    }

    public EncounterAttackResult ResolvePendingReadiedAttackDamageRoll(
        CampaignState campaign,
        string pendingRollId,
        int damageAmount,
        DiceService dice)
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(dice, nameof(dice));
        if (damageAmount is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(damageAmount));

        var pending = RequirePendingRoll(campaign, pendingRollId, "readied_attack_damage");
        if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending readied attack damage is missing combat context.");
        var encounter = RequireEncounter(campaign, pending.EncounterId);
        var reactorCombatant = RequireCombatant(encounter, pending.CombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        if (!reactor.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending readied attack damage actor no longer matches the reacting combatant.");
        if (!reactor.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending readied attack damage no longer belongs to a player character.");

        var targetCombatantId = RequiredPendingContext(pending, "target_combatant_id");
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (target.Dead)
            throw new InvalidOperationException($"{target.Name} is already dead.");

        pending.Context.TryGetValue("attack_name", out var attackName);
        var profile = SelectAttackProfile(reactor, attackName);
        var damageType = pending.Context.TryGetValue("damage_type", out var storedDamageType) && !string.IsNullOrWhiteSpace(storedDamageType)
            ? storedDamageType
            : profile.DamageType;
        var critical = ContextBool(pending, "critical");
        var attackD20 = ContextInt(pending, "attack_d20");
        var attackModifier = ContextInt(pending, "attack_modifier");
        var attackTotal = ContextInt(pending, "attack_total");
        var armorClass = ContextInt(pending, "armor_class");
        var coverBonus = ContextInt(pending, "cover_bonus", 0);
        var mode = ParsePendingRollMode(pending.Context.TryGetValue("attack_mode", out var attackMode) ? attackMode : null);
        var effectAttackBonus = ContextInt(pending, "effect_attack_bonus", 0);
        var helpUsed = ContextBool(pending, "help_used");

        // Clear this damage request before applying damage so a player-owned Concentration
        // save can become the next authoritative pending roll without being overwritten.
        campaign.PendingPlayerRoll = null;
        var resolution = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, damageType, critical);
        var damage = resolution.Damage;
        var concentration = resolution.Concentration;
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = $"Attack{modeText} {attackTotal} vs AC {armorClass}: hit for {damageAmount} damage{(critical ? " (critical)" : "")}{effectText}.";
        var attack = new AttackResult(attackD20, attackModifier, attackTotal, true, critical, damageAmount, attackSummary);
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var coverText = coverBonus > 0 ? $" {CoverLabel(coverBonus)} Cover contributed +{coverBonus} AC." : "";
        var summary = $"{reactor.Name} used a Reaction for the readied {profile.Name} attack against {target.Name}: {attack.Summary}{coverText}{helpText}";
        if (concentration is not null)
            summary += $" {concentration.Summary}";

        Touch(campaign);
        Log(campaign, "ready_attack_triggered", summary);
        Log(campaign, "player_roll_resolved", $"{reactor.Name} supplied {damageAmount} readied {profile.Name} damage.", dmOnly: true);
        return new EncounterAttackResult(encounter.Id, reactor.Name, target.Name, profile.Name, attack, damage, summary, concentration, true, coverBonus);
    }
}
