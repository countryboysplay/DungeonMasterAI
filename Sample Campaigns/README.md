# Sample Campaigns

Working folder for adventure material used while authoring campaigns for the app.

**Contents are gitignored.** Drop reference PDFs here freely — they stay on your
machine and cannot be committed by accident.

## Why nothing here is committed

This repository publishes a public installer and is kept deliberately clean
against **SRD 5.2.1**. Most adventure material that is useful as reference is not:

- Fan modules built on a commercial setting carry that setting owner's
  trademarks, place names and artwork.
- They routinely cite creatures by *Monster Manual* page number rather than from
  the SRD, so their stat blocks are not usable either.
- A fan author cannot grant permission for a setting they do not own, so their
  own licence terms do not resolve it.

Reading such a module locally to run a game is ordinary use. Redistributing it
inside a shipped installer is not, and that is the line this folder exists to
hold.

## Shipping a campaign with the app

Plot structure and pacing are not protectable — a besieged village, an
escalating undead threat, an investigation that turns into a siege. An original
campaign can follow a familiar shape while using original names and SRD
creatures (skeleton, zombie, ghoul, wight, specter, wraith, and the rest of the
SRD undead), and that campaign ships cleanly.

Campaigns are authored against `CampaignState` — `Locations`, `Connections`,
`Quests`, `Factions`, `Secrets`, `Timeline` and `Encounters` — and
`CampaignAiExpansionService` / `CampaignAiCompilerService` can do much of the
conversion work from an outline.
