using System.Text.Json;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Data;

public sealed class AppDataStore
{
    public const int CurrentSchemaVersion = 2;
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
        ArgumentNullException.ThrowIfNull(state);
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

            foreach (var character in campaign.Characters)
            {
                character.Abilities ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

            foreach (var merchant in campaign.Merchants) merchant.Stock ??= [];
            foreach (var encounter in campaign.Encounters) encounter.Combatants ??= [];
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
