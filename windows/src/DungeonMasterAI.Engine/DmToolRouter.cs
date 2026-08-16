using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed record DmToolDefinition(string Name, string Description, object Parameters);
public sealed record DmToolResult(bool Ok, object? Result = null, string? Error = null, string? ErrorCode = null);

public sealed class DmToolRouter(GameEngine engine, DiceService dice, RulesSearchService rules)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<DmToolDefinition> Definitions { get; } =
    [
        Tool("search_rules", "Search the local SRD rules index before resolving an uncertain rule.", Props(("query","string",true),("limit","integer",false))),
        Tool("list_characters", "List current character state available to the DM runtime.", Props()),
        Tool("get_character", "Get one character by id.", Props(("character_id","string",true))),
        Tool("get_inventory", "Get a character inventory.", Props(("character_id","string",true))),
        Tool("list_locations", "List locations currently visible to the players.", Props()),
        Tool("get_location", "Get a player-visible location by id.", Props(("location_id","string",true))),
        Tool("reveal_location", "Reveal a campaign location to the party when fiction or exploration justifies it.", Props(("location_id","string",true))),
        Tool("move_party", "Move the party to a discovered location.", Props(("location_id","string",true))),
        Tool("roll_dice", "Roll an application-owned dice expression such as 2d6+3.", Props(("expression","string",true))),
        Tool("ability_check", "Resolve an ability check using character stats, proficiency, Exhaustion, and a DM-selected DC. NPC checks resolve immediately. Player-character checks create a required player d20 roll and stop until the Game Table supplies it.", Props(("character_id","string",true),("ability","string",true),("dc","integer",true),("skill","string",false),("roll_mode","string",false),("circumstance_modifier","integer",false))),
        Tool("saving_throw", "Resolve a saving throw using character stats, save proficiency, Exhaustion, and a DC. NPC saves resolve immediately. Player-character saves create a required player d20 roll and stop until the Game Table supplies it, unless the save automatically fails.", Props(("character_id","string",true),("ability","string",true),("dc","integer",true),("roll_mode","string",false),("circumstance_modifier","integer",false))),
        Tool("damage_character", "Apply typed damage, including Temporary HP, Resistance, Vulnerability, Immunity, 0 HP, Death Saving Throw rules, and an automatic Concentration save when required.", Props(("character_id","string",true),("amount","integer",true),("damage_type","string",false),("critical_hit","boolean",false))),
        Tool("heal_character", "Heal a living character up to maximum HP.", Props(("character_id","string",true),("amount","integer",true))),
        Tool("grant_temporary_hp", "Grant Temporary Hit Points; a lower new value never stacks onto an existing higher value.", Props(("character_id","string",true),("amount","integer",true))),
        Tool("death_save", "Roll and resolve a Death Saving Throw for a non-player character at 0 HP. Player-character Death Saves are rolled by the player in the Game Table UI. Include encounter_id during active combat so the once-per-turn rule is enforced.", Props(("character_id","string",true),("encounter_id","string",false))),
        Tool("add_condition", "Add a named condition to a character.", Props(("character_id","string",true),("condition","string",true))),
        Tool("remove_condition", "Remove a named condition from a character.", Props(("character_id","string",true),("condition","string",true))),
        Tool("set_exhaustion", "Set a character's Exhaustion level from 0 to 6.", Props(("character_id","string",true),("level","integer",true))),
        Tool("begin_concentration", "Start Concentration on an effect. Starting a new Concentration effect automatically ends the previous one.", Props(("character_id","string",true),("effect","string",true))),
        Tool("end_concentration", "End a character's current Concentration effect.", Props(("character_id","string",true),("reason","string",false))),
        Tool("list_prepared_spells", "List a character's prepared spells with deterministic casting metadata, spell save DC, and spell attack modifier.", Props(("character_id","string",true))),
        Tool("cast_spell", "Cast one prepared spell through the deterministic spell engine. The engine validates spell slots, components, one-slot-spell-per-turn limits, attacks, saving throws, damage, healing, upcasting, Rituals, and Concentration.", Props(("character_id","string",true),("spell_id","string",true),("target_id","string",false),("slot_level","integer",false),("as_ritual","boolean",false),("encounter_id","string",false))),
        Tool("cast_projectile_spell", "Cast a deterministic multi-projectile spell such as Magic Missile or Scorching Ray. target_ids is either one target for every projectile or one target id per projectile in firing order.", Props(("character_id","string",true),("spell_id","string",true),("target_ids","array",true),("slot_level","integer",false),("encounter_id","string",false))),
        Tool("cast_multi_target_spell", "Cast a deterministic multi-target buff spell such as Bless. The engine validates every target before spending the slot and binds the effect to Concentration when required.", Props(("character_id","string",true),("spell_id","string",true),("target_ids","array",true),("slot_level","integer",false),("encounter_id","string",false))),
        Tool("cast_area_spell", "Cast a deterministic tactical area spell. Point-origin spells use center_x/center_y; self-origin cones and cubes use direction. The engine selects positioned creatures from battlefield geometry and resolves all saves and damage.", Props(("character_id","string",true),("spell_id","string",true),("center_x","integer",false),("center_y","integer",false),("direction","string",false),("slot_level","integer",false),("encounter_id","string",false))),
        Tool("cast_persistent_area_spell", "Cast a deterministic persistent-area spell such as Fog Cloud. The engine validates range, slot, action economy, Concentration, tactical geometry, upcast size, and creates a persistent battlefield effect.", Props(("character_id","string",true),("spell_id","string",true),("center_x","integer",false),("center_y","integer",false),("direction","string",false),("slot_level","integer",false),("encounter_id","string",false))),
        Tool("spend_spell_slot", "Spend one configured spell slot directly for a feature that explicitly requires it. Prefer cast_spell for actual spellcasting.", Props(("character_id","string",true),("level","integer",true))),
        Tool("spend_resource", "Spend a configured class or feature resource.", Props(("character_id","string",true),("resource","string",true),("amount","integer",false))),
        Tool("short_rest", "Complete a one-hour Short Rest and recharge configured Short Rest resources.", Props(("character_id","string",true))),
        Tool("spend_hit_die", "Spend one Hit Point Die after a Short Rest; the application rolls the die and applies Constitution.", Props(("character_id","string",true))),
        Tool("long_rest", "Complete an eight-hour Long Rest and apply configured recovery benefits.", Props(("character_id","string",true))),
        Tool("advance_time", "Advance campaign time in minutes.", Props(("minutes","integer",true))),
        Tool("get_active_encounter", "Get the active combat encounter, initiative order, combatants, HP, AC, and current turn.", Props()),
        Tool("list_available_encounters", "List planned encounters at the party's current location for DM adjudication. Treat these as DM-only until fiction reveals them.", Props()),
        Tool("activate_encounter", "Activate a planned encounter and add living player characters to it.", Props(("encounter_id","string",true),("include_party","boolean",false))),
        Tool("start_encounter", "Start a deterministic combat encounter. Player characters are added automatically unless include_party is false.", Props(("name","string",true),("include_party","boolean",false))),
        Tool("add_combatant", "Add an existing NPC, monster, or character to an active encounter. side may be party, opposition, or neutral.", Props(("encounter_id","string",true),("character_id","string",true),("surprised","boolean",false),("side","string",false))),
        Tool("roll_initiative", "Begin or resume deterministic Initiative for the encounter. NPC Initiative is rolled automatically. Each player character receives a required player-controlled d20 request, including Advantage or Disadvantage and Exhaustion, and the sequence resumes automatically after each supplied roll.", Props(("encounter_id","string",true))),
        Tool("set_combatant_position", "Place a combatant on the 5-foot tactical grid. Use for initial positioning or DM-authorized repositioning, not ordinary movement.", Props(("encounter_id","string",true),("combatant_id","string",true),("grid_x","integer",true),("grid_y","integer",true))),
        Tool("add_terrain_feature", "Add deterministic tactical terrain to an encounter. Supports Difficult Terrain, movement blocking, sight blocking, heavy obscurement, and none/half/three-quarters/total cover.", Props(("encounter_id","string",true),("name","string",true),("grid_x","integer",true),("grid_y","integer",true),("width_squares","integer",false),("height_squares","integer",false),("difficult_terrain","boolean",false),("blocks_movement","boolean",false),("blocks_line_of_sight","boolean",false),("heavily_obscured","boolean",false),("cover","string",false))),
        Tool("add_battlefield_effect", "Add a persistent tactical zone such as fire, magical darkness, fog, or hazardous terrain. Supports sphere/cone/cube geometry, start-turn/enter/move-within damage triggers, saves, Difficult Terrain, obscurement, line-of-sight blocking, durations, and Concentration binding.", Props(("encounter_id","string",true),("name","string",true),("origin_x","integer",true),("origin_y","integer",true),("shape","string",false),("size_feet","integer",false),("direction","string",false),("trigger","string",false),("damage_expression","string",false),("damage_type","string",false),("save_ability","string",false),("save_dc","integer",false),("half_on_save","boolean",false),("once_per_turn","boolean",false),("difficult_terrain","boolean",false),("heavily_obscured","boolean",false),("blocks_line_of_sight","boolean",false),("source_character_id","string",false),("source_spell_id","string",false),("requires_concentration","boolean",false),("concentration_name","string",false),("duration_rounds","integer",false),("dm_only","boolean",false))),
        Tool("remove_battlefield_effect", "Remove a persistent tactical battlefield effect by id or exact name.", Props(("encounter_id","string",true),("effect_id","string",true),("reason","string",false))),
        Tool("list_battlefield_effects", "List persistent tactical battlefield effects in an encounter, including geometry, hazards, and duration metadata.", Props(("encounter_id","string",true))),
        Tool("move_combatant", "Move the current combatant on the 5-foot tactical grid. The engine enforces remaining Speed, Difficult Terrain, blocked squares, and Opportunity Attack triggers. If the move provokes, it pauses until reactions are resolved or declined.", Props(("encounter_id","string",true),("combatant_id","string",true),("grid_x","integer",true),("grid_y","integer",true))),
        Tool("take_disengage", "Take the Disengage action for the current combatant. It consumes the combatant's action and prevents its movement from provoking Opportunity Attacks for the rest of the turn.", Props(("encounter_id","string",true),("combatant_id","string",true))),
        Tool("take_dash", "Take the Dash action for the current combatant. It consumes the combatant's action and adds extra movement equal to its effective Speed for the rest of the turn.", Props(("encounter_id","string",true),("combatant_id","string",true))),
        Tool("take_dodge", "Take the Dodge action for the current combatant. It consumes the combatant's action; attacks against it have Disadvantage and its Dexterity saves have Advantage until the start of its next turn while the benefit remains active.", Props(("encounter_id","string",true),("combatant_id","string",true))),
        Tool("take_hide", "Take the 2024 Hide action. The engine requires heavy obscurement or sufficient cover, verifies enemies lack line of sight, rolls DC 15 Dexterity (Stealth), and records the Perception DC on success.", Props(("encounter_id","string",true),("combatant_id","string",true))),
        Tool("search_hidden", "Use the Search action with Wisdom (Perception) against a selected hidden combatant's recorded Hide total. A success ends that combatant's hidden state.", Props(("encounter_id","string",true),("searcher_combatant_id","string",true),("target_combatant_id","string",true))),
        Tool("ready_attack", "Take the Ready action to prepare one configured attack for a perceivable trigger. The action is spent now; the attack uses the creature's Reaction when the DM confirms the trigger occurred.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",true),("trigger","string",true),("attack_name","string",false))),
        Tool("ready_move", "Take the Ready action to prepare movement up to Speed for a perceivable trigger. The action is spent now; movement uses the creature's Reaction when triggered.", Props(("encounter_id","string",true),("combatant_id","string",true),("trigger","string",true))),
        Tool("ready_spell", "Take the 2024 Ready action with a prepared spell whose casting time is 1 Action. The spell is cast and any slot is spent now, then its energy is held with Concentration until the trigger or the creature's next turn.", Props(("encounter_id","string",true),("combatant_id","string",true),("spell_id","string",true),("trigger","string",true),("slot_level","integer",false))),
        Tool("trigger_readied_attack", "Resolve a previously readied attack immediately after the DM confirms its trigger. This spends the readied creature's Reaction and applies normal attack, cover, hidden, damage, and Concentration rules.", Props(("encounter_id","string",true),("combatant_id","string",true))),
        Tool("trigger_readied_move", "Resolve previously readied movement immediately after the DM confirms its trigger. Destination is chosen at trigger time; movement is limited by Speed and can provoke Opportunity Attacks.", Props(("encounter_id","string",true),("combatant_id","string",true),("grid_x","integer",true),("grid_y","integer",true))),
        Tool("trigger_readied_spell", "Release a previously readied spell immediately after the DM confirms its trigger. This spends the creature's Reaction; choose the target at release time when the spell requires one.", Props(("encounter_id","string",true),("combatant_id","string",true),("target_combatant_id","string",false))),
        Tool("help_attack", "Take the Help action to distract an enemy within 5 feet. The next attack roll by an ally against that enemy has Advantage before the helper's next turn.", Props(("encounter_id","string",true),("helper_combatant_id","string",true),("target_combatant_id","string",true))),
        Tool("help_ability_check", "Take the Help action to assist an ally's next ability check with one skill or tool the helper is proficient in. Only call this when the fiction makes verbal or physical assistance possible.", Props(("encounter_id","string",true),("helper_combatant_id","string",true),("ally_combatant_id","string",true),("proficiency","string",true))),
        Tool("first_aid", "Use the Help action to administer first aid with a DC 10 Wisdom (Medicine) check, stabilizing a living creature at 0 HP or ending a nonlethal Unconscious condition on success.", Props(("encounter_id","string",true),("helper_combatant_id","string",true),("target_combatant_id","string",true))),
        Tool("take_search", "Take the Search action and resolve a Wisdom check using Insight, Medicine, Perception, or Survival against a DM-selected DC.", Props(("encounter_id","string",true),("combatant_id","string",true),("skill","string",true),("dc","integer",true))),
        Tool("take_study", "Take the Study action and resolve an Intelligence check using Arcana, History, Investigation, Nature, or Religion against a DM-selected DC.", Props(("encounter_id","string",true),("combatant_id","string",true),("skill","string",true),("dc","integer",true))),
        Tool("take_influence", "Take the Influence action and resolve an appropriate Charisma check, or Wisdom (Animal Handling), against a DM-selected DC.", Props(("encounter_id","string",true),("combatant_id","string",true),("skill","string",true),("dc","integer",true))),
        Tool("unarmed_grapple", "Use one attack from the Attack action to attempt the 2024 Unarmed Strike Grapple option against a target within 5 feet. The target chooses Strength or Dexterity; omit save_ability to let the engine choose its better legal save.", Props(("encounter_id","string",true),("attacker_combatant_id","string",true),("target_combatant_id","string",true),("save_ability","string",false))),
        Tool("unarmed_shove", "Use one attack from the Attack action to attempt the 2024 Unarmed Strike Shove option against a target within 5 feet. effect must be prone or push.", Props(("encounter_id","string",true),("attacker_combatant_id","string",true),("target_combatant_id","string",true),("effect","string",true),("save_ability","string",false))),
        Tool("escape_grapple", "Use the grappled creature's action to attempt Athletics or Acrobatics against one active grapple escape DC.", Props(("encounter_id","string",true),("target_combatant_id","string",true),("grappler_combatant_id","string",true),("skill","string",true))),
        Tool("release_grapple", "Release one grapple you are maintaining. This requires no action.", Props(("encounter_id","string",true),("grappler_combatant_id","string",true),("target_combatant_id","string",true))),
        Tool("stand_from_prone", "Spend movement equal to half the creature's current Speed to end the Prone condition. This does not consume the creature's action.", Props(("encounter_id","string",true),("combatant_id","string",true))),
        Tool("get_pending_opportunity_attacks", "List unresolved Opportunity Attack reactions for a pending combat move.", Props(("encounter_id","string",true))),
        Tool("resolve_opportunity_attack", "Use an eligible combatant's Reaction to make a melee weapon attack or Unarmed Strike against a creature leaving its reach.", Props(("encounter_id","string",true),("reactor_combatant_id","string",true),("attack_name","string",false))),
        Tool("decline_opportunity_attack", "Decline one pending Opportunity Attack without spending the reactor's Reaction.", Props(("encounter_id","string",true),("reactor_combatant_id","string",true))),
        Tool("combat_attack", "Attack one combatant with a configured attack profile. NPC attacks resolve immediately through the deterministic engine. Player-character attacks create a required player d20 roll and stop until the Game Table supplies that roll; never invent or bypass it.", Props(("encounter_id","string",true),("attacker_combatant_id","string",true),("target_combatant_id","string",true),("attack_name","string",false))),
        Tool("next_combat_turn", "Advance to the next combatant and increment the round when initiative wraps.", Props(("encounter_id","string",true))),
        Tool("end_encounter", "Mark a combat encounter completed.", Props(("encounter_id","string",true))),
        Tool("list_quests", "List player-visible quest information.", Props()),
        Tool("set_quest_status", "Set a quest status after the relevant game event occurs.", Props(("quest_id","string",true),("status","string",true))),
        Tool("list_merchants", "List merchants in discovered locations with current stock and prices.", Props()),
        Tool("purchase_item", "Buy an item with application-owned gold and stock updates.", Props(("character_id","string",true),("merchant_id","string",true),("item_id","string",true),("quantity","integer",false))),
        Tool("recent_events", "Get recent player-visible campaign events.", Props(("limit","integer",false)))
    ];

    public DmToolResult Execute(CampaignState campaign, string toolName, string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var a = doc.RootElement;
            object? result = toolName switch
            {
                "search_rules" => rules.Search(RequiredString(a, "query"), OptionalInt(a, "limit", 6)),
                "list_characters" => campaign.Characters.Select(c => DmCharacter(campaign, c)).ToArray(),
                "get_character" => DmCharacter(campaign, RequireCharacter(campaign, RequiredString(a, "character_id"))),
                "get_inventory" => GetInventory(campaign, RequiredString(a, "character_id")),
                "list_locations" => campaign.Locations.Where(x => x.Discovered && !x.DmOnly).Select(PlayerLocation).ToArray(),
                "get_location" => PlayerLocation(RequireVisibleLocation(campaign, RequiredString(a, "location_id"))),
                "reveal_location" => new { changed = engine.RevealLocation(campaign, RequiredString(a, "location_id")) },
                "move_party" => MoveParty(campaign, RequiredString(a, "location_id")),
                "roll_dice" => dice.Roll(RequiredString(a, "expression")),
                "ability_check" => AbilityCheck(campaign, a),
                "saving_throw" => SavingThrow(campaign, a),
                "damage_character" => engine.ApplyDamageWithConcentration(campaign, RequiredString(a, "character_id"), RequiredInt(a, "amount"), dice, OptionalString(a, "damage_type"), OptionalBool(a, "critical_hit", false)),
                "heal_character" => new { hp = engine.Heal(campaign, RequiredString(a, "character_id"), RequiredInt(a, "amount")) },
                "grant_temporary_hp" => new { temp_hp = engine.GrantTemporaryHitPoints(campaign, RequiredString(a, "character_id"), RequiredInt(a, "amount")) },
                "death_save" => DeathSave(campaign, RequiredString(a, "character_id"), OptionalString(a, "encounter_id")),
                "add_condition" => new { changed = engine.AddCondition(campaign, RequiredString(a, "character_id"), RequiredString(a, "condition")) },
                "remove_condition" => new { changed = engine.RemoveCondition(campaign, RequiredString(a, "character_id"), RequiredString(a, "condition")) },
                "set_exhaustion" => new { level = engine.SetExhaustion(campaign, RequiredString(a, "character_id"), RequiredInt(a, "level")) },
                "begin_concentration" => new { effect = engine.BeginConcentration(campaign, RequiredString(a, "character_id"), RequiredString(a, "effect")) },
                "end_concentration" => new { changed = engine.EndConcentration(campaign, RequiredString(a, "character_id"), OptionalString(a, "reason") ?? "ended voluntarily") },
                "list_prepared_spells" => ListPreparedSpells(campaign, RequiredString(a, "character_id")),
                "cast_spell" => engine.CastSpell(campaign, RequiredString(a, "character_id"), RequiredString(a, "spell_id"), dice, OptionalString(a, "target_id"), OptionalNullableInt(a, "slot_level"), OptionalBool(a, "as_ritual", false), OptionalString(a, "encounter_id")),
                "cast_projectile_spell" => engine.CastProjectileSpell(campaign, RequiredString(a, "character_id"), RequiredString(a, "spell_id"), dice, RequiredStringArray(a, "target_ids"), OptionalNullableInt(a, "slot_level"), OptionalString(a, "encounter_id")),
                "cast_multi_target_spell" => engine.CastMultiTargetSpell(campaign, RequiredString(a, "character_id"), RequiredString(a, "spell_id"), dice, RequiredStringArray(a, "target_ids"), OptionalNullableInt(a, "slot_level"), OptionalString(a, "encounter_id")),
                "cast_area_spell" => engine.CastAreaSpell(campaign, RequiredString(a, "character_id"), RequiredString(a, "spell_id"), dice, OptionalNullableInt(a, "center_x"), OptionalNullableInt(a, "center_y"), OptionalString(a, "direction") ?? "north", OptionalNullableInt(a, "slot_level"), OptionalString(a, "encounter_id")),
                "cast_persistent_area_spell" => engine.CastPersistentAreaSpell(campaign, RequiredString(a, "character_id"), RequiredString(a, "spell_id"), dice, OptionalNullableInt(a, "center_x"), OptionalNullableInt(a, "center_y"), OptionalString(a, "direction") ?? "north", OptionalNullableInt(a, "slot_level"), OptionalString(a, "encounter_id")),
                "spend_spell_slot" => new { remaining = engine.SpendSpellSlot(campaign, RequiredString(a, "character_id"), RequiredInt(a, "level")) },
                "spend_resource" => new { remaining = engine.SpendResource(campaign, RequiredString(a, "character_id"), RequiredString(a, "resource"), OptionalInt(a, "amount", 1)) },
                "short_rest" => engine.ShortRest(campaign, RequiredString(a, "character_id")),
                "spend_hit_die" => SpendHitDie(campaign, RequiredString(a, "character_id")),
                "long_rest" => engine.LongRest(campaign, RequiredString(a, "character_id")),
                "advance_time" => AdvanceTime(campaign, RequiredInt(a, "minutes")),
                "get_active_encounter" => GetActiveEncounter(campaign),
                "list_available_encounters" => ListAvailableEncounters(campaign),
                "activate_encounter" => ActivateEncounter(campaign, RequiredString(a, "encounter_id"), OptionalBool(a, "include_party", true)),
                "start_encounter" => StartEncounter(campaign, RequiredString(a, "name"), OptionalBool(a, "include_party", true)),
                "add_combatant" => engine.AddCombatant(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "character_id"), OptionalBool(a, "surprised", false), OptionalString(a, "side")),
                "roll_initiative" => RollInitiative(campaign, RequiredString(a, "encounter_id")),
                "set_combatant_position" => engine.SetCombatantPosition(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredInt(a, "grid_x"), RequiredInt(a, "grid_y")),
                "add_terrain_feature" => engine.AddTerrainFeature(campaign, RequiredString(a, "encounter_id"), new TerrainFeature
                {
                    Name = RequiredString(a, "name"),
                    GridX = RequiredInt(a, "grid_x"),
                    GridY = RequiredInt(a, "grid_y"),
                    WidthSquares = OptionalInt(a, "width_squares", 1),
                    HeightSquares = OptionalInt(a, "height_squares", 1),
                    DifficultTerrain = OptionalBool(a, "difficult_terrain", false),
                    BlocksMovement = OptionalBool(a, "blocks_movement", false),
                    BlocksLineOfSight = OptionalBool(a, "blocks_line_of_sight", false),
                    HeavilyObscured = OptionalBool(a, "heavily_obscured", false),
                    Cover = OptionalString(a, "cover") ?? "none",
                    SourceKind = "runtime_generated"
                }),
                "add_battlefield_effect" => engine.AddBattlefieldEffect(campaign, RequiredString(a, "encounter_id"), new BattlefieldEffectState
                {
                    Name = RequiredString(a, "name"),
                    OriginX = RequiredInt(a, "origin_x"),
                    OriginY = RequiredInt(a, "origin_y"),
                    Shape = OptionalString(a, "shape") ?? "sphere",
                    SizeFeet = OptionalInt(a, "size_feet", 5),
                    Direction = OptionalString(a, "direction") ?? "north",
                    Trigger = OptionalString(a, "trigger") ?? "none",
                    DamageExpression = OptionalString(a, "damage_expression") ?? "",
                    DamageType = OptionalString(a, "damage_type") ?? "",
                    SaveAbility = OptionalString(a, "save_ability") ?? "",
                    SaveDc = OptionalInt(a, "save_dc", 0),
                    HalfDamageOnSuccessfulSave = OptionalBool(a, "half_on_save", false),
                    OncePerTurn = OptionalBool(a, "once_per_turn", true),
                    DifficultTerrain = OptionalBool(a, "difficult_terrain", false),
                    HeavilyObscured = OptionalBool(a, "heavily_obscured", false),
                    BlocksLineOfSight = OptionalBool(a, "blocks_line_of_sight", false),
                    SourceCharacterId = OptionalString(a, "source_character_id") ?? "",
                    SourceSpellId = OptionalString(a, "source_spell_id") ?? "",
                    RequiresSourceConcentration = OptionalBool(a, "requires_concentration", false),
                    ConcentrationName = OptionalString(a, "concentration_name") ?? "",
                    DurationRounds = OptionalInt(a, "duration_rounds", 0),
                    DmOnly = OptionalBool(a, "dm_only", false),
                    SourceKind = "runtime_generated"
                }),
                "remove_battlefield_effect" => new { changed = engine.RemoveBattlefieldEffect(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "effect_id"), OptionalString(a, "reason") ?? "removed") },
                "list_battlefield_effects" => ListBattlefieldEffects(campaign, RequiredString(a, "encounter_id")),
                "move_combatant" => engine.MoveCombatant(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredInt(a, "grid_x"), RequiredInt(a, "grid_y")),
                "take_disengage" => engine.TakeDisengage(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id")),
                "take_dash" => engine.TakeDash(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id")),
                "take_dodge" => engine.TakeDodge(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id")),
                "take_hide" => engine.TakeHide(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), dice),
                "search_hidden" => engine.SearchForHiddenCombatant(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "searcher_combatant_id"), RequiredString(a, "target_combatant_id"), dice),
                "ready_attack" => engine.TakeReadyAttack(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredString(a, "target_combatant_id"), RequiredString(a, "trigger"), OptionalString(a, "attack_name")),
                "ready_move" => engine.TakeReadyMove(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredString(a, "trigger")),
                "ready_spell" => engine.TakeReadySpell(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredString(a, "spell_id"), RequiredString(a, "trigger"), OptionalNullableInt(a, "slot_level")),
                "trigger_readied_attack" => engine.TriggerReadiedAttack(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), dice),
                "trigger_readied_move" => engine.TriggerReadiedMove(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredInt(a, "grid_x"), RequiredInt(a, "grid_y")),
                "trigger_readied_spell" => engine.TriggerReadiedSpell(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), dice, OptionalString(a, "target_combatant_id")),
                "help_attack" => engine.TakeHelpAttack(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "helper_combatant_id"), RequiredString(a, "target_combatant_id")),
                "help_ability_check" => engine.TakeHelpAbilityCheck(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "helper_combatant_id"), RequiredString(a, "ally_combatant_id"), RequiredString(a, "proficiency")),
                "first_aid" => engine.TakeFirstAid(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "helper_combatant_id"), RequiredString(a, "target_combatant_id"), dice),
                "take_search" => engine.TakeSearchAction(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredString(a, "skill"), RequiredInt(a, "dc"), dice),
                "take_study" => engine.TakeStudyAction(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredString(a, "skill"), RequiredInt(a, "dc"), dice),
                "take_influence" => engine.TakeInfluenceAction(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id"), RequiredString(a, "skill"), RequiredInt(a, "dc"), dice),
                "unarmed_grapple" => engine.ResolveUnarmedGrapple(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "attacker_combatant_id"), RequiredString(a, "target_combatant_id"), dice, OptionalString(a, "save_ability")),
                "unarmed_shove" => engine.ResolveUnarmedShove(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "attacker_combatant_id"), RequiredString(a, "target_combatant_id"), RequiredString(a, "effect"), dice, OptionalString(a, "save_ability")),
                "escape_grapple" => engine.EscapeGrapple(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "target_combatant_id"), RequiredString(a, "grappler_combatant_id"), RequiredString(a, "skill"), dice),
                "release_grapple" => engine.ReleaseGrapple(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "grappler_combatant_id"), RequiredString(a, "target_combatant_id")),
                "stand_from_prone" => engine.StandFromProne(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "combatant_id")),
                "get_pending_opportunity_attacks" => engine.GetPendingOpportunityAttacks(campaign, RequiredString(a, "encounter_id")),
                "resolve_opportunity_attack" => engine.ResolveOpportunityAttack(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "reactor_combatant_id"), OptionalString(a, "attack_name"), dice),
                "decline_opportunity_attack" => engine.DeclineOpportunityAttack(campaign, RequiredString(a, "encounter_id"), RequiredString(a, "reactor_combatant_id")),
                "combat_attack" => CombatAttack(campaign, a),
                "next_combat_turn" => NextCombatTurn(campaign, RequiredString(a, "encounter_id")),
                "end_encounter" => engine.EndEncounter(campaign, RequiredString(a, "encounter_id")),
                "list_quests" => campaign.Quests.Where(q => !q.DmOnly).Select(q => new { q.Id, q.Key, q.Name, q.Status, q.Summary, q.RewardGp, q.Objectives, q.SourceKind, generated_details = campaign.Supplements.Where(s => !s.DmOnly && s.TargetKey.Equals(q.Key, StringComparison.OrdinalIgnoreCase)).Select(s => new { s.Category, s.Content, s.SourceKind }).ToArray() }).ToArray(),
                "set_quest_status" => SetQuestStatus(campaign, RequiredString(a, "quest_id"), RequiredString(a, "status")),
                "list_merchants" => ListMerchants(campaign),
                "purchase_item" => engine.Purchase(campaign, RequiredString(a, "character_id"), RequiredString(a, "merchant_id"), RequiredString(a, "item_id"), OptionalInt(a, "quantity", 1)),
                "recent_events" => campaign.Events.Where(e => !e.DmOnly).TakeLast(Math.Clamp(OptionalInt(a, "limit", 10), 1, 50)).ToArray(),
                _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
            };
            return new DmToolResult(true, result);
        }
        catch (KeyNotFoundException ex) { return new DmToolResult(false, Error: ex.Message, ErrorCode: "NOT_FOUND"); }
        catch (ArgumentException ex) { return new DmToolResult(false, Error: ex.Message, ErrorCode: "INVALID_ARGUMENT"); }
        catch (InvalidOperationException ex) { return new DmToolResult(false, Error: ex.Message, ErrorCode: "RULE_REJECTED"); }
        catch (Exception ex) { return new DmToolResult(false, Error: ex.Message, ErrorCode: "ENGINE_ERROR"); }
    }

    public object ToOpenAiToolSchema() => Definitions.Select(d => new
    {
        type = "function",
        function = new { name = d.Name, description = d.Description, parameters = d.Parameters }
    }).ToArray();

    private static object ListAvailableEncounters(CampaignState campaign)
    {
        return campaign.Encounters
            .Where(e => e.Status.Equals("planned", StringComparison.OrdinalIgnoreCase) && (e.LocationId is null || e.LocationId == campaign.PartyLocationId))
            .Select(e => new
            {
                e.Id,
                e.Key,
                e.Name,
                e.Summary,
                e.LocationId,
                e.DmOnly,
                e.SourceKind,
                generated_details = campaign.Supplements.Where(s => s.TargetKey.Equals(e.Key, StringComparison.OrdinalIgnoreCase)).Select(s => new { s.Category, s.Content, s.SourceKind }).ToArray(),
                members = e.Combatants.Select(c =>
                {
                    var character = campaign.Characters.FirstOrDefault(x => x.Id == c.CharacterId);
                    return new { combatant_id = c.Id, character_id = c.CharacterId, name = character?.Name ?? "Unknown", character_type = character?.CharacterType ?? "unknown", c.Positioned, c.GridX, c.GridY, c.MovementRemainingFeet, speed = character is null ? 0 : CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects) };
                }).ToArray()
            }).ToArray();
    }

    private object ActivateEncounter(CampaignState campaign, string encounterId, bool includeParty)
    {
        var encounter = engine.ActivateEncounter(campaign, encounterId, includeParty);
        return EncounterView(campaign, encounter);
    }

    private object? GetActiveEncounter(CampaignState campaign)
    {
        var encounter = campaign.Encounters.LastOrDefault(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
        return encounter is null ? null : EncounterView(campaign, encounter);
    }

    private object StartEncounter(CampaignState campaign, string name, bool includeParty)
    {
        var encounter = engine.StartEncounter(campaign, name);
        if (includeParty)
        {
            foreach (var pc in campaign.Characters.Where(c => c.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) && !c.Dead))
                engine.AddCombatant(campaign, encounter.Id, pc.Id);
        }
        return EncounterView(campaign, encounter);
    }

    private object RollInitiative(CampaignState campaign, string encounterId)
    {
        return engine.BeginInitiativeSequence(campaign, encounterId, dice);
    }

    private object NextCombatTurn(CampaignState campaign, string encounterId)
    {
        var combatant = engine.NextTurn(campaign, encounterId, dice);
        var encounter = campaign.Encounters.First(e => e.Id == encounterId);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        return new { encounter.Round, encounter.TurnIndex, combatant_id = combatant.Id, character_id = character.Id, character.Name };
    }

    private static object EncounterView(CampaignState campaign, EncounterState encounter)
    {
        var current = encounter.Combatants.Count > 0 && encounter.TurnIndex >= 0 && encounter.TurnIndex < encounter.Combatants.Count
            ? encounter.Combatants[encounter.TurnIndex]
            : null;
        return new
        {
            encounter.Id,
            encounter.Name,
            encounter.Status,
            encounter.LocationId,
            encounter.Round,
            encounter.TurnIndex,
            current_combatant_id = current?.Id,
            combatants = encounter.Combatants.Select(c =>
            {
                var character = campaign.Characters.FirstOrDefault(x => x.Id == c.CharacterId);
                return new
                {
                    c.Id,
                    c.CharacterId,
                    name = character?.Name ?? "Unknown",
                    character_type = character?.CharacterType ?? "unknown",
                    armor_class = character?.ArmorClass ?? 0,
                    current_hp = character?.CurrentHp ?? 0,
                    max_hp = character?.MaxHp ?? 0,
                    temp_hp = character?.TempHp ?? 0,
                    dead = character?.Dead ?? false,
                    size = character?.Size ?? "Medium",
                    free_hands = character?.FreeHands ?? 0,
                    conditions = character is null ? Array.Empty<string>() : character.Conditions.ToArray(),
                    c.Initiative,
                    c.Surprised,
                    c.Positioned,
                    c.GridX,
                    c.GridY,
                    c.MovementRemainingFeet,
                    c.Side,
                    c.ActionAvailable,
                    c.BonusActionAvailable,
                    c.AttackActionInProgress,
                    c.AttacksRemainingInAction,
                    c.ReactionAvailable,
                    c.Disengaging,
                    c.Dodging,
                    c.IsHidden,
                    c.HideCheckTotal,
                    readied_action = c.ReadiedAction is null ? null : new { c.ReadiedAction.Kind, c.ReadiedAction.Trigger, c.ReadiedAction.TargetCombatantId, c.ReadiedAction.AttackName, c.ReadiedAction.SpellId, c.ReadiedAction.CastAtLevel, c.ReadiedAction.UsedSpellSlot },
                    c.HelpAttackTargetCombatantId,
                    c.HelpAbilityTargetCharacterId,
                    c.HelpAbilityProficiency,
                    attacks = character is null
                        ? Array.Empty<object>()
                        : AvailableAttacks(character)
                };
            }).ToArray(),
            terrain = encounter.Terrain.Select(t => new { t.Id, t.Name, t.GridX, t.GridY, t.WidthSquares, t.HeightSquares, t.DifficultTerrain, t.BlocksMovement, t.BlocksLineOfSight, t.HeavilyObscured, t.Cover }).ToArray(),
            battlefield_effects = encounter.BattlefieldEffects.Select(BattlefieldEffectView).ToArray(),
            grapples = encounter.Grapples.Select(g => new { g.Id, g.GrapplerCombatantId, g.TargetCombatantId, g.EscapeDc, g.ReachFeet }).ToArray(),
            pending_move = encounter.PendingMove is null ? null : new
            {
                encounter.PendingMove.CombatantId,
                encounter.PendingMove.FromX,
                encounter.PendingMove.FromY,
                encounter.PendingMove.ToX,
                encounter.PendingMove.ToY,
                encounter.PendingMove.DistanceFeet,
                encounter.PendingMove.MovementCostFeet,
                opportunity_attacks = encounter.PendingMove.OpportunityAttacks.Where(x => !x.Resolved).Select(x => new { x.ReactorCombatantId, x.ReactorCharacterId, x.ReactorName, x.ReachFeet, x.TriggerX, x.TriggerY }).ToArray()
            }
        };
    }

    private static object ListBattlefieldEffects(CampaignState campaign, string encounterId)
    {
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Encounter not found.");
        return encounter.BattlefieldEffects.Select(BattlefieldEffectView).ToArray();
    }

    private static object BattlefieldEffectView(BattlefieldEffectState e) => new
    {
        e.Id, e.Name, e.SourceCharacterId, e.SourceSpellId, e.Shape, e.SizeFeet, e.OriginX, e.OriginY, e.Direction,
        e.Trigger, e.DamageExpression, e.DamageType, e.SaveAbility, e.SaveDc, e.HalfDamageOnSuccessfulSave, e.OncePerTurn,
        e.DifficultTerrain, e.HeavilyObscured, e.BlocksLineOfSight, e.RequiresSourceConcentration, e.ConcentrationName,
        e.DurationRounds, e.AppliedRound, e.ExpiresAfterRound, e.DmOnly, e.SourceKind
    };

    private object AbilityCheck(CampaignState campaign, JsonElement a)
    {
        var characterId = RequiredString(a, "character_id");
        var ability = RequiredString(a, "ability");
        var dc = RequiredInt(a, "dc");
        var mode = ParseRollMode(OptionalString(a, "roll_mode"));
        var skill = OptionalString(a, "skill");
        var circumstanceModifier = OptionalInt(a, "circumstance_modifier", 0);
        var character = RequireCharacter(campaign, characterId);
        return character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
            ? engine.RequestAbilityCheckRoll(campaign, character.Id, ability, dc, mode, skill, circumstanceModifier)
            : engine.ResolveAbilityCheckWithDice(campaign, character.Id, ability, dc, dice, mode, skill, circumstanceModifier);
    }

    private object SavingThrow(CampaignState campaign, JsonElement a)
    {
        var characterId = RequiredString(a, "character_id");
        var ability = RequiredString(a, "ability");
        var dc = RequiredInt(a, "dc");
        var requestedMode = ParseRollMode(OptionalString(a, "roll_mode"));
        var circumstanceModifier = OptionalInt(a, "circumstance_modifier", 0);
        var character = RequireCharacter(campaign, characterId);
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            return engine.ResolveSavingThrowWithDice(campaign, character.Id, ability, dc, dice, requestedMode, circumstanceModifier);

        var normalized = CharacterMechanics.NormalizeAbility(ability);
        if (CharacterMechanics.AutomaticallyFailsSavingThrow(character, normalized))
            return engine.ResolveSavingThrow(campaign, character.Id, normalized, dc, 1, null, requestedMode, circumstanceModifier);

        return engine.RequestSavingThrowRoll(campaign, character.Id, normalized, dc, requestedMode, circumstanceModifier);
    }

    private object DeathSave(CampaignState campaign, string characterId, string? encounterId)
    {
        var character = RequireCharacter(campaign, characterId);
        if (character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Player-character Death Saving Throws are player-controlled. Stop at that character's turn and let the player use Roll Death Save in the Game Table.");

        if (!string.IsNullOrWhiteSpace(encounterId))
        {
            var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase)
                || e.Key.Equals(encounterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Encounter '{encounterId}' was not found.");
            var combatant = encounter.Combatants.FirstOrDefault(c => c.CharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"{character.Name} is not a combatant in '{encounter.Name}'.");
            return engine.ResolveCombatDeathSavingThrow(campaign, encounter.Id, combatant.Id, dice);
        }

        return engine.ResolveDeathSavingThrowWithDice(campaign, character.Id, dice);
    }

    private object SpendHitDie(CampaignState campaign, string characterId)
    {
        var character = RequireCharacter(campaign, characterId);
        var roll = dice.Roll($"1d{Math.Max(2, character.HitDieSides)}").Total;
        var regained = engine.SpendHitDie(campaign, characterId, roll);
        return new { die_roll = roll, hp_regained = regained, character.CurrentHp, character.HitDiceRemaining };
    }

    private object GetInventory(CampaignState campaign, string characterId)
    {
        var c = RequireCharacter(campaign, characterId);
        return c.Inventory.Select(entry => new
        {
            item_id = entry.ItemId,
            item_name = campaign.Items.FirstOrDefault(i => i.Id == entry.ItemId)?.Name ?? "Unknown item",
            entry.Quantity,
            entry.Equipped
        }).ToArray();
    }

    private static object DmCharacter(CampaignState campaign, CharacterSheet c) => new
    {
        c.Id, c.Name, c.CharacterType, c.CreatureType, c.Level, c.ArmorClass, c.MaxHp, c.CurrentHp, c.TempHp, c.Gold, c.LocationId,
        c.PublicKnowledge, c.Abilities, c.Speed, c.Size, c.FreeHands, effective_speed = CharacterMechanics.EffectiveSpeed(c, campaign.ActiveEffects), c.ProficiencyBonus,
        c.Conditions, c.SkillProficiencies, c.ToolProficiencies, c.ExhaustionLevel, c.DeathSaveSuccesses, c.DeathSaveFailures, c.Stable, c.Dead,
        c.HitDiceRemaining, c.HitDiceMaximum, c.HitDieSides, c.ConcentrationEffect,
        c.SpellcastingAbility, c.PreparedSpellIds,
        spell_save_dc = 8 + CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(c, c.SpellcastingAbility)) + Math.Max(0, c.ProficiencyBonus),
        spell_attack_modifier = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(c, c.SpellcastingAbility)) + Math.Max(0, c.ProficiencyBonus),
        spell_slots = c.SpellSlots.ToDictionary(x => x.Key, x => new { x.Value.Remaining, x.Value.Maximum }),
        resources = c.Resources.Select(r => new { r.Name, r.Remaining, r.Maximum }).ToArray(),
        ongoing_effects = campaign.ActiveEffects.Where(e => e.TargetCharacterId.Equals(c.Id, StringComparison.OrdinalIgnoreCase)).Select(e => new { e.Name, e.Condition, e.SourceCharacterId, e.SourceSpellId, e.RepeatSaveAbility, e.SaveDc, e.RepeatSaveAtEndOfTurn, e.NextAttackAgainstTargetHasAdvantage, e.AttackRollBonusExpression, e.SavingThrowBonusExpression, e.SpeedModifierFeet, e.ArmorClassBonus, e.ExpireAtStartOfSourceNextTurn }).ToArray()
    };

    private object ListPreparedSpells(CampaignState campaign, string characterId)
    {
        var character = RequireCharacter(campaign, characterId);
        var ids = character.PreparedSpellIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new
        {
            character_id = character.Id,
            character_name = character.Name,
            spellcasting_ability = character.SpellcastingAbility,
            spell_save_dc = engine.SpellSaveDc(character),
            spell_attack_modifier = engine.SpellAttackModifier(character),
            spells = campaign.Spells
                .Where(s => ids.Contains(s.Id) || (!string.IsNullOrWhiteSpace(s.Key) && ids.Contains(s.Key)))
                .OrderBy(s => s.Level)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new
                {
                    s.Id, s.Key, s.Name, s.Level, s.School, s.CastingTime, s.RangeKind, s.RangeFeet,
                    s.RequiresVerbal, s.RequiresSomatic, s.RequiresMaterial, s.Duration, s.RequiresConcentration, s.Ritual,
                    s.RequiresTarget, s.Resolution, s.SaveAbility, s.DamageExpression, s.DamageType, s.HalfDamageOnSuccessfulSave,
                    s.HealingExpression, s.ExtraDamagePerSlotExpression, s.ExtraHealingPerSlotExpression, s.AddSpellcastingAbilityModifierToHealing,
                    s.CantripDamageScaling, s.CantripRangeDoubling, s.IgnoreHalfAndThreeQuartersCoverOnSave, s.RequiredTargetCreatureType,
                    s.ConditionOnFailedSave, s.RepeatSaveAtEndOfTurn, s.NextAttackAgainstTargetHasAdvantage, s.EffectExpiresAtEndOfCasterNextTurn,
                    s.EffectExpiresAtStartOfCasterNextTurn, s.SpeedModifierFeet, s.ArmorClassBonus, s.SaveDisadvantageCreatureType,
                    s.BaseProjectiles, s.ExtraProjectilesPerSlot, s.BaseTargets, s.ExtraTargetsPerSlot, s.AttackRollBonusExpression, s.SavingThrowBonusExpression,
                    s.AreaShape, s.AreaSizeFeet, s.ExtraAreaSizePerSlotFeet, s.AreaOrigin, s.PushFeetOnFailedSave, s.EnvironmentalEffect,
                    s.BattlefieldTrigger, s.BattlefieldDifficultTerrain, s.BattlefieldHeavilyObscured, s.BattlefieldBlocksLineOfSight, s.BattlefieldDurationRounds,
                    s.RequiresVisibleTarget, s.SourceKind
                }).ToArray()
        };
    }

    private static object PlayerLocation(WorldLocation l) => new
    {
        l.Id, l.Key, l.Name, l.Type, l.Description, l.ParentId, l.X, l.Y, l.Discovered
    };

    private object MoveParty(CampaignState c, string locationId)
    {
        engine.MoveParty(c, locationId);
        return new { c.PartyLocationId, time = GameEngine.FormatCampaignTime(c) };
    }

    private object AdvanceTime(CampaignState c, int minutes)
    {
        engine.AdvanceTime(c, minutes);
        return new { c.Day, c.MinuteOfDay, formatted = GameEngine.FormatCampaignTime(c) };
    }

    private static object SetQuestStatus(CampaignState campaign, string id, string status)
    {
        var quest = campaign.Quests.FirstOrDefault(q => q.Id == id && !q.DmOnly) ?? throw new KeyNotFoundException("Player-visible quest not found.");
        quest.Status = status.Trim();
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaign.Events.Add(new CampaignEvent { Type = "quest_status", Summary = $"Quest '{quest.Name}' changed to {quest.Status}." });
        return new { quest.Id, quest.Name, quest.Status };
    }

    private static object ListMerchants(CampaignState campaign)
    {
        var visibleLocationIds = campaign.Locations.Where(l => l.Discovered && !l.DmOnly).Select(l => l.Id).ToHashSet();
        return campaign.Merchants.Where(m => m.LocationId is null || visibleLocationIds.Contains(m.LocationId)).Select(m => new
        {
            m.Id, m.Key, m.Name, m.LocationId, m.SourceKind,
            generated_details = campaign.Supplements.Where(s => !s.DmOnly && s.TargetKey.Equals(m.Key, StringComparison.OrdinalIgnoreCase)).Select(s => new { s.Category, s.Content, s.SourceKind }).ToArray(),
            stock = m.Stock.Select(s => new
            {
                s.ItemId,
                item_name = campaign.Items.FirstOrDefault(i => i.Id == s.ItemId)?.Name ?? "Unknown item",
                s.Quantity,
                s.SourceKind,
                price_gp = s.PriceGp ?? campaign.Items.FirstOrDefault(i => i.Id == s.ItemId)?.PriceGp ?? 0
            }).ToArray()
        }).ToArray();
    }


    private object CombatAttack(CampaignState campaign, JsonElement a)
    {
        var encounterId = RequiredString(a, "encounter_id");
        var attackerCombatantId = RequiredString(a, "attacker_combatant_id");
        var targetCombatantId = RequiredString(a, "target_combatant_id");
        var attackName = OptionalString(a, "attack_name");
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Id.Equals(encounterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Encounter not found.");
        var attackerCombatant = encounter.Combatants.FirstOrDefault(c => c.Id.Equals(attackerCombatantId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Attacker combatant not found.");
        var attacker = RequireCharacter(campaign, attackerCombatant.CharacterId);
        return attacker.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
            ? engine.RequestEncounterAttackRoll(campaign, encounterId, attackerCombatantId, targetCombatantId, attackName)
            : engine.ResolveEncounterAttack(campaign, encounterId, attackerCombatantId, targetCombatantId, attackName, dice);
    }

    private static object[] AvailableAttacks(CharacterSheet character)
    {
        IEnumerable<AttackProfile> attacks = character.Attacks.Count == 0
            ? new[] { CharacterMechanics.UnarmedStrikeProfile(character) }
            : character.Attacks;
        return attacks.Select(a => (object)new { a.Name, a.AttackBonus, a.DamageExpression, a.DamageType, a.ReachFeet, a.RangeFeet }).ToArray();
    }

    private static CharacterSheet RequireCharacter(CampaignState campaign, string id) =>
        campaign.Characters.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("Character not found.");

    private static WorldLocation RequireVisibleLocation(CampaignState campaign, string id) =>
        campaign.Locations.FirstOrDefault(l => l.Id == id && l.Discovered && !l.DmOnly) ?? throw new KeyNotFoundException("Visible location not found.");

    private static D20RollMode CombineRollModes(D20RollMode left, D20RollMode right)
    {
        if (right == D20RollMode.Normal) return left;
        if (left == D20RollMode.Normal) return right;
        return left == right ? left : D20RollMode.Normal;
    }

    private static D20RollMode ParseRollMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
    {
        "advantage" or "adv" => D20RollMode.Advantage,
        "disadvantage" or "dis" => D20RollMode.Disadvantage,
        "normal" or "" => D20RollMode.Normal,
        _ => throw new ArgumentException("roll_mode must be normal, advantage, or disadvantage.")
    };

    private static string RequiredString(JsonElement a, string name) =>
        a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!
            : throw new ArgumentException($"Missing required argument '{name}'.");

    private static string? OptionalString(JsonElement a, string name) =>
        a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;

    private static IReadOnlyList<string> RequiredStringArray(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Missing required array '{name}'.");
        var values = p.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!)
            .ToArray();
        if (values.Length == 0) throw new ArgumentException($"Array '{name}' must contain at least one string value.");
        return values;
    }

    private static int RequiredInt(JsonElement a, string name) =>
        a.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : throw new ArgumentException($"Missing required integer '{name}'.");

    private static int OptionalInt(JsonElement a, string name, int fallback) =>
        a.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;

    private static int? OptionalNullableInt(JsonElement a, string name) =>
        a.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static bool OptionalBool(JsonElement a, string name, bool fallback) =>
        a.TryGetProperty(name, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False) ? p.GetBoolean() : fallback;

    private static DmToolDefinition Tool(string name, string description, object parameters) => new(name, description, parameters);

    private static object Props(params (string Name, string Type, bool Required)[] fields) => new
    {
        type = "object",
        properties = fields.ToDictionary(f => f.Name, f => f.Type == "array"
            ? (object)new { type = "array", items = new { type = "string" } }
            : new { type = f.Type }),
        required = fields.Where(f => f.Required).Select(f => f.Name).ToArray(),
        additionalProperties = false
    };
}
