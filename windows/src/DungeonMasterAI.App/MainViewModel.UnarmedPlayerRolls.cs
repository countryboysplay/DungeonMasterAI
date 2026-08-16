using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task ResolveActiveUnarmedGrappleSaveFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingUnarmedGrappleSaveRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
            await CommitUnarmedPlayerRollResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveUnarmedShoveSaveFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingUnarmedShoveSaveRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
            await CommitUnarmedPlayerRollResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveEscapeGrappleFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingEscapeGrappleRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo);
            await CommitUnarmedPlayerRollResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task CommitUnarmedPlayerRollResultAsync(string summary)
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
