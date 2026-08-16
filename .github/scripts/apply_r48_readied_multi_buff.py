from pathlib import Path

ROOT = Path('windows')


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected exactly one match, found {count}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')


spellcasting = ROOT / 'src/DungeonMasterAI.Engine/Spellcasting.cs'
replace_once(
    spellcasting,
    '''        if (resolution is "multi_buff" or "persistent_area")\n            throw new InvalidOperationException($"Readying {spell.Name}'s multi-target or persistent-area resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");\n''',
    '''        if (resolution == "persistent_area")\n            throw new InvalidOperationException($"Readying {spell.Name}'s persistent-area resolution is not implemented yet. Cast it normally; the engine will not partially resolve an unsupported Ready interaction.");\n''',
    'allow multi-buff Ready',
)
replace_once(
    spellcasting,
    '''        int? areaCenterX = null,\n        int? areaCenterY = null,\n        string? areaDirection = null)\n''',
    '''        int? areaCenterX = null,\n        int? areaCenterY = null,\n        string? areaDirection = null,\n        IReadOnlyList<string>? targetCombatantIds = null)\n''',
    'extend TriggerReadiedSpell signature',
)
replace_once(
    spellcasting,
    '''        CharacterSheet? target = null;\n        CombatantState? targetCombatant = null;\n        if (!string.IsNullOrWhiteSpace(targetCombatantId))\n        {\n            targetCombatant = RequireCombatant(encounter, targetCombatantId);\n            target = RequireCharacter(campaign, targetCombatant.CharacterId);\n        }\n        if (spell.RequiresTarget && target is null)\n            throw new InvalidOperationException($"{spell.Name} requires a target when the readied spell is released.");\n        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");\n        ValidateSpellTargetType(target, spell);\n        ValidateSpellRange(campaign, encounter, caster, target, spell);\n\n        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();\n        ValidateSpellConfiguration(spell, resolution);\n        var castAtLevel = spell.Level == 0 ? 0 : Math.Max(spell.Level, readied.CastAtLevel);\n        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);\n''',
    '''        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();\n        ValidateSpellConfiguration(spell, resolution);\n        var castAtLevel = spell.Level == 0 ? 0 : Math.Max(spell.Level, readied.CastAtLevel);\n        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);\n\n        CharacterSheet? target = null;\n        CombatantState? targetCombatant = null;\n        if (!string.IsNullOrWhiteSpace(targetCombatantId))\n        {\n            targetCombatant = RequireCombatant(encounter, targetCombatantId);\n            target = RequireCharacter(campaign, targetCombatant.CharacterId);\n        }\n        var multiBuffResolution = resolution == "multi_buff";\n        IReadOnlyList<string>? effectiveMultiBuffTargetIds = targetCombatantIds is { Count: > 0 }\n            ? targetCombatantIds\n            : targetCombatant is null ? null : [targetCombatant.Id];\n        ReadiedMultiBuffPlan? multiBuffPlan = null;\n        if (multiBuffResolution)\n            multiBuffPlan = PlanReadiedMultiBuffSpell(campaign, caster, spell, encounter, castAtLevel, effectiveMultiBuffTargetIds);\n\n        if (spell.RequiresTarget && target is null && !multiBuffResolution)\n            throw new InvalidOperationException($"{spell.Name} requires a target when the readied spell is released.");\n        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");\n        if (!multiBuffResolution)\n        {\n            ValidateSpellTargetType(target, spell);\n            ValidateSpellRange(campaign, encounter, caster, target, spell);\n        }\n''',
    'validate multi-buff release before Reaction mutation',
)
replace_once(
    spellcasting,
    '''        IReadOnlyList<SpellTargetResolution>? targetResults = null;\n        var effectSummary = "";\n        switch (resolution)\n''',
    '''        IReadOnlyList<SpellTargetResolution>? targetResults = null;\n        string? resolvedTargetId = target?.Id;\n        var effectSummary = "";\n        switch (resolution)\n''',
    'track resolved target id',
)
replace_once(
    spellcasting,
    '''            case "area_save":\n                var areaResult = BeginReadiedAreaSpellSequence(''',
    '''            case "multi_buff":\n                if (multiBuffPlan is null)\n                    throw new InvalidOperationException($"{spell.Name}'s readied multi-target plan was not prepared before release.");\n                var multiBuffResult = ReleaseReadiedMultiBuffSpell(\n                    campaign,\n                    caster,\n                    spell,\n                    castAtLevel,\n                    readied.UsedSpellSlot,\n                    multiBuffPlan);\n                effectSummary = multiBuffResult.Summary;\n                targetResults = multiBuffResult.TargetResults;\n                resolvedTargetId = multiBuffResult.TargetId;\n                break;\n            case "area_save":\n                var areaResult = BeginReadiedAreaSpellSequence(''',
    'add multi-buff release case',
)
replace_once(
    spellcasting,
    '''            caster.Id,\n            target?.Id,\n            castAtLevel,\n            readied.UsedSpellSlot,''',
    '''            caster.Id,\n            resolvedTargetId,\n            castAtLevel,\n            readied.UsedSpellSlot,''',
    'return resolved multi-buff target id',
)

# Player decision planning and context.
decisions = ROOT / 'src/DungeonMasterAI.Engine/GameEngine.ReadiedSpellDecisions.cs'
replace_once(
    decisions,
    '''        int? areaCenterX = null,\n        int? areaCenterY = null,\n        string? areaDirection = null)\n''',
    '''        int? areaCenterX = null,\n        int? areaCenterY = null,\n        string? areaDirection = null,\n        IReadOnlyList<string>? targetCombatantIds = null)\n''',
    'extend RequestReadiedSpellDecision signature',
)
replace_once(
    decisions,
    '''        CharacterSheet? target = null;\n        CombatantState? targetCombatant = null;\n        if (!string.IsNullOrWhiteSpace(targetCombatantId))\n        {\n            targetCombatant = RequireCombatant(encounter, targetCombatantId);\n            target = RequireCharacter(campaign, targetCombatant.CharacterId);\n        }\n        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();\n        var projectileResolution = resolution is "projectile_attack" or "projectile_auto";\n        var areaResolution = resolution == "area_save";\n        ReadiedAreaSpellPlan? areaPlan = null;\n        if (areaResolution)\n            areaPlan = PlanReadiedAreaSpell(campaign, caster, spell, encounter, areaCenterX, areaCenterY, areaDirection);\n        if ((spell.RequiresTarget || projectileResolution) && !areaResolution && target is null)\n            throw new InvalidOperationException($"{spell.Name} requires a target when its trigger is accepted.");\n        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");\n        ValidateSpellTargetType(target, spell);\n        ValidateSpellRange(campaign, encounter, caster, target, spell);\n''',
    '''        CharacterSheet? target = null;\n        CombatantState? targetCombatant = null;\n        if (!string.IsNullOrWhiteSpace(targetCombatantId))\n        {\n            targetCombatant = RequireCombatant(encounter, targetCombatantId);\n            target = RequireCharacter(campaign, targetCombatant.CharacterId);\n        }\n        var resolution = (spell.Resolution ?? "utility").Trim().ToLowerInvariant();\n        var projectileResolution = resolution is "projectile_attack" or "projectile_auto";\n        var areaResolution = resolution == "area_save";\n        var multiBuffResolution = resolution == "multi_buff";\n        ReadiedAreaSpellPlan? areaPlan = null;\n        ReadiedMultiBuffPlan? multiBuffPlan = null;\n        if (areaResolution)\n            areaPlan = PlanReadiedAreaSpell(campaign, caster, spell, encounter, areaCenterX, areaCenterY, areaDirection);\n        if (multiBuffResolution)\n        {\n            IReadOnlyList<string>? effectiveTargets = targetCombatantIds is { Count: > 0 }\n                ? targetCombatantIds\n                : targetCombatant is null ? null : [targetCombatant.Id];\n            var castAtLevel = spell.Level == 0 ? 0 : Math.Max(spell.Level, readied.CastAtLevel);\n            multiBuffPlan = PlanReadiedMultiBuffSpell(campaign, caster, spell, encounter, castAtLevel, effectiveTargets);\n        }\n        if ((spell.RequiresTarget || projectileResolution) && !areaResolution && !multiBuffResolution && target is null)\n            throw new InvalidOperationException($"{spell.Name} requires a target when its trigger is accepted.");\n        if (target is not null && target.Dead && !string.Equals(spell.Resolution, "healing", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"{target.Name} is dead and is not a valid target for this configured spell effect.");\n        if (!multiBuffResolution)\n        {\n            ValidateSpellTargetType(target, spell);\n            ValidateSpellRange(campaign, encounter, caster, target, spell);\n        }\n''',
    'plan multi-buff before PC decision',
)
replace_once(
    decisions,
    '''            Prompt = areaPlan is not null\n                ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Proposed area origin: ({areaPlan.PointX}, {areaPlan.PointY}), direction {areaPlan.Direction}; affected: {string.Join(", ", areaPlan.TargetNames)}. Use the Reaction to release it now?"\n                : target is null\n''',
    '''            Prompt = multiBuffPlan is not null\n                ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Proposed targets: {string.Join(", ", multiBuffPlan.TargetNames)}. Use the Reaction to release it now?"\n                : areaPlan is not null\n                    ? $"The trigger occurred for {caster.Name}'s readied {spell.Name}. Proposed area origin: ({areaPlan.PointX}, {areaPlan.PointY}), direction {areaPlan.Direction}; affected: {string.Join(", ", areaPlan.TargetNames)}. Use the Reaction to release it now?"\n                : target is null\n''',
    'show multi-buff target proposal',
)
replace_once(
    decisions,
    '''        if (areaPlan is not null)\n        {\n            decision.Context["area_center_x"] = areaPlan.PointX.ToString(System.Globalization.CultureInfo.InvariantCulture);\n            decision.Context["area_center_y"] = areaPlan.PointY.ToString(System.Globalization.CultureInfo.InvariantCulture);\n            decision.Context["area_direction"] = areaPlan.Direction;\n        }\n''',
    '''        if (areaPlan is not null)\n        {\n            decision.Context["area_center_x"] = areaPlan.PointX.ToString(System.Globalization.CultureInfo.InvariantCulture);\n            decision.Context["area_center_y"] = areaPlan.PointY.ToString(System.Globalization.CultureInfo.InvariantCulture);\n            decision.Context["area_direction"] = areaPlan.Direction;\n        }\n        if (multiBuffPlan is not null)\n            decision.Context["target_combatant_ids_json"] = System.Text.Json.JsonSerializer.Serialize(multiBuffPlan.TargetCombatantIds);\n''',
    'persist multi-buff target proposal',
)
replace_once(
    decisions,
    '''        decision.Context.TryGetValue("area_direction", out var areaDirection);\n        var result = TriggerReadiedSpell(\n            campaign,\n            encounter.Id,\n            combatant.Id,\n            dice,\n            string.IsNullOrWhiteSpace(targetCombatantId) ? null : targetCombatantId,\n            centerX,\n            centerY,\n            areaDirection);\n''',
    '''        decision.Context.TryGetValue("area_direction", out var areaDirection);\n        var targetCombatantIds = DecisionOptionalStringArrayJson(decision, "target_combatant_ids_json");\n        var result = TriggerReadiedSpell(\n            campaign,\n            encounter.Id,\n            combatant.Id,\n            dice,\n            string.IsNullOrWhiteSpace(targetCombatantId) ? null : targetCombatantId,\n            centerX,\n            centerY,\n            areaDirection,\n            targetCombatantIds);\n''',
    'replay frozen multi-buff target proposal',
)
replace_once(
    decisions,
    '''    private static int? DecisionOptionalInt(PendingPlayerDecision decision, string key)\n''',
    '''    private static IReadOnlyList<string>? DecisionOptionalStringArrayJson(PendingPlayerDecision decision, string key)\n    {\n        if (!decision.Context.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;\n        try\n        {\n            return System.Text.Json.JsonSerializer.Deserialize<string[]>(raw)\n                ?.Where(value => !string.IsNullOrWhiteSpace(value))\n                .Distinct(StringComparer.OrdinalIgnoreCase)\n                .ToArray();\n        }\n        catch (System.Text.Json.JsonException ex)\n        {\n            throw new InvalidOperationException($"The readied spell decision contains invalid '{key}' JSON.", ex);\n        }\n    }\n\n    private static int? DecisionOptionalInt(PendingPlayerDecision decision, string key)\n''',
    'add frozen target-list parser',
)

router = ROOT / 'src/DungeonMasterAI.Engine/DmToolRouter.ReadiedSpellDecisions.cs'
replace_once(
    router,
    '''        var targetCombatantId = OptionalString(arguments, "target_combatant_id");\n        int? centerX =''',
    '''        var targetCombatantId = OptionalString(arguments, "target_combatant_id");\n        IReadOnlyList<string>? targetCombatantIds = null;\n        if (arguments.TryGetProperty("target_combatant_ids", out var targetIdsElement) && targetIdsElement.ValueKind == JsonValueKind.Array)\n        {\n            targetCombatantIds = targetIdsElement.EnumerateArray()\n                .Where(element => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))\n                .Select(element => element.GetString()!)\n                .Distinct(StringComparer.OrdinalIgnoreCase)\n                .ToArray();\n        }\n        int? centerX =''',
    'parse multi-target trigger arguments',
)
replace_once(
    router,
    '''            return engine.RequestReadiedSpellDecision(campaign, encounter.Id, combatant.Id, targetCombatantId, centerX, centerY, direction);\n\n        return engine.TriggerReadiedSpell(campaign, encounter.Id, combatant.Id, dice, targetCombatantId, centerX, centerY, direction);\n''',
    '''            return engine.RequestReadiedSpellDecision(campaign, encounter.Id, combatant.Id, targetCombatantId, centerX, centerY, direction, targetCombatantIds);\n\n        return engine.TriggerReadiedSpell(campaign, encounter.Id, combatant.Id, dice, targetCombatantId, centerX, centerY, direction, targetCombatantIds);\n''',
    'forward multi-target trigger arguments',
)

router_main = ROOT / 'src/DungeonMasterAI.Engine/DmToolRouter.cs'
replace_once(
    router_main,
    '''        Tool("trigger_readied_spell", "Confirm that a readied spell trigger occurred and provide its release target or area geometry when needed. For an area spell, center_x/center_y are the proposed point of origin and direction is north/east/south/west for directional shapes. NPC reactions release automatically. A player character receives an explicit Reaction choice that shows the proposed area before any player-owned saves or damage dice resolve.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false),("center_x","integer",false),("center_y","integer",false),("direction","string",false))),\n''',
    '''        Tool("trigger_readied_spell", "Confirm that a readied spell trigger occurred and provide its release target, multi-target list, or area geometry when needed. target_combatant_ids is used for deterministic multi-target buffs. For an area spell, center_x/center_y are the proposed point of origin and direction selects a directional shape. NPC reactions release automatically. A player character receives an explicit Reaction choice showing the proposed release before any state is committed.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false),("target_combatant_ids","array",false),("center_x","integer",false),("center_y","integer",false),("direction","string",false))),\n''',
    'expose multi-target Ready tool schema',
)

workflow = Path('.github/workflows/windows-ci.yml')
replace_once(
    workflow,
    '''      - name: Run readied area spell tests\n        run: dotnet run --project tests/DungeonMasterAI.ReadiedAreaSpellTests/DungeonMasterAI.ReadiedAreaSpellTests.csproj --configuration Release\n\n      - name: Run readied movement decision tests\n''',
    '''      - name: Run readied area spell tests\n        run: dotnet run --project tests/DungeonMasterAI.ReadiedAreaSpellTests/DungeonMasterAI.ReadiedAreaSpellTests.csproj --configuration Release\n\n      - name: Run readied multi-buff spell tests\n        run: dotnet run --project tests/DungeonMasterAI.ReadiedMultiBuffTests/DungeonMasterAI.ReadiedMultiBuffTests.csproj --configuration Release\n\n      - name: Run readied movement decision tests\n''',
    'add r48 CI step',
)

print('r48 readied multi-buff codemod applied')
