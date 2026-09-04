# Changelog

## r63 — engine multi-targeted to .NET Standard 2.1 so Unity can load it

- `DungeonMasterAI.Domain`, `.Engine` and `.Data` now build for `net10.0;netstandard2.1`. Unity's
  scripting runtime is .NET Standard 2.1 and will not load a `net10.0` assembly at all, so this was
  the last thing standing between this repository and a machine with Unity on it.
  `DungeonMasterAI.AI` stays `net10.0`-only by design.
- Remediated the six things netstandard2.1 lacks, verified by compiling rather than grepping:
  **145** `ArgumentNullException.ThrowIfNull` call sites rewritten to a `Guard.NotNull` helper
  (Domain 5, Engine 134, Data 6 — all of them the bare-identifier form, so `ParamName` is
  unchanged); an `IsExternalInit` shim per assembly for the **42** `record` declarations (40
  records plus 2 `readonly record struct`s: Domain 28, Engine 8, Data 6); **3**
  `StringSplitOptions.TrimEntries` sites; `Random.Shared` in `DiceService`; **2**
  `[GeneratedRegex]` sites; and one `StreamReader.ReadLineAsync(CancellationToken)`.
- **`DiceService` was deliberately not forked.** The default dice source is now one `[ThreadStatic]`
  `Random` on both targets rather than `Random.Shared` on one and a shim on the other — dice are the
  heart of adjudication and a permanently forked implementation there is exactly where a silent
  behavioural difference would do the most damage. The two regexes keep their pattern and options in
  shared `const` fields, so the `[GeneratedRegex]` and hand-built branches cannot drift apart.
- `PdfPig` is excluded from the netstandard2.1 leg. It would load there, but PDF import is a
  once-per-campaign authoring action the running game never performs. `ExtractPdf` is the single
  fenced call site; on that leg it throws a clear `NotSupportedException`.
- Added `tests/DungeonMasterAI.CrossTargetGoldenTests`: a seeded replay that pins 155 recorded
  engine values — the dice stream, the dice-expression grammar, guard `ParamName`s, XP and
  levelling, combat, death saves, and full campaign round-trips through each assembly's own
  `System.Text.Json` — to a committed golden file. It passes against both engine builds, byte for
  byte, against the same file.
- Added the differential run: any test project built with `-p:UseNetStandardEngine=true` resolves
  its engine references against the netstandard2.1 output. A module initializer fails the process
  before any test runs if the loaded engine is not really netstandard2.1, so a silently no-op
  retarget cannot pass.
- CI gained `netstandard21-build` (Linux, `-warnaserror`, and it reads each DLL's
  `TargetFrameworkAttribute` back) and `netstandard21-differential` (Windows; runs all 28 portable
  test projects against both engine builds and compares stdout byte for byte).
- **What this does not prove:** nothing here has been inside Unity. The netstandard2.1 assemblies
  are verified loaded and executed by a .NET 10 host, not by Unity's Mono runtime, and the
  `System.Text.Json` packaging question (`docs/unity-migration-plan.md` §3) is untouched and still
  open.

## r63 — WPF front end removed

- Rescued 51 engine assertions out of the three WPF-coupled test projects that had extractable
  engine content, verbatim: 39 from `R62MapCombatTests` (retargeted to `net10.0`), 10 from
  `MapRendererTests` (renamed `MapGeometryTests`, since the renderer is what left), 2 from
  `MapAssetTests` (reduced to its `TacticalMapAssetPackValidator` checks).
- Deleted `windows/src/DungeonMasterAI.App` (61 files) and the three test projects with no
  extractable engine content: `GuiSmokeTests`, `R56MapBuilderTests`, `R57MapEditingTests`.
  72 presentation-only assertions removed in total.
- **Coverage genuinely lost, not relocated:** the GUI binding-failure gate, every rendered PNG
  reference, the Map Builder's editing guarantees, and the asset catalog's deterministic seeded
  variant selection. Their requirements survive as Unity acceptance criteria in
  `docs/unity-migration-plan.md` §7.2 and §8.3. Nothing was invented to stand in for them.
- Relocated content out of the deleted project into `windows/content/`: the SRD spell catalog,
  the `core.fantasy.crypt` map pack manifest, and the three approved reference images.
- Removed `build-windows.ps1`, `WINDOWS_TEST.ps1` and the Inno Setup installer. There is no
  application to publish or package until Unity produces one.
- Reworked `windows-ci.yml` to two jobs, source validation and engine tests, with both the build
  list and the test list discovered rather than hand-maintained. This added
  `RuntimeProvisioningTests` to CI, which had never been in it.
- **There is no runnable front end.** See `START_HERE.txt`.

## r23 candidate
- Added authoritative pending player-roll state for required rolls.
- Routed the Game Table Roll d20 button into an active Death Saving Throw instead of producing an orphan roll.
- Added an explicit-roll combat death-save engine path so the exact UI d20 result is authoritative.
- Added an AI combat-state guard that refuses final narration while a non-player combatant still owns the authoritative turn.
- Split death-save engine code and player-roll view-model code into focused partial-class files.
- Added focused independent roll-state tests that report all failures in one run.
- Added state schema migration infrastructure and schema version 2.
- Added Windows GitHub Actions build/test/publish/installer workflow and corrected installer artifact paths.

## Historical revision notes

### WINDOWS_BUILD_FIX_R3.txt

```text
Dungeon Master AI Windows test revision r3

Based on Windows test run 2026-08-15 21:15 local time.

Confirmed:
- .NET SDK 10.0.400 installed and working.
- NuGet restore succeeds for the app and smoke-test projects.
- The native C# compiler is now being exercised successfully.

Compiler fixes in r3:
1. DmToolRouter.cs CS0411: list_characters now passes campaign explicitly through a lambda to DmCharacter.
2. GameEngine.cs CS0103: IsDodgeActive now receives CampaignState explicitly so temporary speed effects can be evaluated.
3. Spellcasting.cs CS0136: projectile attack tuple locals were renamed to avoid shadowing damage/summary locals.
4. Nullability cleanup from the same compiler pass:
   - DiceService now returns raw.Trim() rather than dereferencing the nullable input expression.
   - spell resolution comparison uses string.Equals.
   - Influence passes its normalized non-null skill name.

Run:
  powershell -ExecutionPolicy Bypass -File .\windows\WINDOWS_TEST.ps1

Upload the generated DungeonMasterAI-TestResults-*.zip whether the run passes or fails.
```

### WINDOWS_BUILD_FIX_R4.txt

```text
Revision 4 fixes Windows compiler errors found in test run 20260815-211759:
- Spellcasting.cs: pass CampaignState into IsDodgeActive for area-save spells.
- Spellcasting.cs: pass CampaignState into IsDodgeActive for single-target save spells.
- DmToolRouter.cs: make tool result nullable to match get_active_encounter semantics and remove CS8600.
```

### WINDOWS_BUILD_FIX_R5.txt

```text
Windows build fix r5
====================
Based on the 2026-08-15 21:19:48 Windows compiler report.

Fixed MainViewModel.cs compiler issues:
- corrected SelectedSelectedCampaign typo to SelectedCampaign
- added System.IO for Path, File, and InvalidDataException
- added System.Net.Http for HttpRequestException
- removed nullable dereference warning in SelectedCharacterEffectiveSpeed by passing SelectedCampaign?.ActiveEffects

The previous Windows run confirmed Domain, Data, Engine, and AI projects compiled successfully before the WPF App project reached these errors.
Run windows\\WINDOWS_TEST.ps1 and upload the generated results ZIP whether it passes or fails.
```

### WINDOWS_SMOKE_FIX_R7.txt

```text
DungeonMasterAI Windows Test Source r7

Fixes after the 2026-08-15 21:23 Windows smoke-test run:

- The native Windows application itself already built successfully with 0 warnings and 0 errors.
- The smoke suite reached runtime execution and exposed a test-fixture problem.
- AddCharacter intentionally initializes a character whose CurrentHp is <= 0 to MaxHp, so two smoke fixtures that attempted to create already-dying characters directly were not actually at 0 HP.
- Updated both stabilization fixtures to create healthy PCs and reduce them to 0 HP through the normal deterministic damage engine before testing Stabilizing Cantrip and First Aid.
- Added explicit setup assertions confirming those targets are living, Unconscious creatures at 0 HP.

Run:
  powershell -ExecutionPolicy Bypass -File .\windows\WINDOWS_TEST.ps1

Upload the generated DungeonMasterAI-TestResults-*.zip whether the run passes or fails.
```

### WINDOWS_SMOKE_FIX_R8.txt

```text
Windows smoke-test revision r8

The r7 Windows run compiled the complete native application successfully with 0 warnings and 0 errors and executed 127 smoke assertions before stopping at the level-5 cantrip scaling test.

Cause: the cantrip scaling assertion used a Dexterity saving-throw spell against a normal target, so the random saving throw could succeed and correctly prevent damage. The test was therefore nondeterministic even though the scaling code itself rolls the configured fixed base damage twice at level 5.

Fix: the dedicated scaling-test target is now Paralyzed before the cast. The engine's existing condition rules automatically fail Dexterity saving throws for a Paralyzed target, allowing the assertion to isolate and verify only cantrip damage scaling.
```

### WINDOWS_SMOKE_FIX_R9.txt

```text
Windows smoke-test revision 9

Observed on the real Windows/.NET 10 runner:
- Native application build: PASS, 0 warnings, 0 errors.
- Smoke execution reached the spell turn/action-economy section.
- Failure: the shared Spell Tester fixture had already consumed all level-1 slots in earlier independent spell tests, so the test intended to prove that the one-slotted-spell-per-turn restriction resets on the next turn could not cast its next-turn spell.

Fix:
- The combat action-economy test now explicitly restores at least two level-1 slots before the encounter starts.
- This keeps the test independent from earlier fixture consumption: one slot is spent on the first turn and one remains to verify casting succeeds after NextTurn.
- No production game-engine behavior was changed for this fix.
```

### windows/GUI_DIAGNOSTICS.txt

```text
Revision 13 GUI startup diagnostics

Run:
  powershell -ExecutionPolicy Bypass -File .\windows\WINDOWS_TEST.ps1 -LaunchApp

The application now writes startup diagnostics to:
  %LOCALAPPDATA%\DungeonMasterAI\Logs\startup.log

The Windows test harness copies that log into the generated results ZIP as:
  07-gui-startup.log

If the GUI process exits within five seconds, the harness also captures recent .NET Runtime,
Application Error, and Windows Error Reporting events into:
  08-gui-windows-events.txt

Unhandled WPF/UI startup failures now also display an error dialog instead of silently terminating.
```

### windows/TEST_HARNESS_FIX.txt

```text
Windows test harness revision 2

This revision fixes Windows PowerShell 5.1 NativeCommandError handling so normal stderr output from dotnet is logged instead of terminating the test harness prematurely.

If a dotnet command truly fails, the results ZIP will now include the corresponding numbered log with the actual compiler/NuGet diagnostic and its exit code.
```

### windows/TEST_REVISION_10.txt

```text
Windows test revision 10

Changes in this revision:
- The native smoke suite now uses an injected deterministic dice source so random attack/save outcomes do not make the test harness flaky.
- The Opportunity Attack fixture now gives the Test Raider enough HP to guarantee it survives the earlier melee attack and remains eligible to react.
- Production DiceService still uses Random.Shared by default; deterministic rolls are used only when a caller explicitly injects a test roller.
```

### windows/TEST_REVISION_11.txt

```text
Windows test revision 11

Changes in this revision:
- Corrected the Ready-movement smoke assertion to match the combat state model: off-turn readied movement uses a separate Speed allowance and does not retain a normal-turn movement pool while it is not the creature's turn.
- Added an explicit follow-up assertion that the creature's normal movement budget and Reaction refresh at the start of its next turn.
- No production game-engine behavior was changed for this issue; the prior failure was an incorrect smoke-test expectation.
```

### windows/TEST_REVISION_12.txt

```text
Windows test revision 12

Changes in this revision:
- Corrected the shared Test Hero fixture to explicitly define Strength 16.
- This matches the fixture's existing Longsword +5 attack / +3 damage profile and the later Unarmed Strike expectation of +5 to hit and fixed 4 Bludgeoning damage.
- No production engine code changed for this failure; CharacterMechanics.UnarmedStrikeProfile was behaving correctly from the character's actual ability scores.
```

### windows/TEST_REVISION_14.txt

```text
Windows test revision 14

Fixes:
- Forces all display-only Run.Text bindings to Mode=OneWay. This fixes the WPF startup exception raised for read-only MainViewModel properties such as SpellImplementedCount.
- Creates windows\READY-TO-RUN-APP after publishing and launches the GUI from that copy. This prevents the running app from locking DLLs inside test-results\...\publish while the results ZIP is being created.
- Keeps startup diagnostics enabled.
```

### windows/TEST_REVISION_15.txt

```text
Windows test revision 15 - UI foundation redesign

Changes:
- Replaced the default WPF TabControl chrome with a true dark left navigation rail.
- Removed the white system-colored navigation gutter visible in r14.
- Added selected-page accent treatment and application branding in the navigation rail.
- Reworked global colors, cards, buttons, list selections, spacing, typography hierarchy, and status bar.
- Rebuilt the Dashboard into a command-center layout with a campaign hero, session snapshot, system metrics, active quests, and world events.
- Kept all existing ViewModel commands and deterministic game behavior unchanged.

This is the first visual-system pass, not final art direction. After validation on Windows, individual screens should be redesigned in the same system.
```

### windows/TEST_REVISION_16.txt

```text
Windows test revision 16

Changes:
- Local AI startup now shows live progress instead of appearing stuck at Starting model.
- Settings now displays recent llama.cpp runtime output.
- Added Stop Local AI.
- Dashboard button is now Start Local AI rather than developer-oriented Test Local AI.
- Dashboard Local AI card shows startup/download/load progress.
```

### windows/TEST_REVISION_18.txt

```text
Windows test revision 18

Fixes in this revision:
- Corrected a malformed newline-splitting string literal in MainViewModel.RefreshLocalAiRuntimeLog that prevented r17 from compiling.
- Retains the r17 local-AI request fixes: one leading system message, no replay of UI system notices, no duplicated current player message, and a real chat inference test.
```

### windows/TEST_REVISION_19.txt

```text
Windows test revision 19

Game Table redesign:
- Play Session renamed Game Table and rebuilt as the single live-play workspace.
- Conversation remains on the left while the right side is permanently visual.
- Exploration automatically shows the campaign/world map and current location/party state.
- Active combat automatically switches the entire right side to the tactical battlefield.
- Combat overlay includes current turn, target selection, Attack, and End Turn quick actions.
- Added Look Around, Talk, Continue, Attack Target, and End Turn live-play controls.
- Enter sends player input; Shift+Enter inserts a new line.
- Conversation auto-scrolls to the newest exchange.
- Display strips simple markdown bold/heading markers from session narration.
- Local DM prompt now resolves NPC turns autonomously, positions combatants when combat begins, and avoids dumping raw mechanics/tool transcripts into player-facing narration.
- Added DM busy state text during inference.
```

### windows/TEST_REVISION_20.txt

```text
Windows test revision 20

UI fixes:
- Game Table content now stretches across the full application workspace instead of leaving unused space on the right.
- The battlefield receives the intended majority of the Game Table width.
- Quick Combat labels, target selector, and NPC-turn status now use high-contrast text and controls.

No deterministic engine behavior was changed in this revision.
```

### windows/TEST_REVISION_21.txt

```text
Windows test revision 21

UI fixes:
- Fixed unreadable Quick Combat target dropdown text.
- Added an explicit dark ComboBoxItem template with high-contrast normal, hover, and selected states.
- Changed the Quick Combat selector itself to the dark application theme and widened it slightly for target names.
- The ComboBox item contrast fix applies throughout the app so other dropdowns do not repeat the same white-on-light problem.
```

### windows/TEST_REVISION_22.txt

```text
Windows test revision 22

Death Saving Throw interaction fixes:
- Player-character Death Saving Throws are now player-controlled from the Game Table.
- A PC only needs a Death Save if they START their turn at 0 HP, matching the 2024 rule timing.
- The tactical footer replaces normal combat controls with a prominent Roll Death Save panel when required.
- A combat Death Save can be rolled only once per turn.
- Combat cannot advance past an unresolved required Death Save.
- The AI is forbidden from rolling a player character's Death Save and must stop at that player decision point.
- NPC/non-player Death Saves remain available to the DM runtime.
- After an ordinary Death Save at 0 HP, normal action/movement controls remain unavailable while the character is Unconscious.
- A natural 20 restores 1 HP and returns normal turn control, as resolved by the deterministic engine.
```
