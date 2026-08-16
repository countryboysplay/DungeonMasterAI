using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private static bool IsPlayerCharacter(CharacterSheet character) =>
        character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase);

    private async Task PresentPendingGameTableRollAsync(PendingRollRequest pending)
    {
        StatusMessage = pending.Purpose;
        RaiseCampaignProperties();
        RefreshCombatSelections(keepSelection: true);
        RefreshOpportunityAttackSelection();
        await SaveAsync();
    }

    private async Task PresentCompletedGameTableActionAsync(string summary)
    {
        StatusMessage = summary;
        RaiseCharacterProperties();
        RaiseCampaignProperties();
        RefreshCombatSelections(keepSelection: true);
        RefreshOpportunityAttackSelection();
        await SaveAsync();
    }
}
