using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed record InitiativeSequenceResult(
    bool Completed,
    PendingRollRequest? PendingRoll,
    IReadOnlyList<InitiativeEntry> Order,
    IReadOnlyList<string> ResolvedNpcRolls,
    string Summary);

public sealed partial class GameEngine
{
    public InitiativeSequenceResult BeginInitiativeSequence(
        CampaignState campaign,
        string encounterId,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        if (!encounter.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encounter is not active.");
        if (encounter.Combatants.Count == 0)
            throw new InvalidOperationException("The encounter has no combatants.");

        if (campaign.PendingPlayerRoll?.Required == true)
        {
            var existing = campaign.PendingPlayerRoll;
            if (existing.ResolutionKey.Equals("initiative", StringComparison.OrdinalIgnoreCase)
                && existing.EncounterId?.Equals(encounter.Id, StringComparison.OrdinalIgnoreCase) == true)
            {
                return new InitiativeSequenceResult(
                    false,
                    existing,
                    Array.Empty<InitiativeEntry>(),
                    Array.Empty<string>(),
                    existing.Purpose);
            }

            throw new InvalidOperationException($"Resolve the required player roll first: {existing.Purpose}");
        }

        if (encounter.Combatants.All(c => c.Initiative.HasValue))
        {
            var existingOrder = GetInitiativeOrder(campaign, encounter.Id);
            return new InitiativeSequenceResult(
                true,
                null,
                existingOrder,
                Array.Empty<string>(),
                $"Initiative is already established for encounter '{encounter.Name}'.");
        }

        return AdvanceInitiativeSequence(campaign, encounter, dice);
    }

    public InitiativeSequenceResult ResolvePendingInitiativeRoll(
        CampaignState campaign,
        string pendingRollId,
        int rollOne,
        int? rollTwo,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(dice);
        if (rollOne is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollOne));
        if (rollTwo.HasValue && rollTwo.Value is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rollTwo));

        var pending = campaign.PendingPlayerRoll
            ?? throw new InvalidOperationException("There is no required player roll to resolve.");
        if (!pending.Id.Equals(pendingRollId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The supplied roll does not match the active pending player roll.");
        if (!pending.ResolutionKey.Equals("initiative", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pending roll is '{pending.ResolutionKey}', not Initiative.");
        if (string.IsNullOrWhiteSpace(pending.EncounterId) || string.IsNullOrWhiteSpace(pending.CombatantId))
            throw new InvalidOperationException("The pending Initiative roll is missing combat context.");

        var encounter = RequireEncounter(campaign, pending.EncounterId);
        var combatant = RequireCombatant(encounter, pending.CombatantId);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending Initiative roll no longer belongs to a player character.");
        if (!character.Id.Equals(pending.ActorCharacterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending Initiative actor no longer matches the combatant.");
        if (combatant.Initiative.HasValue)
            throw new InvalidOperationException($"{character.Name} already has an Initiative result.");

        var mode = ParseInitiativeMode(pending.RollMode);
        if (mode != D20RollMode.Normal && !rollTwo.HasValue)
            throw new InvalidOperationException($"This Initiative roll requires two d20 results because it has {mode}.");
        var chosen = mode switch
        {
            D20RollMode.Advantage => Math.Max(rollOne, rollTwo!.Value),
            D20RollMode.Disadvantage => Math.Min(rollOne, rollTwo!.Value),
            _ => rollOne
        };
        var total = chosen + pending.Modifier;
        SetInitiative(campaign, encounter.Id, combatant.Id, total);
        campaign.PendingPlayerRoll = null;
        Log(campaign, "initiative_roll", $"{character.Name} rolled Initiative {chosen} + {pending.Modifier} = {total}.");

        var continuation = AdvanceInitiativeSequence(campaign, encounter, dice);
        var playerSummary = $"{character.Name} Initiative: {total}.";
        var summary = continuation.Completed
            ? $"{playerSummary} {continuation.Summary}"
            : $"{playerSummary} {continuation.PendingRoll?.Purpose ?? continuation.Summary}";
        return continuation with { Summary = summary };
    }

    private InitiativeSequenceResult AdvanceInitiativeSequence(
        CampaignState campaign,
        EncounterState encounter,
        DiceService dice)
    {
        var resolvedNpcRolls = new List<string>();
        foreach (var combatant in encounter.Combatants.Where(c => !c.Initiative.HasValue))
        {
            var character = RequireCharacter(campaign, combatant.CharacterId);
            var mode = InitiativeModeFor(character, combatant);
            var dexterity = CharacterMechanics.AbilityModifier(CharacterMechanics.AbilityScore(character, "dexterity"));
            var exhaustionPenalty = 2 * Math.Clamp(character.ExhaustionLevel, 0, 6);
            var modifier = dexterity - exhaustionPenalty;

            if (character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
            {
                var modeText = mode == D20RollMode.Normal ? "" : $" with {mode}";
                var pending = new PendingRollRequest
                {
                    ActorCharacterId = character.Id,
                    EncounterId = encounter.Id,
                    CombatantId = combatant.Id,
                    Formula = "1d20",
                    RollType = "d20",
                    RollMode = mode.ToString().ToLowerInvariant(),
                    Purpose = $"{character.Name} must roll Initiative{modeText}.",
                    ResolutionKey = "initiative",
                    Modifier = modifier,
                    TargetLabel = "Initiative",
                    Required = true,
                    Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dexterity_modifier"] = dexterity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["exhaustion_penalty"] = exhaustionPenalty.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                };
                campaign.PendingPlayerRoll = pending;
                Touch(campaign);
                Log(campaign, "player_roll_requested", pending.Purpose, dmOnly: true);
                return new InitiativeSequenceResult(false, pending, Array.Empty<InitiativeEntry>(), resolvedNpcRolls, pending.Purpose);
            }

            var roll = dice.RollD20(mode);
            var total = roll.ChosenRoll + modifier;
            SetInitiative(campaign, encounter.Id, combatant.Id, total);
            var modeTextNpc = mode == D20RollMode.Normal ? "" : $" ({mode})";
            var summary = $"{character.Name}: {roll.ChosenRoll} + {modifier} = {total}{modeTextNpc}";
            resolvedNpcRolls.Add(summary);
            Log(campaign, "initiative_roll", summary, dmOnly: true);
        }

        var order = FinalizeInitiative(campaign, encounter.Id);
        var npcText = resolvedNpcRolls.Count == 0 ? "" : $" NPC rolls: {string.Join("; ", resolvedNpcRolls)}.";
        return new InitiativeSequenceResult(
            true,
            null,
            order,
            resolvedNpcRolls,
            $"Initiative order is established for '{encounter.Name}'.{npcText}");
    }

    private static D20RollMode InitiativeModeFor(CharacterSheet character, CombatantState combatant)
    {
        var disadvantage = combatant.Surprised || CharacterMechanics.IsIncapacitated(character);
        var advantage = CharacterMechanics.HasCondition(character, "Invisible");
        return advantage == disadvantage
            ? D20RollMode.Normal
            : advantage ? D20RollMode.Advantage : D20RollMode.Disadvantage;
    }

    private static D20RollMode ParseInitiativeMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
    {
        "advantage" or "adv" => D20RollMode.Advantage,
        "disadvantage" or "dis" => D20RollMode.Disadvantage,
        _ => D20RollMode.Normal
    };
}
