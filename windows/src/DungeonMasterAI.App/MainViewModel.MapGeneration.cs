using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using DungeonMasterAI.AI;
using DungeonMasterAI.App.Controls;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private readonly TacticalMapAiGeneratorService _mapGenerator = new();
    private string _mapDescription = "Create an abandoned six-room crypt with a flooded burial chamber, a collapsed side passage, one secret door, useful cover, and a defensible final chamber.";
    private string _mapTypeInput = "dungeon";
    private string _mapThemeInput = "ancient crypt";
    private string _mapWidthInput = "30";
    private string _mapHeightInput = "20";
    private string _mapFeetPerSquareInput = "5";
    private string _mapSeedInput = "784211";
    private bool _mapFogEnabled = true;
    private bool _isMapGenerationBusy;
    private string _mapGenerationStatus = "Describe a tactical location, then generate a reviewable candidate with the local AI.";
    private string _mapGenerationWarnings = "No candidate generated yet.";
    private string _mapAssetStatus = "Core fantasy assets have not been checked yet.";
    private string? _generatedMapCampaignId;
    private TacticalMap? _generatedMapCandidate;
    private TacticalMap? _selectedCampaignMap;
    private TacticalMap? _mapPreview;
    private ICommand? _generateMapCommand;
    private ICommand? _acceptGeneratedMapCommand;
    private ICommand? _rejectGeneratedMapCommand;
    private ICommand? _randomizeMapSeedCommand;

    public string MapDescription { get => _mapDescription; set { _mapDescription = value; OnPropertyChanged(); } }
    public string MapTypeInput { get => _mapTypeInput; set { _mapTypeInput = value; OnPropertyChanged(); } }
    public string MapThemeInput { get => _mapThemeInput; set { _mapThemeInput = value; OnPropertyChanged(); } }
    public string MapWidthInput { get => _mapWidthInput; set { _mapWidthInput = value; OnPropertyChanged(); } }
    public string MapHeightInput { get => _mapHeightInput; set { _mapHeightInput = value; OnPropertyChanged(); } }
    public string MapFeetPerSquareInput { get => _mapFeetPerSquareInput; set { _mapFeetPerSquareInput = value; OnPropertyChanged(); } }
    public string MapSeedInput { get => _mapSeedInput; set { _mapSeedInput = value; OnPropertyChanged(); } }
    public bool MapFogEnabled { get => _mapFogEnabled; set { _mapFogEnabled = value; OnPropertyChanged(); } }
    public bool IsMapGenerationBusy { get => _isMapGenerationBusy; private set { _isMapGenerationBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(MapGeneratorButtonText)); } }
    public string MapGenerationStatus { get => _mapGenerationStatus; private set { _mapGenerationStatus = value; OnPropertyChanged(); } }
    public string MapGenerationWarnings { get => _mapGenerationWarnings; private set { _mapGenerationWarnings = value; OnPropertyChanged(); } }
    public string MapAssetStatus { get => _mapAssetStatus; private set { _mapAssetStatus = value; OnPropertyChanged(); } }
    public string MapAssetSetId => CoreFantasyMapAssetPackProvisioner.PackId;
    public string MapGeneratorButtonText => IsMapGenerationBusy ? "Generating Map…" : "Generate Map with Local AI";
    public bool HasGeneratedMapCandidate => GeneratedMapCandidate is not null;

    public TacticalMap? GeneratedMapCandidate
    {
        get => _generatedMapCandidate;
        private set
        {
            _generatedMapCandidate = value;
            if (value is null) _generatedMapCampaignId = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGeneratedMapCandidate));
            RefreshMapPreview();
        }
    }

    public TacticalMap? SelectedCampaignMap
    {
        get => _selectedCampaignMap;
        set
        {
            _selectedCampaignMap = value;
            OnPropertyChanged();
            if (GeneratedMapCandidate is null) RefreshMapPreview();
        }
    }

    public TacticalMap? MapPreview
    {
        get => _mapPreview;
        private set { _mapPreview = value; OnPropertyChanged(); }
    }

    public string MapPreviewSummary
    {
        get
        {
            var map = GeneratedMapCandidate ?? SelectedCampaignMap;
            if (map is null) return "No tactical map selected.";
            return $"{map.Name}  •  {map.WidthSquares}×{map.HeightSquares}  •  {map.FeetPerSquare} ft/grid  •  {map.Rooms.Count} regions  •  {map.Doors.Count} doors  •  seed {map.Seed}";
        }
    }

    public ICommand GenerateMapCommand => _generateMapCommand ??= new AsyncRelayCommand(GenerateMapAsync);
    public ICommand AcceptGeneratedMapCommand => _acceptGeneratedMapCommand ??= new AsyncRelayCommand(AcceptGeneratedMapAsync);
    public ICommand RejectGeneratedMapCommand => _rejectGeneratedMapCommand ??= new RelayCommand(RejectGeneratedMap);
    public ICommand RandomizeMapSeedCommand => _randomizeMapSeedCommand ??= new RelayCommand(RandomizeMapSeed);

    public void InitializeMapWorkspace()
    {
        var provision = CoreFantasyMapAssetPackProvisioner.EnsureInstalled();
        MapAssetStatus = provision.Success
            ? $"{provision.Message} {provision.AssetFileCount} raster files available."
            : provision.Message + " Procedural fallbacks remain available.";

        if (SelectedCampaign is not null && (SelectedCampaignMap is null || !SelectedCampaign.TacticalMaps.Contains(SelectedCampaignMap)))
            SelectedCampaignMap = SelectedCampaign.TacticalMaps.FirstOrDefault();
        else
            RefreshMapPreview();
    }

    private async Task GenerateMapAsync()
    {
        if (IsMapGenerationBusy) return;
        if (SelectedCampaign is null)
        {
            MapGenerationStatus = "Select or create a campaign before generating a tactical map.";
            return;
        }
        if (string.IsNullOrWhiteSpace(MapDescription))
        {
            MapGenerationStatus = "Enter a map description first.";
            return;
        }
        if (!TryParseMapNumber(MapWidthInput, 4, 200, "width", out var width)
            || !TryParseMapNumber(MapHeightInput, 4, 200, "height", out var height)
            || !TryParseMapNumber(MapFeetPerSquareInput, 1, 30, "feet per square", out var scale))
            return;

        var seed = 0;
        if (!string.IsNullOrWhiteSpace(MapSeedInput) && (!int.TryParse(MapSeedInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed) || seed < 0))
        {
            MapGenerationStatus = "Seed must be zero or a positive whole number.";
            return;
        }

        var targetCampaignId = SelectedCampaign.Id;
        IsMapGenerationBusy = true;
        MapGenerationStatus = "Starting the local model and preparing the map contract…";
        MapGenerationWarnings = "Validation has not completed yet.";
        try
        {
            InitializeMapWorkspace();
            if (!await EnsureLocalAiReadyAsync(TimeSpan.FromMinutes(45)))
            {
                MapGenerationStatus = "Local AI is not ready. Set up or start Local AI, then generate again.";
                return;
            }

            MapGenerationStatus = "Local AI is designing structured rooms, walls, doors, terrain, props, lights, and spawn points…";
            var request = new TacticalMapGenerationRequest
            {
                Description = MapDescription.Trim(),
                MapType = string.IsNullOrWhiteSpace(MapTypeInput) ? "dungeon" : MapTypeInput.Trim(),
                Theme = string.IsNullOrWhiteSpace(MapThemeInput) ? "ancient crypt" : MapThemeInput.Trim(),
                AssetSetId = CoreFantasyMapAssetPackProvisioner.PackId,
                WidthSquares = width,
                HeightSquares = height,
                FeetPerSquare = scale,
                Seed = seed,
                FogOfWarEnabled = MapFogEnabled,
                AllowedAssetKeys = TacticalMapAiGeneratorService.DefaultAssetKeys.ToList()
            };

            var result = await _mapGenerator.GenerateAsync(request, Settings);
            _generatedMapCampaignId = targetCampaignId;
            GeneratedMapCandidate = result.Map;
            MapSeedInput = result.Map.Seed.ToString(CultureInfo.InvariantCulture);
            MapGenerationWarnings = result.Warnings.Count == 0
                ? "Deterministic validation passed with no warnings."
                : string.Join(Environment.NewLine, result.Warnings.Select(warning => "• " + warning));
            MapGenerationStatus = result.Attempts == 1
                ? "Candidate ready. Review the rendered map before adding it to the campaign."
                : "Candidate ready after one automatic repair pass. Review it before adding it to the campaign.";
            StatusMessage = MapGenerationStatus;
        }
        catch (Exception ex)
        {
            GeneratedMapCandidate = null;
            MapGenerationStatus = $"Map generation failed without changing the campaign: {ex.Message}";
            MapGenerationWarnings = "No map was accepted or saved.";
            StatusMessage = MapGenerationStatus;
        }
        finally
        {
            IsMapGenerationBusy = false;
        }
    }

    private async Task AcceptGeneratedMapAsync()
    {
        if (SelectedCampaign is null || GeneratedMapCandidate is null)
        {
            MapGenerationStatus = "Generate a valid candidate before adding a map to the campaign.";
            return;
        }
        if (!string.Equals(_generatedMapCampaignId, SelectedCampaign.Id, StringComparison.OrdinalIgnoreCase))
        {
            MapGenerationStatus = "This candidate was generated for a different campaign. Return to that campaign or discard it and generate a new map here.";
            StatusMessage = MapGenerationStatus;
            return;
        }

        var candidate = GeneratedMapCandidate;
        EnsureUniqueMapIdentity(SelectedCampaign, candidate);
        candidate.Visibility.RevealAll = !candidate.FogOfWarEnabled;
        SelectedCampaign.TacticalMaps.Add(candidate);
        SelectedCampaign.Events.Add(new CampaignEvent
        {
            Type = "ai_tactical_map_accepted",
            Summary = $"Accepted AI-generated tactical map '{candidate.Name}' using asset pack {candidate.AssetSetId} and seed {candidate.Seed}.",
            DmOnly = true
        });
        GeneratedMapCandidate = null;
        SelectedCampaignMap = candidate;
        OnPropertyChanged(nameof(SelectedCampaign));
        MapRevision++;
        await SaveAsync();
        MapGenerationStatus = $"{candidate.Name} was added to {SelectedCampaign.Name} and saved.";
        StatusMessage = MapGenerationStatus;
    }

    private void RejectGeneratedMap()
    {
        if (GeneratedMapCandidate is null)
        {
            MapGenerationStatus = "There is no generated candidate to discard.";
            return;
        }
        var name = GeneratedMapCandidate.Name;
        GeneratedMapCandidate = null;
        MapGenerationWarnings = "Candidate discarded. Saved campaign maps were not changed.";
        MapGenerationStatus = $"Discarded {name}. Adjust the description or seed and generate another candidate.";
        StatusMessage = MapGenerationStatus;
    }

    private void RandomizeMapSeed()
    {
        MapSeedInput = Random.Shared.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        MapGenerationStatus = "New deterministic seed selected. Generate again to create a different layout.";
    }

    private bool TryParseMapNumber(string text, int minimum, int maximum, string label, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum) return true;
        MapGenerationStatus = $"Map {label} must be a whole number from {minimum} to {maximum}.";
        return false;
    }

    private void EnsureUniqueMapIdentity(CampaignState campaign, TacticalMap map)
    {
        if (campaign.TacticalMaps.Any(existing => existing.Id.Equals(map.Id, StringComparison.OrdinalIgnoreCase)))
            map.Id = Guid.NewGuid().ToString("N");
        var baseKey = string.IsNullOrWhiteSpace(map.Key) ? "generated-map" : map.Key;
        var key = baseKey;
        var suffix = 2;
        while (campaign.TacticalMaps.Any(existing => existing.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            key = $"{baseKey}-{suffix++}";
        map.Key = key;
    }

    private void RefreshMapPreview()
    {
        var source = GeneratedMapCandidate ?? SelectedCampaignMap;
        MapPreview = source is null ? null : CloneForMapPreview(source);
        OnPropertyChanged(nameof(MapPreviewSummary));
    }

    private static TacticalMap CloneForMapPreview(TacticalMap source)
    {
        var json = JsonSerializer.Serialize(source);
        var clone = JsonSerializer.Deserialize<TacticalMap>(json) ?? throw new InvalidDataException("Could not create a tactical-map preview clone.");
        clone.Visibility ??= new TacticalMapVisibility();
        clone.Visibility.RevealAll = true;
        return clone;
    }
}
