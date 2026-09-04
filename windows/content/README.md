# `windows/content`

Game content that used to sit inside `windows/src/DungeonMasterAI.App`. It moved here in r63 when
the WPF front end was deleted, because none of it is presentation code — the Unity front end will
need all of it.

| Path | What it is | Who reads it |
|---|---|---|
| `Rules/srd_spells.json` | The 316-entry SRD 5.2.1 spell catalog with the deterministic resolution overrides the engine adjudicates against. | `DungeonMasterAI.Data.SrdSpellCatalogService`; asserted by `tests/DungeonMasterAI.R58SpellCoverageTests` and `tools/validate_source.py`. |
| `MapPacks/core.fantasy.crypt/manifest.json` | The built-in tactical map asset pack manifest. All fifteen keys declare `allowProceduralFallback: true` and ship no raster art. | Nothing, currently. The loader that read it (`TacticalMapAssetCatalog`) was WPF and is gone; Unity's `ScriptedImporter` replaces it — see `docs/unity-target-architecture.md` §5.3. |
| `ReferenceArt/*.jpg` | The three approved reference images (Greenhaven hero, parchment ground, Aeliana portrait). | Nothing, currently. They were `<Resource>` entries in the WPF app; they are kept as art direction for the Unity rebuild. |

Two other content files were already outside `App` and did not move:
`reference-python/knowledge/srd_chunks.jsonl` and `reference-python/demo/sample_campaign_manifest.json`.

Nothing here is copied to any build output any more — the projects that did that were the WPF app.
Consumers locate these files by walking up from `AppContext.BaseDirectory` to the repository root.
