# Game Feel Direction

**Brief:** "I want this to feel like a game."
**Status:** Direction proposal — decision requested from the product owner on the items in §7.
**Scope of evidence:** `windows/src/DungeonMasterAI.App` (all views, theme, shell, view model), `windows/src/DungeonMasterAI.Engine`, `windows/src/DungeonMasterAI.Domain/Models.cs`, `windows/src/DungeonMasterAI.AI/LocalDmClient.cs`, `CHANGELOG.md`, `START_HERE.txt`. Every claim below cites a file and line.
**Constraint honoured:** this document proposes changes only. No source, XAML, or csproj was modified in producing it.

---

## 1. The direction, in one paragraph

This product does not need to be made more exciting. It needs to **stop deleting the excitement it already computes.** The deterministic engine resolves every dramatic beat in D&D combat at full fidelity — it knows the natural die face, whether it was a critical, exactly how much damage landed, whether that damage dropped a creature to 0, whether concentration broke, whether the death save was the third failure. It returns all of that as strongly-typed records. The view model then throws every field away except `.Summary`, writes that one string into a grey 8.5pt status line, and — on the primary play surface, Live Play — does not display it at all. The player's experience of "I hit for 9, and it was a crit" is currently: a number in a list quietly becomes a different number, and maybe the LLM mentions it. That is the difference between a tool and a game, and it is a UI-layer problem sitting on top of an engine that is already doing the hard part correctly.

So the direction is: **make outcomes felt, then give the session a shape, then give the DM a voice.** Progression and reward systems come last, deliberately — see §3.

---

## 2. The top three, if only three things ship

### 1. The Resolution Beat — surface the engine result the view model already holds
Every mechanical action in the app currently ends with one line of the form `StatusMessage = result.Summary;` (e.g. `MainViewModel.cs:2212`, `:1930`, `:1986`; 29 such assignments across the view-model partials, out of 187 `StatusMessage` writes total). At each of those call sites the view model is *holding* a typed record with the whole story in it — `AttackResult(int D20, int Modifier, int Total, bool Hit, bool Critical, int Damage, string Summary)` (`Domain/Models.cs:556`) — and keeps only the last field.

Replace `StatusMessage` as the outcome channel with a **Resolution Beat**: a small, prominent, animated card that renders the *structure* of what just happened — the d20 face large, the modifier and total beside it, hit/miss as a state not a word, damage as a number that lands, "CRITICAL" as a treatment rather than a parenthetical in prose — plus a running beat feed. This requires **no engine change**: the data is at the call site today.

**Touches:** `MainViewModel.cs` (new `ResolutionBeat` record + `ObservableCollection<ResolutionBeat>`, populated at the existing `.Summary` call sites), `Views/LivePlayView.xaml` (new beat surface — note it currently binds *no* outcome channel at all), `Views/CombatView.xaml` (replace the 8.5pt `TextTrimming="CharacterEllipsis"` status at `:147` and the `MaxHeight="30"` box at `:417`), `Themes/AaaTheme.xaml` (first storyboards — see §4.2).
**Size:** Medium.
**Buys:** the single largest felt change available. Actions acquire consequence you can see. A crit becomes an event instead of a slightly larger integer.

### 2. Session framing — an open and a close
"New Session" (`AaaShellWindow.xaml:71`) is implemented as `MainTabs.SelectedIndex = 1;` (`AaaShellWindow.xaml.cs:74`). There is no session object anywhere: `CampaignState` (`Domain/Models.cs:25-55`) has `Day`, `MinuteOfDay`, `Events`, `Chat` — and no session concept at all. The Live Play header offers `Ⅱ Pause` and `⊗ End Session` buttons (`LivePlayView.xaml:27-28`) with **no `Command` and no `Click`** — they do nothing.

Add a real session: a cold open (a "Previously…" recap composed from the last run of `campaign.Events`, which is already a persisted per-beat log — `Models.cs:50`, appended via `GameEngine.cs:2235`), a visible session clock/round count, and a close that writes a session summary and hands the player a "next time" hook. Nothing here needs new engine work; the material already exists in `campaign.Events` and `campaign.Chat`.

**Touches:** `Domain/Models.cs` (a `SessionState` on `CampaignState`; note schema migration infra and schema version 2 already exist per `CHANGELOG.md` r23), `MainViewModel.cs`, `Views/LivePlayView.xaml`, `AaaShellWindow.xaml.cs:74`.
**Size:** Medium.
**Buys:** converts an always-on utility you have open into a thing you sit down to play and get up from. This is the cheapest available conversion of "app" into "game."

### 3. Give the Dungeon Master a voice
`LocalDmClient.SystemPrompt` (`LocalDmClient.cs:221-241`) is twenty lines. Nineteen of them are prohibitions — "Never invent…", "Do not…", "Never decide…", "Stop and let the application collect it." The only craft instruction in the entire prompt is line 237: "Keep live-play narration immersive and compact: normally 2 to 5 short paragraphs." There is no tone, no register, no genre, no pacing, no instruction to escalate, no instruction to land a beat, no characterisation of *who this DM is*.

Those constraints are correct and must all stay — they are what keeps the LLM from inventing outcomes. But a compliance contract is not a performance brief. Add a voice section: register, sentence rhythm, how to open a scene versus how to close one, how to describe a critical hit differently from a graze, when to withhold, when to hand the moment back to the player. Optionally derive part of it from `CampaignState.Tone` (`Models.cs:31`), which exists and is currently unused by the prompt.

**Touches:** `LocalDmClient.cs:221` — one string literal.
**Size:** Small.
**Buys:** the DM is the product's frontman and currently has no personality. Cheapest large-perceived-change item on this list. Bounded by a 4B local model, so expect improvement, not transformation.

---

## 3. Resolving the brief: which reading carries the weight, and why

The brief admits four readings. My ranking, with reasons specific to this codebase:

**1st — (a) Presentation, understood as *outcome legibility*, not decoration.**
Weight: highest, by a wide margin. Not because juice is intrinsically the most important axis, but because in *this* product the gap between what the system knows and what the player sees is enormous and one-sided. The engine returns `D20TestResult(RollOne, RollTwo?, ChosenRoll, AbilityModifier, ProficiencyModifier, ExhaustionPenalty, Total, DifficultyClass, Success, Summary)` (`Models.cs:559`, produced at `CharacterMechanics.cs:106-140`) — it preserves *both* dice of an advantage roll separately from the chosen one. The player never sees either. Nowhere else on this list is the payoff already sitting in a local variable waiting to be rendered.

**2nd — (d) Session/ritual framing.**
Weight: high. Reason: it is nearly free (the event log already persists), it is the difference between a utility and an occasion, and it is the only item that reshapes the *whole* experience rather than one screen. A game has a beginning and an end; this currently has neither.

**3rd — (c) Narrative framing / the DM as a character.**
Weight: moderate, cost: trivial. Ranked third only because a great voice narrating invisible mechanics is still a chat window. Fix visibility first, then the voice has something to be a voice *about*.

**4th (last) — (b) Game systems: stakes, progression, consequence, reward.**
Weight: real, but explicitly deferred. Reasons:
- The domain has no progression at all. There is no XP field on `CharacterSheet` (`Models.cs:82-131`) and none anywhere in Domain or Engine. `CharacterSheet.Level` (`Models.cs:89`) is **write-once**: clamped at `GameEngine.cs:31` (`character.Level = Math.Max(1, character.Level)`) and never incremented or re-read by any progression logic thereafter.
- Building it means domain fields, engine rules, HP/slot/proficiency recalculation, persistence, a schema migration, and a balance pass. It is by far the largest item here.
- Most importantly: **reward systems amplify felt outcomes; they do not create them.** Granting XP in a UI where the player cannot see that they hit produces a number rising in a form. Do this after 1–3, when there is something for the reward to land on.
- One exception, which is nearly free and belongs now: `Quest.RewardGp` (`Models.cs:472`) is imported (`CampaignImportService.cs:359`, `CampaignExpansionApplyService.cs:171`) and shown to the LLM (`DmToolRouter.cs:213`) but **is never granted to anyone** — no code path in the Engine transfers it. The product already has a stake and never pays it out. See item 10 in §5.

---

## 4. Diagnosis

### 4.1 Where this reads as a tool

**The outcome channel is a status bar.** Detailed above. The sharpest single expression of it: `LivePlayView.xaml` — the primary play surface — binds `StatusMessage` **nowhere**. In Live Play, the deterministic engine's verdict on your action is not displayed. The player learns what happened only if and when the LLM chooses to mention it in prose.

**Nothing in the entire application moves.** A search across all 3,571 lines of XAML and all App-project C# for `Storyboard`, `DoubleAnimation`, `ColorAnimation`, `VisualStateManager`, `BeginStoryboard`, `EventTrigger`, `DispatcherTimer`, or `CompositionTarget.Rendering` returns **zero hits**. Every trigger in `AaaTheme.xaml` is an instantaneous setter (`:134-142`, `:182-196`, `:254-263`, `:341-345`). Custom-drawn chrome — `AaaCombatAtmosphere.cs`, `AaaArcaneCompass.cs`, `AaaDungeonFloor.cs` — is a single static `OnRender` pass (`AaaCombatAtmosphere.cs:14`). The app has no time dimension. State changes are teleports.

**The view model cannot express *what* changed — only *that* something did.** `MainViewModel.RaiseCampaignProperties()` (`MainViewModel.cs:2285-2344`) is a 56-line global invalidation firing 100 times across the view-model partials. It raises `PropertyChanged` for 48 properties and bumps `MapRevision` to force a full battlefield re-render. Every list is a recomputed `IEnumerable` projection, not an `ObservableCollection` — `SessionChat` at `:360-366` rebuilds a fresh `SessionChatMessageDisplay` for every message on every action. This is the structural reason "add some animation" is not currently possible: there are no stable item containers to animate, and no signal saying which row is the one that just took damage.

**The primary screens are dashboard mockups with hardcoded data.** `HomeView.xaml` hardcodes Campaign Level `5` (`:46`), Sessions `12` (`:49`), "Tomorrow / May 24, 2025 · 7:00 PM" (`:88-89`), "Session 13" (`:91`), "4 / 4 Members" (`:117`), "♥ Healthy / No Conditions" (`:122`), and a four-item Recent Activity list that is four literal `TextBlock`s (`:159-162`). It has **6 buttons and 0 of them are wired** — Campaign Settings (`:51`), View Session (`:92`), Open Map (`:110`), Manage Party (`:123`), View World Timeline (`:152`), View All (`:157`). `WorldView.xaml` hardcodes quest progress `40%` and `ProgressBar Value="40"` (`:102`), faction standing `Value="62"` (`:91`), "4 Active" (`:102`), three literal rumours (`:93`), and a map toolbar (`:26-34`) whose Ping / Reveal / Hide / Filter / Add Note are `TextBlock`s, not buttons.

**Dead chrome in the play surface.** `LivePlayView.xaml` has 21 buttons; 10 have no command: `Ⅱ Pause` and `⊗ End Session` (`:27-28`), the four battlefield tool buttons (`:89-92`), and in the main combat action strip **Cast Spell (`:179`), Dodge (`:180`), Ready (`:181`) and Ask AI (`:183`)**. Those four sit in the visually loudest row on the screen, styled identically to Attack and End Turn, which *are* wired (`:178`, `:182`). A player's first three clicks in combat have a good chance of hitting a button that does nothing.

**Developer test affordances ship on the player-facing character sheet.** `CharactersView.xaml` offers `＋ Add Test Character` (`:64`), `▼ Take 1 Damage` (`:131`), `▲ Heal 1` (`:132`), `✚ Grant 5 Temp HP` (`:133`). No game asks you to click "Take 1 Damage."

**Two homes for one fight.** Live Play (`LivePlayView.xaml`) and Combat (`CombatView.xaml`) are separate top-level tabs (`AaaShellWindow.xaml:90-97`). Both render a `CombatGridControl`, both host the death-save prompt and the blocking-decision prompt (`LivePlayView.xaml:118-146`; `CombatView.xaml:314-342`), but only Combat exposes Dash/Disengage/Hide/Grapple/Shove/Ready/Study/Influence (`CombatView.xaml:238-256`, `:371-398`) while only Live Play offers narration and player input (`:41-49`, `:76-80`). Mid-fight the player must tab between two screens that each own half the game.

**Every creature is the same glyph.** `AaaVectorIcon Kind="Characters"` renders the initiative avatar (`CombatView.xaml:100`), the active-combatant portrait (`:193`) and every target card (`:278`). There is no visual identity; the fight is a list of names.

**The shell speaks the language of enterprise software.** Ten tabs — Home, Live Play, Combat, Characters, World, Maps, Quests, Rules, Import, Settings (`AaaShellWindow.xaml:86-125`) — over a status bar reading "v1.0.0 · All systems operational · Local Data Only 🔒" (`:131-137`). Compare the Home dashboard's "◇ Safe Recovery / All systems nominal" (`HomeView.xaml:142`).

**Dead code with 937 lines of spreadsheet in it.** `MainWindow.xaml` is orphaned — startup constructs `AaaShellWindow` (`App.xaml.cs:42`), and the only remaining `MainWindow` reference is the WPF `Application.MainWindow` property assignment at `App.xaml.cs:43`. It still contains a 24-column `GridView` of combatants (`MainWindow.xaml:703-728`: Name, Type, Side, Init, AC, HP, Temp, Grid, Move, Action, Bonus, Attacks, Reaction, Disengage, Dodge, Conditions, Grapple, Help, Hidden, Ready, Concentration, Attacks). It is the ancestor this UI is escaping from and should be deleted so nobody restores it.

**One outcome the engine never computes.** `DiceService.Attack` (`DiceService.cs:114-115`) computes `naturalCritical = d20 == 20` and applies the nat-1 auto-miss inline as `hit = naturalCritical || (d20 != 1 && total >= armorClass)`. `AttackResult` therefore carries `Critical` but has **no fumble/natural-miss field** — a natural 1 is indistinguishable from a mundane miss at the API boundary. Half the drama of the d20 is discarded before it leaves the dice service.

### 4.2 What is already right and must be protected

**The rules engine.** This is the asset and nothing on this list may compromise it. ~70 deterministic tools (`DmToolRouter.cs:13-92`) covering the full 2024 action economy: Dash, Disengage, Dodge, Hide, Search, Study, Influence, Help, First Aid, grapple/shove/escape, readied attack/move/spell with trigger confirmation, opportunity attacks with explicit player reaction windows, concentration, persistent battlefield effects with geometry and save triggers, area/projectile/multi-target spellcasting. `d95fa52` (r60) hardened this further: validate before mutating so a rejected tool call cannot commit state. **The engine is not the problem and should not be reorganised in service of feel.** The one engine change proposed anywhere in this document is additive (item 11, §5).

**The player-authority model.** Player characters never have their dice rolled for them. `PendingRollRequest` (`Models.cs:378-395`) plus `PendingPlayerDecision` (`Domain/PlayerDecisions.cs:8-20`) block the LLM mid-turn until the human acts, and the system prompt enforces it (`LocalDmClient.cs:231-236`). The 2024 death-save timing is modelled correctly — a PC rolls only when they *start* a turn at 0 HP (`CHANGELOG.md` r22, enforced at `LocalDmClient.cs:231`). This is genuine game design already in the product and it is the reason the Resolution Beat has something to render: there is a real decision point to build a moment around.

**The r59 visual language.** `AaaTheme.xaml` is a coherent, committed system: a near-black blue-green ground (`#070B0E`), a single gold accent (`#C7A25C`), Georgia for anything diegetic and Segoe UI for chrome, uniform 3–4px radii, one shared drop shadow. It is dark-fantasy without being a tavern-wood cliché. It reads as authored, not templated. **Do not restyle.** Everything proposed here extends this palette; the only additions needed are motion and a small set of outcome-state colours drawn from the existing `AaaRed`/`AaaGreen`/`AaaGoldBright`.

**The custom-drawn atmosphere work.** `AaaCombatAtmosphere.cs` (vignette, four torch glows, broken walls, wall cracks, frame) and `AaaArcaneCompass.cs` in the nav rail (`AaaTheme.xaml:217`) are hand-authored WPF drawing, not stock assets. They are the right instinct and the right layer. They just never move.

**The Combat workspace's mechanical completeness.** `CombatView.xaml` has 39 buttons, 35 wired. Every SRD action is reachable. Whatever restructuring happens between Live Play and Combat, this coverage must survive it — r57's own comment (`CombatView.xaml:295-300`) records that an earlier pass lost it once already.

---

## 5. Full ranked list — impact per unit effort

| # | Change | Touches | Size | What it buys |
|---|---|---|---|---|
| 1 | **The Resolution Beat.** Animated outcome card + beat feed rendering the typed engine result (d20 face, mods, total vs. DC/AC, hit/miss, damage, crit, dropped-to-zero) instead of `StatusMessage`. Data already present at every call site. | `MainViewModel.cs` (new record + collection at the 29 `.Summary` sites), `Views/LivePlayView.xaml`, `Views/CombatView.xaml:147,:417`, `Themes/AaaTheme.xaml` | M | The core loop acquires visible consequence. Largest felt delta available. |
| 2 | **Remove the debug row from the character sheet.** Delete Add Test Character / Take 1 Damage / Heal 1 / Grant 5 Temp HP, or move behind a dev flag. | `Views/CharactersView.xaml:64,131,132,133` | XS | Removes the loudest single "this is a QA harness" signal. Ten minutes. |
| 3 | **Session framing.** Session object; cold open with a "Previously…" recap built from `campaign.Events`; live session clock; close that writes a summary and a next-time hook. Wire the dead Pause / End Session buttons. | `Domain/Models.cs` (+migration), `MainViewModel.cs`, `Views/LivePlayView.xaml:27-28`, `AaaShellWindow.xaml.cs:74` | M | An occasion instead of an always-open utility. Beginning, middle, end. |
| 4 | **DM voice and pacing brief.** Add register/rhythm/escalation guidance to the system prompt alongside the existing (unchanged) constraints; optionally feed `CampaignState.Tone`. | `LocalDmClient.cs:221-241` | S | The frontman gets a personality. One string literal. |
| 5 | **Targeted invalidation.** Replace the `RaiseCampaignProperties()` shotgun with `ObservableCollection`s and per-entity change notification for combatants and chat, so a row can know it is the one that changed. | `MainViewModel.cs:2285-2344`, `:355-366`; the 100 call sites | M–L | The prerequisite for per-row damage flashes, HP-bar tweening, and message-arrival animation. Also removes a full battlefield re-render per action (`MapRevision++`, `:2343`). |
| 6 | **Active-turn emphasis on the battlefield.** Pulse/ring the active combatant; animate movement between grid squares rather than teleporting; flash the target on damage. Depends on #5. | `Controls/CombatGridControl.cs:100`, `Controls/AaaCombatAtmosphere.cs` | M | Turn order becomes spatial and legible; the map becomes a fight rather than a diagram. |
| 7 | **Replace or delete the mockup data.** Bind real values for level/sessions/party/quest-progress/faction-standing, or cut the panels. | `Views/HomeView.xaml:46,49,88-92,117,122,159-162`, `Views/WorldView.xaml:91,93,102` | S | Fake data is worse than no data: it teaches the player not to trust the screen. |
| 8 | **Unify Live Play and Combat.** Make combat a *mode* of the play surface (the mode switch already exists — `PlaySceneModeTitle`, `MainViewModel.cs:370`) rather than a sibling tab, carrying `CombatView`'s full action coverage across. | `Views/LivePlayView.xaml`, `Views/CombatView.xaml`, `AaaShellWindow.xaml:90-97` | M | One place to play. Removes mid-fight tab-switching between two half-games. |
| 9 | **Wire or remove every dead button.** 10 in Live Play, all 6 in Home, the World map toolbar. | `Views/LivePlayView.xaml:27,28,89-92,179,180,181,183`, `Views/HomeView.xaml`, `Views/WorldView.xaml:26-34` | S | A button that does nothing is the clearest possible statement that the thing is a mockup. |
| 10 | **Grant `Quest.RewardGp` on completion.** The field is imported and shown to the LLM but never paid out to anyone. | `DmToolRouter.cs:517` / `GameEngine`, `Models.cs:472` | XS | Turns quest completion from a status-string change into a thing the player receives. The product's only existing stake, currently dead. |
| 11 | **Make a natural 1 a real outcome.** Add `NaturalCritical` / `NaturalMiss` to `AttackResult` so the fumble is distinguishable at the boundary; render it in the Resolution Beat. | `DiceService.cs:103-131`, `Domain/Models.cs:556` | XS–S | Recovers the other half of the d20's drama. Additive, non-behavioural. |
| 12 | **Delete `MainWindow.xaml` / `.xaml.cs`.** Orphaned; contains the 24-column combatant spreadsheet. | `MainWindow.xaml`, `MainWindow.xaml.cs` | XS | Removes 937 lines of the exact aesthetic the product is moving away from. |
| 13 | **Audio.** No audio subsystem exists — zero references to `MediaPlayer`, `SoundPlayer`, `SystemSounds`, or any audio library anywhere. Minimum viable: die impact, hit, crit, drop-to-0, death-save fail, turn advance. **Needs owner decision — see §7.** | new `Runtime/` service, `MainViewModel` | M | Enormous per-unit impact on feel, but this is a new subsystem plus an asset budget, not a tweak. |
| 14 | **Creature identity.** Portraits or distinct tokens per creature instead of one shared glyph. **Needs art budget — see §7.** | `Views/CombatView.xaml:100,193,278`, `Controls/CombatGridControl.cs` | M (+art) | Combat becomes people rather than rows. |
| 15 | **Progression: XP, levelling, milestone rewards.** Currently entirely absent; `Level` is inert after creation (`GameEngine.cs:31`). Domain + engine + persistence + migration + balance pass. **Needs owner decision — see §7.** | `Domain/Models.cs`, `GameEngine.cs`, `Data/` migration, `MainViewModel.cs`, views | L | The long-term loop. Deliberately last — it amplifies felt outcomes and cannot substitute for them. |

**Sequencing note.** Items 1, 2, 4, 9, 10, 11 and 12 are independent and can proceed in parallel. Item 5 is the gate for 6 and materially improves 1. Item 3 is independent but overlaps 8 in the Live Play XAML — sequence them, and note that another agent is currently editing App XAML in a separate worktree.

---

## 6. What this direction explicitly does *not* do

- It does not restyle. The r59 palette, typography and chrome stay exactly as they are.
- It does not reorganise the engine. One additive field (item 11) is the entire engine footprint.
- It does not weaken player authority. The blocking-roll and blocking-decision model is a feature, and the Resolution Beat is built *around* it, not through it.
- It does not add a monetisation, streak, or daily-reward loop. This is a single-player local application; behavioural-retention machinery would be both inappropriate and off-identity.
- It does not require any technology not already in the project: WPF storyboards, `ObservableCollection`, and custom `OnRender` drawing cover items 1–12 entirely.

---

## 7. Decisions needed from the product owner

1. **Audio: yes or no?** (Item 13.) There is currently no audio anywhere in the product. Sound is the highest per-unit game-feel lever in existence and the reason a die roll feels like a die roll — but it means a new subsystem, an asset budget or licence, and a decision about whether a deliberately offline, local-only, private product ships sound at all. I would recommend yes, scoped to roughly eight cues. I am not proceeding on it without a decision.

2. **Progression: is this a game with a reward loop, or a DM that runs rules faithfully?** (Item 15.) This changes the product's identity, not just its surface. XP and levelling do not exist in the domain today. Adding them is the largest item here and implies a schema migration and an ongoing balance responsibility. My recommendation is to defer it until items 1–8 have shipped and been played, then decide with evidence.

3. **How much of a character is the Dungeon Master allowed to be?** (Item 4.) `AppSettings.PlayerSafeMode` (`Models.cs:22`) defaults to `true` and the prompt currently frames the DM as a neutral, tightly constrained referee. Giving it voice, opinions, and dramatic pacing is a tonal commitment. Should the voice be fixed, or authored per campaign from `CampaignState.Tone` (`Models.cs:31`, currently unused by the prompt)?

4. **Art budget for creature identity.** (Item 14.) Every creature currently renders as the same vector glyph. Distinct tokens are one of the strongest remaining feel levers, but need either commissioned art, a licensed token pack, or a generated-token pipeline — the map-asset-pack infrastructure (`docs/map-asset-packs.md`, `Controls/TacticalMapAssetCatalog.cs`) suggests a precedent exists for the third option.

5. **May `MainWindow.xaml` be deleted?** (Item 12.) It is orphaned dead code, but 937 lines is a large deletion and I want confirmation it is not being kept deliberately as a fallback shell.

6. **Is Live Play meant to remain a separate tab from Combat?** (Item 8.) Merging them is the right call for feel, but it is a visible information-architecture change and it touches the file another agent is currently editing.

7. **Single-protagonist or party?** Several surfaces assert a four-member party — "4 / 4 Members" (`HomeView.xaml:117`), "PARTY (4 / 4)" (`CharactersView.xaml:52`) — while the play loop is built around one acting player character. Whether the player controls a party or a protagonist changes the Resolution Beat's design (whose outcome is being shown) and should be settled before item 1 is built.
