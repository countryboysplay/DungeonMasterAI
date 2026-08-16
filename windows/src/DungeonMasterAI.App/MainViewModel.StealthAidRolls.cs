using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task ResolveActiveHideFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingHideRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo);
            await CommitPlayerRollResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveHiddenSearchFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingHiddenSearchRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo);
            await CommitPlayerRollResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveFirstAidFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingFirstAidRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo);
            await CommitPlayerRollResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task CommitPlayerRollResultAsync(string summary)
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
