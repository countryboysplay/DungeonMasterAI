using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task ResolveActiveOpportunityAttackFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingOpportunityAttackRoll(
                SelectedCampaign,
                pendingRollId,
                rollOne,
                rollTwo,
                _dice);
            await CommitOpportunityAttackResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveOpportunityAttackDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingOpportunityAttackDamageRoll(
                SelectedCampaign,
                pendingRollId,
                damageAmount,
                _dice);
            await CommitOpportunityAttackResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task CommitOpportunityAttackResultAsync(string summary)
    {
        if (SelectedCampaign is null) return;
        StatusMessage = summary;
        SelectedCampaign.Chat.Add(new ChatMessage
        {
            Role = "assistant",
            Content = CleanSessionNarration(summary)
        });
        RaiseCharacterProperties();
        RaiseCampaignProperties();
        RefreshCombatSelections(keepSelection: true);
        await SaveAsync();
    }
}
