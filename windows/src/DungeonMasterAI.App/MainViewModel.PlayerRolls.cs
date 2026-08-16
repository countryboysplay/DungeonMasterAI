using DungeonMasterAI.Domain;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task RollD20Async()
    {
        var rolled = _dice.Roll("1d20");
        LastDiceResult = $"d20: {rolled.Total}";

        if (SelectedCampaign?.PendingPlayerRoll?.Required != true)
        {
            StatusMessage = LastDiceResult;
            return;
        }

        var pending = SelectedCampaign.PendingPlayerRoll;
        if (!pending.Formula.Equals("1d20", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"{LastDiceResult}. This roll does not satisfy the pending {pending.Formula} request.";
            return;
        }

        if (pending.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveDeathSaveFromRollAsync(rolled.Total);
            return;
        }

        StatusMessage = $"{LastDiceResult}. The pending roll type '{pending.ResolutionKey}' is not implemented yet; game state was not changed.";
    }

    private async Task RollActiveDeathSaveAsync()
    {
        if (!PlayerDeathSaveRequired)
        {
            StatusMessage = "A player-controlled Death Saving Throw is not required right now.";
            return;
        }

        var rolled = _dice.Roll("1d20");
        LastDiceResult = $"d20: {rolled.Total}";
        await ResolveActiveDeathSaveFromRollAsync(rolled.Total);
    }

    private async Task ResolveActiveDeathSaveFromRollAsync(int d20Roll)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var pending = SelectedCampaign.PendingPlayerRoll;
            if (!PlayerDeathSaveRequired
                || pending is null
                || string.IsNullOrWhiteSpace(pending.EncounterId)
                || string.IsNullOrWhiteSpace(pending.CombatantId))
                throw new InvalidOperationException("A player-controlled Death Saving Throw is not required right now.");

            var character = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The character for the pending Death Saving Throw no longer exists.");
            var result = _engine.ResolveCombatDeathSavingThrow(
                SelectedCampaign,
                pending.EncounterId,
                pending.CombatantId,
                d20Roll,
                _dice);

            LastDiceResult = $"d20: {d20Roll} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"🎲 {result.Summary}"
            });

            if (result.CurrentHp == 0)
            {
                SelectedCampaign.Chat.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = result.Dead
                        ? $"{character.Name} has died."
                        : result.Stable
                            ? $"{character.Name} is stable but remains unconscious at 0 HP."
                            : $"{character.Name} remains unconscious. Their turn can now end."
                });
            }
            else
            {
                SelectedCampaign.Chat.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = $"{character.Name} regains consciousness at {result.CurrentHp} HP and can continue the turn."
                });
            }

            RaiseCharacterProperties();
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
