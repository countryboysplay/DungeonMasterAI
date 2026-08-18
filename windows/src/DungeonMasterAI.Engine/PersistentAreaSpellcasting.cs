using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public SpellCastResult CastPersistentAreaSpell(
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

        var spell = campaign.Spells.FirstOrDefault(s =>
            s.Id.Equals(spellId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(s.Key) && s.Key.Equals(spellId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"Spell '{spellId}' was not found in the campaign spell catalog.");
        if (!IsPrepared(caster, spell))
            throw new InvalidOperationException($"{caster.Name} does not have {spell.Name} prepared.");

        ValidateComponents(caster, spell);
        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();
        ValidateSpellConfiguration(spell, resolution);
        if (resolution != "persistent_area")
            throw new InvalidOperationException($"{spell.Name} is not configured as a persistent battlefield-area spell.");

        var encounter = ResolveCastingEncounter(campaign, encounterId, caster.Id)
            ?? throw new InvalidOperationException($"{spell.Name} requires a tactical encounter so its persistent area can be placed and tracked deterministically.");
        ValidateCastingTurn(encounter, caster, spell);
        ValidateCastingActionEconomy(encounter, caster, spell);
        ValidateReactionSpellAvailability(encounter, caster, spell);

        var casterCombatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{caster.Name} is not a combatant in the active encounter.");
        if (!casterCombatant.Positioned)
            throw new InvalidOperationException($"{caster.Name} must be positioned on the tactical grid before casting {spell.Name}.");

        var castAtLevel = spell.Level == 0 ? 0 : slotLevel ?? FindLowestAvailableSlot(caster, spell.Level);
        if (spell.Level > 0)
        {
            if (castAtLevel < spell.Level || castAtLevel > 9)
                throw new InvalidOperationException($"{spell.Name} requires a level {spell.Level} or higher spell slot.");
            if (!caster.SpellSlots.TryGetValue(castAtLevel, out var pool) || pool.Remaining <= 0)
                throw new InvalidOperationException($"{caster.Name} has no level {castAtLevel} spell slot available.");
            if (encounter.SpellSlotCasterIdsThisTurn.Contains(caster.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{caster.Name} has already expended a spell slot to cast a spell during the current turn.");
        }

        var originKind = (spell.AreaOrigin ?? "point").Trim().ToLowerInvariant();
        int originX;
        int originY;
        if (originKind == "self")
        {
            originX = casterCombatant.GridX;
            originY = casterCombatant.GridY;
        }
        else if (originKind == "point")
        {
            if (!centerX.HasValue || !centerY.HasValue)
                throw new InvalidOperationException($"{spell.Name} requires a tactical point of origin.");
            originX = centerX.Value;
            originY = centerY.Value;
            var rangeFeet = Math.Max(0, spell.RangeFeet);
            if (rangeFeet > 0 && GridDistanceFeet(casterCombatant.GridX, casterCombatant.GridY, originX, originY) > rangeFeet)
                throw new InvalidOperationException($"{spell.Name}'s point of origin is beyond its {rangeFeet}-foot range.");
            if (GetAreaCoverBonus(encounter, casterCombatant.GridX, casterCombatant.GridY, originX, originY) >= 100)
                throw new InvalidOperationException($"{spell.Name}'s point of origin is behind Total Cover or another line-of-effect obstruction.");
        }
        else
        {
            throw new InvalidOperationException($"{spell.Name} has unsupported area origin metadata '{spell.AreaOrigin}'.");
        }

        _ = SpellAreaGeometry.NormalizeDirection(direction);
        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);
        var sizeFeet = checked(spell.AreaSizeFeet + upcastLevels * spell.ExtraAreaSizePerSlotFeet);
        if (sizeFeet <= 0 || sizeFeet % 5 != 0)
            throw new InvalidOperationException($"{spell.Name} resolved to an invalid persistent-area size of {sizeFeet} feet.");
        _ = SpellAreaGeometry.EnumerateCells(spell.AreaShape, sizeFeet, originX, originY, direction, spell.AreaWidthFeet);

        // All geometry, range, action-economy, slot, and metadata validation is complete before state mutates.
        if (spell.Level > 0)
        {
            SpendSpellSlot(campaign, caster.Id, castAtLevel);
            encounter.SpellSlotCasterIdsThisTurn.Add(caster.Id);
        }
        ConsumeCastingActionEconomy(encounter, caster, spell);
        ConsumeReactionForSpell(encounter, caster, spell);
        if (spell.RequiresConcentration)
            BeginConcentration(campaign, caster.Id, spell.Name);

        var effect = AddBattlefieldEffect(campaign, encounter.Id, new BattlefieldEffectState
        {
            Name = spell.Name,
            SourceCharacterId = caster.Id,
            SourceSpellId = spell.Id,
            Shape = spell.AreaShape,
            SizeFeet = sizeFeet,
            WidthFeet = spell.AreaWidthFeet,
            OriginX = originX,
            OriginY = originY,
            Direction = direction,
            Trigger = string.IsNullOrWhiteSpace(spell.BattlefieldTrigger) ? "none" : spell.BattlefieldTrigger,
            DamageExpression = spell.DamageExpression,
            DamageType = spell.DamageType,
            SaveAbility = spell.SaveAbility,
            SaveDc = string.IsNullOrWhiteSpace(spell.SaveAbility) ? 0 : SpellSaveDc(caster),
            HalfDamageOnSuccessfulSave = spell.HalfDamageOnSuccessfulSave,
            OncePerTurn = !spell.BattlefieldTrigger.Equals("move_within", StringComparison.OrdinalIgnoreCase),
            DifficultTerrain = spell.BattlefieldDifficultTerrain,
            HeavilyObscured = spell.BattlefieldHeavilyObscured,
            BlocksLineOfSight = spell.BattlefieldBlocksLineOfSight,
            RequiresSourceConcentration = spell.RequiresConcentration,
            ConcentrationName = spell.RequiresConcentration ? spell.Name : "",
            DurationRounds = spell.BattlefieldDurationRounds,
            SourceKind = spell.SourceKind
        });

        if (spell.RequiresVerbal)
            BreakHidden(campaign, encounter, casterCombatant, "casting a spell with a Verbal component");

        var slotText = spell.Level == 0 ? "as a cantrip" : $"using a level {castAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}, creating a {sizeFeet}-foot {effect.Shape} battlefield effect centered at ({originX}, {originY}).";
        if (effect.HeavilyObscured) summary += " The area is Heavily Obscured.";
        if (effect.DifficultTerrain) summary += " The area is Difficult Terrain.";
        if (effect.RequiresSourceConcentration) summary += $" It remains bound to {caster.Name}'s Concentration on {spell.Name}.";
        Touch(campaign);
        Log(campaign, "spell_cast", summary);

        return new SpellCastResult(
            spell.Id,
            spell.Name,
            caster.Id,
            null,
            castAtLevel,
            spell.Level > 0,
            false,
            null,
            null,
            null,
            0,
            spell.RequiresConcentration,
            summary,
            []);
    }
}
