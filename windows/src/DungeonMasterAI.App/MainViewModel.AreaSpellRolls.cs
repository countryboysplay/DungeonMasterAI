using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task ResolveActiveAreaSpellSavingThrowFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingAreaSpellSavingThrowRoll(
                SelectedCampaign,
                pendingRollId,
                rollOne,
                rollTwo,
                _dice);

            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = CleanSessionNarration(result.Summary)
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

    private async Task ResolveActiveAreaSpellDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingAreaSpellDamageRoll(
                SelectedCampaign,
                pendingRollId,
                damageAmount,
                _dice);

            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = CleanSessionNarration(result.Summary)
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
