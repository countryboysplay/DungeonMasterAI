from pathlib import Path

path = Path('windows/src/DungeonMasterAI.App/MainViewModel.cs')
text = path.read_text(encoding='utf-8')


def replace_method(source: str, signature: str, replacement: str) -> str:
    start = source.find(signature)
    if start < 0:
        raise SystemExit(f'Method signature not found: {signature}')
    brace = source.find('{', start)
    if brace < 0:
        raise SystemExit(f'Opening brace not found: {signature}')
    depth = 0
    in_string = False
    verbatim = False
    escape = False
    i = brace
    while i < len(source):
        ch = source[i]
        nxt = source[i + 1] if i + 1 < len(source) else ''
        if in_string:
            if verbatim:
                if ch == '"' and nxt == '"':
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif ch == '\\':
                    escape = True
                elif ch == '"':
                    in_string = False
            i += 1
            continue
        if ch == '@' and nxt == '"':
            in_string = True
            verbatim = True
            i += 2
            continue
        if ch == '"':
            in_string = True
            i += 1
            continue
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                return source[:start] + replacement.rstrip() + source[i + 1:]
        i += 1
    raise SystemExit(f'Closing brace not found: {signature}')


text = replace_method(text, '    private async Task RollInitiativeAsync()', '''    private async Task RollInitiativeAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null) return;
        try
        {
            var sequence = _engine.BeginInitiativeSequence(SelectedCampaign, SelectedEncounter.Id, _dice);
            if (sequence.PendingRoll?.Required == true)
            {
                await PresentPendingGameTableRollAsync(sequence.PendingRoll);
                return;
            }

            StatusMessage = sequence.Summary;
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task TakeHideAsync()', '''    private async Task TakeHideAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            var combatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedAttacker.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected combatant is no longer in the encounter.");
            var actor = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected combatant character no longer exists.");
            if (IsPlayerCharacter(actor))
            {
                var pending = _engine.RequestHideRoll(SelectedCampaign, SelectedEncounter.Id, combatant.Id);
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = _engine.TakeHide(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice);
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task SearchHiddenAsync()', '''    private async Task SearchHiddenAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var searcherCombatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedAttacker.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected searcher is no longer in the encounter.");
            var searcher = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(searcherCombatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected searcher character no longer exists.");
            if (IsPlayerCharacter(searcher))
            {
                var pending = _engine.RequestHiddenSearchRoll(SelectedCampaign, SelectedEncounter.Id, searcherCombatant.Id, SelectedTarget.CombatantId);
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = _engine.SearchForHiddenCombatant(SelectedCampaign, SelectedEncounter.Id, searcherCombatant.Id, SelectedTarget.CombatantId, _dice);
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task FirstAidAsync()', '''    private async Task FirstAidAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var helperCombatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedAttacker.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected helper is no longer in the encounter.");
            var helper = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(helperCombatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected helper character no longer exists.");
            if (IsPlayerCharacter(helper))
            {
                var pending = _engine.RequestFirstAidRoll(SelectedCampaign, SelectedEncounter.Id, helperCombatant.Id, SelectedTarget.CombatantId);
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = _engine.TakeFirstAid(SelectedCampaign, SelectedEncounter.Id, helperCombatant.Id, SelectedTarget.CombatantId, _dice);
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task CombatSkillActionAsync(string action)', '''    private async Task CombatSkillActionAsync(string action)
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null) return;
        try
        {
            if (!int.TryParse(CombatDcInput, out var dc) || dc < 1)
                throw new InvalidOperationException("The action DC must be a positive whole number.");

            var combatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedAttacker.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected combatant is no longer in the encounter.");
            var actor = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(combatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected combatant character no longer exists.");

            if (IsPlayerCharacter(actor))
            {
                var pending = action switch
                {
                    "search" => _engine.RequestSearchActionRoll(SelectedCampaign, SelectedEncounter.Id, combatant.Id, CombatSkillInput, dc),
                    "study" => _engine.RequestStudyActionRoll(SelectedCampaign, SelectedEncounter.Id, combatant.Id, CombatSkillInput, dc),
                    "influence" => _engine.RequestInfluenceActionRoll(SelectedCampaign, SelectedEncounter.Id, combatant.Id, CombatSkillInput, dc),
                    _ => throw new InvalidOperationException("Unknown combat skill action.")
                };
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = action switch
            {
                "search" => _engine.TakeSearchAction(SelectedCampaign, SelectedEncounter.Id, combatant.Id, CombatSkillInput, dc, _dice),
                "study" => _engine.TakeStudyAction(SelectedCampaign, SelectedEncounter.Id, combatant.Id, CombatSkillInput, dc, _dice),
                "influence" => _engine.TakeInfluenceAction(SelectedCampaign, SelectedEncounter.Id, combatant.Id, CombatSkillInput, dc, _dice),
                _ => throw new InvalidOperationException("Unknown combat skill action.")
            };
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task GrappleAsync()', '''    private async Task GrappleAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var targetCombatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedTarget.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected grapple target is no longer in the encounter.");
            var target = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(targetCombatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected grapple target character no longer exists.");
            if (IsPlayerCharacter(target))
            {
                var pending = _engine.RequestUnarmedGrappleSaveRoll(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, targetCombatant.Id);
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = _engine.ResolveUnarmedGrapple(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, targetCombatant.Id, _dice);
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task ShoveAsync(string effect)', '''    private async Task ShoveAsync(string effect)
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var targetCombatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedTarget.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected shove target is no longer in the encounter.");
            var target = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(targetCombatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected shove target character no longer exists.");
            if (IsPlayerCharacter(target))
            {
                var pending = _engine.RequestUnarmedShoveSaveRoll(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, targetCombatant.Id, effect);
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = _engine.ResolveUnarmedShove(SelectedCampaign, SelectedEncounter.Id, SelectedAttacker.CombatantId, targetCombatant.Id, effect, _dice);
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

text = replace_method(text, '    private async Task EscapeGrappleAsync()', '''    private async Task EscapeGrappleAsync()
    {
        if (SelectedCampaign is null || SelectedEncounter is null || SelectedAttacker is null || SelectedTarget is null) return;
        try
        {
            var actorCombatant = SelectedEncounter.Combatants.FirstOrDefault(c => c.Id.Equals(SelectedAttacker.CombatantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected escaping combatant is no longer in the encounter.");
            var actor = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(actorCombatant.CharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Selected escaping character no longer exists.");
            if (IsPlayerCharacter(actor))
            {
                var pending = _engine.RequestEscapeGrappleRoll(SelectedCampaign, SelectedEncounter.Id, actorCombatant.Id, SelectedTarget.CombatantId, "athletics");
                await PresentPendingGameTableRollAsync(pending);
                return;
            }

            var result = _engine.EscapeGrapple(SelectedCampaign, SelectedEncounter.Id, actorCombatant.Id, SelectedTarget.CombatantId, "athletics", _dice);
            await PresentCompletedGameTableActionAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }''')

path.write_text(text, encoding='utf-8')

required = [
    'BeginInitiativeSequence(SelectedCampaign',
    'RequestHideRoll(SelectedCampaign',
    'RequestHiddenSearchRoll(SelectedCampaign',
    'RequestFirstAidRoll(SelectedCampaign',
    'RequestSearchActionRoll(SelectedCampaign',
    'RequestStudyActionRoll(SelectedCampaign',
    'RequestInfluenceActionRoll(SelectedCampaign',
    'RequestUnarmedGrappleSaveRoll(SelectedCampaign',
    'RequestUnarmedShoveSaveRoll(SelectedCampaign',
    'RequestEscapeGrappleRoll(SelectedCampaign',
]
missing = [item for item in required if item not in text]
if missing:
    raise SystemExit(f'Missing expected player-input routing markers: {missing}')
