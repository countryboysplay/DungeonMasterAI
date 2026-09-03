using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

public sealed partial class GameEngine
{
    public HideResult TakeHide(CampaignState campaign, string encounterId, string combatantId, DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (character.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Player-character Hide checks must use the authoritative player-roll request path.");
        if (!combatant.Positioned)
            throw new InvalidOperationException($"{character.Name} must be positioned on the tactical grid before using Hide.");
        if (character.Dead || character.Conditions.Any(c => c.Equals("Incapacitated", StringComparison.OrdinalIgnoreCase) || c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{character.Name} cannot take the Hide action right now.");
        if (!HasHideCoverOrObscurity(encounter, combatant))
            throw new InvalidOperationException("Hide requires the creature to be Heavily Obscured or behind Three-Quarters or Total Cover.");

        var visibleEnemy = encounter.Combatants.FirstOrDefault(other =>
            IsEnemy(combatant, other)
            && CanSeeCombatant(campaign, encounter, other, combatant));
        if (visibleEnemy is not null)
        {
            var observer = RequireCharacter(campaign, visibleEnemy.CharacterId);
            throw new InvalidOperationException($"{character.Name} cannot Hide while still in {observer.Name}'s line of sight.");
        }

        ConsumeAction(combatant, character, "Hide");
        var rolls = dice.RollD20(D20RollMode.Normal);
        var check = ResolveAbilityCheck(campaign, character.Id, "dexterity", 15, rolls.RollOne, rolls.RollTwo, D20RollMode.Normal, "stealth");
        combatant.IsHidden = check.Success;
        combatant.HideCheckTotal = check.Success ? check.Total : 0;
        Touch(campaign);

        var summary = check.Success
            ? $"{character.Name} succeeded on the Hide action with Stealth {check.Total}. While hidden, the creature is treated as Invisible; Wisdom (Perception) DC {check.Total} finds it."
            : $"{character.Name} failed the DC 15 Hide check and is not hidden.";
        Log(campaign, "hide", summary);
        return new HideResult(encounter.Id, combatant.Id, character.Id, check, combatant.IsHidden, combatant.HideCheckTotal, summary);
    }

    public HiddenSearchResult SearchForHiddenCombatant(
        CampaignState campaign,
        string encounterId,
        string searcherCombatantId,
        string targetCombatantId,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var searcherCombatant = RequireCombatant(encounter, searcherCombatantId);
        EnsureCurrentTurn(encounter, searcherCombatant.Id);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        if (!targetCombatant.IsHidden)
            throw new InvalidOperationException("The selected target is not currently hidden.");
        if (searcherCombatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A creature cannot search for itself.");

        var searcher = RequireCharacter(campaign, searcherCombatant.CharacterId);
        if (searcher.CharacterType.Equals("pc", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Player-character hidden Search checks must use the authoritative player-roll request path.");
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        ConsumeAction(searcherCombatant, searcher, "Search");
        var dc = Math.Max(1, targetCombatant.HideCheckTotal);
        var check = ResolveAbilityCheckWithDice(campaign, searcher.Id, "wisdom", dc, dice, D20RollMode.Normal, "perception");
        if (check.Success)
            BreakHidden(campaign, encounter, targetCombatant, $"being found by {searcher.Name}'s Wisdom (Perception) check");

        Touch(campaign);
        var summary = check.Success
            ? $"{searcher.Name} found hidden {target.Name} with Perception {check.Total} vs DC {dc}."
            : $"{searcher.Name} failed to find hidden {target.Name}: Perception {check.Total} vs DC {dc}.";
        Log(campaign, "search_hidden", summary);
        return new HiddenSearchResult(encounter.Id, searcherCombatant.Id, targetCombatant.Id, check, check.Success, summary);
    }

    public ReadyActionResult TakeReadyAttack(
        CampaignState campaign,
        string encounterId,
        string combatantId,
        string targetCombatantId,
        string trigger,
        string? attackName = null)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        var targetCombatant = RequireCombatant(encounter, targetCombatantId);
        if (combatant.Id.Equals(targetCombatant.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A readied attack requires another combatant as its target.");
        _ = SelectAttackProfile(character, attackName);
        var normalizedTrigger = NormalizeReadyTrigger(trigger);

        ConsumeAction(combatant, character, "Ready");
        combatant.ReadiedAction = new ReadiedActionState
        {
            Trigger = normalizedTrigger,
            Kind = "attack",
            TargetCombatantId = targetCombatant.Id,
            AttackName = attackName?.Trim(),
            PreparedRound = encounter.Round,
            PreparedTurnIndex = encounter.TurnIndex
        };
        Touch(campaign);
        var summary = $"{character.Name} readied an attack for the trigger: {normalizedTrigger}";
        Log(campaign, "ready_attack", summary);
        return new ReadyActionResult(encounter.Id, combatant.Id, character.Id, "attack", normalizedTrigger, summary);
    }

    public ReadyActionResult TakeReadyMove(CampaignState campaign, string encounterId, string combatantId, string trigger)
    {
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var combatant = RequireCombatant(encounter, combatantId);
        EnsureCurrentTurn(encounter, combatant.Id);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        if (!combatant.Positioned)
            throw new InvalidOperationException($"{character.Name} must be positioned on the tactical grid before readying movement.");
        var normalizedTrigger = NormalizeReadyTrigger(trigger);

        ConsumeAction(combatant, character, "Ready");
        combatant.ReadiedAction = new ReadiedActionState
        {
            Trigger = normalizedTrigger,
            Kind = "move",
            PreparedRound = encounter.Round,
            PreparedTurnIndex = encounter.TurnIndex
        };
        Touch(campaign);
        var summary = $"{character.Name} readied movement up to Speed for the trigger: {normalizedTrigger}";
        Log(campaign, "ready_move", summary);
        return new ReadyActionResult(encounter.Id, combatant.Id, character.Id, "move", normalizedTrigger, summary);
    }

    public EncounterAttackResult TriggerReadiedAttack(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        DiceService dice)
    {
        ArgumentNullException.ThrowIfNull(dice);
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        var reactorCombatant = RequireCombatant(encounter, reactorCombatantId);
        var reactor = RequireCharacter(campaign, reactorCombatant.CharacterId);
        var readied = RequireReadiedAction(reactorCombatant, "attack");
        if (!reactorCombatant.ReactionAvailable)
            throw new InvalidOperationException($"{reactor.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(reactor))
            throw new InvalidOperationException($"{reactor.Name} cannot take a Reaction right now.");

        var targetCombatant = RequireCombatant(encounter, readied.TargetCombatantId ?? "");
        var target = RequireCharacter(campaign, targetCombatant.CharacterId);
        if (target.Dead) throw new InvalidOperationException($"{target.Name} is already dead.");
        var profile = SelectAttackProfile(reactor, readied.AttackName);
        ValidateAttackRange(reactorCombatant, targetCombatant, reactor, target, profile);
        var coverBonus = GetCoverBonus(encounter, reactorCombatant, targetCombatant);
        if (coverBonus >= 100)
            throw new InvalidOperationException($"{target.Name} has Total Cover from {reactor.Name} and cannot be targeted directly by the readied attack.");

        var effectiveArmorClass = EffectiveArmorClass(campaign, target) + coverBonus;
        var attackMode = AttackRollMode(campaign, encounter, reactorCombatant, targetCombatant, reactor, target);
        var helpUsed = ConsumeHelpAttackAdvantage(encounter, reactorCombatant, targetCombatant);
        var automaticCritical = IsAutomaticCriticalHitTarget(reactorCombatant, targetCombatant, target);
        var effectAttackBonus = RollActiveAttackBonus(campaign, reactor.Id, dice);
        var attack = dice.Attack(profile.AttackBonus, effectiveArmorClass, profile.DamageExpression, attackMode, automaticCritical, effectAttackBonus);
        ConsumeNextAttackAdvantageEffect(campaign, target.Id);
        BreakHidden(campaign, encounter, reactorCombatant, "making an attack roll");
        reactorCombatant.ReactionAvailable = false;
        reactorCombatant.ReadiedAction = null;

        DamageResult? damage = null;
        ConcentrationCheckResult? concentration = null;
        if (attack.Hit)
        {
            var resolution = ApplyDamageWithConcentration(campaign, target.Id, attack.Damage, dice, profile.DamageType, attack.Critical);
            damage = resolution.Damage;
            concentration = resolution.Concentration;
        }

        var coverText = coverBonus > 0 ? $" ({CoverLabel(coverBonus)} Cover: +{coverBonus} AC)" : "";
        var helpText = helpUsed ? " Help supplied Advantage for this attack roll." : "";
        var summary = attack.Hit
            ? $"{reactor.Name} used a Reaction for the readied {profile.Name} attack against {target.Name}{coverText}: {attack.Summary}{helpText}"
            : $"{reactor.Name} used a Reaction for the readied {profile.Name} attack against {target.Name}{coverText}: miss.{helpText}";
        if (concentration is not null) summary += $" {concentration.Summary}";
        Touch(campaign);
        Log(campaign, "ready_attack_triggered", summary);
        return new EncounterAttackResult(encounter.Id, reactor.Name, target.Name, profile.Name, attack, damage, summary, concentration, true, coverBonus);
    }

    public CombatMoveResult TriggerReadiedMove(
        CampaignState campaign,
        string encounterId,
        string reactorCombatantId,
        int gridX,
        int gridY)
    {
        if (campaign.PendingPlayerDecision?.Required == true)
            throw new InvalidOperationException($"Resolve the required player decision first: {campaign.PendingPlayerDecision.Prompt}");
        var encounter = RequireEncounter(campaign, encounterId);
        EnsureEncounterActionReady(encounter);
        if (encounter.PendingMove is not null)
            throw new InvalidOperationException("Resolve the pending movement reaction window before triggering readied movement.");
        var combatant = RequireCombatant(encounter, reactorCombatantId);
        var character = RequireCharacter(campaign, combatant.CharacterId);
        _ = RequireReadiedAction(combatant, "move");
        if (!combatant.ReactionAvailable)
            throw new InvalidOperationException($"{character.Name} has already used a Reaction since the start of their last turn.");
        if (!CanTakeReaction(character))
            throw new InvalidOperationException($"{character.Name} cannot take a Reaction right now.");
        if (!combatant.Positioned)
            throw new InvalidOperationException($"{character.Name} must be positioned before using readied movement.");
        EnsureSquareAvailable(encounter, combatant.Id, gridX, gridY);

        var map = ResolveEncounterMap(campaign, encounter.Id);
        var path = TraceGridPath(combatant.GridX, combatant.GridY, gridX, gridY);
        ValidateMovementPath(encounter, combatant.Id, path);
        ValidateMapMovementPath(map, combatant.GridX, combatant.GridY, path);
        var distanceFeet = GridDistanceFeet(combatant.GridX, combatant.GridY, gridX, gridY);
        var movementCostFeet = MovementCostFeet(encounter, map, path, character);
        var speed = CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects);
        if (movementCostFeet > speed)
            throw new InvalidOperationException($"That readied move costs {movementCostFeet} feet, exceeding {character.Name}'s Speed of {speed} feet.");

        // The whole path is validated before the Reaction and the readied action are spent: the
        // committing walk is not atomic, and a throw inside it must not strand a half-spent Ready.
        ValidateMovementBattlefieldEffects(campaign, encounter, combatant, path);

        combatant.ReactionAvailable = false;
        combatant.ReadiedAction = null;
        var opportunityAttacks = FindOpportunityAttackWindows(campaign, encounter, combatant, gridX, gridY);
        if (opportunityAttacks.Count > 0)
        {
            encounter.PendingMove = new PendingCombatMove
            {
                CombatantId = combatant.Id,
                FromX = combatant.GridX,
                FromY = combatant.GridY,
                ToX = gridX,
                ToY = gridY,
                DistanceFeet = distanceFeet,
                MovementCostFeet = movementCostFeet,
                ReadiedReactionMove = true,
                OpportunityAttacks = opportunityAttacks
            };
            Touch(campaign);
            var names = string.Join(", ", opportunityAttacks.Select(x => x.ReactorName));
            var pendingSummary = $"{character.Name}'s readied movement triggered and would leave the reach of {names}. Resolve or decline those Opportunity Attacks before the readied movement is committed.";
            Log(campaign, "ready_move_triggered", pendingSummary, dmOnly: true);
            return new CombatMoveResult(encounter.Id, combatant.Id, character.Id, combatant.GridX, combatant.GridY, gridX, gridY, distanceFeet, movementCostFeet, combatant.MovementRemainingFeet, false, opportunityAttacks, pendingSummary);
        }

        return CommitReadiedCombatMove(campaign, encounter, combatant, character, gridX, gridY, distanceFeet, movementCostFeet);
    }

    private CombatMoveResult CommitReadiedCombatMove(
        CampaignState campaign,
        EncounterState encounter,
        CombatantState combatant,
        CharacterSheet character,
        int gridX,
        int gridY,
        int distanceFeet,
        int movementCostFeet)
    {
        var fromX = combatant.GridX;
        var fromY = combatant.GridY;
        var path = TraceGridPath(fromX, fromY, gridX, gridY);
        var map = ResolveEncounterMap(campaign, encounter.Id);
        var dice = new DiceService();
        var previousX = fromX;
        var previousY = fromY;
        var spentMovement = 0;
        var stepsCompleted = 0;

        foreach (var (nextX, nextY) in path)
        {
            var stepCost = MovementStepCostFeet(encounter, map, nextX, nextY, character);
            combatant.GridX = nextX;
            combatant.GridY = nextY;
            spentMovement += stepCost;
            stepsCompleted++;
            ProcessBattlefieldEffectsOnMovement(campaign, encounter, combatant, previousX, previousY, nextX, nextY, dice);
            previousX = nextX;
            previousY = nextY;
            if (character.Dead || CharacterMechanics.HasCondition(character, "Unconscious") || CharacterMechanics.EffectiveSpeed(character, campaign.ActiveEffects) <= 0)
                break;
        }

        EndInvalidGrapples(campaign, encounter);
        Touch(campaign);
        var actualDistanceFeet = stepsCompleted * 5;
        var difficult = spentMovement > actualDistanceFeet ? $" Difficult Terrain increased the movement cost to {spentMovement} feet." : "";
        var interrupted = stepsCompleted < path.Count ? " Movement ended early because the creature could no longer continue." : "";
        var summary = $"{character.Name} used a Reaction for readied movement, moving {actualDistanceFeet} feet to grid ({combatant.GridX}, {combatant.GridY}).{difficult}{interrupted}";
        Log(campaign, "ready_move_committed", summary);
        return new CombatMoveResult(encounter.Id, combatant.Id, character.Id, fromX, fromY, combatant.GridX, combatant.GridY, actualDistanceFeet, spentMovement, combatant.MovementRemainingFeet, true, [], summary);
    }

    private static ReadiedActionState RequireReadiedAction(CombatantState combatant, string expectedKind)
    {
        var readied = combatant.ReadiedAction ?? throw new InvalidOperationException("That combatant has no readied action awaiting a trigger.");
        if (!readied.Kind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The combatant readied {readied.Kind}, not {expectedKind}.");
        return readied;
    }

    private static string NormalizeReadyTrigger(string trigger)
    {
        var normalized = (trigger ?? "").Trim();
        if (normalized.Length < 3)
            throw new ArgumentException("Ready requires a perceivable trigger description.", nameof(trigger));
        if (normalized.Length > 500)
            throw new ArgumentException("Ready trigger descriptions are limited to 500 characters.", nameof(trigger));
        return normalized;
    }

    private static AttackProfile SelectAttackProfile(CharacterSheet character, string? attackName)
    {
        if (string.IsNullOrWhiteSpace(attackName))
            return character.Attacks.FirstOrDefault() ?? CharacterMechanics.UnarmedStrikeProfile(character);
        var normalized = attackName.Trim();
        var chosen = character.Attacks.FirstOrDefault(a => a.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (chosen is null && normalized.Equals("Unarmed Strike", StringComparison.OrdinalIgnoreCase))
            chosen = CharacterMechanics.UnarmedStrikeProfile(character);
        return chosen ?? throw new InvalidOperationException($"{character.Name} has no configured attack matching '{normalized}'.");
    }

    private static bool HasHideCoverOrObscurity(EncounterState encounter, CombatantState combatant) =>
        encounter.Terrain.Any(t => ContainsSquare(t, combatant.GridX, combatant.GridY)
            && (t.HeavilyObscured || NormalizeCover(t.Cover) is "three-quarters" or "total"))
        || encounter.BattlefieldEffects.Any(e => e.HeavilyObscured && BattlefieldEffectContainsCell(e, combatant.GridX, combatant.GridY));

    /// <summary>
    /// Whether two combatants are on opposing sides. Sides are compared after normalization so a
    /// combatant whose side was written in a legacy vocabulary is not treated as hostile to its own
    /// party by an exact string comparison; an unrecognized side falls back to comparing verbatim.
    /// </summary>
    private static bool IsEnemy(CombatantState subject, CombatantState other)
    {
        if (subject.Id.Equals(other.Id, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(subject.Side) || string.IsNullOrWhiteSpace(other.Side)) return true;
        var subjectSide = CombatSide.TryNormalize(subject.Side) ?? subject.Side.Trim();
        var otherSide = CombatSide.TryNormalize(other.Side) ?? other.Side.Trim();
        if (subjectSide == CombatSide.Neutral || otherSide == CombatSide.Neutral) return false;
        return !subjectSide.Equals(otherSide, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanSeeCombatant(CampaignState campaign, EncounterState encounter, CombatantState observerCombatant, CombatantState targetCombatant)
    {
        var observer = RequireCharacter(campaign, observerCombatant.CharacterId);
        if (targetCombatant.IsHidden) return false;
        if (observer.Dead || observer.Conditions.Any(c => c.Equals("Blinded", StringComparison.OrdinalIgnoreCase) || c.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!observerCombatant.Positioned || !targetCombatant.Positioned)
            return true; // Conservative: unknown positioning is not sufficient proof that Hide is legal.

        if (encounter.Terrain.Any(t => ContainsSquare(t, targetCombatant.GridX, targetCombatant.GridY) && (t.HeavilyObscured || NormalizeCover(t.Cover) == "total" || t.BlocksLineOfSight))
            || encounter.BattlefieldEffects.Any(e => (e.HeavilyObscured || e.BlocksLineOfSight) && BattlefieldEffectContainsCell(e, targetCombatant.GridX, targetCombatant.GridY)))
            return false;

        var line = TraceGridPath(observerCombatant.GridX, observerCombatant.GridY, targetCombatant.GridX, targetCombatant.GridY);
        foreach (var (x, y) in line.Take(Math.Max(0, line.Count - 1)))
        {
            if (encounter.Terrain.Any(t => ContainsSquare(t, x, y) && (t.BlocksLineOfSight || NormalizeCover(t.Cover) == "total"))
                || encounter.BattlefieldEffects.Any(e => e.BlocksLineOfSight && BattlefieldEffectContainsCell(e, x, y)))
                return false;
        }
        return true;
    }

    private static void BreakHidden(CampaignState campaign, EncounterState encounter, CombatantState combatant, string reason)
    {
        if (!combatant.IsHidden) return;
        var character = RequireCharacter(campaign, combatant.CharacterId);
        combatant.IsHidden = false;
        combatant.HideCheckTotal = 0;
        Touch(campaign);
        Log(campaign, "hidden_ended", $"{character.Name} stopped being hidden after {reason}.");
    }
}
