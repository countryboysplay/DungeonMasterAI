# Encounter Design Direction

**Scope:** the encounter and spatial layer — map generation as level design, combatant placement,
encounter composition and session pacing, and environmental storytelling within the existing
tactical-map schema and asset packs.

**Out of scope:** overall game design ownership (a Game Designer holds that in parallel), UI chrome,
narrative voice, rules correctness.

**Constraint reminder:** 2D grid tactical maps, WPF, a small local model as the author, existing
`core.fantasy.crypt` asset keys. Everything below is designed to fit that. No 3D, no new runtime.

This document is design direction only. It changes no code, XAML, or schema. Anything that would
require a schema change is quarantined in its own section at the end, because that is a bigger cost
and should be a deliberate decision, not a side effect.

---

## 1. What I read

Docs: `docs/ai-map-generation.md`, `docs/map-asset-packs.md`, `docs/tactical-map-schema-v1.md`.

Code:

- `windows/src/DungeonMasterAI.Domain/TacticalMaps.cs` — the authored map schema.
- `windows/src/DungeonMasterAI.AI/TacticalMapAiGeneratorService.cs` — the generator, its prompts,
  its asset whitelist, and its acceptance rules.
- `windows/src/DungeonMasterAI.Engine/TacticalMapGeometry.cs` — deterministic map queries.
- `windows/src/DungeonMasterAI.Engine/GameEngine.cs` — placement, movement, cover, line of sight.
- `windows/src/DungeonMasterAI.Engine/BattlefieldEffects.cs` — runtime battlefield effects.
- `windows/src/DungeonMasterAI.Engine/DmToolRouter.cs` — the tool surface the DM model can drive.
- `windows/src/DungeonMasterAI.App/Controls/CombatGridControl.cs` and
  `windows/src/DungeonMasterAI.App/Controls/TacticalMapControl.cs` — the two renderers.
- `windows/src/DungeonMasterAI.App/Views/CombatView.xaml`, `LivePlayView.xaml`, `WorldView.xaml`,
  `MapsView.xaml` — which renderer each surface uses.
- `windows/src/DungeonMasterAI.Data/CampaignReadinessValidator.cs` and
  `windows/src/DungeonMasterAI.Data/CampaignRehearsalService.cs` — the only automated map-quality gates.
- `windows/tests/DungeonMasterAI.MapRendererTests/Program.cs` — the reference fixture, *The Ruined
  Crypt of Saint Veyra*, which is the best authored map in the repo and my benchmark throughout.

---

## 2. The headline finding, stated first

**The tactical map is not the battlefield.** It is a picture that lives in a different room of the
application from the fight.

Three independent facts, each verifiable in one line of code:

1. **The combat view does not render the tactical map.** `CombatView.xaml:166` and
   `LivePlayView.xaml:87` both host `CombatGridControl`. `CombatGridControl` has no `Map`
   dependency property at all — its registered properties are `Campaign`, `Encounter`, `Revision`,
   and the five spell-preview properties (`CombatGridControl.cs:10-44`). `TacticalMapControl`, the
   control that draws rooms, walls, doors, terrain, props, and lights, appears in exactly one place:
   `MapsView.xaml:114`, the Map Builder.

2. **The engine never reads map geometry.** `TacticalMapGeometry` exposes a correct and complete
   spatial API — `IsCellWalkable`, `IsDifficultTerrain`, `MovementCostFeet`, `CanMoveBetween`,
   `HasLineOfSight` (`TacticalMapGeometry.cs:96-148`). Every caller in the repository is either
   validation, the map editor, or a test: `TacticalMapAiGeneratorService.cs:202`,
   `MainViewModel.MapEditing.cs:141`, and the two test programs. `GameEngine` calls none of it.
   Combat movement blocking (`GameEngine.cs:1898-1908`), movement cost
   (`GameEngine.cs:1918-1927`), and cover (`GameEngine.cs:1933-1944`) all read
   `encounter.Terrain` — a `List<TerrainFeature>` (`Models.cs:327-342`) that is a completely
   separate type from `TacticalMapTerrain` and `TacticalMapProp`, populated only by the DM model
   calling `add_terrain_feature` (`DmToolRouter.cs:142-155`) at runtime.

3. **Nothing in the app ever binds an encounter to a map.** `CampaignState.EncounterMapBindings`
   (`TacticalMaps.cs:17`) is referenced zero times in `DungeonMasterAI.App` and zero times in
   `DungeonMasterAI.Engine`. Its only readers are the readiness validator
   (`CampaignReadinessValidator.cs:406`), the rehearsal service
   (`CampaignRehearsalService.cs:191,209`), and the renderer test. The rehearsal service already
   says so out loud: *"Tactical map '{name}' is not bound to any encounter and will never be shown
   during play."* (`CampaignRehearsalService.cs:195-196`).

The consequence for play: the generator can author a beautiful crypt with a pillar that breaks
line of sight, flooded ground that costs double movement, and an ironbound door that has to be
opened — and then the party fights on a featureless, unbounded grid with none of it. You can walk
through the wall. You can shoot through the pillar. The flooded chamber costs nothing.

Everything else in this document assumes that connection gets made. Without it, no amount of
generator improvement changes what a player experiences, because the player never sees the map
during the fight. **This is the number-one recommendation and it is a plumbing job, not a design
job.** The design work below is what makes the plumbing worth doing.

---

## 3. What the schema can express today

This is the good news. The schema is more capable than either the generator or the engine uses.
Full inventory from `TacticalMaps.cs`:

| Layer | Type | Tactically meaningful fields |
|---|---|---|
| Rooms | `TacticalMapRoom` | `Kind` (room/corridor/cave/exterior), rect, `DmOnly` |
| Walls | `TacticalMapWall` | grid-line segment, `BlocksMovement`, `BlocksLineOfSight`, `HeightFeet` |
| Doors | `TacticalMapDoor` | edge + orientation, `State` (open/closed/locked/barred), `Secret`, `Discovered`, `BlocksMovementWhenClosed`, `BlocksLineOfSightWhenClosed` |
| Terrain | `TacticalMapTerrain` | rect, `DifficultTerrain`, `BlocksMovement`, `BlocksLineOfSight`, `HeavilyObscured`, **`Cover`**, **`ElevationFeet`** |
| Props | `TacticalMapProp` | rect + rotation, `BlocksMovement`, `BlocksLineOfSight`, `DifficultTerrain`, **`Cover`** |
| Lights | `TacticalMapLight` | sub-cell origin, `BrightRadiusFeet`, `DimRadiusFeet`, `Color` |
| Spawns | `TacticalMapSpawnPoint` | cell, `Side`, `CharacterKey` (reserve a specific creature's start square) |
| Zones | `TacticalMapZone` | rect, `ZoneType` (encounter/trap/loot/quest/trigger), `ReferenceId` |
| Visibility | `TacticalMapVisibility` | `RevealAll`, `RevealedRoomIds`, `RevealedCells` |

That is a genuinely well-designed tactical vocabulary. Cover grades, sight blockers independent of
movement blockers, per-edge doors with four states, secret doors, obscurement, light radii, reserved
spawn squares, and trigger zones. With those pieces you can author every classic 5e encounter
shape: the pillared hall, the barricaded corridor, the ambush from an unlit side chamber, the
fighting retreat through a door you can bar behind you.

**Three fields are authored-but-inert and worth knowing about before you design around them:**

- `TacticalMapTerrain.ElevationFeet` — the readiness validator checks it is a multiple of 5
  (`CampaignReadinessValidator.cs:344`) and nothing else reads it. `TacticalMapGeometry` never
  looks at it; no renderer draws it. Elevation is currently a comment field. **Do not design
  encounters that depend on height until it is wired.** Treat elevation as a schema-change item
  (Section 9), not an available tool.
- `Cover` on both `TacticalMapTerrain` and `TacticalMapProp` — no query in `TacticalMapGeometry`
  reads it. `CombatGridControl.cs:171,331` reads `TerrainFeature.Cover`, the *encounter* type, not
  the map type. So map-authored cover is validated for spelling and then ignored. It becomes real
  the moment the map→encounter connection in Section 2 is made.
- `TacticalMapWall.HeightFeet` — stored, validated as non-negative, never queried.

---

## 4. What the generator actually asks for, and what it will produce

### 4.1 The prompt

`BuildGenerationPrompt` (`TacticalMapAiGeneratorService.cs:253-286`) is a *legality* brief, not a
*design* brief. Read what it actually asks for:

- application-owned constants (type, theme, dimensions, scale, asset set, seed, fog);
- the allowed asset-key list;
- "Include rooms, walls, doors where useful, terrain, props, lights, spawnPoints, zones, and visibility";
- the coordinate contract — origin, bounds, axis-alignment, door edge conventions;
- "Include at least one spawnPoint with side `player`";
- "Gameplay flags must agree with the described object".

The only tactical guidance anywhere is one sentence buried in the *system* prompt
(`SystemPrompt()`, line ~296): *"Favor coherent connected play spaces, sensible entrances, tactical
choices, readable movement lanes, and useful cover without making the map impassable."*

That sentence is a vibe, not a specification. It has no counts, no ratios, no shapes, no
verification. A small local model at `temperature = 0.2` (`CompleteJsonAsync`, line ~120) is
strongly biased toward the *shortest structure that satisfies the checklist* — and the checklist
does not include tactical content.

### 4.2 What acceptance actually requires

`ValidateGeneratedMap` (`TacticalMapAiGeneratorService.cs:196-218`) accepts a candidate if:

1. `TacticalMapGeometry.Validate` reports no errors — bounds, axis-alignment, unique IDs, valid
   dimensions. All *legality*, no *quality*.
2. `map.Rooms.Count > 0`.
3. At least one spawn point with `Side == "player"`.
4. Every asset key is on the whitelist.

**A 30×20 map consisting of one rectangular room, four boundary walls, one spawn point, zero props,
zero terrain, zero doors, and zero lights passes every one of those checks.** That is the classic
open field, and under a low-temperature small model it is the attractor state, not the edge case.
Nothing in the pipeline can tell the difference between that map and the Ruined Crypt.

### 4.3 The gap, stated as a list

Schema can express it → generator is never asked for it → nothing checks it:

| Capability in schema | Asked for in prompt? | Enforced at acceptance? |
|---|---|---|
| `Cover` grades on props/terrain | mentioned once, vaguely, in system prompt | no |
| `BlocksLineOfSight` independent of movement | "flags must agree with the object" | no |
| Multiple rooms / corridors (`Kind`) | "include rooms" | only `Count > 0` |
| Doors as tactical objects (`locked`/`barred`) | "doors where useful" | no |
| Secret doors | "mark secret doors with secret=true" | no |
| `HeavilyObscured` | not mentioned | no |
| Light radius shaping darkness | "lights" in a list | no |
| Enemy spawn points | **not mentioned at all** | no — only `player` is required |
| Zones (encounter/trap/loot/trigger) | "zones" in a list | no |
| `DifficultTerrain` | mentioned as an example | no |
| `ElevationFeet` | not mentioned | inert anyway |

The enemy-spawn omission is the sharpest one. The prompt requires a player spawn and says nothing
about where the opposition starts. A generated map therefore routinely has one spawn point and no
opposing placement — which hands the placement problem straight to the by-index fallback in
Section 5.

### 4.4 A concrete vocabulary bug in the spawn layer

Three layers disagree about what a side is called:

- The generator **requires** `Side == "player"` (`TacticalMapAiGeneratorService.cs:211`).
- `CampaignReadinessValidator.SpawnSides` is `["party", "enemy", "ally", "neutral"]`
  (`CampaignReadinessValidator.cs:249`) and raises an **error** on any other value
  (line 370-371).
- `GameEngine` combat sides are `party` / `opposition` / `neutral`
  (`GameEngine.cs:1864-1874`).

So every AI-generated map is *guaranteed* to fail campaign readiness with
`"Spawn point '...' has unsupported side 'player'"`, and also to trip the warning
`"defines spawn points but none is marked for the party"` (line 378-379), because the generator's
mandatory value is the one value the validator rejects. This is a placement-layer bug in my lane,
so I am flagging it; the fix is an engineering decision about which vocabulary wins.

---

## 5. Placement and initial tension

Commit `d95fa52` fixed a real correctness bug. It did not make placement a design act.

The current rule is `FreePlacementColumn` (`GameEngine.cs:2064-2070`):

```
gridX = encounter.Combatants.Count * 2;
while (occupied) gridX += 2;
```

Party members land on row `y = 0`, NPCs on row `y = 6` (`AddCombatant`, `GameEngine.cs:698`),
each two columns apart. Every fight in the product begins as two parallel firing lines, thirty feet
apart, on an unbounded empty plane.

Read as level design, that opening states four things to the player, all of them wrong:

- **No approach.** Thirty feet is one Dash for most PCs and inside longbow range for everyone.
  There is no closing phase, so there is no decision about *how* to close.
- **No asymmetry.** Both sides get the identical formation. Nobody is dug in, nobody is flanking,
  nobody is caught out of position. There is no "we have the ground" or "they have the ground".
- **No fiction.** A skeletal warden that has stood beside an altar for two hundred years does not
  line up in rank two columns from its neighbour. Placement contradicts the room's story before the
  DM narrates a word.
- **No reason to move.** With no cover, no blocking terrain, and everyone already in range, the
  optimal opening is *stand still and trade attacks* — for both sides. That is the failure the
  brief asked me to check for directly, and it is present, and it is caused by placement as much as
  by the empty map.

Meanwhile `TacticalMapSpawnPoint` already exists, already carries `Side` and `CharacterKey` (so a
named creature can own a specific square), and the rehearsal service already knows how to fall back
to spawn points when no combatant is positioned (`CampaignRehearsalService.cs:225-227`). The
authoring tool for good placement is built. Nothing calls it during combat setup.

### The placement design I want

**Placement is authored, not computed.** When an encounter is bound to a map, combatants are seated
on that map's spawn points, matched by `Side`, with `CharacterKey` reservations honoured first.
Index-order placement remains only as the last-resort fallback when no map is bound.

Spawn sets should be authored to one of five named opening shapes. These are the pacing beats of an
encounter's first round, and I want the generator to pick one deliberately per encounter:

| Opening | Party start | Opposition start | What the first round is about |
|---|---|---|---|
| **Confrontation** | at the entrance | across the room, aware | choosing a lane; who takes the cover first |
| **Ambush** | mid-room, committed | 2–3 unlit or out-of-sight positions flanking the party's path | recovering; breaking contact vs. turning on the nearest threat |
| **Siege** | outside a doorway or chokepoint | dug in behind cover past it | paying the toll to enter, or refusing to |
| **Interruption** | spread out, mid-exploration | entering from a door behind or beside the party | reforming a line before it costs someone |
| **Standoff** | one side of a hazard, obstacle, or flooded span | the other side | who crosses, and what it costs to cross |

Design rules for spawn authoring, regardless of shape:

- **Opening distance 25–40 ft for Confrontation, 10–20 ft for Ambush and Interruption, 35–60 ft for
  Siege and Standoff.** Under 15 ft with no cover is a knife fight where positioning cannot matter;
  over 60 ft on a 30×20 map is two rounds of walking.
- **Never place both sides in a straight open lane with no cover between them.** If a straight line
  between any party spawn and any opposition spawn crosses zero cover-bearing or sight-blocking
  cells, the encounter is a firing line — the fix is a prop, not a stat.
- **Opposition spawns are never all in one cluster.** At least two distinct groups separated by a
  wall, a prop, or ≥15 ft, so the party has to choose which threat to answer first. One cluster is
  one decision; two clusters is a fight.
- **Every party spawn has a fallback square** — a cell within 15 ft that has half cover or better,
  or is behind a closable door. If the party has nowhere to retreat to, "retreat" is not a tactic,
  and a fight with no retreat option is a fight with one tactic.
- **No spawn adjacent to a map edge** unless the edge is the fiction (a cliff, a sealed wall). Edge
  spawns silently remove half of a combatant's movement options.

---

## 6. What the generator should be asked to produce

The generation prompt should carry a **tactical content brief** alongside the coordinate contract.
It is currently all contract and no brief. Everything in this section uses only fields that already
exist in the schema — nothing here requires a schema change.

### 6.1 Density budget, expressed per 100 squares

A small model follows counts far more reliably than it follows adjectives. "Useful cover" produces
nothing; "4 to 7 cover objects" produces cover. Scale these to `widthSquares × heightSquares / 100`:

| Element | Target per 100 squares | Floor (below this = reject) |
|---|---|---|
| Distinct rooms/corridors (`rooms[].kind`) | 2–4 | 2 for maps ≥ 300 squares |
| Cover-bearing props/terrain (`cover != "none"`) | 4–7 | 3 |
| Sight blockers (`blocksLineOfSight = true`) | 1–3 | 1 |
| Difficult-terrain regions | 1–2 | 0 (optional) |
| Doors | 1–2 | — |
| Lights | 2–4 | 1 |
| Opposition spawn points | 2–5, in ≥ 2 clusters | 2 |
| Party spawn points | 1–2 | 1 |

For a 30×20 map (600 squares, the app default) that is roughly: 12–24 rooms and corridors is too
many, so cap by absolute count as well — **4–8 regions, 6–14 cover objects, 2–5 sight blockers,
2–6 lights, 4–8 opposition spawns across 2–3 clusters**. State the absolute numbers in the prompt
for the requested dimensions; do not make the model do the arithmetic.

### 6.2 Named layout archetypes

Ask the model to pick one archetype and build to it. Named shapes with explicit geometry outperform
free-form "make it tactical" by a wide margin on small models. All five are expressible today:

1. **Pillared hall.** One large room; 4–6 blocking, sight-blocking pillars in a regular grid with
   10–15 ft gaps. Creates lanes and breaks. *Schema:* `prop.pillar.stone_round`,
   `blocksMovement=true`, `blocksLineOfSight=true`, `cover="three-quarters"`.
2. **Chokepoint and flank.** Two rooms joined by a door plus one longer indirect corridor. The
   short path is contested, the long path costs movement. *Schema:* two `rooms`, an interior wall,
   one `door`, one `corridor`-kind room.
3. **Broken ground.** One irregular space with rubble (difficult terrain) and collapsed masonry
   (half cover) laid so that the fast route is exposed and the covered route is slow. *Schema:*
   `terrain.rubble.stone` with `difficultTerrain=true`, `prop.rubble.pillar` with `cover="half"`.
4. **Flooded chamber.** A central difficult-terrain span that separates two dry fighting platforms.
   This is the Standoff opening's home. *Schema:* `terrain.water.crypt_shallow`,
   `difficultTerrain=true`.
5. **Reliquary.** A guarded objective — altar or sarcophagus — with cover *around* the objective
   so that holding it is a position, not a square. *Schema:* `prop.altar.stone_crypt` or
   `prop.sarcophagus.stone`, `blocksMovement=true`, `cover="half"`, plus a `zone` of type `loot` or
   `quest` over it.

The Ruined Crypt fixture (`MapRendererTests/Program.cs:120-175`) is archetypes 1, 3, 4, and 5
combined, and it is the right quality bar. It should be included in the prompt as a worked example.
A one-shot example of a good map is worth more than a page of adjectives to a small model.

### 6.3 Cover semantics the prompt should state explicitly

The model currently guesses. Give it the mapping:

- `prop.pillar.stone_round` → `blocksMovement=true`, `blocksLineOfSight=true`, `cover="three-quarters"`
- `prop.sarcophagus.stone`, `prop.altar.stone_crypt` → `blocksMovement=true`,
  `blocksLineOfSight=false`, `cover="half"`
- `prop.rubble.pillar`, `terrain.rubble.stone` → `blocksMovement=false`, `difficultTerrain=true`,
  `cover="half"`
- `terrain.water.crypt_shallow` → `difficultTerrain=true`, `cover="none"`

Note the spelling: the engine's `NormalizeCover` (`GameEngine.cs:1946-1957`) accepts
`three-quarters`, `threequarters`, and `three_quarters`, but `CampaignReadinessValidator.CoverKinds`
(line 248) only accepts `three_quarters`. The reference fixture uses `three_quarters`
(`Program.cs:154`). **The prompt should specify `three_quarters` exactly**, since that is the value
that passes both gates.

### 6.4 Acceptance checks the generator should apply

The repair pass already exists and is unused for quality. It gets one automatic retry
(`GenerateAsync`, the `for (attempt = 1; attempt <= 2)` loop) with the previous JSON and the
error list. Adding tactical checks to `ValidateGeneratedMap` as *errors* means the model gets one
free shot at fixing a boring map, which is exactly what a repair pass is for.

Proposed checks, all computable from the existing schema with `TacticalMapGeometry`:

- **Open-field check.** Count cells with `cover != "none"` or `blocksLineOfSight=true`. Reject if
  below the floor in 6.1. *This is the single highest-value check.*
- **Firing-line check.** For each (party spawn, opposition spawn) pair, walk the cells between them.
  Reject if **every** pair has a clear line with zero cover-bearing cells crossed. At least one
  approach must be contested.
- **Reachability check.** Every opposition spawn must be reachable from every party spawn treating
  all doors as openable. The rehearsal service already implements exactly this
  (`CampaignRehearsalService.cs:186-240`, `BuildBlockedCells`) — reuse it at generation time
  instead of only at campaign readiness, so a stranded map never reaches the user.
- **Opposition-presence check.** At least two opposition spawn points in at least two clusters.
- **Region check.** At least two `rooms` entries for maps ≥ 300 squares.
- **Sight-line variety check.** Sample `HasLineOfSight` across pairs of spawn cells; if it returns
  true for every pair, the map has no sight breaks and should be repaired.

These are deterministic and cheap. None of them requires the model to be smarter; they require the
application to know what a good map looks like, which is the correct division of labour already
described in `docs/ai-map-generation.md`: *"The current model therefore remains the planner. The
deterministic map engine ... remain[s] responsible for correctness."* Quality is correctness's
neighbour and belongs on the same side of the line.

---

## 7. Encounter composition and session pacing

A session is not a list of fights. It is a rhythm. Right now the product has no rhythm because it
has no notion of an encounter's *role* in a sequence — every encounter is the same shape.

### 7.1 The five-beat arc

Target rhythm for a two-to-three hour session:

```
Time    | Beat            | Tension | Spatial character
--------|-----------------|---------|--------------------------------------------
0:00    | Arrival         | Low     | Open, lit, readable. Environmental story only.
0:15    | Probe fight     | Medium  | Small space, 2-3 enemies, one tactical idea taught
0:35    | Exploration     | Low     | Branching. Optional loot zone. Secret door here.
0:55    | Pressure fight  | High    | Larger space, 2 enemy clusters, cover matters
1:25    | Discovery       | Low     | The room that explains the dungeon. No combat.
1:40    | Set-piece       | Highest | Archetype 1 or 5. Hazard + objective + numbers
2:10    | Resolution      | Low     | Lit, safe, exit visible. Loot and consequence.
```

The ratio that matters: **roughly 40% combat, 60% not-combat, and no two combats adjacent without
a low-tension beat between them.** A session that is fight-fight-fight flattens; the second fight
is not experienced as harder, only as longer.

### 7.2 Encounter variety axes

Escalation should be spatial and compositional before it is numerical. Four axes, escalate along
one or two at a time — never all four at once, which reads as a difficulty spike rather than a
climax:

- **Space:** small enclosed → large with cover → large with hazard + objective.
- **Composition:** one enemy type → two types with different ranges → types that punish clustering.
- **Opening:** Confrontation → Interruption → Ambush → Siege (Section 5).
- **Objective:** kill everything → kill everything before X → hold a position → reach the exit.

Objective variety is the cheapest large win available and it needs no new systems. The existing
`TacticalMapZone` with `zoneType="trigger"` and `zoneType="encounter"` plus `BattlefieldEffectState`
with a `DurationRounds` and a `trigger` (`BattlefieldEffects.cs:7-60`) already expresses "this area
hurts, from round N, until round M." A fight over a shrinking safe area is a different fight from a
fight over hit points, and both are already buildable.

### 7.3 Encounter cadence rules

- **No two consecutive encounters use the same layout archetype.** If the last fight was a pillared
  hall, the next one is broken ground or a chokepoint.
- **No two consecutive encounters use the same opening shape.** Ambush after ambush stops being an
  ambush.
- **Every third encounter introduces one new spatial idea** — a door that can be barred, an area
  that becomes difficult terrain mid-fight, an enemy that starts out of sight. One idea, taught in
  a small fight, then applied under pressure in a larger one.
- **The set-piece is the only encounter permitted to combine hazard, objective, and two enemy
  clusters.** If every fight is a set-piece, none of them is.

---

## 8. Environmental storytelling within the current asset packs

The default whitelist (`TacticalMapAiGeneratorService.DefaultAssetKeys`, lines 47-63) is fifteen
keys: two floors, two walls, three doors, two terrains, four props, two lights. That is a small
vocabulary. It is enough, because environmental storytelling is grammar, not vocabulary — the
meaning is in the *arrangement*, not in the number of nouns.

**The rule I want enforced: every room answers "what happened here?" through arrangement alone,
before any narration.** No filler rooms. A room the DM has nothing to say about is a room that
should not have been generated.

### 8.1 A working grammar for the crypt pack

| Story beat | Arrangement, using only whitelisted keys |
|---|---|
| **Someone fought here and lost** | `door.wood.broken` on the entry edge; `terrain.rubble.stone` spilling *inward* from it; one `prop.rubble.pillar` toppled across the room's centre line; lights on the far side only, so the party enters from dark toward light |
| **Someone fortified here and held** | Intact `door.wood.ironbound` with `state="barred"`; `prop.sarcophagus.stone` dragged *off* the wall line into the room, forming a firing position that faces the door; two lights behind that position |
| **Something was buried and did not stay buried** | `prop.sarcophagus.stone` with `terrain.rubble.stone` immediately adjacent on one side only; the nearest light unlit (omitted); an opposition spawn *inside* the rubble cell |
| **This place drowned slowly** | `terrain.water.crypt_shallow` filling the lowest region and spilling one cell through a doorway; `prop.altar.stone_crypt` half inside the water; no lights in the flooded region |
| **They left in a hurry** | Two `prop.rubble.pillar` scattered off any structural line; `door.stone.secret` with `discovered=false` on the far wall; a `zone` of type `loot` beyond it |
| **This was sacred and still is** | `prop.altar.stone_crypt` centred and axis-aligned; `light.brazier` symmetric on both sides; floor key switched to `floor.stone.crypt_flagstone` while surrounding rooms use `floor.stone.flagstone` |

Three grammatical devices carry almost all of it:

- **Alignment vs. displacement.** A prop on the room's axis or against a wall reads as *placed*.
  The same prop rotated and off-axis reads as *disturbed*. Same asset, opposite story.
- **Directionality.** Rubble that spills inward from a door was pushed in. Rubble that spills
  outward was pushed out. The generator currently places rectangles with no sense of direction; a
  single instruction — "debris rectangles must touch the feature they came from" — buys narrative
  causality for free.
- **Absence.** An unlit brazier in a room where every other brazier is lit is a sentence. Light
  placement is the cheapest storytelling tool in the pack and the generator currently treats
  lights as decoration sprinkled at random.

### 8.2 Light as the navigation and pacing tool

`TacticalMapLight` has `BrightRadiusFeet`, `DimRadiusFeet`, and `Color`. Used deliberately, light is
the critical-path guide, the ambush enabler, and the tension dial — in a top-down 2D map it does
everything that lighting does in a 3D level.

Direction for the generator:

- **The critical path is lit.** Rooms on the route from the party spawn toward the objective carry
  2+ lights. Optional and secret areas carry 0–1. Players follow light; this is the map's only
  wayfinding channel and it currently carries no signal.
- **Ambush positions are unlit.** An opposition spawn with no light within its bright radius is an
  ambush. This is how you author "you did not see it before it hit you" without needing a stealth
  system.
- **Warm vs. cool separates the living from the dead.** `light.brazier` at `#E39A52` for tended,
  occupied, or sacred space; omit lights entirely, or use a colder value, for abandoned space.
  The reference fixture already does this (`Program.cs:158-161`).
- **The exit is visible.** At least one light within the room containing the exit or the objective,
  so the destination reads from the entrance.

### 8.3 Zones carry the story the map cannot draw

`TacticalMapZone` with `ReferenceId` is the hook between a space and campaign content, and the
readiness validator already checks that `encounter` and `quest` zone references resolve
(`CampaignReadinessValidator.cs:386-391`). Every generated map should carry:

- one `encounter` zone over each opposition cluster, so the DM model knows *where* the fight is;
- one `loot` zone in the optional branch, so exploration is rewarded rather than merely permitted;
- one `trigger` zone at the point where the party becomes visible to the opposition, which is what
  makes an Ambush opening an ambush rather than a surprise announcement.

That last one is the highest-value zone and it is currently never generated.

---

## 9. Requires schema change

Everything above uses the schema as it exists. These are the things I would want next, listed
separately because they are a larger cost and should be decided deliberately. **None of them is
required to fix the open-field problem.** They are ordered by value per unit of cost.

1. **`TacticalMapProp.CoverFromDirections` (or equivalent directional cover).**
   Cover today is a property of a square, not of a relationship. A pillar gives the same cover from
   every angle, so flanking a covered enemy is mechanically pointless — you cannot get an angle on
   them. Directional cover is the single change that turns "stand behind the pillar" into "move to
   where the pillar stops helping them," which is the difference between cover as a stat and cover
   as a spatial decision. *Cost: schema field, geometry query, engine cover calculation, renderer
   affordance.*

2. **Wire `ElevationFeet`, then add `TacticalMapTerrain.ElevationKind` (`ledge` / `stairs` /
   `pit`).** Elevation is the standard second axis of 2D tactical design — high ground for ranged
   advantage, a pit as a hazard, stairs as a contested chokepoint. The field exists but is inert
   (Section 3); making it real needs a renderer representation and a movement rule for transitions,
   plus asset keys, which the current pack does not have. *Cost: renderer, movement rules, new
   asset keys, geometry queries.*

3. **`TacticalMapProp.Destructible` / `HitPoints`.**
   A barricade you can break, a pillar you can topple. Destructible cover is what makes a defensive
   position temporary, which is what keeps a fight moving after round two. *Cost: schema, engine
   damage routing to map objects, renderer state, save-game state.*

4. **`TacticalMapZone.ZoneType = "hazard"` with a battlefield-effect template reference.**
   `BattlefieldEffectState` already does everything needed (`BattlefieldEffects.cs`), but it is
   runtime-only, created by the DM model. An authored hazard zone would let a map ship with "this
   area burns from round 3" as part of its design rather than as an improvisation. *Cost: schema,
   a template type, encounter activation wiring.*

5. **`TacticalMapSpawnPoint.Group` and `TacticalMapSpawnPoint.Facing`.**
   `Group` lets a map declare "these three spawns are one cluster," which makes the two-cluster rule
   in Section 5 authored rather than inferred. `Facing` lets a spawn say which way a creature is
   looking, which is the difference between an ambusher and a sentry. *Cost: schema only, plus
   whatever consumes it — cheap, and useful the day the map→encounter binding exists.*

6. **`TacticalMapRoom.NarrativeBeat` (short string).**
   A one-line "what happened here" per room, authored by the generator alongside the geometry, that
   the DM model can read when the party enters. This is the cheapest way to make Section 8's
   arrangement grammar legible to the narrator instead of relying on it to re-infer the story from
   coordinates. *Cost: schema field, prompt change, DM tool exposure.*

---

## 10. Acceptance: how we know it worked

Grey-box discipline applies here as it does anywhere. The generated map *is* the grey box — the
asset pack is the art pass — and `docs/map-asset-packs.md` already guarantees the separation:
*"Replacing an asset pack must not alter map coordinates, walls, doors, encounters, or save-game
state."* That is exactly the blockout/dress split, and it is already correct. So the quality gate
belongs on the geometry, before any art is resolved.

### Readability review, per generated map

**Critical path**
- [ ] The route from party spawn to the objective/exit crosses ≥ 2 named regions.
- [ ] Rooms on the critical path carry ≥ 2 lights; optional branches carry ≤ 1.
- [ ] No region is reachable only through a `secret` door (secrets are rewards, never gates).

**Combat**
- [ ] ≥ 3 cover-bearing objects per 100 squares.
- [ ] ≥ 1 sight blocker per 100 squares.
- [ ] At least one (party spawn → opposition spawn) line is contested by cover.
- [ ] Opposition spawns form ≥ 2 clusters.
- [ ] Every party spawn has a fallback square with half cover or better within 15 ft.
- [ ] Not every spawn pair has line of sight to each other at the start.

**Exploration**
- [ ] At least one optional region with a `loot` or `quest` zone.
- [ ] The optional region is distinguishable by lighting or floor key, not by minimap alone.
- [ ] Every region is reachable from every other with all doors treated as openable
      (the existing `CampaignRehearsalService` check, run at generation time).

**Story**
- [ ] Every room has ≥ 1 prop or terrain feature that is not structural.
- [ ] Debris features touch the feature they originated from.
- [ ] At least one light is deliberately absent where its siblings are present.

### Success metrics

- Zero generated maps pass acceptance with fewer than the density floors in 6.1.
- No generated map produces a campaign-readiness error on its spawn points (fix the
  `player`/`party` vocabulary collision first — Section 4.4).
- In a played encounter, ≥ 2 combatants move on ≥ 2 of the first 3 rounds. If everyone stands still
  and trades attacks, the space failed regardless of what the map JSON contains.
- Every encounter has at least two observed viable approaches, not one.
- A player asked "what happened in this room?" can answer without narration, > 70% of the time.

---

## 11. Recommended order of work

1. **Bind the map to the fight.** Encounter→map binding surfaced in the app; combat renders the
   bound map; `GameEngine` movement, cover, and line of sight consult `TacticalMapGeometry` for the
   bound map. Nothing else in this document matters until this is true. *(Engineering, not design.)*
2. **Seat combatants on authored spawn points** (Section 5), with the current index placement as a
   fallback only. Fix the `player`/`party`/`opposition` vocabulary collision (Section 4.4) as part
   of this.
3. **Add the tactical content brief and density budget to the generation prompt** (Section 6.1–6.3),
   including the Ruined Crypt as a worked example.
4. **Add tactical acceptance checks to `ValidateGeneratedMap`** (Section 6.4), so the existing
   repair pass gets a chance to fix a boring map. Start with the open-field check alone; it is the
   highest value single check in this document.
5. **Add light discipline and the environmental-storytelling grammar to the prompt** (Section 8).
6. **Encounter cadence** (Section 7) — this belongs to the Game Designer's session-level layer, and
   Sections 7.1–7.3 are written to be handed to them as input rather than implemented from here.
7. Revisit the schema-change list (Section 9) once 1–5 are shipped and playtested. Directional
   cover is the one I would fight for.
