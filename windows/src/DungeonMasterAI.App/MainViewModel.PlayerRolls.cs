using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

namespace DungeonMasterAI.App;

public sealed partial class MainViewModel
{
    private async Task RollD20Async()
    {
        var pending = SelectedCampaign?.PendingPlayerRoll;
        if (pending?.Required != true)
        {
            var rolled = _dice.Roll("1d20");
            LastDiceResult = $"d20: {rolled.Total}";
            StatusMessage = LastDiceResult;
            return;
        }

        if (pending.ResolutionKey.Equals("combat_attack_damage", StringComparison.OrdinalIgnoreCase))
        {
            var baseDamageExpression = pending.Context.TryGetValue("base_damage_expression", out var storedExpression)
                && !string.IsNullOrWhiteSpace(storedExpression)
                ? storedExpression
                : pending.Formula;
            var critical = pending.Context.TryGetValue("critical", out var criticalText)
                && bool.TryParse(criticalText, out var parsedCritical)
                && parsedCritical;
            var damageAmount = _dice.RollDamage(baseDamageExpression, critical);
            LastDiceResult = $"{pending.Formula}: {damageAmount}";
            await ResolveActiveAttackDamageFromRollAsync(pending.Id, damageAmount);
            return;
        }

        if (pending.ResolutionKey.Equals("spell_attack_damage", StringComparison.OrdinalIgnoreCase))
        {
            var damageAmount = RollPendingSpellAttackDamage(pending);
            LastDiceResult = $"{pending.Formula}: {damageAmount}";
            await ResolveActiveSpellAttackDamageFromRollAsync(pending.Id, damageAmount);
            return;
        }

        if (pending.ResolutionKey.Equals("projectile_spell_damage", StringComparison.OrdinalIgnoreCase))
{
    var baseDamageExpression = pending.Context.TryGetValue("base_damage_expression", out var storedExpression)
        && !string.IsNullOrWhiteSpace(storedExpression)
        ? storedExpression
        : pending.Formula;
    var critical = pending.Context.TryGetValue("critical", out var criticalText)
        && bool.TryParse(criticalText, out var parsedCritical)
        && parsedCritical;
    var damageAmount = _dice.RollDamage(baseDamageExpression, critical);
    LastDiceResult = $"{pending.Formula}: {damageAmount}";
    await ResolveActiveProjectileSpellDamageFromRollAsync(pending.Id, damageAmount);
    return;
}

        if (pending.ResolutionKey.Equals("projectile_auto_damage", StringComparison.OrdinalIgnoreCase))
{
    var baseDamageExpression = pending.Context.TryGetValue("base_damage_expression", out var storedExpression)
        && !string.IsNullOrWhiteSpace(storedExpression)
        ? storedExpression
        : pending.Formula;
    var damageAmount = _dice.RollDamage(baseDamageExpression);
    LastDiceResult = $"{pending.Formula}: {damageAmount}";
    await ResolveActiveAutoProjectileSpellDamageFromRollAsync(pending.Id, damageAmount);
    return;
}

        if (pending.ResolutionKey.Equals("spell_save_damage", StringComparison.OrdinalIgnoreCase))
        {
            var damageAmount = RollPendingSaveSpellDamage(pending);
            LastDiceResult = $"{pending.Formula}: {damageAmount}";
            await ResolveActiveSaveSpellDamageFromRollAsync(pending.Id, damageAmount);
            return;
        }

        if (!pending.Formula.Equals("1d20", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"The required roll is {pending.Formula}. That roll type is not wired to this control yet.";
            return;
        }

        var mode = ParsePendingPlayerRollMode(pending.RollMode);
        var rolls = _dice.RollD20(mode);
        LastDiceResult = rolls.RollTwo.HasValue
            ? $"d20: {rolls.RollOne}, {rolls.RollTwo.Value} → {rolls.ChosenRoll} ({mode})"
            : $"d20: {rolls.ChosenRoll}";

        if (pending.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveDeathSaveFromRollAsync(rolls.ChosenRoll);
            return;
        }

        if (pending.ResolutionKey.Equals("combat_attack", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveAttackFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        if (pending.ResolutionKey.Equals("spell_attack", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveSpellAttackFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        if (pending.ResolutionKey.Equals("projectile_spell_attack", StringComparison.OrdinalIgnoreCase))
{
    await ResolveActiveProjectileSpellAttackFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
    return;
}

        if (pending.ResolutionKey.Equals("ability_check", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveAbilityCheckFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        if (pending.ResolutionKey.Equals("spell_saving_throw", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveSpellSavingThrowFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        if (pending.ResolutionKey.Equals("saving_throw", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveSavingThrowFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        if (pending.ResolutionKey.Equals("concentration_check", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveConcentrationCheckFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        if (pending.ResolutionKey.Equals("initiative", StringComparison.OrdinalIgnoreCase))
        {
            await ResolveActiveInitiativeFromRollAsync(pending.Id, rolls.RollOne, rolls.RollTwo);
            return;
        }

        StatusMessage = $"{LastDiceResult}. The pending roll type '{pending.ResolutionKey}' is not implemented yet; game state was not changed.";
    }

    private async Task RollActiveDeathSaveAsync()
    {
        if (!PlayerDeathSaveRequired)
        {
            StatusMessage = "The active player character does not currently need a Death Saving Throw.";
            RaiseCampaignProperties();
            return;
        }
        await RollD20Async();
    }

    private async Task ResolveActiveDeathSaveFromRollAsync(int roll)
    {
        if (SelectedCampaign is null || SelectedCampaign.PendingPlayerRoll is null)
            return;

        var pending = SelectedCampaign.PendingPlayerRoll;
        try
        {
            if (!PlayerDeathSaveRequired)
                throw new InvalidOperationException("The active player character no longer requires a Death Saving Throw.");
            if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
                throw new InvalidOperationException("The pending Death Saving Throw is missing its combat context.");

            var character = SelectedCampaign.Characters.FirstOrDefault(c => c.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The character for the pending Death Saving Throw no longer exists.");
            var result = _engine.ResolveCombatDeathSavingThrow(
                SelectedCampaign,
                pending.EncounterId,
                pending.CombatantId,
                roll,
                _dice);

            LastDiceResult = $"d20: {roll} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage { Role = "assistant", Content = $"🎲 {result.Summary}" });
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = result.CurrentHp == 0
                    ? result.Dead
                        ? $"{character.Name} has died."
                        : result.Stable
                            ? $"{character.Name} is stable but remains unconscious at 0 HP."
                            : $"{character.Name} remains unconscious. Their turn can now end."
                    : $"{character.Name} regains consciousness at {result.CurrentHp} HP and can continue the turn."
            });
            RaiseCharacterProperties();
            RaiseCampaignProperties();
            RefreshCombatSelections(keepSelection: true);
            await SaveAsync();

            if (result.Dead || result.Stable || result.CurrentHp > 0)
                await AdvanceNpcTurnsAfterPlayerRollAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RaiseCampaignProperties();
        }
    }

    private async Task ResolveActiveAttackFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingEncounterAttackRoll(
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

    private async Task ResolveActiveAttackDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingEncounterAttackDamageRoll(
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

    private async Task ResolveActiveSpellAttackFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingSpellAttackRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
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

    private async Task ResolveActiveSpellAttackDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingSpellAttackDamageRoll(SelectedCampaign, pendingRollId, damageAmount, _dice);
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

    private int RollPendingSpellAttackDamage(PendingRollRequest pending)
    {
        var critical = pending.Context.TryGetValue("critical", out var criticalText)
            && bool.TryParse(criticalText, out var parsedCritical)
            && parsedCritical;
        var baseExpression = pending.Context.TryGetValue("base_damage_expression", out var storedBase) ? storedBase : "";
        var extraExpression = pending.Context.TryGetValue("extra_damage_expression", out var storedExtra) ? storedExtra : "";
        var baseRolls = pending.Context.TryGetValue("base_rolls", out var baseRollsText) && int.TryParse(baseRollsText, out var parsedBaseRolls) ? parsedBaseRolls : 0;
        var extraRolls = pending.Context.TryGetValue("extra_rolls", out var extraRollsText) && int.TryParse(extraRollsText, out var parsedExtraRolls) ? parsedExtraRolls : 0;
        var total = 0;
        for (var i = 0; i < baseRolls; i++) total += _dice.RollDamage(baseExpression, critical);
        for (var i = 0; i < extraRolls; i++) total += _dice.RollDamage(extraExpression, critical);
        return total;
    }

    private async Task ResolveActiveProjectileSpellAttackFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
{
    if (SelectedCampaign is null) return;
    try
    {
        var result = _engine.ResolvePendingProjectileSpellAttackRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
        StatusMessage = result.Summary;
        SelectedCampaign.Chat.Add(new ChatMessage { Role = "assistant", Content = CleanSessionNarration(result.Summary) });
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

private async Task ResolveActiveProjectileSpellDamageFromRollAsync(string pendingRollId, int damageAmount)
{
    if (SelectedCampaign is null) return;
    try
    {
        var result = _engine.ResolvePendingProjectileSpellDamageRoll(SelectedCampaign, pendingRollId, damageAmount, _dice);
        StatusMessage = result.Summary;
        SelectedCampaign.Chat.Add(new ChatMessage { Role = "assistant", Content = CleanSessionNarration(result.Summary) });
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

    private async Task ResolveActiveAutoProjectileSpellDamageFromRollAsync(string pendingRollId, int damageAmount)
{
    if (SelectedCampaign is null) return;
    try
    {
        var result = _engine.ResolvePendingAutoProjectileSpellDamageRoll(SelectedCampaign, pendingRollId, damageAmount, _dice);
        StatusMessage = result.Summary;
        SelectedCampaign.Chat.Add(new ChatMessage { Role = "assistant", Content = CleanSessionNarration(result.Summary) });
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

    private async Task ResolveActiveSaveSpellDamageFromRollAsync(string pendingRollId, int damageAmount)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingSpellSaveDamageRoll(SelectedCampaign, pendingRollId, damageAmount, _dice);
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

    private int RollPendingSaveSpellDamage(PendingRollRequest pending)
    {
        var baseExpression = pending.Context.TryGetValue("base_damage_expression", out var storedBase) ? storedBase : "";
        var extraExpression = pending.Context.TryGetValue("extra_damage_expression", out var storedExtra) ? storedExtra : "";
        var baseRolls = pending.Context.TryGetValue("base_rolls", out var baseRollsText) && int.TryParse(baseRollsText, out var parsedBaseRolls) ? parsedBaseRolls : 0;
        var extraRolls = pending.Context.TryGetValue("extra_rolls", out var extraRollsText) && int.TryParse(extraRollsText, out var parsedExtraRolls) ? parsedExtraRolls : 0;
        var total = 0;
        for (var i = 0; i < baseRolls; i++) total += _dice.RollDamage(baseExpression);
        for (var i = 0; i < extraRolls; i++) total += _dice.RollDamage(extraExpression);
        return total;
    }

    private async Task ResolveActiveAbilityCheckFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingAbilityCheckRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo);
            LastDiceResult = $"{LastDiceResult} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"🎲 {CleanSessionNarration(result.Summary)}"
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

    private async Task ResolveActiveSpellSavingThrowFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingSpellSavingThrowRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
            LastDiceResult = $"{LastDiceResult} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"🎲 {CleanSessionNarration(result.Summary)}"
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

    private async Task ResolveActiveSavingThrowFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingSavingThrowRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
            LastDiceResult = $"{LastDiceResult} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"🎲 {CleanSessionNarration(result.Summary)}"
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

    private async Task ResolveActiveConcentrationCheckFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingConcentrationCheckRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
            LastDiceResult = $"{LastDiceResult} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"🎲 {CleanSessionNarration(result.Summary)}"
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

    private async Task ResolveActiveInitiativeFromRollAsync(string pendingRollId, int rollOne, int? rollTwo)
    {
        if (SelectedCampaign is null) return;
        try
        {
            var result = _engine.ResolvePendingInitiativeRoll(SelectedCampaign, pendingRollId, rollOne, rollTwo, _dice);
            LastDiceResult = $"{LastDiceResult} • {result.Summary}";
            StatusMessage = result.Summary;
            SelectedCampaign.Chat.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"🎲 {CleanSessionNarration(result.Summary)}"
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

    private async Task AdvanceNpcTurnsAfterPlayerRollAsync()
    {
        if (SelectedCampaign is null || !HasActiveCombat) return;
        var active = SelectedCampaign.Encounters.LastOrDefault(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
        if (active is null || active.Combatants.Count == 0) return;
        var current = active.Combatants[Math.Clamp(active.TurnIndex, 0, active.Combatants.Count - 1)];
        var character = SelectedCampaign.Characters.FirstOrDefault(c => c.Id == current.CharacterId);
        if (character is null || character.CurrentHp > 0 || character.Stable || character.Dead) return;

        // A successful/failed ordinary Death Save leaves the PC unconscious at 0 HP.
        // The user still owns End Turn so the result remains visible instead of immediately
        // disappearing into another model/tool loop.
        StatusMessage = $"{character.Name}'s Death Saving Throw is resolved. End the turn when ready.";
        await Task.CompletedTask;
    }

    private static D20RollMode ParsePendingPlayerRollMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
    {
        "advantage" or "adv" => D20RollMode.Advantage,
        "disadvantage" or "dis" => D20RollMode.Disadvantage,
        _ => D20RollMode.Normal
    };
}
