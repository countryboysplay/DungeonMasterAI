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
    '''        if (resolution is "projectile_auto" or "projectile_attack"\n            && !caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"Readied projectile spells are currently enabled for player-character casters only; NPC projectile Ready resolution will be generalized in a later engine pass.");\n''',
    '',
    'remove NPC projectile Ready guard',
)
replace_once(
    spellcasting,
    '''        allocations);\n}\n\n        var results = new List<SpellTargetResolution>(projectileCount);''',
    '''        allocations,\n        dice);\n}\n\n        var results = new List<SpellTargetResolution>(projectileCount);''',
    'pass DiceService into player auto-projectile sequence',
)

readied = ROOT / 'src/DungeonMasterAI.Engine/GameEngine.ReadiedProjectileSpells.cs'
readied.write_text('''using DungeonMasterAI.Domain;\n\nnamespace DungeonMasterAI.Engine;\n\npublic sealed partial class GameEngine\n{\n    private SpellCastResult BeginReadiedProjectileSpellSequence(\n        CampaignState campaign,\n        CharacterSheet caster,\n        CharacterSheet target,\n        SpellDefinition spell,\n        int castAtLevel,\n        bool usedSlot,\n        EncounterState encounter,\n        DiceService dice)\n    {\n        var upcastLevels = Math.Max(0, castAtLevel - spell.Level);\n        var projectileCount = checked(spell.BaseProjectiles + (upcastLevels * spell.ExtraProjectilesPerSlot));\n        if (projectileCount < 1)\n            throw new InvalidOperationException($"{spell.Name} resolved to an invalid projectile count.");\n\n        ValidateSpellTargetType(target, spell);\n        ValidateSpellRange(campaign, encounter, caster, target, spell);\n        var allocations = Enumerable.Repeat(target.Id, projectileCount).ToArray();\n        var resolution = (spell.Resolution ?? "").Trim().ToLowerInvariant();\n        var playerCaster = caster.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase);\n\n        return resolution switch\n        {\n            "projectile_attack" when playerCaster => BeginPlayerProjectileSpellSequence(\n                campaign,\n                caster,\n                spell,\n                castAtLevel,\n                usedSlot,\n                spell.RequiresConcentration,\n                encounter,\n                allocations,\n                dice,\n                readiedReaction: true),\n            "projectile_attack" => BeginAutomaticReadiedProjectileSpellSequence(\n                campaign,\n                caster,\n                spell,\n                castAtLevel,\n                usedSlot,\n                spell.RequiresConcentration,\n                encounter,\n                allocations,\n                dice),\n            "projectile_auto" when playerCaster => BeginPlayerAutoProjectileSpellSequence(\n                campaign,\n                caster,\n                spell,\n                castAtLevel,\n                usedSlot,\n                spell.RequiresConcentration,\n                encounter,\n                allocations,\n                dice,\n                readiedReaction: true),\n            "projectile_auto" => BeginAutomaticReadiedAutoProjectileSpellSequence(\n                campaign,\n                caster,\n                spell,\n                castAtLevel,\n                usedSlot,\n                spell.RequiresConcentration,\n                encounter,\n                allocations,\n                dice),\n            _ => throw new InvalidOperationException($"{spell.Name} is not configured as a projectile spell.")\n        };\n    }\n}\n''', encoding='utf-8')

attack = ROOT / 'src/DungeonMasterAI.Engine/GameEngine.ProjectilePlayerRolls.cs'
replace_once(
    attack,
    '''    public bool ReadiedReaction { get; set; }\n    public List<string> TargetIds { get; set; } = [];''',
    '''    public bool ReadiedReaction { get; set; }\n    public bool AutomaticCasterRolls { get; set; }\n    public List<string> TargetIds { get; set; } = [];''',
    'add automatic-caster state to attack projectiles',
)
replace_once(
    attack,
    '''            ReadiedReaction = readiedReaction,\n            TargetIds = allocations.ToList(),''',
    '''            ReadiedReaction = readiedReaction,\n            AutomaticCasterRolls = false,\n            TargetIds = allocations.ToList(),''',
    'initialize player projectile state',
)
replace_once(
    attack,
    '''        return AdvancePlayerProjectileSpellSequence(campaign, state, dice);\n    }\n\n    public SpellCastResult ResolvePendingProjectileSpellAttackRoll''',
    '''        return AdvancePlayerProjectileSpellSequence(campaign, state, dice);\n    }\n\n    private SpellCastResult BeginAutomaticReadiedProjectileSpellSequence(\n        CampaignState campaign,\n        CharacterSheet caster,\n        SpellDefinition spell,\n        int castAtLevel,\n        bool usedSlot,\n        bool concentrationStarted,\n        EncounterState encounter,\n        IReadOnlyList<string> allocations,\n        DiceService dice)\n    {\n        var state = new PlayerProjectileSpellSequenceState\n        {\n            CasterId = caster.Id,\n            SpellId = spell.Id,\n            CastAtLevel = castAtLevel,\n            UsedSpellSlot = usedSlot,\n            ConcentrationStarted = concentrationStarted,\n            EncounterId = encounter.Id,\n            ReadiedReaction = true,\n            AutomaticCasterRolls = true,\n            TargetIds = allocations.ToList(),\n            NextProjectileIndex = 0,\n            Results = []\n        };\n        return AdvancePlayerProjectileSpellSequence(campaign, state, dice);\n    }\n\n    public SpellCastResult ResolvePendingProjectileSpellAttackRoll''',
    'add automatic readied attack-projectile entrypoint',
)
replace_once(
    attack,
    '''        var encounter = RequireProjectileEncounterIfAny(campaign, state, caster);\n        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));''',
    '''        var encounter = RequireProjectileEncounterIfAny(campaign, state, caster);\n        if (state.AutomaticCasterRolls)\n            return AdvanceAutomaticProjectileSpellAttack(campaign, state, caster, spell, target, encounter, dice);\n\n        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));''',
    'route NPC attack projectile through automatic sequence',
)
replace_once(
    attack,
    '''    private SpellCastResult FinalizePlayerProjectileSpellSequence(CampaignState campaign, PlayerProjectileSpellSequenceState state)''',
    '''    private SpellCastResult AdvanceAutomaticProjectileSpellAttack(\n        CampaignState campaign,\n        PlayerProjectileSpellSequenceState state,\n        CharacterSheet caster,\n        SpellDefinition spell,\n        CharacterSheet target,\n        EncounterState? encounter,\n        DiceService dice)\n    {\n        var projectileIndex = state.NextProjectileIndex;\n        var (attack, damage, attackSummary) = ResolveSpellAttack(campaign, caster, target, spell, 0, dice, encounter);\n        var projectileSummary = $"Projectile {projectileIndex + 1}: {attackSummary}" +\n            (damage?.Concentration is null ? "" : $" {damage.Concentration.Summary}");\n        state.Results.Add(new SpellTargetResolution(target.Id, target.Name, projectileIndex + 1, attack, null, damage, 0, projectileSummary));\n        state.NextProjectileIndex++;\n\n        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)\n        {\n            campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "projectile_spell_sequence";\n            campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);\n            Touch(campaign);\n            var waitSummary = state.NextProjectileIndex < state.TargetIds.Count\n                ? $"{projectileSummary} Resolve {target.Name}'s Concentration save before projectile {state.NextProjectileIndex + 1} can continue."\n                : $"{projectileSummary} Resolve {target.Name}'s Concentration save before the spell finishes resolving.";\n            return BuildProjectileSequenceResult(campaign, state, waitSummary);\n        }\n\n        return AdvancePlayerProjectileSpellSequence(campaign, state, dice);\n    }\n\n    private SpellCastResult FinalizePlayerProjectileSpellSequence(CampaignState campaign, PlayerProjectileSpellSequenceState state)''',
    'add automatic attack-projectile resolver',
)

auto = ROOT / 'src/DungeonMasterAI.Engine/GameEngine.AutoProjectilePlayerRolls.cs'
replace_once(
    auto,
    '''    public bool ReadiedReaction { get; set; }\n    public List<string> TargetIds { get; set; } = [];''',
    '''    public bool ReadiedReaction { get; set; }\n    public bool AutomaticCasterRolls { get; set; }\n    public List<string> TargetIds { get; set; } = [];''',
    'add automatic-caster state to auto projectiles',
)
replace_once(
    auto,
    '''        EncounterState? encounter,\n        IReadOnlyList<string> allocations,\n        bool readiedReaction = false)''',
    '''        EncounterState? encounter,\n        IReadOnlyList<string> allocations,\n        DiceService dice,\n        bool readiedReaction = false)''',
    'add DiceService to player auto-projectile entrypoint',
)
replace_once(
    auto,
    '''            ReadiedReaction = readiedReaction,\n            TargetIds = allocations.ToList(),''',
    '''            ReadiedReaction = readiedReaction,\n            AutomaticCasterRolls = false,\n            TargetIds = allocations.ToList(),''',
    'initialize player auto-projectile state',
)
replace_once(
    auto,
    '''        return AdvancePlayerAutoProjectileSpellSequence(campaign, state);\n    }\n\n    public SpellCastResult ResolvePendingAutoProjectileSpellDamageRoll''',
    '''        return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);\n    }\n\n    private SpellCastResult BeginAutomaticReadiedAutoProjectileSpellSequence(\n        CampaignState campaign,\n        CharacterSheet caster,\n        SpellDefinition spell,\n        int castAtLevel,\n        bool usedSlot,\n        bool concentrationStarted,\n        EncounterState encounter,\n        IReadOnlyList<string> allocations,\n        DiceService dice)\n    {\n        var state = new PlayerAutoProjectileSpellSequenceState\n        {\n            CasterId = caster.Id,\n            SpellId = spell.Id,\n            CastAtLevel = castAtLevel,\n            UsedSpellSlot = usedSlot,\n            ConcentrationStarted = concentrationStarted,\n            EncounterId = encounter.Id,\n            ReadiedReaction = true,\n            AutomaticCasterRolls = true,\n            TargetIds = allocations.ToList(),\n            NextProjectileIndex = 0,\n            Results = []\n        };\n        return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);\n    }\n\n    public SpellCastResult ResolvePendingAutoProjectileSpellDamageRoll''',
    'add automatic readied auto-projectile entrypoint',
)
replace_once(
    auto,
    '''        return AdvancePlayerAutoProjectileSpellSequence(campaign, state);\n    }\n\n    private SpellCastResult AdvancePlayerAutoProjectileSpellSequence(\n        CampaignState campaign,\n        PlayerAutoProjectileSpellSequenceState state)''',
    '''        return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);\n    }\n\n    private SpellCastResult AdvancePlayerAutoProjectileSpellSequence(\n        CampaignState campaign,\n        PlayerAutoProjectileSpellSequenceState state,\n        DiceService dice)''',
    'pass DiceService while advancing auto-projectiles',
)
replace_once(
    auto,
    '''        var encounter = RequireAutoProjectileEncounterIfAny(campaign, state, caster);\n        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));\n\n        var pending = new PendingRollRequest''',
    '''        var encounter = RequireAutoProjectileEncounterIfAny(campaign, state, caster);\n        if (state.AutomaticCasterRolls)\n        {\n            var projectileIndex = state.NextProjectileIndex;\n            var damageAmount = dice.RollDamage(spell.DamageExpression);\n            DamageResolutionResult? damage = null;\n            if (!target.Dead)\n                damage = ApplyDamageWithConcentration(campaign, target.Id, damageAmount, dice, spell.DamageType);\n            var summary = target.Dead && damage is null\n                ? $"Projectile {projectileIndex + 1} was already allocated to {target.Name}; the target had been reduced to death by an earlier declared projectile before this damage instance was applied."\n                : $"Projectile {projectileIndex + 1} automatically struck {target.Name} for {damageAmount} {spell.DamageType} damage." + (damage?.Concentration is null ? "" : $" {damage.Concentration.Summary}");\n            state.Results.Add(new SpellTargetResolution(target.Id, target.Name, projectileIndex + 1, null, null, damage, 0, summary));\n            state.NextProjectileIndex++;\n\n            if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase) == true)\n            {\n                campaign.PendingPlayerRoll.Context["continuation_resolution_key"] = "auto_projectile_spell_sequence";\n                campaign.PendingPlayerRoll.Context["continuation_sequence_json"] = JsonSerializer.Serialize(state);\n                Touch(campaign);\n                var waitSummary = state.NextProjectileIndex < state.TargetIds.Count\n                    ? $"{summary} Resolve {target.Name}'s Concentration save before projectile {state.NextProjectileIndex + 1} can continue."\n                    : $"{summary} Resolve {target.Name}'s Concentration save before the spell finishes resolving.";\n                return BuildAutoProjectileSequenceResult(campaign, state, waitSummary);\n            }\n\n            return AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);\n        }\n\n        var casterCombatant = encounter?.Combatants.FirstOrDefault(c => c.CharacterId.Equals(caster.Id, StringComparison.OrdinalIgnoreCase));\n\n        var pending = new PendingRollRequest''',
    'resolve NPC auto-projectile damage automatically',
)
replace_once(
    auto,
    '''        var result = AdvancePlayerAutoProjectileSpellSequence(campaign, state);''',
    '''        var result = AdvancePlayerAutoProjectileSpellSequence(campaign, state, dice);''',
    'resume automatic/player auto-projectile sequence with DiceService',
)

tests = ROOT / 'tests/DungeonMasterAI.ReadiedProjectileSpellTests/Program.cs'
replace_once(
    tests,
    '''Run("NPC projectile Ready remains blocked instead of partially resolving unsupported ownership", () =>\n{\n    var f = CreateFixture(CreateAttackProjectileSpell(), targetType: "pc");\n    f.Caster.CharacterType = "monster";\n    var rejected = false;\n    try { f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the hero advances", 2); }\n    catch (InvalidOperationException) { rejected = true; }\n    True(rejected, "NPC projectile Ready is explicitly rejected in r45");\n    Equal(3, f.Caster.SpellSlots[2].Remaining, "rejected NPC Ready spends no slot");\n    True(f.CasterCombatant.ActionAvailable, "rejected NPC Ready spends no action");\n});\n''',
    '''Run("NPC readied attack projectiles resolve automatically but pause for PC Concentration", () =>\n{\n    var f = CreateFixture(CreateAttackProjectileSpell(), targetType: "pc");\n    f.Caster.CharacterType = "monster";\n    var dice = MaximumDice();\n    var slotsBefore = f.Caster.SpellSlots[2].Remaining;\n    var hpBefore = f.Target.CurrentHp;\n    f.Engine.BeginConcentration(f.Campaign, f.Target.Id, "Bless");\n\n    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the hero advances", 2);\n    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "NPC Ready spends its slot once");\n    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);\n    f.Engine.TriggerReadiedSpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, dice, f.TargetCombatant.Id);\n\n    for (var projectile = 1; projectile <= 3; projectile++)\n    {\n        var concentration = f.Campaign.PendingPlayerRoll ?? throw new Exception($"Concentration save missing after NPC projectile {projectile}");\n        Equal("concentration_check", concentration.ResolutionKey, $"NPC projectile {projectile} Concentration handoff");\n        Equal("projectile_spell_sequence", concentration.Context["continuation_resolution_key"], $"NPC projectile {projectile} continuation key");\n        f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentration.Id, 20, null, dice);\n    }\n\n    True(f.Campaign.PendingPlayerRoll is null, "NPC attack-projectile sequence finishes after final Concentration save");\n    True(f.Target.CurrentHp < hpBefore, "NPC attack projectiles dealt automatic damage");\n    Equal(slotsBefore - 1, f.Caster.SpellSlots[2].Remaining, "NPC projectile release never spends a second slot");\n    True(!f.CasterCombatant.ReactionAvailable, "NPC projectile release spends Reaction");\n});\n\nRun("NPC readied auto-hit projectiles resolve damage automatically and preserve Concentration pauses", () =>\n{\n    var f = CreateFixture(CreateAutoProjectileSpell(), targetType: "pc");\n    f.Caster.CharacterType = "monster";\n    var dice = MaximumDice();\n    var hpBefore = f.Target.CurrentHp;\n    f.Engine.BeginConcentration(f.Campaign, f.Target.Id, "Bless");\n\n    f.Engine.TakeReadySpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, f.Spell.Id, "the hero advances", 1);\n    f.Engine.NextTurn(f.Campaign, f.Encounter.Id, dice);\n    f.Engine.TriggerReadiedSpell(f.Campaign, f.Encounter.Id, f.CasterCombatant.Id, dice, f.TargetCombatant.Id);\n\n    for (var projectile = 1; projectile <= 3; projectile++)\n    {\n        var concentration = f.Campaign.PendingPlayerRoll ?? throw new Exception($"Concentration save missing after NPC auto projectile {projectile}");\n        Equal("concentration_check", concentration.ResolutionKey, $"NPC auto projectile {projectile} Concentration handoff");\n        Equal("auto_projectile_spell_sequence", concentration.Context["continuation_resolution_key"], $"NPC auto projectile {projectile} continuation key");\n        f.Engine.ResolvePendingConcentrationCheckRoll(f.Campaign, concentration.Id, 20, null, dice);\n    }\n\n    True(f.Campaign.PendingPlayerRoll is null, "NPC auto-projectile sequence finishes cleanly");\n    True(f.Target.CurrentHp < hpBefore, "NPC auto projectiles dealt automatic damage");\n});\n''',
    'replace blocked NPC projectile test with automatic sequence coverage',
)
replace_once(
    tests,
    '''static DiceService MinimumDice() => new((min, max) => min);''',
    '''static DiceService MinimumDice() => new((min, max) => min);\nstatic DiceService MaximumDice() => new((min, max) => max);''',
    'add deterministic maximum dice helper',
)

print('r47 NPC readied projectile codemod applied')
