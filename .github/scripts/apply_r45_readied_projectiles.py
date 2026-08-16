from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str):
    text = path.read_text(encoding='utf-8')
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'{path}: anchor not found for {label}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')

# Projectile attack sequence state knows whether current-turn enforcement should be bypassed
# because the spell is being released as a readied Reaction.
projectile = Path('windows/src/DungeonMasterAI.Engine/GameEngine.ProjectilePlayerRolls.cs')
text = projectile.read_text(encoding='utf-8')
old = '''    public string? EncounterId { get; set; }
    public List<string> TargetIds { get; set; } = [];
'''
new = '''    public string? EncounterId { get; set; }
    public bool ReadiedReaction { get; set; }
    public List<string> TargetIds { get; set; } = [];
'''
if 'public bool ReadiedReaction { get; set; }' not in text:
    if old not in text: raise SystemExit('Projectile state anchor not found')
    text = text.replace(old, new, 1)
old = '''        IReadOnlyList<string> allocations,
        DiceService dice)
'''
new = '''        IReadOnlyList<string> allocations,
        DiceService dice,
        bool readiedReaction = false)
'''
if 'bool readiedReaction = false)' not in text:
    if old not in text: raise SystemExit('Projectile begin signature anchor not found')
    text = text.replace(old, new, 1)
old = '''            EncounterId = encounter?.Id,
            TargetIds = allocations.ToList(),
'''
new = '''            EncounterId = encounter?.Id,
            ReadiedReaction = readiedReaction,
            TargetIds = allocations.ToList(),
'''
if 'ReadiedReaction = readiedReaction' not in text:
    if old not in text: raise SystemExit('Projectile state init anchor not found')
    text = text.replace(old, new, 1)
old = '''        EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
'''
new = '''        if (!state.ReadiedReaction)
            EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
'''
if 'if (!state.ReadiedReaction)' not in text:
    if old not in text: raise SystemExit('Projectile turn guard anchor not found')
    text = text.replace(old, new, 1)
old = '''        var slotText = spell.Level == 0 ? "as a cantrip" : $"using a level {state.CastAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}, resolving {state.TargetIds.Count} projectile{(state.TargetIds.Count == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. "
'''
new = '''        var slotText = spell.Level == 0
            ? "as a cantrip"
            : state.ReadiedReaction
                ? $"from the level {state.CastAtLevel} slot expended when it was readied"
                : $"using a level {state.CastAtLevel} spell slot";
        var verb = state.ReadiedReaction ? "released readied" : "cast";
        var summary = $"{caster.Name} {verb} {spell.Name} {slotText}, resolving {state.TargetIds.Count} projectile{(state.TargetIds.Count == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. "
'''
if 'var verb = state.ReadiedReaction ? "released readied" : "cast";' not in text:
    if old not in text: raise SystemExit('Projectile final summary anchor not found')
    text = text.replace(old, new, 1)
projectile.write_text(text, encoding='utf-8')

# Auto-hit projectile sequence gets the same off-turn/readied state behavior.
auto = Path('windows/src/DungeonMasterAI.Engine/GameEngine.AutoProjectilePlayerRolls.cs')
text = auto.read_text(encoding='utf-8')
old = '''    public string? EncounterId { get; set; }
    public List<string> TargetIds { get; set; } = [];
'''
new = '''    public string? EncounterId { get; set; }
    public bool ReadiedReaction { get; set; }
    public List<string> TargetIds { get; set; } = [];
'''
if 'public bool ReadiedReaction { get; set; }' not in text:
    if old not in text: raise SystemExit('Auto projectile state anchor not found')
    text = text.replace(old, new, 1)
old = '''        EncounterState? encounter,
        IReadOnlyList<string> allocations)
'''
new = '''        EncounterState? encounter,
        IReadOnlyList<string> allocations,
        bool readiedReaction = false)
'''
if 'bool readiedReaction = false)' not in text:
    if old not in text: raise SystemExit('Auto projectile begin signature anchor not found')
    text = text.replace(old, new, 1)
old = '''            EncounterId = encounter?.Id,
            TargetIds = allocations.ToList(),
'''
new = '''            EncounterId = encounter?.Id,
            ReadiedReaction = readiedReaction,
            TargetIds = allocations.ToList(),
'''
if 'ReadiedReaction = readiedReaction' not in text:
    if old not in text: raise SystemExit('Auto projectile state init anchor not found')
    text = text.replace(old, new, 1)
old = '''        EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
'''
new = '''        if (!state.ReadiedReaction)
            EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
'''
if 'if (!state.ReadiedReaction)' not in text:
    if old not in text: raise SystemExit('Auto projectile turn guard anchor not found')
    text = text.replace(old, new, 1)
old = '''        var slotText = spell.Level == 0 ? "as a cantrip" : $"using a level {state.CastAtLevel} spell slot";
        var summary = $"{caster.Name} cast {spell.Name} {slotText}, resolving {state.TargetIds.Count} projectile{(state.TargetIds.Count == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. "
'''
new = '''        var slotText = spell.Level == 0
            ? "as a cantrip"
            : state.ReadiedReaction
                ? $"from the level {state.CastAtLevel} slot expended when it was readied"
                : $"using a level {state.CastAtLevel} spell slot";
        var verb = state.ReadiedReaction ? "released readied" : "cast";
        var summary = $"{caster.Name} {verb} {spell.Name} {slotText}, resolving {state.TargetIds.Count} projectile{(state.TargetIds.Count == 1 ? "" : "s")} against {string.Join(", ", distinctTargets)}. "
'''
if 'var verb = state.ReadiedReaction ? "released readied" : "cast";' not in text:
    if old not in text: raise SystemExit('Auto projectile final summary anchor not found')
    text = text.replace(old, new, 1)
auto.write_text(text, encoding='utf-8')

# Ready now accepts projectile modes for PCs, while still refusing the modes whose release
# needs area geometry or multi-target allocation UI that has not been implemented yet.
spellcasting = Path('windows/src/DungeonMasterAI.Engine/Spellcasting.cs')
text = spellcasting.read_text(encoding='utf-8')
old = '''        if (resolution is "projectile_auto" or "projectile_attack" or "area_save" or "multi_buff" or "persistent_area")
            throw new InvalidOperationException($"Readying {spell.Name}'s multi-target or area resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");
'''
new = '''        if (resolution is "area_save" or "multi_buff" or "persistent_area")
            throw new InvalidOperationException($"Readying {spell.Name}'s area or multi-target resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");
        if (resolution is "projectile_auto" or "projectile_attack"
            && !caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Readied projectile spells are currently enabled for player-character casters only; NPC projectile Ready resolution will be generalized in a later engine pass.");
'''
if 'Readied projectile spells are currently enabled for player-character casters only' not in text:
    if old not in text: raise SystemExit('TakeReadySpell restriction anchor not found')
    text = text.replace(old, new, 1)

old = '''        var healing = 0;
        var effectSummary = "";
        switch (resolution)
'''
new = '''        var healing = 0;
        IReadOnlyList<SpellTargetResolution>? targetResults = null;
        var effectSummary = "";
        switch (resolution)
'''
# Replace only in TriggerReadiedSpell section, not CastSpell. Locate after method signature.
trigger_idx = text.find('    public SpellCastResult TriggerReadiedSpell(')
if trigger_idx < 0: raise SystemExit('TriggerReadiedSpell not found')
head, tail = text[:trigger_idx], text[trigger_idx:]
if 'IReadOnlyList<SpellTargetResolution>? targetResults = null;' not in tail.split('    private static string ReadiedSpellConcentrationLabel', 1)[0]:
    if old not in tail: raise SystemExit('TriggerReadiedSpell variable anchor not found')
    tail = tail.replace(old, new, 1)
text = head + tail

trigger_idx = text.find('    public SpellCastResult TriggerReadiedSpell(')
head, tail = text[:trigger_idx], text[trigger_idx:]
old = '''            case "healing":
                target ??= caster;
'''
new = '''            case "projectile_attack":
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
'''
if 'BeginReadiedProjectileSpellSequence(' not in tail.split('    private static string ReadiedSpellConcentrationLabel', 1)[0]:
    if old not in tail: raise SystemExit('TriggerReadiedSpell projectile switch anchor not found')
    tail = tail.replace(old, new, 1)
text = head + tail

trigger_idx = text.find('    public SpellCastResult TriggerReadiedSpell(')
head, tail = text[:trigger_idx], text[trigger_idx:]
old = '''            spell.RequiresConcentration,
            summary);
'''
new = '''            spell.RequiresConcentration,
            summary,
            targetResults);
'''
if 'summary,\n            targetResults);' not in tail.split('    private static string ReadiedSpellConcentrationLabel', 1)[0]:
    if old not in tail: raise SystemExit('TriggerReadiedSpell result anchor not found')
    tail = tail.replace(old, new, 1)
text = head + tail
spellcasting.write_text(text, encoding='utf-8')

# A projectile readied spell always needs a release target even if legacy catalog metadata did
# not set RequiresTarget, because all projectiles are allocated to the chosen release target in r45.
readied_decisions = Path('windows/src/DungeonMasterAI.Engine/GameEngine.ReadiedSpellDecisions.cs')
text = readied_decisions.read_text(encoding='utf-8')
old = '''        if (spell.RequiresTarget && target is null)
            throw new InvalidOperationException($"{spell.Name} requires a target when its trigger is accepted.");
'''
new = '''        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();
        var projectileResolution = resolution is "projectile_attack" or "projectile_auto";
        if ((spell.RequiresTarget || projectileResolution) && target is null)
            throw new InvalidOperationException($"{spell.Name} requires a target when its trigger is accepted.");
'''
if 'var projectileResolution = resolution is "projectile_attack" or "projectile_auto";' not in text:
    if old not in text: raise SystemExit('Readied spell decision target anchor not found')
    text = text.replace(old, new, 1)
readied_decisions.write_text(text, encoding='utf-8')

checks = {
    projectile: ['public bool ReadiedReaction', 'if (!state.ReadiedReaction)', 'released readied'],
    auto: ['public bool ReadiedReaction', 'if (!state.ReadiedReaction)', 'released readied'],
    spellcasting: ['Readied projectile spells are currently enabled', 'BeginReadiedProjectileSpellSequence(', 'targetResults);'],
    readied_decisions: ['projectileResolution = resolution is "projectile_attack" or "projectile_auto"'],
}
for path, markers in checks.items():
    current = path.read_text(encoding='utf-8')
    missing = [m for m in markers if m not in current]
    if missing: raise SystemExit(f'{path}: missing expected markers {missing}')
