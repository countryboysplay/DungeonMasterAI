# DungeonMasterAI Map Asset Packs

Tactical maps store **stable asset keys** and authoritative game geometry. Asset packs provide optional visual files for those keys. Replacing or removing a pack never changes movement, doors, line of sight, fog, encounters, or campaign state because the renderer falls back procedurally when an image cannot be loaded.

## Search locations

The Windows renderer looks for `manifest.json` beneath:

1. `Assets/MapPacks` beside the installed application.
2. `%LOCALAPPDATA%/DungeonMasterAI/MapPacks` for user-installed packs.

Tests and future Campaign Builder tooling may provide an explicit asset-pack root.

## Manifest

Each pack has a `manifest.json` containing:

- `schemaVersion`
- globally stable `packId`
- name and version
- author
- license and optional license/source URLs
- credits
- asset definitions

Every asset definition contains a stable key such as `floor.stone.crypt_flagstone`, a kind, render mode, opacity/scale, a fallback policy, and zero or more weighted image variants.

Example:

```json
{
  "schemaVersion": 1,
  "packId": "example.crypt.hd",
  "name": "Example Crypt HD",
  "version": "1.0.0",
  "author": "Example Artist",
  "license": "CC0-1.0",
  "sourceUrl": "https://example.invalid/asset-pack",
  "assets": [
    {
      "key": "floor.stone.crypt_flagstone",
      "kind": "floor",
      "renderMode": "tile",
      "variants": [
        { "file": "floors/flagstone-01.png", "weight": 3 },
        { "file": "floors/flagstone-02.png", "weight": 2, "rotationDegrees": 90 }
      ]
    }
  ]
}
```

Variant file paths must remain inside the pack directory. Absolute paths and `..` traversal are rejected.

## Deterministic variants

Variant choice is based on:

- map generation seed
- grid coordinate
- stable asset key

The same campaign therefore gets the same floor/prop variation after save/load or on another machine with the same pack installed.

## Supported image loading

The WPF resolver uses Windows/WPF bitmap decoders. PNG, JPEG, BMP, GIF, and TIFF are expected to work on supported Windows installations. Other formats are accepted only if the machine has a compatible decoder; if decoding fails, the procedural fallback is used.

For portable first-party packs, prefer transparent PNG for sprites and high-resolution PNG/JPEG for opaque tiles.

## Render modes

- `tile`: repeatable floor or small surface tile, normally rendered per grid cell.
- `stretch`: terrain overlays or larger rectangular surfaces.
- `sprite`: transparent prop/light artwork.
- `segment`: horizontal source artwork that can be rotated for walls and doors.

## Licensing rule

Third-party visual files should not be committed or distributed unless their license explicitly permits the intended redistribution. The manifest records author, license, source URL, and credits so the installer and future Campaign Builder can expose attribution cleanly.

The built-in `core.fantasy.crypt` manifest currently describes the stable keys used by the reference crypt while intentionally relying on project-original procedural fallbacks. High-resolution first-party or redistributable variants can be added later without modifying tactical-map JSON.

## Future Campaign Builder

The Campaign Builder should:

1. enumerate installed packs and their license metadata;
2. show asset previews grouped by kind/tag;
3. let generated maps request semantic keys instead of filenames;
4. warn when a campaign references a pack that is not installed;
5. optionally remap missing keys to compatible keys from another pack;
6. package only redistributable assets when exporting a campaign.
