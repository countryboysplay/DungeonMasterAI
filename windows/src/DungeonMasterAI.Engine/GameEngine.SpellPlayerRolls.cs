using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private PendingRollRequest RequestPlayerSpellAttackRoll(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        int castAtLevel,
        bool usedSlot,
        bool ritual,
        bool concentrationStarted,
        EncounterState? encounter)
    {
        if (campaign.PendingPlayerRoll?.Required == true)
  throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

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
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);

        var pending = new PendingRollRequest
        {
  ActorCharacterId = caster.Id,
  EncounterId = encounter?.Id,
  CombatantId = casterCombatant?.Id,
  Formula = "1d20",
  RollType = "d20",
  RollMode = mode.ToString().ToLowerInvariant(),
  Purpose = $"{caster.Name} cast {spell.Name} at {target.Name}. Roll the spell attack d20{modeText} against AC {armorClass}{coverText}.",
  ResolutionKey = "spell_attack",
  Modifier = SpellAttackModifier(caster),
  TargetNumber = armorClass,
  TargetLabel = $"AC {armorClass}",
  Required = true,
  Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
  {
      ["spell_id"] = spell.Id,
      ["target_id"] = target.Id,
      ["cast_at_level"] = castAtLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["upcast_levels"] = upcastLevels.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["used_slot"] = usedSlot ? "true" : "false",
      ["ritual"] = ritual ? "true" : "false",
      ["concentration_started"] = concentrationStarted ? "true" : "false",
      ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["automatic_critical"] = automaticCritical ? "true" : "false"
  }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public SpellCastResult ResolvePendingSpellAttackRoll(
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
        if (!pending.ResolutionKey.Equals("spell_attack", StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not a spell attack.");

        var caster = RequireCharacter(campaign, pending.ActorCharacterId);
        if (!caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException("The pending spell attack no longer belongs to a player character.");
        var spell = RequirePendingSpell(campaign, pending);
        var target = RequirePendingSpellTarget(campaign, pending);
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");

        EncounterState? encounter = null;
        CombatantState? casterCombatant = null;
        CombatantState? targetCombatant = null;
        if (!string.IsNullOrWhiteSpace(pending.EncounterId))
        {
  encounter = RequireEncounter(campaign, pending.EncounterId);
  if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("The encounter is no longer active.");
  casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
  targetCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
  if (casterCombatant is null || targetCombatant is null)
      throw new InvalidOperationException("The pending spell attack combatants are no longer present in the encounter.");
  EnsureCurrentTurn(encounter, casterCombatant.Id);
        }
        ValidateSpellTargetType(target, spell);
        ValidateSpellRange(campaign, encounter, caster, target, spell);

        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
  throw new InvalidOperationException($"This spell attack requires two d20 results because it has {mode}.");
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
        var automaticCritical = SpellContextBool(pending, "automatic_critical");
        var hit = naturalCritical || (chosen != 1 && total >= armorClass);
        var critical = hit && (naturalCritical || automaticCritical);
        var helpUsed = encounter is not null && casterCombatant is not null && targetCombatant is not null
  && ConsumeHelpAttackAdvantage(encounter, casterCombatant, targetCombatant);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        if (encounter is not null && casterCombatant is not null)
  BreakHidden(campaign, encounter, casterCombatant, "making a spell attack roll");

        var coverBonus = SpellContextInt(pending, "cover_bonus");
        var coverText = coverBonus > 0 ? $" with {CoverLabel(coverBonus)} Cover (+{coverBonus} AC)" : "";
        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var helpText = helpUsed ? " Help supplied Advantage for this spell attack." : "";
        var effectBonusText = effectAttackBonus == 0 ? "" : $" +{effectAttackBonus} from active effects";
        var attackSummary = hit
  ? $"Spell attack{modeText} {total} vs AC {armorClass}{coverText}{effectBonusText}: hit{(critical ? " (critical)" : "")}.{helpText}"
  : $"Spell attack{modeText} {total} vs AC {armorClass}{coverText}{effectBonusText}: miss.{helpText}";
        var attack = new AttackResult(chosen, totalModifier, total, hit, critical, 0, attackSummary);
        var castAtLevel = SpellContextInt(pending, "cast_at_level");
        var usedSlot = SpellContextBool(pending, "used_slot");
        var ritual = SpellContextBool(pending, "ritual");
        var concentrationStarted = SpellContextBool(pending, "concentration_started");
        var upcastLevels = SpellContextInt(pending, "upcast_levels");

        if (!hit)
        {
  campaign.PendingPlayerRoll = null;
  var summary = BuildSpellAttackFinalSummary(caster, spell, castAtLevel, ritual, attackSummary);
  Touch(campaign);
  Log(campaign, "spell_cast", summary);
  return new SpellCastResult(spell.Id, spell.Name, caster.Id, target.Id, castAtLevel, usedSlot, ritual, attack, null, null, 0, concentrationStarted, summary);
        }

        var baseRolls = !string.IsNullOrWhiteSpace(spell.DamageExpression)
  ? spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1
  : 0;
        var extraRolls = !string.IsNullOrWhiteSpace(spell.ExtraDamagePerSlotExpression) ? upcastLevels : 0;
        if (baseRolls <= 0 && extraRolls <= 0)
        {
  campaign.PendingPlayerRoll = null;
  ApplySpellAttackHitEffects(campaign, encounter, caster, target, spell);
  var summary = BuildSpellAttackFinalSummary(caster, spell, castAtLevel, ritual, attackSummary);
  Touch(campaign);
  Log(campaign, "spell_cast", summary);
  return new SpellCastResult(spell.Id, spell.Name, caster.Id, target.Id, castAtLevel, usedSlot, ritual, attack, null, null, 0, concentrationStarted, summary);
        }

        var damageFormula = BuildPendingSpellDamageFormula(spell, baseRolls, extraRolls, critical);
        var damagePending = new PendingRollRequest
        {
  ActorCharacterId = caster.Id,
  EncounterId = encounter?.Id,
  CombatantId = casterCombatant?.Id,
  Formula = damageFormula,
  RollType = "damage",
  RollMode = "normal",
  Purpose = $"{caster.Name}'s {spell.Name} hit {target.Name}{(critical ? " critically" : "")}. Roll {damageFormula} damage.",
  ResolutionKey = "spell_attack_damage",
  Required = true,
  Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
  {
      ["spell_id"] = spell.Id,
      ["target_id"] = target.Id,
      ["cast_at_level"] = castAtLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["used_slot"] = usedSlot ? "true" : "false",
      ["ritual"] = ritual ? "true" : "false",
      ["concentration_started"] = concentrationStarted ? "true" : "false",
      ["damage_type"] = spell.DamageType,
      ["base_damage_expression"] = spell.DamageExpression,
      ["base_rolls"] = baseRolls.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["extra_damage_expression"] = spell.ExtraDamagePerSlotExpression,
      ["extra_rolls"] = extraRolls.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
        var pendingSummary = BuildSpellAttackFinalSummary(caster, spell, castAtLevel, ritual, $"{attackSummary} Damage roll required: {damageFormula}.");
        return new SpellCastResult(spell.Id, spell.Name, caster.Id, target.Id, castAtLevel, usedSlot, ritual, attack, null, null, 0, concentrationStarted, pendingSummary);
    }

    public SpellCastResult ResolvePendingSpellAttackDamageRoll(
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
        if (!pending.ResolutionKey.Equals("spell_attack_damage", StringComparison.OrdinalIgnoreCase))
  throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not spell attack damage.");

        var caster = RequireCharacter(campaign, pending.ActorCharacterId);
        var spell = RequirePendingSpell(campaign, pending);
        var target = RequirePendingSpellTarget(campaign, pending);
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");

        EncounterState? encounter = null;
        if (!string.IsNullOrWhiteSpace(pending.EncounterId))
        {
  encounter = RequireEncounter(campaign, pending.EncounterId);
  if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("The encounter is no longer active.");
  var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
      ?? throw new InvalidOperationException("The spellcaster is no longer in the active encounter.");
  EnsureCurrentTurn(encounter, casterCombatant.Id);
        }

        var critical = SpellContextBool(pending, "critical");
        var attackD20 = SpellContextInt(pending, "attack_d20");
        var attackModifier = SpellContextInt(pending, "attack_modifier");
        var attackTotal = SpellContextInt(pending, "attack_total");
        var armorClass = SpellContextInt(pending, "armor_class");
        var coverBonus = SpellContextInt(pending, "cover_bonus");
        var mode = ParsePendingRollMode(pending.Context.TryGetValue("attack_mode", out var attackMode) ? attackMode : null);
        var effectAttackBonus = SpellContextInt(pending, "effect_attack_bonus");
        var helpUsed = SpellContextBool(pending, "help_used");
        var castAtLevel = SpellContextInt(pending, "cast_at_level");
        var usedSlot = SpellContextBool(pending, "used_slot");
        var ritual = SpellContextBool(pending, "ritual");
        var concentrationStarted = SpellContextBool(pending, "concentration_started");
        var damageType = pending.Context.TryGetValue("damage_type", out var storedDamageType) ? storedDamageType : spell.DamageType;
        var concentrationBefore = target.ConcentrationEffect;

        // The damage request is satisfied before damage is committed. A concentrating PC
        // target is then free to replace it with the authoritative Concentration request.
        campaign.PendingPlayerRoll = null;
        var damage = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, damageType, critical);
        ApplySpellAttackHitEffects(campaign, encounter, caster, target, spell);

        var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
        var coverText = coverBonus > 0 ? $" with {CoverLabel(coverBonus)} Cover (+{coverBonus} AC)" : "";
        var helpText = helpUsed ? " Help supplied Advantage for this spell attack." : "";
        var effectBonusText = effectAttackBonus == 0 ? "" : $" +{effectAttackBonus} from active effects";
        var attackSummary = $"Spell attack{modeText} {attackTotal} vs AC {armorClass}{coverText}{effectBonusText}: hit for {damageAmount} {damageType} damage{(critical ? " (critical)" : "")}.{helpText}";
        var attack = new AttackResult(attackD20, attackModifier, attackTotal, true, critical, damageAmount, attackSummary);
        var summary = BuildSpellAttackFinalSummary(caster, spell, castAtLevel, ritual, attackSummary);
        if (damage.Concentration is not null) summary += $" {damage.Concentration.Summary}";
        else if (!string.IsNullOrWhiteSpace(concentrationBefore) && string.IsNullOrWhiteSpace(target.ConcentrationEffect) && damage.Damage.EffectiveDamage > 0)
  summary += $" {target.Name} lost Concentration on {concentrationBefore}.";
        if (spell.NextAttackAgainstTargetHasAdvantage && !target.Dead)
  summary += $" The next attack roll against {target.Name} has Advantage before the effect expires.";

        // Do not clear PendingPlayerRoll here: ApplyDamageWithConcentration may have created
        // a new player-controlled Concentration check for the target.
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return new SpellCastResult(spell.Id, spell.Name, caster.Id, target.Id, castAtLevel, usedSlot, ritual, attack, null, damage, 0, concentrationStarted, summary);
    }

    private static string BuildPendingSpellDamageFormula(SpellDefinition spell, int baseRolls, int extraRolls, bool critical)
    {
        var parts = new List<string>();
        for (var i = 0; i < baseRolls; i++) parts.Add(spell.DamageExpression);
        for (var i = 0; i < extraRolls; i++) parts.Add(spell.ExtraDamagePerSlotExpression);
        var formula = string.Join(" + ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        return critical ? $"{formula} (critical dice)" : formula;
    }

    private static string BuildSpellAttackFinalSummary(CharacterSheet caster, SpellDefinition spell, int castAtLevel, bool ritual, string effectSummary)
    {
        var slotText = spell.Level == 0
  ? "as a cantrip"
  : ritual
      ? "as a Ritual without expending a spell slot"
      : $"using a level {castAtLevel} spell slot";
        return $"{caster.Name} cast {spell.Name} {slotText}. {effectSummary}".Trim();
    }

    private void ApplySpellAttackHitEffects(CampaignState campaign, EncounterState? encounter, CharacterSheet caster, CharacterSheet target, SpellDefinition spell)
    {
        if (target.Dead) return;
        if (spell.NextAttackAgainstTargetHasAdvantage)
  ApplyNextAttackAdvantageEffect(campaign, encounter, caster, target, spell);
        if (spell.SpeedModifierFeet != 0 || spell.ArmorClassBonus != 0)
  ApplyD20BonusEffect(campaign, caster, target, spell);
    }

    private SpellDefinition RequirePendingSpell(CampaignState campaign, PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("spell_id", out var spellId) || string.IsNullOrWhiteSpace(spellId))
  throw new InvalidOperationException("The pending spell roll is missing its spell context.");
        return campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
  || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
  ?? throw new InvalidOperationException("The spell for the pending player roll no longer exists.");
    }

    private CharacterSheet RequirePendingSpellTarget(CampaignState campaign, PendingRollRequest pending)
    {
        if (!pending.Context.TryGetValue("target_id", out var targetId) || string.IsNullOrWhiteSpace(targetId))
  throw new InvalidOperationException("The pending spell roll is missing its target context.");
        return RequireCharacter(campaign, targetId);
    }

    private static int SpellContextInt(PendingRollRequest pending, string key, int fallback = 0)
    {
        return pending.Context.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool SpellContextBool(PendingRollRequest pending, string key)
    {
        return pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
    }
}
