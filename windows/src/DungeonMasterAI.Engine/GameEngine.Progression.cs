using DungeonMasterAI.Domain;

namespace DungeonMasterAI.Engine;

/// <summary>
/// The XP economy's engine surface, specified in <c>docs/progression-direction.md</c>.
///
/// Every faucet is adjudicated here, in C#, deterministically. The DM model has no tool that
/// grants, adjusts, or withholds XP and is not getting one; it reads progression and narrates it.
/// The two faucets it can influence at all -- quest status and encounter end -- pay once per
/// subject and are gated on state it does not control.
/// </summary>
public sealed partial class GameEngine
{
    private const string PlayerCharacterType = "pc";

    // -------------------------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Player characters who take a share of an award: alive, and a PC.
    ///
    /// A PC at 0 HP, unconscious, stable, or dying takes a FULL share. Docking XP from the
    /// character who got dropped punishes the player for the thing the fight was about, and that
    /// is where players start refusing to engage with a reward system.
    /// </summary>
    public static IReadOnlyList<CharacterSheet> EligiblePartyMembers(CampaignState campaign)
    {
        Guard.NotNull(campaign, nameof(campaign));
        return campaign.Characters
            .Where(c => c.CharacterType.Equals(PlayerCharacterType, StringComparison.OrdinalIgnoreCase) && !c.Dead)
            .ToArray();
    }

    /// <summary>
    /// The party's level for scaling payouts: the LOWEST among eligible PCs, not the average or
    /// the highest. Every level-scaled default in this economy is money going out, and scaling a
    /// payout off the strongest member would over-reward a party carrying a low-level character.
    /// </summary>
    public static int PartyLevel(CampaignState campaign)
    {
        var eligible = EligiblePartyMembers(campaign);
        return eligible.Count == 0 ? 1 : eligible.Min(c => Math.Clamp(c.Level, 1, Progression.MaximumLevel));
    }

    /// <summary>
    /// A creature's XP value: explicit override, then authored Challenge Rating, then derivation
    /// from stats. Floored so a defeated hostile never pays nothing.
    /// </summary>
    public static int ExperienceValueOf(CharacterSheet character)
    {
        Guard.NotNull(character, nameof(character));
        if (character.ExperienceValue is { } authored && authored > 0) return authored;
        if (Progression.TryExperienceForChallengeRating(character.ChallengeRating, out var byRating))
            return Math.Max(Progression.MinimumCreatureExperience, byRating);
        return Math.Max(
            Progression.MinimumCreatureExperience,
            Progression.ExperienceForChallengeRating(Progression.DeriveChallengeRating(character)));
    }

    // -------------------------------------------------------------------------------------------
    // Awards
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Divides a party total across the eligible party and grants it.
    ///
    /// The remainder from integer division goes one point at a time to the members with the lowest
    /// totals, which is deterministic and actively pulls the party back together instead of
    /// letting rounding drift accumulate across hundreds of awards.
    /// </summary>
    public IReadOnlyList<ExperienceAward> AwardExperience(
        CampaignState campaign,
        int partyTotal,
        string sourceKind,
        string sourceName)
    {
        Guard.NotNull(campaign, nameof(campaign));
        if (partyTotal < 0) throw new ArgumentOutOfRangeException(nameof(partyTotal));

        var eligible = EligiblePartyMembers(campaign);
        if (partyTotal == 0 || eligible.Count == 0)
        {
            if (partyTotal > 0)
            {
                // Not queued. An award with nobody alive to take it is logged and dropped, so a
                // wipe cannot bank a windfall that pays out on the next resurrection.
                Log(campaign, "experience_unawarded",
                    $"{partyTotal} XP from {sourceName} was not awarded: no living player character.");
            }
            return [];
        }

        var shares = DivideAcrossParty(partyTotal, eligible);
        var awards = new List<ExperienceAward>(eligible.Count);
        for (var i = 0; i < eligible.Count; i++)
        {
            if (shares[i] <= 0) continue;
            awards.Add(GrantExperienceTo(campaign, eligible[i], shares[i], sourceKind, sourceName));
        }

        Touch(campaign);
        return awards;
    }

    /// <summary>
    /// Grants the same amount to every eligible PC without dividing it. Used by the discovery
    /// faucet: finding a place is not an encounter and does not get thinner with a larger party.
    /// </summary>
    public IReadOnlyList<ExperienceAward> AwardExperienceToEachPartyMember(
        CampaignState campaign,
        int amountEach,
        string sourceKind,
        string sourceName)
    {
        Guard.NotNull(campaign, nameof(campaign));
        if (amountEach < 0) throw new ArgumentOutOfRangeException(nameof(amountEach));
        if (amountEach == 0) return [];

        var awards = EligiblePartyMembers(campaign)
            .Select(pc => GrantExperienceTo(campaign, pc, amountEach, sourceKind, sourceName))
            .ToArray();
        if (awards.Length > 0) Touch(campaign);
        return awards;
    }

    /// <summary>
    /// Faucet F1 and F4: one creature pays out, once, ever. Called from the death choke point and
    /// from encounter resolution, and the shared flag is what makes killing two of four watchers
    /// and talking down the rest pay for four creatures rather than six.
    /// </summary>
    public IReadOnlyList<ExperienceAward> AwardDefeatExperience(
        CampaignState campaign,
        CharacterSheet defeated,
        string sourceKind = "defeat")
    {
        Guard.NotNull(campaign, nameof(campaign));
        Guard.NotNull(defeated, nameof(defeated));
        if (defeated.CharacterType.Equals(PlayerCharacterType, StringComparison.OrdinalIgnoreCase)) return [];
        if (defeated.ExperienceAwarded) return [];

        defeated.ExperienceAwarded = true;
        return AwardExperience(campaign, ExperienceValueOf(defeated), sourceKind, defeated.Name);
    }

    /// <summary>
    /// The default payout for a quest that authored no XP: 15% of the party's current level band,
    /// per character. Expressed as a fraction of the band rather than a flat number, this one
    /// constant holds from level 1 to level 20 with no per-level tuning and cannot drift out of
    /// proportion as the curve steepens.
    /// </summary>
    public static int DefaultQuestExperience(int partyLevel, int partySize)
    {
        if (partySize <= 0) return 0;
        var perCharacter = (int)Math.Round(0.15 * Progression.LevelBandWidth(partyLevel), MidpointRounding.AwayFromZero);
        return Math.Max(0, perCharacter) * partySize;
    }

    /// <summary>
    /// Seeds a new character's XP so it is consistent with the level they were created at.
    ///
    /// Without this, an imported level-5 character sits at 0 XP and the next 300-XP award banks a
    /// level-up as though they were levelling 1 to 2 -- and keeps doing so until their XP catches
    /// up with a level they already had.
    ///
    /// What this deliberately does NOT decide is what XP a character should get for joining a
    /// campaign already in progress. XP is stored per character rather than as one party counter,
    /// so nothing in this schema forbids that, but the policy belongs to whoever builds joining.
    /// </summary>
    private static void SeedExperienceForNewCharacter(CharacterSheet character)
    {
        if (character.ExperiencePoints > 0) return;
        character.ExperiencePoints = Progression.ExperienceThresholdForLevel(character.Level);
    }

    private ExperienceAward GrantExperienceTo(
        CampaignState campaign,
        CharacterSheet character,
        int amount,
        string sourceKind,
        string sourceName)
    {
        // Level BEFORE and AFTER are read from the XP total, not from character.Level, because a
        // banked level-up leaves those two deliberately out of step. Comparing totals is what lets
        // a second threshold crossing bank a second level while the first is still unclaimed.
        var levelBefore = Progression.LevelForExperience(character.ExperiencePoints);
        character.ExperiencePoints += amount;
        var levelAfter = Progression.LevelForExperience(character.ExperiencePoints);

        var crossed = levelAfter > levelBefore;
        if (crossed)
        {
            var headroom = Math.Max(0, Progression.MaximumLevel - (character.Level + character.PendingLevelUps));
            var banked = Math.Min(levelAfter - levelBefore, headroom);
            if (banked > 0)
            {
                character.PendingLevelUps += banked;
                Log(campaign, "level_up_available",
                    $"{character.Name} has earned level {character.Level + character.PendingLevelUps}. It applies on a Long Rest.");
            }
            else
            {
                crossed = false;
            }
        }

        var summary = $"{character.Name} gained {amount} XP from {sourceName} ({character.ExperiencePoints} total).";
        Log(campaign, "experience_awarded", summary);

        return new ExperienceAward(
            character.Id,
            character.Name,
            amount,
            character.ExperiencePoints,
            character.Level,
            Progression.ExperienceToNextLevel(character.ExperiencePoints),
            crossed,
            sourceKind,
            sourceName,
            summary);
    }

    /// <summary>
    /// Splits a total across the party, remainder first to the lowest current totals, then by
    /// campaign order. Deterministic: no randomness anywhere in the XP path.
    /// </summary>
    internal static int[] DivideAcrossParty(int total, IReadOnlyList<CharacterSheet> recipients)
    {
        var shares = new int[recipients.Count];
        if (recipients.Count == 0 || total <= 0) return shares;

        var each = total / recipients.Count;
        for (var i = 0; i < shares.Length; i++) shares[i] = each;

        var remainder = total % recipients.Count;
        if (remainder == 0) return shares;

        var order = Enumerable.Range(0, recipients.Count)
            .OrderBy(i => recipients[i].ExperiencePoints)
            .ThenBy(i => i)
            .ToArray();
        for (var i = 0; i < remainder; i++) shares[order[i]]++;
        return shares;
    }

    // -------------------------------------------------------------------------------------------
    // Levelling
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies one banked level-up. Refused during an active encounter the character is fighting
    /// in: a level grants Hit Points, so allowing it mid-fight would make arranging to be damaged
    /// at the moment the threshold is crossed a genuine strategy.
    /// </summary>
    public LevelUpResult ApplyLevelUp(CampaignState campaign, string characterId)
    {
        Guard.NotNull(campaign, nameof(campaign));
        var character = RequireCharacter(campaign, characterId);

        // Validate everything before mutating anything (r60): a refused level-up must not leave
        // half a level applied.
        if (character.Dead)
            throw new InvalidOperationException($"{character.Name} is dead and cannot gain a level.");
        if (character.PendingLevelUps <= 0)
            throw new InvalidOperationException($"{character.Name} has no level-up waiting.");
        if (character.Level >= Progression.MaximumLevel)
            throw new InvalidOperationException($"{character.Name} is already at the maximum level.");
        if (IsInActiveEncounter(campaign, character.Id))
            throw new InvalidOperationException("A level-up cannot be applied during an active encounter. Finish the fight, then rest.");

        return ApplyLevelUpCore(campaign, character);
    }

    private LevelUpResult ApplyLevelUpCore(CampaignState campaign, CharacterSheet character)
    {
        var constitution = CharacterMechanics.AbilityModifier(
            CharacterMechanics.AbilityScore(character, "constitution"));
        // The SRD fixed-average option, never a roll. A level-up must not depend on a die the
        // player has to be prompted for -- and an XP economy whose payouts vary by luck is not
        // one anybody can balance.
        var hitPointsGained = Math.Max(1, (Math.Max(2, character.HitDieSides) / 2) + 1 + constitution);

        character.Level = Math.Min(Progression.MaximumLevel, character.Level + 1);
        character.ProficiencyBonus = CharacterMechanics.ProficiencyBonusForLevel(character.Level);
        character.MaxHp += hitPointsGained;
        // A level-up does not raise a character off 0 Hit Points. It is growth, not a heal.
        if (character.CurrentHp > 0)
            character.CurrentHp = Math.Min(character.MaxHp, character.CurrentHp + hitPointsGained);
        character.HitDiceMaximum++;
        character.HitDiceRemaining = Math.Min(character.HitDiceMaximum, character.HitDiceRemaining + 1);
        character.PendingLevelUps = Math.Max(0, character.PendingLevelUps - 1);

        var summary =
            $"{character.Name} reached level {character.Level}: +{hitPointsGained} maximum HP " +
            $"({character.MaxHp} total), Proficiency Bonus +{character.ProficiencyBonus}, " +
            $"{character.HitDiceMaximum} Hit Point Dice. Spell slots and class features are not granted automatically.";
        Touch(campaign);
        Log(campaign, "level_up", summary);

        return new LevelUpResult(
            character.Id,
            character.Name,
            character.Level,
            hitPointsGained,
            character.MaxHp,
            character.ProficiencyBonus,
            character.PendingLevelUps,
            summary);
    }

    /// <summary>
    /// Applies every banked level-up for a character, ignoring the encounter gate.
    ///
    /// Only called from LongRest, where the gate buys nothing: a Long Rest restores all Hit Points
    /// anyway, so the level-up's Hit Points cannot be the point of taking one.
    /// </summary>
    private List<LevelUpResult> ApplyAllPendingLevelUps(CampaignState campaign, CharacterSheet character)
    {
        var applied = new List<LevelUpResult>();
        while (character.PendingLevelUps > 0 && character.Level < Progression.MaximumLevel)
            applied.Add(ApplyLevelUpCore(campaign, character));
        // Nothing can be claimed above the cap; clear it rather than leaving a counter that
        // silently never drains.
        character.PendingLevelUps = 0;
        return applied;
    }

    private static bool IsInActiveEncounter(CampaignState campaign, string characterId) =>
        campaign.Encounters.Any(e =>
            e.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
            && e.Combatants.Any(c => c.CharacterId.Equals(characterId, StringComparison.OrdinalIgnoreCase)));

    // -------------------------------------------------------------------------------------------
    // Faucet hooks
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Faucet F2. Sets a quest's status and, on the transition into a completing status, pays the
    /// XP and -- for the first time in this product's life -- the gold that
    /// <see cref="Quest.RewardGp"/> has been advertising and never granting.
    /// </summary>
    public Quest SetQuestStatus(CampaignState campaign, string questId, string status)
    {
        Guard.NotNull(campaign, nameof(campaign));
        var quest = campaign.Quests.FirstOrDefault(q => q.Id == questId && !q.DmOnly)
            ?? throw new KeyNotFoundException("Player-visible quest not found.");

        var previous = quest.Status;
        quest.Status = (status ?? "").Trim();
        Touch(campaign);
        Log(campaign, "quest_status", $"Quest '{quest.Name}' changed to {quest.Status}.");

        if (!quest.RewardsGranted
            && Progression.IsCompletingQuestStatus(quest.Status)
            && !Progression.IsCompletingQuestStatus(previous))
        {
            GrantQuestRewards(campaign, quest);
        }

        return quest;
    }

    private void GrantQuestRewards(CampaignState campaign, Quest quest)
    {
        quest.RewardsGranted = true;
        var eligible = EligiblePartyMembers(campaign);

        var total = quest.RewardExperience > 0
            ? quest.RewardExperience
            : DefaultQuestExperience(PartyLevel(campaign), eligible.Count);
        AwardExperience(campaign, total, "quest", quest.Name);

        if (quest.RewardGp > 0 && eligible.Count > 0)
        {
            var goldShares = DivideAcrossParty(quest.RewardGp, eligible);
            for (var i = 0; i < eligible.Count; i++)
            {
                if (goldShares[i] <= 0) continue;
                eligible[i].Gold += goldShares[i];
            }
            Log(campaign, "quest_reward_gold",
                $"The party shared {quest.RewardGp} gp for completing '{quest.Name}'.");
            Touch(campaign);
        }
    }

    /// <summary>
    /// Faucet F4. Every opposition creature still standing when an ACTIVE encounter is ended pays
    /// its full value, once.
    ///
    /// Full value, not a fraction, and this is deliberate: the campaign manifests already author
    /// alternate_resolutions -- "the watchers can be bribed", "a stealth approach can avoid
    /// combat". Paying those less would state, in the only language an economy has, that the
    /// authored non-violent solution is the inferior play.
    ///
    /// It cannot be farmed. The encounter must have been active (a planned encounter the party has
    /// never met is not), each creature carries the same one-time flag the kill faucet uses, and
    /// a second end finds the encounter already completed.
    /// </summary>
    private void AwardEncounterResolutionExperience(CampaignState campaign, EncounterState encounter)
    {
        foreach (var combatant in encounter.Combatants.ToArray())
        {
            var character = campaign.Characters.FirstOrDefault(c => c.Id == combatant.CharacterId);
            if (character is null) continue;
            if (character.CharacterType.Equals(PlayerCharacterType, StringComparison.OrdinalIgnoreCase)) continue;
            if (combatant.Side.Equals("party", StringComparison.OrdinalIgnoreCase)) continue;
            if (combatant.Side.Equals("neutral", StringComparison.OrdinalIgnoreCase)) continue;
            if (character.ExperienceAwarded) continue;
            AwardDefeatExperience(campaign, character, "resolution");
        }
    }
}
