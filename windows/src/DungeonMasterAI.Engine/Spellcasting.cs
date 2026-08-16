using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public SpellCastResult CastSpell(
        CampaignState campaign,
        string casterId,
        string spellId,
        DiceService dice,
        string? targetId = null,
        int? slotLevel = null,
        bool asRitual = false,
        string? encounterId = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var caster = RequireCharacter(campaign, casterId);
        if (caster.Dead || caster.CurrentHp <= 0)
            throw new InvalidOperationException($"{caster.Name} cannot cast while dead or at 0 Hit Points.");
        if (CharacterMechanics.IsIncapacitated(caster))
            throw new InvalidOperationException($"{caster.Name} is Incapacitated and cannot cast a spell.");

        var spell = campaign.Spells.FirstOrDefault(s =>
            s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"Spell '{spellId}' was not found in the campaign spell catalog.");

        if (!IsPrepared(caster, spell))
            throw new InvalidOperationException($"{caster.Name} does not have {spell.Name} prepared.");

        ValidateComponents(caster, spell);
        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);

        if (resolution == "area_save")
            throw new InvalidOperationException($"{spell.Name} is an area spell. Use the deterministic area-casting path so the battlefield geometry can be validated before any slot is spent.");
        if (resolution == "persistent_area")
            throw new InvalidOperationException($"{spell.Name} creates a persistent battlefield area. Use the persistent-area casting path so geometry, Concentration, and zone state are validated before any slot is spent.");
        if (resolution == "multi_buff")
            throw new InvalidOperationException($"{spell.Name} is a multi-target spell. Use the deterministic multi-target casting path so every target is validated before any slot is spent.");

        if (resolution is "projectile_auto" or "projectile_attack")
        {
            if (asRitual)
                throw new InvalidOperationException($"{spell.Name} is a projectile spell and cannot use the Ritual path in the deterministic engine.");
            if (string.IsNullOrWhiteSpace(targetId))
                throw new InvalidOperationException($"{spell.Name} requires at least one target.");
            return CastProjectileSpell(campaign, casterId, spellId, dice, [targetId], slotLevel, encounterId);
        }

        CharacterSheet? target = null;
        if (!string.IsNullOrWhiteSpace(targetId)) target = RequireCharacter(campaign, targetId);
        if (spell.RequiresTarget && target is null)
            throw new InvalidOperationException($"{spell.Name} requires a target.");
        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");
        ValidateSpellTargetType(target, spell);

        var activeEncounter = ResolveCastingEncounter(campaign, encounterId, caster.Id);
        if (activeEncounter is null && (spell.RepeatSaveAtEndOfTurn || spell.EffectExpiresAtEndOfCasterNextTurn))
            throw new InvalidOperationException($"{spell.Name} has a turn-timed deterministic effect and is currently resolved only inside an active encounter.");
        ValidateCastingTurn(activeEncounter, caster, spell);
        ValidateCastingActionEconomy(activeEncounter, caster, spell);
        ValidateReactionSpellAvailability(activeEncounter, caster, spell);
        ValidateSpellRange(campaign, activeEncounter, caster, target, spell);
        if (asRitual && activeEncounter is not null)
            throw new InvalidOperationException("Ritual casting is not resolved inside an active encounter in this alpha.");

        var usedSlot = false;
        var castAtLevel = 0;
        if (spell.Level == 0)
        {
            if (asRitual) throw new InvalidOperationException("Cantrips are cast without a spell slot and are not cast as Rituals.");
            castAtLevel = 0;
        }
        else if (asRitual)
        {
            if (!spell.Ritual) throw new InvalidOperationException($"{spell.Name} does not have the Ritual tag.");
            castAtLevel = spell.Level;
        }
        else
        {
            castAtLevel = slotLevel ?? FindLowestAvailableSlot(caster, spell.Level);
            if (castAtLevel < spell.Level || castAtLevel > 9)
                throw new InvalidOperationException($"{spell.Name} requires a level {spell.Level} or higher spell slot.");
            if (activeEncounter is not null && activeEncounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{caster.Name} has already expended a spell slot to cast a spell during the current turn.");
            SpendSpellSlot(campaign, caster.Id, castAtLevel);
            usedSlot = true;
            if (activeEncounter is not null && !activeEncounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase))
                activeEncounter.SpellSlotCasterIdsThisTurn.Add(caster.Id);
        }

        ConsumeCastingActionEconomy(activeEncounter, caster, spell);
        ConsumeReactionForSpell(activeEncounter, caster, spell);

        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        AttackResult? spellAttack = null;
        D20TestResult? savingThrow = null;
        DamageResolutionResult? damage = null;
        var healing = 0;
        var concentrationStarted = false;
        var effectSummary = "";

        if (spell.RequiresConcentration)
        {
            BeginConcentration(campaign, caster.Id, spell.Name);
            concentrationStarted = true;
        }

        switch (resolution)
        {
            case "attack":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target for its spell attack.");
                if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
                {
                    var pending = RequestPlayerSpellAttackRoll(
                        campaign,
                        caster,
                        target,
                        spell,
                        castAtLevel,
                        usedSlot,
                        asRitual,
                        concentrationStarted,
                        activeEncounter);
                    effectSummary = pending.Purpose;
                }
                else
                {
                    (spellAttack, damage, effectSummary) = ResolveSpellAttack(campaign, caster, target, spell, upcastLevels, dice, activeEncounter);
                }
                break;

            case "save":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target for its saving throw.");
                var saveAbility = string.IsNullOrWhiteSpace(spell.SaveAbility) ? "" : CharacterMechanics.NormalizeAbility(spell.SaveAbility);
                if (target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(saveAbility)
                    && !CharacterMechanics.AutomaticallyFailsSavingThrow(target, saveAbility))
                {
                    var pending = RequestPlayerSpellSavingThrowRoll(
                        campaign,
                        caster,
                        target,
                        spell,
                        castAtLevel,
                        usedSlot,
                        asRitual,
                        concentrationStarted,
                        activeEncounter);
                    effectSummary = pending.Purpose;
                }
                else if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(spell.DamageExpression))
                {
                    (savingThrow, effectSummary) = ResolveSaveForPlayerCasterBeforeDamage(
                        campaign,
                        caster,
                        target,
                        spell,
                        castAtLevel,
                        usedSlot,
                        asRitual,
                        concentrationStarted,
                        dice,
                        activeEncounter);
                }
                else
                {
                    (savingThrow, damage, effectSummary) = ResolveSaveSpell(campaign, caster, target, spell, upcastLevels, dice, activeEncounter);
                }
                break;

            case "healing":
                target ??= caster;
                healing = ResolveHealingSpell(campaign, caster, target, spell, upcastLevels, dice);
                effectSummary = $"{target.Name} regained {healing} Hit Points.";
                break;

            case "stabilize":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target.");
                effectSummary = ResolveStabilizingSpell(campaign, target, spell);
                break;

            case "utility":
                effectSummary = "The configured spell has no automatic numeric effect; its non-mechanical effect is left to the DM runtime.";
                break;

            default:
                if (resolution == "unsupported")
                    throw new InvalidOperationException($"{spell.Name} is in the rules catalog, but its deterministic effect is not implemented yet. The app will not guess the spell's mechanics.");
                throw new InvalidOperationException($"Spell resolution mode '{spell.Resolution}' is not supported.");
        }

        if (activeEncounter is not null && spell.RequiresVerbal)
        {
            var casterCombatant = activeEncounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
            if (casterCombatant is not null) BreakHidden(campaign, activeEncounter, casterCombatant, "casting a spell with a Verbal component");
        }

        var castingMinutes = ParseLongCastingTimeMinutes(spell.CastingTime);
        if (asRitual) castingMinutes += 10;
        if (castingMinutes > 0)
        {
            if (activeEncounter is not null)
                throw new InvalidOperationException("Long casting times and Ritual casting are not resolved inside an active encounter in this alpha.");
            AdvanceTime(campaign, castingMinutes);
        }

        Touch(campaign);
        var slotText = spell.Level == 0
            ? "as a cantrip"
            : asRitual
                ? "as a Ritual without expending a spell slot"
                : $"using a level {castAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}. {effectSummary}".Trim();
        Log(campaign, "spell_cast", summary);

        return new SpellCastResult(
            spell.Id,
            spell.Name,
            caster.Id,
            target?.Id,
            castAtLevel,
            usedSlot,
            asRitual,
            spellAttack,
            savingThrow,
            damage,
            healing,
            concentrationStarted,
            summary);
    }

    public SpellCastResult CastProjectileSpell(
        CampaignState campaign,
        string casterId,
        string spellId,
        DiceService dice,
        IReadOnlyList<string> targetIds,
        int? slotLevel = null,
        string? encounterId = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        ArgumentNullException.ThrowIfNull(targetIds);
        if (campaign.PendingPlayerRoll?.Required == true)
            throw new InvalidOperationException($"Resolve the required player roll first: {campaign.PendingPlayerRoll.Purpose}");

        var caster = RequireCharacter(campaign, casterId);
        if (caster.Dead || caster.CurrentHp <= 0)
            throw new InvalidOperationException($"{caster.Name} cannot cast while dead or at 0 Hit Points.");
        if (CharacterMechanics.IsIncapacitated(caster))
            throw new InvalidOperationException($"{caster.Name} is Incapacitated and cannot cast a spell.");

        var spell = campaign.Spells.FirstOrDefault(s =>
            s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"Spell '{spellId}' was not found in the campaign spell catalog.");
        if (!IsPrepared(caster, spell))
            throw new InvalidOperationException($"{caster.Name} does not have {spell.Name} prepared.");

        ValidateComponents(caster, spell);
        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);
        if (resolution is not ("projectile_auto" or "projectile_attack"))
            throw new InvalidOperationException($"{spell.Name} is not configured as a projectile spell.");
        if (spell.BaseProjectiles < 1)
            throw new InvalidOperationException($"{spell.Name} has no configured projectile count.");
        if (targetIds.Count == 0)
            throw new InvalidOperationException($"{spell.Name} requires at least one target allocation.");

        var activeEncounter = ResolveCastingEncounter(campaign, encounterId, caster.Id);
        ValidateCastingTurn(activeEncounter, caster, spell);
        ValidateCastingActionEconomy(activeEncounter, caster, spell);
        ValidateReactionSpellAvailability(activeEncounter, caster, spell);

        int castAtLevel;
        var usedSlot = spell.Level > 0;
        if (spell.Level == 0)
        {
            castAtLevel = 0;
        }
        else
        {
            castAtLevel = slotLevel ?? FindLowestAvailableSlot(caster, spell.Level);
            if (castAtLevel < spell.Level || castAtLevel > 9)
                throw new InvalidOperationException($"{spell.Name} requires a level {spell.Level} or higher spell slot.");
            if (!caster.SpellSlots.TryGetValue(castAtLevel, out var pool) || pool.Remaining <= 0)
                throw new InvalidOperationException($"{caster.Name} has no level {castAtLevel} spell slot available.");
            if (activeEncounter is not null && activeEncounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{caster.Name} has already expended a spell slot to cast a spell during the current turn.");
        }

        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        var projectileCount = checked(spell.BaseProjectiles + (upcastLevels * spell.ExtraProjectilesPerSlot));
        if (projectileCount < 1)
            throw new InvalidOperationException($"{spell.Name} resolved to an invalid projectile count.");

        var allocations = targetIds.Count == 1
            ? Enumerable.Repeat(targetIds[0], projectileCount).ToArray()
            : targetIds.ToArray();
        if (allocations.Length != projectileCount)
            throw new InvalidOperationException($"{spell.Name} creates {projectileCount} projectile{(projectileCount == 1 ? "" : "s")} at this slot level. Provide either one target to receive all projectiles or exactly {projectileCount} target allocations.");

        var targets = new CharacterSheet[allocations.Length];
        for (var i = 0; i < allocations.Length; i++)
        {
            var target = RequireCharacter(campaign, allocations[i]);
            if (target.Dead)
                throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for {spell.Name}.");
            ValidateSpellTargetType(target, spell);
            ValidateSpellRange(campaign, activeEncounter, caster, target, spell);
            targets[i] = target;
        }

        // No state is mutated until every allocation is validated.
        if (usedSlot)
        {
            SpendSpellSlot(campaign, caster.Id, castAtLevel);
            if (activeEncounter is not null) activeEncounter.SpellSlotCasterIdsThisTurn.Add(caster.Id);
        }
        ConsumeCastingActionEconomy(activeEncounter, caster, spell);
        ConsumeReactionForSpell(activeEncounter, caster, spell);

        var concentrationStarted = false;
        if (spell.RequiresConcentration)
        {
            BeginConcentration(campaign, caster.Id, spell.Name);
            concentrationStarted = true;
        }

        if (resolution == "projectile_attack" && caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
{
    if (activeEncounter is not null && spell.RequiresVerbal)
    {
        var casterCombatant = activeEncounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        if (casterCombatant is not null) BreakHidden(campaign, activeEncounter, casterCombatant, "casting a spell with a Verbal component");
    }
    return BeginPlayerProjectileSpellSequence(
        campaign,
        caster,
        spell,
        castAtLevel,
        usedSlot,
        concentrationStarted,
        activeEncounter,
        allocations,
        dice);
}

        if (resolution == "projectile_auto" && caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
{
    if (activeEncounter is not null && spell.RequiresVerbal)
    {
        var casterCombatant = activeEncounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        if (casterCombatant is not null) BreakHidden(campaign, activeEncounter, casterCombatant, "casting a spell with a Verbal component");
    }
    return BeginPlayerAutoProjectileSpellSequence(
        campaign,
        caster,
        spell,
        castAtLevel,
        usedSlot,
        concentrationStarted,
        activeEncounter,
        allocations,
        dice);
}

        var results = new List<SpellTargetResolution>(projectileCount);
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (resolution == "projectile_attack")
            {
                var (attack, attackDamage, attackSummary) = ResolveSpellAttack(campaign, caster, target, spell, 0, dice, activeEncounter);
                results.Add(new SpellTargetResolution(target.Id, target.Name, i + 1, attack, null, attackDamage, 0, attackSummary));
                continue;
            }

            // Auto-hit projectiles are separate damage instances. Targets were all validated
            // before any projectile resolved so a creature reduced to 0 HP by an earlier
            // simultaneous projectile remains part of the declared allocation.
            var rolledDamage = dice.RollDamage(spell.DamageExpression);
            DamageResolutionResult? damage = null;
            if (!target.Dead)
                damage = ApplyDamageWithConcentration(campaign, target.Id, rolledDamage, dice, spell.DamageType);
            var summary = target.Dead && damage is null
                ? $"Projectile {i + 1} was already allocated to {target.Name}; the target had been reduced to death by another simultaneously declared projectile before this damage instance was applied."
                : $"Projectile {i + 1} struck {target.Name} for {rolledDamage} {spell.DamageType} damage." + (damage?.Concentration is null ? "" : $" {damage.Concentration.Summary}");
            results.Add(new SpellTargetResolution(target.Id, target.Name, i + 1, null, null, damage, 0, summary));
        }

        if (activeEncounter is not null && spell.RequiresVerbal)
        {
            var casterCombatant = activeEncounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
            if (casterCombatant is not null) BreakHidden(campaign, activeEncounter, casterCombatant, "casting a spell with a Verbal component");
        }

        Touch(campaign);
        var distinctTargets = targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var slotText = spell.Level == 0 ? "as a cantrip" : $"using a level {castAtLevel} spell slot";
        var summaryText = $"{caster.Name} cast {spell.Name} {slotText}, resolving {projectileCount} projectile{(projectileCount == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. " + string.Join(" ", results.Select(r => r.Summary));
        Log(campaign, "spell_cast", summaryText);

        var onlyTarget = targets.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return new SpellCastResult(spell.Id, spell.Name, caster.Id, onlyTarget.Length == 1 ? onlyTarget[0] : null,
            castAtLevel, usedSlot, false, null, null, null, 0, concentrationStarted, summaryText, results);
    }


    public SpellCastResult CastMultiTargetSpell(
        CampaignState campaign,
        string casterId,
        string spellId,
        DiceService dice,
        IReadOnlyList<string> targetIds,
        int? slotLevel = null,
        string? encounterId = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        ArgumentNullException.ThrowIfNull(targetIds);

        var caster = RequireCharacter(campaign, casterId);
        if (caster.Dead || caster.CurrentHp <= 0 || CharacterMechanics.IsIncapacitated(caster))
            throw new InvalidOperationException($"{caster.Name} cannot cast this spell right now.");
        var spell = campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"Spell '{spellId}' was not found in the campaign spell catalog.");
        if (!IsPrepared(caster, spell)) throw new InvalidOperationException($"{caster.Name} does not have {spell.Name} prepared.");
        ValidateComponents(caster, spell);
        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);
        if (resolution != "multi_buff") throw new InvalidOperationException($"{spell.Name} is not configured as a deterministic multi-target buff spell.");
        if (spell.BaseTargets < 1) throw new InvalidOperationException($"{spell.Name} has no configured target count.");

        var encounter = ResolveCastingEncounter(campaign, encounterId, caster.Id);
        ValidateCastingTurn(encounter, caster, spell);
        ValidateCastingActionEconomy(encounter, caster, spell);
        ValidateReactionSpellAvailability(encounter, caster, spell);

        var castAtLevel = spell.Level == 0 ? 0 : slotLevel ?? FindLowestAvailableSlot(caster, spell.Level);
        if (spell.Level > 0)
        {
            if (castAtLevel < spell.Level || castAtLevel > 9) throw new InvalidOperationException($"{spell.Name} requires a level {spell.Level} or higher spell slot.");
            if (!caster.SpellSlots.TryGetValue(castAtLevel, out var pool) || pool.Remaining <= 0) throw new InvalidOperationException($"{caster.Name} has no level {castAtLevel} spell slot available.");
            if (encounter is not null && encounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{caster.Name} has already expended a spell slot to cast a spell during the current turn.");
        }

        var maximumTargets = checked(spell.BaseTargets + Math.Max(0, castAtLevel - spell.Level) * spell.ExtraTargetsPerSlot);
        var distinctIds = targetIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctIds.Length < 1 || distinctIds.Length > maximumTargets)
            throw new InvalidOperationException($"{spell.Name} can target from 1 to {maximumTargets} creature{(maximumTargets == 1 ? "" : "s")} at this slot level.");
        var targets = distinctIds.Select(id => RequireCharacter(campaign, id)).ToArray();
        foreach (var target in targets)
        {
            if (target.Dead) throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for {spell.Name}.");
            ValidateSpellTargetType(target, spell);
            ValidateSpellRange(campaign, encounter, caster, target, spell);
        }

        // Mutation begins only after every target has passed validation.
        if (spell.Level > 0)
        {
            SpendSpellSlot(campaign, caster.Id, castAtLevel);
            if (encounter is not null) encounter.SpellSlotCasterIdsThisTurn.Add(caster.Id);
        }
        ConsumeCastingActionEconomy(encounter, caster, spell);
        ConsumeReactionForSpell(encounter, caster, spell);
        var concentrationStarted = false;
        if (spell.RequiresConcentration)
        {
            BeginConcentration(campaign, caster.Id, spell.Name);
            concentrationStarted = true;
        }
        foreach (var target in targets) ApplyD20BonusEffect(campaign, caster, target, spell);
        if (encounter is not null && spell.RequiresVerbal)
        {
            var cc = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
            if (cc is not null) BreakHidden(campaign, encounter, cc, "casting a spell with a Verbal component");
        }

        var targetResults = targets.Select((t, i) =>
        {
            var benefits = new List<string>();
            if (!string.IsNullOrWhiteSpace(spell.AttackRollBonusExpression)) benefits.Add($"attack rolls +{spell.AttackRollBonusExpression}");
            if (!string.IsNullOrWhiteSpace(spell.SavingThrowBonusExpression)) benefits.Add($"saving throws +{spell.SavingThrowBonusExpression}");
            if (spell.ArmorClassBonus != 0) benefits.Add($"AC {(spell.ArmorClassBonus > 0 ? "+" : "")}{spell.ArmorClassBonus}");
            if (spell.SpeedModifierFeet != 0) benefits.Add($"Speed {(spell.SpeedModifierFeet > 0 ? "+" : "")}{spell.SpeedModifierFeet} ft");
            return new SpellTargetResolution(t.Id, t.Name, i + 1, null, null, null, 0, $"{t.Name} gained {spell.Name}: {string.Join(", ", benefits)}.");
        }).ToArray();
        var summary = $"{caster.Name} cast {spell.Name} using a level {castAtLevel} spell slot on {string.Join(", ", targets.Select(t => t.Name))}.";
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return new SpellCastResult(spell.Id, spell.Name, caster.Id, targets.Length == 1 ? targets[0].Id : null, castAtLevel, spell.Level > 0,
            false, null, null, null, 0, concentrationStarted, summary, targetResults);
    }

    public SpellCastResult CastAreaSpell(
        CampaignState campaign,
        string casterId,
        string spellId,
        DiceService dice,
        int? centerX = null,
        int? centerY = null,
        string direction = "north",
        int? slotLevel = null,
        string? encounterId = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        var caster = RequireCharacter(campaign, casterId);
        if (caster.Dead || caster.CurrentHp <= 0 || CharacterMechanics.IsIncapacitated(caster))
            throw new InvalidOperationException($"{caster.Name} cannot cast this spell right now.");
        var spell = campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"Spell '{spellId}' was not found in the campaign spell catalog.");
        if (!IsPrepared(caster, spell)) throw new InvalidOperationException($"{caster.Name} does not have {spell.Name} prepared.");
        ValidateComponents(caster, spell);
        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);
        if (resolution != "area_save") throw new InvalidOperationException($"{spell.Name} is not configured as a deterministic area spell.");
        if (string.IsNullOrWhiteSpace(spell.AreaShape) || spell.AreaSizeFeet <= 0) throw new InvalidOperationException($"{spell.Name} has incomplete area geometry metadata.");
        if (string.IsNullOrWhiteSpace(spell.SaveAbility)) throw new InvalidOperationException($"{spell.Name} has no configured saving throw ability.");

        var encounter = ResolveCastingEncounter(campaign, encounterId, caster.Id)
            ?? throw new InvalidOperationException($"{spell.Name}'s tactical area is currently resolved only inside an active encounter.");
        ValidateCastingTurn(encounter, caster, spell);
        ValidateCastingActionEconomy(encounter, caster, spell);
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{caster.Name} is not a combatant in the active encounter.");
        if (!casterCombatant.Positioned) throw new InvalidOperationException($"{caster.Name} must be positioned on the battlefield before casting {spell.Name}.");

        var castAtLevel = spell.Level == 0 ? 0 : slotLevel ?? FindLowestAvailableSlot(caster, spell.Level);
        if (spell.Level > 0)
        {
            if (castAtLevel < spell.Level || castAtLevel > 9) throw new InvalidOperationException($"{spell.Name} requires a level {spell.Level} or higher spell slot.");
            if (!caster.SpellSlots.TryGetValue(castAtLevel, out var pool) || pool.Remaining <= 0) throw new InvalidOperationException($"{caster.Name} has no level {castAtLevel} spell slot available.");
            if (encounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{caster.Name} has already expended a spell slot to cast a spell during the current turn.");
        }

        var shape = spell.AreaShape.Trim().ToLowerInvariant();
        var origin = (spell.AreaOrigin ?? "self").Trim().ToLowerInvariant();
        var pointX = origin == "point" ? centerX ?? throw new InvalidOperationException($"{spell.Name} requires an area center X coordinate.") : casterCombatant.GridX;
        var pointY = origin == "point" ? centerY ?? throw new InvalidOperationException($"{spell.Name} requires an area center Y coordinate.") : casterCombatant.GridY;
        if (origin == "point")
        {
            var range = Math.Sqrt(Math.Pow(pointX - casterCombatant.GridX, 2) + Math.Pow(pointY - casterCombatant.GridY, 2)) * 5.0;
            if (spell.RangeFeet > 0 && range > spell.RangeFeet + 0.001) throw new InvalidOperationException($"The chosen center is {range:0.#} feet away, beyond {spell.Name}'s range of {spell.RangeFeet} feet.");
            if (GetAreaCoverBonus(encounter, casterCombatant.GridX, casterCombatant.GridY, pointX, pointY) >= 100)
                throw new InvalidOperationException($"{spell.Name}'s chosen point of origin is behind Total Cover or a line-of-effect blocking feature. Choose a point on the near side of the obstruction.");
        }
        _ = SpellAreaGeometry.NormalizeDirection(direction); // Validate the direction even when the selected shape does not use it.
        var affectedCombatants = encounter.Combatants.Where(c => c.Positioned)
            .Where(c => AreaContains(shape, spell.AreaSizeFeet, origin, casterCombatant, c, pointX, pointY, direction))
            .Where(c => GetAreaCoverBonus(encounter, pointX, pointY, c.GridX, c.GridY) < 100)
            .ToArray();
        if (affectedCombatants.Length == 0) throw new InvalidOperationException($"No positioned creatures are inside {spell.Name}'s area with an unblocked line of effect from its point of origin.");

        if (spell.Level > 0)
        {
            SpendSpellSlot(campaign, caster.Id, castAtLevel);
            encounter.SpellSlotCasterIdsThisTurn.Add(caster.Id);
        }
        ConsumeCastingActionEconomy(encounter, caster, spell);
        if (spell.RequiresConcentration) BeginConcentration(campaign, caster.Id, spell.Name);
        if (spell.RequiresVerbal) BreakHidden(campaign, encounter, casterCombatant, "casting a spell with a Verbal component");

        return BeginPlayerAreaSpellSequence(
        campaign,
        caster,
        spell,
        castAtLevel,
        spell.Level > 0,
        spell.RequiresConcentration,
        encounter,
        pointX,
        pointY,
        direction,
        affectedCombatants.Select(c => c.Id).ToArray(),
        dice);
}

    private static bool AreaContains(
        string shape,
        int sizeFeet,
        string origin,
        CombatantState caster,
        CombatantState target,
        int pointX,
        int pointY,
        string? direction)
    {
        if (origin == "self" && target.Id.Equals(caster.Id, StringComparison.OrdinalIgnoreCase)) return false;
        return SpellAreaGeometry.ContainsCell(shape, sizeFeet, pointX, pointY, target.GridX, target.GridY, direction);
    }

    private static int GetAreaCoverBonus(EncounterState encounter, int originX, int originY, int targetX, int targetY)
    {
        var best = 0;
        foreach (var (x, y) in SpellAreaGeometry.TraceGridLine(originX, originY, targetX, targetY))
        {
            foreach (var terrain in encounter.Terrain.Where(t => ContainsSquare(t, x, y)))
            {
                var cover = NormalizeCover(terrain.Cover);
                if (terrain.BlocksLineOfSight || cover == "total") return 100;
                if (cover == "three-quarters") best = Math.Max(best, 5);
                else if (cover == "half") best = Math.Max(best, 2);
            }
            if (encounter.BattlefieldEffects.Any(e => e.BlocksLineOfSight && BattlefieldEffectContainsCell(e, x, y)))
                return 100;
        }
        return best;
    }

    private static int PushAreaTarget(EncounterState encounter, int sourceX, int sourceY, CombatantState target, int feet)
    {
        var dx = Math.Sign(target.GridX - sourceX);
        var dy = Math.Sign(target.GridY - sourceY);
        if (dx == 0 && dy == 0) return 0;
        var squares = Math.Max(0, feet / 5);
        var moved = 0;
        for (var i = 0; i < squares; i++)
        {
            var nx = target.GridX + dx;
            var ny = target.GridY + dy;
            if (encounter.Terrain.Any(t => t.BlocksMovement && ContainsSquare(t, nx, ny))) break;
            if (encounter.Combatants.Any(c => c.Positioned && !c.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase) && c.GridX == nx && c.GridY == ny)) break;
            target.GridX = nx;
            target.GridY = ny;
            moved += 5;
        }
        return moved;
    }

    public ReadyActionResult TakeReadySpell(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string spellId,
        string trigger,
        int? slotLevel = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var caster = RequireCharacter(campaign, combatant.CharacterId);
        if (CharacterMechanics.IsIncapacitated(caster) || caster.CurrentHp <= 0)
            throw new InvalidOperationException($"{caster.Name} is Incapacitated and cannot Ready a spell.");

        var spell = campaign.Spells.FirstOrDefault(s =>
            s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"Spell '{spellId}' was not found in the campaign spell catalog.");
        if (!IsPrepared(caster, spell))
            throw new InvalidOperationException($"{caster.Name} does not have {spell.Name} prepared.");
        if (!(spell.CastingTime ?? "Action").Trim().Equals("Action", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Only a spell with a casting time of 1 Action can be readied. {spell.Name} has casting time '{spell.CastingTime}'.");

        ValidateComponents(caster, spell);
        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);
        if (resolution is "multi_buff" or "persistent_area")
            throw new InvalidOperationException($"Readying {spell.Name}'s multi-target or persistent-area resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");
        var normalizedTrigger = NormalizeReadyTrigger(trigger);
        if (!combatant.ActionAvailable)
            throw new InvalidOperationException($"{caster.Name} has already used their action this turn and cannot take the Ready action.");

        var castAtLevel = 0;
        var usedSlot = false;
        if (spell.Level > 0)
        {
            castAtLevel = slotLevel ?? FindLowestAvailableSlot(caster, spell.Level);
            if (castAtLevel < spell.Level || castAtLevel > 9)
                throw new InvalidOperationException($"{spell.Name} requires a level {spell.Level} or higher spell slot.");
            if (!caster.SpellSlots.TryGetValue(castAtLevel, out var pool) || pool.Remaining <= 0)
                throw new InvalidOperationException($"{caster.Name} has no level {castAtLevel} spell slot available.");
            if (encounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{caster.Name} has already expended a spell slot to cast a spell during the current turn.");
            usedSlot = true;
        }

        ConsumeAction(combatant, caster, "Ready");
        if (usedSlot)
        {
            SpendSpellSlot(campaign, caster.Id, castAtLevel);
            encounter.SpellSlotCasterIdsThisTurn.Add(caster.Id);
        }
        if (spell.RequiresVerbal)
            BreakHidden(campaign, encounter, combatant, "casting a spell with a Verbal component");

        var heldEffect = ReadiedSpellConcentrationLabel(spell);
        BeginConcentration(campaign, caster.Id, heldEffect);
        combatant.ReadiedAction = new ReadiedActionState
        {
            Trigger = normalizedTrigger,
            Kind = "spell",
            SpellId = spell.Id,
            CastAtLevel = castAtLevel,
            UsedSpellSlot = usedSlot,
            PreparedRound = encounter.Round,
            PreparedTurnIndex = encounter.TurnIndex
        };

        Touch(campaign);
        var slotText = spell.Level == 0 ? "as a cantrip" : $"using a level {castAtLevel} spell slot";
        var summary = $"{caster.Name} cast and readied {spell.Name} {slotText}, holding its energy with Concentration for the trigger: {normalizedTrigger}";
        Log(campaign, "ready_spell", summary);
        return new ReadyActionResult(encounter.Id, combatant.Id, caster.Id, "spell", normalizedTrigger, summary);
    }

    public SpellCastResult TriggerReadiedSpell(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        DiceService dice,
        string? targetCombatantId = null,
        int? areaCenterX = null,
        int? areaCenterY = null,
        string? areaDirection = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, reactorCombatantId);
        var caster = RequireCharacter(campaign, combatant.CharacterId);
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
        if (spell.RequiresTarget && target is null)
            throw new InvalidOperationException($"{spell.Name} requires a target when the readied spell is released.");
        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");
        ValidateSpellTargetType(target, spell);
        ValidateSpellRange(campaign, encounter, caster, target, spell);

        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);
        var castAtLevel = spell.Level == 0 ? 0 : Math.Max(spell.Level, readied.CastAtLevel);
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);

        combatant.ReactionAvailable = false;
        combatant.ReadiedAction = null;
        if (spell.RequiresConcentration)
            BeginConcentration(campaign, caster.Id, spell.Name);
        else
            EndConcentrationInternal(campaign, caster, $"releasing the readied {spell.Name}");

        AttackResult? spellAttack = null;
        D20TestResult? savingThrow = null;
        DamageResolutionResult? damage = null;
        var healing = 0;
        IReadOnlyList<SpellTargetResolution>? targetResults = null;
        var effectSummary = "";
        switch (resolution)
        {
            case "attack":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target for its spell attack.");
                if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
                {
                    var pending = RequestPlayerSpellAttackRoll(
                        campaign,
                        caster,
                        target,
                        spell,
                        castAtLevel,
                        readied.UsedSpellSlot,
                        false,
                        spell.RequiresConcentration,
                        encounter);
                    pending.Context["readied_reaction"] = "true";
                    effectSummary = pending.Purpose;
                }
                else
                {
                    (spellAttack, damage, effectSummary) = ResolveSpellAttack(campaign, caster, target, spell, upcastLevels, dice, encounter);
                }
                break;
            case "save":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target for its saving throw.");
                var saveAbility = string.IsNullOrWhiteSpace(spell.SaveAbility) ? "" : CharacterMechanics.NormalizeAbility(spell.SaveAbility);
                if (target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(saveAbility)
                    && !CharacterMechanics.AutomaticallyFailsSavingThrow(target, saveAbility))
                {
                    var pending = RequestPlayerSpellSavingThrowRoll(
                        campaign,
                        caster,
                        target,
                        spell,
                        castAtLevel,
                        readied.UsedSpellSlot,
                        false,
                        spell.RequiresConcentration,
                        encounter);
                    pending.Context["readied_reaction"] = "true";
                    effectSummary = pending.Purpose;
                }
                else if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(spell.DamageExpression))
                {
                    (savingThrow, effectSummary) = ResolveSaveForPlayerCasterBeforeDamage(
                        campaign,
                        caster,
                        target,
                        spell,
                        castAtLevel,
                        readied.UsedSpellSlot,
                        false,
                        spell.RequiresConcentration,
                        dice,
                        encounter);
                    MarkReadiedSpellPending(campaign);
                }
                else
                {
                    (savingThrow, damage, effectSummary) = ResolveSaveSpell(campaign, caster, target, spell, upcastLevels, dice, encounter);
                }
                break;
            case "area_save":
                var areaResult = BeginReadiedAreaSpellSequence(
                    campaign,
                    caster,
                    spell,
                    castAtLevel,
                    readied.UsedSpellSlot,
                    encounter,
                    areaCenterX,
                    areaCenterY,
                    areaDirection,
                    dice);
                effectSummary = areaResult.Summary;
                targetResults = areaResult.TargetResults;
                break;
            case "projectile_attack":
            case "projectile_auto":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target when the readied projectile spell is released.");
                var projectileResult = BeginReadiedProjectileSpellSequence(
                    campaign,
                    caster,
                    target,
                    spell,
                    castAtLevel,
                    readied.UsedSpellSlot,
                    encounter,
                    dice);
                effectSummary = projectileResult.Summary;
                targetResults = projectileResult.TargetResults;
                break;
            case "healing":
                target ??= caster;
                healing = ResolveHealingSpell(campaign, caster, target, spell, upcastLevels, dice);
                effectSummary = $"{target.Name} regained {healing} Hit Points.";
                break;
            case "stabilize":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target.");
                effectSummary = ResolveStabilizingSpell(campaign, target, spell);
                break;
            case "utility":
                effectSummary = "The configured spell has no automatic numeric effect; its non-mechanical effect is left to the DM runtime.";
                break;
            default:
                throw new InvalidOperationException($"Spell resolution mode '{spell.Resolution}' is not supported for a readied spell.");
        }

        Touch(campaign);
        var slotText = spell.Level == 0 ? "as a cantrip" : $"from the level {castAtLevel} slot expended when it was readied";
        var summary = $"{caster.Name} used a Reaction to release readied {spell.Name} {slotText}. {effectSummary}".Trim();
        Log(campaign, "ready_spell_triggered", summary);
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            caster.Id,
            target?.Id,
            castAtLevel,
            readied.UsedSpellSlot,
            false,
            spellAttack,
            savingThrow,
            damage,
            healing,
            spell.RequiresConcentration,
            summary,
            targetResults);
    }

    private static string ReadiedSpellConcentrationLabel(SpellDefinition spell) => $"Readied spell: {spell.Name}";

    public int SpellSaveDc(CharacterSheet caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        var ability = string.IsNullOrWhiteSpace(caster.SpellcastingAbility) ? "intelligence" : caster.SpellcastingAbility;
        return 8 + CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(caster, ability)) + Math.Max(0, caster.ProficiencyBonus);
    }

    public int SpellAttackModifier(CharacterSheet caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        var ability = string.IsNullOrWhiteSpace(caster.SpellcastingAbility) ? "intelligence" : caster.SpellcastingAbility;
        return CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(caster, ability)) + Math.Max(0, caster.ProficiencyBonus);
    }

    private (AttackResult Attack, DamageResolutionResult? Damage, string Summary) ResolveSpellAttack(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        int upcastLevels,
        DiceService dice,
        EncounterState? encounter)
    {
        var modifier = SpellAttackModifier(caster);
        var coverBonus = GetSpellCoverBonus(encounter, caster.Id, target.Id);
        var effectiveArmorClass = EffectiveArmorClass(campaign, target) + coverBonus;
        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var attackMode = encounter is not null && casterCombatant is not null && targetCombatant is not null
            ? AttackRollMode(campaign, encounter, casterCombatant, targetCombatant, caster, target)
            : D20RollMode.Normal;
        var helpUsed = encounter is not null && casterCombatant is not null && targetCombatant is not null
            && ConsumeHelpAttackAdvantage(encounter, casterCombatant, targetCombatant);
        var attackRolls = dice.RollD20(attackMode);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        var d20 = attackRolls.ChosenRoll;
        var effectAttackBonus = RollActiveAttackBonus(campaign, caster.Id, dice);
        var total = d20 + modifier + effectAttackBonus;
        var naturalCritical = d20 == 20;
        var hit = naturalCritical || (d20 != 1 && total >= effectiveArmorClass);
        var critical = hit && (naturalCritical || (casterCombatant is not null && targetCombatant is not null && IsAutomaticCriticalHitTarget(casterCombatant, targetCombatant, target)));
        if (encounter is not null && casterCombatant is not null) BreakHidden(campaign, encounter, casterCombatant, "making an attack roll");
        var rolledDamage = 0;
        DamageResolutionResult? damage = null;

        if (hit)
        {
            var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
            for (var i = 0; i < baseRolls; i++)
                rolledDamage += dice.RollDamage(spell.DamageExpression, critical);
            for (var i = 0; i < upcastLevels; i++)
                rolledDamage += dice.RollDamage(spell.ExtraDamagePerSlotExpression, critical);
            if (rolledDamage > 0)
                damage = ApplyDamageWithConcentration(campaign, target.Id, rolledDamage, dice, spell.DamageType, critical);
            if (spell.NextAttackAgainstTargetHasAdvantage && !target.Dead)
                ApplyNextAttackAdvantageEffect(campaign, encounter, caster, target, spell);
            if ((spell.SpeedModifierFeet != 0 || spell.ArmorClassBonus != 0) && !target.Dead)
                ApplyD20BonusEffect(campaign, caster, target, spell);
        }

        var coverText = coverBonus > 0 ? $" with {CoverLabel(coverBonus)} Cover (+{coverBonus} AC)" : "";
        var modeText = attackMode == D20RollMode.Normal ? "" : $" with {attackMode}";
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var effectBonusText = effectAttackBonus > 0 ? $" +{effectAttackBonus} from active effects" : "";
        var attackSummary = hit
            ? $"Spell attack{modeText} {total} vs AC {effectiveArmorClass}{coverText}{effectBonusText}: hit for {rolledDamage} {spell.DamageType} damage{(critical ? " (critical)" : "")}.{helpText}"
            : $"Spell attack{modeText} {total} vs AC {effectiveArmorClass}{coverText}{effectBonusText}: miss.{helpText}";
        var attack = new AttackResult(d20, modifier + effectAttackBonus, total, hit, critical, rolledDamage, attackSummary);
        var summary = damage?.Concentration is null ? attackSummary : $"{attackSummary} {damage.Concentration.Summary}";
        if (hit && spell.NextAttackAgainstTargetHasAdvantage && !target.Dead)
            summary += $" The next attack roll against {target.Name} has Advantage before the effect expires.";
        return (attack, damage, summary);
    }

    private (D20TestResult Save, DamageResolutionResult? Damage, string Summary) ResolveSaveSpell(
        CampaignState campaign,
        CharacterSheet caster,
        CharacterSheet target,
        SpellDefinition spell,
        int upcastLevels,
        DiceService dice,
        EncounterState? encounter)
    {
        if (string.IsNullOrWhiteSpace(spell.SaveAbility))
            throw new InvalidOperationException($"{spell.Name} is configured as a saving-throw spell but has no save ability.");

        var dc = SpellSaveDc(caster);
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var dodgeMode = ability.Equals("dexterity", StringComparison.OrdinalIgnoreCase) && targetCombatant is not null && IsDodgeActive(campaign, targetCombatant, target)
            ? D20RollMode.Advantage
            : D20RollMode.Normal;
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(target, ability);
        var typeMode = !string.IsNullOrWhiteSpace(spell.SaveDisadvantageCreatureType)
            && target.CreatureType.Equals(spell.SaveDisadvantageCreatureType, StringComparison.OrdinalIgnoreCase)
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
        var saveMode = CombineAdvantage(CombineAdvantage(dodgeMode, conditionMode), typeMode);
        var roll = dice.RollD20(saveMode);
        var proficient = target.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase) ||
            x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase));
        var coverBonus = ability.Equals("dexterity", StringComparison.OrdinalIgnoreCase) && !spell.IgnoreHalfAndThreeQuartersCoverOnSave
            ? GetSpellCoverBonus(encounter, caster.Id, target.Id)
            : 0;
        D20TestResult save;
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
        {
            var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, ability));
            var proficiencyModifier = proficient ? Math.Max(0, target.ProficiencyBonus) : 0;
            var exhaustionPenalty = 2 * Math.Clamp(target.ExhaustionLevel, 0, 6);
            var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
            var total = roll.ChosenRoll + abilityModifier + proficiencyModifier + coverBonus + effectSaveBonus - exhaustionPenalty;
            save = new D20TestResult(roll.RollOne, roll.RollTwo, roll.ChosenRoll, abilityModifier, proficiencyModifier, exhaustionPenalty, total, dc, false, $"{ability} saving throw automatically failed because {target.Name}'s condition causes automatic failure on Strength and Dexterity saving throws.");
        }
        else
        {
            var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
            save = CharacterMechanics.ResolveD20Test(target, ability, dc, roll.RollOne, roll.RollTwo, saveMode, proficient, coverBonus + effectSaveBonus);
        }

        var rolledDamage = 0;
        if (!string.IsNullOrWhiteSpace(spell.DamageExpression))
        {
            var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
            for (var i = 0; i < baseRolls; i++)
                rolledDamage += dice.RollDamage(spell.DamageExpression);
            for (var i = 0; i < upcastLevels; i++)
                rolledDamage += dice.RollDamage(spell.ExtraDamagePerSlotExpression);
        }

        var appliedDamage = save.Success
            ? spell.HalfDamageOnSuccessfulSave ? rolledDamage / 2 : 0
            : rolledDamage;
        DamageResolutionResult? damage = null;
        if (appliedDamage > 0)
            damage = ApplyDamageWithConcentration(campaign, target.Id, appliedDamage, dice, spell.DamageType);
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave))
            ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, dc);

        var saveCoverText = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} Dexterity save)" : "";
        var dodgeSaveText = dodgeMode == D20RollMode.Advantage && conditionMode == D20RollMode.Normal ? " with Advantage from Dodge" : "";
        var restraintSaveText = conditionMode == D20RollMode.Disadvantage && dodgeMode == D20RollMode.Normal ? " with Disadvantage from Restrained" : "";
        var hasDamage = !string.IsNullOrWhiteSpace(spell.DamageExpression);
        var resultText = !hasDamage
            ? save.Success
                ? $"{target.Name} succeeded on the {ability} save{dodgeSaveText}{restraintSaveText}{saveCoverText}."
                : $"{target.Name} failed the {ability} save{dodgeSaveText}{restraintSaveText}{saveCoverText}."
            : save.Success
                ? spell.HalfDamageOnSuccessfulSave
                    ? $"{target.Name} succeeded on the {ability} save{dodgeSaveText}{restraintSaveText}{saveCoverText} and took {appliedDamage} {spell.DamageType} damage."
                    : $"{target.Name} succeeded on the {ability} save{dodgeSaveText}{restraintSaveText}{saveCoverText} and took no damage."
                : $"{target.Name} failed the {ability} save{dodgeSaveText}{restraintSaveText}{saveCoverText} and took {appliedDamage} {spell.DamageType} damage.";
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave))
            resultText += $" {target.Name} gained the {spell.ConditionOnFailedSave.Trim()} condition.";
        if (damage?.Concentration is not null) resultText += $" {damage.Concentration.Summary}";
        return (save, damage, resultText);
    }

    private int ResolveHealingSpell(CampaignState campaign, CharacterSheet caster, CharacterSheet target, SpellDefinition spell, int upcastLevels, DiceService dice)
    {
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is dead and cannot be healed by this configured spell effect.");
        if (string.IsNullOrWhiteSpace(spell.HealingExpression))
            throw new InvalidOperationException($"{spell.Name} is configured as a healing spell but has no healing expression.");

        var amount = dice.RollDamage(spell.HealingExpression);
        for (var i = 0; i < upcastLevels; i++)
            amount += dice.RollDamage(spell.ExtraHealingPerSlotExpression);
        if (spell.AddSpellcastingAbilityModifierToHealing)
            amount += CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(caster, caster.SpellcastingAbility));
        amount = Math.Max(0, amount);
        if (amount <= 0) return 0;
        var before = target.CurrentHp;
        Heal(campaign, target.Id, amount);
        return target.CurrentHp - before;
    }

    private string ResolveStabilizingSpell(CampaignState campaign, CharacterSheet target, SpellDefinition spell)
    {
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is dead and cannot be stabilized by {spell.Name}.");
        if (target.CurrentHp != 0)
            throw new InvalidOperationException($"{spell.Name} can target only a creature at 0 Hit Points.");
        target.Stable = true;
        target.DeathSaveSuccesses = 0;
        target.DeathSaveFailures = 0;
        Touch(campaign);
        return $"{target.Name} became Stable at 0 Hit Points.";
    }

    private static int CantripUpgradeMultiplier(int characterLevel) => characterLevel switch
    {
        >= 17 => 4,
        >= 11 => 3,
        >= 5 => 2,
        _ => 1
    };

    private static void ValidateSpellConfiguration(SpellDefinition spell, string resolution)
    {
        if (resolution == "unsupported")
            throw new InvalidOperationException($"{spell.Name} is in the rules catalog, but its deterministic effect is not implemented yet. The app will not guess the spell's mechanics.");
        if (resolution is not ("attack" or "save" or "healing" or "stabilize" or "utility" or "projectile_auto" or "projectile_attack" or "area_save" or "multi_buff" or "persistent_area"))
            throw new InvalidOperationException($"Spell resolution mode '{spell.Resolution}' is not supported.");
        if ((resolution is "attack" or "projectile_auto" or "projectile_attack") && string.IsNullOrWhiteSpace(spell.DamageExpression))
            throw new InvalidOperationException($"{spell.Name} is configured to deal deterministic damage but has no damage expression.");
        if (resolution == "save" && string.IsNullOrWhiteSpace(spell.SaveAbility))
            throw new InvalidOperationException($"{spell.Name} is configured as a saving-throw spell but has no save ability.");
        if (resolution == "healing" && string.IsNullOrWhiteSpace(spell.HealingExpression))
            throw new InvalidOperationException($"{spell.Name} is configured as a healing spell but has no deterministic healing expression.");
        if (resolution == "area_save" && (string.IsNullOrWhiteSpace(spell.SaveAbility) || string.IsNullOrWhiteSpace(spell.AreaShape) || spell.AreaSizeFeet <= 0))
            throw new InvalidOperationException($"{spell.Name} is configured as an area spell but its save or area geometry metadata is incomplete.");
        if (resolution == "multi_buff" && spell.BaseTargets < 1)
            throw new InvalidOperationException($"{spell.Name} is configured as a multi-target spell but has no target-count metadata.");
        if (resolution == "persistent_area" && (string.IsNullOrWhiteSpace(spell.AreaShape) || spell.AreaSizeFeet <= 0 || string.IsNullOrWhiteSpace(spell.AreaOrigin)))
            throw new InvalidOperationException($"{spell.Name} is configured as a persistent-area spell but its area geometry metadata is incomplete.");
    }

    private static void ValidateSpellTargetType(CharacterSheet? target, SpellDefinition spell)
    {
        if (target is null || string.IsNullOrWhiteSpace(spell.RequiredTargetCreatureType)) return;
        if (string.IsNullOrWhiteSpace(target.CreatureType))
            throw new InvalidOperationException($"{spell.Name} requires a {spell.RequiredTargetCreatureType} target, but {target.Name}'s creature type is unknown. The deterministic engine will not guess it.");
        if (!target.CreatureType.Equals(spell.RequiredTargetCreatureType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{spell.Name} can target only a {spell.RequiredTargetCreatureType}; {target.Name} is {target.CreatureType}.");
    }

    private static void ValidateSpellRange(CampaignState campaign, EncounterState? encounter, CharacterSheet caster, CharacterSheet? target, SpellDefinition spell)
    {
        if (target is null) return;
        var kind = (spell.RangeKind ?? "distance").Trim().ToLowerInvariant();
        if (kind == "self" && !target.Id.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{spell.Name} has a range of Self.");
        if (encounter is null || target.Id.Equals(caster.Id, StringComparison.OrdinalIgnoreCase)) return;

        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        var targetCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        if (casterCombatant is null || targetCombatant is null) return;
        if (spell.RequiresVisibleTarget && !CanSeeCombatant(campaign, encounter, casterCombatant, targetCombatant))
            throw new InvalidOperationException($"{caster.Name} cannot see {target.Name}, which is required to target it with {spell.Name}.");
        if (!casterCombatant.Positioned || !targetCombatant.Positioned) return;
        if (GetCoverBonus(encounter, casterCombatant, targetCombatant) >= 100)
            throw new InvalidOperationException($"{target.Name} has Total Cover and cannot be targeted directly by {spell.Name} from {caster.Name}'s position.");

        var distance = Math.Max(Math.Abs(targetCombatant.GridX - casterCombatant.GridX), Math.Abs(targetCombatant.GridY - casterCombatant.GridY)) * 5;
        var rangeFeet = spell.RangeFeet;
        if (spell.Level == 0 && spell.CantripRangeDoubling && rangeFeet > 0)
            rangeFeet *= CantripUpgradeMultiplier(caster.Level);
        var maximum = kind switch
        {
            "touch" => 5,
            "distance" when rangeFeet > 0 => rangeFeet,
            _ => 0
        };
        if (maximum > 0 && distance > maximum)
            throw new InvalidOperationException($"{target.Name} is {distance} feet away, beyond {spell.Name}'s range of {maximum} feet.");
    }

    private static int GetSpellCoverBonus(EncounterState? encounter, string casterCharacterId, string targetCharacterId)
    {
        if (encounter is null || casterCharacterId.Equals(targetCharacterId, StringComparison.OrdinalIgnoreCase)) return 0;
        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(casterCharacterId, StringComparison.OrdinalIgnoreCase));
        var targetCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(targetCharacterId, StringComparison.OrdinalIgnoreCase));
        if (casterCombatant is null || targetCombatant is null) return 0;
        var bonus = GetCoverBonus(encounter, casterCombatant, targetCombatant);
        return bonus >= 100 ? 0 : bonus;
    }

    private static bool IsPrepared(CharacterSheet caster, SpellDefinition spell) =>
        caster.PreparedSpellIds.Any(x =>
            x.Equals(spell.Id, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(spell.Key) && x.Equals(spell.Key, StringComparison.OrdinalIgnoreCase)));

    private static void ValidateComponents(CharacterSheet caster, SpellDefinition spell)
    {
        if (spell.RequiresVerbal && !caster.CanProvideVerbalComponents)
            throw new InvalidOperationException($"{caster.Name} cannot provide the Verbal component for {spell.Name}.");
        if (spell.RequiresSomatic && !caster.CanProvideSomaticComponents)
            throw new InvalidOperationException($"{caster.Name} cannot provide the Somatic component for {spell.Name}.");
        if (spell.RequiresMaterial && !caster.CanProvideMaterialComponents)
            throw new InvalidOperationException($"{caster.Name} cannot provide the Material component for {spell.Name}.");
    }

    private static int FindLowestAvailableSlot(CharacterSheet caster, int minimumLevel)
    {
        for (var level = Math.Max(1, minimumLevel); level <= 9; level++)
            if (caster.SpellSlots.TryGetValue(level, out var pool) && pool.Remaining > 0)
                return level;
        throw new InvalidOperationException($"{caster.Name} has no level {minimumLevel} or higher spell slot available.");
    }

    private EncounterState? ResolveCastingEncounter(CampaignState campaign, string? encounterId, string casterId)
    {
        EncounterState? encounter = null;
        if (!string.IsNullOrWhiteSpace(encounterId)) encounter = RequireEncounter(campaign, encounterId);
        else encounter = campaign.Encounters.LastOrDefault(e =>
            e.Status.Equals("active", StringComparison.OrdinalIgnoreCase) &&
            e.Combatants.Any(c => c.CharacterId == casterId));
        return encounter;
    }

    private static void ValidateCastingActionEconomy(EncounterState? encounter, CharacterSheet caster, SpellDefinition spell)
    {
        if (encounter is null) return;
        var castingTime = (spell.CastingTime ?? "Action").Trim();
        if (castingTime.Equals("Reaction", StringComparison.OrdinalIgnoreCase)) return;
        if (ParseLongCastingTimeMinutes(castingTime) > 0) return;

        var combatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{caster.Name} is not a combatant in the active encounter.");
        if (castingTime.Equals("Bonus Action", StringComparison.OrdinalIgnoreCase))
        {
            if (!combatant.BonusActionAvailable)
                throw new InvalidOperationException($"{caster.Name} has already used their Bonus Action this turn.");
            return;
        }

        if (castingTime.Equals("Action", StringComparison.OrdinalIgnoreCase))
        {
            if (!combatant.ActionAvailable)
                throw new InvalidOperationException($"{caster.Name} has already used their action this turn and cannot take the Magic action.");
            return;
        }

        throw new InvalidOperationException($"Spell casting time '{spell.CastingTime}' is not supported by the combat action economy yet.");
    }

    private static void ConsumeCastingActionEconomy(EncounterState? encounter, CharacterSheet caster, SpellDefinition spell)
    {
        if (encounter is null) return;
        var castingTime = (spell.CastingTime ?? "Action").Trim();
        if (castingTime.Equals("Reaction", StringComparison.OrdinalIgnoreCase) || ParseLongCastingTimeMinutes(castingTime) > 0) return;
        var combatant = encounter.Combatants.First(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        if (castingTime.Equals("Bonus Action", StringComparison.OrdinalIgnoreCase))
        {
            combatant.BonusActionAvailable = false;
            return;
        }
        if (castingTime.Equals("Action", StringComparison.OrdinalIgnoreCase))
            ConsumeAction(combatant, caster, "Magic");
    }

    private static void ValidateReactionSpellAvailability(EncounterState? encounter, CharacterSheet caster, SpellDefinition spell)
    {
        if (encounter is null || !(spell.CastingTime ?? "Action").Trim().Equals("Reaction", StringComparison.OrdinalIgnoreCase)) return;
        var combatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{caster.Name} is not a combatant in the active encounter.");
        if (!combatant.ReactionAvailable)
            throw new InvalidOperationException($"{caster.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(caster))
            throw new InvalidOperationException($"{caster.Name} cannot take a Reaction right now.");
    }

    private static void ConsumeReactionForSpell(EncounterState? encounter, CharacterSheet caster, SpellDefinition spell)
    {
        if (encounter is null || !(spell.CastingTime ?? "Action").Trim().Equals("Reaction", StringComparison.OrdinalIgnoreCase)) return;
        var combatant = encounter.Combatants.First(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));
        combatant.ReactionAvailable = false;
    }

    private static void ValidateCastingTurn(EncounterState? encounter, CharacterSheet caster, SpellDefinition spell)
    {
        if (encounter is null) return;
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected encounter is not active.");
        if (encounter.Combatants.Count == 0 || encounter.Combatants.Any(c => !c.Initiative.HasValue))
            throw new InvalidOperationException("Initiative must be established before casting spells in combat.");

        var castingTime = (spell.CastingTime ?? "Action").Trim();
        if (castingTime.Equals("Reaction", StringComparison.OrdinalIgnoreCase)) return;
        if (ParseLongCastingTimeMinutes(castingTime) > 0)
            throw new InvalidOperationException("Spells with a casting time of 1 minute or longer are not auto-resolved during an active encounter in this alpha.");

        var index = Math.Clamp(encounter.TurnIndex, 0, encounter.Combatants.Count - 1);
        var current = encounter.Combatants[index];
        if (current.CharacterId != caster.Id)
            throw new InvalidOperationException($"It is not {caster.Name}'s turn. Only Reaction spells can be cast outside the caster's turn in the current combat layer.");
    }

    private static int ParseLongCastingTimeMinutes(string? castingTime)
    {
        if (string.IsNullOrWhiteSpace(castingTime)) return 0;
        var text = castingTime.Trim().ToLowerInvariant();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var amount) || amount < 1) return 0;
        if (parts[1].StartsWith("minute", StringComparison.Ordinal)) return amount;
        if (parts[1].StartsWith("hour", StringComparison.Ordinal)) return checked(amount * 60);
        return 0;
    }
}
