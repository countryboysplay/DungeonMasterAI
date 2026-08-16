using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public PendingRollRequest RequestEncounterAttackRoll(
        CampaignState campaign,
        string encounterId,
        string attackerCombatantId,
        string targetCombatantId,
        string? attackName = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.Combatants.Any(c => c.Initiative is null))
            throw new InvalidOperationException("Initiative must be finalized before resolving an attack.");
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve or decline the pending Opportunity Attacks before attacking.");

        var attackerCombatant = RequireCombatant(encounter, attackerCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);

        if (!attacker.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can create a player-controlled attack roll request.");
        if (CharacterMechanics.IsIncapacitated(attacker))
            throw new InvalidOperationException($"{attacker.Name} is Incapacitated and cannot take the Attack action.");
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");

        EnsureAttackAvailableWithoutConsuming(attackerCombatant, attacker);
        var profile = SelectPendingAttackProfile(attacker, attackName);
        ValidateAttackRange(attackerCombatant, targetCombatant, attacker, target, profile);
        var coverBonus = GetCoverBonus(encounter, attackerCombatant, targetCombatant);
        if (coverBonus >= 100)
            throw new InvalidOperationException($"{target.Name} has Total Cover from {attacker.Name} and cannot be targeted directly by that attack.");

        var effectiveArmorClass = EffectiveArmorClass(campaign, target) + coverBonus;
        var mode = AttackRollMode(campaign, encounter, attackerCombatant, targetCombatant, attacker, target);
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var coverText = coverBonus > 0 ? $" including {CoverLabel(coverBonus)} Cover" : "";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = attacker.Id,
            EncounterId = encounter.Id,
            CombatantId = attackerCombatant.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = mode.ToString().ToLowerInvariant(),
            Purpose = $"{attacker.Name} attacks {target.Name} with {profile.Name}. Roll the attack d20{modeText} against AC {effectiveArmorClass}{coverText}.",
            ResolutionKey = "combat_attack",
            Modifier = profile.AttackBonus,
            TargetNumber = effectiveArmorClass,
            TargetLabel = $"AC {effectiveArmorClass}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target_combatant_id"] = targetCombatant.Id,
                ["attack_name"] = profile.Name,
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automatic_critical"] = IsAutomaticCriticalHitTarget(attackerCombatant, targetCombatant, target) ? "true" : "false"
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public EncounterAttackResult ResolvePendingEncounterAttackRoll(
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

        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals("combat_attack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not a combat attack.");
        if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending attack is missing combat context.");
        if (!pending.Context.TryGetValue("target_combatant_id", out var targetCombatantId) || string.IsNullOrWhiteSpace(targetCombatantId))
            throw new InvalidOperationException("The pending attack is missing its target.");
        pending.Context.TryGetValue("attack_name", out var attackName);

        var encounter = RequireEncounter(campaign, pending.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is no longer active.");
        var attackerCombatant = RequireCombatant(encounter, pending.CombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (!attacker.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending attack actor no longer matches the active combatant.");
        if (CharacterMechanics.IsIncapacitated(attacker))
            throw new InvalidOperationException($"{attacker.Name} is Incapacitated and cannot complete the Attack action.");
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");

        EnsureAttackAvailableWithoutConsuming(attackerCombatant, attacker);
        var profile = SelectPendingAttackProfile(attacker, attackName);
        ValidateAttackRange(attackerCombatant, targetCombatant, attacker, target, profile);

        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This attack requires two d20 results because it has {mode}.");
        var chosen = mode switch
        {
            D20RollMode.Advantage => Math.Max(rollOne, rollTwo!.Value),
            D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo!.Value),
            _ => rollOne
        };

        var armorClass = pending.TargetNumber ?? EffectiveArmorClass(campaign, target);
        var effectAttackBonus = RollActiveAttackBonus(campaign, attacker.Id, dice);
        var totalModifier = profile.AttackBonus + effectAttackBonus;
        var total = chosen + totalModifier;
        var naturalCritical = chosen == 20;
        var automaticCritical = pending.Context.TryGetValue("automatic_critical", out var automaticCriticalText)
            && bool.TryParse(automaticCriticalText, out var parsedAutomaticCritical)
            && parsedAutomaticCritical;
        var hit = naturalCritical || (chosen != 1 && total >= armorClass);
        var critical = hit && (naturalCritical || automaticCritical);
        var damageAmount = hit ? dice.RollDamage(profile.DamageExpression, critical) : 0;

        // Commit the attack only after the supplied player roll has been validated.
        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var helpUsed = ConsumeHelpAttackAdvantage(encounter, attackerCombatant, targetCombatant);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        BreakHidden(campaign, encounter, attackerCombatant, "making an attack roll");

        DamageResult? damage = null;
        ConcentrationCheckResult? concentration = null;
        var concentrationBefore = target.ConcentrationEffect;
        if (hit)
        {
            var resolution = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, profile.DamageType, critical);
            damage = resolution.Damage;
            concentration = resolution.Concentration;
        }

        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = hit
            ? $"Attack{modeText} {total} vs AC {armorClass}: hit for {damageAmount} damage{(critical ? " (critical)" : "")}{effectText}."
            : $"Attack{modeText} {total} vs AC {armorClass}: miss{effectText}.";
        var attack = new AttackResult(chosen, totalModifier, total, hit, critical, damageAmount, attackSummary);

        var coverBonus = pending.Context.TryGetValue("cover_bonus", out var coverTextValue)
            && int.TryParse(coverTextValue, out var parsedCover) ? parsedCover : 0;
        var coverText = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} AC)" : "";
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var summary = hit
            ? $"{attacker.Name} used {profile.Name} against {target.Name}{coverText}: {attack.Summary}{helpText}"
            : $"{attacker.Name} used {profile.Name} against {target.Name}{coverText}: miss ({total} vs AC {armorClass}).{helpText}";
        if (concentration is not null) summary += $" {concentration.Summary}";
        else if (!string.IsNullOrWhiteSpace(concentrationBefore) && string.IsNullOrWhiteSpace(target.ConcentrationEffect) && damage?.EffectiveDamage > 0)
            summary += $" {target.Name} lost Concentration on {concentrationBefore}.";

        campaign.PendingPlayerRoll = null;
        Touch(campaign);
        Log(campaign, "combat_attack", summary);
        return new EncounterAttackResult(encounter.Id, attacker.Name, target.Name, profile.Name, attack, damage, summary, concentration, false, coverBonus);
    }

    private static void EnsureAttackAvailableWithoutConsuming(CombatantState combatant, CharacterSheet character)
    {
        if (CharacterMechanics.IsIncapacitated(character))
            throw new InvalidOperationException($"{character.Name} is Incapacitated and cannot take the Attack action.");
        if (combatant.AttackActionInProgress)
        {
            if (combatant.AttacksRemainingInAction <= 0)
                throw new InvalidOperationException($"{character.Name} has no attacks remaining in their Attack action this turn.");
            return;
        }
        if (!combatant.ActionAvailable)
            throw new InvalidOperationException($"{character.Name} has already used their action this turn and cannot take the Attack action.");
    }

    private static AttackProfile SelectPendingAttackProfile(CharacterSheet attacker, string? attackName)
    {
        var profile = string.IsNullOrWhiteSpace(attackName)
            ? attacker.Attacks.FirstOrDefault() ?? CharacterMechanics.UnarmedStrikeProfile(attacker)
            : attacker.Attacks.FirstOrDefault(a => a.Name.Equals(attackName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null && string.Equals(attackName?.Trim(), "Unarmed Strike", StringComparison.OrdinalIgnoreCase))
            profile = CharacterMechanics.UnarmedStrikeProfile(attacker);
        return profile ?? throw new InvalidOperationException($"{attacker.Name} has no configured attack matching '{attackName ?? "default"}'.");
    }

    private static D20RollMode ParsePendingRollMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
    {
        "advantage" or "adv" => D20RollMode.Advantage,
        "disadvantage" or "dis" => D20RollMode.Disadvantage,
        _ => D20RollMode.Normal
    };
}
