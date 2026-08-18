using System.IO;
using System.Text.Json;
using System.Windows.Input;
using DungeonMasterAI.AI;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private TacticalMap? _mapEditDraft;
    private string? _mapEditCampaignId;
    private string? _mapEditSourceMapId;
    private bool _mapEditSourceIsCandidate;
    private string _mapEditorStatus = "Select a generated candidate or saved campaign map, then choose Edit Map.";
    private TacticalMapRoom? _selectedEditRoom;
    private TacticalMapDoor? _selectedEditDoor;
    private TacticalMapTerrain? _selectedEditTerrain;
    private TacticalMapProp? _selectedEditProp;
    private ICommand? _beginMapEditCommand;
    private ICommand? _applyMapEditCommand;
    private ICommand? _cancelMapEditCommand;
    private ICommand? _refreshMapEditPreviewCommand;
    private ICommand? _rerollMapVisualsCommand;
    private ICommand? _addMapPropCommand;
    private ICommand? _removeMapPropCommand;
    private ICommand? _addMapTerrainCommand;
    private ICommand? _removeMapTerrainCommand;
    private ICommand? _removeMapDoorCommand;

    public TacticalMap? MapEditDraft
    {
        get => _mapEditDraft;
        private set
        {
            _mapEditDraft = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMapEditDraft));
            OnPropertyChanged(nameof(MapEditRooms));
            OnPropertyChanged(nameof(MapEditDoors));
            OnPropertyChanged(nameof(MapEditTerrain));
            OnPropertyChanged(nameof(MapEditProps));
            OnPropertyChanged(nameof(MapEditSourceLabel));
        }
    }

    public bool HasMapEditDraft => MapEditDraft is not null;
    public string MapEditorStatus { get => _mapEditorStatus; private set { _mapEditorStatus = value; OnPropertyChanged(); } }
    public string MapEditSourceLabel => MapEditDraft is null ? "No edit draft" : _mapEditSourceIsCandidate ? "Generated candidate working copy" : "Saved campaign map working copy";
    public IEnumerable<TacticalMapRoom> MapEditRooms => MapEditDraft?.Rooms ?? [];
    public IEnumerable<TacticalMapDoor> MapEditDoors => MapEditDraft?.Doors ?? [];
    public IEnumerable<TacticalMapTerrain> MapEditTerrain => MapEditDraft?.Terrain ?? [];
    public IEnumerable<TacticalMapProp> MapEditProps => MapEditDraft?.Props ?? [];

    public TacticalMapRoom? SelectedEditRoom { get => _selectedEditRoom; set { _selectedEditRoom = value; OnPropertyChanged(); } }
    public TacticalMapDoor? SelectedEditDoor { get => _selectedEditDoor; set { _selectedEditDoor = value; OnPropertyChanged(); } }
    public TacticalMapTerrain? SelectedEditTerrain { get => _selectedEditTerrain; set { _selectedEditTerrain = value; OnPropertyChanged(); } }
    public TacticalMapProp? SelectedEditProp { get => _selectedEditProp; set { _selectedEditProp = value; OnPropertyChanged(); } }

    public ICommand BeginMapEditCommand => _beginMapEditCommand ??= new RelayCommand(BeginMapEdit);
    public ICommand ApplyMapEditCommand => _applyMapEditCommand ??= new AsyncRelayCommand(ApplyMapEditAsync);
    public ICommand CancelMapEditCommand => _cancelMapEditCommand ??= new RelayCommand(CancelMapEdit);
    public ICommand RefreshMapEditPreviewCommand => _refreshMapEditPreviewCommand ??= new RelayCommand(RefreshMapEditorPreview);
    public ICommand RerollMapVisualsCommand => _rerollMapVisualsCommand ??= new RelayCommand(RerollMapVisuals);
    public ICommand AddMapPropCommand => _addMapPropCommand ??= new RelayCommand(AddMapProp);
    public ICommand RemoveMapPropCommand => _removeMapPropCommand ??= new RelayCommand(RemoveSelectedMapProp);
    public ICommand AddMapTerrainCommand => _addMapTerrainCommand ??= new RelayCommand(AddMapTerrain);
    public ICommand RemoveMapTerrainCommand => _removeMapTerrainCommand ??= new RelayCommand(RemoveSelectedMapTerrain);
    public ICommand RemoveMapDoorCommand => _removeMapDoorCommand ??= new RelayCommand(RemoveSelectedMapDoor);

    private void BeginMapEdit()
    {
        if (SelectedCampaign is null)
        {
            MapEditorStatus = "Select a campaign before editing maps.";
            return;
        }

        TacticalMap? source;
        if (GeneratedMapCandidate is not null)
        {
            if (!string.Equals(_generatedMapCampaignId, SelectedCampaign.Id, StringComparison.OrdinalIgnoreCase))
            {
                MapEditorStatus = "The current generated candidate belongs to a different campaign.";
                return;
            }
            source = GeneratedMapCandidate;
            _mapEditSourceIsCandidate = true;
            _mapEditSourceMapId = source.Id;
        }
        else
        {
            source = SelectedCampaignMap;
            _mapEditSourceIsCandidate = false;
            _mapEditSourceMapId = source?.Id;
        }

        if (source is null)
        {
            MapEditorStatus = "Generate a candidate or select a saved tactical map first.";
            return;
        }

        _mapEditCampaignId = SelectedCampaign.Id;
        MapEditDraft = CloneTacticalMap(source);
        SelectedEditRoom = MapEditDraft.Rooms.FirstOrDefault();
        SelectedEditDoor = MapEditDraft.Doors.FirstOrDefault();
        SelectedEditTerrain = MapEditDraft.Terrain.FirstOrDefault();
        SelectedEditProp = MapEditDraft.Props.FirstOrDefault();
        MapEditorStatus = $"Editing {source.Name} on an isolated working copy. Campaign state has not changed.";
        RefreshMapEditorPreview();
    }

    public void RefreshMapEditorPreview()
    {
        if (MapEditDraft is null)
        {
            RefreshMapPreview();
            return;
        }

        MapPreview = CloneForMapPreview(MapEditDraft);
        MapRevision++;
        MapEditorStatus = "Working-copy preview refreshed. Use Apply Edits to persist only after validation passes.";
    }

    private async Task ApplyMapEditAsync()
    {
        if (SelectedCampaign is null || MapEditDraft is null || string.IsNullOrWhiteSpace(_mapEditCampaignId))
        {
            MapEditorStatus = "There is no active map edit draft to apply.";
            return;
        }
        if (!string.Equals(SelectedCampaign.Id, _mapEditCampaignId, StringComparison.OrdinalIgnoreCase))
        {
            MapEditorStatus = "This working copy belongs to a different campaign. Return to that campaign or cancel the edit.";
            return;
        }

        var validation = TacticalMapGeometry.Validate(MapEditDraft);
        var errors = validation.Issues.Where(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (errors.Length > 0)
        {
            MapEditorStatus = "Edits were not applied: " + string.Join(" ", errors.Take(3).Select(issue => $"{issue.Path}: {issue.Message}"));
            return;
        }

        var accepted = CloneTacticalMap(MapEditDraft);
        accepted.Visibility ??= new TacticalMapVisibility();

        if (_mapEditSourceIsCandidate)
        {
            if (GeneratedMapCandidate is null || !string.Equals(GeneratedMapCandidate.Id, _mapEditSourceMapId, StringComparison.OrdinalIgnoreCase))
            {
                MapEditorStatus = "The generated candidate changed while this working copy was open. Cancel and begin editing the current candidate again.";
                return;
            }
            GeneratedMapCandidate = accepted;
            _generatedMapCampaignId = SelectedCampaign.Id;
            ClearMapEditDraft(false);
            MapGenerationStatus = "Candidate edits applied. Review the result, then choose Add to Campaign when ready.";
            MapEditorStatus = "Candidate working copy applied. Saved campaign state is still unchanged.";
            StatusMessage = MapEditorStatus;
            return;
        }

        var index = SelectedCampaign.TacticalMaps.FindIndex(map => map.Id.Equals(_mapEditSourceMapId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            MapEditorStatus = "The saved source map no longer exists. Edits were not applied.";
            return;
        }

        SelectedCampaign.TacticalMaps[index] = accepted;
        SelectedCampaign.Events.Add(new CampaignEvent
        {
            Type = "tactical_map_edited",
            Summary = $"Applied validated map edits to '{accepted.Name}'. Geometry seed {accepted.GenerationSeed}, visual seed {accepted.Seed}.",
            DmOnly = true
        });
        SelectedCampaignMap = accepted;
        ClearMapEditDraft(false);
        OnPropertyChanged(nameof(SelectedCampaign));
        MapRevision++;
        await SaveAsync();
        MapEditorStatus = $"Validated edits to {accepted.Name} were saved to the campaign.";
        StatusMessage = MapEditorStatus;
    }

    private void CancelMapEdit()
    {
        if (MapEditDraft is null)
        {
            MapEditorStatus = "There is no edit draft to cancel.";
            return;
        }
        ClearMapEditDraft(true);
        MapEditorStatus = "Working-copy edits discarded. Campaign state was not changed.";
        StatusMessage = MapEditorStatus;
    }

    private void RerollMapVisuals()
    {
        if (MapEditDraft is null)
        {
            BeginMapEdit();
            if (MapEditDraft is null) return;
        }

        // Bring a legacy draft up to the current map schema before rerolling art, so the geometry
        // seed is snapshotted by the same normalizer the persistence migration uses instead of by a
        // rule that only ever ran inside this one interaction.
        TacticalMapSchema.NormalizeMap(MapEditDraft);

        var previous = MapEditDraft.Seed;
        var next = previous;
        while (next == previous) next = Random.Shared.Next(1, int.MaxValue);
        MapEditDraft.Seed = next;
        MapEditorStatus = $"Visual variants rerolled from seed {previous} to {next}. Geometry and gameplay objects are unchanged.";
        RefreshMapEditorPreview();
    }

    private void AddMapProp()
    {
        if (MapEditDraft is null) { MapEditorStatus = "Begin editing a map before adding props."; return; }
        var prop = new TacticalMapProp
        {
            Name = "Stone Pillar",
            AssetKey = "prop.pillar.stone_round",
            X = Math.Clamp(MapEditDraft.WidthSquares / 2, 0, Math.Max(0, MapEditDraft.WidthSquares - 1)),
            Y = Math.Clamp(MapEditDraft.HeightSquares / 2, 0, Math.Max(0, MapEditDraft.HeightSquares - 1)),
            WidthSquares = 1,
            HeightSquares = 1,
            BlocksMovement = true,
            BlocksLineOfSight = false,
            Cover = "half"
        };
        MapEditDraft.Props.Add(prop);
        SelectedEditProp = prop;
        OnPropertyChanged(nameof(MapEditProps));
        MapEditorStatus = "Added a pillar to the working copy. Adjust its coordinates and refresh the preview.";
        RefreshMapEditorPreview();
    }

    private void RemoveSelectedMapProp()
    {
        if (MapEditDraft is null || SelectedEditProp is null) { MapEditorStatus = "Select a prop to remove."; return; }
        MapEditDraft.Props.Remove(SelectedEditProp);
        SelectedEditProp = MapEditDraft.Props.FirstOrDefault();
        OnPropertyChanged(nameof(MapEditProps));
        RefreshMapEditorPreview();
    }

    private void AddMapTerrain()
    {
        if (MapEditDraft is null) { MapEditorStatus = "Begin editing a map before adding terrain."; return; }
        var terrain = new TacticalMapTerrain
        {
            Name = "Rubble",
            TerrainType = "rubble",
            AssetKey = "terrain.rubble.stone",
            X = Math.Clamp(MapEditDraft.WidthSquares / 2, 0, Math.Max(0, MapEditDraft.WidthSquares - 1)),
            Y = Math.Clamp(MapEditDraft.HeightSquares / 2, 0, Math.Max(0, MapEditDraft.HeightSquares - 1)),
            WidthSquares = 2,
            HeightSquares = 2,
            DifficultTerrain = true,
            Cover = "half"
        };
        MapEditDraft.Terrain.Add(terrain);
        SelectedEditTerrain = terrain;
        OnPropertyChanged(nameof(MapEditTerrain));
        MapEditorStatus = "Added rubble terrain to the working copy. Adjust its bounds and refresh the preview.";
        RefreshMapEditorPreview();
    }

    private void RemoveSelectedMapTerrain()
    {
        if (MapEditDraft is null || SelectedEditTerrain is null) { MapEditorStatus = "Select a terrain region to remove."; return; }
        MapEditDraft.Terrain.Remove(SelectedEditTerrain);
        SelectedEditTerrain = MapEditDraft.Terrain.FirstOrDefault();
        OnPropertyChanged(nameof(MapEditTerrain));
        RefreshMapEditorPreview();
    }

    private void RemoveSelectedMapDoor()
    {
        if (MapEditDraft is null || SelectedEditDoor is null) { MapEditorStatus = "Select a door to remove."; return; }
        MapEditDraft.Doors.Remove(SelectedEditDoor);
        SelectedEditDoor = MapEditDraft.Doors.FirstOrDefault();
        OnPropertyChanged(nameof(MapEditDoors));
        RefreshMapEditorPreview();
    }

    private void ClearMapEditDraft(bool restoreNormalPreview)
    {
        MapEditDraft = null;
        _mapEditCampaignId = null;
        _mapEditSourceMapId = null;
        _mapEditSourceIsCandidate = false;
        SelectedEditRoom = null;
        SelectedEditDoor = null;
        SelectedEditTerrain = null;
        SelectedEditProp = null;
        if (restoreNormalPreview) RefreshMapPreview();
    }

    private static TacticalMap CloneTacticalMap(TacticalMap source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<TacticalMap>(json) ?? throw new InvalidDataException("Could not clone tactical map for editing.");
    }
}
