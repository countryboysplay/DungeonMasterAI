from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str):
    text = path.read_text(encoding='utf-8')
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'{path}: anchor not found for {label}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')

# 1. Area sequence state can continue legally off turn when released via Ready.
area = Path('windows/src/DungeonMasterAI.Engine/GameEngine.AreaSpellPlayerRolls.cs')
text = area.read_text(encoding='utf-8')
old = '''    public bool ConcentrationStarted { get; set; }
    public string EncounterId { get; set; } = "";
'''
new = '''    public bool ConcentrationStarted { get; set; }
    public bool ReadiedReaction { get; set; }
    public string EncounterId { get; set; } = "";
'''
if 'public bool ReadiedReaction { get; set; }' not in text:
    if old not in text: raise SystemExit('Area sequence state anchor not found')
    text = text.replace(old, new, 1)
old = '''        IReadOnlyList<string> targetCombatantIds,
        DiceService dice)
'''
new = '''        IReadOnlyList<string> targetCombatantIds,
        DiceService dice,
        bool readiedReaction = false)
'''
if 'bool readiedReaction = false)' not in text:
    if old not in text: raise SystemExit('Area sequence begin signature anchor not found')
    text = text.replace(old, new, 1)
old = '''            ConcentrationStarted = concentrationStarted,
            EncounterId = encounter.Id,
'''
new = '''            ConcentrationStarted = concentrationStarted,
            ReadiedReaction = readiedReaction,
            EncounterId = encounter.Id,
'''
if 'ReadiedReaction = readiedReaction' not in text:
    if old not in text: raise SystemExit('Area sequence state init anchor not found')
    text = text.replace(old, new, 1)
old = '''        EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
'''
new = '''        if (!state.ReadiedReaction)
            EnsureCurrentTurn(encounter, casterCombatant.Id);
        return encounter;
'''
if 'if (!state.ReadiedReaction)' not in text:
    if old not in text: raise SystemExit('Area sequence turn guard anchor not found')
    text = text.replace(old, new, 1)
old = '''        var slotText = spell.Level == 0
            ? "as a cantrip"
            : $"using a level {state.CastAtLevel} spell slot";
'''
new = '''        var slotText = spell.Level == 0
            ? "as a cantrip"
            : state.ReadiedReaction
                ? $"from the level {state.CastAtLevel} slot expended when it was readied"
                : $"using a level {state.CastAtLevel} spell slot";
'''
if 'slot expended when it was readied' not in text:
    if old not in text: raise SystemExit('Area final slot summary anchor not found')
    text = text.replace(old, new, 1)
old = '''        var summary = $"{caster.Name} cast {spell.Name} {slotText}, affecting {state.Results.Count} creature{(state.Results.Count == 1 ? "" : "s")}. {string.Join(" ", state.Results.Select(r => r.Summary))}{environmentText}".Trim();
'''
new = '''        var verb = state.ReadiedReaction ? "released readied" : "cast";
        var summary = $"{caster.Name} {verb} {spell.Name} {slotText}, affecting {state.Results.Count} creature{(state.Results.Count == 1 ? "" : "s")}. {string.Join(" ", state.Results.Select(r => r.Summary))}{environmentText}".Trim();
'''
if 'var verb = state.ReadiedReaction ? "released readied" : "cast";' not in text:
    if old not in text: raise SystemExit('Area final summary anchor not found')
    text = text.replace(old, new, 1)
area.write_text(text, encoding='utf-8')

# 2. Allow area_save in Ready and release it through the existing area sequence.
spellcasting = Path('windows/src/DungeonMasterAI.Engine/Spellcasting.cs')
text = spellcasting.read_text(encoding='utf-8')
old = '''        if (resolution is "area_save" or "multi_buff" or "persistent_area")
            throw new InvalidOperationException($"Readying {spell.Name}'s area or multi-target resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");
'''
new = '''        if (resolution is "multi_buff" or "persistent_area")
            throw new InvalidOperationException($"Readying {spell.Name}'s multi-target or persistent-area resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");
'''
replace_once(spellcasting, old, new, 'allow area_save Ready')

# Extend TriggerReadiedSpell with area geometry parameters at the end so existing callers remain source-compatible.
old = '''        DiceService dice,
        string? targetCombatantId = null)
'''
new = '''        DiceService dice,
        string? targetCombatantId = null,
        int? areaCenterX = null,
        int? areaCenterY = null,
        string? areaDirection = null)
'''
# Scope to TriggerReadiedSpell, because other signatures may resemble it.
idx = text.find('    public SpellCastResult TriggerReadiedSpell(')
if idx < 0: raise SystemExit('TriggerReadiedSpell not found')
head, tail = text[:idx], text[idx:]
if 'int? areaCenterX = null' not in tail.split('    private static string ReadiedSpellConcentrationLabel', 1)[0]:
    if old not in tail: raise SystemExit('TriggerReadiedSpell signature anchor not found')
    tail = tail.replace(old, new, 1)
text = head + tail

idx = text.find('    public SpellCastResult TriggerReadiedSpell(')
head, tail = text[:idx], text[idx:]
old = '''            case "projectile_attack":
            case "projectile_auto":
'''
new = '''            case "area_save":
                var areaResult = BeginReadiedAreaSpellSequence(
                    campaign,
                    caster,
                    spell,
                    castAtLevel,
                    readied.UsedSpellSlot,
                    encounter,
                    areaCenterX,
                    areaCenterY,
                    areaDirection,
                    dice);
                effectSummary = areaResult.Summary;
                targetResults = areaResult.TargetResults;
                break;
            case "projectile_attack":
            case "projectile_auto":
'''
if 'BeginReadiedAreaSpellSequence(' not in tail.split('    private static string ReadiedSpellConcentrationLabel', 1)[0]:
    if old not in tail: raise SystemExit('TriggerReadiedSpell area switch anchor not found')
    tail = tail.replace(old, new, 1)
text = head + tail
spellcasting.write_text(text, encoding='utf-8')

# 3. PC Reaction decision freezes the proposed area geometry and tells the player who is affected.
decisions = Path('windows/src/DungeonMasterAI.Engine/GameEngine.ReadiedSpellDecisions.cs')
text = decisions.read_text(encoding='utf-8')
old = '''        string encounterId,
        string reactorCombatantId,
        string? targetCombatantId = null)
'''
new = '''        string encounterId,
        string reactorCombatantId,
        string? targetCombatantId = null,
        int? areaCenterX = null,
        int? areaCenterY = null,
        string? areaDirection = null)
'''
replace_once(decisions, old, new, 'readied spell decision signature')

old = '''        var projectileResolution = resolution is "projectile_attack" or "projectile_auto";
        if ((spell.RequiresTarget || projectileResolution) && target is null)
'''
new = '''        var projectileResolution = resolution is "projectile_attack" or "projectile_auto";
        var areaResolution = resolution == "area_save";
        ReadiedAreaSpellPlan? areaPlan = null;
        if (areaResolution)
            areaPlan = PlanReadiedAreaSpell(campaign, caster, spell, encounter, areaCenterX, areaCenterY, areaDirection);
        if ((spell.RequiresTarget || projectileResolution) && !areaResolution && target is null)
'''
replace_once(decisions, old, new, 'area decision planning')

old = '''            Prompt = target is null
                ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Use the Reaction to release it now?"
                : $"The trigger occurred for {caster.Name}'s readied {spell.Name} at {target.Name}. Use the Reaction to release it now?",
'''
new = '''            Prompt = areaPlan is not null
                ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Proposed area origin: ({areaPlan.PointX}, {areaPlan.PointY}), direction {areaPlan.Direction}; affected: {string.Join(", ", areaPlan.TargetNames)}. Use the Reaction to release it now?"
                : target is null
                    ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Use the Reaction to release it now?"
                    : $"The trigger occurred for {caster.Name}'s readied {spell.Name} at {target.Name}. Use the Reaction to release it now?",
'''
replace_once(decisions, old, new, 'area decision prompt')

old = '''        if (targetCombatant is not null)
            decision.Context["target_combatant_id"] = targetCombatant.Id;
'''
new = '''        if (targetCombatant is not null)
            decision.Context["target_combatant_id"] = targetCombatant.Id;
        if (areaPlan is not null)
        {
            decision.Context["area_center_x"] = areaPlan.PointX.ToString(System.Globalization.CultureInfo.InvariantCulture);
            decision.Context["area_center_y"] = areaPlan.PointY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            decision.Context["area_direction"] = areaPlan.Direction;
        }
'''
replace_once(decisions, old, new, 'store area geometry')

old = '''        decision.Context.TryGetValue("target_combatant_id", out var targetCombatantId);
        var result = TriggerReadiedSpell(
            campaign,
            encounter.Id,
            combatant.Id,
            dice,
            string.IsNullOrWhiteSpace(targetCombatantId) ? null : targetCombatantId);
'''
new = '''        decision.Context.TryGetValue("target_combatant_id", out var targetCombatantId);
        var centerX = DecisionOptionalInt(decision, "area_center_x");
        var centerY = DecisionOptionalInt(decision, "area_center_y");
        decision.Context.TryGetValue("area_direction", out var areaDirection);
        var result = TriggerReadiedSpell(
            campaign,
            encounter.Id,
            combatant.Id,
            dice,
            string.IsNullOrWhiteSpace(targetCombatantId) ? null : targetCombatantId,
            centerX,
            centerY,
            areaDirection);
'''
replace_once(decisions, old, new, 'resolve area geometry')

old = '''    private static bool IsReadiedSpellPending(PendingRollRequest pending)
'''
new = '''    private static int? DecisionOptionalInt(PendingPlayerDecision decision, string key)
    {
        if (!decision.Context.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"The readied spell decision contains an invalid '{key}' value.");
        return value;
    }

    private static bool IsReadiedSpellPending(PendingRollRequest pending)
'''
replace_once(decisions, old, new, 'area context int helper')
decisions.write_text(text, encoding='utf-8')

# 4. DM trigger supplies tactical geometry but cannot spend a PC Reaction.
router_helper = Path('windows/src/DungeonMasterAI.Engine/DmToolRouter.ReadiedSpellDecisions.cs')
text = router_helper.read_text(encoding='utf-8')
old = '''        var targetCombatantId = OptionalString(arguments, "target_combatant_id");
'''
new = '''        var targetCombatantId = OptionalString(arguments, "target_combatant_id");
        int? centerX = arguments.TryGetProperty("center_x", out var centerXElement) && centerXElement.TryGetInt32(out var parsedCenterX) ? parsedCenterX : null;
        int? centerY = arguments.TryGetProperty("center_y", out var centerYElement) && centerYElement.TryGetInt32(out var parsedCenterY) ? parsedCenterY : null;
        var direction = OptionalString(arguments, "direction");
'''
replace_once(router_helper, old, new, 'readied spell tool geometry parse')
old = '''        if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            return engine.RequestReadiedSpellDecision(campaign, encounter.Id, combatant.Id, targetCombatantId);

        return engine.TriggerReadiedSpell(campaign, encounter.Id, combatant.Id, dice, targetCombatantId);
'''
new = '''        if (caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            return engine.RequestReadiedSpellDecision(campaign, encounter.Id, combatant.Id, targetCombatantId, centerX, centerY, direction);

        return engine.TriggerReadiedSpell(campaign, encounter.Id, combatant.Id, dice, targetCombatantId, centerX, centerY, direction);
'''
replace_once(router_helper, old, new, 'readied spell tool geometry routing')
router_helper.write_text(text, encoding='utf-8')

router = Path('windows/src/DungeonMasterAI.Engine/DmToolRouter.cs')
text = router.read_text(encoding='utf-8')
old = '''        Tool("trigger_readied_spell", "Confirm that a readied spell trigger occurred and identify its release target when needed. NPC reactions release automatically. A player character receives an explicit Reaction choice, and any player-owned attack, save, or damage dice are requested before resolution continues.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false))),
'''
new = '''        Tool("trigger_readied_spell", "Confirm that a readied spell trigger occurred and provide its release target or area geometry when needed. For an area spell, center_x/center_y are the proposed point of origin and direction is north/east/south/west for directional shapes. NPC reactions release automatically. A player character receives an explicit Reaction choice that shows the proposed area before any player-owned saves or damage dice resolve.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false),("center_x","integer",false),("center_y","integer",false),("direction","string",false))),
'''
replace_once(router, old, new, 'readied spell tool schema')
router.write_text(text, encoding='utf-8')

# 5. Direct Game Table Trigger is itself the player's acceptance; feed the existing area controls.
vm = Path('windows/src/DungeonMasterAI.App/MainViewModel.cs')
text = vm.read_text(encoding='utf-8')
old = '''            else if (ready.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase))
            {
                var result = _engine.TriggerReadiedSpell(SelectedCampaign, SelectedEncounter.Id, combatant.Id, _dice, SelectedTarget?.CombatantId);
                StatusMessage = result.Summary;
            }
'''
new = '''            else if (ready.Kind.Equals("spell", StringComparison.OrdinalIgnoreCase))
            {
                var readiedSpell = SelectedCampaign.Spells.FirstOrDefault(s => s.Id.Equals(ready.SpellId ?? "", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The readied spell is no longer in the campaign spell catalog.");
                int? centerX = null;
                int? centerY = null;
                if ((readiedSpell.Resolution ?? "").Equals("area_save", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(SpellAreaCenterXInput, out var parsedX) || !int.TryParse(SpellAreaCenterYInput, out var parsedY))
                        throw new InvalidOperationException("Area center X and Y must be whole-number grid coordinates before releasing a readied area spell.");
                    centerX = parsedX;
                    centerY = parsedY;
                }
                var result = _engine.TriggerReadiedSpell(
                    SelectedCampaign,
                    SelectedEncounter.Id,
                    combatant.Id,
                    _dice,
                    SelectedTarget?.CombatantId,
                    centerX,
                    centerY,
                    SpellAreaDirectionInput);
                StatusMessage = result.Summary;
            }
'''
replace_once(vm, old, new, 'Game Table readied area release')
vm.write_text(text, encoding='utf-8')

checks = {
    area: ['public bool ReadiedReaction', 'if (!state.ReadiedReaction)', 'released readied'],
    spellcasting: ['BeginReadiedAreaSpellSequence(', 'int? areaCenterX = null'],
    decisions: ['ReadiedAreaSpellPlan? areaPlan', 'area_center_x', 'DecisionOptionalInt'],
    router_helper: ['centerXElement', 'RequestReadiedSpellDecision(campaign, encounter.Id, combatant.Id, targetCombatantId, centerX, centerY, direction)'],
    router: ['("center_x","integer",false)'],
    vm: ['SpellAreaCenterXInput', 'SpellAreaDirectionInput'],
}
for path, markers in checks.items():
    current = path.read_text(encoding='utf-8')
    missing = [m for m in markers if m not in current]
    if missing: raise SystemExit(f'{path}: missing expected markers {missing}')
