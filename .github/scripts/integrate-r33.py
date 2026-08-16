from pathlib import Path

# 1. Single-target save spell dispatch: player casters own damage dice after the target save.
path = Path('windows/src/DungeonMasterAI.Engine/Spellcasting.cs')
text = path.read_text(encoding='utf-8')
old = '''                else
                {
                    (savingThrow, damage, effectSummary) = ResolveSaveSpell(campaign, caster, target, spell, upcastLevels, dice, activeEncounter);
                }
                break;'''
new = '''                else if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
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
                break;'''
if old not in text:
    raise SystemExit('Spellcasting save fallback block not found')
path.write_text(text.replace(old, new, 1), encoding='utf-8')

# 2. When a player target has supplied its save, hand damage dice back to a player caster.
path = Path('windows/src/DungeonMasterAI.Engine/GameEngine.SpellSavePlayerRolls.cs')
text = path.read_text(encoding='utf-8')
marker = '''        var concentrationStarted = PendingSpellSaveContextBool(pending, "concentration_started");

        var rolledDamage = 0;'''
insert = '''        var concentrationStarted = PendingSpellSaveContextBool(pending, "concentration_started");

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

        var rolledDamage = 0;'''
if marker not in text:
    raise SystemExit('SpellSavePlayerRolls damage marker not found')
path.write_text(text.replace(marker, insert, 1), encoding='utf-8')

# 3. Route save-spell damage through the Game Table roll control.
path = Path('windows/src/DungeonMasterAI.App/MainViewModel.PlayerRolls.cs')
text = path.read_text(encoding='utf-8')
marker = '        if (!pending.Formula.Equals("1d20", StringComparison.OrdinalIgnoreCase))'
block = '''        if (pending.ResolutionKey.Equals("spell_save_damage", StringComparison.OrdinalIgnoreCase))
        {
            var damageAmount = RollPendingSaveSpellDamage(pending);
            LastDiceResult = $"{pending.Formula}: {damageAmount}";
            await ResolveActiveSaveSpellDamageFromRollAsync(pending.Id, damageAmount);
            return;
        }

'''
if marker not in text:
    raise SystemExit('PlayerRolls non-d20 marker not found')
text = text.replace(marker, block + marker, 1)

method_marker = '    private async Task ResolveActiveAbilityCheckFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)'
methods = '''    private async Task ResolveActiveSaveSpellDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingSpellSaveDamageRoll(SelectedCampaign, pendingRollId, damageAmount, _dice);
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = CleanSessionNarration(result.Summary)
            });
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private int RollPendingSaveSpellDamage(PendingRollRequest pending)
    {
        var baseExpression = pending.Context.TryGetValue("base_damage_expression", out var storedBase) ? storedBase : "";
        var extraExpression = pending.Context.TryGetValue("extra_damage_expression", out var storedExtra) ? storedExtra : "";
        var baseRolls = pending.Context.TryGetValue("base_rolls", out var baseRollsText) && int.TryParse(baseRollsText, out var parsedBaseRolls) ? parsedBaseRolls : 0;
        var extraRolls = pending.Context.TryGetValue("extra_rolls", out var extraRollsText) && int.TryParse(extraRollsText, out var parsedExtraRolls) ? parsedExtraRolls : 0;
        var total = 0;
        for (var i = 0; i < baseRolls; i++) total += _dice.RollDamage(baseExpression);
        for (var i = 0; i < extraRolls; i++) total += _dice.RollDamage(extraExpression);
        return total;
    }

'''
if method_marker not in text:
    raise SystemExit('PlayerRolls ability resolver marker not found')
path.write_text(text.replace(method_marker, methods + method_marker, 1), encoding='utf-8')

# 4. Keep the local DM's tool contract aligned with the engine.
path = Path('windows/src/DungeonMasterAI.Engine/DmToolRouter.cs')
text = path.read_text(encoding='utf-8')
old = 'A player character targeted by a single-target saving-throw spell receives a required player d20 save unless the rules make that save automatically fail. NPC spell attacks and NPC saving throws still resolve automatically.'
new = 'A player character targeted by a single-target saving-throw spell receives a required player d20 save unless the rules make that save automatically fail. When a player character casts a damaging saving-throw spell, the target save is resolved first and the caster then receives a required player damage roll whenever damage applies. NPC spell attacks, NPC saving throws, and NPC-caster damage dice still resolve automatically.'
if old not in text:
    raise SystemExit('cast_spell description marker not found')
path.write_text(text.replace(old, new, 1), encoding='utf-8')
