using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public DeathSaveResult ResolveDeathSavingThrow(CampaignState campaign, string characterId, int roll, int savingThrowBonus = 0)
    {
        if (roll is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(roll));
        var character = RequireCharacter(campaign, characterId);
        if (character.Dead) throw new InvalidOperationException($"{character.Name} is dead.");
        if (character.CurrentHp != 0) throw new InvalidOperationException("Death Saving Throws are made only while at 0 Hit Points.");
        if (character.Stable) throw new InvalidOperationException($"{character.Name} is Stable and does not make Death Saving Throws.");

        var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
        var modifiedTotal = roll + savingThrowBonus - exhaustionPenalty;
        string summary;
        if (roll == 20)
        {
            character.CurrentHp = 1;
            character.DeathSaveSuccesses = 0;
            character.DeathSaveFailures = 0;
            character.Stable = false;
            RemoveConditionInternal(character, "Unconscious");
            summary = $"{character.Name} rolled a natural 20 on a Death Saving Throw and regained 1 HP.";
        }
        else if (roll == 1)
        {
            character.DeathSaveFailures = Math.Min(3, character.DeathSaveFailures + 2);
            if (character.DeathSaveFailures >= 3) MarkDead(character);
            summary = character.Dead
                ? $"{character.Name} rolled a natural 1, suffered two failures, and died."
                : $"{character.Name} rolled a natural 1 and suffered two Death Saving Throw failures.";
        }
        else if (modifiedTotal >= 10)
        {
            character.DeathSaveSuccesses = Math.Min(3, character.DeathSaveSuccesses + 1);
            if (character.DeathSaveSuccesses >= 3)
            {
                character.Stable = true;
                character.DeathSaveSuccesses = 0;
                character.DeathSaveFailures = 0;
                summary = $"{character.Name} reached three Death Saving Throw successes and is Stable.";
            }
            else summary = $"{character.Name} succeeded on a Death Saving Throw with a modified total of {modifiedTotal} ({character.DeathSaveSuccesses}/3).";
        }
        else
        {
            character.DeathSaveFailures = Math.Min(3, character.DeathSaveFailures + 1);
            if (character.DeathSaveFailures >= 3) MarkDead(character);
            summary = character.Dead
                ? $"{character.Name} reached three Death Saving Throw failures and died."
                : $"{character.Name} failed a Death Saving Throw with a modified total of {modifiedTotal} ({character.DeathSaveFailures}/3).";
        }

        if (character.Dead) EndGrapplesForCharacter(campaign, character.Id, includeTarget: true);
        Touch(campaign);
        Log(campaign, "death_save", summary);
        return new DeathSaveResult(roll, character.DeathSaveSuccesses, character.DeathSaveFailures, character.Stable, character.Dead, character.CurrentHp, summary);
    }

    public DeathSaveResult ResolveDeathSavingThrowWithDice(CampaignState campaign, string characterId, DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var roll = dice.Roll("1d20").Total;
        var effectBonus = RollActiveSavingThrowBonus(campaign, characterId, dice);
        return ResolveDeathSavingThrow(campaign, characterId, roll, effectBonus);
    }

    public DeathSaveResult ResolveCombatDeathSavingThrow(CampaignState campaign, string encounterId, string combatantId, DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var roll = dice.Roll("1d20").Total;
        return ResolveCombatDeathSavingThrow(campaign, encounterId, combatantId, roll, dice);
    }

    public DeathSaveResult ResolveCombatDeathSavingThrow(CampaignState campaign, string encounterId, string combatantId, int d20Roll, DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        if (d20Roll is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(d20Roll));

        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!combatant.DeathSaveRequiredThisTurn)
            throw new InvalidOperationException($"{character.Name} did not start this turn needing a Death Saving Throw.");
        if (combatant.DeathSaveResolvedThisTurn)
            throw new InvalidOperationException($"{character.Name} has already made a Death Saving Throw this turn.");

        var pending = campaign.PendingPlayerRoll;
        if (pending is not null && (!pending.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase)
            || !pending.ActorCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pending.CombatantId, combatant.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another required player roll is pending and must be resolved first.");

        var effectBonus = RollActiveSavingThrowBonus(campaign, character.Id, dice);
        var result = ResolveDeathSavingThrow(campaign, character.Id, d20Roll, effectBonus);
        combatant.DeathSaveResolvedThisTurn = true;

        if (character.CurrentHp == 0)
        {
            combatant.MovementRemainingFeet = 0;
            combatant.ActionAvailable = false;
            combatant.BonusActionAvailable = false;
            combatant.AttackActionInProgress = false;
            combatant.AttacksRemainingInAction = 0;
        }

        if (campaign.PendingPlayerRoll?.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase) == true
            && campaign.PendingPlayerRoll.ActorCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase))
            campaign.PendingPlayerRoll = null;

        Touch(campaign);
        Log(campaign, "combat_death_save", result.Summary);
        return result;
    }

    public PendingRollRequest? EnsurePendingPlayerRollForActiveCombat(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var encounter = campaign.Encounters.FirstOrDefault(e => e.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
        if (encounter is null || encounter.Combatants.Count == 0 || encounter.TurnIndex < 0 || encounter.TurnIndex >= encounter.Combatants.Count)
        {
            if (campaign.PendingPlayerRoll?.EncounterId is not null) campaign.PendingPlayerRoll = null;
            return campaign.PendingPlayerRoll;
        }

        var current = encounter.Combatants[encounter.TurnIndex];
        SyncPendingPlayerRollForCurrentTurn(campaign, encounter, current);
        return campaign.PendingPlayerRoll;
    }

    private static void SyncPendingPlayerRollForCurrentTurn(CampaignState campaign, EncounterState encounter, CombatantState combatant)
    {
        var character = RequireCharacter(campaign, combatant.CharacterId);
        var requiresDeathSave = character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase)
            && character.CurrentHp == 0
            && !character.Stable
            && !character.Dead
            && !combatant.DeathSaveResolvedThisTurn;

        combatant.DeathSaveRequiredThisTurn = requiresDeathSave;
        if (!requiresDeathSave)
        {
            if (campaign.PendingPlayerRoll?.EncounterId == encounter.Id
                && campaign.PendingPlayerRoll.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase))
                campaign.PendingPlayerRoll = null;
            return;
        }

        if (campaign.PendingPlayerRoll is not null
            && campaign.PendingPlayerRoll.ResolutionKey.Equals("combat_death_save", StringComparison.OrdinalIgnoreCase)
            && campaign.PendingPlayerRoll.ActorCharacterId.Equals(character.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(campaign.PendingPlayerRoll.CombatantId, combatant.Id, StringComparison.OrdinalIgnoreCase))
            return;

        campaign.PendingPlayerRoll = new PendingRollRequest
        {
            ActorCharacterId = character.Id,
            EncounterId = encounter.Id,
            CombatantId = combatant.Id,
            Formula = "1d20",
            RollType = "d20",
            Purpose = $"{character.Name} must make a Death Saving Throw.",
            ResolutionKey = "combat_death_save",
            TargetNumber = 10,
            TargetLabel = "DC 10",
            Required = true
        };
    }
}
