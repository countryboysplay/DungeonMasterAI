using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public CampaignState CreateCampaign(string name)
    {
        var campaign = new CampaignState { Name = string.IsNullOrWhiteSpace(name) ? "New Campaign" : name.Trim() };
        var start = new WorldLocation
        {
            Key = "starting-area",
            Name = "Starting Area",
            Type = "area",
            Description = "The campaign begins here.",
            X = 0.5,
            Y = 0.5,
            Discovered = true,
            SourceKind = "inferred"
        };
        campaign.Locations.Add(start);
        campaign.PartyLocationId = start.Id;
        Log(campaign, "campaign_created", $"Campaign '{campaign.Name}' created.");
        return campaign;
    }

    public CharacterSheet AddCharacter(CampaignState campaign, CharacterSheet character)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(character);
        character.Level = Math.Max(1, character.Level);
        character.ProficiencyBonus = character.ProficiencyBonus > 0
            ? character.ProficiencyBonus
            : CharacterMechanics.ProficiencyBonusForLevel(character.Level);
        character.MaxHp = Math.Max(1, character.MaxHp);
        if (character.CurrentHp <= 0) character.CurrentHp = character.MaxHp;
        character.CurrentHp = Math.Clamp(character.CurrentHp, 0, character.MaxHp);
        character.Speed = Math.Max(0, character.Speed);
        character.Size = NormalizeSize(character.Size);
        character.FreeHands = Math.Clamp(character.FreeHands, 0, 4);
        character.HitDiceMaximum = Math.Max(1, character.HitDiceMaximum <= 0 ? character.Level : character.HitDiceMaximum);
        character.HitDiceRemaining = Math.Clamp(character.HitDiceRemaining, 0, character.HitDiceMaximum);
        character.LocationId ??= campaign.PartyLocationId;
        campaign.Characters.Add(character);
        Touch(campaign);
        Log(campaign, "character_added", $"{character.Name} joined the campaign.");
        return character;
    }

    public bool RevealLocation(CampaignState campaign, string locationId)
    {
        var location = RequireLocation(campaign, locationId);
        if (location.Discovered) return false;
        location.Discovered = true;
        Touch(campaign);
        Log(campaign, "location_discovered", $"Discovered {location.Name}.");
        return true;
    }

    public void MoveParty(CampaignState campaign, string locationId)
    {
        var location = RequireLocation(campaign, locationId);
        if (location.DmOnly && !location.Discovered)
            throw new InvalidOperationException("The party cannot move to an undiscovered DM-only location.");
        if (!location.Discovered)
            throw new InvalidOperationException("The destination has not been discovered.");

        campaign.PartyLocationId = location.Id;
        foreach (var pc in campaign.Characters.Where(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)))
            pc.LocationId = location.Id;
        Touch(campaign);
        Log(campaign, "party_moved", $"Party moved to {location.Name}.");
    }

    public int ApplyDamage(CampaignState campaign, string characterId, int amount) =>
        ApplyDamageDetailed(campaign, characterId, amount).CurrentHp;

    public DamageResult ApplyDamageDetailed(
        CampaignState campaign,
        string characterId,
        int amount,
        string? damageType = null,
        bool criticalHit = false)
    {
        var character = RequireCharacter(campaign, characterId);
        if (!string.IsNullOrWhiteSpace(character.ConcentrationEffect))
            throw new InvalidOperationException("Damage to a concentrating creature must use the concentration-aware damage resolver.");
        return ApplyDamageCore(campaign, characterId, amount, damageType, criticalHit);
    }

    private DamageResult ApplyDamageCore(
        CampaignState campaign,
        string characterId,
        int amount,
        string? damageType = null,
        bool criticalHit = false)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var character = RequireCharacter(campaign, characterId);
        if (character.Dead) throw new InvalidOperationException($"{character.Name} is dead and cannot take further damage.");

        var normalizedType = NormalizeDamageType(damageType);
        var effective = amount;
        if (normalizedType is not null && ContainsIgnoreCase(character.DamageImmunities, normalizedType)) effective = 0;
        else if (effective > 0)
        {
            if (normalizedType is not null && ContainsIgnoreCase(character.DamageResistances, normalizedType)) effective /= 2;
            if (normalizedType is not null && ContainsIgnoreCase(character.DamageVulnerabilities, normalizedType)) effective *= 2;
        }

        if (effective == 0)
        {
            var zeroSummary = normalizedType is null
                ? $"{character.Name} took no damage."
                : $"{character.Name} took no {normalizedType} damage.";
            Log(campaign, "damage", zeroSummary);
            return new DamageResult(amount, 0, normalizedType, 0, 0, character.CurrentHp, false, character.Dead, character.DeathSaveFailures, zeroSummary);
        }

        if (character.CurrentHp == 0)
        {
            character.Stable = false;
            var failures = criticalHit ? 2 : 1;
            character.DeathSaveFailures = Math.Min(3, character.DeathSaveFailures + failures);
            if (effective >= character.MaxHp || character.DeathSaveFailures >= 3)
                MarkDead(character);
            if (character.Dead) EndGrapplesForCharacter(campaign, character.Id, includeTarget: true);
            Touch(campaign);
            var atZeroSummary = character.Dead
                ? $"{character.Name} took {effective} damage at 0 HP and died."
                : $"{character.Name} took {effective} damage at 0 HP and suffered {failures} Death Saving Throw failure{(failures == 1 ? "" : "s")}.";
            Log(campaign, "damage_at_zero", atZeroSummary);
            return new DamageResult(amount, effective, normalizedType, 0, 0, 0, false, character.Dead, character.DeathSaveFailures, atZeroSummary);
        }

        var tempBefore = character.TempHp;
        var tempLost = Math.Min(tempBefore, effective);
        character.TempHp -= tempLost;
        var remaining = effective - tempLost;
        var hpBefore = character.CurrentHp;
        var hpLost = Math.Min(hpBefore, remaining);
        character.CurrentHp = Math.Max(0, hpBefore - remaining);
        var droppedToZero = hpBefore > 0 && character.CurrentHp == 0;

        if (droppedToZero)
        {
            if (!string.IsNullOrWhiteSpace(character.ConcentrationEffect))
                EndConcentrationInternal(campaign, character, "being incapacitated or killed by damage");
            var overkill = Math.Max(0, remaining - hpBefore);
            var monster = character.CharacterType.Equals("monster", StringComparison.OrdinalIgnoreCase);
            if (monster || overkill >= character.MaxHp)
            {
                MarkDead(character);
            }
            else
            {
                character.Stable = false;
                AddConditionInternal(character, "Unconscious");
                AddConditionInternal(character, "Prone");
            }
        }

        if (droppedToZero) EndGrapplesForCharacter(campaign, character.Id, includeTarget: character.Dead);
        if (character.Dead) RemoveAllEffectsOnTarget(campaign, character.Id, "the target died");
        Touch(campaign);
        var summary = $"{character.Name} took {effective}{(normalizedType is null ? "" : " " + normalizedType)} damage and is at {character.CurrentHp}/{character.MaxHp} HP.";
        if (character.Dead) summary += " The character is dead.";
        else if (droppedToZero) summary += " The character is Unconscious and must make Death Saving Throws.";
        Log(campaign, "damage", summary);
        return new DamageResult(amount, effective, normalizedType, tempLost, hpLost, character.CurrentHp, droppedToZero, character.Dead, character.DeathSaveFailures, summary);
    }

    public int Heal(CampaignState campaign, string characterId, int amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var character = RequireCharacter(campaign, characterId);
        if (character.Dead) throw new InvalidOperationException($"{character.Name} is dead and cannot be healed by ordinary healing.");
        if (amount == 0) return character.CurrentHp;
        character.CurrentHp = Math.Min(character.MaxHp, character.CurrentHp + amount);
        if (character.CurrentHp > 0)
        {
            character.Stable = false;
            character.DeathSaveSuccesses = 0;
            character.DeathSaveFailures = 0;
            RemoveConditionInternal(character, "Unconscious");
        }
        Touch(campaign);
        Log(campaign, "healing", $"{character.Name} healed {amount} HP and is at {character.CurrentHp}/{character.MaxHp} HP.");
        return character.CurrentHp;
    }

    public int GrantTemporaryHitPoints(CampaignState campaign, string characterId, int amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var character = RequireCharacter(campaign, characterId);
        if (amount > character.TempHp) character.TempHp = amount;
        Touch(campaign);
        Log(campaign, "temporary_hp", $"{character.Name} now has {character.TempHp} Temporary Hit Points.");
        return character.TempHp;
    }

    public D20TestResult ResolveAbilityCheck(
        CampaignState campaign,
        string characterId,
        string ability,
        int difficultyClass,
        int rollOne,
        int? rollTwo = null,
        D20RollMode mode = D20RollMode.Normal,
        string? skill = null,
        int circumstanceModifier = 0)
    {
        var character = RequireCharacter(campaign, characterId);
        var proficient = !string.IsNullOrWhiteSpace(skill) && ContainsIgnoreCase(character.SkillProficiencies, skill.Trim());
        var effectiveMode = CombineAdvantage(mode, AbilityCheckModeFromConditions(character));
        var result = CharacterMechanics.ResolveD20Test(character, ability, difficultyClass, rollOne, rollTwo, effectiveMode, proficient, circumstanceModifier);
        Log(campaign, "ability_check", $"{character.Name}: {result.Summary}");
        return result;
    }

    public D20TestResult ResolveAbilityCheckWithDice(
        CampaignState campaign,
        string characterId,
        string ability,
        int difficultyClass,
        DiceService dice,
        D20RollMode mode = D20RollMode.Normal,
        string? skill = null,
        int circumstanceModifier = 0)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var character = RequireCharacter(campaign, characterId);
        var helper = FindHelpAbilityCheckHelper(campaign, characterId, skill);
        var requestedMode = helper is null ? mode : CombineAdvantage(mode, D20RollMode.Advantage);
        var effectiveMode = CombineAdvantage(requestedMode, AbilityCheckModeFromConditions(character));
        var rolls = dice.RollD20(effectiveMode);
        var proficient = !string.IsNullOrWhiteSpace(skill) && ContainsIgnoreCase(character.SkillProficiencies, skill.Trim());
        var result = CharacterMechanics.ResolveD20Test(character, ability, difficultyClass, rolls.RollOne, rolls.RollTwo, effectiveMode, proficient, circumstanceModifier);
        Log(campaign, "ability_check", $"{character.Name}: {result.Summary}");
        if (helper is not null)
        {
            var helperCharacter = RequireCharacter(campaign, helper.CharacterId);
            helper.HelpAbilityTargetCharacterId = null;
            helper.HelpAbilityProficiency = null;
            Log(campaign, "help_ability_consumed", $"{helperCharacter.Name}'s Help supplied Advantage to the ability check.");
        }
        return result;
    }

    public D20TestResult ResolveSavingThrow(
        CampaignState campaign,
        string characterId,
        string ability,
        int difficultyClass,
        int rollOne,
        int? rollTwo = null,
        D20RollMode mode = D20RollMode.Normal,
        int circumstanceModifier = 0)
    {
        var character = RequireCharacter(campaign, characterId);
        var normalized = CharacterMechanics.NormalizeAbility(ability);
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(character, normalized);
        var effectiveMode = CombineAdvantage(mode, conditionMode);
        var proficient = ContainsIgnoreCase(character.SavingThrowProficiencies, normalized) || ContainsIgnoreCase(character.SavingThrowProficiencies, normalized[..3]);

        if (CharacterMechanics.AutomaticallyFailsSavingThrow(character, normalized))
        {
            var chosen = rollOne;
            if (rollTwo.HasValue)
            {
                chosen = effectiveMode switch
                {
                    D20RollMode.Advantage => Math.Max(rollOne, rollTwo.Value),
                    D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo.Value),
                    _ => rollOne
                };
            }
            var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, normalized));
            var proficiencyModifier = proficient ? Math.Max(0, character.ProficiencyBonus) : 0;
            var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
            var total = chosen + abilityModifier + proficiencyModifier + circumstanceModifier - exhaustionPenalty;
            var result = new D20TestResult(rollOne, rollTwo, chosen, abilityModifier, proficiencyModifier, exhaustionPenalty, total, difficultyClass, false, $"{normalized} saving throw automatically failed because {character.Name}'s condition causes automatic failure on Strength and Dexterity saving throws.");
            Log(campaign, "saving_throw", $"{character.Name}: {result.Summary}");
            return result;
        }

        var resolved = CharacterMechanics.ResolveD20Test(character, normalized, difficultyClass, rollOne, rollTwo, effectiveMode, proficient, circumstanceModifier);
        Log(campaign, "saving_throw", $"{character.Name}: {resolved.Summary}");
        return resolved;
    }

    public D20TestResult ResolveSavingThrowWithDice(
        CampaignState campaign,
        string characterId,
        string ability,
        int difficultyClass,
        DiceService dice,
        D20RollMode mode = D20RollMode.Normal,
        int circumstanceModifier = 0)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var character = RequireCharacter(campaign, characterId);
        var normalized = CharacterMechanics.NormalizeAbility(ability);
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(character, normalized);
        var effectiveMode = CombineAdvantage(mode, conditionMode);
        var rolls = dice.RollD20(effectiveMode);
        var effectBonus = RollActiveSavingThrowBonus(campaign, characterId, dice);
        return ResolveSavingThrow(campaign, characterId, normalized, difficultyClass, rolls.RollOne, rolls.RollTwo, mode, circumstanceModifier + effectBonus);
    }

    public int SetExhaustion(CampaignState campaign, string characterId, int level)
    {
        var character = RequireCharacter(campaign, characterId);
        character.ExhaustionLevel = Math.Clamp(level, 0, 6);
        if (character.ExhaustionLevel >= 6)
        {
            if (!string.IsNullOrWhiteSpace(character.ConcentrationEffect))
                EndConcentrationInternal(campaign, character, "dying from Exhaustion");
            MarkDead(character);
            EndGrapplesForCharacter(campaign, character.Id, includeTarget: true);
        }
        Touch(campaign);
        Log(campaign, "exhaustion", character.Dead
            ? $"{character.Name} reached Exhaustion level 6 and died."
            : $"{character.Name}'s Exhaustion level is now {character.ExhaustionLevel}.");
        return character.ExhaustionLevel;
    }

    public bool AddCondition(CampaignState campaign, string characterId, string condition)
    {
        var character = RequireCharacter(campaign, characterId);
        if (string.IsNullOrWhiteSpace(condition)) throw new ArgumentException("Condition is required.", nameof(condition));
        var normalized = condition.Trim();
        var changed = AddConditionInternal(character, normalized);
        if (changed)
        {
            if (normalized.Equals("Unconscious", StringComparison.OrdinalIgnoreCase))
                AddConditionInternal(character, "Prone");
            if (BreaksConcentration(normalized) && !string.IsNullOrWhiteSpace(character.ConcentrationEffect))
                EndConcentrationInternal(campaign, character, $"the {normalized} condition");
            if (normalized.Equals("Incapacitated", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Paralyzed", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Stunned", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Dead", StringComparison.OrdinalIgnoreCase))
                EndGrapplesForCharacter(campaign, character.Id, includeTarget: normalized.Equals("Dead", StringComparison.OrdinalIgnoreCase));
            Touch(campaign);
            Log(campaign, "condition_added", $"{character.Name} gained the {normalized} condition.");
        }
        return changed;
    }

    public bool RemoveCondition(CampaignState campaign, string characterId, string condition)
    {
        var character = RequireCharacter(campaign, characterId);
        var changed = RemoveConditionInternal(character, condition.Trim());
        if (changed)
        {
            Touch(campaign);
            Log(campaign, "condition_removed", $"{character.Name} no longer has the {condition.Trim()} condition.");
        }
        return changed;
    }

    public string BeginConcentration(CampaignState campaign, string characterId, string effect)
    {
        var character = RequireCharacter(campaign, characterId);
        var normalized = (effect ?? "").Trim();
        if (normalized.Length == 0) throw new ArgumentException("A concentration effect name is required.", nameof(effect));
        if (character.Dead || character.CurrentHp <= 0 || character.Conditions.Any(BreaksConcentration))
            throw new InvalidOperationException($"{character.Name} cannot begin Concentration while incapacitated or dead.");

        var previous = character.ConcentrationEffect;
        if (!string.IsNullOrWhiteSpace(previous))
            RemoveConcentrationBoundEffects(campaign, character.Id, previous, $"{character.Name} began Concentrating on {normalized}");
        if (!string.IsNullOrWhiteSpace(previous)
            && !previous.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            && previous.StartsWith("Readied spell:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var encounter in campaign.Encounters)
            {
                var readyCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase));
                if (readyCombatant?.ReadiedAction?.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase) == true)
                    readyCombatant.ReadiedAction = null;
            }
            Log(campaign, "ready_spell_dissipated", $"{character.Name}'s held readied spell dissipated when a different Concentration effect began.", dmOnly: true);
        }
        character.ConcentrationEffect = normalized;
        Touch(campaign);
        if (!string.IsNullOrWhiteSpace(previous) && !previous.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            Log(campaign, "concentration_ended", $"{character.Name}'s Concentration on {previous} ended when {normalized} began.");
        Log(campaign, "concentration_started", $"{character.Name} began Concentrating on {normalized}.");
        return normalized;
    }

    public bool EndConcentration(CampaignState campaign, string characterId, string reason = "ended voluntarily")
    {
        var character = RequireCharacter(campaign, characterId);
        return EndConcentrationInternal(campaign, character, string.IsNullOrWhiteSpace(reason) ? "ended" : reason.Trim());
    }

    public ConcentrationCheckResult? ResolveConcentrationAfterDamage(
        CampaignState campaign,
        string characterId,
        int effectiveDamage,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        if (effectiveDamage <= 0) return null;
        var character = RequireCharacter(campaign, characterId);
        if (string.IsNullOrWhiteSpace(character.ConcentrationEffect)) return null;

        var effect = character.ConcentrationEffect!;
        if (character.Dead || character.CurrentHp <= 0 || character.Conditions.Any(BreaksConcentration))
        {
            EndConcentrationInternal(campaign, character, "being incapacitated");
            return null;
        }

        var dc = Math.Min(30, Math.Max(10, effectiveDamage / 2));
        var mode = D20RollMode.Normal;
        var rolls = dice.RollD20(mode);
        var proficient = character.SavingThrowProficiencies.Any(x => CharacterMechanics.NormalizeAbility(x) == "constitution");
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, character.Id, dice);
        var savingThrow = CharacterMechanics.ResolveD20Test(
            character,
            "constitution",
            dc,
            rolls.RollOne,
            rolls.RollTwo,
            mode,
            proficient,
            effectSaveBonus);
        var maintained = savingThrow.Success;
        if (!maintained)
            EndConcentrationInternal(campaign, character, $"failing a DC {dc} Constitution saving throw after taking damage");
        else
        {
            Touch(campaign);
            Log(campaign, "concentration_check", $"{character.Name} maintained Concentration on {effect} ({savingThrow.Total} vs DC {dc}).");
        }

        var summary = maintained
            ? $"{character.Name} maintained Concentration on {effect} ({savingThrow.Total} vs DC {dc})."
            : $"{character.Name} lost Concentration on {effect} ({savingThrow.Total} vs DC {dc}).";
        return new ConcentrationCheckResult(effect, effectiveDamage, dc, savingThrow, maintained, summary);
    }

    public DamageResolutionResult ApplyDamageWithConcentration(
        CampaignState campaign,
        string characterId,
        int amount,
        DiceService dice,
        string? damageType = null,
        bool criticalHit = false)
    {
        var damage = ApplyDamageCore(campaign, characterId, amount, damageType, criticalHit);
        var concentration = ResolveConcentrationAfterDamage(campaign, characterId, damage.EffectiveDamage, dice);
        return new DamageResolutionResult(damage, concentration);
    }

    public int SpendSpellSlot(CampaignState campaign, string characterId, int level)
    {
        if (level is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(level));
        var character = RequireCharacter(campaign, characterId);
        if (!character.SpellSlots.TryGetValue(level, out var pool) || pool.Remaining <= 0)
            throw new InvalidOperationException($"{character.Name} has no level {level} spell slot available.");
        pool.Remaining--;
        Touch(campaign);
        Log(campaign, "spell_slot_spent", $"{character.Name} spent a level {level} spell slot ({pool.Remaining}/{pool.Maximum} remaining)." );
        return pool.Remaining;
    }

    public int SpendResource(CampaignState campaign, string characterId, string resourceName, int amount = 1)
    {
        if (amount < 1) throw new ArgumentOutOfRangeException(nameof(amount));
        var character = RequireCharacter(campaign, characterId);
        var resource = character.Resources.FirstOrDefault(r => r.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Resource '{resourceName}' was not found.");
        if (resource.Remaining < amount) throw new InvalidOperationException($"Not enough {resource.Name} remaining.");
        resource.Remaining -= amount;
        Touch(campaign);
        Log(campaign, "resource_spent", $"{character.Name} spent {amount} {resource.Name} ({resource.Remaining}/{resource.Maximum} remaining)." );
        return resource.Remaining;
    }

    public RestResult ShortRest(CampaignState campaign, string characterId)
    {
        var character = RequireCharacter(campaign, characterId);
        if (character.CurrentHp < 1) throw new InvalidOperationException("A creature must have at least 1 Hit Point to start a Short Rest.");
        var effects = new List<string>();
        foreach (var resource in character.Resources.Where(r => r.RechargeOnShortRest))
        {
            if (resource.Remaining == resource.Maximum) continue;
            resource.Remaining = resource.Maximum;
            effects.Add($"Restored {resource.Name}.");
        }
        AdvanceTime(campaign, 60);
        var summary = effects.Count == 0
            ? $"{character.Name} finished a Short Rest. Hit Point Dice may now be spent."
            : $"{character.Name} finished a Short Rest. {string.Join(" ", effects)}";
        Log(campaign, "short_rest", summary);
        return new RestResult("Short Rest", 60, effects, summary);
    }

    public int SpendHitDie(CampaignState campaign, string characterId, int dieRoll)
    {
        var character = RequireCharacter(campaign, characterId);
        if (character.CurrentHp < 1) throw new InvalidOperationException("Hit Point Dice can be spent only by a creature with at least 1 Hit Point during a Short Rest.");
        if (character.HitDiceRemaining <= 0) throw new InvalidOperationException("No Hit Point Dice remain.");
        if (dieRoll < 1 || dieRoll > Math.Max(2, character.HitDieSides)) throw new ArgumentOutOfRangeException(nameof(dieRoll));
        var conModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, "constitution"));
        var regained = Math.Max(1, dieRoll + conModifier);
        character.HitDiceRemaining--;
        character.CurrentHp = Math.Min(character.MaxHp, character.CurrentHp + regained);
        Touch(campaign);
        Log(campaign, "hit_die_spent", $"{character.Name} spent a d{character.HitDieSides} Hit Point Die and regained {regained} HP ({character.HitDiceRemaining}/{character.HitDiceMaximum} dice remaining)." );
        return regained;
    }

    public RestResult LongRest(CampaignState campaign, string characterId)
    {
        var character = RequireCharacter(campaign, characterId);
        if (character.CurrentHp < 1) throw new InvalidOperationException("A creature must have at least 1 Hit Point to start a Long Rest.");
        var effects = new List<string>();
        if (character.CurrentHp != character.MaxHp) effects.Add("Restored all Hit Points.");
        character.CurrentHp = character.MaxHp;
        if (character.TempHp > 0) effects.Add("Temporary Hit Points expired.");
        character.TempHp = 0;
        character.HitDiceRemaining = character.HitDiceMaximum;
        effects.Add("Restored spent Hit Point Dice.");
        if (character.ExhaustionLevel > 0)
        {
            character.ExhaustionLevel--;
            effects.Add("Reduced Exhaustion by 1 level.");
        }
        foreach (var pool in character.SpellSlots.Values) pool.Remaining = pool.Maximum;
        if (character.SpellSlots.Count > 0) effects.Add("Restored spell slots.");
        foreach (var resource in character.Resources.Where(r => r.RechargeOnLongRest)) resource.Remaining = resource.Maximum;
        if (character.Resources.Any(r => r.RechargeOnLongRest)) effects.Add("Restored Long Rest resources.");
        character.Stable = false;
        character.DeathSaveSuccesses = 0;
        character.DeathSaveFailures = 0;
        RemoveConditionInternal(character, "Unconscious");
        AdvanceTime(campaign, 480);
        var summary = $"{character.Name} finished a Long Rest. {string.Join(" ", effects)}";
        Log(campaign, "long_rest", summary);
        return new RestResult("Long Rest", 480, effects, summary);
    }

    public EncounterState StartEncounter(CampaignState campaign, string name, string? locationId = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var encounterLocation = locationId ?? campaign.PartyLocationId;
        if (encounterLocation is not null) RequireLocation(campaign, encounterLocation);
        if (campaign.Encounters.Any(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another encounter is already active.");

        var encounter = new EncounterState
        {
            Key = $"encounter.{campaign.Encounters.Count + 1}",
            Name = string.IsNullOrWhiteSpace(name) ? "Encounter" : name.Trim(),
            Status = "active",
            LocationId = encounterLocation,
            Round = 1,
            TurnIndex = 0
        };
        campaign.Encounters.Add(encounter);
        Touch(campaign);
        Log(campaign, "encounter_started", $"Encounter '{encounter.Name}' started.");
        return encounter;
    }

    public EncounterState ActivateEncounter(CampaignState campaign, string encounterId, bool includeParty = true)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (campaign.Encounters.Any(e => e.Id != encounter.Id && e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another encounter is already active.");
        if (encounter.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A completed encounter cannot be reactivated without resetting it.");

        encounter.Status = "active";
        encounter.Round = 1;
        encounter.TurnIndex = 0;
        encounter.SpellSlotCasterIdsThisTurn.Clear();
        encounter.PendingMove = null;
        if (campaign.PendingPlayerRoll?.EncounterId == encounter.Id) campaign.PendingPlayerRoll = null;
        foreach (var combatant in encounter.Combatants)
        {
            combatant.Initiative = null;
            combatant.ActionAvailable = false;
            combatant.BonusActionAvailable = false;
            combatant.AttackActionInProgress = false;
            combatant.AttacksRemainingInAction = 0;
            combatant.ReactionAvailable = true;
            combatant.Disengaging = false;
            combatant.Dodging = false;
            combatant.IsHidden = false;
            combatant.HideCheckTotal = 0;
            if (combatant.ReadiedAction?.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase) == true)
            {
                var character = RequireCharacter(campaign, combatant.CharacterId);
                if (character.ConcentrationEffect?.StartsWith("Readied spell:", StringComparison.OrdinalIgnoreCase) == true)
                    EndConcentrationInternal(campaign, character, "the encounter ending before the trigger occurred");
            }
            combatant.ReadiedAction = null;
            combatant.MovementRemainingFeet = 0;
            if (string.IsNullOrWhiteSpace(combatant.Side))
            {
                var existingCharacter = RequireCharacter(campaign, combatant.CharacterId);
                combatant.Side = DefaultCombatSide(existingCharacter);
            }
        }

        if (includeParty)
        {
            foreach (var pc in campaign.Characters.Where(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) && !c.Dead))
            {
                if (encounter.Combatants.Any(c => c.CharacterId == pc.Id)) continue;
                encounter.Combatants.Add(new CombatantState
                {
                    CharacterId = pc.Id,
                    TieBreaker = encounter.Combatants.Count,
                    Side = "party",
                    Positioned = true,
                    GridX = encounter.Combatants.Count * 2,
                    GridY = 0
                });
            }
        }

        Touch(campaign);
        Log(campaign, "encounter_activated", $"Encounter '{encounter.Name}' became active.");
        return encounter;
    }

    public CombatantState AddCombatant(CampaignState campaign, string encounterId, string characterId, bool surprised = false, string? side = null)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Combatants can be added only to an active encounter.");
        var character = RequireCharacter(campaign, characterId);
        if (encounter.Combatants.Any(c => c.CharacterId == characterId))
            throw new InvalidOperationException($"{character.Name} is already in the encounter.");

        var combatant = new CombatantState
        {
            CharacterId = character.Id,
            Surprised = surprised,
            TieBreaker = encounter.Combatants.Count,
            Side = NormalizeCombatSide(side, character),
            Positioned = true,
            GridX = encounter.Combatants.Count * 2,
            GridY = character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? 0 : 6
        };
        encounter.Combatants.Add(combatant);
        Touch(campaign);
        Log(campaign, "combatant_added", $"{character.Name} joined encounter '{encounter.Name}'.");
        return combatant;
    }

    public int SetInitiative(CampaignState campaign, string encounterId, string combatantId, int initiative)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        var combatant = RequireCombatant(encounter, combatantId);
        combatant.Initiative = initiative;
        Touch(campaign);
        return initiative;
    }

    public IReadOnlyList<InitiativeEntry> FinalizeInitiative(CampaignState campaign, string encounterId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (encounter.Combatants.Count == 0) throw new InvalidOperationException("The encounter has no combatants.");
        if (encounter.Combatants.Any(c => c.Initiative is null))
            throw new InvalidOperationException("Every combatant must have an Initiative result before combat begins.");

        encounter.Combatants = encounter.Combatants
            .OrderByDescending(c => c.Initiative!.Value)
            .ThenBy(c => c.TieBreaker)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        encounter.Round = Math.Max(1, encounter.Round);
        encounter.TurnIndex = Math.Clamp(encounter.TurnIndex, 0, encounter.Combatants.Count - 1);
        encounter.SpellSlotCasterIdsThisTurn.Clear();
        encounter.PendingMove = null;
        foreach (var combatant in encounter.Combatants)
        {
            combatant.MovementRemainingFeet = 0;
            combatant.ActionAvailable = false;
            combatant.BonusActionAvailable = false;
            combatant.AttackActionInProgress = false;
            combatant.AttacksRemainingInAction = 0;
            combatant.ReactionAvailable = true;
            combatant.Disengaging = false;
        }
        var startingCombatant = encounter.Combatants[encounter.TurnIndex];
        ResetTurnResources(campaign, startingCombatant);
        ProcessBattlefieldEffectsAtTurnStart(campaign, encounter, startingCombatant, new DiceService());
        SyncPendingPlayerRollForCurrentTurn(campaign, encounter, startingCombatant);
        Touch(campaign);

        var ties = encounter.Combatants
            .GroupBy(c => c.Initiative!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderByDescending(x => x)
            .ToArray();
        if (ties.Length > 0)
            Log(campaign, "initiative_tie", $"Initiative tie detected at {string.Join(", ", ties)}. Stable encounter order was used; the DM may reorder tied combatants before continuing.", dmOnly: true);

        var order = encounter.Combatants.Select(c =>
        {
            var character = RequireCharacter(campaign, c.CharacterId);
            return new InitiativeEntry(c.Id, character.Id, character.Name, c.Initiative!.Value, c.Surprised);
        }).ToArray();
        Log(campaign, "initiative", $"Initiative established for encounter '{encounter.Name}'.");
        return order;
    }

    public IReadOnlyList<InitiativeEntry> GetInitiativeOrder(CampaignState campaign, string encounterId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        return encounter.Combatants
            .Where(c => c.Initiative.HasValue)
            .OrderByDescending(c => c.Initiative!.Value)
            .ThenBy(c => c.TieBreaker)
            .Select(c =>
            {
                var character = RequireCharacter(campaign, c.CharacterId);
                return new InitiativeEntry(c.Id, character.Id, character.Name, c.Initiative!.Value, c.Surprised);
            })
            .ToArray();
    }

    public EncounterAttackResult ResolveEncounterAttack(
        CampaignState campaign,
        string encounterId,
        string attackerCombatantId,
        string targetCombatantId,
        string? attackName,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.Combatants.Any(c => c.Initiative is null))
            throw new InvalidOperationException("Initiative must be finalized before resolving an attack.");
        var attackerCombatant = RequireCombatant(encounter, attackerCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (CharacterMechanics.IsIncapacitated(attacker))
            throw new InvalidOperationException($"{attacker.Name} is Incapacitated and cannot take the Attack action.");
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");

        var profile = string.IsNullOrWhiteSpace(attackName)
            ? attacker.Attacks.FirstOrDefault() ?? CharacterMechanics.UnarmedStrikeProfile(attacker)
            : attacker.Attacks.FirstOrDefault(a => a.Name.Equals(attackName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null && string.Equals(attackName?.Trim(), "Unarmed Strike", StringComparison.OrdinalIgnoreCase))
            profile = CharacterMechanics.UnarmedStrikeProfile(attacker);
        if (profile is null)
            throw new InvalidOperationException($"{attacker.Name} has no configured attack matching '{attackName ?? "default"}'.");

        ValidateAttackRange(attackerCombatant, targetCombatant, attacker, target, profile);
        var coverBonus = GetCoverBonus(encounter, attackerCombatant, targetCombatant);
        if (coverBonus >= 100)
            throw new InvalidOperationException($"{target.Name} has Total Cover from {attacker.Name} and cannot be targeted directly by that attack.");

        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var effectiveArmorClass = EffectiveArmorClass(campaign, target) + coverBonus;
        var attackMode = AttackRollMode(campaign, encounter, attackerCombatant, targetCombatant, attacker, target);
        var helpUsed = ConsumeHelpAttackAdvantage(encounter, attackerCombatant, targetCombatant);
        var automaticCritical = IsAutomaticCriticalHitTarget(attackerCombatant, targetCombatant, target);
        var effectAttackBonus = RollActiveAttackBonus(campaign, attacker.Id, dice);
        var attack = dice.Attack(profile.AttackBonus, effectiveArmorClass, profile.DamageExpression, attackMode, automaticCritical, effectAttackBonus);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        BreakHidden(campaign, encounter, attackerCombatant, "making an attack roll");
        DamageResult? damage = null;
        ConcentrationCheckResult? concentration = null;
        var concentrationBefore = target.ConcentrationEffect;
        if (attack.Hit)
        {
            var resolution = ApplyDamageWithConcentration(campaign, target.Id, attack.Damage, dice, profile.DamageType, attack.Critical);
            damage = resolution.Damage;
            concentration = resolution.Concentration;
        }

        var coverText = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} AC)" : "";
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var summary = attack.Hit
            ? $"{attacker.Name} used {profile.Name} against {target.Name}{coverText}: {attack.Summary}{helpText}"
            : $"{attacker.Name} used {profile.Name} against {target.Name}{coverText}: miss.{helpText}";
        if (concentration is not null) summary += $" {concentration.Summary}";
        else if (!string.IsNullOrWhiteSpace(concentrationBefore) && string.IsNullOrWhiteSpace(target.ConcentrationEffect) && damage?.EffectiveDamage > 0)
            summary += $" {target.Name} lost Concentration on {concentrationBefore}.";
        Touch(campaign);
        Log(campaign, "combat_attack", summary);
        return new EncounterAttackResult(encounter.Id, attacker.Name, target.Name, profile.Name, attack, damage, summary, concentration, false, coverBonus);
    }

    public CombatantState NextTurn(CampaignState campaign, string encounterId, DiceService? dice = null)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (encounter.Combatants.Count == 0) throw new InvalidOperationException("The encounter has no combatants.");
        if (encounter.Combatants.Any(c => c.Initiative is null))
            throw new InvalidOperationException("Initiative must be finalized before advancing turns.");

        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve or decline all pending Opportunity Attacks before advancing the turn.");

        dice ??= new DiceService();
        var endingCombatant = encounter.Combatants[Math.Clamp(encounter.TurnIndex, 0, encounter.Combatants.Count - 1)];
        var endingCharacter = RequireCharacter(campaign, endingCombatant.CharacterId);
        if (campaign.PendingPlayerRoll?.Required == true
            && string.Equals(campaign.PendingPlayerRoll.EncounterId, encounter.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolve the required player roll before ending the turn: {campaign.PendingPlayerRoll.Purpose}");
        if (endingCombatant.DeathSaveRequiredThisTurn && !endingCombatant.DeathSaveResolvedThisTurn)
            throw new InvalidOperationException($"{endingCharacter.Name} must make the required Death Saving Throw before this turn can end.");
        ProcessEndOfTurnEffects(campaign, encounter, endingCombatant, dice);

        encounter.TurnIndex++;
        encounter.SpellSlotCasterIdsThisTurn.Clear();
        if (encounter.TurnIndex >= encounter.Combatants.Count)
        {
            encounter.TurnIndex = 0;
            encounter.Round++;
        }
        foreach (var combatant in encounter.Combatants)
        {
            combatant.MovementRemainingFeet = 0;
            combatant.ActionAvailable = false;
            combatant.BonusActionAvailable = false;
            combatant.AttackActionInProgress = false;
            combatant.AttacksRemainingInAction = 0;
            combatant.Disengaging = false;
        }
        var current = encounter.Combatants[encounter.TurnIndex];
        current.Surprised = false;
        ResetTurnResources(campaign, current);
        ProcessBattlefieldEffectsAtTurnStart(campaign, encounter, current, dice);
        SyncPendingPlayerRollForCurrentTurn(campaign, encounter, current);
        Touch(campaign);
        var character = RequireCharacter(campaign, current.CharacterId);
        Log(campaign, "combat_turn", $"Round {encounter.Round}: {character.Name}'s turn.");
        return current;
    }

    public TerrainFeature AddTerrainFeature(CampaignState campaign, string encounterId, TerrainFeature terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        var encounter = RequireEncounter(campaign, encounterId);
        terrain.WidthSquares = Math.Max(1, terrain.WidthSquares);
        terrain.HeightSquares = Math.Max(1, terrain.HeightSquares);
        terrain.Cover = NormalizeCover(terrain.Cover);
        encounter.Terrain.Add(terrain);
        Touch(campaign);
        Log(campaign, "terrain_added", $"Terrain '{terrain.Name}' added at grid ({terrain.GridX}, {terrain.GridY}).", dmOnly: true);
        return terrain;
    }

    public CombatantState SetCombatantPosition(CampaignState campaign, string encounterId, string combatantId, int gridX, int gridY)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureSquareAvailable(encounter, combatant.Id, gridX, gridY);
        combatant.Positioned = true;
        combatant.GridX = gridX;
        combatant.GridY = gridY;
        EndInvalidGrapples(campaign, encounter);
        Touch(campaign);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        Log(campaign, "combat_position", $"{character.Name} was placed at grid ({gridX}, {gridY}).", dmOnly: true);
        return combatant;
    }

    public CombatMoveResult MoveCombatant(CampaignState campaign, string encounterId, string combatantId, int gridX, int gridY)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.Combatants.Any(c => c.Initiative is null))
            throw new InvalidOperationException("Initiative must be finalized before combat movement.");
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve or decline the pending Opportunity Attacks before starting another move.");

        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        if (!combatant.Positioned)
            throw new InvalidOperationException("The combatant must be placed on the tactical grid before moving.");
        EnsureSquareAvailable(encounter, combatant.Id, gridX, gridY);

        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.Dead || CharacterMechanics.HasCondition(character, "Unconscious"))
            throw new InvalidOperationException($"{character.Name} cannot move while dead or Unconscious.");
        if ((gridX != combatant.GridX || gridY != combatant.GridY) && CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects) <= 0)
            throw new InvalidOperationException($"{character.Name} cannot move because their Speed is 0.");

        var fromX = combatant.GridX;
        var fromY = combatant.GridY;
        var path = TraceGridPath(fromX, fromY, gridX, gridY);
        ValidateMovementPath(encounter, combatant.Id, path);
        var distanceFeet = GridDistanceFeet(fromX, fromY, gridX, gridY);
        var movementCostFeet = MovementCostFeet(encounter, path, character);
        if (movementCostFeet > combatant.MovementRemainingFeet)
            throw new InvalidOperationException($"That move costs {movementCostFeet} feet of movement, but only {combatant.MovementRemainingFeet} feet remain this turn.");

        var opportunityAttacks = combatant.Disengaging
            ? new List<OpportunityAttackWindow>()
            : FindOpportunityAttackWindows(campaign, encounter, combatant, gridX, gridY);

        if (opportunityAttacks.Count > 0)
        {
            encounter.PendingMove = new PendingCombatMove
            {
                CombatantId = combatant.Id,
                FromX = fromX,
                FromY = fromY,
                ToX = gridX,
                ToY = gridY,
                DistanceFeet = distanceFeet,
                MovementCostFeet = movementCostFeet,
                OpportunityAttacks = opportunityAttacks
            };
            Touch(campaign);
            var names = string.Join(", ", opportunityAttacks.Select(x => x.ReactorName));
            var pendingSummary = $"{character.Name}'s move would leave the reach of {names}. Resolve or decline those Opportunity Attacks before movement is committed.";
            Log(campaign, "opportunity_attack_trigger", pendingSummary, dmOnly: true);
            return new CombatMoveResult(encounter.Id, combatant.Id, character.Id, fromX, fromY, gridX, gridY, distanceFeet, movementCostFeet, combatant.MovementRemainingFeet, false, opportunityAttacks, pendingSummary);
        }

        return CommitCombatMove(campaign, encounter, combatant, character, gridX, gridY, distanceFeet, movementCostFeet);
    }

    public IReadOnlyList<OpportunityAttackWindow> GetPendingOpportunityAttacks(CampaignState campaign, string encounterId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        return encounter.PendingMove?.OpportunityAttacks.Where(x => !x.Resolved).ToArray() ?? [];
    }

    public CombatantState TakeDisengage(CampaignState campaign, string encounterId, string combatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("A pending move must be resolved before taking the Disengage action.");
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        ConsumeAction(combatant, character, "Disengage");
        combatant.Disengaging = true;
        Touch(campaign);
        Log(campaign, "disengage", $"{character.Name} took the Disengage action and won't provoke Opportunity Attacks for the rest of this turn.");
        return combatant;
    }

    public CombatantState TakeDash(CampaignState campaign, string encounterId, string combatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("A pending move must be resolved before taking the Dash action.");
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        ConsumeAction(combatant, character, "Dash");
        var extraMovement = CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects);
        combatant.MovementRemainingFeet = checked(combatant.MovementRemainingFeet + extraMovement);
        Touch(campaign);
        Log(campaign, "dash", $"{character.Name} took the Dash action and gained {extraMovement} feet of movement for the rest of the turn.");
        return combatant;
    }

    public CombatantState TakeDodge(CampaignState campaign, string encounterId, string combatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("A pending move must be resolved before taking the Dodge action.");
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        ConsumeAction(combatant, character, "Dodge");
        combatant.Dodging = true;
        Touch(campaign);
        Log(campaign, "dodge", $"{character.Name} took the Dodge action. Attack rolls against them have Disadvantage and their Dexterity saving throws have Advantage until the start of their next turn while the Dodge benefit remains active.");
        return combatant;
    }

    public CombatantState TakeHelpAttack(CampaignState campaign, string encounterId, string helperCombatantId, string targetCombatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var helperCombatant = RequireCombatant(encounter, helperCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, helperCombatant.Id);
        if (helperCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot use Help to distract itself.");
        var helper = RequireCharacter(campaign, helperCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (!helperCombatant.Positioned || !targetCombatant.Positioned)
            throw new InvalidOperationException("Both creatures must be positioned to use Help on an attack roll.");
        if (GridDistanceFeet(helperCombatant.GridX, helperCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY) > 5)
            throw new InvalidOperationException($"{target.Name} must be within 5 feet of {helper.Name} for the Help action to assist an attack.");
        if (!string.IsNullOrWhiteSpace(helperCombatant.Side) && !string.IsNullOrWhiteSpace(targetCombatant.Side) && helperCombatant.Side.Equals(targetCombatant.Side, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The attack-assist option of Help targets an enemy, not an ally.");
        ConsumeAction(helperCombatant, helper, "Help");
        helperCombatant.HelpAttackTargetCombatantId = targetCombatant.Id;
        Touch(campaign);
        Log(campaign, "help_attack", $"{helper.Name} used Help to distract {target.Name}. The next attack roll by one of {helper.Name}'s allies against {target.Name} has Advantage before the start of {helper.Name}'s next turn.");
        return helperCombatant;
    }

    public CombatantState TakeHelpAbilityCheck(
        CampaignState campaign,
        string encounterId,
        string helperCombatantId,
        string allyCombatantId,
        string proficiency)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var helperCombatant = RequireCombatant(encounter, helperCombatantId);
        var allyCombatant = RequireCombatant(encounter, allyCombatantId);
        EnsureCurrentTurn(encounter, helperCombatant.Id);
        if (helperCombatant.Id.Equals(allyCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot use Help to assist its own ability check.");
        var helper = RequireCharacter(campaign, helperCombatant.CharacterId);
        var ally = RequireCharacter(campaign, allyCombatant.CharacterId);
        if (!string.IsNullOrWhiteSpace(helperCombatant.Side) && !string.IsNullOrWhiteSpace(allyCombatant.Side) && !helperCombatant.Side.Equals(allyCombatant.Side, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The ability-check option of Help assists an ally, not an enemy.");
        var normalized = (proficiency ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("A skill or tool proficiency is required for Help.", nameof(proficiency));
        var proficient = ContainsIgnoreCase(helper.SkillProficiencies, normalized) || ContainsIgnoreCase(helper.ToolProficiencies, normalized);
        if (!proficient)
            throw new InvalidOperationException($"{helper.Name} is not proficient with {normalized} and cannot use that proficiency to Help the check.");
        ConsumeAction(helperCombatant, helper, "Help");
        helperCombatant.HelpAbilityTargetCharacterId = ally.Id;
        helperCombatant.HelpAbilityProficiency = normalized;
        Touch(campaign);
        Log(campaign, "help_ability", $"{helper.Name} used Help to assist {ally.Name}'s next {normalized} ability check before the start of {helper.Name}'s next turn. The DM remains responsible for whether the assistance is physically or verbally possible.");
        return helperCombatant;
    }

    public CombatSkillActionResult TakeSearchAction(CampaignState campaign, string encounterId, string combatantId, string skill, int dc, DiceService dice) =>
        ResolveCombatSkillAction(campaign, encounterId, combatantId, "Search", "wisdom", skill, dc, dice, ["insight", "medicine", "perception", "survival"]);

    public CombatSkillActionResult TakeStudyAction(CampaignState campaign, string encounterId, string combatantId, string skill, int dc, DiceService dice) =>
        ResolveCombatSkillAction(campaign, encounterId, combatantId, "Study", "intelligence", skill, dc, dice, ["arcana", "history", "investigation", "nature", "religion"]);

    public CombatSkillActionResult TakeInfluenceAction(CampaignState campaign, string encounterId, string combatantId, string skill, int dc, DiceService dice)
    {
        var normalized = (skill ?? "").Trim().ToLowerInvariant();
        var ability = normalized == "animal handling" ? "wisdom" : "charisma";
        return ResolveCombatSkillAction(campaign, encounterId, combatantId, "Influence", ability, normalized, dc, dice, ["animal handling", "deception", "intimidation", "performance", "persuasion"]);
    }

    public FirstAidResult TakeFirstAid(CampaignState campaign, string encounterId, string helperCombatantId, string targetCombatantId, DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var helperCombatant = RequireCombatant(encounter, helperCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, helperCombatant.Id);
        if (helperCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot administer first aid to itself with this action.");
        var helper = RequireCharacter(campaign, helperCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is dead and cannot be stabilized with first aid.");
        var unconscious = target.Conditions.Any(c => c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase));
        if (target.CurrentHp > 0 && !unconscious)
            throw new InvalidOperationException($"{target.Name} does not currently need combat first aid.");
        if (target.CurrentHp == 0 && target.Stable)
            throw new InvalidOperationException($"{target.Name} is already Stable.");
        if (helperCombatant.Positioned && targetCombatant.Positioned && GridDistanceFeet(helperCombatant.GridX, helperCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY) > 5)
            throw new InvalidOperationException($"{helper.Name} must be adjacent to {target.Name} to administer first aid on the tactical grid.");

        ConsumeAction(helperCombatant, helper, "Help (First Aid)");
        var baseMode = AbilityCheckModeFromConditions(helper);
        var check = ResolveAbilityCheckWithDice(campaign, helper.Id, "wisdom", 10, dice, baseMode, "medicine");
        var stabilized = false;
        var awakened = false;
        if (check.Success)
        {
            if (target.CurrentHp == 0)
            {
                target.Stable = true;
                target.DeathSaveSuccesses = 0;
                target.DeathSaveFailures = 0;
                stabilized = true;
            }
            else if (unconscious)
            {
                RemoveConditionInternal(target, "Unconscious");
                awakened = true;
            }
        }

        var summary = check.Success
            ? stabilized
                ? $"{helper.Name} administered first aid to {target.Name}; the DC 10 Wisdom (Medicine) check succeeded and {target.Name} is Stable."
                : $"{helper.Name} administered first aid to {target.Name}; the DC 10 Wisdom (Medicine) check succeeded and the Unconscious condition ended."
            : $"{helper.Name} administered first aid to {target.Name}, but the DC 10 Wisdom (Medicine) check failed.";
        Touch(campaign);
        Log(campaign, "first_aid", summary);
        return new FirstAidResult(encounter.Id, helperCombatant.Id, target.Id, check, stabilized, awakened, summary);
    }

    public GrappleResult ResolveUnarmedGrapple(
        CampaignState campaign,
        string encounterId,
        string attackerCombatantId,
        string targetCombatantId,
        DiceService dice,
        string? targetSaveAbility = null)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var attackerCombatant = RequireCombatant(encounter, attackerCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        if (attackerCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot grapple itself.");

        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsureCanTakeAttackOption(attacker);
        EnsureWithinUnarmedRange(attackerCombatant, targetCombatant, attacker, target);
        if (SizeRank(target.Size) > SizeRank(attacker.Size) + 1)
            throw new InvalidOperationException($"{target.Name} is too large for {attacker.Name} to grapple with an Unarmed Strike.");
        if (AvailableGrappleHands(encounter, attackerCombatant, attacker) <= 0)
            throw new InvalidOperationException($"{attacker.Name} needs a free hand to grapple another creature.");
        if (encounter.Grapples.Any(g => g.GrapplerCombatantId == attackerCombatant.Id && g.TargetCombatantId == targetCombatant.Id))
            throw new InvalidOperationException($"{attacker.Name} is already grappling {target.Name}.");

        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var dc = 8 + CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(attacker, "strength")) + Math.Max(0, attacker.ProficiencyBonus);
        var saveAbility = ChooseStrengthOrDexteritySave(target, targetSaveAbility);
        var save = ResolveStrengthDexteritySaveForUnarmed(campaign, target, saveAbility, dc, dice);
        var grappled = !save.Success;
        if (grappled)
        {
            encounter.Grapples.Add(new GrappleState
            {
                GrapplerCombatantId = attackerCombatant.Id,
                TargetCombatantId = targetCombatant.Id,
                EscapeDc = dc,
                ReachFeet = 5
            });
            AddConditionInternal(target, "Grappled");
            targetCombatant.MovementRemainingFeet = 0;
        }

        var summary = grappled
            ? $"{attacker.Name} grappled {target.Name}. {target.Name} failed the {saveAbility} save against DC {dc}."
            : $"{target.Name} resisted {attacker.Name}'s grapple with a successful {saveAbility} save against DC {dc}.";
        Touch(campaign);
        Log(campaign, "unarmed_grapple", summary);
        return new GrappleResult(encounter.Id, attackerCombatant.Id, targetCombatant.Id, dc, saveAbility, save, grappled, summary);
    }

    public ShoveResult ResolveUnarmedShove(
        CampaignState campaign,
        string encounterId,
        string attackerCombatantId,
        string targetCombatantId,
        string effect,
        DiceService dice,
        string? targetSaveAbility = null)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var attackerCombatant = RequireCombatant(encounter, attackerCombatantId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, attackerCombatant.Id);
        if (attackerCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot shove itself.");

        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        EnsureCanTakeAttackOption(attacker);
        EnsureWithinUnarmedRange(attackerCombatant, targetCombatant, attacker, target);
        if (SizeRank(target.Size) > SizeRank(attacker.Size) + 1)
            throw new InvalidOperationException($"{target.Name} is too large for {attacker.Name} to shove with an Unarmed Strike.");

        var normalizedEffect = (effect ?? "prone").Trim().ToLowerInvariant();
        if (normalizedEffect is not ("prone" or "push"))
            throw new ArgumentException("Shove effect must be 'prone' or 'push'.", nameof(effect));

        ConsumeAttackActionAttack(attackerCombatant, attacker);
        var dc = 8 + CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(attacker, "strength")) + Math.Max(0, attacker.ProficiencyBonus);
        var saveAbility = ChooseStrengthOrDexteritySave(target, targetSaveAbility);
        var save = ResolveStrengthDexteritySaveForUnarmed(campaign, target, saveAbility, dc, dice);
        var succeeded = !save.Success;
        var applied = normalizedEffect;
        if (succeeded && normalizedEffect == "prone")
        {
            AddConditionInternal(target, "Prone");
        }
        else if (succeeded)
        {
            PushTargetOneSquare(encounter, attackerCombatant, targetCombatant, attacker, target);
            EndInvalidGrapples(campaign, encounter);
        }

        var effectText = normalizedEffect == "prone" ? "knocked Prone" : "pushed 5 feet away";
        var summary = succeeded
            ? $"{attacker.Name} shoved {target.Name}; {target.Name} failed the {saveAbility} save against DC {dc} and was {effectText}."
            : $"{target.Name} resisted {attacker.Name}'s shove with a successful {saveAbility} save against DC {dc}.";
        Touch(campaign);
        Log(campaign, "unarmed_shove", summary);
        return new ShoveResult(encounter.Id, attackerCombatant.Id, targetCombatant.Id, dc, saveAbility, save, succeeded, applied, summary);
    }

    public EscapeGrappleResult EscapeGrapple(
        CampaignState campaign,
        string encounterId,
        string targetCombatantId,
        string grapplerCombatantId,
        string skill,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        EnsureCurrentTurn(encounter, targetCombatant.Id);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        var grapple = encounter.Grapples.FirstOrDefault(g => g.TargetCombatantId == targetCombatant.Id && g.GrapplerCombatantId == grapplerCombatantId)
            ?? throw new InvalidOperationException($"{target.Name} is not grappled by that combatant.");
        var normalizedSkill = (skill ?? "athletics").Trim().ToLowerInvariant();
        var ability = normalizedSkill switch
        {
            "athletics" => "strength",
            "acrobatics" => "dexterity",
            _ => throw new ArgumentException("Escaping a grapple uses Athletics or Acrobatics.", nameof(skill))
        };
        ConsumeAction(targetCombatant, target, "escape grapple");
        var mode = AbilityCheckModeFromConditions(target);
        var check = ResolveAbilityCheckWithDice(campaign, target.Id, ability, grapple.EscapeDc, dice, mode, normalizedSkill);
        var escaped = check.Success;
        if (escaped)
        {
            encounter.Grapples.Remove(grapple);
            RefreshGrappledCondition(campaign, encounter, targetCombatant.Id);
        }
        var grappler = RequireCharacter(campaign, RequireCombatant(encounter, grapplerCombatantId).CharacterId);
        var summary = escaped
            ? $"{target.Name} escaped {grappler.Name}'s grapple with {normalizedSkill}."
            : $"{target.Name} failed to escape {grappler.Name}'s grapple with {normalizedSkill}.";
        Touch(campaign);
        Log(campaign, "grapple_escape", summary);
        return new EscapeGrappleResult(encounter.Id, grapplerCombatantId, targetCombatant.Id, grapple.EscapeDc, normalizedSkill, check, escaped, summary);
    }

    public string ReleaseGrapple(CampaignState campaign, string encounterId, string grapplerCombatantId, string targetCombatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        var grapple = encounter.Grapples.FirstOrDefault(g => g.GrapplerCombatantId == grapplerCombatantId && g.TargetCombatantId == targetCombatantId)
            ?? throw new InvalidOperationException("That grapple is not active.");
        var grappler = RequireCharacter(campaign, RequireCombatant(encounter, grapplerCombatantId).CharacterId);
        var target = RequireCharacter(campaign, RequireCombatant(encounter, targetCombatantId).CharacterId);
        encounter.Grapples.Remove(grapple);
        RefreshGrappledCondition(campaign, encounter, targetCombatantId);
        var summary = $"{grappler.Name} released {target.Name} from the grapple.";
        Touch(campaign);
        Log(campaign, "grapple_released", summary);
        return summary;
    }

    public CombatantState StandFromProne(CampaignState campaign, string encounterId, string combatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!character.Conditions.Any(c => c.Equals("Prone", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{character.Name} is not Prone.");
        var speed = CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects);
        if (speed <= 0) throw new InvalidOperationException($"{character.Name} cannot stand while Speed is 0.");
        var cost = speed / 2;
        if (combatant.MovementRemainingFeet < cost)
            throw new InvalidOperationException($"Standing costs {cost} feet of movement, but {character.Name} has only {combatant.MovementRemainingFeet} feet remaining.");
        combatant.MovementRemainingFeet -= cost;
        RemoveConditionInternal(character, "Prone");
        Touch(campaign);
        Log(campaign, "stand_from_prone", $"{character.Name} stood from Prone by spending {cost} feet of movement.");
        return combatant;
    }

    public EncounterAttackResult ResolveOpportunityAttack(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        string? attackName,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        var pending = encounter.PendingMove ?? throw new InvalidOperationException("There is no pending movement that can provoke an Opportunity Attack.");
        var window = pending.OpportunityAttacks.FirstOrDefault(x => x.ReactorCombatantId.Equals(reactorCombatantId, StringComparison.OrdinalIgnoreCase) && !x.Resolved)
            ?? throw new InvalidOperationException("That combatant has no unresolved Opportunity Attack for the pending move.");
        var reactorCombatant = RequireCombatant(encounter, reactorCombatantId);
        var moverCombatant = RequireCombatant(encounter, pending.CombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        var mover = RequireCharacter(campaign, moverCombatant.CharacterId);
        if (!reactorCombatant.ReactionAvailable) throw new InvalidOperationException($"{reactor.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(reactor)) throw new InvalidOperationException($"{reactor.Name} cannot take a Reaction right now.");

        var profile = SelectMeleeAttack(reactor, attackName);
        var currentDistance = GridDistanceFeet(reactorCombatant.GridX, reactorCombatant.GridY, window.TriggerX, window.TriggerY);
        var reach = Math.Max(5, profile.ReachFeet);
        if (currentDistance > reach)
            throw new InvalidOperationException($"{mover.Name} is not within {reactor.Name}'s {profile.Name} reach at the Opportunity Attack trigger point.");

        reactorCombatant.ReactionAvailable = false;
        var attackMode = AttackRollMode(campaign, encounter, reactorCombatant, moverCombatant, reactor, mover);
        var helpUsed = ConsumeHelpAttackAdvantage(encounter, reactorCombatant, moverCombatant);
        var automaticCritical = IsAutomaticCriticalHitTarget(reactorCombatant, moverCombatant, mover);
        var effectAttackBonus = RollActiveAttackBonus(campaign, reactor.Id, dice);
        var attack = dice.Attack(profile.AttackBonus, EffectiveArmorClass(campaign, mover), profile.DamageExpression, attackMode, automaticCritical, effectAttackBonus);
        ConsumeNextAttackAdvantageEffect(campaign, mover.Id);
        BreakHidden(campaign, encounter, reactorCombatant, "making an attack roll");
        DamageResult? damage = null;
        ConcentrationCheckResult? concentration = null;
        if (attack.Hit)
        {
            var resolution = ApplyDamageWithConcentration(campaign, mover.Id, attack.Damage, dice, profile.DamageType, attack.Critical);
            damage = resolution.Damage;
            concentration = resolution.Concentration;
        }

        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var summary = attack.Hit
            ? $"{reactor.Name} used a Reaction for an Opportunity Attack with {profile.Name} against {mover.Name}: {attack.Summary}{helpText}"
            : $"{reactor.Name} used a Reaction for an Opportunity Attack with {profile.Name} against {mover.Name}: miss.{helpText}";
        if (concentration is not null) summary += $" {concentration.Summary}";
        window.Resolved = true;
        window.Declined = false;
        window.ResolutionSummary = summary;
        Log(campaign, "opportunity_attack", summary);
        FinalizePendingMoveIfReady(campaign, encounter);
        Touch(campaign);
        return new EncounterAttackResult(encounter.Id, reactor.Name, mover.Name, profile.Name, attack, damage, summary, concentration, true, 0);
    }

    public string DeclineOpportunityAttack(CampaignState campaign, string encounterId, string reactorCombatantId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        var pending = encounter.PendingMove ?? throw new InvalidOperationException("There is no pending movement that can provoke an Opportunity Attack.");
        var window = pending.OpportunityAttacks.FirstOrDefault(x => x.ReactorCombatantId.Equals(reactorCombatantId, StringComparison.OrdinalIgnoreCase) && !x.Resolved)
            ?? throw new InvalidOperationException("That combatant has no unresolved Opportunity Attack for the pending move.");
        window.Resolved = true;
        window.Declined = true;
        window.ResolutionSummary = $"{window.ReactorName} declined the Opportunity Attack.";
        Log(campaign, "opportunity_attack_declined", window.ResolutionSummary, dmOnly: true);
        FinalizePendingMoveIfReady(campaign, encounter);
        Touch(campaign);
        return window.ResolutionSummary;
    }

    private CombatMoveResult CommitCombatMove(CampaignState campaign, EncounterState encounter, CombatantState combatant, CharacterSheet character, int gridX, int gridY, int distanceFeet, int movementCostFeet)
    {
        var fromX = combatant.GridX;
        var fromY = combatant.GridY;
        var path = TraceGridPath(fromX, fromY, gridX, gridY);
        var dice = new DiceService();
        var previousX = fromX;
        var previousY = fromY;
        var spentMovement = 0;
        var stepsCompleted = 0;

        foreach (var (nextX, nextY) in path)
        {
            var stepCost = MovementStepCostFeet(encounter, nextX, nextY, character);
            combatant.GridX = nextX;
            combatant.GridY = nextY;
            combatant.MovementRemainingFeet = Math.Max(0, combatant.MovementRemainingFeet - stepCost);
            spentMovement += stepCost;
            stepsCompleted++;
            ProcessBattlefieldEffectsOnMovement(campaign, encounter, combatant, previousX, previousY, nextX, nextY, dice);
            previousX = nextX;
            previousY = nextY;
            if (character.Dead || CharacterMechanics.HasCondition(character, "Unconscious") || CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects) <= 0)
                break;
        }

        EndInvalidGrapples(campaign, encounter);
        Touch(campaign);
        var actualDistanceFeet = stepsCompleted * 5;
        var difficult = spentMovement > actualDistanceFeet ? $" Difficult Terrain increased the movement cost to {spentMovement} feet." : "";
        var interrupted = stepsCompleted < path.Count ? " Movement ended early because the creature could no longer continue." : "";
        var summary = $"{character.Name} moved {actualDistanceFeet} feet to grid ({combatant.GridX}, {combatant.GridY}); {combatant.MovementRemainingFeet} feet remain.{difficult}{interrupted}";
        Log(campaign, "combat_move", summary);
        return new CombatMoveResult(encounter.Id, combatant.Id, character.Id, fromX, fromY, combatant.GridX, combatant.GridY, actualDistanceFeet, spentMovement, combatant.MovementRemainingFeet, true, [], summary);
    }

    private void FinalizePendingMoveIfReady(CampaignState campaign, EncounterState encounter)
    {
        var pending = encounter.PendingMove;
        if (pending is null || pending.OpportunityAttacks.Any(x => !x.Resolved)) return;
        var combatant = RequireCombatant(encounter, pending.CombatantId);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.Dead || character.Conditions.Any(c => c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)) || CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects) <= 0)
        {
            var summary = $"{character.Name}'s pending movement was cancelled because the creature can no longer complete it.";
            encounter.PendingMove = null;
            Log(campaign, "combat_move_cancelled", summary);
            return;
        }

        EnsureSquareAvailable(encounter, combatant.Id, pending.ToX, pending.ToY);
        if (pending.ReadiedReactionMove)
            CommitReadiedCombatMove(campaign, encounter, combatant, character, pending.ToX, pending.ToY, pending.DistanceFeet, pending.MovementCostFeet);
        else
            CommitCombatMove(campaign, encounter, combatant, character, pending.ToX, pending.ToY, pending.DistanceFeet, pending.MovementCostFeet);
        encounter.PendingMove = null;
    }

    private List<OpportunityAttackWindow> FindOpportunityAttackWindows(CampaignState campaign, EncounterState encounter, CombatantState mover, int destinationX, int destinationY)
    {
        var windows = new List<OpportunityAttackWindow>();
        var path = TraceGridPath(mover.GridX, mover.GridY, destinationX, destinationY);
        foreach (var reactorCombatant in encounter.Combatants)
        {
            if (reactorCombatant.Id.Equals(mover.Id, StringComparison.OrdinalIgnoreCase) || !reactorCombatant.Positioned || !reactorCombatant.ReactionAvailable) continue;
            if (!string.IsNullOrWhiteSpace(mover.Side) && !string.IsNullOrWhiteSpace(reactorCombatant.Side) && mover.Side.Equals(reactorCombatant.Side, StringComparison.OrdinalIgnoreCase)) continue;
            var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
            if (!CanTakeReaction(reactor)) continue;
            if (!CanSeeCombatant(campaign, encounter, reactorCombatant, mover)) continue;
            var meleeProfiles = GetMeleeAttacks(reactor).ToArray();
            if (meleeProfiles.Length == 0) continue;
            var maxReach = meleeProfiles.Max(a => Math.Max(5, a.ReachFeet));

            var previousX = mover.GridX;
            var previousY = mover.GridY;
            foreach (var (nextX, nextY) in path)
            {
                var before = GridDistanceFeet(reactorCombatant.GridX, reactorCombatant.GridY, previousX, previousY);
                var after = GridDistanceFeet(reactorCombatant.GridX, reactorCombatant.GridY, nextX, nextY);
                if (before <= maxReach && after > maxReach)
                {
                    windows.Add(new OpportunityAttackWindow
                    {
                        ReactorCombatantId = reactorCombatant.Id,
                        ReactorCharacterId = reactor.Id,
                        ReactorName = reactor.Name,
                        ReachFeet = maxReach,
                        TriggerX = previousX,
                        TriggerY = previousY
                    });
                    break;
                }
                previousX = nextX;
                previousY = nextY;
            }
        }
        return windows;
    }

    private static void EnsureEncounterActionReady(EncounterState encounter)
    {
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.Combatants.Any(c => c.Initiative is null))
            throw new InvalidOperationException("Initiative must be finalized before taking a combat action.");
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve or decline the pending Opportunity Attacks before taking another action.");
    }

    private static void EnsureCanTakeAttackOption(CharacterSheet attacker)
    {
        if (CharacterMechanics.IsIncapacitated(attacker))
            throw new InvalidOperationException($"{attacker.Name} is Incapacitated and cannot make an Unarmed Strike right now.");
    }

    private static void EnsureWithinUnarmedRange(CombatantState attacker, CombatantState target, CharacterSheet attackerCharacter, CharacterSheet targetCharacter)
    {
        if (!attacker.Positioned || !target.Positioned)
            throw new InvalidOperationException("Both creatures must be positioned on the tactical grid for Grapple or Shove.");
        var distance = GridDistanceFeet(attacker.GridX, attacker.GridY, target.GridX, target.GridY);
        if (distance > 5)
            throw new InvalidOperationException($"{targetCharacter.Name} is {distance} feet away; {attackerCharacter.Name}'s Unarmed Strike can Grapple or Shove only within 5 feet.");
    }

    private static string NormalizeSize(string? size)
    {
        var value = (size ?? "Medium").Trim().ToLowerInvariant();
        return value switch
        {
            "tiny" => "Tiny",
            "small" => "Small",
            "medium" => "Medium",
            "large" => "Large",
            "huge" => "Huge",
            "gargantuan" => "Gargantuan",
            _ => throw new ArgumentException($"Unknown creature size '{size}'.")
        };
    }

    private static int SizeRank(string? size) => NormalizeSize(size) switch
    {
        "Tiny" => 0,
        "Small" => 1,
        "Medium" => 2,
        "Large" => 3,
        "Huge" => 4,
        "Gargantuan" => 5,
        _ => 2
    };

    private static int AvailableGrappleHands(EncounterState encounter, CombatantState grappler, CharacterSheet character)
    {
        var used = encounter.Grapples.Count(g => g.GrapplerCombatantId == grappler.Id);
        return Math.Max(0, Math.Max(0, character.FreeHands) - used);
    }

    private static string ChooseStrengthOrDexteritySave(CharacterSheet target, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var normalized = CharacterMechanics.NormalizeAbility(requested);
            if (normalized is not ("strength" or "dexterity"))
                throw new ArgumentException("The target may choose only Strength or Dexterity for this save.", nameof(requested));
            return normalized;
        }
        int Score(string ability)
        {
            var normalized = CharacterMechanics.NormalizeAbility(ability);
            var proficient = ContainsIgnoreCase(target.SavingThrowProficiencies, normalized) || ContainsIgnoreCase(target.SavingThrowProficiencies, normalized[..3]);
            return CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, normalized)) + (proficient ? Math.Max(0, target.ProficiencyBonus) : 0);
        }
        return Score("dexterity") > Score("strength") ? "dexterity" : "strength";
    }

    private D20TestResult ResolveStrengthDexteritySaveForUnarmed(CampaignState campaign, CharacterSheet target, string ability, int dc, DiceService dice)
    {
        var normalized = CharacterMechanics.NormalizeAbility(ability);
        var mode = CharacterMechanics.SavingThrowModeFromConditions(target, normalized);
        var rolls = dice.RollD20(mode);
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
        return ResolveSavingThrow(campaign, target.Id, normalized, dc, rolls.RollOne, rolls.RollTwo, D20RollMode.Normal, effectSaveBonus);
    }

    private static D20RollMode AbilityCheckModeFromConditions(CharacterSheet character)
    {
        var disadvantage = CharacterMechanics.HasCondition(character, "Frightened")
            || CharacterMechanics.HasCondition(character, "Poisoned");
        return disadvantage ? D20RollMode.Disadvantage : D20RollMode.Normal;
    }

    private static void PushTargetOneSquare(EncounterState encounter, CombatantState attacker, CombatantState target, CharacterSheet attackerCharacter, CharacterSheet targetCharacter)
    {
        var dx = Math.Sign(target.GridX - attacker.GridX);
        var dy = Math.Sign(target.GridY - attacker.GridY);
        if (dx == 0 && dy == 0) throw new InvalidOperationException("The target must occupy a different grid square to be pushed away.");
        var destinationX = target.GridX + dx;
        var destinationY = target.GridY + dy;
        if (encounter.Terrain.Any(t => t.BlocksMovement && ContainsSquare(t, destinationX, destinationY)))
            throw new InvalidOperationException($"{targetCharacter.Name} cannot be pushed into blocking terrain at grid ({destinationX}, {destinationY}).");
        EnsureSquareAvailable(encounter, target.Id, destinationX, destinationY);
        target.GridX = destinationX;
        target.GridY = destinationY;
    }

    private static void RefreshGrappledCondition(CampaignState campaign, EncounterState encounter, string targetCombatantId)
    {
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (encounter.Grapples.Any(g => g.TargetCombatantId == targetCombatantId))
        {
            AddConditionInternal(target, "Grappled");
            targetCombatant.MovementRemainingFeet = 0;
        }
        else RemoveConditionInternal(target, "Grappled");
    }

    private static void EndGrapplesForCharacter(CampaignState campaign, string characterId, bool includeTarget)
    {
        foreach (var encounter in campaign.Encounters.Where(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)))
        {
            var combatantIds = encounter.Combatants.Where(c => c.CharacterId == characterId).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (combatantIds.Count == 0) continue;
            var removedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grapple in encounter.Grapples.Where(g => combatantIds.Contains(g.GrapplerCombatantId) || (includeTarget && combatantIds.Contains(g.TargetCombatantId))).ToArray())
            {
                removedTargets.Add(grapple.TargetCombatantId);
                encounter.Grapples.Remove(grapple);
            }
            foreach (var targetId in removedTargets) RefreshGrappledCondition(campaign, encounter, targetId);
        }
    }

    private static void EndInvalidGrapples(CampaignState campaign, EncounterState encounter)
    {
        foreach (var grapple in encounter.Grapples.ToArray())
        {
            var grapplerCombatant = encounter.Combatants.FirstOrDefault(c => c.Id == grapple.GrapplerCombatantId);
            var targetCombatant = encounter.Combatants.FirstOrDefault(c => c.Id == grapple.TargetCombatantId);
            if (grapplerCombatant is null || targetCombatant is null)
            {
                encounter.Grapples.Remove(grapple);
                continue;
            }
            var grappler = RequireCharacter(campaign, grapplerCombatant.CharacterId);
            var invalid = grappler.Dead
                || grappler.Conditions.Any(c => c.Equals("Incapacitated", StringComparison.OrdinalIgnoreCase) || c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase))
                || !grapplerCombatant.Positioned
                || !targetCombatant.Positioned
                || GridDistanceFeet(grapplerCombatant.GridX, grapplerCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY) > grapple.ReachFeet;
            if (!invalid) continue;
            encounter.Grapples.Remove(grapple);
            RefreshGrappledCondition(campaign, encounter, targetCombatant.Id);
            var target = RequireCharacter(campaign, targetCombatant.CharacterId);
            Log(campaign, "grapple_ended", $"{grappler.Name}'s grapple on {target.Name} ended because the grapple requirements were no longer met.");
        }
    }

    private CombatSkillActionResult ResolveCombatSkillAction(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string actionName,
        string ability,
        string skill,
        int dc,
        DiceService dice,
        IReadOnlyCollection<string> allowedSkills)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        var normalizedSkill = (skill ?? "").Trim().ToLowerInvariant();
        if (!allowedSkills.Contains(normalizedSkill, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{skill} is not a valid skill for the {actionName} action.", nameof(skill));
        if (dc < 1) throw new ArgumentOutOfRangeException(nameof(dc), "DC must be at least 1.");
        ConsumeAction(combatant, character, actionName);
        var baseMode = AbilityCheckModeFromConditions(character);
        var check = ResolveAbilityCheckWithDice(campaign, character.Id, ability, dc, dice, baseMode, normalizedSkill);
        var summary = $"{character.Name} took the {actionName} action using {ability} ({normalizedSkill}): {check.Summary}";
        Touch(campaign);
        Log(campaign, $"{actionName.ToLowerInvariant()}_action", summary);
        return new CombatSkillActionResult(encounter.Id, combatant.Id, character.Id, actionName, ability, normalizedSkill, check, summary);
    }

    private static CombatantState? FindHelpAbilityCheckHelper(CampaignState campaign, string targetCharacterId, string? proficiency)
    {
        if (string.IsNullOrWhiteSpace(proficiency)) return null;
        foreach (var encounter in campaign.Encounters.Where(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)))
        {
            var helper = encounter.Combatants.FirstOrDefault(c =>
                string.Equals(c.HelpAbilityTargetCharacterId, targetCharacterId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.HelpAbilityProficiency, proficiency.Trim(), StringComparison.OrdinalIgnoreCase));
            if (helper is not null) return helper;
        }
        return null;
    }

    private static D20RollMode CombineAdvantage(D20RollMode current, D20RollMode added)
    {
        if (added == D20RollMode.Normal) return current;
        if (current == D20RollMode.Normal) return added;
        return current == added ? current : D20RollMode.Normal;
    }

    private static bool HasHelpAttackAdvantage(EncounterState encounter, CombatantState attackerCombatant, CombatantState targetCombatant) =>
        encounter.Combatants.Any(helper =>
            helper.Id != attackerCombatant.Id
            && helper.HelpAttackTargetCombatantId == targetCombatant.Id
            && !string.IsNullOrWhiteSpace(helper.Side)
            && helper.Side.Equals(attackerCombatant.Side, StringComparison.OrdinalIgnoreCase));

    private static bool ConsumeHelpAttackAdvantage(EncounterState encounter, CombatantState attackerCombatant, CombatantState targetCombatant)
    {
        var helper = encounter.Combatants.FirstOrDefault(h =>
            h.Id != attackerCombatant.Id
            && h.HelpAttackTargetCombatantId == targetCombatant.Id
            && !string.IsNullOrWhiteSpace(h.Side)
            && h.Side.Equals(attackerCombatant.Side, StringComparison.OrdinalIgnoreCase));
        if (helper is null) return false;
        helper.HelpAttackTargetCombatantId = null;
        return true;
    }

    private static D20RollMode AttackRollMode(CampaignState campaign, EncounterState encounter, CombatantState attackerCombatant, CombatantState targetCombatant, CharacterSheet attacker, CharacterSheet target)
    {
        var advantage = attackerCombatant.IsHidden || CharacterMechanics.HasCondition(attacker, "Invisible") || HasNextAttackAdvantageEffect(campaign, target.Id);
        var disadvantage = targetCombatant.IsHidden || CharacterMechanics.HasCondition(target, "Invisible");
        if (!CanSeeCombatant(campaign, encounter, attackerCombatant, targetCombatant)) disadvantage = true;
        if (!CanSeeCombatant(campaign, encounter, targetCombatant, attackerCombatant)) advantage = true;
        if (CharacterMechanics.HasCondition(attacker, "Prone")
            || CharacterMechanics.HasCondition(attacker, "Blinded")
            || CharacterMechanics.HasCondition(attacker, "Frightened")
            || CharacterMechanics.HasCondition(attacker, "Poisoned")
            || CharacterMechanics.HasCondition(attacker, "Restrained"))
            disadvantage = true;
        if (CharacterMechanics.HasCondition(target, "Blinded")
            || CharacterMechanics.HasCondition(target, "Restrained")
            || CharacterMechanics.HasCondition(target, "Paralyzed")
            || CharacterMechanics.HasCondition(target, "Stunned")
            || CharacterMechanics.HasCondition(target, "Unconscious"))
            advantage = true;
        if (CharacterMechanics.HasCondition(target, "Prone") || CharacterMechanics.HasCondition(target, "Unconscious"))
        {
            var distance = attackerCombatant.Positioned && targetCombatant.Positioned
                ? GridDistanceFeet(attackerCombatant.GridX, attackerCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY)
                : 999;
            if (distance <= 5) advantage = true;
            else disadvantage = true;
        }
        if (IsDodgeActive(campaign, targetCombatant, target)) disadvantage = true;
        if (HasHelpAttackAdvantage(encounter, attackerCombatant, targetCombatant)) advantage = true;
        var grapplers = encounter.Grapples.Where(g => g.TargetCombatantId == attackerCombatant.Id).Select(g => g.GrapplerCombatantId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grapplers.Count > 0 && !grapplers.Contains(targetCombatant.Id)) disadvantage = true;
        if (advantage == disadvantage) return D20RollMode.Normal;
        return advantage ? D20RollMode.Advantage : D20RollMode.Disadvantage;
    }

    private static bool IsAutomaticCriticalHitTarget(CombatantState attackerCombatant, CombatantState targetCombatant, CharacterSheet target)
    {
        if (!CharacterMechanics.HasCondition(target, "Paralyzed") && !CharacterMechanics.HasCondition(target, "Unconscious"))
            return false;
        if (!attackerCombatant.Positioned || !targetCombatant.Positioned) return false;
        return GridDistanceFeet(attackerCombatant.GridX, attackerCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY) <= 5;
    }

    private static IEnumerable<AttackProfile> GetMeleeAttacks(CharacterSheet character)
    {
        var configured = character.Attacks.Where(a => a.RangeFeet <= 0).ToArray();
        return configured.Length > 0 ? configured : [CharacterMechanics.UnarmedStrikeProfile(character)];
    }

    private static AttackProfile SelectMeleeAttack(CharacterSheet character, string? attackName)
    {
        var attacks = GetMeleeAttacks(character).ToArray();
        if (string.IsNullOrWhiteSpace(attackName)) return attacks[0];
        var chosen = attacks.FirstOrDefault(a => a.Name.Equals(attackName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null && attackName.Trim().Equals("Unarmed Strike", StringComparison.OrdinalIgnoreCase))
            chosen = CharacterMechanics.UnarmedStrikeProfile(character);
        return chosen ?? throw new InvalidOperationException($"{character.Name} has no melee weapon or Unarmed Strike matching '{attackName}'.");
    }

    private static string DefaultCombatSide(CharacterSheet character) =>
        character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) ? "party" : "opposition";

    private static string NormalizeCombatSide(string? side, CharacterSheet character)
    {
        var value = string.IsNullOrWhiteSpace(side) ? DefaultCombatSide(character) : side.Trim().ToLowerInvariant();
        return value switch
        {
            "party" or "opposition" or "neutral" => value,
            _ => throw new ArgumentException("combat side must be party, opposition, or neutral.")
        };
    }

    private static bool IsDodgeActive(CampaignState campaign, CombatantState combatant, CharacterSheet character) =>
        combatant.Dodging
        && CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects) > 0
        && !CharacterMechanics.IsIncapacitated(character);

    private static bool CanTakeReaction(CharacterSheet character) => !CharacterMechanics.IsIncapacitated(character);

    private static IReadOnlyList<(int X, int Y)> TraceGridPath(int fromX, int fromY, int toX, int toY)
    {
        var points = new List<(int X, int Y)>();
        var x = fromX;
        var y = fromY;
        while (x != toX || y != toY)
        {
            if (x < toX) x++; else if (x > toX) x--;
            if (y < toY) y++; else if (y > toY) y--;
            points.Add((x, y));
        }
        return points;
    }

    private static void ValidateMovementPath(EncounterState encounter, string movingCombatantId, IReadOnlyList<(int X, int Y)> path)
    {
        foreach (var (x, y) in path)
        {
            if (encounter.Terrain.Any(t => t.BlocksMovement && ContainsSquare(t, x, y)))
                throw new InvalidOperationException($"Movement is blocked by terrain at grid ({x}, {y}).");
            var occupied = encounter.Combatants.Any(c => c.Positioned && !c.Id.Equals(movingCombatantId, StringComparison.OrdinalIgnoreCase) && c.GridX == x && c.GridY == y);
            if (occupied)
                throw new InvalidOperationException($"The straight-line path is blocked by another combatant at grid ({x}, {y}). Choose a different destination or path.");
        }
    }

    private static int MovementCostFeet(EncounterState encounter, IReadOnlyList<(int X, int Y)> path, CharacterSheet mover)
    {
        var cost = 0;
        foreach (var (x, y) in path)
            cost += MovementStepCostFeet(encounter, x, y, mover);
        return cost;
    }

    private static int MovementStepCostFeet(EncounterState encounter, int x, int y, CharacterSheet mover)
    {
        var cost = 5;
        if (mover.Conditions.Any(c => c.Equals("Prone", StringComparison.OrdinalIgnoreCase)))
            cost += 5;
        if (encounter.Terrain.Any(t => t.DifficultTerrain && ContainsSquare(t, x, y))
            || encounter.BattlefieldEffects.Any(e => e.DifficultTerrain && BattlefieldEffectContainsCell(e, x, y)))
            cost += 5;
        return cost;
    }

    private static bool ContainsSquare(TerrainFeature terrain, int x, int y) =>
        x >= terrain.GridX && x < terrain.GridX + Math.Max(1, terrain.WidthSquares)
        && y >= terrain.GridY && y < terrain.GridY + Math.Max(1, terrain.HeightSquares);

    private static int GetCoverBonus(EncounterState encounter, CombatantState attacker, CombatantState target)
    {
        if (!attacker.Positioned || !target.Positioned) return 0;
        var cover = encounter.Terrain
            .Where(t => ContainsSquare(t, target.GridX, target.GridY))
            .Select(t => (t.Cover ?? "none").Trim().ToLowerInvariant())
            .ToArray();
        if (cover.Contains("total")) return 100;
        if (cover.Contains("three-quarters") || cover.Contains("threequarters") || cover.Contains("three_quarters")) return 5;
        if (cover.Contains("half")) return 2;
        return 0;
    }

    private static string NormalizeCover(string? value)
    {
        var normalized = (value ?? "none").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return normalized switch
        {
            "none" or "" => "none",
            "half" => "half",
            "three-quarters" or "threequarters" => "three-quarters",
            "total" => "total",
            _ => throw new ArgumentException("cover must be none, half, three-quarters, or total.")
        };
    }

    private static string CoverLabel(int bonus) => bonus >= 5 ? "Three-Quarters" : "Half";

    public EncounterState EndEncounter(CampaignState campaign, string encounterId)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        encounter.Status = "completed";
        encounter.PendingMove = null;
        if (campaign.PendingPlayerRoll?.EncounterId == encounter.Id) campaign.PendingPlayerRoll = null;
        foreach (var grapple in encounter.Grapples.ToArray())
        {
            var target = RequireCombatant(encounter, grapple.TargetCombatantId);
            encounter.Grapples.Remove(grapple);
            RefreshGrappledCondition(campaign, encounter, target.Id);
        }
        encounter.SpellSlotCasterIdsThisTurn.Clear();
        encounter.BattlefieldEffects.Clear();
        foreach (var combatant in encounter.Combatants)
        {
            combatant.ActionAvailable = false;
            combatant.BonusActionAvailable = false;
            combatant.AttackActionInProgress = false;
            combatant.AttacksRemainingInAction = 0;
            combatant.MovementRemainingFeet = 0;
            combatant.Disengaging = false;
            combatant.Dodging = false;
            combatant.HelpAttackTargetCombatantId = null;
            combatant.HelpAbilityTargetCharacterId = null;
            combatant.HelpAbilityProficiency = null;
            combatant.IsHidden = false;
            combatant.HideCheckTotal = 0;
            combatant.ReadiedAction = null;
        }
        Touch(campaign);
        Log(campaign, "encounter_ended", $"Encounter '{encounter.Name}' ended.");
        return encounter;
    }

    private static void ResetTurnResources(CampaignState campaign, CombatantState combatant)
    {
        var character = RequireCharacter(campaign, combatant.CharacterId);
        ProcessStartOfTurnEffects(campaign, character);
        if (combatant.ReadiedAction?.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase) == true
            && character.ConcentrationEffect?.StartsWith("Readied spell:", StringComparison.OrdinalIgnoreCase) == true)
            EndConcentrationInternal(campaign, character, "the Ready action expiring at the start of the creature's next turn");
        combatant.MovementRemainingFeet = CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects);
        combatant.ActionAvailable = true;
        combatant.BonusActionAvailable = true;
        combatant.AttackActionInProgress = false;
        combatant.AttacksRemainingInAction = 0;
        combatant.ReactionAvailable = true;
        combatant.Disengaging = false;
        combatant.Dodging = false;
        combatant.DeathSaveRequiredThisTurn = character.CurrentHp == 0 && !character.Stable && !character.Dead;
        combatant.DeathSaveResolvedThisTurn = false;
        combatant.HelpAttackTargetCombatantId = null;
        combatant.HelpAbilityTargetCharacterId = null;
        combatant.HelpAbilityProficiency = null;
        combatant.ReadiedAction = null;
    }

    private static void ConsumeAction(CombatantState combatant, CharacterSheet character, string actionName)
    {
        if (CharacterMechanics.IsIncapacitated(character))
            throw new InvalidOperationException($"{character.Name} is Incapacitated and cannot take the {actionName} action.");
        if (!combatant.ActionAvailable)
            throw new InvalidOperationException($"{character.Name} has already used their action this turn and cannot take the {actionName} action.");
        combatant.ActionAvailable = false;
        combatant.AttackActionInProgress = false;
        combatant.AttacksRemainingInAction = 0;
    }

    private static void ConsumeAttackActionAttack(CombatantState combatant, CharacterSheet character)
    {
        if (CharacterMechanics.IsIncapacitated(character))
            throw new InvalidOperationException($"{character.Name} is Incapacitated and cannot take the Attack action.");
        if (!combatant.AttackActionInProgress)
        {
            if (!combatant.ActionAvailable)
                throw new InvalidOperationException($"{character.Name} has already used their action this turn and cannot take the Attack action.");
            combatant.ActionAvailable = false;
            combatant.AttackActionInProgress = true;
            combatant.AttacksRemainingInAction = Math.Max(1, character.AttacksPerAction);
        }

        if (combatant.AttacksRemainingInAction <= 0)
            throw new InvalidOperationException($"{character.Name} has no attacks remaining in their Attack action this turn.");

        combatant.AttacksRemainingInAction--;
        if (combatant.AttacksRemainingInAction == 0) combatant.AttackActionInProgress = false;
    }

    private static void EnsureCurrentTurn(EncounterState encounter, string combatantId)
    {
        if (encounter.Combatants.Count == 0) throw new InvalidOperationException("The encounter has no combatants.");
        var currentIndex = Math.Clamp(encounter.TurnIndex, 0, encounter.Combatants.Count - 1);
        if (!encounter.Combatants[currentIndex].Id.Equals(combatantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That combatant cannot take a normal turn action or movement outside its current turn.");
    }

    private static int GridDistanceFeet(int x1, int y1, int x2, int y2) =>
        Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1)) * 5;

    private static void EnsureSquareAvailable(EncounterState encounter, string movingCombatantId, int gridX, int gridY)
    {
        var occupied = encounter.Combatants.Any(c =>
            c.Positioned &&
            !c.Id.Equals(movingCombatantId, StringComparison.OrdinalIgnoreCase) &&
            c.GridX == gridX && c.GridY == gridY);
        if (occupied) throw new InvalidOperationException("A combatant already occupies that grid square.");
    }

    private static void ValidateAttackRange(CombatantState attackerCombatant, CombatantState targetCombatant, CharacterSheet attacker, CharacterSheet target, AttackProfile profile)
    {
        if (!attackerCombatant.Positioned || !targetCombatant.Positioned) return;
        var distance = GridDistanceFeet(attackerCombatant.GridX, attackerCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY);
        var maximum = profile.RangeFeet > 0 ? profile.RangeFeet : Math.Max(5, profile.ReachFeet);
        if (distance > maximum)
            throw new InvalidOperationException($"{target.Name} is {distance} feet away, beyond {attacker.Name}'s {profile.Name} range of {maximum} feet.");
    }

    public void AdvanceTime(CampaignState campaign, int minutes)
    {
        if (minutes < 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        var beforeAbsolute = ((long)Math.Max(1, campaign.Day) - 1L) * 1440L + Math.Clamp(campaign.MinuteOfDay, 0, 1439);
        var afterAbsolute = checked(beforeAbsolute + minutes);
        campaign.Day = checked((int)(afterAbsolute / 1440L) + 1);
        campaign.MinuteOfDay = (int)(afterAbsolute % 1440L);
        Touch(campaign);
        Log(campaign, "time_advanced", $"Time advanced by {minutes} minutes to {FormatCampaignTime(campaign)}.");
        ResolveDueTimelineEvents(campaign, afterAbsolute);
    }

    private static void ResolveDueTimelineEvents(CampaignState campaign, long currentAbsoluteMinutes)
    {
        foreach (var evt in campaign.Timeline
            .Where(e => !e.Resolved && e.TriggerType.Equals("time", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CampaignDay)
            .ThenBy(e => e.MinuteOfDay))
        {
            var scheduled = ((long)Math.Max(1, evt.CampaignDay) - 1L) * 1440L + Math.Clamp(evt.MinuteOfDay, 0, 1439);
            if (scheduled > currentAbsoluteMinutes) continue;

            evt.Resolved = true;
            var consequence = string.IsNullOrWhiteSpace(evt.Consequence) ? "The scheduled world event occurred." : evt.Consequence.Trim();
            var linkedQuest = string.IsNullOrWhiteSpace(evt.EffectQuestKey)
                ? null
                : campaign.Quests.FirstOrDefault(q => q.Key.Equals(evt.EffectQuestKey, StringComparison.OrdinalIgnoreCase));
            if (linkedQuest is not null && !string.IsNullOrWhiteSpace(consequence))
            {
                var note = $"Timeline [{evt.Name}]: {consequence}";
                if (!linkedQuest.DmNotes.Contains(note, StringComparison.Ordinal))
                    linkedQuest.DmNotes = string.IsNullOrWhiteSpace(linkedQuest.DmNotes) ? note : linkedQuest.DmNotes.TrimEnd() + Environment.NewLine + note;
            }

            var detail = string.IsNullOrWhiteSpace(evt.DmNotes) ? consequence : $"{consequence} DM notes: {evt.DmNotes}";
            Log(campaign, "timeline_event", $"{evt.Name}: {detail}", dmOnly: true);
        }
    }

    public PurchaseResult Purchase(CampaignState campaign, string buyerCharacterId, string merchantId, string itemId, int quantity = 1)
    {
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        var buyer = RequireCharacter(campaign, buyerCharacterId);
        var merchant = campaign.Merchants.FirstOrDefault(m => m.Id == merchantId)
            ?? throw new KeyNotFoundException("Merchant not found.");
        var item = campaign.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new KeyNotFoundException("Item not found.");
        var stock = merchant.Stock.FirstOrDefault(s => s.ItemId == itemId)
            ?? throw new InvalidOperationException("Merchant does not stock that item.");
        if (stock.Quantity < quantity)
            return new PurchaseResult(false, "Not enough stock.", buyer.Gold, stock.Quantity);

        var unitPrice = stock.PriceGp ?? item.PriceGp;
        var total = checked(unitPrice * quantity);
        if (buyer.Gold < total)
            return new PurchaseResult(false, "Insufficient gold.", buyer.Gold, stock.Quantity);

        buyer.Gold -= total;
        merchant.Gold += total;
        stock.Quantity -= quantity;
        var owned = buyer.Inventory.FirstOrDefault(x => x.ItemId == itemId);
        if (owned is null) buyer.Inventory.Add(new InventoryEntry { ItemId = itemId, Quantity = quantity });
        else owned.Quantity += quantity;
        Touch(campaign);
        Log(campaign, "purchase", $"{buyer.Name} bought {quantity}× {item.Name} from {merchant.Name} for {total} gp.");
        return new PurchaseResult(true, "Purchase complete.", buyer.Gold, stock.Quantity);
    }

    public static string FormatCampaignTime(CampaignState campaign)
    {
        var hour = campaign.MinuteOfDay / 60;
        var minute = campaign.MinuteOfDay % 60;
        return $"Day {campaign.Day}, {hour:00}:{minute:00}";
    }

    private static EncounterState RequireEncounter(CampaignState campaign, string id) =>
        campaign.Encounters.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Encounter not found.");

    private static CombatantState RequireCombatant(EncounterState encounter, string id) =>
        encounter.Combatants.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Combatant not found.");

    private static WorldLocation RequireLocation(CampaignState campaign, string id) =>
        campaign.Locations.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Location not found.");

    private static CharacterSheet RequireCharacter(CampaignState campaign, string id) =>
        campaign.Characters.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Character not found.");

    private static string? NormalizeDamageType(string? damageType) => string.IsNullOrWhiteSpace(damageType) ? null : damageType.Trim();

    private static bool ContainsIgnoreCase(IEnumerable<string> values, string value) =>
        values.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase) || x.Equals("all", StringComparison.OrdinalIgnoreCase));

    private static bool AddConditionInternal(CharacterSheet character, string condition)
    {
        if (character.Conditions.Any(x => x.Equals(condition, StringComparison.OrdinalIgnoreCase))) return false;
        character.Conditions.Add(condition);
        return true;
    }

    private static bool RemoveConditionInternal(CharacterSheet character, string condition)
    {
        var existing = character.Conditions.FirstOrDefault(x => x.Equals(condition, StringComparison.OrdinalIgnoreCase));
        return existing is not null && character.Conditions.Remove(existing);
    }

    private static bool BreaksConcentration(string condition) =>
        condition.Equals("Incapacitated", StringComparison.OrdinalIgnoreCase)
        || condition.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)
        || condition.Equals("Paralyzed", StringComparison.OrdinalIgnoreCase)
        || condition.Equals("Stunned", StringComparison.OrdinalIgnoreCase)
        || condition.Equals("Dead", StringComparison.OrdinalIgnoreCase);

    private static bool EndConcentrationInternal(CampaignState campaign, CharacterSheet character, string reason)
    {
        if (string.IsNullOrWhiteSpace(character.ConcentrationEffect)) return false;
        var effect = character.ConcentrationEffect;
        RemoveConcentrationBoundEffects(campaign, character.Id, effect, reason);
        character.ConcentrationEffect = null;
        if (effect.StartsWith("Readied spell:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var encounter in campaign.Encounters)
            {
                var combatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase));
                if (combatant?.ReadiedAction?.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase) == true)
                    combatant.ReadiedAction = null;
            }
            Log(campaign, "ready_spell_dissipated", $"{character.Name}'s held readied spell dissipated when Concentration ended.", dmOnly: true);
        }
        Touch(campaign);
        Log(campaign, "concentration_ended", $"{character.Name}'s Concentration on {effect} ended ({reason}).");
        return true;
    }

    private static void MarkDead(CharacterSheet character)
    {
        character.Dead = true;
        character.Stable = false;
        character.CurrentHp = 0;
        character.ConcentrationEffect = null;
        AddConditionInternal(character, "Dead");
        RemoveConditionInternal(character, "Unconscious");
    }

    private static void Touch(CampaignState campaign) => campaign.UpdatedAt = DateTimeOffset.UtcNow;

    private static void Log(CampaignState campaign, string type, string summary, bool dmOnly = false) =>
        campaign.Events.Add(new CampaignEvent { Type = type, Summary = summary, DmOnly = dmOnly });
}
