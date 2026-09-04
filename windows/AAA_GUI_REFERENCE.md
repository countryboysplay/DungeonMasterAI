# Dungeon Master AI — AAA GUI Visual Contract

> **r63 status: kept deliberately. The WPF application this described was deleted; the visual
> language it specifies was not.** `docs/unity-target-architecture.md` §9.10 puts it as "drop the
> code, keep the language". Read every reference below to WPF, XAML, controls or pixel sizes as art
> direction for the Unity rebuild rather than as a description of code that exists. The 1536×864
> reference renders and the approved artwork now live in `windows/content/ReferenceArt/`.

This document was authoritative for the r50+ native Windows UI redesign.

Jonathan approved four 1536×864 concept renders as the target product appearance. They are not loose inspiration. The native WPF application should reproduce their composition, proportions, visual hierarchy, color language, panel treatment, and interaction emphasis as closely as practical while remaining a real data-driven desktop application.

## Reference canvas

- Target reference size: **1536 × 864**
- Aspect ratio: **16:9**
- Top application chrome: approximately **62 px**
- Bottom status rail: approximately **26 px**
- Expanded left navigation rail: approximately **198–202 px**
- Main content begins immediately to the right of the navigation rail with compact 10–14 px insets.
- Primary panel gaps: approximately **6–8 px**.
- Corners are restrained, normally **3–5 px**, not large web-dashboard radii.

## Shared visual language

- Native desktop/game-tool presentation, never a generic website/dashboard aesthetic.
- Near-black charcoal base with subtle blue-black variation.
- Warm muted bronze/gold borders and selected states.
- Ivory/cream primary text.
- Muted stone/taupe secondary text.
- Magical blue for navigation/runtime/map affordances.
- Green for healthy/safe/ready states.
- Red for danger, failed saves, unconscious state, and mandatory combat interruptions.
- Purple used sparingly for party/arcane accents.
- Fantasy serif typography for names, headings, key values and narrative copy.
- Modern sans-serif typography for compact metadata and machine/system text.
- Fine one-pixel borders, restrained inner highlights, subtle dark drop shadows.
- Selected navigation uses a dim bronze fill, gold outline, and warm glow.
- No oversized rounded cards, bright white surfaces, generic Material styling, or large empty whitespace.

## Global shell

The approved renders use the same shell on every major screen:

1. Borderless custom Windows title bar.
2. Hamburger / navigation control at far left.
3. Dungeon Master AI mark and title.
4. Local AI status panel with green status indicator.
5. Centered current-campaign crest, label, campaign name, and dropdown indicator.
6. Right-side quick actions: New Session, Add Note, Quick Roll, Ask AI.
7. Custom minimize, maximize/restore, and close controls.
8. Expanded left navigation: Home, Live Play, Combat, Characters, World, Maps, Quests, Rules, Import, Settings.
9. Decorative arcane compass motif at the bottom of the navigation rail.
10. Bottom status line with version, green operational state, and Local Data Only / lock state.

## Home reference

The Home render establishes these proportions:

- Main hero/campaign region occupies the upper center-left.
- AI Runtime Status is a narrow card at upper right.
- Hero contains a campaign crest, very large campaign name, short cinematic summary, compact campaign metadata, and Campaign Settings.
- Second row contains four equal summary cards: Next Session, Active Quest, Current Location, Party Status.
- Save & Recovery occupies the corresponding right rail.
- Lower center contains Recent World Events and Recent Activity.
- Lower right contains a parchment/map presentation panel.
- Information density is intentionally high while remaining calm and readable.

## Live Play reference

Live Play is the heart of the application and must preserve the approved hierarchy:

- Left narrative / player-agency column: approximately **380 px**.
- Center tactical battlefield: consumes the majority of remaining width.
- Right combat tracker / reactions / status column: approximately **255–265 px**.
- Upper scene strip includes location/scene, day/time, session time, pause/end-session controls, and floor/layer control.
- Player Roll Required is a dominant red-framed interruption card, not a toast or small status line.
- The exact pending roll type, actor/target, DC/target, and reason are visible.
- The large Roll d20 action satisfies the authoritative pending game-engine roll.
- Battlefield retains a readable five-foot grid, tokens, health/status markers, path/reach overlays and map tools.
- Bottom action strip contains Attack, Cast Spell, Dodge, Ready, End Turn, Ask AI.
- Combat tracker remains visible while the required player roll is pending.

## Characters reference

- Party roster fixed at the right, approximately **290–305 px**.
- Large character portrait/header across upper left/center.
- Character name and identity dominate header typography.
- HP, AC, Speed, Initiative and Spell Save DC appear as a compact stat strip.
- Six ability scores appear beneath as small framed values.
- Tab strip: Overview, Inventory, Spells, Conditions, Journal, Progression.
- Lower view is a dense three-column workspace for Inventory, Spells, and Active Effects / Death Saves / progression notes.
- Party cards show portrait, name, level/archetype, HP bar, role summary and status icons.

## World / Maps / Quests reference

- Large world map fills the primary center-left surface.
- Right intelligence rail approximately **300–305 px**.
- Player View / DM View toggle is centered over the map.
- Map tool strip sits at upper right of the map.
- Compact legend floats over the left side of the map.
- Selected location displays as a centered floating dark detail card over the map.
- Right rail contains Current Quests, Factions, Rumors and Secrets.
- Bottom of the screen contains World Timeline across the map area and Quest Tracker under the right rail.
- Player-safe and DM-only information must remain authoritative and actually switch with the real state, not merely change labels.

## Fidelity rule

When implementation convenience conflicts with the approved visual composition, prefer the approved composition unless doing so would break accessibility, deterministic gameplay correctness, or essential application functionality.

When a control in the render represents data that does not yet exist, implement the visual slot without inventing game state. Wire it to real state when the corresponding model becomes available.

The deterministic engine remains authoritative. Visual fidelity must never reintroduce silent player dice, AI-owned player decisions, invented HP, or other state bypasses.

## Acceptance for each rebuilt screen

A major screen is not considered visually complete until:

- It compiles under the real Windows WPF compiler.
- It opens without missing-resource or XAML runtime errors.
- Existing underlying commands/state still work.
- Required player decisions and rolls remain first-class and obvious.
- At 1536×864, panel placement and proportions closely match the approved reference.
- At 1280×720, the app remains usable without critical controls disappearing.
- The screen uses the shared shell/theme rather than local one-off styling.
