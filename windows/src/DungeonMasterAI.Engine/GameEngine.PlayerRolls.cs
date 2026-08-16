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

        // The attack action is committed only after the player's supplied d20 has been validated.
        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var helpUsed = ConsumeHelpAttackAdvantage(encounter, attackerCombatant, targetCombatant);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        BreakHidden(campaign, encounter, attackerCombatant, "making an attack roll");

        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = hit
            ? $"Attack{modeText} {total} vs AC {armorClass}: hit{(critical ? " (critical)" : "")}{effectText}; damage roll required."
            : $"Attack{modeText} {total} vs AC {armorClass}: miss{effectText}.";
        var attack = new AttackResult(chosen, totalModifier, total, hit, critical, 0, attackSummary);

        var coverBonus = pending.Context.TryGetValue("cover_bonus", out var coverTextValue)
            && int.TryParse(coverTextValue, out var parsedCover) ? parsedCover : 0;
        var coverLabel = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} AC)" : "";
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";

        if (!hit)
        {
            var missSummary = $"{attacker.Name} used {profile.Name} against {target.Name}{coverLabel}: miss ({total} vs AC {armorClass}).{helpText}";
            campaign.PendingPlayerRoll = null;
            Touch(campaign);
            Log(campaign, "combat_attack", missSummary);
            return new EncounterAttackResult(encounter.Id, attacker.Name, target.Name, profile.Name, attack, null, missSummary, null, false, coverBonus);
        }

        var damageFormula = BuildPendingDamageFormula(profile.DamageExpression, critical);
        var damagePending = new PendingRollRequest
        {
            ActorCharacterId = attacker.Id,
            EncounterId = encounter.Id,
            CombatantId = attackerCombatant.Id,
            Formula = damageFormula,
            RollType = "damage",
            RollMode = "normal",
            Purpose = $"{attacker.Name} hit {target.Name} with {profile.Name}{(critical ? " critically" : "")}. Roll {damageFormula} damage.",
            ResolutionKey = "combat_attack_damage",
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
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attack_mode"] = mode.ToString().ToLowerInvariant(),
                ["effect_attack_bonus"] = effectAttackBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["help_used"] = helpUsed ? "true" : "false"
            }
        };

        campaign.PendingPlayerRoll = damagePending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", damagePending.Purpose, dmOnly: true);
        var hitSummary = $"{attacker.Name} used {profile.Name} against {target.Name}{coverLabel}: hit ({total} vs AC {armorClass}){(critical ? " critical" : "")}.{helpText} Roll {damageFormula} damage.";
        return new EncounterAttackResult(encounter.Id, attacker.Name, target.Name, profile.Name, attack, null, hitSummary, null, false, coverBonus);
    }

    public EncounterAttackResult ResolvePendingEncounterAttackDamageRoll(
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
        if (!pending.ResolutionKey.Equals("combat_attack_damage", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not combat attack damage.");
        if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending damage roll is missing combat context.");
        if (!pending.Context.TryGetValue("target_combatant_id", out var targetCombatantId) || string.IsNullOrWhiteSpace(targetCombatantId))
            throw new InvalidOperationException("The pending damage roll is missing its target.");

        var encounter = RequireEncounter(campaign, pending.EncounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is no longer active.");
        var attackerCombatant = RequireCombatant(encounter, pending.CombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (!attacker.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending damage actor no longer matches the active combatant.");
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");

        pending.Context.TryGetValue("attack_name", out var attackName);
        var profile = SelectPendingAttackProfile(attacker, attackName);
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

        var concentrationBefore = target.ConcentrationEffect;
        var resolution = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, damageType, critical);
        var damage = resolution.Damage;
        var concentration = resolution.Concentration;

        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var effectText = effectAttackBonus == 0 ? "" : $" plus {effectAttackBonus} from active effects";
        var attackSummary = $"Attack{modeText} {attackTotal} vs AC {armorClass}: hit for {damageAmount} damage{(critical ? " (critical)" : "")}{effectText}.";
        var attack = new AttackResult(attackD20, attackModifier, attackTotal, true, critical, damageAmount, attackSummary);
        var coverLabel = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} AC)" : "";
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var summary = $"{attacker.Name} used {profile.Name} against {target.Name}{coverLabel}: {attack.Summary}{helpText}";
        if (concentration is not null) summary += $" {concentration.Summary}";
        else if (!string.IsNullOrWhiteSpace(concentrationBefore) && string.IsNullOrWhiteSpace(target.ConcentrationEffect) && damage.EffectiveDamage > 0)
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

    private static string BuildPendingDamageFormula(string? damageExpression, bool critical)
    {
        var compact = new string((damageExpression ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (string.IsNullOrWhiteSpace(compact)) return "0";
        if (!critical || int.TryParse(compact, out _)) return compact;

        var dIndex = compact.IndexOf('d');
        if (dIndex < 0) dIndex = compact.IndexOf('D');
        if (dIndex <= 0 && !compact.StartsWith("d", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported damage expression '{damageExpression}'.");

        var modifierIndex = -1;
        for (var i = dIndex + 1; i < compact.Length; i++)
        {
            if (compact[i] is '+' or '-')
            {
                modifierIndex = i;
                break;
            }
        }

        var countText = dIndex == 0 ? "1" : compact[..dIndex];
        var sidesText = modifierIndex < 0 ? compact[(dIndex + 1)..] : compact[(dIndex + 1)..modifierIndex];
        var modifierText = modifierIndex < 0 ? "" : compact[modifierIndex..];
        if (!int.TryParse(countText, out var count) || count < 1 || count > 100)
            throw new InvalidOperationException($"Unsupported damage dice count in '{damageExpression}'.");
        if (!int.TryParse(sidesText, out var sides) || sides < 2 || sides > 1000)
            throw new InvalidOperationException($"Unsupported damage die in '{damageExpression}'.");
        return $"{count * 2}d{sides}{modifierText}";
    }

    private static int ContextInt(PendingRollRequest pending, string key, int? fallback = null)
    {
        if (pending.Context.TryGetValue(key, out var text) && int.TryParse(text, out var value)) return value;
        if (fallback.HasValue) return fallback.Value;
        throw new InvalidOperationException($"The pending player roll is missing integer context '{key}'.");
    }

    private static bool ContextBool(PendingRollRequest pending, string key)
        => pending.Context.TryGetValue(key, out var text) && bool.TryParse(text, out var value) && value;

    private static D20RollMode ParsePendingRollMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
    {
        "advantage" or "adv" => D20RollMode.Advantage,
        "disadvantage" or "dis" => D20RollMode.Disadvantage,
        _ => D20RollMode.Normal
    };
}
