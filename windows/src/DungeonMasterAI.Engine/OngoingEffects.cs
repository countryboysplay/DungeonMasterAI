using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private ActiveEffectState ApplySpellConditionEffect(
        CampaignState campaign,
        EncounterState? encounter,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        string condition,
        string repeatSaveAbility,
        int saveDc)
    {
        var ownsCondition = !CharacterMechanics.HasCondition(target, condition);
        if (ownsCondition) AddCondition(campaign, target.Id, condition);

        var effect = new ActiveEffectState
        {
            Name = spell.Name,
            SourceCharacterId = caster.Id,
            TargetCharacterId = target.Id,
            SourceSpellId = spell.Id,
            ConcentrationName = spell.RequiresConcentration ? spell.Name : "",
            RequiresSourceConcentration = spell.RequiresConcentration,
            Condition = condition,
            OwnsCondition = ownsCondition,
            RepeatSaveAbility = repeatSaveAbility,
            SaveDc = saveDc,
            RepeatSaveAtEndOfTurn = spell.RepeatSaveAtEndOfTurn,
            AppliedRound = encounter?.Round ?? 0,
            AppliedTurnIndex = encounter?.TurnIndex ?? -1
        };
        campaign.ActiveEffects.Add(effect);
        Touch(campaign);
        Log(campaign, "ongoing_effect_added", $"{target.Name} is affected by {spell.Name}: {condition}.");
        return effect;
    }

    private ActiveEffectState ApplyNextAttackAdvantageEffect(
        CampaignState campaign,
        EncounterState? encounter,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell)
    {
        // The same spell should not create duplicate unconsumed markers on one target.
        foreach (var existing in campaign.ActiveEffects
            .Where(e => e.TargetCharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
                && e.SourceSpellId.Equals(spell.Id, StringComparison.OrdinalIgnoreCase)
                && e.NextAttackAgainstTargetHasAdvantage)
            .ToArray())
            RemoveActiveEffect(campaign, existing, "replaced by a newer application");

        var effect = new ActiveEffectState
        {
            Name = spell.Name,
            SourceCharacterId = caster.Id,
            TargetCharacterId = target.Id,
            SourceSpellId = spell.Id,
            NextAttackAgainstTargetHasAdvantage = true,
            ConsumeOnNextAttackAgainst = true,
            ExpireAtEndOfSourceNextTurn = spell.EffectExpiresAtEndOfCasterNextTurn,
            AppliedRound = encounter?.Round ?? 0,
            AppliedTurnIndex = encounter?.TurnIndex ?? -1
        };
        campaign.ActiveEffects.Add(effect);
        Touch(campaign);
        Log(campaign, "ongoing_effect_added", $"The next attack roll against {target.Name} has Advantage from {spell.Name}.");
        return effect;
    }


    private ActiveEffectState ApplyD20BonusEffect(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell)
    {
        foreach (var existing in campaign.ActiveEffects
            .Where(e => e.TargetCharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
                && e.SourceSpellId.Equals(spell.Id, StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(e.AttackRollBonusExpression)
                    || !string.IsNullOrWhiteSpace(e.SavingThrowBonusExpression)
                    || e.SpeedModifierFeet != 0
                    || e.ArmorClassBonus != 0))
            .ToArray())
            RemoveActiveEffect(campaign, existing, "replaced by a newer application");

        var effect = new ActiveEffectState
        {
            Name = spell.Name,
            SourceCharacterId = caster.Id,
            TargetCharacterId = target.Id,
            SourceSpellId = spell.Id,
            ConcentrationName = spell.RequiresConcentration ? spell.Name : "",
            RequiresSourceConcentration = spell.RequiresConcentration,
            AttackRollBonusExpression = spell.AttackRollBonusExpression,
            SavingThrowBonusExpression = spell.SavingThrowBonusExpression,
            SpeedModifierFeet = spell.SpeedModifierFeet,
            ArmorClassBonus = spell.ArmorClassBonus,
            ExpireAtStartOfSourceNextTurn = spell.EffectExpiresAtStartOfCasterNextTurn
        };
        campaign.ActiveEffects.Add(effect);
        Touch(campaign);
        Log(campaign, "ongoing_effect_added", $"{target.Name} is affected by {spell.Name}.");
        return effect;
    }


    private static int EffectiveArmorClass(CampaignState campaign, CharacterSheet character)
    {
        var bonus = campaign.ActiveEffects
            .Where(e => e.TargetCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.ArmorClassBonus);
        return Math.Max(0, character.ArmorClass + bonus);
    }

    private static void ProcessStartOfTurnEffects(CampaignState campaign, CharacterSheet source)
    {
        foreach (var effect in campaign.ActiveEffects
            .Where(e => e.ExpireAtStartOfSourceNextTurn
                && e.SourceCharacterId.Equals(source.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray())
            RemoveActiveEffect(campaign, effect, "the source creature's next turn started");
    }

    private static int RollActiveAttackBonus(CampaignState campaign, string characterId, DiceService dice)
    {
        var total = 0;
        foreach (var effect in campaign.ActiveEffects.Where(e => e.TargetCharacterId.Equals(characterId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(e.AttackRollBonusExpression)))
            total += Math.Max(0, dice.Roll(effect.AttackRollBonusExpression).Total);
        return total;
    }

    private static int RollActiveSavingThrowBonus(CampaignState campaign, string characterId, DiceService dice)
    {
        var total = 0;
        foreach (var effect in campaign.ActiveEffects.Where(e => e.TargetCharacterId.Equals(characterId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(e.SavingThrowBonusExpression)))
            total += Math.Max(0, dice.Roll(effect.SavingThrowBonusExpression).Total);
        return total;
    }

    private static bool HasNextAttackAdvantageEffect(CampaignState campaign, string targetCharacterId) =>
        campaign.ActiveEffects.Any(e => e.TargetCharacterId.Equals(targetCharacterId, StringComparison.OrdinalIgnoreCase)
            && e.NextAttackAgainstTargetHasAdvantage);

    private static bool ConsumeNextAttackAdvantageEffect(CampaignState campaign, string targetCharacterId)
    {
        var effect = campaign.ActiveEffects.FirstOrDefault(e => e.TargetCharacterId.Equals(targetCharacterId, StringComparison.OrdinalIgnoreCase)
            && e.NextAttackAgainstTargetHasAdvantage);
        if (effect is null) return false;
        RemoveActiveEffect(campaign, effect, "consumed by the next attack roll");
        return true;
    }

    private void ProcessEndOfTurnEffects(CampaignState campaign, EncounterState encounter, CombatantState combatant, DiceService dice)
    {
        var character = RequireCharacter(campaign, combatant.CharacterId);

        foreach (var effect in campaign.ActiveEffects
            .Where(e => e.TargetCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase) && e.RepeatSaveAtEndOfTurn)
            .ToArray())
        {
            if (string.IsNullOrWhiteSpace(effect.RepeatSaveAbility) || effect.SaveDc <= 0) continue;
            var ability = CharacterMechanics.NormalizeAbility(effect.RepeatSaveAbility);
            var mode = CharacterMechanics.SavingThrowModeFromConditions(character, ability);
            var rolls = dice.RollD20(mode);
            var effectSaveBonus = RollActiveSavingThrowBonus(campaign, character.Id, dice);
            var save = ResolveSavingThrow(campaign, character.Id, ability, effect.SaveDc, rolls.RollOne, rolls.RollTwo, mode, effectSaveBonus);
            if (save.Success)
            {
                RemoveActiveEffect(campaign, effect, "the target succeeded on its end-of-turn saving throw");
                Log(campaign, "ongoing_effect_save", $"{character.Name} ended {effect.Name} with a successful {ability} saving throw ({save.Total} vs DC {effect.SaveDc}).");
            }
            else
            {
                Log(campaign, "ongoing_effect_save", $"{character.Name} remains affected by {effect.Name} after failing a {ability} saving throw ({save.Total} vs DC {effect.SaveDc}).");
            }
        }

        foreach (var effect in campaign.ActiveEffects
            .Where(e => e.SourceCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase)
                && e.ExpireAtEndOfSourceNextTurn
                && (e.AppliedRound != encounter.Round || e.AppliedTurnIndex != encounter.TurnIndex))
            .ToArray())
            RemoveActiveEffect(campaign, effect, "the source creature's next turn ended");
    }

    private static void RemoveAllEffectsOnTarget(CampaignState campaign, string targetCharacterId, string reason)
    {
        foreach (var effect in campaign.ActiveEffects
            .Where(e => e.TargetCharacterId.Equals(targetCharacterId, StringComparison.OrdinalIgnoreCase))
            .ToArray())
            RemoveActiveEffect(campaign, effect, reason);
    }

    private static void RemoveConcentrationBoundEffects(CampaignState campaign, string sourceCharacterId, string concentrationName, string reason)
    {
        foreach (var effect in campaign.ActiveEffects
            .Where(e => e.RequiresSourceConcentration
                && e.SourceCharacterId.Equals(sourceCharacterId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(concentrationName) || e.ConcentrationName.Equals(concentrationName, StringComparison.OrdinalIgnoreCase)))
            .ToArray())
            RemoveActiveEffect(campaign, effect, reason);

        foreach (var encounter in campaign.Encounters)
        {
            foreach (var battlefieldEffect in encounter.BattlefieldEffects
                .Where(e => e.RequiresSourceConcentration
                    && e.SourceCharacterId.Equals(sourceCharacterId, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(concentrationName) || e.ConcentrationName.Equals(concentrationName, StringComparison.OrdinalIgnoreCase)))
                .ToArray())
            {
                encounter.BattlefieldEffects.Remove(battlefieldEffect);
                Touch(campaign);
                Log(campaign, "battlefield_effect_removed", $"Battlefield effect '{battlefieldEffect.Name}' ended when Concentration ended ({reason}).", dmOnly: battlefieldEffect.DmOnly);
            }
        }
    }

    private static void RemoveActiveEffect(CampaignState campaign, ActiveEffectState effect, string reason)
    {
        if (!campaign.ActiveEffects.Contains(effect)) return;

        if (!string.IsNullOrWhiteSpace(effect.Condition) && effect.OwnsCondition)
        {
            var successor = campaign.ActiveEffects.FirstOrDefault(e => !ReferenceEquals(e, effect)
                && e.TargetCharacterId.Equals(effect.TargetCharacterId, StringComparison.OrdinalIgnoreCase)
                && e.Condition.Equals(effect.Condition, StringComparison.OrdinalIgnoreCase));
            if (successor is not null)
            {
                successor.OwnsCondition = true;
            }
            else
            {
                var target = campaign.Characters.FirstOrDefault(c => c.Id.Equals(effect.TargetCharacterId, StringComparison.OrdinalIgnoreCase));
                if (target is not null) RemoveConditionInternal(target, effect.Condition);
            }
        }

        campaign.ActiveEffects.Remove(effect);
        Touch(campaign);
        Log(campaign, "ongoing_effect_ended", $"{effect.Name} ended on {campaign.Characters.FirstOrDefault(c => c.Id == effect.TargetCharacterId)?.Name ?? effect.TargetCharacterId} ({reason}).");
    }
}
