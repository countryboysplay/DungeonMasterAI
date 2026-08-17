# Tactical Map Schema v1

DungeonMasterAI tactical maps are **structured game data first** and artwork second. The same map definition drives rendering, movement, line of sight, doors, difficult terrain, fog, encounter placement, and future Campaign Builder editing.

## Design contract

1. Llama or a human author produces a `TacticalMap` object.
2. `TacticalMapGeometry.Validate` must accept the map before it becomes playable.
3. Gameplay rules query the structured geometry, never pixels from a rendered image.
4. The renderer resolves stable asset keys such as `floor.stone.crypt_flagstone` or `prop.pillar.stone_round`.
5. Missing visual assets must fall back gracefully without changing gameplay geometry.
6. Replacing an asset pack must not alter map coordinates, walls, doors, encounters, or save-game state.
7. Map generation is seedable so decorative variation can be reproduced.

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
