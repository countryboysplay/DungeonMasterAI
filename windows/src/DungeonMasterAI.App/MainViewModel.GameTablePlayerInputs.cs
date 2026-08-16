using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private static bool IsPlayerCharacter(CharacterSheet? character)
        => character?.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase) == true;

    private static bool RequiresPlayerStrengthOrDexteritySave(CharacterSheet target)
        => IsPlayerCharacter(target)
           && !(CharacterMechanics.AutomaticallyFailsSavingThrow(target, "strength")
                && CharacterMechanics.AutomaticallyFailsSavingThrow(target, "dexterity"));

    private async Task CommitPendingGameTableInputAsync(PendingRollRequest pending)
    {
        StatusMessage = pending.Purpose;
        RaiseCharacterProperties();
        RaiseCampaignProperties();
        RefreshCombatSelections(keepSelection: true);
        await SaveAsync();
    }

    private async Task CommitCompletedGameTableActionAsync(string summary, string? chatContent = null)
    {
        if (SelectedCampaign is null) return;
        StatusMessage = summary;
        if (!string.IsNullOrWhiteSpace(chatContent))
        {
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = CleanSessionNarration(chatContent)
            });
        }
        RaiseCharacterProperties();
        RaiseCampaignProperties();
        RefreshCombatSelections(keepSelection: true);
        await SaveAsync();
    }
}
