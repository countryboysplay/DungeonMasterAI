using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task ResolveActiveReadiedAttackFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingReadiedAttackRoll(
                SelectedCampaign,
                pendingRollId,
                rollOne,
                rollTwo,
                _dice);
            await CommitReadiedAttackResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveReadiedAttackDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingReadiedAttackDamageRoll(
                SelectedCampaign,
                pendingRollId,
                damageAmount,
                _dice);
            await CommitReadiedAttackResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task CommitReadiedAttackResultAsync(string summary)
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
