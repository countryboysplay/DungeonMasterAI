using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

public sealed class AppDataStore
{
    public const int CurrentSchemaVersion = 5;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string DataDirectory { get; }
    public string StatePath => Path.Combine(DataDirectory, "state.json");
    public string PreviousStatePath => Path.Combine(DataDirectory, "state.previous.json");
    public string RecoveryDirectory => Path.Combine(DataDirectory, "Recovery");
    public string? LastRecoveryMessage { get; private set; }

    public AppDataStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DungeonMasterAI");
    }

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DataDirectory);
        LastRecoveryMessage = null;
        if (!File.Exists(StatePath)) return Normalize(Migrate(new AppState()));

        var current = await TryLoadAsync(StatePath, cancellationToken);
        if (current is not null) return Normalize(Migrate(current));

        PreserveUnreadableState(StatePath);
        if (File.Exists(PreviousStatePath))
        {
            var previous = await TryLoadAsync(PreviousStatePath, cancellationToken);
            if (previous is not null)
            {
                LastRecoveryMessage = "The newest state file was unreadable, so the previous safe copy was restored.";
                return Normalize(Migrate(previous));
            }
            PreserveUnreadableState(PreviousStatePath);
        }

        LastRecoveryMessage = "Campaign state could not be read. A clean state was opened and unreadable files were preserved in Recovery.";
        return new AppState();
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(state, nameof(state));
        Directory.CreateDirectory(DataDirectory);
        var temp = StatePath + ".tmp";

        await using (var stream = new FileStream(
            temp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, Normalize(Migrate(state)), _json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            if (File.Exists(StatePath))
            {
                File.Replace(temp, StatePath, PreviousStatePath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, StatePath);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private async Task<AppState?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<AppState>(stream, _json, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PreserveUnreadableState(string source)
    {
        try
        {
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(RecoveryDirectory);
            var stem = Path.GetFileNameWithoutExtension(source);
            var destination = Path.Combine(RecoveryDirectory, $"{stem}-unreadable-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(source, destination, overwrite: false);
        }
        catch
        {
            // Recovery preservation must never prevent the app from opening.
        }
    }

    private static AppState Migrate(AppState state)
    {
        if (state.SchemaVersion <= 0) state.SchemaVersion = 1;

        while (state.SchemaVersion < CurrentSchemaVersion)
        {
            switch (state.SchemaVersion)
            {
                case 1:
                    // v2 introduces structured PendingPlayerRoll state. Existing saves need no
                    // data transformation because the field is nullable and is reconstructed
                    // from authoritative combat state when the campaign is opened.
                    state.SchemaVersion = 2;
                    break;
                case 2:
                    // v3 introduces persisted tactical map state (CampaignState.TacticalMaps and
                    // CampaignState.EncounterMapBindings). New collections deserialize to their
                    // initializer defaults, so no data is recovered here, but giving map state a
                    // versioned home means every later map-geometry change has a migration slot
                    // instead of being applied lazily during an editor interaction. This pass also
                    // collapses the interim "ai_generated" provenance value onto "ai_expanded".
                    foreach (var campaign in state.Campaigns ?? [])
                    {
                        TacticalMapSchema.NormalizeCampaign(campaign);
                    }
                    state.SchemaVersion = 3;
                    break;
                case 3:
                    // v4 retires the settings that predate the bundled runtime and the pinned,
                    // hash-verified GGUF. Changing the defaults in AppSettings does nothing for an
                    // install that already has a saved state file, and the stale values are not
                    // merely suboptimal -- they route around the whole provisioning path:
                    //
                    //   HuggingFaceModel non-empty  -> LlamaRuntimeManager passes -hf instead of -m,
                    //     which ignores the verified local file and makes llama-server auto-pull the
                    //     repository's ~0.9 GB mmproj vision projector that this app never uses.
                    //   ModelPath empty             -> nothing names the file the provisioner writes.
                    //   GpuLayers 99                -> full offload requested from a CPU-only build.
                    //
                    // Only values identical to the superseded defaults are rewritten. Anything a
                    // user actually chose is left exactly as they set it, even where the new default
                    // would suit them better; a migration is not the place to overrule a preference.
                    if (state.Settings is { } settings)
                    {
                        var fresh = new AppSettings();
                        if (string.IsNullOrWhiteSpace(settings.ModelPath)) settings.ModelPath = fresh.ModelPath;
                        if (settings.HuggingFaceModel == "unsloth/Qwen3.5-9B-GGUF:UD-Q4_K_XL") settings.HuggingFaceModel = fresh.HuggingFaceModel;
                        if (settings.ContextSize == 16384) settings.ContextSize = fresh.ContextSize;
                        if (settings.GpuLayers == 99) settings.GpuLayers = fresh.GpuLayers;
                        if (settings.Temperature == 0.75) settings.Temperature = fresh.Temperature;
                    }
                    state.SchemaVersion = 4;
                    break;
                case 4:
                    // v5 introduces progression: experience points, banked level-ups, and the
                    // one-time payout flags described in docs/progression-direction.md.
                    //
                    // Two things this migration must do, and one it must not.
                    //
                    // It must seed experience from the level a character already has. Every
                    // existing save has characters at levels 1..n and no XP at all. Left at 0, an
                    // imported level-5 player character would bank a level-up on their next
                    // 300-XP award, as though they were levelling 1 to 2, and would keep doing so
                    // until their XP caught up with a level they already had. The seed is the
                    // whole substance of the migration.
                    //
                    // It must settle every subject that could otherwise pay out retroactively:
                    // creatures already dead, quests already completed, locations already found.
                    //
                    // It must NOT grant anything. No XP is paid, no level-up is banked, no gold
                    // moves. A migration that paid out would hand a long-running campaign a
                    // windfall on upgrade, and a migration is not the place to overrule the state
                    // of a game already in progress.
                    foreach (var campaign in state.Campaigns ?? [])
                    {
                        foreach (var character in campaign.Characters ?? [])
                        {
                            if (character.ExperiencePoints <= 0)
                                character.ExperiencePoints = Progression.ExperienceThresholdForLevel(character.Level);
                            // PendingLevelUps is deliberately NOT reset here. A v4 file cannot
                            // carry the field, so it already deserializes to 0 -- and Migrate runs
                            // on every save as well as every load, so zeroing it would destroy a
                            // banked level-up on any state that reached this arm with one set.
                            if (character.Dead && !character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
                                character.ExperienceAwarded = true;
                        }
                        foreach (var quest in campaign.Quests ?? [])
                        {
                            if (Progression.IsCompletingQuestStatus(quest.Status)) quest.RewardsGranted = true;
                        }
                        foreach (var location in campaign.Locations ?? [])
                        {
                            if (location.Discovered) location.DiscoveryExperienceAwarded = true;
                        }
                    }
                    state.SchemaVersion = 5;
                    break;
                default:
                    throw new InvalidDataException($"No migration is defined from state schema version {state.SchemaVersion}.");
            }
        }

        return state;
    }

    private static AppState Normalize(AppState state)
    {
        state.Settings ??= new AppSettings();
        state.Campaigns ??= [];
        foreach (var campaign in state.Campaigns)
        {
            campaign.Locations ??= [];
            campaign.Connections ??= [];
            campaign.Characters ??= [];
            campaign.Items ??= [];
            campaign.Merchants ??= [];
            campaign.Quests ??= [];
            campaign.Factions ??= [];
            campaign.Relationships ??= [];
            campaign.Secrets ??= [];
            campaign.Timeline ??= [];
            campaign.Supplements ??= [];
            campaign.Encounters ??= [];
            campaign.Events ??= [];
            campaign.Chat ??= [];

            // Tactical map shape is normalized on every load and before every save. Deserialization
            // discards the case-insensitive comparer declared on EncounterMapBindings, so this also
            // restores that lookup contract rather than leaving it silently case-sensitive.
            TacticalMapSchema.NormalizeCampaign(campaign);

            foreach (var character in campaign.Characters)
            {
                // Rebuilt rather than null-coalesced: a deserialized dictionary is non-null but
                // case-sensitive, so ??= leaves Abilities["strength"] unable to find "Strength".
                character.Abilities = CaseInsensitiveMap.Normalize(character.Abilities);
                character.SavingThrowProficiencies ??= [];
                character.SkillProficiencies ??= [];
                character.Conditions ??= [];
                character.DamageResistances ??= [];
                character.DamageVulnerabilities ??= [];
                character.DamageImmunities ??= [];
                character.SpellSlots ??= [];
                character.Resources ??= [];
                character.Attacks ??= [];
                character.Inventory ??= [];
            }

            if (campaign.PendingPlayerRoll is not null)
                campaign.PendingPlayerRoll.Context = CaseInsensitiveMap.Normalize(campaign.PendingPlayerRoll.Context);

            foreach (var merchant in campaign.Merchants) merchant.Stock ??= [];
            foreach (var encounter in campaign.Encounters)
            {
                encounter.Combatants ??= [];
                encounter.BattlefieldEffects ??= [];
                foreach (var effect in encounter.BattlefieldEffects)
                    effect.LastTriggeredTurnByCharacter =
                        CaseInsensitiveMap.Normalize(effect.LastTriggeredTurnByCharacter);
            }
            foreach (var quest in campaign.Quests) quest.Objectives ??= [];
            foreach (var secret in campaign.Secrets)
            {
                secret.KnownByKeys ??= [];
                secret.RevealConditions ??= [];
            }
        }
        return state;
    }
}
