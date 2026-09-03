using System.Windows.Input;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.App;

/// <summary>
/// Binds authored tactical maps to the encounter being fought and exposes the bound map to the
/// battlefield renderer.
/// <para>
/// Nothing in the shipped application wrote <see cref="CampaignState.EncounterMapBindings"/>
/// before this, so a map could be generated, edited, and saved into the campaign and still had no
/// route into play. The two commands here are that route, and <see cref="ActiveTacticalMap"/> is
/// what the combat grid renders.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private ICommand? _bindMapToEncounterCommand;
    private ICommand? _clearEncounterMapBindingCommand;

    /// <summary>
    /// The tactical map bound to the selected encounter, resolved through the same engine call
    /// combat adjudication uses. Rendering and rules therefore cannot disagree about which map is
    /// in play — the alternative, a separately-tracked "map being shown", is exactly how the
    /// renderer and the engine drifted apart in the first place.
    /// </summary>
    public TacticalMap? ActiveTacticalMap =>
        SelectedCampaign is null ? null : GameEngine.ResolveEncounterMap(SelectedCampaign, SelectedEncounter?.Id);

    public string EncounterMapBindingSummary
    {
        get
        {
            if (SelectedEncounter is null) return "Select an encounter to give it a battlefield.";
            var map = ActiveTacticalMap;
            return map is null
                ? $"'{SelectedEncounter.Name}' has no tactical map. Combat runs on a bare grid."
                : $"'{SelectedEncounter.Name}' is fought on '{map.Name}' ({map.WidthSquares}×{map.HeightSquares}, {map.FeetPerSquare} ft/grid).";
        }
    }

    public ICommand BindMapToEncounterCommand => _bindMapToEncounterCommand ??= new RelayCommand(BindSelectedMapToEncounter);
    public ICommand ClearEncounterMapBindingCommand => _clearEncounterMapBindingCommand ??= new RelayCommand(ClearEncounterMapBinding);

    private void BindSelectedMapToEncounter()
    {
        var campaign = SelectedCampaign;
        var encounter = SelectedEncounter;
        var map = SelectedCampaignMap;
        if (campaign is null || encounter is null || map is null)
        {
            StatusMessage = "Select a campaign, an encounter, and a saved tactical map before binding them.";
            return;
        }

        if (!campaign.TacticalMaps.Any(m => m.Id.Equals(map.Id, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Add the map to the campaign before binding it to an encounter.";
            return;
        }

        // A map the engine would refuse is a map that produces an unplayable fight, so the gate is
        // here rather than at the first blocked move.
        var validation = TacticalMapGeometry.Validate(map);
        if (!validation.IsValid)
        {
            StatusMessage = $"'{map.Name}' has {validation.Errors} geometry error(s) and cannot be used for combat: "
                + string.Join("; ", validation.Issues.Where(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).Take(3).Select(i => $"{i.Path}: {i.Message}"));
            return;
        }

        campaign.EncounterMapBindings = TacticalMapSchema.NormalizeBindings(campaign.EncounterMapBindings);
        campaign.EncounterMapBindings[encounter.Id] = map.Id;
        StatusMessage = $"'{encounter.Name}' will now be fought on '{map.Name}'.";
        RaiseEncounterMapProperties();
        _ = SaveAsync();
    }

    private void ClearEncounterMapBinding()
    {
        var campaign = SelectedCampaign;
        var encounter = SelectedEncounter;
        if (campaign is null || encounter is null)
        {
            StatusMessage = "Select an encounter before clearing its tactical map.";
            return;
        }

        campaign.EncounterMapBindings = TacticalMapSchema.NormalizeBindings(campaign.EncounterMapBindings);
        if (!campaign.EncounterMapBindings.Remove(encounter.Id))
        {
            StatusMessage = $"'{encounter.Name}' has no tactical map bound.";
            return;
        }

        StatusMessage = $"'{encounter.Name}' no longer has a tactical map. Combat falls back to a bare grid.";
        RaiseEncounterMapProperties();
        _ = SaveAsync();
    }

    private void RaiseEncounterMapProperties()
    {
        OnPropertyChanged(nameof(ActiveTacticalMap));
        OnPropertyChanged(nameof(EncounterMapBindingSummary));
        RaiseCampaignProperties();
    }
}
