# DungeonMasterAI Audio Direction

Scope: the audio layer for a .NET 10 WPF Windows desktop application. This document covers stack
selection, the engine seams a cue can fire from, the experience design in priority order, the honest
cost, and a phased plan.

This is input for the Game Designer, who owns the overall "feel like a game" brief. Audio is one
slice of that. Where this document depends on a design decision that is not mine to make, it says so.

## 0. What exists today

Nothing. Confirmed by search across the repository:

- No audio assets of any kind. A search for `*.wav *.mp3 *.ogg *.flac *.opus *.m4a *.aiff *.wma`
  across the tree (excluding `obj/`, `bin/`) returns zero files.
- No audio code. No reference to `System.Media`, `SoundPlayer`, `MediaPlayer`, `MediaElement`,
  `NAudio`, or `CSCore` in any `.cs`, `.xaml`, or `.csproj` under `windows/src`.
- No audio dependency. The entire solution has exactly one third-party NuGet package:
  `PdfPig 0.1.15` in `windows/src/DungeonMasterAI.Data/DungeonMasterAI.Data.csproj`.
- No prior intent. `CHANGELOG.md` contains no mention of audio, sound, or music.

The application is silent from launch to quit. Given that the product's core interaction is *rolling
dice and resolving violence*, this is a large part of why it reads as a productivity tool.

## 1. Stack recommendation

### Recommended: NAudio (`NAudio.Core` + `NAudio.Wasapi`) with `NVorbis` for music decode

All three are MIT-licensed, pure managed C# with no native binaries and no OS media-stack
dependency. Reference the focused packages, **not** the `NAudio` meta-package — the meta-package
pulls in `NAudio.WinForms`, `NAudio.Asio`, `NAudio.Dmo`, and `NAudio.Midi`, none of which this
product needs.

| Criterion | NAudio |
|---|---|
| Concurrent sounds | `MixingSampleProvider` with `ReadFully = true` mixes N inputs into one output device. This is the canonical fire-and-forget pattern. |
| Gapless looping | Sample-accurate. A custom `ISampleProvider` wrapping a cached buffer that returns to sample 0 on exhaustion has no gap by construction. |
| Crossfading | Trivial. Two `VolumeSampleProvider` instances into the mixer, ramped per buffer. No animation framework involved. |
| Per-channel volume | Native. One `VolumeSampleProvider` per bus (music / ambience / SFX / UI) under one master. |
| Latency | WASAPI shared mode, tunable. NAudio 3 also offers `WasapiPlayer` using `IAudioClient3` low-latency shared mode. We do not need low latency (see §5) and should deliberately choose a large buffer. |
| Licensing | MIT. `NAudio 3.0.1` (Aug 2026) targets `net9.0` / `net9.0-windows`, which the `net10.0-windows` App consumes cleanly. |
| Install size | ~1.0–1.5 MB of managed DLLs. See §6. |
| OS dependency | None beyond WASAPI / WinMM, present on every Windows SKU including N and KN editions. |

`NVorbis` (MIT, ~104 KB, fully managed Ogg Vorbis decoder) covers compressed music and ambience.
Its last release is 0.10.5 (Oct 2022), which reflects that Vorbis is a frozen format rather than an
abandoned project. It targets `netstandard2.0` and runs on .NET 10.

**Asset format policy that follows from this choice:**

- SFX: 16-bit mono PCM WAV at 22.05 kHz. Read natively by `NAudio.Core`, no decoder dependency,
  decoded to memory once at startup, zero playback latency.
- Music and ambience: Ogg Vorbis, decoded by `NVorbis`, streamed on a background thread.

This keeps the entire audio path inside managed code we ship. Nothing depends on codecs the user's
Windows install may or may not have.

### Runner-up: WPF `MediaPlayer`, and why it lost

`System.Windows.Media.MediaPlayer` is the only serious alternative. It is already available — zero
new dependencies, zero added install size — and it does technically satisfy the basic requirements:
one instance per sound gives concurrency, it exposes a `Volume` property in `[0,1]`, and a
`DoubleAnimation` on that property gives a crossfade. For a handful of one-shots it would work.

It lost on four counts, in order of weight:

1. **It depends on Windows Media Player components.** Microsoft's own WPF multimedia documentation
   states that `MediaElement` and `MediaPlayer` use the Windows Media Player control for playback.
   Windows N and KN editions ship without those components and require the Media Feature Pack. This
   product ships as a public installer to unknown machines. An audio layer that silently fails on a
   subset of legitimate Windows installs is a support burden we would be choosing for no gain.
2. **Looping is not gapless.** The idiomatic loop is to handle `MediaEnded` and reset `Position = 0`.
   That round trip is audible. Ambient beds are the one thing in this design that must loop for
   twenty minutes without the player noticing, and this is precisely what `MediaPlayer` cannot do.
3. **No mixing bus.** Per-sound volume exists; a *master* and *category* volume does not. You would
   implement the bus arithmetic yourself across N player instances, and every volume change means
   walking every live instance. With a mixer this is one multiply in one place.
4. **Per-instance cost and thread affinity.** Each `MediaPlayer` is a relatively heavy object that
   must live on a thread with a `Dispatcher`. Rapid one-shots — exactly the dice-and-hits profile —
   mean either pooling players or paying construction cost on the UI thread during play.

`MediaPlayer` remains the correct fallback if the project decides it will not take a new dependency
under any circumstances. It is not the correct default.

### Rejected outright

**`System.Media.SoundPlayer`** — disqualified, not merely inferior. It plays *one* sound at a time
(a second `Play()` stops the first), has **no volume control whatsoever**, and accepts only PCM WAV.
An audio design with no volume control cannot ship, because the mute and volume requirement in §5 is
non-negotiable. It is unusable for anything past a proof of concept.

**CSCore** — capable and architecturally clean, but licensed Ms-PL rather than MIT, and its last
NuGet release is 1.2.1.2 from October 2017. Taking a dependency dormant for eight years, for a
capability NAudio provides under a more permissive license, is not a trade worth making.

### On FMOD and Wwise: do not use them here

I want to be direct, because this is my default toolset and it is the wrong answer for this product.

FMOD Studio and Wwise are middleware for real-time game engines. Their value is decoupling audio
authoring from code, driving continuous parameter automation against a running simulation, and
handling 3D spatialisation and voice budgets for scenes with hundreds of concurrent emitters. None
of those conditions hold here. This is a turn-based WPF desktop application with, at peak, perhaps
six simultaneous sounds and no spatial dimension at all.

The costs are concrete and disqualifying: both require native x64 DLLs shipped alongside the
application, adding meaningfully more than NAudio's ~1.5 MB; both require a C# binding layer
maintained against engine-agnostic APIs; both introduce a commercial licensing question for a
publicly distributed product; and both would require whoever maintains this repository to install
and learn a separate authoring tool in order to change a single sound effect. Wwise additionally
requires per-title license registration. There is no adaptive-audio capability in §4 that NAudio
cannot do in a few hundred lines of C# that a .NET developer can read and debug.

Recommending FMOD here would be reaching for a familiar tool rather than the right one.

## 2. Where audio hooks in

### 2.1 The result records are the event surface

Every deterministic outcome in this product is already a `sealed record` carrying the exact fields an
audio cue needs. They live in `windows/src/DungeonMasterAI.Domain/Models.cs`, lines 555–599. This is
unusually good news: the hard part of retrofitting audio — knowing *what just happened* — is already
solved by the engine's existing design.

| Record | Line | Fields that drive a cue |
|---|---|---|
| `AttackResult` | 556 | `D20` (natural roll), `Critical`, `Hit`, `Damage` |
| `DamageResult` | 561 | `EffectiveDamage`, `CurrentHp`, `DroppedToZero`, `Dead`, `TempHpLost` |
| `DeathSaveResult` | 560 | `Roll`, `Successes`, `Failures`, `Stable`, `Dead` |
| `D20TestResult` | 559 | `RollOne`, `RollTwo` (non-null ⇒ advantage/disadvantage), `ChosenRoll`, `Success` |
| `EncounterAttackResult` | 566 | `Attack`, `Damage`, `Concentration`, `UsedReaction`, `CoverBonus` |
| `SpellCastResult` | 584 | `SpellName`, `CastAtLevel`, `UsedSpellSlot`, `Ritual`, `Healing`, `ConcentrationStarted`, `TargetResults` |
| `SpellTargetResolution` | 575 | `Sequence` (projectile ordering), per-target `SpellAttack` / `TargetSavingThrow` / `Damage` |
| `ConcentrationCheckResult` | 562 | `Maintained` |
| `InitiativeSequenceResult` | `Engine/GameEngine.InitiativeRolls.cs:5` | `Completed`, `Order` |
| `RestResult` | 564 | `RestType`, `Minutes` |
| `CombatMoveResult` | 557 | `Committed`, `OpportunityAttacks` (non-empty ⇒ tension) |

Notable specifics, verified in the engine:

- **There is no fumble flag.** `AttackResult` carries `Critical` for a natural 20
  (`Engine/DiceService.cs:114`, `naturalCritical = d20 == 20`) but nothing for a natural 1. The
  natural 1 is detectable, and only detectable, as `Attack.D20 == 1`. Any nat-1 cue must test that
  field directly.
- **Death saves already model both extremes.** `Engine/GameEngine.DeathSaves.cs:18-25` handles the
  natural 20 (resets successes and failures, restores 1 HP); lines 27–29 handle the natural 1
  (counts as two failures). The result is constructed at line 59. The engine has already decided
  these are special. Audio only has to agree with it.
- **Advantage and disadvantage are visible.** `D20TestResult.RollTwo` is non-null exactly when two
  dice were rolled. Two dice hitting the table should sound like two dice.
- **Turn advancement returns no flag.** `GameEngine.NextTurn` (`Engine/GameEngine.cs:858-907`)
  returns the new `CombatantState` and carries no "round advanced" boolean. A round-change cue must
  compare `EncounterState.Round` (`Models.cs:265`) before and after the call, or read the log line
  written at `Engine/GameEngine.cs:905`.

### 2.2 There is no single chokepoint. Hook the App layer.

This is the most important structural finding, and it contradicts the obvious guess.

`DmToolRouter` is **not** a universal gameplay funnel. It is a JSON-in / JSON-out tool-calling shim
for the LLM (`Engine/DmToolRouter.cs:94`, `Execute(CampaignState, string toolName, string argumentsJson)`,
dispatching a `switch` across lines 100–219 and returning `DmToolResult(bool Ok, object? Result, ...)`
declared at line 7). Its only consumer is `AI/LocalDmClient.cs:173`.

Human player actions bypass it entirely. `MainViewModel` instantiates `DmToolRouter` at
`App/MainViewModel.cs:79` solely to hand to the LLM client, then calls `GameEngine` methods
**directly** for its own roughly sixty player commands — `_engine.ResolveEncounterAttack`,
`_engine.CastSpell`, `_engine.NextTurn`, `_engine.ResolveCombatDeathSavingThrow`, and so on.

**Consequence: an audio hook placed in `DmToolRouter` would miss every action the player takes.**
That is the wrong seam.

The engine also offers no push mechanism to subscribe to. There are no `event` declarations, no
`INotifyPropertyChanged`, and no `IObservable` anywhere in `windows/src/DungeonMasterAI.Engine`. The
App calls synchronously and reads the returned record.

The correct seam is therefore **`MainViewModel`**, where both paths converge and where the typed
result object is in hand. Concretely, cues fire from the existing result-handling sites — for example
`App/MainViewModel.cs:2205-2212` (`ResolveEncounterAttack`) and `:1450-1459`
(`ResolveCombatDeathSavingThrow`) — and across the roll-resolution partials in
`App/MainViewModel.PlayerRolls.cs` (`ResolveCombatDeathSavingThrow` at :270,
`ResolvePendingEncounterAttackRoll` at :311, `ResolvePendingEncounterAttackDamageRoll` at :341).

This also keeps the Engine platform-neutral. `DungeonMasterAI.Engine` targets `net10.0`, not
`net10.0-windows`, and is exercised by roughly thirty headless console test projects in CI. Audio
must not be reachable from it.

### 2.3 The rollback trap

`SendPlayerInputCoreAsync` (`App/MainViewModel.cs:1307-1373`) runs the LLM turn against a **cloned**
campaign (`_campaignCloner.Clone`, line 1329) and commits it only on success
(`CommitCampaign`, line 1346). On failure it discards the clone and writes a `dm_turn_rolled_back`
event to the original (lines 1349–1362).

Audio must fire **after the commit, never from inside the engine call.** A hook placed in
`GameEngine` or `DmToolRouter` would play a critical-hit sting for a blow that was subsequently
rolled back and never happened. The player would hear an event the game then denies.

This mirrors the concern already fixed in commit `d95fa52`, "validate before mutating so a rejected
tool call cannot commit state." The audio layer must inherit the same discipline: **sound is a
consequence of committed state, not of computation.**

Practically, for the LLM path this means capturing a watermark into `CampaignState.Events` before
`RunTurnAsync` and replaying only the tail after `CommitCampaign` returns.

### 2.4 A cheap breadth mechanism: the existing event log

`GameEngine` already writes a typed log through a private helper at `Engine/GameEngine.cs:2235-2236`,
appending `CampaignEvent { Type, Summary }` (declared `Models.cs:539-546`) to `CampaignState.Events`.
Observed `Type` discriminators include `damage`, `damage_at_zero`, `death_save`,
`combat_death_save`, `combat_attack`, `combat_turn`, `initiative_roll`, and `initiative`.

This is effectively a free, pre-existing event bus with a string discriminator, covering both the
player and LLM paths. Its limitation is real: `CampaignEvent` carries only `Type` and prose
`Summary`, with **no numeric fields** — it can tell you a hit landed but not that it was a natural 20.

It is also a plain `List<CampaignEvent>`, not an `ObservableCollection`, so consumption is by
count-watermark diffing after each command, not by `CollectionChanged`.

That yields a two-tier strategy:

- **Tier A — breadth, near-free.** Diff `CampaignState.Events` after each command and switch on
  `Type`. Covers turn changes, initiative, damage, and death saves with no engine changes and one
  hook site. Enough to stop the app feeling silent.
- **Tier B — the earned moments.** Explicit cue calls at the `MainViewModel` sites where the typed
  record is available, testing `Attack.D20 == 20`, `Attack.D20 == 1`, `Damage.DroppedToZero`,
  `DeathSaveResult.Roll`. This is where the character comes from.

Tier A is a scaffold. Tier B is the product. Both hook the same layer.

### 2.5 Scene and pacing state that already exists

| Signal | Location | Audio use |
|---|---|---|
| `MainViewModel.HasActiveCombat` | `App/MainViewModel.cs:369` | The combat / exploration switch. Already computed from `EncounterState.Status == "active"`. |
| `MainViewModel.PlaySceneModeTitle` | `App/MainViewModel.cs:370` | Already renders "TACTICAL COMBAT" / "EXPLORATION". Audio should follow the same state, not invent a parallel one. |
| `EncounterState.Round`, `.TurnIndex` | `Models.cs:265-266` | Combat intensity ramp. |
| `CombatantState.DeathSaveRequiredThisTurn` | `Models.cs:295` | Crisis trigger. |
| `CombatantState.Surprised`, `.Side` | `Models.cs:281, 287` | Ambush colour; friend/foe weighting. |
| `CampaignState.MinuteOfDay`, `.Day` | `Models.cs:33-34` | Time-of-day ambience blend, already tracked, free. |
| `CampaignState.Tone` | `Models.cs:31` | Free-form; could steer a music palette later. |
| `CampaignState.PendingPlayerRoll` | `Models.cs:52` | The held-breath moment: the game is waiting on a human d20. |
| `MainViewModel.IsDmBusy` | `App/MainViewModel.cs:1323, 1368` | The LLM is thinking. See §4.2. |

## 3. The honest feasibility finding: ambient beds are blocked, combat cues are not

The brief lists ambient beds first, and that ordering is intuitive — a tavern that sounds like a
tavern is the most obviously "game-like" thing audio can do. But the data does not support it yet,
and the combat cues that sound less glamorous are entirely unblocked.

`WorldLocation.Type` (`Models.cs:61`) is a **free-form string defaulting to `"area"`**. There is no
biome or scene enum anywhere in the codebase. Its values come from `area_type` in campaign manifests,
mapped at `Data/CampaignImportService.cs:83` and `Data/CampaignExpansionApplyService.cs:38`. The
values actually present in shipped campaign data are:

```
town, shop, secret, quest, landmark, inn
```

These are **administrative categories, not acoustic ones.** `inn` maps cleanly to a tavern bed and
`town` to a street bed, but `secret`, `quest`, and `landmark` describe a location's narrative role
and say nothing about how it sounds. A "landmark" could be a windswept cliff or a cathedral.

Worse for reliability: the LLM campaign compiler generates these values freely
(`AI/CampaignAiCompilerService.cs:259` instructs the model to emit `type/area_type`), so an
AI-authored campaign can produce any string at all. Keyword matching against an open vocabulary will
be wrong often, and a tavern bed playing in a crypt is worse than silence.

**This is a dependency on the Game Designer, not a thing audio can solve alone.** What is needed is a
small controlled acoustic vocabulary carried alongside the existing `Type` — something on the order
of `interior_crowded`, `interior_quiet`, `dungeon`, `wilderness`, `urban_exterior`, `wilderness_night`,
`silent` — authored per location, defaulting to `silent` rather than guessing. Roughly six to eight
values. Until that exists, scene-accurate ambience cannot ship correctly.

Meanwhile every combat and dice cue in §4.3 needs **no new data whatsoever.** The typed records
already carry the natural roll, the crit flag, the damage, the drop to zero, and the death save.

That inverts the naive priority order, and it is the central recommendation of this document.

## 4. Experience design, ranked by impact per unit of effort

### 4.1 Ranking

| # | Element | Impact | Effort | Blocked? |
|---|---|---|---|---|
| 1 | Dice and attack resolution | High | Low | No |
| 2 | Natural 20 and natural 1 | Very high | Very low (on top of 1) | No |
| 3 | Damage, drop to 0, death | High | Low | No |
| 4 | Death saves | Very high | Low | No |
| 5 | Turn change and initiative | Medium | Low | No |
| 6 | UI feedback | Low | Low | No |
| 7 | Spell cast and impact | Medium | Medium | No |
| 8 | Adaptive combat music | High | High (needs composition) | Budget |
| 9 | Ambient beds | High perceived | High | **Yes — §3** |

Items 1–6 are Phase 1. Items 7–9 come after, in that order.

### 4.2 A pacing note that changes the usual doctrine

My standard rule is that music transitions must be tempo-synced and stingers beat-quantised. **That
rule is wrong for this product, and I am setting it aside deliberately.**

This is a turn-based application whose pacing is set by a human reading prose and by a local LLM that
may take anywhere from a few seconds to minutes to answer — `AI/LocalDmClient.cs:14` sets an
`HttpClient` timeout of three minutes. There is no groove for the player to be locked into and no
continuous action for a beat grid to serve. Quantising a stinger to a bar line would only delay it
relative to the text that just appeared on screen, which is where the player's attention actually is.

Therefore:

- **Transitions are slow crossfades of 2–4 seconds, aligned to nothing.** Musically unfashionable,
  correct here.
- **Cues fire immediately on the committed result**, tied to the text landing in the narration feed.
- **Nothing may loop-fatigue during a long LLM turn.** A bed must survive several minutes of no
  player input without becoming annoying. This argues for long loops (90+ seconds) and low-detail,
  low-melodic-content material.
- `IsDmBusy` (`App/MainViewModel.cs:1323, 1368`) is a legitimate audio state — a "the DM is
  considering" thinking bed — but it is a Phase 3 nicety, not a launch requirement.

### 4.3 Discrete cues (Phase 1)

The full Phase 1 sample list is about eighteen files. Every one of them reads its trigger from a
field cited in §2.1.

**Dice.** The most repeated interaction in the app and therefore the highest-leverage sound in it.

| Cue | Trigger | Notes |
|---|---|---|
| `dice.d20.single` | any `D20TestResult` where `RollTwo == null` | 3–4 variants, randomised |
| `dice.d20.double` | `D20TestResult.RollTwo != null` | Two dice audible. Advantage and disadvantage *sound* different from a flat roll. |
| `dice.damage` | any damage roll resolution | Handful of dice, lower pitch |

Randomised variant selection with a small pitch and gain jitter (±2 semitones, ±1.5 dB) is
mandatory here. A single unvaried dice sample heard four hundred times in a session becomes hostile.
Never repeat the same variant twice in a row.

**The two earned moments.** This is the single best impact-to-effort ratio in the document: two
extra branches and two samples, on top of work already done for the dice cue.

| Cue | Trigger | Design |
|---|---|---|
| `d20.natural20` | `Attack.D20 == 20`, or `D20TestResult.ChosenRoll == 20` | A rising, bright, *short* flourish layered **over** the dice sound, not replacing it. It must feel like the room reacting, not like a slot machine. Under 1.2 seconds. |
| `d20.natural1` | `Attack.D20 == 1`, or `ChosenRoll == 1` | Not a comedy sound. A dull, dropped, deadened thud — the absence of resonance where the nat-20 has resonance. The joke wears out; the sinking feeling does not. |

Both must be *rarer than they feel*. They fire on 10% of d20 rolls combined, which in a long combat
is often. Keep them short and keep them quieter than instinct suggests — around 6 dB below the
loudest impact — so they punctuate rather than dominate.

**Combat impacts.**

| Cue | Trigger |
|---|---|
| `hit.melee` / `hit.ranged` | `EncounterAttackResult.Attack.Hit == true`, chosen by `AttackName` |
| `hit.critical` | `Attack.Critical == true` — heavier layer, plays *with* `d20.natural20`, not instead of it |
| `miss.whiff` | `Attack.Hit == false` — must exist. A miss that makes no sound reads as a bug. |
| `damage.taken.player` | `DamageResult.EffectiveDamage > 0` on a party member |
| `combatant.dropped` | `DamageResult.DroppedToZero == true` — the body-hits-floor moment |
| `combatant.died` | `DamageResult.Dead == true` — distinct from dropping; a PC at 0 is not dead |

The distinction between `DroppedToZero` and `Dead` is one the engine already draws carefully
(`Engine/GameEngine.cs:143` and `MarkDead` at `:2223-2231`). Audio must draw it too — conflating them
would tell the player someone died when they are still savable, which is a genuine misinformation bug.

**Death saves.** The most emotionally loaded mechanic in 5e, and the engine already models every
branch. Four samples buy a disproportionate amount of drama.

| Cue | Trigger (`DeathSaveResult`) |
|---|---|
| `deathsave.success` | `Roll >= 10`, not stable yet — a held, uneasy tone; relief that resolves nothing |
| `deathsave.failure` | `Roll < 10` — one step down a ladder. Consider pitching each successive failure lower using `Failures` as the index; the sound of the situation worsening. |
| `deathsave.stabilised` | `Stable == true` — release |
| `deathsave.death` | `Dead == true` — the only genuinely final sound in the game. Give it length and let it sit alone; duck everything else under it. |
| `deathsave.natural20` | `Roll == 20` — the character stands back up. Reuse `d20.natural20` with a warmer layer. Per `GameEngine.DeathSaves.cs:18-25` this is mechanically the best possible outcome; it should be the best-sounding one. |

**Turn and initiative.**

| Cue | Trigger |
|---|---|
| `combat.initiative` | `InitiativeSequenceResult.Completed == true` — combat begins; also the music transition point |
| `turn.player` | `NextTurn` result where the new combatant is a party member — a soft, positive marker meaning *it is your move* |
| `turn.round` | `EncounterState.Round` changed across the call — deeper, rarer |
| `combat.end` | encounter status leaves `"active"` — releases combat music |

`turn.player` is quietly one of the most useful sounds here: during a long LLM turn the player may
well have looked away, and an audible "you're up" is genuine functional value, not decoration.

**UI feedback — restrained.** Four samples, all short, all at least 12 dB below combat cues, none
with pitched or melodic content:

- `ui.click` — primary buttons only, not every clickable thing
- `ui.panel` — view or tab change
- `ui.error` — a rejected action or a `RULE_REJECTED` / `INVALID_ARGUMENT` code from
  `Engine/DmToolRouter.cs:222-225`
- `ui.narration` — an *optional, off by default* soft marker when new DM narration lands

Explicitly **no** sound on: hover, focus, scroll, text entry, list selection, save. A desktop app
that clicks at every mouse movement is instantly exhausting, and this one is used for hours at a
sitting.

### 4.4 Adaptive combat music (Phase 3)

Vertical layering, not horizontal re-sequencing. My usual preference is the reverse for memory
reasons, but memory is not a constraint on a desktop machine that is already hosting a multi-gigabyte
language model, and horizontal re-sequencing needs a bar grid this turn-based product does not have.

Four stems over one harmonic bed, each independently faded:

| Layer | Enters at intensity |
|---|---|
| `bed` — sustained low drone, always audible in combat | 0.00 |
| `pulse` — low percussion | 0.30 |
| `drive` — full rhythm | 0.55 |
| `crisis` — high strings / dissonance | 0.80 |

Intensity is computed from state the engine already holds, recomputed once per committed turn and
smoothed toward the target at roughly 0.1 per second so it never jumps:

```
intensity = clamp01(
      0.35                                             // floor: combat is active
    + 0.15 * min(EncounterState.Round / 6.0, 1.0)      // attrition over rounds
    + 0.30 * (1 - partyCurrentHp / partyMaxHp)         // the party is losing
    + 0.25 * (any friendly CombatantState.DeathSaveRequiredThisTurn ? 1 : 0)
    + 0.10 * (round 1 && any friendly .Surprised ? 1 : 0)
)
```

Two rules that matter more than the formula:

- **Intensity falls when the party is winning.** As hostile combatants drop, the health term stops
  rising and the round term should be damped. Music that keeps escalating through a won fight tells
  the player the wrong thing.
- **The `crisis` layer is reserved for death saves.** Nothing else may reach 0.80. If the top layer
  fires during routine combat it stops meaning anything, and the moment a character is actually
  dying has nowhere left to go.

Transitions: 3-second crossfade into combat on `InitiativeSequenceResult.Completed`, 4-second fade
out and return to the exploration bed on encounter end. Never a hard cut.

### 4.5 Ambient beds (Phase 4, blocked)

Design, for when the vocabulary in §3 exists:

- One bed per acoustic category, 90+ seconds, seamlessly looped, deliberately low in detail.
- Transitions on `PartyLocationId` change: 2.5-second crossfade, both beds live simultaneously
  during the overlap. Never stop-then-start.
- Time of day blends free from `CampaignState.MinuteOfDay` (`Models.cs:33`) — a day and a night
  variant per outdoor bed, crossfaded on the clock. This is genuine production value for one extra
  file per outdoor category.
- Ambience ducks by ~6 dB under combat music rather than stopping, so the location does not vanish
  during a fight.
- The default for an unrecognised category is **`silent`, not a guess.**

### 4.6 Spell audio: use the taxonomy, not the spell list

The SRD catalogue at `App/Assets/Rules/srd_spells.json` holds **316 spells**. Authoring a sound per
spell is not viable and never will be.

It is also unnecessary, because `SpellDefinition` (`Models.cs:133-203`) already carries the axes that
matter: `School` (8 values, all populated — Transmutation 54, Conjuration 52, Evocation 50,
Abjuration 46, Enchantment 30, Divination 30, Illusion 28, Necromancy 26), `DamageType`,
`Resolution`, `Level`, and `AreaShape`.

A two-axis lookup gives complete coverage for roughly ten samples:

- **Cast gesture by `School`** — 8 samples. Every spell in the catalogue, and every spell added
  later, is covered on the day it is added.
- **Impact by `DamageType`** — fire, cold, lightning, thunder, force, necrotic, radiant, and the
  physical types, plus one healing shimmer keyed on `SpellCastResult.Healing > 0`.
- **Scale by `CastAtLevel`** — a gain and low-end lift, not a separate sample.

Two supporting details: `SpellTargetResolution.Sequence` (`Models.cs:578`) lets a multi-projectile
spell such as Magic Missile fire staggered impacts about 90 ms apart rather than one stacked hit,
which is a large perceived-quality win for no extra assets. And only 26 of the 316 spells currently
have a supported `Resolution` (the rest are `"unsupported"`), which is a further argument that
per-spell audio would be premature even if it were affordable.

## 5. Cost

### 5.1 Mute and volume, and the default

**An application that makes noise unbidden is worse than a silent one.** The controls are a launch
requirement, not a follow-up.

The plumbing already exists and the change is purely additive. `AppSettings`
(`Models.cs:11-23`) is a plain POCO nested in `AppState.Settings`, serialised by `AppDataStore` to
`%LocalAppData%\DungeonMasterAI\state.json` (`Data/AppDataStore.cs:15, 22-24`) with an atomic
temp-file-plus-`File.Replace` write. New properties with sane defaults deserialise correctly from
existing state files with no schema migration.

```
bool   AudioEnabled      = true    // master kill switch
double MasterVolume      = 0.5
double SfxVolume         = 1.0
double AmbienceVolume    = 0.7
bool   MusicAndAmbienceEnabled = false   // opt-in, see below
```

There is already a settings screen to put them in: `App/Views/SettingsView.xaml` binds directly to
`Settings.*` two-way (`:26` `LlamaServerUrl`, `:49` `PlayerSafeMode`), reachable from the shell nav
(`App/AaaShellWindow.xaml.cs:144`). A slider row is a natural addition. `MainViewModel.Settings` is
already exposed at `App/MainViewModel.cs:147`.

**The recommended default draws a line between reactive and ambient audio:**

- **Reactive audio on by default at 50% master.** Every Phase 1 cue is a direct response to an action
  the player just took. A die makes a noise because the player rolled it. That is not unbidden sound,
  and it is the entire point of the exercise — a Phase 1 that ships muted delivers nothing.
- **Music and ambience off by default.** These start on their own and continue indefinitely. They
  are the ones that ambush somebody at 1 a.m. in a quiet house. Ship them behind an explicit toggle
  with a plain label, and let the player opt in.

This split has a pleasant side effect: Phase 1 needs no first-run consent dialogue at all, because
nothing plays until the player does something.

Two further behaviours, both cheap:

- **Duck to silence when the window is not active.** An LLM turn can complete minutes after the
  player alt-tabbed away. Sound arriving from a background window is startling and rude.
- **A visible mute affordance outside the settings screen.** A speaker glyph in the shell chrome,
  one click, always reachable. Requiring a trip into settings to stop a noise is a bad experience at
  precisely the moment the user is annoyed.

Finally: `MainViewModel` already implements `IDisposable` and is disposed on shell close
(`App/MainViewModel.cs:2348`, wired at `App/AaaShellWindow.xaml.cs:39-41`). The audio engine should
be disposed on the same hook, so the WASAPI device is released cleanly on exit.

### 5.2 Install size

Current shipping profile, from `.github/workflows/windows-ci.yml:134-148`: `dotnet publish
--runtime win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false`,
packaged by Inno Setup (`windows/installer/DungeonMasterAI.iss`) with `Compression=lzma2/max` and
`SolidCompression=yes`, installed per-user to `{localappdata}\Programs\DungeonMasterAI`.

Two things worth knowing. First, the framework-dependent Release build is only 9.6 MB, so the
installer's bulk is almost entirely the bundled .NET runtime and WPF. Second, the app's own asset
payload today is tiny — `Assets/Rules` 204 KB, `Assets/Reference` 32 KB, `Assets/MapPacks` 4 KB — and
`Runtime/` and `Models/` contain only `.gitkeep`, because llama.cpp and the GGUF model are downloaded
after install rather than shipped.

A self-contained ReadyToRun WPF publish of this shape typically lands around 150–250 MB unpacked and
roughly 60–120 MB compressed. **No measured figure exists in the repository** — no build logs or
artifacts are checked in — so this is an estimate from the publish flags. To get a real number,
download the `DungeonMasterAI-installer` artifact from a recent CI run.

Audio's marginal cost against that baseline:

| Item | Uncompressed | Installer delta |
|---|---|---|
| `NAudio.Core` + `NAudio.Wasapi` + `NVorbis` | ~1.0–1.5 MB | ~0.6 MB |
| Phase 1: ~18 SFX, 16-bit mono WAV @ 22.05 kHz, avg 0.6 s | ~0.5 MB | ~0.3 MB (PCM compresses well under LZMA2) |
| **Phase 1 total** | **~2 MB** | **~1 MB** |
| Phase 3: 5 combat stems, 60 s, Ogg Vorbis q3 | ~4 MB | ~4 MB (already compressed) |
| Phase 4: 6 ambient beds, 90 s, Ogg Vorbis q3 | ~7 MB | ~7 MB |
| Phase 2/3: ~10 spell samples | ~0.4 MB | ~0.3 MB |
| **Mature total** | **~14 MB** | **~12 MB** |

**Phase 1 adds roughly 1 MB to an installer already in the 60–120 MB range — about 1%.** The mature
audio layer adds around 12 MB, roughly 10–15%. For a product that currently makes no sound at all,
that is a cheap trade, and Phase 1 is close to free.

Follow the existing asset conventions in `App/DungeonMasterAI.App.csproj:15-25`. Audio files should
be `Content` with `CopyToOutputDirectory="PreserveNewest"`, matching `Assets/MapPacks` and
`Assets/Rules`, rather than WPF `Resource` items — they are loaded by path at runtime, not through
`pack://` URIs, and keeping them loose allows a missing-file fallback (§5.3).

### 5.3 Asset sourcing and licensing

This ships in a public installer, so licensing is a real constraint, not paperwork.

**Recommended sources.**

| Source | Licence | Use |
|---|---|---|
| Sonniss GDC Game Audio Bundle | Royalty-free, worldwide, non-exclusive, commercial use, **no attribution required** | Best default for SFX. Explicitly permits use in distributed games. Note it forbids use as AI/ML training data. |
| Kenney.nl | CC0 | UI sounds. Arcade-leaning; audition against the fantasy tone. |
| Freesound, filtered to **CC0 only** | CC0 | Dice, impacts, room tone. Requires curation; quality varies widely. Filter must be CC0, not "Creative Commons". |
| OpenGameArt, filtered to CC0 | CC0 | Fills gaps. |
| Pixabay audio | Pixabay Content License | Usable, with a caveat — see below. |

**Traps to avoid.**

- **Tabletop Audio is not usable.** It is the first place anyone building a D&D app will look, and
  its tracks are licensed **CC BY-NC-ND 4.0**. The `NC` term rules out a commercially distributed
  product, and the `ND` term additionally forbids the trimming and loop-editing this design requires.
  The creator offers case-by-case arrangements via Patreon, but that is a negotiated licence, not
  something to build a shipping default on. Flagging this explicitly because it is the most likely
  mistake.
- **Pixabay's standalone-redistribution clause.** Pixabay audio may be used commercially as part of a
  larger creative work but may **not** be distributed on a standalone basis. Loose `.ogg` files sitting
  in an install directory sit uncomfortably close to that line. If Pixabay material is used, embed it
  in a pack container rather than shipping bare files — which is worth doing anyway.
- **Any CC BY source** (Kevin MacLeod / Incompetech, much of Freesound) is usable but obliges
  attribution *in the shipped product*, which means an in-app credits screen must exist before the
  first such asset lands.
- **Public-domain classical recordings** are a trap: the composition may be public domain while the
  *recording* is separately copyrighted.

**Mechanism: mirror the map asset pack design.** This repository has already solved exactly this
problem once. Per `docs/map-asset-packs.md`, map packs carry a `manifest.json` with a stable pack ID,
author, **licence, licence and source URLs, and credits**, and the renderer falls back procedurally
when an image cannot be loaded, so removing a pack never breaks the product. `srd_spells.json`
likewise records `"license": "CC-BY-4.0"` inline.

Audio should adopt the same pattern rather than invent one:

- An audio pack manifest with per-asset `license`, `sourceUrl`, and `credits`.
- Search `Assets/Audio` beside the executable, then `%LOCALAPPDATA%/DungeonMasterAI/Audio` for
  user-installed packs — the same two-location search map packs use, which also gives users a
  supported way to supply their own material.
- **A missing file is silence, never an exception.** Audio is decoration; it must never be able to
  break a session.
- An in-app credits screen generated from the manifests, so attribution obligations are satisfied
  automatically as assets are added.

If CC0 material cannot carry the intended tone — likely for music specifically, where free libraries
are weakest — commissioning roughly five minutes of layered stems is the honest alternative, and that
is a budget decision for the product owner rather than a technical one.

### 5.4 CPU, with a local LLM already saturating the machine

The starting point is better than it looks. `LlamaRuntimeManager` runs llama.cpp **out of process**
(`AI/RuntimeBootstrapService.cs`, `AI/LlamaRuntimeManager.cs`), reached over HTTP at
`http://127.0.0.1:8080` (`Models.cs:13`). The application's own UI thread is largely idle while a
turn is being generated. The contention is for total system CPU and memory bandwidth, not for
threads inside this process.

That said, an audio callback that misses its deadline produces an audible glitch, and a machine
pinned at 100% by inference is exactly where that happens. The design should trade latency for
robustness at every opportunity, because **latency does not matter in a turn-based application.**

Budget and rules:

- **Target: under 1% of one core, under 25 MB of audio memory.** A float mixer summing 16 voices at
  44.1 kHz is a few million multiply-adds per second — trivially within budget. The risk is never
  throughput; it is scheduling.
- **WASAPI shared mode with a large buffer, 100–200 ms.** Do not use exclusive mode and do not chase
  low latency. NAudio's conservative default is appropriate here for once. A 150 ms delay between
  clicking Attack and hearing the die is imperceptible in this context; a buffer underrun is not.
- **Preload and fully decode every SFX to memory at startup.** They total under a megabyte. No disk
  I/O and no decode may ever occur on the audio path.
- **Stream music and ambience from a background thread** with a generous read-ahead, so a stalled
  disk read cannot starve the mixer.
- **Cap concurrent voices at 16**, dropping the oldest non-critical voice on overflow. The realistic
  peak is a multi-projectile spell resolving against several targets. Death and death-save cues are
  exempt from stealing.
- **One output device, one mixer, for the process lifetime.** Never open and close a device per
  sound; device initialisation is the expensive operation and doing it under load is what causes
  audible hitches.
- **Never touch the mixer from the UI thread and never touch WPF from the audio thread.** The
  codebase's existing convention is `Dispatcher.BeginInvoke` (`App/MainWindow.xaml.cs:65-72`) and
  `AsyncRelayCommand` (`App/RelayCommand.cs:13-25`); audio should post to a lock-free queue the
  mixer drains, and follow the same dispatcher idiom if it ever needs to update bound state.

One measurement is worth taking before Phase 3 ships: run a combat encounter with generation active
on the lowest target hardware and confirm no dropouts. If glitches appear, the first lever is buffer
size, not sample quality.

## 6. Phased plan

### Phase 1 — "It makes a sound when you roll" (smallest version that delivers most of the feel)

Roughly 18 samples, ~1 MB of installer growth, no new data model, nothing blocked.

1. `AudioEngine` in the App project: `WasapiOut` (or NAudio 3 `WasapiPlayer`) + `MixingSampleProvider`
   with `ReadFully = true`, four `VolumeSampleProvider` buses under one master, disposed on the
   existing `MainViewModel.Dispose` hook (`App/MainViewModel.cs:2348`).
2. `AppSettings` additions (§5.1) and a slider row plus mute toggle in `Views/SettingsView.xaml`.
3. Tier A event-log breadth (§2.4) for turn, initiative, damage, and death-save coverage.
4. Tier B typed cues for the earned moments: natural 20, natural 1, critical hit,
   `DroppedToZero`, `Dead`, and each death-save branch.
5. Dice with 3–4 randomised variants, pitch and gain jitter, no immediate repeats.
6. The four restrained UI sounds.

Ship this alone and the product stops sounding like a spreadsheet. Everything after this is
elaboration.

### Phase 2 — Spell audio and polish

School-based cast gestures and damage-type impacts (§4.6), staggered multi-projectile impacts via
`SpellTargetResolution.Sequence`, concentration-broken cue from `ConcentrationCheckResult.Maintained`,
and the audio pack manifest plus credits screen (§5.3). Roughly +0.3 MB.

### Phase 3 — Adaptive combat music

The four-layer intensity system in §4.4. This is the first phase that needs either a composer or a
serious curation effort, and the first with a real budget question. Roughly +4 MB.

### Phase 4 — Ambient beds

**Blocked on the Game Designer defining the acoustic vocabulary described in §3.** Not startable
until that exists. Roughly +7 MB.

### What I would not build

- **Per-spell sound design.** 316 spells, only 26 with a supported resolution. The taxonomy approach
  in §4.6 gives complete coverage for a tenth of the effort and covers spells added later for free.
- **Voice acting or TTS narration.** A local LLM already competing for the machine, plus per-line
  generation latency measured in seconds, plus an enormous asset or model footprint. The narration
  is text and should stay text.
- **Spatial audio, HRTF, occlusion, reverb zones.** There is a 2D tactical grid, not a 3D scene. A
  stereo pan derived from grid X would be a gimmick that survives one demo. My usual spatial rig has
  no application here and I would push back on a request for it.
- **A first-run audio setup wizard.** The reactive/ambient default split in §5.1 makes it unnecessary.
- **Audio middleware.** See §1.

### The bottom line

**Audio is worth doing now, but only Phase 1 is worth doing now.**

Phase 1 costs roughly 1 MB of installer growth, one new managed dependency, no engine changes, no
data-model changes, and no design decisions that are not already made. It targets the single most
repeated interaction in the product — rolling a d20 — and it makes the game's two most dramatic
mechanical events, the natural 20 and the death save, actually feel like events. The engine already
computes every field required, and has already decided which outcomes are special; audio only has to
agree with it.

Phases 3 and 4 have real costs — composition budget, a data-model dependency, ~11 MB — and should be
scheduled deliberately rather than assumed. In particular, the ambient beds that seem like the most
obvious win are the one part of this that cannot be built correctly today.
