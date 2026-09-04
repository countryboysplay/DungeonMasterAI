# Unity Target Architecture

**Status:** proposal, r63. Nothing here is built yet. Unity is not installed on the development
machine, so every version and package claim below is a decision to validate at install time, not an
observation.

**Decision this document serves:** Unity replaces the WPF front end entirely. The purpose is
**craft and feel**, not distribution — the owner's framing was *"At this point I just want to use
Unity to make a fully polished game. I'm not so much worried about moving it to Steam and
production."* Everything downstream follows: no store, no ratings, no Deck, no Proton, no netcode.
Windows desktop, single player, a party of four PCs, one person at the keyboard.

**Companion specs.** This document is the architecture. The behaviour it must produce is specified
in `docs/game-feel-direction.md`, `docs/narrative-direction.md`,
`docs/encounter-design-direction.md`, `docs/audio-direction.md`, `docs/progression-direction.md`,
`docs/tactical-map-schema-v1.md`, `docs/map-asset-packs.md` and `docs/ai-map-generation.md`. Those
stop being aspirational at r63 — they are the requirements. Note they predate the four-real-PCs
decision and mostly assume a single protagonist; §9.9 says where that bites.

---

## 0. The thesis

> **The engine is already the game. Unity is a presentation client for a state machine it does not
> own.** The port succeeds or fails on whether that is still true after six months of "just this
> one little check in the view."

`DungeonMasterAI.Domain` (10 files, 1,749 lines) and `DungeonMasterAI.Engine` (43 files, 13,115
lines) contain zero references to `System.Windows`, `System.Drawing` or any dispatcher type —
verified by grep across both projects, not assumed. That is ~15,000 lines of adjudicated D&D 5e
that survives the port untouched, and it is the only reason this is a few months of work rather
than a rewrite. Protecting it outranks every aesthetic goal in this document.

The specific failure mode to design against is not "Unity is hard." It is **two half-engines**: a
Unity `TokenView` that decides for itself whether a square is reachable, a `CombatUI` that decides
for itself whether an attack has advantage, a `ScriptableObject` that holds a damage die. Each is
individually reasonable and collectively fatal, because the moment Unity and the engine disagree
about a rule, the game is lying to the player and no test catches it.

Sections 3.3 and 3.4 make that structurally impossible rather than merely discouraged.

---

## 1. Verified ground truth

Everything here was read directly in the r62 tree at `8f21a7f`. Where I am inferring rather than
observing, I say so.

### 1.1 The engine has no events, no async, and no UI seam

`GameEngine` exposes **no `public event`** of any kind — grep across all 25 `GameEngine.*.cs`
partials returns nothing. Its entire surface is synchronous methods that mutate a `CampaignState`
and return a result record. `DiceService`'s constructor takes a `Func<int,int,int>`; that is the
only injection seam in the engine.

This matters more than it sounds. There is **no stream of "something happened" notifications** to
animate against. The return value *is* the notification. A `TokenView` cannot subscribe to
"combatant took damage"; something must call the engine, read
`EncounterAttackResult.Damage.HpLost`, and drive presentation from that.

`docs/audio-direction.md` independently reached the same conclusion and named it precisely: *"The
engine has no events, no `INotifyPropertyChanged`, no `IObservable` — nothing to subscribe to."*
Section 3.4 builds the whole boundary around this fact.

### 1.2 The result records were already written as presentation scripts

The happiest discovery in the codebase. From `Domain/Models.cs:605`:

```
/// One character's share of one XP award. Carries everything the presentation layer needs to
/// render the award beat without recomputing anything: the amount, where it came from, the new
/// total, and how far the bar has left to travel.
public sealed record ExperienceAward(... int Amount, int NewTotal, int Level,
    int ExperienceToNextLevel, bool CrossedThreshold, string SourceKind, ...);
```

and `Domain/Progression.cs:98`:

```
/// How far through the current level band a total sits, 0.0 to 1.0. The presentation layer
/// binds this rather than recomputing it, so one definition of "the bar" exists.
public static double LevelProgressFraction(int experiencePoints)
```

The engine already thinks in *beats* and already refuses to make the view recompute anything. The
architecture below continues that intent rather than imposing on it. **Where a result record lacks
a field the presentation needs, the fix is to add the field to the record — never to compute it in
Unity.** Section 10 lists the fields I already know are missing.

Every result record also carries a `Summary` string. WPF rendered those directly, in the DM's
voice, which `docs/narrative-direction.md` identifies as a defect: 23 of the 24 call sites that
append an `assistant` chat message are engine strings, not model output, and the player reads
`"🎲 Death save 20: regains 1 HP."` under the heading "Dungeon Master". **Unity must treat
`Summary` as a debug log line and a last-resort fallback, never as narration.** The structured
fields beside it drive the presentation.

### 1.3 The engine is a resumable state machine with two pause slots

`CampaignState` carries:

- `PendingRollRequest? PendingPlayerRoll` (`Models.cs:411` — `ResolutionKey`, `RollMode`,
  `Modifier`, `TargetNumber`, `TargetLabel`, `Context`)
- `PendingPlayerDecision? PendingPlayerDecision` (`PlayerDecisions.cs` — `DecisionType`, `Prompt`,
  `Options[]` each with `Label`/`Description`/`Emphasis`)

Roughly forty `Request*` / `Resolve*` methods pair with them. A player-character attack does not
resolve in one call: it parks a `PendingRollRequest`, returns, waits for the front end to supply
the d20, continues, parks again for the damage roll, and possibly again for a concentration save.

`EnsurePendingPlayerRollForActiveCombat(campaign)` exists specifically so a front end can ask "is
the machine waiting on me?" after a reload. **That method is Unity's resume primitive** and must be
called on campaign load and after every transaction.

The practical consequence: **the Unity combat loop is a pump, not a call.** Issue an intent, then
drain pending roll and decision requests until the engine stops asking. Any design that assumes
"the player clicks Attack and an attack happens" is wrong within one sprint.

`PlayerDecisionOption.Emphasis` already exists (`"normal"` and presumably others) — that is a
presentation hint the engine is offering and Unity should honour it in button styling.

### 1.4 Validation is exception-based

Illegal moves throw `InvalidOperationException` with player-readable messages
(`GameEngine.TacticalMapCombat.cs:96-99`, `GameEngine.cs:1921-1930`):

```
throw new InvalidOperationException($"Movement from grid ({previous.X}, {previous.Y}) to ({x}, {y}) is blocked by a wall or closed door on '{map.Name}'.");
```

Unity must never let one of these reach the player as a crash or console spam. Every engine call
goes through one wrapper that catches and converts to a typed rejection (§3.4.3).

### 1.5 Movement is straight-line only — there is no pathfinder

`GameEngine.TraceGridPath` (`GameEngine.cs:1907`) is eleven lines and walks a Chebyshev diagonal:

```csharp
while (x != toX || y != toY)
{
    if (x < toX) x++; else if (x > toX) x--;
    if (y < toY) y++; else if (y > toY) y--;
    points.Add((x, y));
}
```

If anything sits on that line — a wall edge, an unwalkable cell, another combatant — the move
throws. `ValidateMovementPath` says so in the message it raises: *"The straight-line path is
blocked by another combatant… Choose a different destination or path."* That message is the WPF
front end's UX leaking into an exception string, and it is not acceptable in a game. Clicking a
tile around a corner must make the character walk around the corner. §6.6 solves it without putting
a pathfinder's *authority* into Unity.

Note each step costs a flat 5 ft including diagonals (`MovementStepCostFeet`, `GameEngine.cs:1945`;
Prone adds 5, difficult terrain adds 5 once regardless of how many sources make it difficult). Any
Unity path planner must use **Chebyshev distance** — not Euclidean, not Manhattan — or its cost
preview will disagree with the engine.

### 1.6 Two line algorithms exist, for two different jobs

- `GameEngine.TraceGridPath` — Chebyshev, for **movement**.
- `SpellAreaGeometry.TraceGridLine` — Bresenham, for **line of effect**.

They yield different cells. Unity must use the right one per preview: movement ghosting uses the
first, spell targeting the second. Getting this wrong produces a preview that lies, which is worse
than no preview at all.

### 1.7 The tactical map schema is, by accident, a URP 2D scene description

`TacticalMap` (`Domain/TacticalMaps.cs`) already carries everything a 2D lit renderer wants:

| Schema | Renders as |
|---|---|
| `WidthSquares` / `HeightSquares` / `FeetPerSquare` | Grid bounds; 1 Unity unit = 1 square = `FeetPerSquare` ft |
| `TacticalMapRoom.FloorAssetKey` / `WallAssetKey` | Tilemap fills |
| `TacticalMapWall` (`FromX/FromY → ToX/ToY`, on grid **edges**) | Wall sprites + `ShadowCaster2D` where `BlocksLineOfSight` |
| `TacticalMapDoor` (edge-anchored, `Orientation`, `State`) | Animated prefab; shadow caster toggled by `State` |
| `TacticalMapTerrain` (`DifficultTerrain`, `Cover`, `HeavilyObscured`) | Overlay tint + cover badge |
| **`TacticalMapLight`** (`double X/Y`, `BrightRadiusFeet`, `DimRadiusFeet`, `Color` hex) | **`Light2D`: inner = bright, outer = bright+dim, colour parsed from hex** |
| `TacticalMapVisibility` (`RevealAll`, `RevealedRoomIds`, `RevealedCells`) | Fog-of-war mask texture |
| `TacticalMapSpawnPoint.Side` (canonical `CombatSide`) | Token spawn, side-coloured base ring |
| `Seed` vs `GenerationSeed` | Art variant selection rerollable without touching geometry |

`TacticalMapLight` has sub-cell `double` coordinates, a bright radius, a dim radius and a colour.
That is a light, not a decoration. **This is the strongest argument for the render pipeline choice
in §2.3, and it comes from the data rather than from taste.**

**Coordinate handedness is the first thing to get right.** `(0,0)` is the **upper-left** cell and Y
increases **downward**. Unity is Y-up. Cells, rooms, terrain, props, zones and spawn points live in
cell space `0..W-1`; **walls and doors live in grid-line space `0..W` inclusive** — one larger. §6.2
specifies the single conversion.

### 1.8 Presentation-safe geometry already lives in Domain

`SpellAreaGeometry` is documented as *"UI-neutral tactical geometry shared by the deterministic
spell engine **and battlefield preview**."* `EnumerateCells(shape, sizeFeet, originX, originY,
direction, widthFeet)` returns exactly the cells a fireball will hit, including the half-square
allowances baked into the cone and cube tests that a reimplementation would get wrong. Unity's AoE
template calls this. It does not draw a circle that looks about right.

`CombatSide` (`Domain/CombatSide.cs`, r62) is the one vocabulary for sides, with a synonym table
collapsing `player`/`ally`/`enemy`/`hostile`/etc. onto `party`/`opposition`/`neutral`. Unity token
colouring routes through `CombatSide.IsParty` / `IsOpposition` / `IsNeutral` and never compares
side strings itself.

### 1.9 State changes are transactional, and beats belong after the commit

r60 landed "validate before mutating so a rejected tool call cannot commit state", and
`DungeonMasterAI.Data/CampaignCloneService.cs` exists to clone a `CampaignState` so an LLM turn can
run against a copy and commit only on success.

`docs/audio-direction.md` draws the correct conclusion and it generalises to every kind of
presentation: **"Sound is a consequence of committed state, not of computation."** A crit sting for
a blow that was rolled back is worse than silence. The same is true of a damage number, a screen
shake, an XP bar and a narration line.

**Therefore: beats are emitted from the committed transaction, never from inside the engine call.**
This is the single most important sequencing rule in the document and §3.4 is built on it.

### 1.10 The AI sidecar is a plain child process

`DungeonMasterAI.AI/LlamaRuntimeManager.cs:71-118` spawns `llama-server` as a child process;
`LocalDmClient` talks to it over HTTP on `127.0.0.1`. There is no P/Invoke into llama.cpp.
`LlamaRuntimeManager.cs:202` P/Invokes `kernel32.dll` only to count physical cores — Windows-only,
which is fine here.

Unity can spawn the identical sidecar with `System.Diagnostics.Process`. Runtime and model are
pinned and hash-verified: `windows/runtime.lock.json` → llama.cpp `b10786` CPU build (18.4 MB);
`windows/model.lock.json` → `Qwen3.5-4B-Q4_K_M.gguf` (2.74 GB, fetched on first run because it
exceeds GitHub Releases' 2 GiB per-file limit, verified by SHA-256 twice).

**Discrepancy to resolve.** `docs/narrative-direction.md` reports
`AppSettings.HuggingFaceModel` defaulting to a **9B** model while the r62 `Models.cs:20` I read has
it defaulting to `""` with `ModelPath = "Qwen3.5-4B-Q4_K_M.gguf"` and an explicit comment that the
`-hf` branch is deliberately avoided. The r62 code is almost certainly right and the doc stale, but
confirm before tuning generation settings — the difference is ~3× in latency and the entire pacing
design in §7 assumes 4B.

Note also that `Models.cs` and `narrative-direction.md` disagree on generation parameters:
`AppSettings` ships `Temperature = 0.4` / `MaxTokens = 700` / `ContextSize = 8192` with a comment
that 0.75 *"measurably degrades tool-call argument fidelity on a 4B model"*; the narrative doc
assumes 0.75 / 700 / 16384. Section 7.7 treats this as an owner decision, because it is a real
tension between prose quality and tool-call reliability.

### 1.11 Test coverage is partly hostage to WPF

33 test projects, ~11,000 lines. Six reference `DungeonMasterAI.App` and target `net10.0-windows`:

| Project | Lines |
|---|---|
| `DungeonMasterAI.GuiSmokeTests` | 485 |
| `DungeonMasterAI.MapAssetTests` | 222 |
| `DungeonMasterAI.MapRendererTests` | 176 |
| `DungeonMasterAI.R56MapBuilderTests` | 217 |
| `DungeonMasterAI.R57MapEditingTests` | 187 |
| **`DungeonMasterAI.R62MapCombatTests`** | **563** |

Deleting WPF deletes ~1,850 lines of regression coverage over exactly the systems Unity is
rebuilding. `R62MapCombatTests` is the painful one: it mixes genuine engine assertions about
map-bound combat with `CombatGridControl` pixel snapshots in one file
(`using System.Windows.Media.Imaging;` at line 7, `RenderToPixels(CombatGridControl…)` at 366).

**Recommendation, and it must happen before the WPF project is deleted:** split
`R62MapCombatTests` into a `net10.0` engine-assertions project and a discardable
renderer-snapshot project. Same for `MapAssetTests`, `R56MapBuilderTests`, `R57MapEditingTests`.
The engine half is worth keeping forever; the WPF half dies with WPF and should be allowed to.

Unity's own test story is thin by comparison and §11.6 is honest about that.

---

## 2. Unity version, scripting backend, render pipeline

### 2.1 Version: Unity 6.3 LTS (`6000.3.x`)

Pin **Unity 6.3 LTS**. It shipped December 2025 as the first LTS since Unity 6.0 and carries
two-year support to **December 2027**, comfortably outliving this project's interesting period.
Unity 6.0 LTS ends support October 2026 and should not be started from.

Install via Unity Hub with **Windows Build Support (Mono)**. Nothing else — no Android, no WebGL,
no IL2CPP module unless §2.2 is revisited.

*Uncertainty flagged:* take the newest `6000.3.x` patch in the Hub, not a tech-stream `6000.4+`
build. If the Hub offers a newer version explicitly marked **LTS**, take that and treat this
section as stale.

### 2.2 Scripting backend: **Mono**, deliberately, with an expiry date

Unity's default recommendation for a shipping desktop player is IL2CPP. **Do not use it here.**

The reason is specific, not general. `DungeonMasterAI.Engine` uses `System.Text.Json` in at least
seven files (`DmToolRouter.cs`, `GameEngine.ProjectilePlayerRolls.cs`,
`GameEngine.AreaSpellPlayerRolls.cs`, `GameEngine.AutoProjectilePlayerRolls.cs`,
`GameEngine.SpellSaveDamageRolls.cs`, `GameEngine.ReadiedSpellDecisions.cs`,
`GameEngine.SpellPlayerRolls.cs`), and `DungeonMasterAI.Data/AppDataStore.cs` uses it for the
entire save file. `System.Text.Json`'s non-source-generated path is reflection-driven. IL2CPP is
ahead-of-time and strips aggressively; reflection-driven serialization against stripped types is
the classic way a Unity build works in the editor and dies in the player, and the fix is a
hand-maintained `link.xml` that silently rots.

Mono has none of those problems, JIT-compiles the same IL the .NET SDK produced, iterates faster,
and — with no store submission, no console and no distribution requirement — costs nothing this
project cares about. Startup is slightly slower and managed code slightly slower; a turn-based game
on a 30×20 board will not notice either.

**Expiry date, and it matters.** Unity is replacing the scripting runtime with CoreCLR; current
guidance is that **Unity 6.8 removes Mono as an option**, with CoreCLR presently experimental and
desktop-only. So:

- Mono is correct on 6.3 LTS today.
- When a CoreCLR-based LTS stabilises (expect 2027), revisit. CoreCLR runs modern .NET, which
  likely makes the entire `netstandard2.1` bridge in §3.1 **unnecessary**.

Treat `netstandard2.1` as a **bridge with a known end date**, not the permanent shape of the
codebase. Keep the `net10.0` target alive in the multi-target for exactly that reason.

### 2.3 Render pipeline: **URP with the 2D Renderer**

Not built-in, not HDRP.

**Why, from the data rather than from taste:** `TacticalMapLight` already models point lights with
bright/dim radii and a colour, and `TacticalMapWall.BlocksLineOfSight` already marks which walls
occlude. URP's 2D Renderer gives `Light2D` and `ShadowCaster2D` as first-class components mapping
one-to-one onto those fields. A wall torch becomes a `Light2D` with
`pointLightInnerRadius = BrightRadiusFeet / FeetPerSquare` and
`pointLightOuterRadius = (Bright + Dim) / FeetPerSquare`, colour parsed from the hex string, and the
wall it hangs on casts a real shadow. In built-in none of that exists and all of it would be
hand-rolled shaders.

`docs/encounter-design-direction.md` then makes lighting *load-bearing for gameplay reading*:
critical-path rooms carry 2+ lights, optional areas 0–1, **"ambush positions are unlit — an
opposition spawn with no light within its bright radius *is* an ambush"**, warm `#E39A52` braziers
for tended space, and "the exit is visible" as a rule. That is a design language that only works if
lights are real. URP 2D makes it real for free.

URP also brings the **Volume framework** — scene-scoped, blendable post-processing profiles — which
is exactly the mechanism for "combat looks different from exploration": a `Volume` on the Encounter
scene with vignette, chromatic aberration and colour grading, weighted up on initiative and down on
encounter end. And URP 2D supports Shader Graph, where fog of war, AoE templates and reachability
overlays belong rather than in CPU-generated meshes.

**The honest tradeoff.** URP is not free:

- 2D lighting is a real per-light cost and gets slow with many overlapping lights on large sprites.
  A 30×20 map with a dozen torches is fine; sixty lights is not. Cap active `Light2D` count and cull
  by camera bounds. The schema permits maps up to **500×500** (`TacticalMapGeometry.Validate`),
  which at the encounter doc's density budget of 2–4 lights per 100 squares would be **5,000–10,000
  lights**. That is not a hypothetical — put a hard cap in the renderer and light only what the
  camera can see.
- Every sprite that should receive light needs a **lit** material (`Sprite-Lit-Default`). Mixing lit
  and unlit sprites is the number-one "why is my map black" mistake. UI stays unlit and outside the
  2D light pass.
- URP upgrades between Unity versions are occasionally disruptive in ways built-in never is.
- Built-in genuinely would be simpler if the game were flat unlit sprites. The schema says
  otherwise.

HDRP is straightforwardly wrong: no 2D renderer, built for high-end 3D deferred lighting, weeks of
cost for nothing.

### 2.4 Camera and projection: orthographic top-down, not isometric

The grid is authored in axis-aligned integer squares with edge-anchored walls. Isometric would
require transforming every one of those for display and every player click back — a whole class of
off-by-one bugs on a system where "which square is this" is load-bearing for adjudication.
Top-down orthographic makes screen space and grid space differ by a single scale-and-offset, which
is worth more than the visual novelty.

Depth and mood come from **lighting, shadow casters, prop layering and slight parallax on
decorative layers**, not from the camera. `TacticalMapTerrain.ElevationFeet` reads as a rim light
and a drop shadow, not a Z offset — and note that field is currently **inert in the engine** (§10.4).

### 2.5 Package set

| Package | Status | Why |
|---|---|---|
| `com.unity.render-pipelines.universal` | Committed | §2.3 |
| `com.unity.inputsystem` | Committed | Rebindable input; clean action maps per mode (Table / Encounter / MapBuilder) |
| `com.unity.cinemachine` (3.x) | Committed | Framing, confiner to map bounds, **Impulse** for shake |
| `com.unity.ugui` (brings TextMeshPro) | Committed | World-space HUD; §2.6 |
| `com.unity.2d.tilemap` + `com.unity.2d.sprite` | Committed | Floor/terrain tilemaps |
| `com.unity.addressables` | Committed | Map asset packs, portraits, audio banks; §5.4 |
| `com.unity.test-framework` | Committed | EditMode tests over the Session layer; §11.6 |
| `com.unity.2d.animation` | Recommended | Skeletal token animation if tokens outgrow a bob and a flash |
| **PrimeTween** *or* DOTween Pro | Recommended | Unity has no built-in tween. §6.8 leans on this heavily. PrimeTween is allocation-free and free; DOTween is more familiar. **Pick one and never mix.** |
| `com.unity.nuget.newtonsoft-json` | Conditional | Only for Unity-side config. Campaign saves keep the engine's own `System.Text.Json` path — §3.5 |

**Explicitly rejected, with reasons:**

- **DOTS / Entities / Burst / the Job System.** The simulation is a ≤500×500 grid with under twenty
  combatants, adjudicated by a synchronous C# library that runs in microseconds. ECS buys nothing,
  costs a rewrite of the presentation layer into a paradigm hostile to the reference-type
  `CampaignState` model, and makes the `GameEngine`↔Unity boundary dramatically harder to keep
  clean. The only DOTS-adjacent thing worth *considering* is Burst for a large fog-of-war raycast
  pass on a 500×500 map, and even that is premature. **This is a case where the tempting advanced
  answer is the wrong one, and saying so is more useful than using it.**
- **Netcode for GameObjects / Unity Gaming Services.** Multiplayer is deferred. No hooks, no
  abstractions "in case" — an abstraction built for a deferred feature is a tax paid forever on a
  feature that may never arrive.
- **Unity Timeline for combat beats.** Timeline is authored, linear and asset-driven; combat beats
  are generated at runtime from engine results and vary per action. Timeline is right for a fixed
  intro cinematic and wrong for the combat loop. §6.8 uses a runtime beat scheduler instead.
- **FMOD / Wwise.** See §8.1 — `docs/audio-direction.md` already rejected them and the reasoning
  survives the move to Unity, with one caveat worth an owner decision.

### 2.6 UI: **UI Toolkit for documents, world-space uGUI for the map**

- **UI Toolkit (UIElements)** for everything that is a document: narration column, character sheets,
  campaign library, settings, quest log, inventory, level-up, Map Builder inspectors. Real flexbox
  layout, USS styling, and — decisively for a text-heavy AI DM — far better handling of long
  scrolling rich text than uGUI. The same UXML/USS drives in-editor tooling for free.
- **uGUI in world space** for anything anchored to the grid: floating damage numbers, token HP arcs,
  condition icon strips, initiative badges. These follow world transforms and sort against sprites,
  which is uGUI's strength and UI Toolkit's current weakness.

This split costs two UI systems and two mental models, and should be stated as such. The
alternative (all uGUI) makes the narration column and character sheets significantly worse, and
narration is the product. Worth it — but worth revisiting once if UI Toolkit world-space support
has matured by the time the map is built.

**The visual language is already decided and must not be relitigated.** `docs/game-feel-direction.md`
is explicit: ground `#070B0E`, single gold accent `#C7A25C`, **Georgia** for anything diegetic,
**Segoe UI** for chrome, uniform 3–4px corner radii, one shared drop shadow. *"Do not restyle."* The
only permitted additions are **motion** and a small set of outcome-state colours drawn from the
existing `AaaRed` / `AaaGreen` / `AaaGoldBright`. Port the palette into a USS variable file and a
`ThemePaletteSO` and treat it as fixed.

---

## 3. How the engine is consumed, and the boundary that keeps it authoritative

This is the section that matters most.

### 3.1 The `netstandard2.1` bridge, and what specifically blocks it

Unity 6.3's scripting runtime is .NET Standard 2.1. A `net10.0` assembly will not load. The
assumption for this document is that `Domain`, `Engine` and `Data` get multi-targeted
`netstandard2.1;net10.0` and Unity consumes the `netstandard2.1` output as a precompiled DLL.
Another agent owns that migration; this section states what I found that will make it harder than
"add a TFM", because these are the things most likely to blow up that estimate.

**Blocker 1 — `ArgumentNullException.ThrowIfNull` (.NET 6+). ~90 call sites.** Spread across
`Spellcasting.cs` (18), `GameEngine.cs` (15), `GameEngine.PlayerRolls.cs` (10),
`GameEngine.AreaSpellPlayerRolls.cs` (10), `GameEngine.UnarmedPlayerRolls.cs` (8),
`GameEngine.Progression.cs` (8), `CombatSide.cs`, and a dozen more partials. You **cannot polyfill
this** — you cannot add a static method to `System.ArgumentNullException`. The options are a
`#if !NET6_0_OR_GREATER` shim namespace trick (fragile), or a mechanical rewrite to a
`Guard.NotNull(x)` helper or plain `if (x is null) throw new ArgumentNullException(nameof(x));`.
Roughly 90 edits, mechanical but not free, and each one is a chance to change semantics if done
carelessly. **This is the single largest mechanical cost of the multi-target and it deserves to be
estimated explicitly.**

**Blocker 2 — `required` members (C# 11). ~60 uses**, heaviest in `DmToolRouter.cs` (19),
`GameEngine.PlayerRolls.cs` (9), `GameEngine.cs` (5). These compile fine with the .NET 10 SDK
targeting `netstandard2.1`, **but only if `RequiredMemberAttribute`, `CompilerFeatureRequiredAttribute`
and `SetsRequiredMembersAttribute` exist.** They do not exist in netstandard2.1. Fix is a small
`#if NETSTANDARD2_1` file declaring them as `internal` in `System.Runtime.CompilerServices`. Same
for **`IsExternalInit`**, which every `record` and every `init` setter needs — and this codebase is
built on records. Cheap fix, non-obvious failure mode, and it fails at compile time with a
confusing error, so write it down.

**Blocker 3 — `StringSplitOptions.TrimEntries` (.NET 5+).** Two uses in `Spellcasting.cs`. Trivial.

**Non-blockers, verified:** `Math.Clamp` (netstandard2.1 ✓), collection expressions `[]` and
file-scoped namespaces (compile-time syntax only), `Path.IsPathRooted`, `DateTimeOffset`,
`record struct`, nullable reference types. **No reflection, no `dynamic`, no `Activator`** anywhere
in Domain or Engine — grep confirms — which is a genuine asset.

**`Data` is portable with one carve-out.** `AppDataStore` (schema version **5**, `state.json` +
`state.previous.json` + a `Recovery` directory under `LocalApplicationData`), `CampaignCloneService`,
`CampaignReadinessValidator`, `CampaignRehearsalService`, `CampaignExpansionApplyService` and
`SrdSpellCatalogService` are all pure. **`CampaignImportService` depends on PdfPig** (`UglyToad.PdfPig`)
for `ExtractSourceAsync`/`ImportAsync`; `ImportManifestJson` and `CompileText` do not. PdfPig
targets netstandard2.0 and would probably load, but pulling a PDF parser into a game player is
poor taste. **Recommendation: `#if` the PDF path out of the netstandard2.1 target and keep PDF
campaign import as an out-of-game `net10.0` CLI tool.** §9.7.

### 3.2 Where the DLLs live

```
Assets/
  Plugins/
    DungeonMasterAI/
      DungeonMasterAI.Domain.dll        # netstandard2.1 build
      DungeonMasterAI.Engine.dll
      DungeonMasterAI.Data.dll
      DungeonMasterAI.Domain.xml        # XML docs — Rider/VS surface them in Unity
      DungeonMasterAI.Engine.xml
      DungeonMasterAI.Data.xml
      Third-Party/                      # §3.5
      DungeonMasterAI.Engine.pdb        # portable PDB for step-debugging into the engine
```

Bring the XML doc files. The engine's doc comments are unusually good — `CombatSide.cs` explains
*why* three vocabularies collapsed into one — and losing them at the Unity boundary is a real cost.
Bring the portable PDBs too; being able to step from a Unity `EngineSession` call into
`Spellcasting.cs` is worth a great deal when a rule looks wrong.

**Build these with a script, not by hand.** `tools/build-engine-for-unity.ps1` should
`dotnet build -f netstandard2.1 -c Release` the three projects and copy outputs into
`Assets/Plugins/DungeonMasterAI/`. A hand-copied DLL will go stale and produce an afternoon of
debugging a bug that was fixed a week ago. Wire it into the existing CI as a build-only job so a
Domain/Engine change that breaks the netstandard2.1 target fails on the PR, not three weeks later
in Unity.

### 3.3 The boundary is enforced by the assembly graph, not by discipline

This is the mechanism. Everything else in this document is a consequence.

```
DungeonMasterAI.Domain.dll   (data types, CombatSide, SpellAreaGeometry, Progression)
DungeonMasterAI.Engine.dll   (GameEngine, DmToolRouter, TacticalMapGeometry, DiceService)
DungeonMasterAI.Data.dll     (AppDataStore, clone, validators)
        ▲                            ▲                         ▲
        │                            │                         │
        │                    ┌───────┴─────────────────────────┘
        │                    │
        │            DMAI.Session.asmdef      ← the ONLY assembly referencing Engine + Data
        │            (plain C#, no UnityEngine types)
        │                    ▲
        │                    │
        └────────── DMAI.Presentation.asmdef  ← every MonoBehaviour, every ScriptableObject
                    references Domain + Session. Does NOT reference Engine or Data.
                             ▲
                             │
                    DMAI.Editor.asmdef        ← editor-only tooling, Map Builder importers
```

**A MonoBehaviour cannot call `GameEngine` because it cannot see the type.** Not "should not" —
*cannot*. The compiler rejects it. If a future contributor wants to sneak a rules check into a
`TokenView`, they must first edit an `.asmdef` to add a reference, which is a visible, reviewable,
one-line diff that anyone can spot. That is the difference between a convention and an
architecture.

**Why Presentation may reference `Domain` but not `Engine`:** Domain is data plus pure functions.
Presentation must render `CombatantState`, `TacticalMap`, `CharacterSheet` — projecting all of that
into parallel Unity view models would be thousands of lines of duplication that rots. And Domain
carries `SpellAreaGeometry` and `CombatSide`, which are *exactly* the helpers the preview layer
should call. Engine, by contrast, is where mutation lives. The line "data and pure geometry are
shared; mutation and adjudication are not" is both principled and practical.

**The one rule Presentation must self-police**, since the compiler cannot: **never mutate a Domain
object.** Reading `combatant.GridX` is correct. Writing `combatant.GridX = 4` is a bug that will
desynchronise the view from the engine's own record of the world and produce a save file that
disagrees with what the player saw. Domain types are mutable classes with public setters, so this
is not compiler-enforceable without wrapping every type. Two mitigations, both cheap:

1. An **EditMode test** that reflects over `DMAI.Presentation`'s IL and fails on any `stfld`/
   `callvirt set_*` targeting a `DungeonMasterAI.Domain` type. About sixty lines with Mono.Cecil,
   and it turns a convention into a red test.
2. A naming convention: Presentation holds Domain objects in fields named `_readOnly*` or behind a
   `ReadOnlySnapshot<T>` struct wrapper for the handful of hot types.

Do (1). It is the higher-value one and it runs in CI.

**The rule, stated so it can be quoted in a code review:**

> **No `if` in Unity code may branch on a D&D rule.** If a Presentation script contains "advantage",
> "proficiency", "AC", "saving throw", "difficult terrain", "opportunity attack", "spell slot" or
> "concentration" inside a *conditional*, it is a bug. Unity may **display** those words. It may not
> **compute** with them.

### 3.4 `EngineSession`: the one door

`DMAI.Session` contains one primary type. It is a plain C# class — **not** a MonoBehaviour, not a
ScriptableObject, not a singleton — so it is unit-testable without the Unity player loop.

```
EngineSession
  owns:  GameEngine, DiceService, DmToolRouter, RulesSearchService,
         AppDataStore, CampaignCloneService, AppState, CampaignState
  exposes:  Commands  (mutating, transactional, emit beats)
            Queries   (pure, non-mutating, drive previews)
            State     (read-only projection for binding)
```

#### 3.4.1 Commands are transactions that emit beats

Every mutation follows one shape, and the shape is dictated by §1.9:

```
1. Clone the campaign  (CampaignCloneService — already exists, already used by r60)
2. Run the engine call(s) against the clone
3. On exception → discard the clone, return a typed Rejection. Nothing was mutated. No beats.
4. On success   → diff clone against live, COMMIT the clone as the new CampaignState,
                  THEN build the beat list from the returned result records + the diff
5. Return BeatSequence to the caller
```

Step 4's ordering is the whole point. **Beats are produced from committed state.** No damage number
appears for damage that was rolled back; no crit sting plays for a blow that did not land.

A **beat** is an immutable value describing one presentable moment, carrying enough structure that
the presentation layer needs no engine access:

```
Beat kinds (indicative, not exhaustive):
  TokenMove(combatantId, path[], movementRemainingFeet)
  AttackDeclared(attackerId, targetId, attackName)
  DiceRoll(d20Face, secondFace?, modifier, total, target, rollMode)   // both advantage dice
  AttackOutcome(hit, critical, naturalOne, coverBonus)
  Damage(targetId, effectiveDamage, damageType, hpAfter, maxHp, tempHpLost, droppedToZero, dead)
  ConditionChanged(characterId, condition, added)
  ConcentrationCheck(characterId, dc, result, maintained)
  DeathSave(characterId, roll, successes, failures, stable, dead)
  SpellCast(casterId, spellId, school, castAtLevel, shape?, cells[])
  BattlefieldEffectAdded / Removed(effectId, shape, cells[])
  ExperienceAwarded(awards[], coalescedTotal, sourceNames[])          // coalesced, §8/§9
  LevelUpAvailable(characterId, pendingCount)
  TurnAdvanced(combatantId, round, roundChanged)
  EncounterEnded(encounterId, outcome)
  Rejection(message, code)
  NarrationHint(beat, severity, targetState)                          // §7.4
```

`BeatSequence` carries an ordered list plus a suggested pacing hint per beat. The **presentation
layer owns timing**; the Session owns content. Session says "damage 7, dropped to zero"; the
`BeatPlayer` decides that lands 180 ms after the impact frame with a 90 ms hit-stop.

This directly serves the convergence that `docs/game-feel-direction.md`, `docs/narrative-direction.md`,
`docs/audio-direction.md` and `docs/progression-direction.md` all independently arrived at: the
Resolution Beat card, the `narration_hint`, the `hit.critical` cue and the XP award are **the same
instant**. One beat pipeline fans out to card, prose, XP bar and audio.

`DiceRoll` deliberately carries **both** advantage dice. `D20TestResult(RollOne, RollTwo?,
ChosenRoll, …)` preserves them and, per the game-feel doc, *"the player never sees either."* Showing
the discarded die is free drama the engine is already computing.

#### 3.4.2 Queries are pure and never reimplement a rule

The presentation layer needs answers before it acts: where can I move, who can I see, what does this
fireball cover. Every one delegates to engine or Domain code:

| Query | Delegates to |
|---|---|
| `IsWalkable(cell)` | `TacticalMapGeometry.IsCellWalkable` |
| `CanStep(from, to)` | `TacticalMapGeometry.CanTraverseStep` |
| `HasLineOfSight(from, to)` | `TacticalMapGeometry.HasLineOfSight` |
| `AreaCells(shape, sizeFeet, origin, direction, width)` | `SpellAreaGeometry.EnumerateCells` |
| `LineOfEffect(from, to)` | `SpellAreaGeometry.TraceGridLine` |
| `MovementPathCells(from, to)` | mirrors `TraceGridPath` — **see §10.1, this one is a problem** |
| `ReachableCells(combatantId)` | **does not exist yet — §10.1** |
| `ThreatenedCells(combatantId)` | **does not exist yet — §10.2** |
| `SideOf(combatantId)` | `CombatSide.TryNormalize` |
| `LevelProgress(characterId)` | `Progression.LevelProgressFraction` |

Queries are non-mutating and safe to call every frame during a hover, though in practice they are
cached per turn and invalidated on commit.

#### 3.4.3 Rejections are first-class, never exceptions crossing the boundary

```
Rejection(string PlayerMessage, RejectionCode Code, string? DiagnosticDetail)
```

`EngineSession` catches `InvalidOperationException`, `ArgumentException` and `KeyNotFoundException`
from the engine, logs the full detail, and returns a `Rejection`. The presentation layer plays the
`ui.error` cue (§8.3), shakes the offending control, and shows the message. **It never shows a stack
trace and never leaves the game in a half-applied state**, because the transaction was against a
clone.

`RejectionCode` should be a real enum. Right now the engine encodes reasons only in message strings,
which means Unity would have to string-match to distinguish "out of movement" from "blocked by a
wall" — and string-matching an exception message is how you get a UI that breaks when someone fixes
a typo. §10.3 asks for structured codes.

#### 3.4.4 State is a read-only projection

`EngineSession.State` exposes the current `CampaignState`, active `EncounterState`, bound
`TacticalMap`, initiative order and pending roll/decision, plus a **monotonically increasing
`Revision`** bumped on every commit. Presentation binds to `Revision` for coarse invalidation and to
beats for fine-grained animation.

The WPF app used a global `MapRevision++` invalidation shotgun that forced a full battlefield
re-render every action — `docs/game-feel-direction.md` names this as the thing blocking per-row
damage flashes and HP tweening. **Unity must not repeat it.** `Revision` is a staleness check for
panels that are cheap to rebuild (quest log, inventory). Anything animated is driven by beats.

#### 3.4.5 The turn pump

The one loop everything routes through, expressed as pseudocode because getting it wrong is the
most likely source of "the game froze" bugs:

```
async Task Submit(PlayerIntent intent):
    var seq = session.Execute(intent)            // transaction + commit + beats
    await beatPlayer.Play(seq)                   // presentation owns timing

    while (true):
        if session.State.PendingPlayerRoll is {} roll:
            var value = await rollSurface.Prompt(roll)      // dice UI, or auto-roll setting
            seq = session.ResolveRoll(roll, value)
            await beatPlayer.Play(seq)
            continue
        if session.State.PendingPlayerDecision is {} decision:
            var option = await decisionSurface.Prompt(decision)
            seq = session.ResolveDecision(decision, option)
            await beatPlayer.Play(seq)
            continue
        break

    await autosave.Write()
```

On campaign load, run the same drain loop after calling
`EnsurePendingPlayerRollForActiveCombat` — a save made mid-attack must resume asking for the damage
roll, not silently swallow it.

Use `UnityEngine.Awaitable` (Unity 6's first-class async type with `MainThreadAsync()` /
`BackgroundThreadAsync()`) rather than adding UniTask. It avoids a dependency and its main-thread
semantics are exactly what this loop needs.

### 3.5 The `System.Text.Json` problem — the #1 technical risk of the port

On netstandard2.1, `System.Text.Json` is a NuGet package with a transitive chain: `System.Memory`,
`System.Buffers`, `System.Runtime.CompilerServices.Unsafe`, `System.Threading.Tasks.Extensions`,
`System.Numerics.Vectors`, `System.Text.Encodings.Web` and (for async APIs)
`Microsoft.Bcl.AsyncInterfaces`.

**Unity has no NuGet integration.** Every one of those DLLs must land in `Assets/Plugins` by hand,
and several — `System.Runtime.CompilerServices.Unsafe` above all — are notorious for colliding with
assemblies Unity already ships, producing duplicate-assembly errors or, worse, silent version
mismatches that fail at runtime with `MissingMethodException`.

Three options, in my order of preference:

1. **ILRepack/ILMerge `System.Text.Json` and its dependencies into `DungeonMasterAI.Engine.dll`
   with types internalised.** One DLL crosses into Unity. No version conflict is possible because
   no separate assembly identity exists. Costs a build step in
   `tools/build-engine-for-unity.ps1`; it is a solved problem with well-trodden tooling.
   **Recommended.**
2. **NuGetForUnity**, which resolves the graph into `Assets/Packages`. Faster to set up, but you are
   one Unity upgrade away from a conflict, and diagnosing that conflict is genuinely miserable.
   Reasonable as a day-one path if option 1 is slowing you down, with a note to migrate.
3. **Replace `System.Text.Json` in the engine.** Rejected. It would touch seven engine files and the
   entire save format for a Unity packaging problem. Never modify the engine to suit the view.

Verify at install time whether Unity 6.3 ships any part of `System.Text.Json` — my understanding is
that it does not (only `com.unity.nuget.newtonsoft-json` is officially packaged), but this is
exactly the sort of thing that changes between versions and it is cheap to check first.

**If this section turns out to be harder than expected, it is the thing that delays the port**, and
it is worth spiking on day one with a hello-world Unity project and the real `Engine.dll` before any
scene work begins. Do not discover this in month two.

### 3.6 The AI layer: what ports, what is rewritten

`DungeonMasterAI.AI` splits cleanly along a seam that is already there:

**Ports to netstandard2.1 unchanged** — `LlamaRuntimeManager` (process spawn, physical-core
counting via `kernel32`, runtime/model provisioning against the pinned lock files, hash
verification, health check, shutdown). This is `System.Diagnostics.Process` and `HttpClient`; it
works identically under Unity's Mono. Unity gets a `SidecarHost` MonoBehaviour in the Boot scene
that owns its lifetime and — critically — **kills the child process in `OnApplicationQuit` and in
`OnDisable` when exiting play mode in the editor.** Forgetting the editor case leaves orphaned
`llama-server.exe` processes eating 4 GB of RAM after twenty play-mode entries. It will happen once;
make sure it only happens once.

**Rewritten in Unity** — the transport half of `LocalDmClient`. Unity needs token-level streaming
integrated with the frame loop, and the current client is shaped for a WPF `async`/`await` world.

The prompt-construction and tool-call orchestration half — system prompt assembly, the 20-turn
history window, the single-leading-system-message constraint of the Qwen chat template, the
tool-call loop against `DmToolRouter` — is **product behaviour and should port**, ideally into
`DMAI.Session` so it sits behind the same fence as everything else that touches the engine.

Transport recommendation: **`HttpClient` on a background thread, draining into a
`ConcurrentQueue<string>` that a MonoBehaviour reads in `Update`.** Not `UnityWebRequest` —
streaming SSE through `DownloadHandlerScript` is fiddly and buys nothing here, and the endpoint is
`127.0.0.1` so none of UnityWebRequest's platform handling matters. Avoid `IAsyncEnumerable`, which
would drag in `Microsoft.Bcl.AsyncInterfaces` for no benefit; a callback and a queue is simpler and
one fewer DLL in §3.5's pile.

---

## 4. Scenes and prefabs

### 4.1 Scene list

Multi-scene additive, with one persistent shell.

| Scene | Load | Contents |
|---|---|---|
| **Boot** | Single, first | Nothing visible but a logo. Constructs `EngineSession`, loads `AppState` via `AppDataStore`, starts `SidecarHost` provisioning, then loads Shell additively and unloads itself. Holds the project's **only** `DontDestroyOnLoad` object. |
| **Shell** | Additive, persistent | UI Toolkit root document, `AudioMixer` + `AudioDirector`, `CinemachineBrain` camera rig, narration surface, settings overlay, save/load, the mute affordance, global input actions. |
| **CampaignLibrary** | Additive | Pick / create / import a campaign. Readiness report. Unloaded once a campaign is chosen. |
| **Table** | Additive | The default play surface: narration column, party rail (four PCs), location view, action strip, quest log, world map. |
| **Encounter** | Additive, **on top of Table** | Tactical map, tokens, combat camera, combat Volume profile, combat action bar, initiative track. Loaded on `COMBAT_START`, unloaded on `AFTERMATH`. |
| **MapBuilder** | Additive, replaces Table | Authoring surface for `TacticalMap`. Reuses the Encounter renderer prefab in an edit configuration. |

### 4.2 Why combat is a scene and not a panel

This is the one structurally interesting choice here, and it earns its keep.

`docs/encounter-design-direction.md` treats an encounter as a designed space with lighting
discipline, cover semantics and an opening shape. `docs/audio-direction.md` wants a 3-second
crossfade into combat music and a 4-second fade out. `docs/narrative-direction.md` gives combat its
own prose register, shorter than exploration. Combat is a **mode**, not a widget.

Making it a scene means the whole atmosphere swaps as one unit: the URP Volume profile (vignette,
grade, aberration), the 2D lighting set, the audio snapshot, the Cinemachine rig, the Input System
action map. Loading and unloading it is one operation with one obvious place to hook the transition,
and it makes it structurally impossible for a combat-only system to keep running during exploration.
Additive-on-top means the Table scene stays loaded underneath, so the narration column is continuous
across the transition — which matters, because the DM keeps talking.

Cost: two scenes are live at once, and cross-scene references need care. Use `SceneManager`'s
loaded-scene events and a small `SceneServices` locator scoped per scene rather than
`FindObjectOfType`.

### 4.3 The one `DontDestroyOnLoad`, and why

My default position is that `DontDestroyOnLoad` singletons are an anti-pattern, and they are. There
is exactly one legitimate use here: a `PersistentServices` GameObject in Boot holding the
`SidecarHost` (which owns an OS process that must outlive scene loads) and the reference to
`EngineSession`.

It holds **no rules logic, no game state and no behaviour** — it is a lifetime anchor. Everything
else receives what it needs by explicit injection.

**Injection mechanism.** Two workable options; I recommend the first for a solo project:

1. **`SessionReferenceSO`** — a `ScriptableObject` with a non-serialized runtime field holding the
   `EngineSession`. Boot writes it; everything else `[SerializeField]`s the SO and reads through it.
   Zero framework, inspector-visible wiring, no service locator.

   **The hazard, and it is real:** with **Enter Play Mode Options** enabled (domain reload disabled
   — and you *will* enable it, because it takes iteration from 8 seconds to instant), static and SO
   runtime state survives exiting play mode. A stale `EngineSession` referencing a disposed sidecar
   will be there on the next play. Mitigation: clear the field in `OnDisable`, and add a
   `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` reset. Write
   this down in the SO's own doc comment, because whoever hits it will lose an hour.

   Note this does **not** violate the "never store scene-instance references in a ScriptableObject"
   rule — `EngineSession` is a plain C# object, not a `UnityEngine.Object`, so there is no
   serialization leak. The domain-reload hazard is a separate and lesser problem.

2. **VContainer** (or Zenject). Proper constructor injection, scene-scoped lifetimes, testable. The
   right answer on a team. On a solo project it is a framework to learn for a problem that has about
   six instances. Adopt it if the wiring in option 1 starts to hurt; do not start there.

### 4.4 Prefabs, and the self-containment rule

Every prefab must instantiate in an empty scene without throwing. Concretely, for this project:

| Prefab | Responsibility |
|---|---|
| `TokenView` | One combatant on the grid. Base ring, portrait, HP arc, condition strip, initiative badge, selection/hover. Holds a `combatantId` string and nothing else stateful. |
| `MapRenderer` | Builds tilemaps, walls, doors, props and lights from a `TacticalMap`. Pure function of the map plus the asset pack. |
| `OverlaySurface` | The reachability / AoE / threat / fog mask quad. §6.4. |
| `DamageNumber` | Pooled world-space uGUI number with a curve-driven rise-and-fade. |
| `ResolutionBeatCard` | The outcome card from `docs/game-feel-direction.md`. §6.9. |
| `NarrationSurface` | Streaming DM prose with speaker differentiation. §7. |
| `PartyMemberRail` | One of four PC status cards. |
| `DiceRollSurface` | The player d20 prompt for `PendingRollRequest`. |
| `DecisionSurface` | Options prompt for `PendingPlayerDecision`, honouring `Emphasis`. |

A `TokenView` that assumes a `GameManager` exists in the scene is a bug. A `TokenView` that reads
`CombatantState.GridX` through an injected `EngineSession.State` is correct.

---

## 5. Data architecture: where ScriptableObjects help, and where they are forbidden

### 5.1 The test

> **If deleting a ScriptableObject would change a dice outcome, it is in the wrong place.**

Quotable, memorable, and it settles every case below without further argument.

### 5.2 Where ScriptableObjects genuinely help

All presentation, all designer-facing, none of it authoritative.

| SO type | Holds | Why it belongs in Unity |
|---|---|---|
| `MapAssetPackSO` | `AssetKey` → sprite variants, render mode, scale, opacity, flip/rotation | Pure art lookup. §5.3 |
| `AudioCueSO` | Clip variants, bus, gain, pitch jitter range, polyphony cap, cooldown, "never repeat last variant" flag | §8.2 |
| `AudioBankSO` | A named set of cues loaded together | Addressable grouping |
| `MusicLayerSetSO` | The four combat stems and their intensity thresholds (0.00 / 0.30 / 0.55 / 0.80) | §8.4 |
| `CombatFeelProfileSO` | Hit-stop ms, shake magnitude/decay per outcome tier, damage-number rise curve, camera punch, token move duration | Tunable live in play mode. **This is where game feel actually gets dialled in.** |
| `NarrationPacingSO` | Reveal chars/sec, TTFT thinking threshold, catch-up backlog, staged-reveal timings | §7 |
| `ConditionIconSO` | Condition name → icon, tint, tooltip copy | The engine owns which conditions apply; Unity owns what they look like |
| `SpellPresentationSO` | Spell id → cast VFX prefab, impact VFX by damage type, school cue, colour ramp | §8.5. **Never damage, range or slot level.** |
| `PortraitSetSO` | Character key → portrait sprite, token art, base ring variant, hurt/bloodied/downed states | Fixes the game-feel doc's "every creature is the same glyph" |
| `ThemePaletteSO` | `#070B0E`, `#C7A25C`, `AaaRed`/`AaaGreen`/`AaaGoldBright`, Georgia/Segoe UI | The r59 language, in one asset |
| `BeatPresentationSO` | `narration_hint` beat name → card treatment, cue, camera behaviour | The `fumble`/`graze`/`solid_hit`/`critical_hit` table from the narrative doc, as art direction |
| `LayoutArchetypeSO` | The five named archetypes' *authoring* templates for the Map Builder | §9.6 — a Map Builder convenience, not a rule |
| `GameEventSO` channels | UI-to-UI messaging (open sheet, close panel, focus token) | Decoupling *within* the presentation layer only |

The `CombatFeelProfileSO` deserves emphasis. `docs/game-feel-direction.md` found **zero**
`Storyboard`, `DoubleAnimation`, `VisualStateManager` or `CompositionTarget.Rendering` across 3,571
lines of XAML — *"the app has no time dimension. State changes are teleports."* Every timing that
fixes this should live in one inspector-editable asset that can be adjusted while combat is running.
That is the single highest-leverage designer tool in the project.

### 5.3 Asset packs: JSON stays authoritative, Unity imports it

`TacticalMapAssetPackManifest` already exists in Domain with a validator that enforces required
`PackId`/`Name`/`Author`/`License`, unique keys, opacity in 0..1, positive scale and weight, and
rejection of absolute paths and `..` traversal. Map JSON stores **stable semantic asset keys**, never
filenames, and `docs/map-asset-packs.md` makes replacing a pack a no-op for geometry rule 6.

**Do not replace that with a Unity-native format.** Instead: write a **`ScriptedImporter`** that
consumes a pack's `manifest.json` plus its image folder and produces a `MapAssetPackSO` at import
time, running `TacticalMapAssetPackValidator` and surfacing its errors and warnings in the Unity
console with clickable asset context.

That split is exactly right: **the engine owns validity, Unity owns lookup speed.** Packs stay
moddable and portable, the two search locations (`Assets/MapPacks` beside the build, then
`%LOCALAPPDATA%/DungeonMasterAI/MapPacks`, user-local winning) still work for user-installed packs
loaded at runtime, and the built-in `core.fantasy.crypt` pack becomes a fast SO with no per-frame
dictionary lookups.

**The deterministic variant selection algorithm must port exactly:**

```
hash = StableHash(map.Seed, x, y, assetKey)
pick = (int)((uint)hash % (uint)totalWeight)
walk valid variants subtracting Weight until pick < Weight
```

Same campaign, same floor variation after save/load, and on another machine with the same pack.
**`StableHash` must not be `string.GetHashCode()`** — .NET randomises string hashing per process and
the whole determinism guarantee evaporates. Port the engine's implementation verbatim, or if it is
currently using a framework hash, fix it in the engine (§10.7) rather than working around it in
Unity.

All fifteen shipped `core.fantasy.crypt` keys have empty `variants` arrays with
`allowProceduralFallback: true`, so **the Unity renderer needs procedural fallbacks for every key on
day one** — the pack ships no art. A missing image is silence, not an exception: draw the fallback.

### 5.4 Addressables

Use Addressables, not `Resources`, with groups by loading profile:

| Group | Contents | Load |
|---|---|---|
| `core-ui` | Fonts, USS, icons, theme | Preloaded, never released |
| `audio-core` | The ~18 Phase-1 SFX (§8.3) | Preloaded — the audio doc requires zero disk I/O on the audio path |
| `audio-music` | Combat stems, ambient beds | On demand, released on scene unload |
| `mappack-<packId>` | One group per asset pack | On demand when a map binds to that pack |
| `portraits` | Character and creature art | On demand per campaign |
| `vfx` | Spell and impact effects | On demand per encounter |

The `mappack-*` grouping matters because user-installed packs live outside the build in
`%LOCALAPPDATA%` and must load from disk at runtime — Addressables' remote/local catalog split
handles the built-in packs and a plain `Texture2D.LoadImage` path handles user ones. Two paths, one
`MapAssetPackSO` interface in front of both.

### 5.5 Where ScriptableObjects are forbidden

This list is not stylistic. Every entry is a place where a Unity asset would become a second source
of truth for a rule.

| Never in an SO | Lives in |
|---|---|
| AC, HP, hit dice, damage expressions, attack bonuses | `CharacterSheet`, `AttackProfile` in `CampaignState` |
| Spell definitions — level, school, range, damage, save ability, area size | `SpellDefinition`, `Assets/Rules/srd_spells.json` |
| Condition mechanics (what Prone *does*) | `CharacterMechanics`, `GameEngine` |
| XP thresholds, the CR ladder, the CR→XP table, derived-CR tables | `Domain/Progression.cs` |
| Level-up grants (proficiency bonus, HP formula) | `GameEngine.Progression.cs` |
| Monster and NPC stat blocks | `CampaignState.Characters` |
| Encounter definitions, combatant rosters, spawn sides | `EncounterState`, `TacticalMapSpawnPoint` |
| Tactical map geometry — rooms, walls, doors, terrain, cover, difficult terrain | `TacticalMap` JSON in the campaign |
| Cover values, LoS blocking, movement cost | `TacticalMapGeometry`, `MovementStepCostFeet` |
| The DM tool list and its JSON schemas | `DmToolRouter.Definitions` |
| Campaign save data of any kind | `AppDataStore`, `state.json` |
| The `CombatSide` vocabulary and its synonyms | `Domain/CombatSide.cs` |

**The tempting one is the tactical map.** Unity's Tilemap editor is a genuinely nice map authoring
tool, and it will be tempting to author maps as Unity scenes or Tilemap assets. **Do not.** The
engine adjudicates movement, cover and line of sight against `TacticalMap`; a Unity Tilemap is a
**render cache** built from that JSON and thrown away. If the two ever diverge, the player watches a
wall that does not block anything. The Map Builder (§9.6) edits `TacticalMap` JSON and re-renders;
it does not edit a Tilemap and export.

**The second tempting one is spell VFX.** `SpellPresentationSO` maps a spell id to a prefab. It must
not also carry the area size, because `SpellAreaGeometry.EnumerateCells` needs the engine's
`sizeFeet` and the two will drift the first time a spell is upcast.

---

## 6. The tactical map and combat presentation

The centrepiece. `docs/encounter-design-direction.md` opens with the problem this section closes:
*"The tactical map is not the battlefield — it is a picture that lives in a different room of the
app from the fight."* r62 wired the engine side. Unity is where it becomes something you feel.

### 6.1 Layer stack

Bottom to top, with sorting layers named explicitly so nothing fights:

```
0  Floor          Tilemap, lit    rooms' FloorAssetKey, TacticalMapTerrain
1  FloorDecal     Tilemap, lit    rubble, stains, blood from resolved beats
2  Overlay        Quad + shader   reachability / AoE / threat / hover  (§6.4)
3  Props          Sprites, lit    TacticalMapProp, sorted by Y then RotationDegrees
4  Walls          Sprites, lit    TacticalMapWall + ShadowCaster2D      (§6.3)
5  Doors          Prefabs, lit    TacticalMapDoor, animated by State
6  Tokens         Prefabs, lit    one TokenView per positioned combatant, sorted by Y
7  TokenHud       World uGUI      HP arcs, condition strips, initiative badges — UNLIT
8  Effects        VFX             spell impacts, battlefield effect zones
9  Fog            Quad + shader   TacticalMapVisibility mask            (§6.5)
10 Numbers        World uGUI      damage numbers, XP pips               — UNLIT
```

Layers 7 and 10 must sit outside the 2D light pass or a torch will dim the HP bars. This is the
"why is my map black" mistake in reverse and it is easy to miss.

### 6.2 Coordinates: one conversion, one place

The schema is Y-down with `(0,0)` upper-left. Unity is Y-up. Rooms, terrain, props, zones, spawn
points and combatants live in **cell space** `0..W-1` / `0..H-1`; **walls and doors live in
grid-line space** `0..W` / `0..H` inclusive, which is one larger in each dimension.

One static class in `DMAI.Presentation`, and nothing else may do this arithmetic:

```
GridSpace.CellToWorld(int x, int y)      → (x + 0.5f, -(y + 0.5f))   // cell centre
GridSpace.LineToWorld(int x, int y)      → (x,        -y)            // grid intersection
GridSpace.WorldToCell(Vector2 world)     → (floor(world.x), floor(-world.y))
GridSpace.FeetToUnits(int feet, map)     → feet / (float)map.FeetPerSquare
```

1 Unity unit = 1 square = `FeetPerSquare` feet, and `FeetPerSquare` is authoritative (1..30, default
5). Do not hardcode 5 — the schema permits other values and a 10-ft-per-square outdoor map would
silently halve every range.

Every off-by-one bug in this system will trace to something doing its own conversion. Make
`GridSpace` the only path and add an EditMode test that round-trips every cell of a 500×500 map.

### 6.3 Walls and doors are the fiddly part — say so up front

Walls are **edge segments in grid-line space**, not cells. Unity's Tilemap is cell-based and cannot
represent them directly. Two approaches:

1. **A second `Grid` offset by half a cell** so a tile lands on an edge. Clever, cheap to render, and
   an ongoing source of confusion about which grid a coordinate is in.
2. **One pooled `WallSegmentView` sprite per `TacticalMapWall`**, stretched along the segment,
   `RenderMode = segment` from the asset pack (which is defined as *"horizontal source artwork that
   can be rotated"* — the pack format already anticipates this), with a `ShadowCaster2D` sized to
   match where `BlocksLineOfSight`.

**Recommend 2.** Wall counts are in the tens for a typical map, so the GameObject overhead is
irrelevant, and it keeps grid-line space visibly separate from cell space. It also makes doors
trivial — a door is a `WallSegmentView` variant with a `State` machine (`open`/`closed`/`locked`/
`barred`) that toggles its `ShadowCaster2D` and swaps its sprite, and r62 already verified the engine
re-permits movement through an opened door.

Secret doors (`Secret: true`, `Discovered: false`) render as wall until discovered. `DmOnly` on any
element means Map Builder shows it and play mode does not — one boolean, checked in one place at
build time, never per-frame.

Flag this honestly: **this is the part of the renderer most likely to need a second attempt.** Budget
for it.

### 6.4 The overlay is a texture, not thousands of GameObjects

Reachability, AoE templates, threat ranges and hover highlights all answer "which cells are in this
set". The naive implementation instantiates a tinted quad per cell and it will be the first thing
that stutters.

Instead: **one full-map quad with a shader sampling a small `Texture2D` mask**, one texel per cell,
point-filtered, four channels:

- **R** — reachability (0 = unreachable, graded by movement cost so the last 5 ft reads differently)
- **G** — AoE / targeting template, from `SpellAreaGeometry.EnumerateCells`
- **B** — threat / opportunity-attack range
- **A** — hover and selection

A 500×500 RGBA32 mask is 1 MB and updates in a single `SetPixels32` + `Apply`, only when the
selection changes — never per frame. The shader gets smooth edges, animated pulses and per-channel
colours for free, and `docs/game-feel-direction.md`'s requirement to "pulse/ring the active
combatant" becomes a shader parameter rather than a `Storyboard`.

**Difficult terrain and cover need to read at a glance** — the encounter doc's density budget
(4–7 cover objects and 1–3 sight blockers per 100 squares) only matters if the player can see them.
Cover is a static property of the map, so bake it into a second static mask at map load rather than
into the dynamic overlay.

### 6.5 Fog of war

`TacticalMapVisibility` gives `RevealAll`, `RevealedRoomIds` and `RevealedCells`, gated by
`TacticalMap.FogOfWarEnabled`. Same texture-mask technique, its own quad on layer 9, with a soft
blur so the boundary is not a hard staircase.

Two notes. First, **fog is presentation of engine state, not a Unity-computed thing** — Unity renders
the revealed set; it does not decide what is revealed. Second, the engine has no
"reveal what this combatant can see" call, so fog currently only advances when something explicitly
writes to `Visibility`. If dynamic line-of-sight fog is wanted, that is an engine feature request
(§10.5), not a Unity one.

### 6.6 Movement: path planning in the view, adjudication in the engine

The hard problem from §1.5. The solution, and it is the one place where a Unity-side algorithm is
legitimate:

1. Player hovers a destination. `TokenController` runs **A\* over the engine's own predicates** —
   `TacticalMapGeometry.IsCellWalkable`, `CanTraverseStep`, plus occupancy — with **Chebyshev**
   distance and cost from the engine's step-cost query. **No rule is reimplemented; only a search is
   performed over engine-owned answers.**
2. Ghost the path, show cost and remaining movement, and mark cells where the path leaves an enemy's
   reach so opportunity attacks are visible *before* committing. This is the single biggest tactical
   UX improvement over the WPF app.
3. On commit, reduce the path to **its corner points** and issue one `MoveCombatant` per corner. Each
   leg is straight-line-legal, so the engine accepts it, and the engine still validates every square,
   still charges movement, still fires opportunity-attack windows.
4. If a leg parks a `PendingCombatMove` awaiting reactions, the turn pump (§3.4.5) drains it before
   the next leg. The token stops mid-path, the reaction resolves with its own beats, and movement
   resumes or is halted — which is correct, and looks far better than teleporting.

**Corner points, not every square** — decomposing a 12-square path into 12 calls means 12 separate
opportunity-attack evaluations. Reactions refresh on the reactor's turn, so a reactor should not fire
twice within one move, **but I have not verified that and it is exactly the kind of thing that is
subtly wrong.** Write a test: a rogue moving 30 ft in an L around an orc must provoke exactly once.
If it provokes twice, the fix is §10.1's path-accepting `MoveCombatant`, not a workaround in Unity.

Cost consistency is safe: the engine charges a flat 5 ft per step including diagonals, so a path with
more squares costs proportionally more, which is what a player expects when routing around a pillar.

### 6.7 Tokens

`TokenView` holds a `combatantId` and **no rules state**. Everything it renders is read from
`EngineSession.State` or delivered by a beat:

- **Base ring** coloured by `CombatSide.IsParty/IsOpposition/IsNeutral` — never by string comparison
- **Portrait** from `PortraitSetSO`, with hurt / bloodied / downed states driven by
  `CurrentHp / MaxHp` thresholds that are *presentation* thresholds, not rules
- **HP arc** — world-space uGUI radial, tweened on a `Damage` beat, never snapped
- **Condition strip** from `ConditionIconSO`, animating in and out on `ConditionChanged`
- **Initiative badge** from `InitiativeEntry`
- **Active pulse** — the game-feel doc's explicit requirement, driven by the overlay shader
- **Hidden** (`CombatantState.IsHidden`) — dimmed and outlined for party members; **not drawn at all
  for opposition**, which is a place Unity must be careful not to leak DM information
- **Facing** — the schema has no facing field (`SpawnPoint.Facing` is on the encounter doc's
  wishlist), so facing is presentation-only: point the token at its last target and never let
  anything read it

Fix the game-feel doc's complaint that every creature renders as the same `AaaVectorIcon
Kind="Characters"` glyph in three places. Distinct tokens are a content problem more than a code
problem, but the code must not stand in the way.

### 6.8 Beat playback and timing

`BeatPlayer` consumes a `BeatSequence` and plays it against a `CombatFeelProfileSO`. Rules that
matter:

**Never use `Time.timeScale = 0` for hit-stop.** It freezes UI animation, tween-driven text and
anything else on unscaled time is a special case you will forget. Use a **`PresentationClock`** that
the beat player and token animators read, leaving UI and audio on real time. Hit-stop then becomes
"the clock pauses for 90 ms" and the narration column keeps streaming underneath, which is exactly
what you want.

**Cinemachine Impulse for shake**, not manual transform manipulation. It composes with the confiner
and the framing target group instead of fighting them. Magnitude by outcome tier from the feel
profile: a graze is nothing, a critical is a real jolt, a party member dropping to zero is the
biggest non-death event in the game.

**Camera** — one `CinemachineCamera` with a `Confiner2D` bounded by the map, and a
`CinemachineTargetGroup` containing the acting combatant and its target so the frame naturally holds
both. A second, higher-priority camera for dramatic beats (crit, drop-to-zero, death, spell
resolution) that blends in for ~1.2 s and releases.

**Turn presentation budget.** An NPC turn should play in **under 2.5 seconds** of animation with a
hold-to-skip and a persistent speed setting (1× / 1.5× / instant). The encounter doc's success
metric — *"in a played encounter, ≥2 combatants move on ≥2 of the first 3 rounds"* — means movement
animation happens constantly, so its duration is the number most worth tuning. Start at 0.18 s per
square with easing and expect to shorten it.

**Multi-target sequencing.** `SpellTargetResolution.Sequence` exists; the audio doc asks for
projectile impacts staggered **~90 ms** apart. That number should drive the visual stagger too, so
audio and visuals land together.

### 6.9 The Resolution Beat

`docs/game-feel-direction.md` names this the top priority, and it is where the beat pipeline pays
off. One card, animated, rendering the *structure* of an outcome:

- the **d20 face large** — and, on advantage or disadvantage, **both dice** with the discarded one
  visibly discarded
- modifier and total beside it
- **hit/miss as a visual state, not a word**
- **damage as a number that lands** — mass, a settle, a shake proportional to severity
- **"CRITICAL" as a treatment, not a parenthetical**
- the **XP award fused into the same card**, per the progression doc's §7.1: *"The kill and the XP
  are one beat."* Multiple kills from one area spell **coalesce into one card** with a combined total
  and a source list, not three cards.

Beside it, a scrolling **beat feed** — a compact history so a player who looked away can catch up.

Severity tiering comes from the narrative doc's classification and should drive the card's
treatment as well as the prose: `glancing` (<10% of max HP) / `real` (10–30%) / `heavy` (30–60%) /
`devastating` (>60%) / `dropped` / `killed`. **Severity is a fraction of the target's max HP, never
raw damage** — 12 damage is trivial to an ogre and lethal to a rat, and a card that treats them
identically is lying about the drama.

`nat 1` currently has no representation: `AttackResult` has `Critical` but no fumble flag, so a
natural 1 is indistinguishable from an ordinary miss at the API boundary. §10.6.

### 6.10 What the Encounter scene does when there is no map

`ResolveEncounterMap` returns `null` for an unbound encounter, and the engine explicitly supports
that: *"A null map is a supported state: every consumer falls back to the pre-map behaviour so an
unbound encounter still plays."*

Unity must honour that. With no bound map, render a neutral procedural floor sized to the
combatants' extents, no walls, no lights beyond a flat ambient, and the same tokens and overlays.
It should look deliberately abstract — a training-room grid — not broken. This path will be hit
constantly during development and by every campaign authored before r53.

---

## 7. LLM narration: designing for a model that takes seconds

The pacing problem is real, specific, and the thing most likely to make the finished game feel bad:
a 4B model on CPU takes seconds, and dead air reads as a hang.

### 7.1 The governing decision: mechanics never wait for the model

**The single most important pacing decision in the project.** The engine is instantaneous. If the
player attacks, the swing, the die, the damage, the HP drain, the XP and the audio all play
*immediately* from beats. Narration arrives afterwards as **additive colour**, never as a gate.

This is not a compromise. It is better design than the alternative even if generation were instant,
because it separates "what happened" (crisp, immediate, mechanical) from "what it felt like" (slower,
prose, atmospheric), and those genuinely want different rhythms.

Concretely: the Resolution Beat card appears in ~200 ms. The DM's line about it lands two or three
seconds later, in the narration column, and does not repeat a number the card already showed.

### 7.2 Combat asks the model far less often

`docs/narrative-direction.md` recommends **deferred narration — one model call per round, on End
Turn** — over per-roll model calls, and that recommendation is even stronger in Unity than in WPF.
Per-action generation would put a multi-second stall inside every single attack.

So: during a combat round, Unity plays the full mechanical beat sequence with **zero model
involvement**, and requests one narration pass covering the round when the round closes. That pass
can generate *while the player is taking their next turn*, which hides most of its latency
completely.

The 23 engine strings currently masquerading as DM speech get **templated dressing** — 3–5
hand-authored variants per `narration_hint` beat, selected without a model call, zero latency. Ship
that first (the narrative doc's option 1), then layer deferred model narration on top.

### 7.3 Streaming, with a reveal queue

`llama-server` supports SSE on `/v1/chat/completions` with `"stream": true`. Consume it — but do not
render tokens as they arrive.

Local generation is **bursty**: a pause, then eight tokens at once, then a pause. Rendering arrivals
directly produces text that stutters and reads as broken. Instead, tokens land in a **reveal queue**
drained at a smoothed rate:

| Parameter | Starting value | Note |
|---|---|---|
| Reveal rate | **45 chars/sec** | Comfortable reading pace; in `NarrationPacingSO` |
| Catch-up rate | **110 chars/sec** | Used when backlog exceeds the threshold |
| Backlog threshold | **180 chars** | Beyond this, accelerate rather than fall further behind |
| Click-to-complete | always available | Non-negotiable — reveals the rest instantly |
| Punctuation dwell | **+60 ms** on `.`/`!`/`?`, **+30 ms** on `,` | Cheap, and it makes prose breathe |

If generation finishes before the reveal queue drains, that is fine and normal — the queue keeps
draining. If the reveal catches up to a still-generating model, hold on the last character rather
than showing a cursor that stalls.

### 7.4 What the front end does while the model is thinking

Not a spinner. A spinner says "the software is busy"; the game should say "the DM is considering."

| Elapsed | Presentation |
|---|---|
| 0–800 ms | **Nothing.** Below this, any indicator flickers in and out and reads as jitter. |
| 800 ms+ | The **thinking state**: candle flame steadies and dims, ambient bed swells ~2 dB, a slow parchment-grain shimmer on the narration column, the input field's caret slows. Diegetic, not chrome. |
| 4 s+ | Deepen it. Do not add a second indicator — intensify the first. |
| 15 s+ | Show a quiet, cancellable "still thinking" affordance. Never a percentage; there isn't one. |
| Error / timeout | A one-line in-fiction fallback and a retry. **Never a stack trace, never a modal.** |

Cancellation must work at every stage and must leave state untouched — which it does for free,
because the LLM turn runs against a clone (§1.9) and cancellation simply discards it.

### 7.5 Hiding latency with overlap

Three places where generation is free because the player is busy:

1. **Round-close narration** generates while the player takes their next turn (§7.2).
2. **`AFTERMATH`** generates during the XP award and level-up presentation, which the progression
   doc wants to be *"the loudest thing the application does"* anyway.
3. **`SESSION_OPEN` / the "Previously…" recap** generates during campaign load and scene transition,
   behind a screen that is doing something visible regardless.

Keep the sidecar hot: issue a tiny warm-up completion after load so the model is resident. Keep the
**system prompt byte-identical across turns** so `llama-server`'s prefix cache hits — a changed
system prompt silently discards the KV cache and adds seconds to every turn. The narrative doc's
NPC-voice supplements must therefore be filtered to NPCs at `PartyLocationId` **and** ordered
deterministically, or the cache misses on every location change and probably more often than that.

### 7.6 Two channels, and the boundary between them

Prose and tool calls arrive on the same stream and must be handled differently:

- **Prose tokens** — stream to the reveal queue immediately.
- **Tool-call JSON** — buffer to completion, then apply **atomically** through `DmToolRouter` inside
  the transaction. A half-parsed tool call must never touch state; r60 established this and it is
  not negotiable.

If a turn emits both, the prose reveals while the tool call buffers, so the player is reading during
what would otherwise be dead time. That is free and worth engineering for.

### 7.7 Speaker differentiation, and one open parameter

`docs/narrative-direction.md`'s smallest, highest-value fix: the WPF `ListBox` template binds only
`Content` and discards `Speaker`/`IsUser`/`IsAssistant`, so player and DM render identically —
*"the reader cannot see who is talking."*

Unity's `NarrationSurface` must differentiate structurally: **DM in Georgia, larger, full width, on
the parchment ground; player input in Segoe UI, smaller, indented, muted.** Engine-dressed mechanical
text is a **third** register — compact, monospace-ish, in the beat feed, **never in the DM column**.
Three channels, three looks, no ambiguity about who is talking.

And the layer-wide rule from the same doc, which the beat architecture makes easy to honour:

> **Never print a number the player already saw.** The Resolution Beat card shows mechanics. The DM
> narrates. *"When the DM repeats '24 vs AC 15' it stops being a Dungeon Master and becomes a
> receipt."*

**Open parameter for the owner (§12).** `Models.cs:27` ships `Temperature = 0.4` with the comment
that 0.75 *"measurably degrades tool-call argument fidelity on a 4B model, and every state change in
this application goes through a tool call."* `narrative-direction.md` assumes 0.75 for prose
quality. Both are right about their own concern. If §7.2's deferred-narration split lands, the
tension resolves cleanly: **tool-calling turns at 0.4, pure-narration turns at 0.75**, since the
latter emit no tool calls. That is my recommendation, but it needs measuring rather than asserting.

---

## 8. Audio

`docs/audio-direction.md` is the most implementation-ready of the direction documents. Most of it
transfers to Unity unchanged; the parts that do not are the stack.

### 8.1 Stack: Unity's built-in `AudioMixer`, with FMOD as a flagged decision

The audio doc chose NAudio + NVorbis because WPF has no audio stack worth using. Unity does, and it
covers the requirements: an `AudioMixer` with named groups, exposed volume parameters, snapshots
with transition times, and `AudioSource` pooling. **The NAudio work does not port and does not need
to.**

The doc rejected FMOD/Wwise for native DLLs, licensing and authoring overhead. Two of those three
change here: Unity has first-class FMOD integration, and with no commercial distribution the
licensing objection evaporates. What FMOD would genuinely buy is §8.4's four-layer adaptive combat
music — parameter-driven vertical layering is what FMOD exists for, and hand-rolling it against
`AudioMixer` snapshots is fiddlier than it looks.

**Recommendation: start with Unity's `AudioMixer`.** Phases 1 and 2 (the ~18 SFX and the spell
taxonomy) need nothing more, and they are the entire near-term value. Revisit FMOD only if the
adaptive music in Phase 3 proves painful. **Flag as an owner decision** — it is a genuine coin-flip
and it is much cheaper to make before the cue layer is written than after.

### 8.2 Bus structure and cue assets

The doc's four buses map directly onto `AudioMixerGroup`s:

```
Master
 ├── Music
 ├── Ambience
 ├── SFX
 └── UI
```

Settings port unchanged: `AudioEnabled = true`, `MasterVolume = 0.5`, `SfxVolume = 1.0`,
`AmbienceVolume = 0.7`, **`MusicAndAmbienceEnabled = false`** — reactive audio on by default at 50%
master, music and ambience opt-in. Duck to silence when the window loses focus
(`Application.focusChanged`). Keep the **visible mute affordance in the shell chrome**, one click,
always reachable — that requirement is about trust and it should not get lost in a settings screen.

`AudioCueSO` carries clip variants, bus, gain, pitch jitter, polyphony cap and cooldown, and
implements the doc's mandatory variation rule: **±2 semitones pitch, ±1.5 dB gain, never the same
variant twice in a row.** That last clause needs a remembered-last-index per cue and is the kind of
thing that gets skipped and then makes dice rolls sound mechanical. Put it in the SO's play method
so it cannot be skipped.

Performance targets port unchanged: **preload and fully decode every SFX at startup** (Addressables
`audio-core` group, `Decompress On Load`, <1 MB total), **cap concurrent voices at 16** with
oldest-non-critical stealing, and **exempt death and death-save cues from voice stealing.** Unity's
`AudioSource` pool handles this; the exemption does not come for free and must be written.

### 8.3 Cues, and where they fire

All ~18 Phase-1 cues fire from **beats**, never from `Update` polling and never from inside an
engine call. Because beats are emitted post-commit (§1.9/§3.4.1), the doc's rollback hazard — *"a
crit sting for a blow that was rolled back"* — is structurally impossible rather than carefully
avoided. This is the clearest payoff of the beat architecture and it is worth noticing that the
audio doc arrived at the requirement independently.

Two details that are easy to get wrong and expensive when wrong:

- **`DroppedToZero` and `Dead` must never be conflated.** The doc calls conflating them *"a genuine
  misinformation bug"* and it is right — a player who hears the death cue for an unconscious ally
  will make a different, wrong decision. `DamageResult` carries both flags separately; the beat must
  too.
- **`deathsave.failure` pitches lower on each successive failure**, indexed by
  `DeathSaveResult.Failures`. Three failures should audibly walk downward. This is the cheapest
  dramatic effect in the entire document.

`miss.whiff` must exist — *"a silent miss reads as a bug"* — and UI cues are exactly four
(`ui.click`, `ui.panel`, `ui.error`, optional `ui.narration`), all ≥12 dB below combat, with
**explicitly no sound on hover, focus, scroll, text entry, list selection or save.**

### 8.4 Adaptive combat music

The doc's four-stem vertical layering over one harmonic bed, with thresholds at 0.00 / 0.30 / 0.55 /
0.80, and the intensity formula:

```
intensity = clamp01(
      0.35                                          // combat is active
    + 0.15 * min(Round / 6.0, 1.0)                  // attrition
    + 0.30 * (1 - partyCurrentHp / partyMaxHp)      // the party is losing
    + 0.25 * (any friendly DeathSaveRequiredThisTurn ? 1 : 0)
    + 0.10 * (round 1 && any friendly Surprised ? 1 : 0))
```

Recomputed **once per committed turn** — which in this architecture means on a `TurnAdvanced` beat,
naturally — and smoothed toward target at **~0.1/second**. **The `crisis` layer is reserved for death
saves; nothing else may reach 0.80.** Intensity falls when the party is winning.

Transitions are **slow crossfades of 2–4 seconds aligned to nothing** — 3 s into combat on initiative
completing, 4 s out on encounter end, never a hard cut. Ambience ducks ~6 dB under combat music
rather than stopping. Location beds crossfade over 2.5 s with both live during the overlap.

`MusicLayerSetSO` holds the four stems and their thresholds; `AudioDirector` in the Shell scene owns
the intensity state and lives across the Table/Encounter scene boundary, which is another reason the
Shell scene is persistent.

### 8.5 Spell audio

The doc's taxonomy — cast gesture by `SpellDefinition.School` (8 samples), impact by `DamageType`,
scale by `CastAtLevel` as a gain and low-end lift rather than a separate sample — covers all 316 SRD
spells in about ten assets. `SpellPresentationSO` (§5.2) holds the mapping for both audio and VFX so
they stay in step, and multi-projectile impacts stagger **~90 ms** apart by
`SpellTargetResolution.Sequence`, matching the visual stagger in §6.8.

### 8.6 Blocked, and how Unity should behave meanwhile

Ambient beds remain blocked on `WorldLocation.Type` being a free-form string (shipped values
`town, shop, secret, quest, landmark, inn` are administrative, not acoustic, and the LLM compiler can
emit anything). The doc wants a controlled vocabulary of ~6–8 acoustic categories.

Unity's rule until then is the doc's own: **default to `silent`, never guess.** A wrong ambient bed
is worse than none, and this is a Domain change (§10.8), not something to paper over with a Unity
lookup table — which is exactly the kind of well-intentioned patch that creates a second half-engine.

---

## 9. What the WPF app does today: rebuild, drop, or reshape

Inventory of the existing surface. Ten shell tabs exist today — Home, Live Play, Combat, Characters,
World, Maps, Quests, Rules, Import, Settings — plus a second unused `MainWindow.xaml` shell.

### 9.1 Campaign library and loading — **rebuild, reshaped**

`Views/CampaignLibraryView.xaml`, backed by `AppDataStore` (schema v5), `CampaignImportService` and
`CampaignReadinessValidator`.

Unity rebuilds this as the `CampaignLibrary` scene. Keep everything real: campaign list, manifest
import (`ImportManifestJson`), the readiness report with severities, and the sample campaign. Reshape
the presentation from a list view into something that reads as choosing an adventure rather than
opening a file — cover art, one-line hook, party thumbnails, last-played.

Save location, format and schema are untouched: `state.json` + `state.previous.json` + `Recovery/`
under `LocalApplicationData`, written by the same `AppDataStore`. **Do not reimplement saving in
Unity.** A Unity-native save would fork the format and lose the recovery path and the v1→v5 migration
chain, which is real accumulated correctness.

### 9.2 Home / dashboard — **drop**

Six buttons, **zero wired**. Campaign Level `5`, Sessions `12`, "Tomorrow / May 24, 2025 · 7:00 PM",
"4 / 4 Members" and four literal activity rows are all hardcoded mockup data.

This is a mockup, not a feature. Do not port it. The genuinely useful parts — session recap, party
status, "what was I doing" — belong to `SESSION_OPEN` and the party rail on the Table surface, where
they have real data behind them. Rebuilding a dashboard first is how you spend three weeks and have
nothing playable.

### 9.3 Live Play and Combat — **rebuild as one surface**

`Views/LivePlayView.xaml` + `Views/CombatView.xaml`, driven by `MainViewModel` and its fourteen
partials.

The game-feel doc already proposes merging them, notes the mode switch exists as
`PlaySceneModeTitle`, and reports the damning asymmetry: **Live Play has 21 buttons of which 10 have
no command** — including Cast Spell, Dodge, Ready and Ask AI, styled identically to the Attack and
End Turn buttons that *are* wired — while **Combat has 39 buttons of which 35 are wired.**

Unity's answer is the Table scene plus the additively-loaded Encounter scene (§4.2): one continuous
surface where combat *arrives* rather than being a tab you switch to. **The 35 wired Combat commands
are the real mechanical coverage and every one must survive**; they are the actual specification of
what a player can do. The 10 dead Live Play buttons get wired or deleted — a button that looks like
the working button beside it and does nothing is worse than an absent button.

The `MainViewModel` partials map onto the Session layer, not onto MonoBehaviours: `PlayerRolls`,
`AreaSpellRolls`, `CombatSkillRolls`, `OpportunityAttackRolls`, `ReadiedAttackRolls`,
`StealthAidRolls`, `UnarmedPlayerRolls`, `PlayerDecisions` and `GameTableInputRouting` are all
"drive the pending-roll pump for a particular resolution kind", which is `DMAI.Session`'s job. That
they exist as nine separate partials is a strong hint that the pump generalises and should be
written once against `PendingRollRequest.ResolutionKey` rather than nine times.

### 9.4 Character sheets — **rebuild, reshaped, and this is where four PCs bites**

`Views/CharactersView.xaml`. Displays and edits `CharacterSheet`.

**Delete the debug affordances from the player-facing sheet**: `＋ Add Test Character`,
`▼ Take 1 Damage`, `▲ Heal 1`, `✚ Grant 5 Temp HP`. They belong behind a developer toggle, not in
the game.

**Add**, per the progression doc's §7.4 bindable surface: `ExperiencePoints`, `PendingLevelUps`,
`Level`, and an XP bar bound to `Progression.LevelProgressFraction` that visibly advances — *"the bar
advancing is the entire point."* Plus the level-up application screen, which must be **honest that
spell slots and class features are not granted**, because the domain has no class field.

**The four-PC reshape.** The direction docs predate the four-real-PCs decision and assume one
protagonist. Concretely, that changes:

- A **party rail** of four persistent status cards on the Table surface, not one character panel
- The Resolution Beat must say **whose** outcome it is — with one protagonist that was implicit
- XP awards land on four characters at once (the engine already divides by eligible PC count with a
  deterministic remainder rule), so the award card must show four bars advancing, or a party total
  with per-character detail on demand
- `PendingLevelUps` can be non-zero on several PCs simultaneously; the "rest and take it" prompt is
  a party-level prompt
- Second-person narration ("You push the door") is a genuine open question with four PCs — see §12

### 9.5 The tactical map viewer — **rebuild entirely**

`Controls/CombatGridControl.cs` and `Controls/TacticalMapControl.cs`, single static `OnRender`
passes with no animation.

This is §6 and it is the largest single piece of new work. Nothing of the WPF rendering survives;
what survives is the *contract* it validated — `R62MapCombatTests` proves the engine binds maps to
encounters correctly, and that test's engine half should outlive its rendering half (§1.11).

### 9.6 The Map Builder — **rebuild, and it gets better**

`Views/MapsView.xaml` plus `TacticalMapAssetCatalog`, `TacticalMapAssetPalette` and
`CoreFantasyMapAssetPackProvisioner`, with `MainViewModel.MapEditing` and `MainViewModel.MapGeneration`.

Rebuild as the `MapBuilder` scene, editing `TacticalMap` JSON directly and re-rendering through the
same `MapRenderer` prefab the Encounter scene uses — so what you author is exactly what you play,
which the WPF split did not guarantee.

Unity makes this meaningfully better in ways worth naming:

- **Live lighting.** Place a `TacticalMapLight` and see the room lit, which turns the encounter doc's
  light discipline (2+ lights on the critical path, unlit ambush positions, `#E39A52` braziers, the
  exit visible) from a checklist into something you can see.
- **In-editor validation.** Run `TacticalMapGeometry.Validate` and the encounter doc's six acceptance
  checks continuously, surfacing issues as scene-view gizmos on the offending object rather than as a
  list of text. The **open-field check** is called out as the highest-value one.
- **Density readout.** The doc's budget (per 100 squares: 2–4 regions, 4–7 cover objects, 1–3 sight
  blockers, 2–4 lights, 2–5 opposition spawns in ≥2 clusters) as a live panel while authoring.
- **`LayoutArchetypeSO` stamps** for the five named archetypes — pillared hall, chokepoint and flank,
  broken ground, flooded chamber, reliquary — each producing schema-valid JSON with the correct cover
  semantics. Note the spelling trap: the readiness validator accepts only **`three_quarters`** with an
  underscore, though `NormalizeCover` is more forgiving. The stamps must emit the strict spelling.

AI map generation (`docs/ai-map-generation.md`) rebuilds as a panel in this scene: the eight-field
form, the candidate preview with validation warnings, and the five actions (Regenerate, Change Theme,
Regenerate Decorations, Edit, Add to Campaign). Keep the one-repair-attempt rule and, critically,
**keep visible failure** — a generator that silently accepts a broken map is worse than one that
refuses.

### 9.7 Campaign import — **reshape, and move the PDF path out**

`CampaignImportService`. `ImportManifestJson` and `CompileText` are pure and port. `ExtractSourceAsync`
and `ImportAsync` depend on PdfPig.

Recommendation: **keep manifest and text import in the game; move PDF import to an out-of-game
`net10.0` CLI tool** that emits a campaign manifest. It keeps a PDF parser out of the player, avoids
one more DLL in §3.5's pile, and PDF import is a once-per-campaign authoring action that does not
need to happen inside the running game.

### 9.8 World, Quests, Rules — **rebuild, trimmed**

- **World** (`Views/WorldView.xaml`) — locations, connections, factions, timeline. Rebuild the real
  data; **delete the hardcoded mockups** (quest progress `40%`, faction standing `62`, "4 Active",
  three literal rumours) and the map toolbar that is `TextBlock`s pretending to be buttons.
- **Quests** (`Views/QuestsView.xaml`) — rebuild. Note the progression doc found `Quest.RewardGp` is
  imported and shown to the LLM but **never granted to anyone**; r62's F2 faucet pays it at the same
  moment as quest XP. Verify that landed and show it.
- **Rules** (`Views/RulesView.xaml`) — SRD search over `RulesSearchService` and `srd_chunks.jsonl`.
  Rebuild, but **reshape it from a tab into a contextual lookup** — a hover on a condition icon or a
  spell name that shows the SRD text in place. A rules tab is a thing you leave the game to use; an
  inline lookup is part of the game.

### 9.9 Settings — **rebuild, expanded**

`Views/SettingsView.xaml`, over `AppSettings` (`Models.cs:11`): `LlamaServerUrl`, `ModelName`,
`ModelPath`, `HuggingFaceModel`, `ContextSize`, `GpuLayers`, `Temperature`, `MaxTokens`,
`AutoProvisionRuntime`, `PlayerSafeMode`.

All port. Add:

- **Audio** — the five fields from §8.2
- **Presentation speed** — turn playback 1× / 1.5× / instant, and a reduced-motion toggle that
  disables shake and hit-stop
- **Narration reveal rate** and a click-to-complete-by-default toggle
- **Auto-roll** — whether player d20 requests prompt or roll themselves, which is a genuine taste
  question and should be per-player rather than decided here
- **Graphics** — resolution, fullscreen, vsync; Unity gives none of this for free and a game without
  it feels unfinished

Keep `GpuLayers = 0` and the comment explaining why (the bundled runtime is a CPU build with no
offload target). Keep `AutoProvisionRuntime` and surface the 2.74 GB first-run model download as a
proper progress screen — it is the worst first-run experience in the product and Unity is a chance
to make it a *"preparing your world"* moment instead of a stalled window.

### 9.10 The old shell — **drop**

`MainWindow.xaml`, `AaaShellWindow.xaml`, `Themes/AaaTheme.xaml`, `UiConverters.cs`, `UiModels.cs`,
`RelayCommand.cs`, and the `Aaa*` decorative controls (`AaaArcaneCompass`, `AaaBrandMark`,
`AaaCampaignCrest`, `AaaCombatAtmosphere`, `AaaDungeonFloor`, `AaaVectorIcon`).

None of the code survives. **The visual language does** — §2.6. `AaaCombatAtmosphere` and
`AaaDungeonFloor` are worth reading before deleting, since they encode the intended mood in
procedural drawing code, and that intent should carry into the Unity materials.

The game-feel doc asks whether to delete `MainWindow.xaml`; the answer once WPF goes is that the
question stops existing.

### 9.11 Summary

| Surface | Verdict |
|---|---|
| Campaign library + import (manifest/text) | Rebuild, reshaped |
| PDF campaign import | Move out of the game to a CLI tool |
| Save/load, schema v5, recovery | **Reuse unchanged** — do not reimplement |
| Home dashboard | **Drop** (mockup, 0/6 wired) |
| Live Play + Combat | Rebuild merged; all 35 wired commands must survive |
| Character sheets | Rebuild; drop debug buttons; add XP/level-up; reshape for four PCs |
| Tactical map rendering | Rebuild entirely — §6 |
| Map Builder + AI generation | Rebuild, substantially better |
| World / Quests | Rebuild; delete hardcoded mockups |
| Rules search | Rebuild as contextual lookup, not a tab |
| Settings | Rebuild, expanded |
| Shell chrome and `Aaa*` controls | Drop the code, keep the language |
| WPF-coupled tests | Split engine half out first, then drop |

---

## 10. Engine API requests found while designing this

These are gaps in the engine that Unity would otherwise be tempted to fill locally — which is exactly
how the second half-engine gets built. Each belongs in `Engine`, not in Unity. Ordered by how much
damage the workaround would do.

**10.1 A non-mutating movement query.** There is no public API for "which cells can this combatant
reach". `TacticalMapGeometry.MovementCostFeet(map, x, y)` is public but ignores encounter terrain,
battlefield effects and the Prone penalty; the method that gets it right,
`GameEngine.MovementStepCostFeet(encounter, map, x, y, mover)`, is **private**. Unity cannot draw an
honest reachability overlay without reimplementing it, and a reachability overlay that disagrees with
the engine is a UI that lies about the most common action in the game.

Requested:
```
public int MovementStepCostFeet(CampaignState c, string encounterId, string combatantId, int x, int y)
public IReadOnlyDictionary<TacticalMapCell,int> ReachableCells(CampaignState c, string encounterId, string combatantId)
public MovementPreview PreviewMove(CampaignState c, string encounterId, string combatantId, int x, int y)
```
`PreviewMove` returning the traced path, cost and the opportunity-attack windows it would provoke
**without mutating anything** would let Unity show consequences before commitment, which is the single
biggest tactical UX win available.

**Related:** `MoveCombatant` accepting an explicit waypoint path would remove §6.6's
decomposition entirely and settle the "does an L-shaped move provoke twice" question at the source.

**10.2 Threatened cells.** `GetPendingOpportunityAttacks` reports windows *after* a move. There is no
"which cells does this creature threaten" query, so Unity cannot shade enemy reach. Same reasoning
as 10.1.

**10.3 Structured rejection codes.** Illegal actions throw `InvalidOperationException` with prose. To
distinguish "out of movement" from "blocked by a wall" from "not your turn", Unity must string-match
an exception message — which breaks the day someone fixes a typo. Requested: an exception type
carrying a `RejectionCode` enum, with the prose kept as the default player message.

**10.4 Inert schema fields.** `TacticalMapTerrain.ElevationFeet` is validated as a multiple of 5 and
never read. `Cover` on `TacticalMapTerrain` and `TacticalMapProp` is stored but no geometry query
consults it — the engine reads `TerrainFeature.Cover`, a *different type*.
`TacticalMapWall.HeightFeet` is validated and never queried. **Unity must decide deliberately whether
to render these ahead of the engine.** My recommendation: **do not.** Rendering cover the engine does
not apply teaches the player a rule that is not real, and that is a worse bug than a missing feature.
Either wire them in the engine or leave them invisible.

**10.5 Line-of-sight fog.** Fog only advances when something writes to `TacticalMapVisibility`. If
dynamic per-combatant visibility is wanted, it is an engine feature (`HasLineOfSight` already exists
and is public), not a Unity one.

**10.6 Natural 1.** `AttackResult` has `Critical` but no fumble flag, so a nat 1 is indistinguishable
from an ordinary miss at the API boundary. The narrative doc wants a `fumble` beat and the audio doc
wants a `d20.natural1` cue that is *"not a comedy sound."* Requested: `NaturalCritical` and
`NaturalMiss` on `AttackResult`.

**10.7 Stable hashing for asset variants.** `docs/map-asset-packs.md` requires the same variant on
every machine. If the implementation uses `string.GetHashCode()` anywhere, that guarantee is already
broken — .NET randomises string hashing per process. Verify, and if so replace with an explicit
FNV-1a or similar in the engine, not with a Unity-side workaround.

**10.8 An acoustic category on `WorldLocation`.** `Type` is free-form; ambient beds are blocked on it
(§8.6). Requested: a controlled `AcousticCategory` of ~6–8 values defaulting to `silent`.

**10.9 A `netstandard2.1` CI job.** Once multi-targeting lands, CI must build the netstandard2.1
output on every PR. Without it, a Domain change that breaks Unity is discovered weeks later by
someone else.

---

## 11. What genuinely worries me

Honest risks, worst first. None is a reason not to do this; all are reasons to sequence carefully.

**11.1 `System.Text.Json` in Unity (§3.5) — highest risk, and it is a *packaging* risk, which is the
worst kind because it is unglamorous and blocks everything.** The dependency graph collides with
assemblies Unity ships, `System.Runtime.CompilerServices.Unsafe` most of all. ILRepack solves it,
but I have not proven it against this specific engine build, and I would not want to discover a
problem here in month two. **Spike it on day one** with a hello-world Unity project and the real
`Engine.dll`, before any scene work.

**11.2 The boundary erodes under deadline pressure.** The asmdef fence (§3.3) makes calling
`GameEngine` from a MonoBehaviour impossible, but it cannot stop someone *mutating* a Domain object
from Presentation, and it cannot stop someone adding an asmdef reference at 1 a.m. The Cecil-based
EditMode test is the real defence and it should be written **early**, when there is nothing to fix,
rather than late when it produces forty failures and gets disabled.

**11.3 The ~90 `ArgumentNullException.ThrowIfNull` rewrites (§3.1).** Mechanical, but ninety chances
to change behaviour in the most safety-critical layer of the product, and the tests that would catch
a mistake are the ones being split apart in §1.11 at the same time. **Do the netstandard2.1
migration while the full `net10.0` test suite is green and untouched**, and run it against both
targets.

**11.4 Straight-line movement (§1.5) is more entangled than it looks.** §6.6's waypoint decomposition
is sound, but I have *not* verified that an L-shaped move provokes exactly one opportunity attack
rather than one per leg. If it provokes twice, that is a rules bug visible to the player and the fix
is in the engine. **Write that test before building the movement UI**, not after.

**11.5 Scope. This is the one I would actually bet on.** Sections 6, 7 and 8 each describe several
weeks of work, and the direction documents together specify hundreds of concrete requirements — 18
audio cues, 10 narrative beats, 5 layout archetypes, a 5-beat session arc, a 4-layer adaptive music
system, a full progression presentation contract. All of it is good. Attempting it in parallel
produces six half-finished systems and a game that feels worse than the WPF app, which at least
works. §13 proposes an order; the discipline is to keep one vertical slice playable at all times.

**11.6 Unity CI is a real regression.** The existing pipeline builds on Windows, runs 33 test
projects, and produces WPF GUI snapshots as artifacts. Unity's test framework (EditMode/PlayMode)
exists but needs a licensed runner in CI, and there is no cheap equivalent of the pixel-snapshot
tests. Honest answer: **keep the .NET CI for `Domain`/`Engine`/`Data` where correctness actually
lives, add the netstandard2.1 build job, and accept that the Unity layer is verified by running it.**
Do not build elaborate Unity CI for a project with one developer and no distribution — but do make
`DMAI.Session` testable in EditMode without the player loop, because that is where the pump logic
and beat construction live and those are worth testing.

**11.7 The direction docs assume one protagonist.** Second-person narration, the Resolution Beat's
implicit subject, and the party rail all shift with four PCs. The engine is already party-aware
(XP divides by eligible PC count with a deterministic remainder), so this is a presentation and
prompt problem, not an engine one — but the narrative doc's voice rules will need revisiting and
that is design work, not implementation.

**11.8 Model latency may simply be worse than 4B benchmarks suggest.** §7 is built on the assumption
that deferred, overlapped generation hides most of it. If a round-close narration takes 15 seconds
on the target machine, the overlap window is not big enough and the answer is templated dressing
(§7.2's option 1) doing far more of the work than planned. **Measure the actual TTFT and tokens/sec
on the target hardware before finalising the pacing constants**, and treat `NarrationPacingSO` as
something to tune against real numbers rather than the ones I guessed.

**11.9 Two seeds are easy to conflate.** `Seed` (art variants, rerollable) and `GenerationSeed`
(geometry, reproducible) are one careless line apart, and conflating them means rerolling the
decorations silently changes the map. Name the Unity-side variables so the mistake is visible.

**11.10 An orphaned `llama-server.exe` per play-mode exit.** Small, certain, and infuriating. §3.6.

---

## 12. Decisions for the owner

Genuine coin-flips or matters of taste. I have given a recommendation for each, but these should be
decided rather than defaulted.

1. **FMOD or Unity's `AudioMixer`?** (§8.1) The licensing objection is gone with no commercial
   distribution, and FMOD is meaningfully better at the four-layer adaptive music. Much cheaper to
   decide before the cue layer is written. *Recommend: Unity `AudioMixer`, revisit at Phase 3.*
2. **Temperature 0.4 or 0.75?** (§7.7) Tool-call fidelity versus prose quality; the code and the
   narrative doc disagree. *Recommend: split — 0.4 for tool-calling turns, 0.75 for pure-narration
   turns — and measure.*
3. **Confirm the model is 4B, not 9B.** (§1.10) `narrative-direction.md` and `Models.cs` disagree.
   The entire pacing design in §7 assumes 4B. **Resolve this first**; it is a one-line check with
   large consequences.
4. **Auto-roll or always prompt?** Does a player d20 request stop and ask, or roll itself with a
   visible animation? Ceremony versus pace, and it is pure taste. *Recommend: prompt by default,
   setting to auto, because the dice are the drama.*
5. **Second person with four PCs.** "You push the door" was written for one protagonist. Options:
   keep second person addressing the party collectively; switch to third person naming the acting
   PC; or second person for the currently-controlled PC. *Recommend: third person in combat where
   the acting character matters, second person in exploration where the party acts together — but
   this is a voice decision and it is yours.*
6. **Render inert schema fields?** (§10.4) `ElevationFeet` and map-level `Cover` are authored but
   never adjudicated. *Recommend: do not render them until the engine reads them.*
7. **PrimeTween or DOTween?** (§2.5) Both fine; PrimeTween is allocation-free and free, DOTween is
   more familiar and better documented. *Recommend: PrimeTween. Either way, pick one and never mix.*
8. **How much Map Builder?** (§9.6) It can be a faithful rebuild or it can become a genuinely good
   authoring tool with live lighting, continuous validation and archetype stamps. The second is
   weeks more work and would be very satisfying. *Recommend: faithful rebuild first, upgrade after
   the first playable slice.*
9. **VContainer, or the `SessionReferenceSO`?** (§4.3) *Recommend: the SO. Adopt DI only if wiring
   starts to hurt.*
10. **Does the WPF app stay buildable during the port?** Keeping it alive means the game is always
    playable but the engine must satisfy two front ends. Deleting it early frees the six
    WPF-coupled test projects but leaves a gap with nothing playable. *Recommend: keep WPF buildable
    until the Unity vertical slice reaches combat, then delete it in one commit — and split the
    engine assertions out of the WPF-coupled tests first, whichever way you go.*

---

## 13. Suggested sequencing

Not a schedule. An order, chosen so that something is playable at every step and the riskiest
unknowns resolve first.

**Phase 0 — De-risk (before any scene work).**
Spike `Engine.dll` loading in an empty Unity project with the real `System.Text.Json` graph (§11.1).
Multi-target `Domain`/`Engine`/`Data` and add the netstandard2.1 CI job. Split engine assertions out
of the six WPF-coupled test projects. Write the L-shaped-move opportunity-attack test (§11.4).
*If Phase 0 fails, everything after it is wrong, and better to know in week one.*

**Phase 1 — The skeleton.** Boot + Shell + Table scenes. `EngineSession` with commands, queries and
the turn pump. The asmdef fence and the Cecil boundary test. Load a campaign, show narration, take a
non-combat turn end to end. **No art, no audio, no animation.** The point is to prove the boundary
holds under a real turn.

**Phase 2 — The map.** `MapRenderer`, `GridSpace`, tokens, the overlay mask, URP 2D lighting from
`TacticalMapLight`. Load `The Ruined Crypt of Saint Veyra` and look at it. Combat playable but ugly.

**Phase 3 — The beat pipeline.** `BeatSequence`, `BeatPlayer`, `CombatFeelProfileSO`, the Resolution
Beat card, token movement tweening, damage numbers, hit-stop, Cinemachine Impulse. **This is where
the project stops being a port and starts being a game**, and it is the phase most worth taking time
over.

**Phase 4 — Audio.** The four buses, `AudioCueSO`, the ~18 Phase-1 cues fired from beats. High
value-per-hour and it makes Phase 3 feel twice as good.

**Phase 5 — Narration pacing.** Streaming, the reveal queue, the thinking state, speaker
differentiation, templated dressing for the 23 engine strings. Measure real latency first (§11.8).

**Phase 6 — Progression presentation.** XP awards fused into the Resolution Beat, the coalesced
multi-kill card, the threshold-crossing moment, the level-up screen.

**Phase 7 — The rest.** Map Builder, AI generation panel, world/quests, contextual rules lookup,
settings, session framing (cold open, session clock, close).

Delete WPF somewhere around Phase 3, once combat is playable in Unity.

---

## Appendix A: the rules, condensed

For pinning above a desk.

1. The engine adjudicates. Unity presents. **No `if` in Unity branches on a D&D rule.**
2. The asmdef graph enforces rule 1. Only `DMAI.Session` may reference `Engine`.
3. **If deleting a ScriptableObject would change a dice outcome, it is in the wrong place.**
4. Beats are emitted **after** the commit, never during computation.
5. Mechanics never wait for the model.
6. Never print a number the player already saw.
7. Missing art is a fallback. Missing audio is silence. Neither is an exception.
8. `TacticalMap` JSON is authoritative. A Unity Tilemap is a render cache.
9. `GridSpace` is the only place that converts coordinates.
10. If a result record lacks a field the presentation needs, **add it to the record.**
