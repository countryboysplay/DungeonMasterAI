# Tactical Map Schema

**Current record version: 2** (`TacticalMapSchema.CurrentMapSchemaVersion`). Version 1 records still load; see *Record versions* below.

DungeonMasterAI tactical maps are **structured game data first** and artwork second. The same map definition drives rendering, movement, line of sight, doors, difficult terrain, fog, encounter placement, and future Campaign Builder editing.

## Design contract

1. Llama or a human author produces a `TacticalMap` object.
2. `TacticalMapGeometry.Validate` must accept the map before it becomes playable.
3. Gameplay rules query the structured geometry, never pixels from a rendered image.
4. The renderer resolves stable asset keys such as `floor.stone.crypt_flagstone` or `prop.pillar.stone_round`.
5. Missing visual assets must fall back gracefully without changing gameplay geometry.
6. Replacing an asset pack must not alter map coordinates, walls, doors, encounters, or save-game state.
7. Map generation is seedable so decorative variation can be reproduced.

## Record versions

| Version | Introduced | Change |
|---|---|---|
| 1 | r53 | Original tactical map schema. |
| 2 | r62 | Spawn point `side` is stored using the canonical combat-side vocabulary. |

`TacticalMapSchema.NormalizeMap` is the one place that raises a deserialized record to the current
version, so migrations stay covered by migration tests rather than happening lazily inside an
editor interaction. A record reporting a version newer than this build supports is refused rather
than reinterpreted.

## Combat sides

`Domain.CombatSide` is the single source of truth for which side anything fights on. It is shared by
live combatants (`CombatantState.Side`) and by authored spawn points (`TacticalMapSpawnPoint.Side`),
because a spawn point exists to place a combatant.

Canonical values — the only ones written to disk:

- `party` — the player characters and anyone fighting alongside them.
- `opposition` — creatures fighting against the party.
- `neutral` — creatures on no side; excluded from side-based hostility checks.

Synonyms are **accepted on input and normalized away**, never persisted: `player`, `players`, `pc`,
`pcs`, `ally`, `allies`, `friendly`, `hero`, `heroes` resolve to `party`; `enemy`, `enemies`,
`hostile`, `foe`, `foes`, `monster`, `monsters` resolve to `opposition`; `bystander` resolves to
`neutral`. `ally` collapses onto `party` because the engine has no fourth side — an allied NPC is
mechanically on the party's side for initiative, targeting, and stealth.

A side that resolves onto nothing is **not** guessed at. It is stored verbatim and reported as a
campaign readiness error, so unknown data never silently becomes a creature that fights.

Before r62 the generator, the engine, and the readiness validator each carried a private and
mutually incompatible list, and every generated map failed readiness on the value the generator was
required to emit. New side comparisons must route through `CombatSide` rather than adding another
inline string literal.

## Coordinate convention

- Cell coordinates are zero based: `(0,0)` is the upper-left grid cell.
- `WidthSquares` and `HeightSquares` define the playable grid extent.
- Rooms, terrain, props, zones, spawn points, and combatants use cell coordinates.
- Walls use grid-line coordinates, including the outer map boundary.
- A vertical door at `(X,Y)` occupies the edge from `(X,Y)` to `(X,Y+1)`.
- A horizontal door at `(X,Y)` occupies the edge from `(X,Y)` to `(X+1,Y)`.
- `FeetPerSquare` is authoritative for movement and range conversion.

## Layers

### Structure

- rooms and corridors
- walls
- doors and secret doors
- map bounds and scale
- elevation metadata

### Gameplay

- difficult or blocking terrain
- cover and line-of-sight blockers
- spawn points
- encounter, trap, loot, quest, and trigger zones
- live combatant positions supplied by encounter state
- fog/discovery state

### Visual

- asset set ID
- room floor and wall asset keys
- terrain asset keys
- prop asset keys
- light asset keys and color/radius
- deterministic visual seed

## Stable asset keys

The current WPF prototype draws procedural fallbacks for asset keys. Future high-resolution PNG/WebP tile packs should register the same keys, for example:

- `floor.stone.flagstone`
- `floor.stone.crypt_flagstone`
- `wall.stone.block`
- `wall.stone.crypt_block`
- `door.wood.ironbound`
- `door.stone.secret`
- `terrain.water.crypt_shallow`
- `terrain.rubble.stone`
- `prop.pillar.stone_round`
- `prop.altar.stone_crypt`
- `prop.sarcophagus.stone`
- `light.torch.wall`

An asset pack can improve presentation without changing the authored map JSON.

## Llama generation contract

Future map-generation prompts should instruct the local model to return schema-conformant JSON only. Llama should choose rooms, dimensions, topology, walls, doors, terrain, props, lights, spawn points, and zones. It should **not** generate rendered pixels or decide gameplay rules outside the schema.

Generation flow:

`description -> Llama map JSON -> schema validation -> deterministic geometry checks -> renderer -> user review -> campaign`

The Campaign Builder should reject or repair invalid generated maps before they become campaign canon.

The prompt asks for canonical `side` values, but a small local model reaches for `player` and
`enemy` by habit, so generated spawn sides are normalized through `CombatSide` once, before the
acceptance gate runs. Gate and readiness therefore test the same value. A map must contain at least
one `party` spawn point on a walkable square, because combat places creatures on them.

## Prototype acceptance

The r53 prototype verifies:

- schema validation
- campaign JSON round trip
- closed/open door movement behavior
- blocking props
- difficult terrain movement cost
- line of sight
- live encounter token overlay
- deterministic 1280x720 PNG rendering

The first reference fixture is **The Ruined Crypt of Saint Veyra**.

## r62 acceptance

Design-contract item 3 — *gameplay rules query the structured geometry* — became true in r62. The
engine verifies:

- spawn placement uses authored spawn points for the combatant's side;
- movement rejects unwalkable squares and edges blocked by walls or closed doors, and re-permits a
  move once the door is opened;
- map difficult terrain costs the extra 5 feet;
- an encounter with no bound map, or a dangling binding, behaves exactly as it did before;
- the combat grid renders the map beneath the combatant layer.
