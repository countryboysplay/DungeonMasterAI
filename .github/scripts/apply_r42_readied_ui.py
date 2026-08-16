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

checks = {
    player_rolls: ['readied_attack_damage', 'ResolveActiveReadiedAttackDamageFromRollAsync', 'ResolveActiveReadiedAttackFromRollAsync'],
    vm: ['RequestReadiedAttackRoll(SelectedCampaign']
}
for path, markers in checks.items():
    current = path.read_text(encoding='utf-8')
    missing = [m for m in markers if m not in current]
    if missing:
        raise SystemExit(f'{path}: missing {missing}')
