# Dungeon Master AI - Native Windows Track

This folder contains the deterministic .NET 10 core of Dungeon Master AI.

`Domain`, `Engine` and `Data` are multi-targeted `net10.0;netstandard2.1`. That is not a
preference: Unity's scripting runtime is .NET Standard 2.1, and a `net10.0` assembly does not load
there at all. `net10.0` remains the target everything in this repository builds and tests against;
the netstandard2.1 leg exists so the engine can be dropped into a Unity project unchanged. `AI` is
deliberately not multi-targeted — it spawns a `llama-server` child process and speaks HTTP, and the
Unity side handles that separately. See the "Build status" section for how both legs are kept
honest, and `docs/unity-migration-plan.md` §1 and §5 for the mechanics.

## There is no front end

r63 deleted `DungeonMasterAI.App`, the WPF desktop application, in favour of a Unity front end that
has not been written yet. **Nothing here is runnable as a game.** There is no shell, no window, no
publish target and no installer. The design for the replacement is `docs/unity-target-architecture.md`
and the ordered plan is `docs/unity-migration-plan.md`.

The feature descriptions further down this file describe the **engine's** capabilities, which are
unchanged and still under test. Where they previously described a screen, a button or a view, that
surface no longer exists — only the deterministic behaviour behind it does. Read every "the app
does X" below as "the engine can do X, and nothing currently asks it to."

`docs/unity-target-architecture.md` §9 is the inventory of what the deleted WPF app did, surface by
surface, marked rebuild / drop / reshape. That inventory is why deleting the code did not delete the
knowledge of what it was for.

## Architecture

- `DungeonMasterAI.Domain`: campaign, character, combat, faction, secret, timeline, inventory, and application-state models.
- `DungeonMasterAI.Engine`: deterministic mechanics, campaign clock, combat, rules search, readiness-sensitive state changes, and the DM tool allow-list.
- `DungeonMasterAI.Data`: crash-safe local persistence, campaign import, source extraction, and campaign-readiness validation.
- `DungeonMasterAI.AI`: llama.cpp lifecycle, local DM client, chunked local-AI campaign canon extraction, and a separate AI playability-expansion stage.
- `content`: game content that used to live inside the WPF project — the SRD spell catalog, the built-in map asset pack manifest, the approved reference art. See `content/README.md`.
- `tests/`: 31 console-executable test projects. Each exits 0 on pass and prints every failure on stderr; there is no test runner to install.
- `tests/DungeonMasterAI.CrossTargetGoldenTests`: replays a fixed, seeded scenario through the engine and pins 155 recorded values to a committed golden file, so a behavioural difference between the `net10.0` and `netstandard2.1` engine builds shows up as a diff of actual numbers rather than as a suite that happens to stay green.
- `tests/Directory.Build.props` and `tests/Directory.Build.targets`: the differential-run wiring. Building any test project with `-p:UseNetStandardEngine=true` resolves its `Domain`/`Engine`/`Data` references against the netstandard2.1 output instead of `net10.0`, into a separate `bin`/`obj` so the two can never be confused for each other.
- `tests/DungeonMasterAI.Smoke`: broad framework-free native smoke-test executable, and the only suite that takes an argument (a campaign manifest path).
- `tests/DungeonMasterAI.RollTests`: focused independent roll-state regression tests that report all failures in one run.
- `tools/fetch-llama-runtime.ps1`: vendors the pinned, hash-verified llama.cpp CPU runtime into `runtime/llama-cpp`. Nothing consumes that directory yet; the front end that packaged it is gone.

The core rule remains: **the LLM tells the story; the application runs the game.** The local model never receives direct persistence access and can alter deterministic state only through allow-listed tools.

## Current native feature set

The engine adjudicates campaigns, live play, characters, spellcasting, combat, map exploration, world state, rules lookup, and local-AI settings. Until r63 a WPF shell drove all of it; that shell is gone and nothing drives it today. Campaign state includes HP, AC, Temporary HP, death saves, Exhaustion, rests, spell slots/resources, Concentration, prepared spells, inventory, gold, merchants/stock, quests, combat encounters, tactical positions, factions, relationships, secrets, and scheduled world events.

The import layer supports JSON, TXT/Markdown, PDF, and DOCX. Quick Import uses deterministic parsing and validation. **AI Compile File** is a local-only two-stage path: first it chunks a source document, sends each chunk to the local model with a strict non-invention canon-extraction policy, merges repeated entities, marks extracted records `source_canon`, and imports them deterministically. A second pass then examines readiness gaps and can add only separately provenance-marked `ai_expanded` content. Existing canon keys are protected from overwrite. Generated quest/clue/reveal details that would otherwise mutate canon are stored as attached supplements instead.

AI turns are transactional. Tool calls mutate a cloned campaign and are committed only after the local DM finishes the turn successfully. If the model or runtime fails, the last verified campaign state remains intact. Disk persistence also uses atomic replacement plus a previous-safe-state recovery copy.

The combat layer supports encounter activation, deterministic initiative, attacker/target selection, verified attacks, HP changes, Concentration visibility and automatic damage-triggered Constitution saves, 5-foot tactical-grid positioning and movement budgets, attack range/reach enforcement, round advancement, and encounter completion. Orthogonal and diagonal adjacent grid squares each cost 5 feet, matching the SRD grid rules. Creatures without a configured attack can still use the SRD-derived Unarmed Strike damage option; fixed damage expressions are supported without inventing damage dice.

The action layer now also supports Dash, Disengage, Dodge, Hide, Search-for-hidden, Ready attack/move/spell, attack-assist Help, proficiency-gated ability-check Help, first aid, Unarmed Strike Grapple and Shove, persistent Grappled/Prone state, grapple escape/release, crawling/standing, and the Search, Study, and Influence actions. Ability-check Help requires the helper to actually possess the chosen skill or tool proficiency and is consumed only by the matching ally check. First aid consumes the helper's Action and resolves the DC 10 Wisdom (Medicine) check in the deterministic engine. Hide enforces the DC 15 Dexterity (Stealth) rule plus tactical cover/obscurement and line of sight. Readied spells spend their slot when readied, require Concentration while held, use the Reaction when released, and dissipate if that Concentration is lost.

The spellcasting layer now owns prepared-spell validation, spell slots, cantrips, Ritual timing, components, spell attack modifiers, spell save DCs, attack/save/healing resolution modes, configured upcasting, Concentration, combat turn restrictions, tactical range checks, target-visibility requirements, multi-projectile targeting, multi-target Concentration buffs, and tactical area geometry. Magic Missile can allocate every dart to one creature or distribute one declared target per dart; Scorching Ray similarly resolves one independent spell attack per ray. Bless creates persistent per-target 1d4 attack/save bonuses that end with the caster's Concentration. Burning Hands, Thunderwave, and Fireball use shared battlefield geometry for cone/cube/sphere target selection, validate the area before spending a slot, trace area line of effect from the spell point of origin through Total Cover, roll one area damage total, resolve individual saves, and apply Thunderwave forced movement. The engine can enumerate an area spell's effective squares before the slot is spent, distinguishing them from squares blocked by Total Cover; the Battlefield view that presented this went with the WPF app. Persistent battlefield areas use the same geometry engine. Fog Cloud creates a Concentration-bound, Heavily Obscured sight-blocking sphere that grows when upcast. Spike Growth creates Concentration-bound Difficult Terrain and resolves 2d4 Piercing damage for every 5 feet a creature actually moves into or within the area. Combat movement is committed one grid step at a time, so a movement hazard can stop a creature on the exact square where it becomes unable to continue.

Condition-aware resolution now also applies Restrained Dexterity-save Disadvantage, automatic Strength/Dexterity save failure for Paralyzed/Stunned/Unconscious creatures, condition-driven attack Advantage/Disadvantage, and automatic close-range Critical Hits against Paralyzed or Unconscious targets. Active spell effects can now apply fixed AC and Speed modifiers without overwriting base character stats, can expire at the start of the source caster's next turn, and can modify every deterministic attack or movement calculation that depends on those values. Area-save metadata can also impose creature-type-specific Disadvantage, which is used by Shatter against Constructs. The app carries a compact local catalog of 316 SRD 5.2.1 spell metadata records generated from the licensed SRD. Twenty-six SRD spells currently have explicit deterministic implementations: Cure Wounds, Healing Word, Fire Bolt, Sacred Flame, Spare the Dying, Guiding Bolt, Hold Person, Magic Missile, Scorching Ray, Bless, Burning Hands, Thunderwave, Fireball, Fog Cloud, Spike Growth, Ray of Frost, Shatter, Shield of Faith, Lightning Bolt, Cone of Cold, Circle of Death, Inflict Wounds, Longstrider, Mass Cure Wounds, Mass Healing Word, and Heal. Area geometry now also covers Line-shaped areas, whose lateral width stays constant instead of widening with distance the way a Cone does, and Lightning Bolt is the first spell to use one. Multi-target healing rolls its dice once and restores that same total to each chosen creature, matching the SRD wording, and it is used by Mass Cure Wounds and Mass Healing Word. Heal applies flat healing with no dice and ends the Blinded, Deafened, and Poisoned conditions on its target. All other catalog entries remain deliberately `unsupported` until their mechanics are explicitly implemented and tested; the engine refuses to guess their effects. Campaign-authored or test-fixture spells can use the supported deterministic resolution modes now.

Generated playability content now has provenance across locations, connections, NPCs, items, merchants and stock, quests, factions, relationships, secrets, timeline events, encounters, and attached supplements. Generated supplements stay separable from canon in the model, so a front end can present them apart; the World State screen that did so went with the WPF app.

Campaign Rehearsal provides a deterministic pre-play pass over travel reachability, orphaned public locations, merchant and encounter readiness, duplicate stable keys, unrevealed-secret leakage, overdue timeline events, and generated-detail references. It reports findings without asking the LLM to decide whether game state is valid.

Campaign time automatically resolves imported time-triggered world events. Their consequences are recorded as DM-only history and can update linked quest DM notes without leaking the event to player-safe context.

## Build status

`.github/workflows/windows-ci.yml` has four jobs. `source-validation` runs
`tools/validate_source.py` on Linux (MSBuild XML, C# delimiter/lexical structure, duplicate DM tool
names, drift in the deterministic SRD spell catalog). `engine-tests` then builds every project and
runs every test project under `tests/` on `windows-latest`, reporting all failing suites in one run
rather than stopping at the first. Both the build list and the test list are discovered, not
hand-maintained, so a new project cannot be silently skipped — which is exactly how
`RuntimeProvisioningTests` came to be absent from CI before r63.

The two remaining jobs exist because the engine now has to stay loadable in Unity:

- `netstandard21-build` (Linux) builds `Domain`, `Engine` and `Data` for `netstandard2.1` with
  `-warnaserror`, then reads each emitted DLL's own `TargetFrameworkAttribute` back to confirm the
  output is what Unity needs rather than trusting that the build was green. A change that compiles
  on `net10.0` but not on `netstandard2.1` fails on the pull request, not on the day somebody opens
  Unity.
- `netstandard21-differential` (Windows) builds and runs every test project that does not reference
  the `net10.0`-only `DungeonMasterAI.AI` **twice** — once against each engine build — and compares
  the two runs' stdout byte for byte. Same assertions, same printed values, two different compiled
  engines underneath. This is the job that catches the failure mode a green build cannot: an engine
  that compiles on both targets and quietly adjudicates differently on one.

**What CI stopped proving in r63, and nothing replaces:** the GUI binding-failure gate, every
rendered PNG reference, the Map Builder's editing guarantees, and the win-x64 publish and installer.
The workflow file names each loss at the top; `docs/unity-migration-plan.md` §8.3 explains why the
binding gate in particular has no stand-in.

`tools/validate_source.py` is still not a substitute for a real compiler pass.

The Python reference implementation remains under `reference-python/` as the tested behavior oracle while native functionality is ported.

### Player-controlled Death Saving Throws

During active combat, a player character that starts its turn at 0 HP creates a persisted `PendingPlayerRoll` request and pauses the encounter for a player-controlled d20 Death Saving Throw. A front end satisfies that pending request by passing the exact d20 result the player saw into the deterministic engine; the WPF `Roll d20` and Death Save buttons that did so are gone, and the pause is currently unsatisfiable because nothing is driving the engine. The engine enforces one Death Save per turn and will not advance combat past an unresolved required save. The local DM cannot roll a PC's Death Save on the player's behalf. If the local model tries to finish narration while a non-player combatant still owns the authoritative turn, the AI client rejects that narration and requires the model to keep using deterministic combat tools instead of pretending the player turn has begun.

