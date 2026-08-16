from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str):
    text = path.read_text(encoding='utf-8')
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'{path}: anchor not found for {label}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')

# Add readied movement decision resolution.
decisions = Path('windows/src/DungeonMasterAI.Engine/GameEngine.PlayerDecisions.cs')
old = '''            "readied_attack_reaction" => ResolveReadiedAttackDecision(campaign, decision, option),
            "readied_spell_reaction" => ResolveReadiedSpellDecision(campaign, decision, option, dice ?? new DiceService()),
'''
new = '''            "readied_attack_reaction" => ResolveReadiedAttackDecision(campaign, decision, option),
            "readied_move_reaction" => ResolveReadiedMoveDecision(campaign, decision, option),
            "readied_spell_reaction" => ResolveReadiedSpellDecision(campaign, decision, option, dice ?? new DiceService()),
'''
replace_once(decisions, old, new, 'readied movement player decision switch')

# The DM can confirm the trigger and propose a destination, but cannot spend a PC Reaction.
router = Path('windows/src/DungeonMasterAI.Engine/DmToolRouter.cs')
old = '''        Tool("trigger_readied_move", "Resolve previously readied movement immediately after the DM confirms its trigger. Destination is chosen at trigger time; movement is limited by Speed and can provoke Opportunity Attacks.", Props(("encounter_id","string",true),("combatant_id","string",true),("grid_x","integer",true),("grid_y","integer",true))),
'''
new = '''        Tool("trigger_readied_move", "Confirm that a readied-movement trigger occurred and propose a legal destination. NPC movement resolves automatically. A player character receives an explicit Reaction choice before any movement is committed; Opportunity Attacks and battlefield hazards still resolve through the authoritative engine.", Props(("encounter_id","string",true),("combatant_id","string",true),("grid_x","integer",true),("grid_y","integer",true))),
'''
replace_once(router, old, new, 'readied movement tool description')
old = '''                "trigger_readied_move" => engine.TriggerReadiedMove(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredInt(a, "grid_x"), RequiredInt(a, "grid_y")),
'''
new = '''                "trigger_readied_move" => ResolveReadiedMoveTriggerTool(engine, campaign, a),
'''
replace_once(router, old, new, 'readied movement router switch')

# Prevent direct engine callers from bypassing an already-present player decision.
ready = Path('windows/src/DungeonMasterAI.Engine/StealthReady.cs')
old = '''    {
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve the pending movement reaction window before triggering readied movement.");
'''
new = '''    {
        if (campaign.PendingPlayerDecision?.Required == true)
            throw new InvalidOperationException($"Resolve the required player decision first: {campaign.PendingPlayerDecision.Prompt}");
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve the pending movement reaction window before triggering readied movement.");
'''
# This method-shaped anchor occurs once in TriggerReadiedMove in the current file.
idx = ready.read_text(encoding='utf-8').find('    public CombatMoveResult TriggerReadiedMove(')
if idx < 0:
    raise SystemExit('StealthReady.cs: TriggerReadiedMove not found')
text = ready.read_text(encoding='utf-8')
segment = text[idx:]
if 'Resolve the required player decision first:' not in segment.split('    private CombatMoveResult CommitReadiedCombatMove', 1)[0]:
    if old not in segment:
        raise SystemExit('StealthReady.cs: TriggerReadiedMove guard anchor not found')
    segment = segment.replace(old, new, 1)
    text = text[:idx] + segment
    ready.write_text(text, encoding='utf-8')

checks = {
    decisions: ['"readied_move_reaction" => ResolveReadiedMoveDecision'],
    router: ['"trigger_readied_move" => ResolveReadiedMoveTriggerTool'],
    ready: ['Resolve the required player decision first: {campaign.PendingPlayerDecision.Prompt}'],
}
for path, markers in checks.items():
    current = path.read_text(encoding='utf-8')
    missing = [marker for marker in markers if marker not in current]
    if missing:
        raise SystemExit(f'{path}: missing expected markers {missing}')
