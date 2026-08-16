from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str):
    text = path.read_text(encoding='utf-8')
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'{path}: anchor not found for {label}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')

# 1. Readied spell release uses the same authoritative spell roll pipeline, but marks
# the request so off-turn Reaction resolution does not trip normal current-turn guards.
spellcasting = Path('windows/src/DungeonMasterAI.Engine/Spellcasting.cs')
old = '''            case "attack":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target for its spell attack.");
                (spellAttack, damage, effectSummary) = ResolveSpellAttack(campaign, caster, target, spell, upcastLevels, dice, encounter);
                break;
            case "save":
                if (target is null) throw new InvalidOperationException($"{spell.Name} requires a target for its saving throw.");
                (savingThrow, damage, effectSummary) = ResolveSaveSpell(campaign, caster, target, spell, upcastLevels, dice, encounter);
                break;
'''
new = '''            case "attack":
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
'''
replace_once(spellcasting, old, new, 'readied spell attack/save dispatch')

# 2. Normal spell-attack continuations are reusable by a readied Reaction off turn.
spell_rolls = Path('windows/src/DungeonMasterAI.Engine/GameEngine.SpellPlayerRolls.cs')
text = spell_rolls.read_text(encoding='utf-8')
text = text.replace(
    '  EnsureCurrentTurn(encounter, casterCombatant.Id);',
    '  if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn(encounter, casterCombatant.Id);')
anchor = '''        campaign.PendingPlayerRoll = damagePending;
'''
insert = '''        if (IsReadiedSpellPending(pending))
            damagePending.Context["readied_reaction"] = "true";
        campaign.PendingPlayerRoll = damagePending;
'''
if 'damagePending.Context["readied_reaction"] = "true"' not in text:
    if anchor not in text:
        raise SystemExit('SpellPlayerRolls: damage pending anchor not found')
    text = text.replace(anchor, insert, 1)
spell_rolls.write_text(text, encoding='utf-8')

# 3. Player saving throws and their follow-up caster damage are also legal off turn.
save_rolls = Path('windows/src/DungeonMasterAI.Engine/GameEngine.SpellSavePlayerRolls.cs')
text = save_rolls.read_text(encoding='utf-8')
text = text.replace(
    '            EnsureCurrentTurn(encounter, casterCombatant.Id);',
    '            if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn(encounter, casterCombatant.Id);')
anchor = '''                var slotTextPending = spell.Level == 0
'''
insert = '''                if (IsReadiedSpellPending(pending))
                    damagePending.Context["readied_reaction"] = "true";
                var slotTextPending = spell.Level == 0
'''
if 'damagePending.Context["readied_reaction"] = "true"' not in text:
    if anchor not in text:
        raise SystemExit('SpellSavePlayerRolls: follow-up damage anchor not found')
    text = text.replace(anchor, insert, 1)
save_rolls.write_text(text, encoding='utf-8')

save_damage = Path('windows/src/DungeonMasterAI.Engine/GameEngine.SpellSaveDamageRolls.cs')
text = save_damage.read_text(encoding='utf-8')
text = text.replace(
    '            EnsureCurrentTurn(encounter, casterCombatant.Id);',
    '            if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn(encounter, casterCombatant.Id);')
save_damage.write_text(text, encoding='utf-8')

# 4. First-class decision resolver supports the new readied-spell Reaction and passes
# the app DiceService so any NPC save caused by accepting the trigger is deterministic.
decisions = Path('windows/src/DungeonMasterAI.Engine/GameEngine.PlayerDecisions.cs')
text = decisions.read_text(encoding='utf-8')
old_sig = '''        string decisionId,
        string optionId)
'''
new_sig = '''        string decisionId,
        string optionId,
        DiceService? dice = null)
'''
if new_sig not in text:
    if old_sig not in text:
        raise SystemExit('PlayerDecisions: method signature anchor not found')
    text = text.replace(old_sig, new_sig, 1)
anchor = '''            "readied_attack_reaction" => ResolveReadiedAttackDecision(campaign, decision, option),
'''
insert = '''            "readied_attack_reaction" => ResolveReadiedAttackDecision(campaign, decision, option),
            "readied_spell_reaction" => ResolveReadiedSpellDecision(campaign, decision, option, dice ?? new DiceService()),
'''
if '"readied_spell_reaction" => ResolveReadiedSpellDecision' not in text:
    if anchor not in text:
        raise SystemExit('PlayerDecisions: readied attack switch anchor not found')
    text = text.replace(anchor, insert, 1)
decisions.write_text(text, encoding='utf-8')

readied_decisions = Path('windows/src/DungeonMasterAI.Engine/GameEngine.ReadiedSpellDecisions.cs')
text = readied_decisions.read_text(encoding='utf-8')
old = '''        PendingPlayerDecision decision,
        PlayerDecisionOption option)
'''
new = '''        PendingPlayerDecision decision,
        PlayerDecisionOption option,
        DiceService dice)
'''
if new not in text:
    if old not in text:
        raise SystemExit('ReadiedSpellDecisions: resolver signature anchor not found')
    text = text.replace(old, new, 1)
text = text.replace('            new DiceService(),\n', '            dice,\n', 1)
readied_decisions.write_text(text, encoding='utf-8')

app_decisions = Path('windows/src/DungeonMasterAI.App/MainViewModel.PlayerDecisions.cs')
text = app_decisions.read_text(encoding='utf-8')
old = '            var result = _engine.ResolvePendingPlayerDecision(SelectedCampaign, pending.Id, option.Id);\n'
new = '            var result = _engine.ResolvePendingPlayerDecision(SelectedCampaign, pending.Id, option.Id, _dice);\n'
if new not in text:
    if old not in text:
        raise SystemExit('MainViewModel.PlayerDecisions: decision resolver anchor not found')
    text = text.replace(old, new, 1)
app_decisions.write_text(text, encoding='utf-8')

# 5. The DM can confirm the trigger but cannot choose a PC's readied-spell Reaction.
router = Path('windows/src/DungeonMasterAI.Engine/DmToolRouter.cs')
text = router.read_text(encoding='utf-8')
old = '''        Tool("trigger_readied_spell", "Release a previously readied spell immediately after the DM confirms its trigger. This spends the creature's Reaction; choose the target at release time when the spell requires one.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false))),
'''
new = '''        Tool("trigger_readied_spell", "Confirm that a readied spell trigger occurred and identify its release target when needed. NPC reactions release automatically. A player character receives an explicit Reaction choice, and any player-owned attack, save, or damage dice are requested before resolution continues.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false))),
'''
if old in text:
    text = text.replace(old, new, 1)
old = '''                "trigger_readied_spell" => engine.TriggerReadiedSpell(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), dice, OptionalString(a, "target_combatant_id")),
'''
new = '''                "trigger_readied_spell" => ResolveReadiedSpellTriggerTool(engine, dice, campaign, a),
'''
if new not in text:
    if old not in text:
        raise SystemExit('DmToolRouter: trigger_readied_spell switch anchor not found')
    text = text.replace(old, new, 1)
router.write_text(text, encoding='utf-8')

# Verification markers.
checks = {
    spellcasting: ['pending.Context["readied_reaction"] = "true"', 'MarkReadiedSpellPending(campaign)'],
    spell_rolls: ['if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn', 'damagePending.Context["readied_reaction"] = "true"'],
    save_rolls: ['if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn', 'damagePending.Context["readied_reaction"] = "true"'],
    save_damage: ['if (!IsReadiedSpellPending(pending)) EnsureCurrentTurn'],
    decisions: ['"readied_spell_reaction" => ResolveReadiedSpellDecision'],
    app_decisions: ['option.Id, _dice'],
    router: ['"trigger_readied_spell" => ResolveReadiedSpellTriggerTool'],
}
for path, markers in checks.items():
    current = path.read_text(encoding='utf-8')
    missing = [m for m in markers if m not in current]
    if missing:
        raise SystemExit(f'{path}: missing expected markers {missing}')
