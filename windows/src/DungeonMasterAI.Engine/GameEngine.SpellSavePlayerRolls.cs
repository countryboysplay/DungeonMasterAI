using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    private PendingRollRequest RequestPlayerSpellSavingThrowRoll(
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
        if (string.IsNullOrWhiteSpace(spell.SaveAbility))
            throw new InvalidOperationException($"{spell.Name} is configured as a saving-throw spell but has no save ability.");
        if (!target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a player character can create a player-controlled spell saving throw request.");

        var dc = SpellSaveDc(caster);
        var ability = CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
            throw new InvalidOperationException($"{target.Name}'s current condition causes this {ability} saving throw to fail automatically, so no player roll is made.");

        var targetCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var dodgeMode = ability == "dexterity" && targetCombatant is not null && IsDodgeActive(campaign, targetCombatant, target)
            ? D20RollMode.Advantage
            : D20RollMode.Normal;
        var conditionMode = CharacterMechanics.SavingThrowModeFromConditions(target, ability);
        var typeMode = !string.IsNullOrWhiteSpace(spell.SaveDisadvantageCreatureType)
            && target.CreatureType.Equals(spell.SaveDisadvantageCreatureType, StringComparison.OrdinalIgnoreCase)
            ? D20RollMode.Disadvantage
            : D20RollMode.Normal;
        var saveMode = CombineAdvantage(CombineAdvantage(dodgeMode, conditionMode), typeMode);
        var proficient = target.SavingThrowProficiencies.Any(x =>
            x.Equals(ability, StringComparison.OrdinalIgnoreCase)
            || x.Equals(ability[..3], StringComparison.OrdinalIgnoreCase));
        var coverBonus = ability == "dexterity" && !spell.IgnoreHalfAndThreeQuartersCoverOnSave
            ? GetSpellCoverBonus(encounter, caster.Id, target.Id)
            : 0;
        var abilityModifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(target, ability));
        var proficiencyModifier = proficient ? Math.Max(0, target.ProficiencyBonus) : 0;
        var exhaustionPenalty = 2 * Math.Clamp(target.ExhaustionLevel, 0, 6);
        var staticModifier = abilityModifier + proficiencyModifier + coverBonus - exhaustionPenalty;
        var modeText = saveMode == D20RollMode.Normal ? "" : $" with {saveMode}";
        var coverText = coverBonus > 0 ? $" including +{coverBonus} from cover" : "";

        var pending = new PendingRollRequest
        {
            ActorCharacterId = target.Id,
            EncounterId = encounter?.Id,
            CombatantId = targetCombatant?.Id,
            Formula = "1d20",
            RollType = "d20",
            RollMode = saveMode.ToString().ToLowerInvariant(),
            Purpose = $"{target.Name} must make a {ability} saving throw{modeText} against {caster.Name}'s {spell.Name}, DC {dc}{coverText}.",
            ResolutionKey = "spell_saving_throw",
            Modifier = staticModifier,
            TargetNumber = dc,
            TargetLabel = $"DC {dc}",
            Required = true,
            Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["spell_id"] = spell.Id,
                ["caster_id"] = caster.Id,
                ["target_id"] = target.Id,
                ["cast_at_level"] = castAtLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["upcast_levels"] = Math.Max(0, castAtLevel - spell.Level).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["used_slot"] = usedSlot ? "true" : "false",
                ["ritual"] = ritual ? "true" : "false",
                ["concentration_started"] = concentrationStarted ? "true" : "false",
                ["save_ability"] = ability,
                ["proficient"] = proficient ? "true" : "false",
                ["cover_bonus"] = coverBonus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["dodge_advantage"] = dodgeMode == D20RollMode.Advantage ? "true" : "false",
                ["condition_disadvantage"] = conditionMode == D20RollMode.Disadvantage ? "true" : "false"
            }
        };

        campaign.PendingPlayerRoll = pending;
        Touch(campaign);
        Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
        return pending;
    }

    public SpellCastResult ResolvePendingSpellSavingThrowRoll(
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
        if (!pending.ResolutionKey.Equals("spell_saving_throw", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not a spell saving throw.");
        if (!pending.Context.TryGetValue("caster_id", out var casterId) || string.IsNullOrWhiteSpace(casterId))
            throw new InvalidOperationException("The pending spell saving throw is missing its caster context.");
        if (!pending.Context.TryGetValue("target_id", out var targetId) || string.IsNullOrWhiteSpace(targetId))
            throw new InvalidOperationException("The pending spell saving throw is missing its target context.");
        if (!pending.Context.TryGetValue("spell_id", out var spellId) || string.IsNullOrWhiteSpace(spellId))
            throw new InvalidOperationException("The pending spell saving throw is missing its spell context.");

        var caster = RequireCharacter(campaign, casterId);
        var target = RequireCharacter(campaign, targetId);
        if (!target.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending spell saving throw actor no longer matches its target.");
        if (!target.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending spell saving throw no longer belongs to a player character.");
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");
        var spell = campaign.Spells.FirstOrDefault(s => s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The spell for the pending saving throw no longer exists.");
        if (string.IsNullOrWhiteSpace(spell.SaveAbility))
            throw new InvalidOperationException($"{spell.Name} no longer has a configured saving throw ability.");

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
        ValidateSpellTargetType(target, spell);
        ValidateSpellRange(campaign, encounter, caster, target, spell);

        var ability = pending.Context.TryGetValue("save_ability", out var storedAbility) && !string.IsNullOrWhiteSpace(storedAbility)
            ? CharacterMechanics.NormalizeAbility(storedAbility)
            : CharacterMechanics.NormalizeAbility(spell.SaveAbility);
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(target, ability))
            throw new InvalidOperationException($"{target.Name}'s condition now causes the {ability} saving throw to fail automatically. Re-resolve the spell from the authoritative state.");
        var mode = ParsePendingRollMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This saving throw requires two d20 results because it has {mode}.");
        var proficient = pending.Context.TryGetValue("proficient", out var proficientText)
            && bool.TryParse(proficientText, out var parsedProficient)
            && parsedProficient;
        var coverBonus = pending.Context.TryGetValue("cover_bonus", out var coverText)
            && int.TryParse(coverText, out var parsedCover) ? parsedCover : 0;
        var effectSaveBonus = RollActiveSavingThrowBonus(campaign, target.Id, dice);
        var dc = pending.TargetNumber ?? SpellSaveDc(caster);
        var save = CharacterMechanics.ResolveD20Test(
            target,
            ability,
            dc,
            rollOne,
            rollTwo,
            mode,
            proficient,
            coverBonus + effectSaveBonus);

        var castAtLevel = PendingSpellSaveContextInt(pending, "cast_at_level", spell.Level);
        var upcastLevels = PendingSpellSaveContextInt(pending, "upcast_levels", Math.Max(0, castAtLevel - spell.Level));
        var usedSlot = PendingSpellSaveContextBool(pending, "used_slot");
        var ritual = PendingSpellSaveContextBool(pending, "ritual");
        var concentrationStarted = PendingSpellSaveContextBool(pending, "concentration_started");

        // The target player's saving throw is now authoritative and complete. If the caster is
        // also a player character, the caster owns any damage dice that follow the save.
        if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            campaign.PendingPlayerRoll = null;
            if (PlayerSaveSpellNeedsDamageRoll(spell, save))
            {
                var damagePending = CreatePendingSaveSpellDamageRequest(
                    campaign,
                    caster,
                    target,
                    spell,
                    save,
                    castAtLevel,
                    upcastLevels,
                    usedSlot,
                    ritual,
                    concentrationStarted,
                    encounter);
                var slotTextPending = spell.Level == 0
                    ? "as a cantrip"
                    : ritual
                        ? "as a Ritual without expending a spell slot"
                        : $"using a level {castAtLevel} spell slot";
                var pendingSummary = $"{caster.Name} cast {spell.Name} {slotTextPending}. {damagePending.Purpose}".Trim();
                Touch(campaign);
                Log(campaign, "spell_cast_pending_damage", pendingSummary, dmOnly: true);
                return new SpellCastResult(
                    spell.Id,
                    spell.Name,
                    caster.Id,
                    target.Id,
                    castAtLevel,
                    usedSlot,
                    ritual,
                    null,
                    save,
                    null,
                    0,
                    concentrationStarted,
                    pendingSummary);
            }

            if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave))
                ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, dc);
            var noDamageText = BuildSaveSpellNoDamageSummary(target, spell, save, ability);
            var slotTextNoDamage = spell.Level == 0
                ? "as a cantrip"
                : ritual
                    ? "as a Ritual without expending a spell slot"
                    : $"using a level {castAtLevel} spell slot";
            var noDamageSummary = $"{caster.Name} cast {spell.Name} {slotTextNoDamage}. {noDamageText}".Trim();
            Touch(campaign);
            Log(campaign, "spell_cast", noDamageSummary);
            return new SpellCastResult(
                spell.Id,
                spell.Name,
                caster.Id,
                target.Id,
                castAtLevel,
                usedSlot,
                ritual,
                null,
                save,
                null,
                0,
                concentrationStarted,
                noDamageSummary);
        }

        var rolledDamage = 0;
        if (!string.IsNullOrWhiteSpace(spell.DamageExpression))
        {
            var baseRolls = spell.CantripDamageScaling ? CantripUpgradeMultiplier(caster.Level) : 1;
            for (var i = 0; i < baseRolls; i++) rolledDamage += dice.RollDamage(spell.DamageExpression);
            for (var i = 0; i < upcastLevels; i++) rolledDamage += dice.RollDamage(spell.ExtraDamagePerSlotExpression);
        }
        var appliedDamage = save.Success
            ? spell.HalfDamageOnSuccessfulSave ? rolledDamage / 2 : 0
            : rolledDamage;

        // The player's save is satisfied before damage is committed so damage can hand off
        // directly to another required player roll, such as a Concentration check.
        campaign.PendingPlayerRoll = null;
        DamageResolutionResult? damage = null;
        if (appliedDamage > 0)
            damage = ApplyDamageWithConcentration(campaign, target.Id, appliedDamage, dice, spell.DamageType);
        if (!save.Success && !string.IsNullOrWhiteSpace(spell.ConditionOnFailedSave))
            ApplySpellConditionEffect(campaign, encounter, caster, target, spell, spell.ConditionOnFailedSave.Trim(), ability, dc);

        var dodgeAdvantage = PendingSpellSaveContextBool(pending, "dodge_advantage");
        var conditionDisadvantage = PendingSpellSaveContextBool(pending, "condition_disadvantage");
        var saveCoverText = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} Dexterity save)" : "";
        var dodgeSaveText = dodgeAdvantage && !conditionDisadvantage ? " with Advantage from Dodge" : "";
        var restraintSaveText = conditionDisadvantage && !dodgeAdvantage ? " with Disadvantage from Restrained" : "";
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

        var slotText = spell.Level == 0
            ? "as a cantrip"
            : ritual
                ? "as a Ritual without expending a spell slot"
                : $"using a level {castAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}. {resultText}".Trim();
        Touch(campaign);
        Log(campaign, "spell_cast", summary);
        return new SpellCastResult(
            spell.Id,
            spell.Name,
            caster.Id,
            target.Id,
            castAtLevel,
            usedSlot,
            ritual,
            null,
            save,
            damage,
            0,
            concentrationStarted,
            summary);
    }

    private static int PendingSpellSaveContextInt(PendingRollRequest pending, string key, int fallback = 0)
    {
        return pending.Context.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool PendingSpellSaveContextBool(PendingRollRequest pending, string key)
    {
        return pending.Context.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
    }
}
