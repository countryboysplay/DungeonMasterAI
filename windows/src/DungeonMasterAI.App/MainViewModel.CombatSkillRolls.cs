using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task ResolveActiveCombatSkillActionFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;

        try
        {
            var result = _engine.ResolvePendingCombatSkillActionRoll(
                SelectedCampaign,
                pendingRollId,
                rollOne,
                rollTwo);

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
