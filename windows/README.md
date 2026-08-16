# Dungeon Master AI - Native Windows Track

This folder contains the native Windows implementation intended to become the distributable desktop application.

## Architecture

- `DungeonMasterAI.Domain`: campaign, character, combat, faction, secret, timeline, inventory, and application-state models.
- `DungeonMasterAI.Engine`: deterministic mechanics, campaign clock, combat, rules search, readiness-sensitive state changes, and the DM tool allow-list.
- `DungeonMasterAI.Data`: crash-safe local persistence, campaign import, source extraction, and campaign-readiness validation.
- `DungeonMasterAI.AI`: llama.cpp lifecycle, local DM client, chunked local-AI campaign canon extraction, and a separate AI playability-expansion stage.
- `DungeonMasterAI.App`: WPF desktop interface.
- `tests/DungeonMasterAI.Smoke`: broad framework-free native smoke-test executable.
- `tests/DungeonMasterAI.RollTests`: focused independent roll-state regression tests that report all failures in one run.
- `installer`: Inno Setup definition for the Windows test installer.

The core rule remains: **the LLM tells the story; the application runs the game.** The local model never receives direct persistence access and can alter deterministic state only through allow-listed tools.

## Current native feature set

The Windows source now contains a desktop shell for campaigns, live play, characters, spellcasting, combat, map exploration, world state, rules, and local-AI settings. Campaign state includes HP, AC, Temporary HP, death saves, Exhaustion, rests, spell slots/resources, Concentration, prepared spells, inventory, gold, merchants/stock, quests, combat encounters, tactical positions, factions, relationships, secrets, and scheduled world events.

The import layer supports JSON, TXT/Markdown, PDF, and DOCX. Quick Import uses deterministic parsing and validation. **AI Compile File** is a local-only two-stage path: first it chunks a source document, sends each chunk to the local model with a strict non-invention canon-extraction policy, merges repeated entities, marks extracted records `source_canon`, and imports them deterministically. A second pass then examines readiness gaps and can add only separately provenance-marked `ai_expanded` content. Existing canon keys are protected from overwrite. Generated quest/clue/reveal details that would otherwise mutate canon are stored as attached supplements instead.

AI turns are transactional. Tool calls mutate a cloned campaign and are committed only after the local DM finishes the turn successfully. If the model or runtime fails, the last verified campaign state remains intact. Disk persistence also uses atomic replacement plus a previous-safe-state recovery copy.

The combat UI supports encounter activation, deterministic initiative, attacker/target selection, verified attacks, HP changes, Concentration visibility and automatic damage-triggered Constitution saves, 5-foot tactical-grid positioning and movement budgets, attack range/reach enforcement, round advancement, and encounter completion. Orthogonal and diagonal adjacent grid squares each cost 5 feet, matching the SRD grid rules. Creatures without a configured attack can still use the SRD-derived Unarmed Strike damage option; fixed damage expressions are supported without inventing damage dice.

The action layer now also supports Dash, Disengage, Dodge, Hide, Search-for-hidden, Ready attack/move/spell, attack-assist Help, proficiency-gated ability-check Help, first aid, Unarmed Strike Grapple and Shove, persistent Grappled/Prone state, grapple escape/release, crawling/standing, and the Search, Study, and Influence actions. Ability-check Help requires the helper to actually possess the chosen skill or tool proficiency and is consumed only by the matching ally check. First aid consumes the helper's Action and resolves the DC 10 Wisdom (Medicine) check in the deterministic engine. Hide enforces the DC 15 Dexterity (Stealth) rule plus tactical cover/obscurement and line of sight. Readied spells spend their slot when readied, require Concentration while held, use the Reaction when released, and dissipate if that Concentration is lost.

The spellcasting layer now owns prepared-spell validation, spell slots, cantrips, Ritual timing, components, spell attack modifiers, spell save DCs, attack/save/healing resolution modes, configured upcasting, Concentration, combat turn restrictions, tactical range checks, target-visibility requirements, multi-projectile targeting, multi-target Concentration buffs, and tactical area geometry. Magic Missile can allocate every dart to one creature or distribute one declared target per dart; Scorching Ray similarly resolves one independent spell attack per ray. Bless creates persistent per-target 1d4 attack/save bonuses that end with the caster's Concentration. Burning Hands, Thunderwave, and Fireball use shared battlefield geometry for cone/cube/sphere target selection, validate the area before spending a slot, trace area line of effect from the spell point of origin through Total Cover, roll one area damage total, resolve individual saves, and apply Thunderwave forced movement. The Battlefield view previews the selected area spell before casting and distinguishes effective squares from squares blocked by Total Cover. Persistent battlefield areas use the same geometry engine. Fog Cloud creates a Concentration-bound, Heavily Obscured sight-blocking sphere that grows when upcast. Spike Growth creates Concentration-bound Difficult Terrain and resolves 2d4 Piercing damage for every 5 feet a creature actually moves into or within the area. Combat movement is committed one grid step at a time, so a movement hazard can stop a creature on the exact square where it becomes unable to continue.

Condition-aware resolution now also applies Restrained Dexterity-save Disadvantage, automatic Strength/Dexterity save failure for Paralyzed/Stunned/Unconscious creatures, condition-driven attack Advantage/Disadvantage, and automatic close-range Critical Hits against Paralyzed or Unconscious targets. Active spell effects can now apply fixed AC and Speed modifiers without overwriting base character stats, can expire at the start of the source caster's next turn, and can modify every deterministic attack or movement calculation that depends on those values. Area-save metadata can also impose creature-type-specific Disadvantage, which is used by Shatter against Constructs. The app carries a compact local catalog of 316 SRD 5.2.1 spell metadata records generated from the licensed SRD. Eighteen SRD spells currently have explicit deterministic implementations: Cure Wounds, Healing Word, Fire Bolt, Sacred Flame, Spare the Dying, Guiding Bolt, Hold Person, Magic Missile, Scorching Ray, Bless, Burning Hands, Thunderwave, Fireball, Fog Cloud, Spike Growth, Ray of Frost, Shatter, and Shield of Faith. All other catalog entries remain deliberately `unsupported` until their mechanics are explicitly implemented and tested; the engine refuses to guess their effects. Campaign-authored or test-fixture spells can use the supported deterministic resolution modes now.

Generated playability content now has provenance across locations, connections, NPCs, items, merchants and stock, quests, factions, relationships, secrets, timeline events, encounters, and attached supplements. The World State screen exposes generated supplements separately from canon.

Campaign Rehearsal provides a deterministic pre-play pass over travel reachability, orphaned public locations, merchant and encounter readiness, duplicate stable keys, unrevealed-secret leakage, overdue timeline events, and generated-detail references. It reports findings without asking the LLM to decide whether game state is valid.

Campaign time automatically resolves imported time-triggered world events. Their consequences are recorded as DM-only history and can update linked quest DM notes without leaking the event to player-safe context.

## Build status

The repository root contains a Windows .NET 10 GitHub Actions workflow that restores, builds, runs the focused roll-state tests and the broad smoke executable, publishes a self-contained win-x64 app, and then builds an Inno Setup installer artifact.

This execution environment does not currently contain the .NET SDK. A direct attempt to fetch the official .NET 10.0.400 Linux x64 SDK on 2026-08-15 failed because the container could not resolve the Microsoft build host, so the native track has not yet received a real compiler pass here. `tools/validate_source.py` now makes those source-level checks reproducible (XML, C# delimiter/lexical structure, duplicate DM tools, and WPF Command bindings). These checks are still not a substitute for a real compiler pass.

The Python reference implementation remains under `reference-python/` as the tested behavior oracle while native functionality is ported.

### Player-controlled Death Saving Throws

During active combat, a player character that starts its turn at 0 HP now creates a persisted `PendingPlayerRoll` request and pauses the Game Table for a player-controlled d20 Death Saving Throw. The large `Roll d20` button and the dedicated Death Save button both satisfy that same pending request, and the exact d20 result shown in the UI is passed into the deterministic engine. The engine enforces one Death Save per turn and will not advance combat past an unresolved required save. The local DM cannot roll a PC's Death Save on the player's behalf. If the local model tries to finish narration while a non-player combatant still owns the authoritative turn, the AI client rejects that narration and requires the model to keep using deterministic combat tools instead of pretending the player turn has begun.

