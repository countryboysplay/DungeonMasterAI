# First User-Test Alpha Acceptance Criteria

The build is not considered ready for Jonathan to test until all items below are complete.

- Launches on Windows without Python, Docker, Ollama, or a developer environment.
- Persists campaigns and settings locally and recovers safely from an unreadable state file.
- Loads the included Greenhaven sample campaign.
- Imports structured JSON, TXT/Markdown, PDF, and DOCX campaign sources.
- Provides player-safe and DM map views, including hidden locations/connections.
- Deterministically manages character HP, AC, gold, inventory, merchant stock, purchases, dice, locations, quests, campaign time, combat initiative, tactical movement/range, Action/Bonus Action/Reaction economy, Attack/Extra Attack budgeting, Dash, Disengage, Dodge, Opportunity Attacks, Concentration, and supported spellcasting.
- Searches the local SRD rules index and bundled SRD spell metadata catalog without internet access.
- Never silently improvises an official spell effect that has not been implemented in the deterministic engine.
- Starts/stops a bundled llama.cpp runtime with the app.
- Includes a bundled initial GGUF model that can narrate a turn offline.
- The model can change game reality only through allow-listed application tools.
- AI/runtime errors cannot corrupt persistent campaign state.
- Windows smoke tests and build workflow pass.
- Delivered as a user-friendly Windows test package/installer.
