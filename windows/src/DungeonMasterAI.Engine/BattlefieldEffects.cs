using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public BattlefieldEffectState AddBattlefieldEffect(CampaignState campaign, string encounterId, BattlefieldEffectState effect)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(effect);
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Battlefield effects can be added only to an active encounter.");

        effect.Name = string.IsNullOrWhiteSpace(effect.Name) ? "Battlefield Effect" : effect.Name.Trim();
        effect.Shape = NormalizeBattlefieldEffectShape(effect.Shape);
        if (effect.SizeFeet <= 0 || effect.SizeFeet % 5 != 0)
            throw new ArgumentOutOfRangeException(nameof(effect.SizeFeet), "Battlefield effect size must be a positive multiple of 5 feet.");
        effect.Direction = NormalizeBattlefieldEffectDirection(effect.Direction);
        effect.Trigger = NormalizeBattlefieldEffectTrigger(effect.Trigger);
        effect.DamageExpression = (effect.DamageExpression ?? "").Trim();
        effect.DamageType = (effect.DamageType ?? "").Trim();
        effect.SaveAbility = string.IsNullOrWhiteSpace(effect.SaveAbility) ? "" : CharacterMechanics.NormalizeAbility(effect.SaveAbility);
        effect.DurationRounds = Math.Max(0, effect.DurationRounds);
        effect.AppliedRound = Math.Max(1, encounter.Round);
        effect.AppliedTurnIndex = Math.Clamp(encounter.TurnIndex, 0, Math.Max(0, encounter.Combatants.Count - 1));
        effect.ExpiresAfterRound = effect.DurationRounds > 0
            ? checked(effect.AppliedRound + effect.DurationRounds - 1)
            : 0;
        effect.SourceKind = string.IsNullOrWhiteSpace(effect.SourceKind) ? "runtime_generated" : effect.SourceKind.Trim();

        if (!string.IsNullOrWhiteSpace(effect.SourceCharacterId))
        {
            var source = RequireCharacter(campaign, effect.SourceCharacterId);
            if (effect.RequiresSourceConcentration)
            {
                if (string.IsNullOrWhiteSpace(source.ConcentrationEffect))
                    throw new InvalidOperationException($"{source.Name} is not Concentrating, so a Concentration-bound battlefield effect cannot be created.");
                if (string.IsNullOrWhiteSpace(effect.ConcentrationName))
                    effect.ConcentrationName = source.ConcentrationEffect!;
                if (!source.ConcentrationEffect!.Equals(effect.ConcentrationName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{source.Name} is Concentrating on {source.ConcentrationEffect}, not {effect.ConcentrationName}.");
            }
        }
        else if (effect.RequiresSourceConcentration)
        {
            throw new InvalidOperationException("A Concentration-bound battlefield effect requires a source character.");
        }

        if (effect.Trigger != "none" && string.IsNullOrWhiteSpace(effect.DamageExpression))
            throw new InvalidOperationException("A triggered battlefield effect requires a damage expression. Use trigger 'none' for terrain/obscurement-only effects.");
        if (!string.IsNullOrWhiteSpace(effect.SaveAbility) && effect.SaveDc <= 0)
            throw new InvalidOperationException("A battlefield effect with a saving throw requires a positive Save DC.");
        if (string.IsNullOrWhiteSpace(effect.SaveAbility) && effect.SaveDc > 0)
            throw new InvalidOperationException("A battlefield effect Save DC requires a saving throw ability.");

        encounter.BattlefieldEffects.Add(effect);
        Touch(campaign);
        Log(campaign, "battlefield_effect_added", $"Battlefield effect '{effect.Name}' was added at ({effect.OriginX}, {effect.OriginY}) as a {effect.SizeFeet}-ft {effect.Shape}.", dmOnly: effect.DmOnly);
        return effect;
    }

    public bool RemoveBattlefieldEffect(CampaignState campaign, string encounterId, string effectId, string reason = "removed")
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var encounter = RequireEncounter(campaign, encounterId);
        var effect = encounter.BattlefieldEffects.FirstOrDefault(e =>
            e.Id.Equals(effectId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(e.Name) && e.Name.Equals(effectId, StringComparison.OrdinalIgnoreCase)));
        if (effect is null) return false;
        encounter.BattlefieldEffects.Remove(effect);
        Touch(campaign);
        Log(campaign, "battlefield_effect_removed", $"Battlefield effect '{effect.Name}' ended ({(string.IsNullOrWhiteSpace(reason) ? "removed" : reason.Trim())}).", dmOnly: effect.DmOnly);
        return true;
    }

    private void ProcessBattlefieldEffectsAtTurnStart(CampaignState campaign, EncounterState encounter, CombatantState combatant, DiceService dice)
    {
        ExpireBattlefieldEffects(campaign, encounter);
        if (!combatant.Positioned) return;
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.Dead) return;

        foreach (var effect in encounter.BattlefieldEffects.ToArray())
        {
            if (effect.Trigger is not ("start_turn" or "start_or_enter")) continue;
            if (!BattlefieldEffectContainsCell(effect, combatant.GridX, combatant.GridY)) continue;
            TriggerBattlefieldEffect(campaign, encounter, effect, combatant, dice, "started its turn inside");
            if (character.Dead) break;
        }
    }

    /// <summary>
    /// Mirrors the guards the turn-start damage path enforces so a caller can refuse the turn
    /// transition before it mutates the turn index, the round or the action economy.
    /// </summary>
    private static void ValidateTurnStartBattlefieldEffects(CampaignState campaign, EncounterState encounter, CombatantState combatant)
    {
        if (!combatant.Positioned) return;
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.Dead) return;

        foreach (var effect in encounter.BattlefieldEffects)
        {
            if (effect.Trigger is not ("start_turn" or "start_or_enter")) continue;
            if (!BattlefieldEffectContainsCell(effect, combatant.GridX, combatant.GridY)) continue;
            ValidateBattlefieldEffectDamage(campaign, character, effect);
        }
    }

    /// <summary>
    /// Mirrors the same guards for every effect the traced movement path would trigger, so a
    /// caller can refuse the move before it spends a Reaction the throw would strand.
    /// </summary>
    private static void ValidateMovementBattlefieldEffects(CampaignState campaign, EncounterState encounter, CombatantState combatant, IReadOnlyList<(int X, int Y)> path)
    {
        if (!combatant.Positioned) return;
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.Dead) return;

        foreach (var effect in encounter.BattlefieldEffects)
        {
            if (effect.Trigger is not ("enter" or "start_or_enter" or "move_within")) continue;
            if (!path.Any(cell => BattlefieldEffectContainsCell(effect, cell.X, cell.Y))) continue;
            ValidateBattlefieldEffectDamage(campaign, character, effect);
        }
    }

    /// <summary>
    /// Raises the failure <see cref="TriggerBattlefieldEffect"/> would raise for this effect.
    /// </summary>
    private static void ValidateBattlefieldEffectDamage(CampaignState campaign, CharacterSheet character, BattlefieldEffectState effect)
    {
        if (!string.IsNullOrWhiteSpace(effect.DamageExpression) && !DiceService.TryValidateExpression(effect.DamageExpression))
            throw new InvalidOperationException($"Battlefield effect '{effect.Name}' has an unparseable damage expression '{effect.DamageExpression}'. The deterministic engine will not guess it.");
        if (campaign.PendingPlayerRoll?.Required == true
            && character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(character.ConcentrationEffect))
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");
    }

    private void ProcessBattlefieldEffectsOnMovement(
        CampaignState campaign,
        EncounterState encounter,
        CombatantState combatant,
        int fromX,
        int fromY,
        int toX,
        int toY,
        DiceService dice)
    {
        if (!combatant.Positioned) return;
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.Dead) return;
        var path = TraceGridPath(fromX, fromY, toX, toY);

        foreach (var effect in encounter.BattlefieldEffects.ToArray())
        {
            if (effect.Trigger == "move_within")
            {
                var previousInside = BattlefieldEffectContainsCell(effect, fromX, fromY);
                foreach (var cell in path)
                {
                    var inside = BattlefieldEffectContainsCell(effect, cell.X, cell.Y);
                    if (inside)
                    {
                        var triggerText = previousInside ? "moved 5 feet within" : "moved 5 feet into";
                        TriggerBattlefieldEffect(campaign, encounter, effect, combatant, dice, triggerText);
                        if (character.Dead || CharacterMechanics.HasCondition(character, "Unconscious")) break;
                    }
                    previousInside = inside;
                }
                if (character.Dead || CharacterMechanics.HasCondition(character, "Unconscious")) break;
                continue;
            }

            if (effect.Trigger is not ("enter" or "start_or_enter")) continue;
            var startedInside = BattlefieldEffectContainsCell(effect, fromX, fromY);
            var entered = !startedInside && path.Any(cell => BattlefieldEffectContainsCell(effect, cell.X, cell.Y));
            if (!entered) continue;
            TriggerBattlefieldEffect(campaign, encounter, effect, combatant, dice, "entered");
            if (character.Dead || CharacterMechanics.HasCondition(character, "Unconscious")) break;
        }
    }

    private void TriggerBattlefieldEffect(
        CampaignState campaign,
        EncounterState encounter,
        BattlefieldEffectState effect,
        CombatantState combatant,
        DiceService dice,
        string triggerText)
    {
        var character = RequireCharacter(campaign, combatant.CharacterId);
        effect.LastTriggeredTurnByCharacter ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stamp = $"{encounter.Round}:{encounter.TurnIndex}";
        if (effect.OncePerTurn
            && effect.LastTriggeredTurnByCharacter.TryGetValue(character.Id, out var previousStamp)
            && previousStamp == stamp)
            return;

        var rolledDamage = Math.Max(0, dice.RollDamage(effect.DamageExpression));
        D20TestResult? save = null;
        var appliedDamage = rolledDamage;
        if (!string.IsNullOrWhiteSpace(effect.SaveAbility) && effect.SaveDc > 0)
        {
            save = ResolveSavingThrowWithDice(campaign, character.Id, effect.SaveAbility, effect.SaveDc, dice);
            if (save.Success) appliedDamage = effect.HalfDamageOnSuccessfulSave ? rolledDamage / 2 : 0;
        }

        DamageResolutionResult? damage = null;
        if (appliedDamage > 0)
            damage = ApplyDamageWithConcentration(campaign, character.Id, appliedDamage, dice, effect.DamageType);

        effect.LastTriggeredTurnByCharacter[character.Id] = stamp;
        var saveText = save is null
            ? ""
            : save.Success
                ? $" {character.Name} succeeded on the {effect.SaveAbility} save ({save.Total} vs DC {effect.SaveDc})."
                : $" {character.Name} failed the {effect.SaveAbility} save ({save.Total} vs DC {effect.SaveDc}).";
        var damageText = appliedDamage > 0
            ? $" {character.Name} took {appliedDamage}{(string.IsNullOrWhiteSpace(effect.DamageType) ? "" : " " + effect.DamageType)} damage."
            : " No damage was applied.";
        var concentrationText = damage?.Concentration is null ? "" : " " + damage.Concentration.Summary;
        var summary = $"{character.Name} {triggerText} battlefield effect '{effect.Name}'.{saveText}{damageText}{concentrationText}".Trim();
        Touch(campaign);
        Log(campaign, "battlefield_effect_trigger", summary, dmOnly: effect.DmOnly);
    }

    private static void ExpireBattlefieldEffects(CampaignState campaign, EncounterState encounter)
    {
        foreach (var effect in encounter.BattlefieldEffects
            .Where(e => e.ExpiresAfterRound > 0 && encounter.Round > e.ExpiresAfterRound)
            .ToArray())
        {
            encounter.BattlefieldEffects.Remove(effect);
            Touch(campaign);
            Log(campaign, "battlefield_effect_removed", $"Battlefield effect '{effect.Name}' expired after round {effect.ExpiresAfterRound}.", dmOnly: effect.DmOnly);
        }
    }

    private static bool BattlefieldEffectContainsCell(BattlefieldEffectState effect, int x, int y) =>
        SpellAreaGeometry.ContainsCell(effect.Shape, effect.SizeFeet, effect.OriginX, effect.OriginY, x, y, effect.Direction, effect.WidthFeet);

    private static string NormalizeBattlefieldEffectShape(string? value) => (value ?? "sphere").Trim().ToLowerInvariant() switch
    {
        "sphere" => "sphere",
        "cone" => "cone",
        "cube" => "cube",
        "line" => "line",
        _ => throw new ArgumentException("Battlefield effect shape must be sphere, cone, cube, or line.")
    };

    private static string NormalizeBattlefieldEffectDirection(string? value)
    {
        var raw = (value ?? "north").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        _ = SpellAreaGeometry.NormalizeDirection(raw);
        return raw switch
        {
            "n" or "north" => "north",
            "ne" or "north-east" or "northeast" => "northeast",
            "e" or "east" => "east",
            "se" or "south-east" or "southeast" => "southeast",
            "s" or "south" => "south",
            "sw" or "south-west" or "southwest" => "southwest",
            "w" or "west" => "west",
            _ => "northwest"
        };
    }

    private static string NormalizeBattlefieldEffectTrigger(string? value) => (value ?? "none").Trim().ToLowerInvariant() switch
    {
        "" or "none" => "none",
        "start" or "start_turn" => "start_turn",
        "enter" => "enter",
        "start_or_enter" or "start-or-enter" or "start or enter" => "start_or_enter",
        "move_within" or "move-within" or "move within" => "move_within",
        _ => throw new ArgumentException("Battlefield effect trigger must be none, start_turn, enter, start_or_enter, or move_within.")
    };
}
