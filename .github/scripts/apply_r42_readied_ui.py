from pathlib import Path

player_rolls = Path('windows/src/DungeonMasterAI.App/MainViewModel.PlayerRolls.cs')
text = player_rolls.read_text(encoding='utf-8')

needle = '''        if (pending.ResolutionKey.Equals("combat_attack_damage", StringComparison.OrdinalIgnoreCase))
'''
insert = '''        if (pending.ResolutionKey.Equals("readied_attack_damage", StringComparison.OrdinalIgnoreCase))
        {
            var baseDamageExpression = pending.Context.TryGetValue("base_damage_expression", out var storedExpression)
                && !string.IsNullOrWhiteSpace(storedExpression)
                ? storedExpression
                : pending.Formula;
            var critical = pending.Context.TryGetValue("critical", out var criticalText)
                && bool.TryParse(criticalText, out var parsedCritical)
                && parsedCritical;
            var damageAmount = _dice.RollDamage(baseDamageExpression, critical);
            LastDiceResult = $"{pending.Formula}: {damageAmount}";
            await ResolveActiveReadiedAttackDamageFromRollAsync(pending.Id, damageAmount);
            return;
        }

'''
if 'ResolutionKey.Equals("readied_attack_damage"' not in text:
    if needle not in text:
        raise SystemExit('combat attack damage dispatch anchor not found')
    text = text.replace(needle, insert + needle, 1)

needle = '''        if (pending.ResolutionKey.Equals("combat_attack", StringComparison.OrdinalIgnoreCase))
'''
insert = '''        if (pending.ResolutionKey.Equals("readied_attack", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveReadiedAttackFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

'''
if 'ResolutionKey.Equals("readied_attack"' not in text:
    if needle not in text:
        raise SystemExit('combat attack dispatch anchor not found')
    text = text.replace(needle, insert + needle, 1)

player_rolls.write_text(text, encoding='utf-8')

vm = Path('windows/src/DungeonMasterAI.App/MainViewModel.cs')
text = vm.read_text(encoding='utf-8')
old = '''            if (ready.Kind.Equals("attack", StringComparison.OrdinalIgnoreCase))
            {
                var result = _engine.TriggerReadiedAttack(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice);
                StatusMessage = result.Summary;
            }
'''
new = '''            if (ready.Kind.Equals("attack", StringComparison.OrdinalIgnoreCase))
            {
                var reactor = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The readied attacker character no longer exists.");
                if (IsPlayerCharacter(reactor))
                {
                    var pending = _engine.RequestReadiedAttackRoll(SelectedCampaign, SelectedEncounter.Id, combatant.Id);
                    await PresentPendingGameTableRollAsync(pending);
                    return;
                }

                var result = _engine.TriggerReadiedAttack(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice);
                StatusMessage = result.Summary;
            }
'''
if 'RequestReadiedAttackRoll(SelectedCampaign' not in text:
    if old not in text:
        raise SystemExit('readied attack Game Table anchor not found')
    text = text.replace(old, new, 1)
vm.write_text(text, encoding='utf-8')

player_decisions = Path('windows/src/DungeonMasterAI.Engine/GameEngine.PlayerDecisions.cs')
text = player_decisions.read_text(encoding='utf-8')
needle = '''            "opportunity_attack_reaction" => ResolveOpportunityAttackDecision(campaign, decision, option),
'''
insert = '''            "readied_attack_reaction" => ResolveReadiedAttackDecision(campaign, decision, option),
'''
if '"readied_attack_reaction" => ResolveReadiedAttackDecision' not in text:
    if needle not in text:
        raise SystemExit('player decision switch anchor not found')
    text = text.replace(needle, needle + insert, 1)

unreachable = '''        return;

        // Any impossible PC windows above have been auto-declined. If no unresolved window remains,
        // finish the move now. Otherwise the remaining windows belong to NPCs and stay available to the DM runtime.
        if (pendingMove.OpportunityAttacks.All(x => x.Resolved))
            FinalizePendingMoveIfReady(campaign, encounter);
'''
if unreachable in text:
    text = text.replace(unreachable, '        return;\n', 1)
player_decisions.write_text(text, encoding='utf-8')

router = Path('windows/src/DungeonMasterAI.Engine/DmToolRouter.cs')
text = router.read_text(encoding='utf-8')
old = '''        Tool("trigger_readied_attack", "Resolve a previously readied attack immediately after the DM confirms its trigger. This spends the readied creature's Reaction and applies normal attack, cover, hidden, damage, and Concentration rules.", Props(("encounter_id","string",true),("combatant_id","string",true))),
'''
new = '''        Tool("trigger_readied_attack", "Confirm that a readied attack trigger occurred. NPC reactions resolve automatically. A player character receives an explicit Reaction choice; accepting it creates required player-owned attack and damage rolls.", Props(("encounter_id","string",true),("combatant_id","string",true))),
'''
if old in text:
    text = text.replace(old, new, 1)
old = '''                "trigger_readied_attack" => engine.TriggerReadiedAttack(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), dice),
'''
new = '''                "trigger_readied_attack" => ResolveReadiedAttackTriggerTool(engine, dice, campaign, a),
'''
if '"trigger_readied_attack" => ResolveReadiedAttackTriggerTool' not in text:
    if old not in text:
        raise SystemExit('DM readied attack trigger switch anchor not found')
    text = text.replace(old, new, 1)
router.write_text(text, encoding='utf-8')

checks = {
    player_rolls: ['readied_attack_damage', 'ResolveActiveReadiedAttackDamageFromRollAsync', 'ResolveActiveReadiedAttackFromRollAsync'],
    vm: ['RequestReadiedAttackRoll(SelectedCampaign'],
    player_decisions: ['"readied_attack_reaction" => ResolveReadiedAttackDecision'],
    router: ['"trigger_readied_attack" => ResolveReadiedAttackTriggerTool']
}
for path, markers in checks.items():
    current = path.read_text(encoding='utf-8')
    missing = [m for m in markers if m not in current]
    if missing:
        raise SystemExit(f'{path}: missing {missing}')
