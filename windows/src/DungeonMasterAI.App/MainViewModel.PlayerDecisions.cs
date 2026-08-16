using System.Windows.Input;
using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private ICommand? _resolvePrimaryPlayerDecisionCommand;
    private ICommand? _resolveSecondaryPlayerDecisionCommand;

    public PendingPlayerDecision? PendingPlayerDecision => SelectedCampaign?.PendingPlayerDecision;
    public bool PlayerDecisionRequired => PendingPlayerDecision?.Required == true;
    public string PendingPlayerDecisionPrompt => PendingPlayerDecision?.Prompt ?? "No required player decision.";
    public string PendingPlayerDecisionPrimaryLabel => PendingPlayerDecision?.Options
        .FirstOrDefault(o => o.Emphasis.Equals("primary", StringComparison.OrdinalIgnoreCase))?.Label
        ?? PendingPlayerDecision?.Options.FirstOrDefault()?.Label
        ?? "Choose";
    public string PendingPlayerDecisionSecondaryLabel => PendingPlayerDecision?.Options
        .FirstOrDefault(o => o.Emphasis.Equals("secondary", StringComparison.OrdinalIgnoreCase))?.Label
        ?? PendingPlayerDecision?.Options.Skip(1).FirstOrDefault()?.Label
        ?? "Decline";
    public string PendingPlayerDecisionActorName
    {
        get
        {
            var decision = PendingPlayerDecision;
            if (decision is null || SelectedCampaign is null) return "Player";
            return SelectedCampaign.Characters.FirstOrDefault(c =>
                c.Id.Equals(decision.ActorCharacterId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Player";
        }
    }

    public ICommand ResolvePrimaryPlayerDecisionCommand =>
        _resolvePrimaryPlayerDecisionCommand ??= new AsyncRelayCommand(() => ResolvePlayerDecisionAsync(primary: true));

    public ICommand ResolveSecondaryPlayerDecisionCommand =>
        _resolveSecondaryPlayerDecisionCommand ??= new AsyncRelayCommand(() => ResolvePlayerDecisionAsync(primary: false));

    private async Task ResolvePlayerDecisionAsync(bool primary)
    {
        if (SelectedCampaign is null || PendingPlayerDecision is null) return;
        try
        {
            var decision = PendingPlayerDecision;
            var option = primary
                ? decision.Options.FirstOrDefault(o => o.Emphasis.Equals("primary", StringComparison.OrdinalIgnoreCase))
                    ?? decision.Options.FirstOrDefault()
                : decision.Options.FirstOrDefault(o => o.Emphasis.Equals("secondary", StringComparison.OrdinalIgnoreCase))
                    ?? decision.Options.Skip(1).FirstOrDefault();
            if (option is null)
                throw new InvalidOperationException("The pending player decision has no matching option.");

            var result = _engine.ResolvePendingPlayerDecision(SelectedCampaign, decision.Id, option.Id);
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "system",
                Content = $"[Player decision] {result.OptionLabel}"
            });
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }
}
