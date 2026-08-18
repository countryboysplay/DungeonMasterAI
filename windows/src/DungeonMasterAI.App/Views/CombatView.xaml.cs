using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DungeonMasterAI.App.Views;

public partial class CombatView : UserControl
{
    private MainViewModel? _viewModel;

    public CombatView()
    {
        InitializeComponent();
    }

    private void CombatView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        SyncActiveAttacker();
    }

    private void CombatView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }

    private void AttachViewModel()
    {
        if (ReferenceEquals(_viewModel, DataContext)) return;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTurnCombatant))
            Dispatcher.BeginInvoke(SyncActiveAttacker);
    }

    private void SyncActiveAttacker()
    {
        if (_viewModel?.ActiveTurnCombatant is null) return;
        var activeId = _viewModel.ActiveTurnCombatant.Id;
        var active = _viewModel.Combatants.FirstOrDefault(c => c.CombatantId == activeId);
        if (active is not null)
            _viewModel.SelectedAttacker = active;
        SyncSelectedCharacter();
    }

    private void Initiative_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AttachViewModel();
        SyncSelectedCharacter();
    }

    private void Target_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel?.SelectedCampaign is null || _viewModel.SelectedTarget is null) return;
        _viewModel.SelectedSpellTarget = _viewModel.SelectedCampaign.Characters
            .FirstOrDefault(c => c.Id == _viewModel.SelectedTarget.CharacterId);
    }

    private void SyncSelectedCharacter()
    {
        if (_viewModel?.SelectedCampaign is null || _viewModel.SelectedAttacker is null) return;
        var character = _viewModel.SelectedCampaign.Characters
            .FirstOrDefault(c => c.Id == _viewModel.SelectedAttacker.CharacterId);
        if (character is not null && !ReferenceEquals(_viewModel.SelectedCharacter, character))
            _viewModel.SelectedCharacter = character;
    }

    private void CastSpell_Click(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        if (_viewModel?.SelectedCampaign is null || _viewModel.SelectedCombatPreparedSpell is null) return;

        SyncSelectedCharacter();
        _viewModel.SelectedPreparedSpell = _viewModel.SelectedCombatPreparedSpell;

        if (_viewModel.SelectedTarget is not null)
        {
            _viewModel.SelectedSpellTarget = _viewModel.SelectedCampaign.Characters
                .FirstOrDefault(c => c.Id == _viewModel.SelectedTarget.CharacterId);
        }

        if (_viewModel.CastSelectedSpellCommand.CanExecute(null))
            _viewModel.CastSelectedSpellCommand.Execute(null);
    }
}
