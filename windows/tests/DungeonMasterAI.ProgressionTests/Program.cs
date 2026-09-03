using System.Text.Json;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

// r62 progression tests. The economy specified in docs/progression-direction.md, exercised against
// a four-player-character party. Every failure is reported in one run rather than stopping at the
// first.
//
// Two groups here carry more weight than the rest:
//
//   * Derived Challenge Rating. Nothing in the domain or in any campaign manifest records a CR, so
//     the derivation is the only thing standing between this economy and awarding the floor for
//     every creature forever while compiling cleanly. It is pinned against two known SRD monsters.
//
//   * The migration round trip. A save file written at schema version 4 must still load. Migration
//     bugs eat player saves.

var failures = new List<string>();
var passed = 0;

// ---------------------------------------------------------------------------
// The curve
// ---------------------------------------------------------------------------

Run("the SRD level thresholds are exact at both ends and in the middle", () =>
{
    Equal(0, Progression.ExperienceThresholdForLevel(1), "level 1 threshold");
    Equal(300, Progression.ExperienceThresholdForLevel(2), "level 2 threshold");
    Equal(2700, Progression.ExperienceThresholdForLevel(4), "level 4 threshold");
    Equal(64000, Progression.ExperienceThresholdForLevel(10), "level 10 threshold");
    Equal(355000, Progression.ExperienceThresholdForLevel(20), "level 20 threshold");
    // Out-of-range levels clamp rather than throwing: this runs against imported data.
    Equal(0, Progression.ExperienceThresholdForLevel(0), "level 0 clamps to level 1");
    Equal(355000, Progression.ExperienceThresholdForLevel(99), "level 99 clamps to level 20");
});

Run("level for experience is exact on both sides of every boundary it is asked about", () =>
{
    Equal(1, Progression.LevelForExperience(0), "0 XP");
    Equal(1, Progression.LevelForExperience(299), "one short of level 2");
    Equal(2, Progression.LevelForExperience(300), "exactly level 2");
    Equal(2, Progression.LevelForExperience(899), "one short of level 3");
    Equal(3, Progression.LevelForExperience(900), "exactly level 3");
    Equal(20, Progression.LevelForExperience(355000), "exactly level 20");
    Equal(20, Progression.LevelForExperience(9_000_000), "far past the cap stays level 20");
});

Run("experience to next level counts down and stops at the cap", () =>
{
    Equal(300, Progression.ExperienceToNextLevel(0), "from 0 XP");
    Equal(1, Progression.ExperienceToNextLevel(299), "one XP short of level 2");
    Equal(600, Progression.ExperienceToNextLevel(300), "band from level 2 to level 3");
    Equal(0, Progression.ExperienceToNextLevel(355000), "at the level cap nothing is owed");
});

Run("the level band width is what the quest default scales off", () =>
{
    Equal(300, Progression.LevelBandWidth(1), "level 1 band");
    Equal(1800, Progression.LevelBandWidth(3), "level 3 band");
    // At the cap there is no next band, so the last real one stands in.
    Equal(50000, Progression.LevelBandWidth(20), "level 20 falls back to the 19-to-20 band");
});

// ---------------------------------------------------------------------------
// Derived Challenge Rating -- the load-bearing derivation
// ---------------------------------------------------------------------------

Run("the sample manifest's Ashen Watcher derives to CR 1/8, matching an SRD Guard at 25 XP", () =>
{
    // Exactly the stats reference-python/demo/sample_campaign_manifest.json authors: AC 12, 9 HP,
    // +3 to hit, 1d6+1. If this creature is ever worth 10 XP, the economy is dead on arrival in
    // every imported campaign and nothing else in this file would notice.
    var watcher = Monster("Ashen Watcher", armorClass: 12, maxHp: 9, attackBonus: 3, damage: "1d6+1");
    Equal("1/8", Progression.DeriveChallengeRating(watcher), "derived challenge rating");
    Equal(25, GameEngine.ExperienceValueOf(watcher), "derived XP value");
});

Run("an orc derives to CR 1/2, matching the real orc at 100 XP", () =>
{
    var orc = Monster("Orc", armorClass: 13, maxHp: 15, attackBonus: 5, damage: "1d12+3");
    Equal("1/2", Progression.DeriveChallengeRating(orc), "derived challenge rating");
    Equal(100, GameEngine.ExperienceValueOf(orc), "derived XP value");
});

Run("a creature with no attacks and no stats still pays the floor, never zero", () =>
{
    var rat = new CharacterSheet { Name = "Rat", CharacterType = "monster", MaxHp = 1, ArmorClass = 10 };
    Equal(Progression.MinimumCreatureExperience, GameEngine.ExperienceValueOf(rat), "floor value");
    True(GameEngine.ExperienceValueOf(rat) > 0, "a defeated hostile must never award nothing");
});

Run("a heavyweight derives high rather than saturating at the floor", () =>
{
    var giant = Monster("Stone Giant", armorClass: 17, maxHp: 126, attackBonus: 9, damage: "3d8+6");
    var value = GameEngine.ExperienceValueOf(giant);
    True(value >= 1100, $"a 126 HP AC 17 heavy hitter should be worth at least CR 4, got {value} XP");
});

Run("an explicit value beats an authored CR, which beats derivation", () =>
{
    var derived = Monster("Plain", armorClass: 12, maxHp: 9, attackBonus: 3, damage: "1d6+1");
    Equal(25, GameEngine.ExperienceValueOf(derived), "derivation is used when nothing is authored");

    var rated = Monster("Rated", armorClass: 12, maxHp: 9, attackBonus: 3, damage: "1d6+1");
    rated.ChallengeRating = "5";
    Equal(1800, GameEngine.ExperienceValueOf(rated), "an authored CR overrides derivation");

    var overridden = Monster("Overridden", armorClass: 12, maxHp: 9, attackBonus: 3, damage: "1d6+1");
    overridden.ChallengeRating = "5";
    overridden.ExperienceValue = 4242;
    Equal(4242, GameEngine.ExperienceValueOf(overridden), "an explicit value overrides an authored CR");
});

Run("authored challenge ratings parse in the forms a human actually writes", () =>
{
    Equal(25, Progression.ExperienceForChallengeRating("1/8"), "fraction form");
    Equal(25, Progression.ExperienceForChallengeRating("0.125"), "decimal form");
    Equal(100, Progression.ExperienceForChallengeRating("CR 1/2"), "prefixed form");
    Equal(155000, Progression.ExperienceForChallengeRating("30"), "the top of the ladder");
    // Unparseable text falls through to the floor rather than throwing; it arrives from imports.
    Equal(Progression.MinimumCreatureExperience, Progression.ExperienceForChallengeRating("deadly"), "garbage");
});

Run("damage expressions average rather than roll, so an XP value is never a matter of luck", () =>
{
    Equal(4.5, Progression.AverageOfDamageExpression("1d6+1"), "1d6+1");
    Equal(9.5, Progression.AverageOfDamageExpression("1d12+3"), "1d12+3");
    Equal(7.0, Progression.AverageOfDamageExpression("2d6"), "2d6");
    Equal(5.0, Progression.AverageOfDamageExpression("5"), "a fixed value");
    Equal(0.0, Progression.AverageOfDamageExpression("not a die"), "malformed input averages zero");
    Equal(0.0, Progression.AverageOfDamageExpression(null), "null averages zero");
});

// ---------------------------------------------------------------------------
// Award arithmetic across a four-PC party
// ---------------------------------------------------------------------------

Run("an award divides evenly across four player characters", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var awards = engine.AwardExperience(campaign, 400, "test", "Test");

    Equal(4, awards.Count, "one award record per player character");
    foreach (var pc in party) Equal(100, pc.ExperiencePoints, $"{pc.Name} XP");
});

Run("division is a function of the real party size, not of the number four", () =>
{
    // Four is the design target and the common case, but nothing in the split may assume it.
    var (sixEngine, sixCampaign, six) = Party(6);
    sixEngine.AwardExperience(sixCampaign, 600, "test", "Test");
    foreach (var pc in six) Equal(100, pc.ExperiencePoints, $"{pc.Name} share of six");

    var (twoEngine, twoCampaign, two) = Party(2);
    twoEngine.AwardExperience(twoCampaign, 600, "test", "Test");
    foreach (var pc in two) Equal(300, pc.ExperiencePoints, $"{pc.Name} share of two");

    var (oneEngine, oneCampaign, one) = Party(1);
    oneEngine.AwardExperience(oneCampaign, 600, "test", "Test");
    Equal(600, one[0].ExperiencePoints, "a solo character takes the whole total");

    // A total that divides evenly by four but not by three must still fully distribute.
    var (threeEngine, threeCampaign, three) = Party(3);
    threeEngine.AwardExperience(threeCampaign, 400, "test", "Test");
    Equal(400, three.Sum(p => p.ExperiencePoints), "every point of a party total is distributed");
    True(three.Max(p => p.ExperiencePoints) - three.Min(p => p.ExperiencePoints) <= 1,
        "and an indivisible total spreads within a single point");
});

Run("the level-scaled quest default follows the real party size too", () =>
{
    Equal(270 * 2, GameEngine.DefaultQuestExperience(3, 2), "party of two");
    Equal(270 * 4, GameEngine.DefaultQuestExperience(3, 4), "party of four");
    Equal(270 * 7, GameEngine.DefaultQuestExperience(3, 7), "party of seven");
    Equal(0, GameEngine.DefaultQuestExperience(3, 0), "no party, no payout");
});

Run("the remainder goes to the lowest totals, deterministically, and pulls the party together", () =>
{
    var (engine, campaign, party) = FourPcParty();
    // Deliberately uneven starting totals, and 402 does not divide by four.
    party[0].ExperiencePoints = 30;
    party[1].ExperiencePoints = 10;
    party[2].ExperiencePoints = 20;
    party[3].ExperiencePoints = 40;

    engine.AwardExperience(campaign, 402, "test", "Test");

    // 402 / 4 = 100 each, remainder 2, to the two lowest: party[1] and party[2].
    Equal(130, party[0].ExperiencePoints, "highest-but-one gets the base share");
    Equal(111, party[1].ExperiencePoints, "lowest gets a remainder point");
    Equal(121, party[2].ExperiencePoints, "second lowest gets a remainder point");
    Equal(140, party[3].ExperiencePoints, "highest gets the base share");

    // Repeating the same award from the same state must produce the same split.
    var (engineB, campaignB, partyB) = FourPcParty();
    partyB[0].ExperiencePoints = 30;
    partyB[1].ExperiencePoints = 10;
    partyB[2].ExperiencePoints = 20;
    partyB[3].ExperiencePoints = 40;
    engineB.AwardExperience(campaignB, 402, "test", "Test");
    for (var i = 0; i < 4; i++)
        Equal(party[i].ExperiencePoints, partyB[i].ExperiencePoints, $"deterministic split for member {i}");
});

Run("a dead player character takes no share and the survivors split the whole total", () =>
{
    var (engine, campaign, party) = FourPcParty();
    party[3].Dead = true;

    engine.AwardExperience(campaign, 300, "test", "Test");

    Equal(100, party[0].ExperiencePoints, "survivor share");
    Equal(100, party[1].ExperiencePoints, "survivor share");
    Equal(100, party[2].ExperiencePoints, "survivor share");
    Equal(0, party[3].ExperiencePoints, "the dead take nothing");
});

Run("a player character at 0 HP still takes a full share", () =>
{
    var (engine, campaign, party) = FourPcParty();
    party[2].CurrentHp = 0;
    party[2].Conditions.Add("Unconscious");

    engine.AwardExperience(campaign, 400, "test", "Test");

    Equal(100, party[2].ExperiencePoints,
        "docking XP from the character who got dropped punishes the player for the thing the fight was about");
});

Run("an award with no living player character is dropped, not banked", () =>
{
    var (engine, campaign, party) = FourPcParty();
    foreach (var pc in party) pc.Dead = true;

    var awards = engine.AwardExperience(campaign, 1000, "test", "Test");
    Equal(0, awards.Count, "nothing is awarded");
    True(campaign.Events.Any(e => e.Type == "experience_unawarded"), "the drop is recorded");

    // And it must not pay out later.
    party[0].Dead = false;
    Equal(0, party[0].ExperiencePoints, "a wipe does not bank a windfall for the next resurrection");
});

Run("monsters and NPCs never take a share", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var bystander = engine.AddCharacter(campaign, new CharacterSheet { Name = "Innkeeper", CharacterType = "npc", MaxHp = 8 });

    engine.AwardExperience(campaign, 400, "test", "Test");

    Equal(0, bystander.ExperiencePoints, "an NPC takes no share");
    Equal(100, party[0].ExperiencePoints, "the party still splits the whole total four ways");
});

// ---------------------------------------------------------------------------
// Threshold crossing
// ---------------------------------------------------------------------------

Run("crossing a threshold banks a level-up and does not apply one", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var levelsBefore = party.Select(p => p.Level).ToArray();

    // 1200 party total = 300 each = exactly the level 2 threshold.
    var awards = engine.AwardExperience(campaign, 1200, "test", "Test");

    for (var i = 0; i < party.Count; i++)
    {
        Equal(300, party[i].ExperiencePoints, "XP at the threshold");
        Equal(1, party[i].PendingLevelUps, "one level banked");
        Equal(levelsBefore[i], party[i].Level, "level is NOT applied at the threshold");
    }
    True(awards.All(a => a.CrossedThreshold), "every award reports the crossing");
    True(campaign.Events.Count(e => e.Type == "level_up_available") == 4, "one availability event per PC");
});

Run("one XP short of the threshold banks nothing", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var awards = engine.AwardExperience(campaign, 1196, "test", "Test");

    Equal(299, party[0].ExperiencePoints, "one short of level 2");
    Equal(0, party[0].PendingLevelUps, "nothing banked");
    True(awards.All(a => !a.CrossedThreshold), "no award reports a crossing");
    Equal(1, awards[0].ExperienceToNextLevel, "the award reports exactly how far is left");
});

Run("an award that skips a whole level banks two, applied one at a time", () =>
{
    var (engine, campaign, party) = FourPcParty();
    // 3600 party total = 900 each = level 3 outright, crossing both thresholds.
    engine.AwardExperience(campaign, 3600, "test", "Test");

    var pc = party[0];
    Equal(900, pc.ExperiencePoints, "XP total");
    Equal(2, pc.PendingLevelUps, "two levels banked");
    Equal(1, pc.Level, "still level 1 until they are applied");

    var first = engine.ApplyLevelUp(campaign, pc.Id);
    Equal(2, first.NewLevel, "first application");
    Equal(1, first.PendingLevelUpsRemaining, "one still waiting");
    var second = engine.ApplyLevelUp(campaign, pc.Id);
    Equal(3, second.NewLevel, "second application");
    Equal(0, second.PendingLevelUpsRemaining, "nothing left");
});

Run("banking stops at the level cap", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var pc = party[0];
    pc.Level = 20;
    pc.ExperiencePoints = 355000;

    engine.AwardExperience(campaign, 4_000_000, "test", "Test");

    Equal(0, pc.PendingLevelUps, "nothing can be banked above level 20");
    True(pc.ExperiencePoints > 355000, "XP still accrues past the cap rather than being clamped away");
});

// ---------------------------------------------------------------------------
// Applying a level
// ---------------------------------------------------------------------------

Run("a level grants hit points, proficiency, and a hit die -- and says what it does not grant", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var pc = party[0];
    pc.HitDieSides = 10;
    pc.Abilities["constitution"] = 16; // +3
    pc.MaxHp = 12;
    pc.CurrentHp = 12;
    pc.HitDiceMaximum = 1;
    pc.HitDiceRemaining = 1;
    pc.PendingLevelUps = 1;

    var result = engine.ApplyLevelUp(campaign, pc.Id);

    // Fixed average of a d10 is 6, plus a +3 Constitution modifier.
    Equal(9, result.HitPointsGained, "hit points gained");
    Equal(21, pc.MaxHp, "new maximum HP");
    Equal(21, pc.CurrentHp, "current HP rises with the maximum");
    Equal(2, pc.Level, "new level");
    Equal(2, pc.ProficiencyBonus, "proficiency bonus at level 2");
    Equal(2, pc.HitDiceMaximum, "hit dice maximum");
    Equal(2, pc.HitDiceRemaining, "hit dice remaining");
    True(result.Summary.Contains("Spell slots", StringComparison.OrdinalIgnoreCase),
        "the result is honest that spell slots and class features are not granted");
});

Run("the proficiency bonus finally moves -- Level stops being write-once", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var pc = party[0];
    pc.PendingLevelUps = 4;
    for (var i = 0; i < 4; i++) engine.ApplyLevelUp(campaign, pc.Id);

    Equal(5, pc.Level, "level after four applications");
    Equal(3, pc.ProficiencyBonus, "proficiency bonus is recomputed from the new level");
});

Run("a level-up is refused during an active encounter the character is fighting in", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var pc = party[0];
    pc.PendingLevelUps = 1;
    pc.CurrentHp = 1;

    var encounter = engine.StartEncounter(campaign, "Ambush");
    engine.AddCombatant(campaign, encounter.Id, pc.Id, side: "party");

    var threw = false;
    try { engine.ApplyLevelUp(campaign, pc.Id); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "a level grants HP, so applying one mid-fight would make being damaged a strategy");
    Equal(1, pc.CurrentHp, "nothing was healed by the refused attempt");
    Equal(1, pc.PendingLevelUps, "and nothing was consumed");
});

Run("a level-up with nothing banked, or on a dead character, is refused", () =>
{
    var (engine, campaign, party) = FourPcParty();

    var threwEmpty = false;
    try { engine.ApplyLevelUp(campaign, party[0].Id); }
    catch (InvalidOperationException) { threwEmpty = true; }
    True(threwEmpty, "nothing pending");

    party[1].PendingLevelUps = 1;
    party[1].Dead = true;
    var threwDead = false;
    try { engine.ApplyLevelUp(campaign, party[1].Id); }
    catch (InvalidOperationException) { threwDead = true; }
    True(threwDead, "dead character");
});

Run("a level-up does not raise a character off 0 hit points", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var pc = party[0];
    pc.CurrentHp = 0;
    pc.PendingLevelUps = 1;

    engine.ApplyLevelUp(campaign, pc.Id);

    Equal(0, pc.CurrentHp, "growth, not a heal");
    True(pc.MaxHp > 10, "the maximum still rose");
});

Run("a Long Rest applies every banked level-up, then restores to the new maximum", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var pc = party[0];
    pc.Abilities["constitution"] = 10; // +0
    pc.HitDieSides = 8;                // fixed average 5
    pc.MaxHp = 10;
    pc.CurrentHp = 4;
    pc.PendingLevelUps = 2;

    engine.LongRest(campaign, pc.Id);

    Equal(3, pc.Level, "both levels applied");
    Equal(20, pc.MaxHp, "two levels of +5 maximum HP");
    Equal(20, pc.CurrentHp, "the rest restores to the NEW maximum, not the old one");
    Equal(0, pc.PendingLevelUps, "nothing left banked");
});

// ---------------------------------------------------------------------------
// Faucet F1 -- defeated opposition
// ---------------------------------------------------------------------------

Run("killing a creature pays the party at the moment it dies", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var watcher = engine.AddCharacter(campaign, Monster("Ashen Watcher", 12, 9, 3, "1d6+1"));

    engine.ApplyDamageDetailed(campaign, watcher.Id, 40);

    True(watcher.Dead, "the watcher died");
    // 25 XP across four PCs: 6 each, remainder 1 to the lowest total.
    Equal(25, party.Sum(p => p.ExperiencePoints), "the party total equals the creature's value");
    True(campaign.Events.Any(e => e.Type == "experience_awarded"), "the award is in the event log");
});

Run("a creature pays exactly once, and damage without death pays nothing", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var watcher = engine.AddCharacter(campaign, Monster("Ashen Watcher", 12, 9, 3, "1d6+1"));

    engine.ApplyDamageDetailed(campaign, watcher.Id, 3);
    Equal(0, party.Sum(p => p.ExperiencePoints), "hitting something is not a faucet");

    engine.ApplyDamageDetailed(campaign, watcher.Id, 40);
    var afterKill = party.Sum(p => p.ExperiencePoints);
    Equal(25, afterKill, "paid on death");
    True(watcher.ExperienceAwarded, "the creature is flagged as having paid");

    // Force the flag's job: a second award attempt on the same creature must do nothing.
    engine.AwardDefeatExperience(campaign, watcher);
    Equal(afterKill, party.Sum(p => p.ExperiencePoints), "a creature never pays twice");
});

Run("a player character's death awards nobody", () =>
{
    var (engine, campaign, party) = FourPcParty();
    engine.SetExhaustion(campaign, party[3].Id, 6);

    True(party[3].Dead, "the PC died of Exhaustion");
    Equal(0, party.Sum(p => p.ExperiencePoints), "PCs are not opposition");
});

// ---------------------------------------------------------------------------
// Faucet F4 -- overcoming without combat
// ---------------------------------------------------------------------------

Run("ending an active encounter pays for the opposition still standing", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var encounter = engine.StartEncounter(campaign, "Mill Watchers");
    var a = engine.AddCharacter(campaign, Monster("Watcher 1", 12, 9, 3, "1d6+1"));
    var b = engine.AddCharacter(campaign, Monster("Watcher 2", 12, 9, 3, "1d6+1"));
    engine.AddCombatant(campaign, encounter.Id, a.Id, side: "opposition");
    engine.AddCombatant(campaign, encounter.Id, b.Id, side: "opposition");

    engine.EndEncounter(campaign, encounter.Id);

    Equal(50, party.Sum(p => p.ExperiencePoints), "talking two watchers down pays what killing them would");
    True(a.ExperienceAwarded && b.ExperienceAwarded, "both are flagged");
});

Run("killing half and talking down the rest pays for each creature exactly once", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var encounter = engine.StartEncounter(campaign, "Mill Watchers");
    var a = engine.AddCharacter(campaign, Monster("Watcher 1", 12, 9, 3, "1d6+1"));
    var b = engine.AddCharacter(campaign, Monster("Watcher 2", 12, 9, 3, "1d6+1"));
    engine.AddCombatant(campaign, encounter.Id, a.Id, side: "opposition");
    engine.AddCombatant(campaign, encounter.Id, b.Id, side: "opposition");

    engine.ApplyDamageDetailed(campaign, a.Id, 40);
    engine.EndEncounter(campaign, encounter.Id);

    Equal(50, party.Sum(p => p.ExperiencePoints), "two creatures, two payouts, not three");
});

Run("ending an encounter twice, or one that was never active, pays nothing", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var encounter = engine.StartEncounter(campaign, "Mill Watchers");
    var a = engine.AddCharacter(campaign, Monster("Watcher 1", 12, 9, 3, "1d6+1"));
    engine.AddCombatant(campaign, encounter.Id, a.Id, side: "opposition");

    engine.EndEncounter(campaign, encounter.Id);
    var afterFirst = party.Sum(p => p.ExperiencePoints);
    Equal(25, afterFirst, "the first end pays");

    engine.EndEncounter(campaign, encounter.Id);
    Equal(afterFirst, party.Sum(p => p.ExperiencePoints), "a second end pays nothing");

    // A planned encounter the party never met.
    var planned = new EncounterState { Name = "Never Met", Status = "planned" };
    var ghost = engine.AddCharacter(campaign, Monster("Ghost", 12, 9, 3, "1d6+1"));
    planned.Combatants.Add(new CombatantState { CharacterId = ghost.Id, Side = "opposition" });
    campaign.Encounters.Add(planned);

    engine.EndEncounter(campaign, planned.Id);
    Equal(afterFirst, party.Sum(p => p.ExperiencePoints), "an encounter that was never active pays nothing");
    True(!ghost.ExperienceAwarded, "and its creatures are not flagged as paid");
});

Run("allies and neutrals in an encounter are not paid out as opposition", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var encounter = engine.StartEncounter(campaign, "Standoff");
    var ally = engine.AddCharacter(campaign, Monster("Hired Sword", 12, 9, 3, "1d6+1"));
    var neutral = engine.AddCharacter(campaign, Monster("Bystander", 12, 9, 3, "1d6+1"));
    engine.AddCombatant(campaign, encounter.Id, ally.Id, side: "party");
    engine.AddCombatant(campaign, encounter.Id, neutral.Id, side: "neutral");

    engine.EndEncounter(campaign, encounter.Id);

    Equal(0, party.Sum(p => p.ExperiencePoints), "neither side pays");
});

// ---------------------------------------------------------------------------
// Faucet F2 -- quests
// ---------------------------------------------------------------------------

Run("completing a quest pays authored XP and, at last, the gold", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var quest = new Quest { Name = "The Mill", Status = "active", RewardExperience = 800, RewardGp = 120 };
    campaign.Quests.Add(quest);

    engine.SetQuestStatus(campaign, quest.Id, "completed");

    Equal(800, party.Sum(p => p.ExperiencePoints), "authored XP is a party total");
    Equal(120, party.Sum(p => p.Gold), "RewardGp is finally granted to somebody");
    True(quest.RewardsGranted, "the quest is flagged as paid");
});

Run("completing a quest twice pays once", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var quest = new Quest { Name = "The Mill", Status = "active", RewardExperience = 800, RewardGp = 120 };
    campaign.Quests.Add(quest);

    engine.SetQuestStatus(campaign, quest.Id, "completed");
    engine.SetQuestStatus(campaign, quest.Id, "active");
    engine.SetQuestStatus(campaign, quest.Id, "completed");

    Equal(800, party.Sum(p => p.ExperiencePoints), "quest status churn is not a faucet");
    Equal(120, party.Sum(p => p.Gold), "and neither is it a gold faucet");
});

Run("a quest that authors no XP still pays, scaled to the party's level band", () =>
{
    var (engine, campaign, party) = FourPcParty();
    foreach (var pc in party) { pc.Level = 3; pc.ExperiencePoints = 900; }
    var quest = new Quest { Name = "Unauthored", Status = "active" };
    campaign.Quests.Add(quest);

    engine.SetQuestStatus(campaign, quest.Id, "completed");

    // 15% of the level 3 band (1800) is 270 per character, across four characters.
    Equal(1080, party.Sum(p => p.ExperiencePoints) - 3600, "imported campaigns are not economically dead");
    Equal(1080, GameEngine.DefaultQuestExperience(3, 4), "the default is a fraction of the band");
    Equal(180, GameEngine.DefaultQuestExperience(1, 4), "and it scales down as well as up");
});

Run("a non-completing status change pays nothing", () =>
{
    var (engine, campaign, party) = FourPcParty();
    var quest = new Quest { Name = "The Mill", Status = "available", RewardExperience = 800 };
    campaign.Quests.Add(quest);

    engine.SetQuestStatus(campaign, quest.Id, "active");
    Equal(0, party.Sum(p => p.ExperiencePoints), "starting a quest is not completing it");
});

// ---------------------------------------------------------------------------
// Faucet F3 -- discovery
// ---------------------------------------------------------------------------

Run("a first reveal pays every player character, and a second reveal pays nothing", () =>
{
    var (engine, campaign, party) = FourPcParty();
    foreach (var pc in party) pc.Level = 3;
    var vault = new WorldLocation { Name = "Sunken Vault" };
    campaign.Locations.Add(vault);

    engine.RevealLocation(campaign, vault.Id);

    // 5 x party level, to EACH character -- exploration does not get thinner with a bigger party.
    foreach (var pc in party) Equal(15, pc.ExperiencePoints, $"{pc.Name} discovery XP");
    True(vault.DiscoveryExperienceAwarded, "the location is flagged");

    engine.RevealLocation(campaign, vault.Id);
    Equal(60, party.Sum(p => p.ExperiencePoints), "re-revealing is not a faucet");
});

Run("discovery is a trickle, not a magnitude -- it stays far under one kill", () =>
{
    var (engine, campaign, party) = FourPcParty();
    foreach (var pc in party) pc.Level = 3;
    var vault = new WorldLocation { Name = "Sunken Vault" };
    campaign.Locations.Add(vault);
    engine.RevealLocation(campaign, vault.Id);

    var perCharacterDiscovery = party[0].ExperiencePoints;
    var perCharacterFromOneCr1Kill = 200 / 4;
    True(perCharacterDiscovery * 3 < perCharacterFromOneCr1Kill,
        $"discovery ({perCharacterDiscovery}) must stay well under a kill ({perCharacterFromOneCr1Kill})");
});

// ---------------------------------------------------------------------------
// Seeding on creation
// ---------------------------------------------------------------------------

Run("a character created above level 1 starts at that level's threshold", () =>
{
    var engine = new GameEngine();
    var campaign = engine.CreateCampaign("Seeded");
    var pc = engine.AddCharacter(campaign, new CharacterSheet { Name = "Veteran", CharacterType = "pc", Level = 5, MaxHp = 40 });

    Equal(6500, pc.ExperiencePoints, "otherwise the next 300 XP would bank a level-up for a level 5 character");
    Equal(0, pc.PendingLevelUps, "and it must not bank one on creation either");
});

Run("nothing in the schema forbids a character being added to a campaign in progress", () =>
{
    // Progression is stored per character, not as one party counter, so adding a character to a
    // running campaign is a supported shape even though no policy for what XP they should get is
    // decided here. What must hold is that the addition does not corrupt anyone's progression.
    var (engine, campaign, party) = FourPcParty();
    engine.AwardExperience(campaign, 1200, "test", "Test");
    var totalsBefore = party.Select(p => p.ExperiencePoints).ToArray();

    var latecomer = engine.AddCharacter(campaign, new CharacterSheet { Name = "Latecomer", CharacterType = "pc", Level = 1, MaxHp = 10 });

    Equal(0, latecomer.ExperiencePoints, "a level 1 character starts at the level 1 threshold");
    for (var i = 0; i < party.Count; i++)
        Equal(totalsBefore[i], party[i].ExperiencePoints, "adding a character disturbs nobody else's total");

    // And from here the enlarged party is simply a party of five.
    engine.AwardExperience(campaign, 500, "test", "Test");
    Equal(100, latecomer.ExperiencePoints, "the new member takes a share like anyone else");
});

// ---------------------------------------------------------------------------
// The LLM boundary
// ---------------------------------------------------------------------------

Run("no DM tool grants experience or applies a level", () =>
{
    var router = new DmToolRouter(new GameEngine(), new DiceService(), new RulesSearchService());
    var suspicious = router.Definitions
        .Where(d => d.Name.Contains("xp", StringComparison.OrdinalIgnoreCase)
                 || d.Name.Contains("experience", StringComparison.OrdinalIgnoreCase)
                 || d.Name.Contains("level_up", StringComparison.OrdinalIgnoreCase))
        .Select(d => d.Name)
        .ToArray();
    Equal(0, suspicious.Length, $"the model must not be able to set the rate of the only currency: {string.Join(", ", suspicious)}");
});

Run("the DM can read progression even though it cannot write it", () =>
{
    var (engine, campaign, party) = FourPcParty();
    engine.AwardExperience(campaign, 400, "test", "Test");
    var router = new DmToolRouter(engine, new DiceService(), new RulesSearchService());

    var result = router.Execute(campaign, "get_character", $$"""{"character_id":"{{party[0].Id}}"}""");
    True(result.Ok, "get_character succeeded");
    // The DM-facing view uses snake_case throughout (spell_save_dc, effective_speed), so these
    // land as experience_points / experience_to_next_level.
    var json = JsonSerializer.Serialize(result.Result);
    True(json.Contains("experience_points", StringComparison.OrdinalIgnoreCase), "XP is visible to the DM");
    True(json.Contains("experience_to_next_level", StringComparison.OrdinalIgnoreCase), "so is the distance to the next level");
    True(json.Contains("100", StringComparison.Ordinal), "and it carries the real value, not a placeholder");
});

// ---------------------------------------------------------------------------
// Persistence: the migration that must not eat a save
// ---------------------------------------------------------------------------

Run("the state schema version has a slot for progression", () =>
{
    True(AppDataStore.CurrentSchemaVersion >= 5, "progression needs a versioned migration slot");
});

Run("a v4 save written before progression existed still loads", () =>
{
    using var dir = new TempDirectory();
    File.WriteAllText(Path.Combine(dir.Path, "state.json"), """
    {
      "schemaVersion": 4,
      "campaigns": [
        {
          "id": "campaign-1",
          "name": "Greenhaven",
          "characters": [
            { "id": "pc-1", "name": "Vera",  "characterType": "pc", "level": 5, "maxHp": 40, "currentHp": 40 },
            { "id": "pc-2", "name": "Bram",  "characterType": "pc", "level": 5, "maxHp": 44, "currentHp": 44 },
            { "id": "pc-3", "name": "Nell",  "characterType": "pc", "level": 5, "maxHp": 38, "currentHp": 38 },
            { "id": "pc-4", "name": "Osric", "characterType": "pc", "level": 5, "maxHp": 41, "currentHp": 41 },
            { "id": "m-1", "name": "Slain Watcher", "characterType": "monster", "level": 1, "maxHp": 9, "currentHp": 0, "dead": true }
          ],
          "quests": [
            { "id": "q-1", "name": "Done Already", "status": "completed", "rewardGp": 100 },
            { "id": "q-2", "name": "Still Open",   "status": "active",    "rewardGp": 100 }
          ],
          "locations": [
            { "id": "l-1", "name": "Old Mill", "discovered": true },
            { "id": "l-2", "name": "Deep Crypt", "discovered": false }
          ]
        }
      ]
    }
    """);

    var state = new AppDataStore(dir.Path).LoadAsync().GetAwaiter().GetResult();
    Equal(AppDataStore.CurrentSchemaVersion, state.SchemaVersion, "migrated schema version");

    var campaign = state.Campaigns.Single();
    Equal(5, campaign.Characters.Count, "every character survived the migration");

    // The substance of the migration: XP is seeded from the level a character already had.
    foreach (var pc in campaign.Characters.Where(c => c.CharacterType == "pc"))
    {
        Equal(6500, pc.ExperiencePoints, $"{pc.Name} is seeded to the level 5 threshold");
        Equal(5, pc.Level, $"{pc.Name} keeps the level they had");
        Equal(0, pc.PendingLevelUps, $"{pc.Name} banks nothing on upgrade");
    }

    // Nothing pre-existing may pay out retroactively.
    Equal(true, campaign.Characters.Single(c => c.Id == "m-1").ExperienceAwarded, "an already-dead creature is settled");
    Equal(true, campaign.Quests.Single(q => q.Id == "q-1").RewardsGranted, "an already-completed quest is settled");
    Equal(false, campaign.Quests.Single(q => q.Id == "q-2").RewardsGranted, "an open quest is left payable");
    Equal(true, campaign.Locations.Single(l => l.Id == "l-1").DiscoveryExperienceAwarded, "a found location is settled");
    Equal(false, campaign.Locations.Single(l => l.Id == "l-2").DiscoveryExperienceAwarded, "an unfound one is left payable");

    // And no gold was moved.
    Equal(0, campaign.Characters.Where(c => c.CharacterType == "pc").Sum(c => c.Gold), "a migration grants nothing");
});

Run("a migrated v4 party does not bank a spurious level-up on its next award", () =>
{
    using var dir = new TempDirectory();
    File.WriteAllText(Path.Combine(dir.Path, "state.json"), """
    {
      "schemaVersion": 4,
      "campaigns": [
        {
          "id": "c", "name": "Greenhaven",
          "characters": [
            { "id": "pc-1", "name": "Vera",  "characterType": "pc", "level": 5, "maxHp": 40, "currentHp": 40 },
            { "id": "pc-2", "name": "Bram",  "characterType": "pc", "level": 5, "maxHp": 44, "currentHp": 44 },
            { "id": "pc-3", "name": "Nell",  "characterType": "pc", "level": 5, "maxHp": 38, "currentHp": 38 },
            { "id": "pc-4", "name": "Osric", "characterType": "pc", "level": 5, "maxHp": 41, "currentHp": 41 }
          ]
        }
      ]
    }
    """);

    var state = new AppDataStore(dir.Path).LoadAsync().GetAwaiter().GetResult();
    var campaign = state.Campaigns.Single();
    new GameEngine().AwardExperience(campaign, 1200, "test", "Test");

    foreach (var pc in campaign.Characters)
    {
        Equal(6800, pc.ExperiencePoints, $"{pc.Name} XP after the award");
        Equal(0, pc.PendingLevelUps,
            $"{pc.Name} must not level from 5 to 6 on a 300 XP award -- this is what the seed prevents");
    }
});

Run("a pre-v1 save migrates the whole chain to progression", () =>
{
    using var dir = new TempDirectory();
    File.WriteAllText(Path.Combine(dir.Path, "state.json"),
        """{ "campaigns": [ { "id": "c", "name": "Legacy", "characters": [ { "id": "p", "name": "Old", "characterType": "pc", "level": 3 } ] } ] }""");

    var state = new AppDataStore(dir.Path).LoadAsync().GetAwaiter().GetResult();
    Equal(AppDataStore.CurrentSchemaVersion, state.SchemaVersion, "schema version after the full chain");
    Equal(900, state.Campaigns.Single().Characters.Single().ExperiencePoints, "seeded through every intermediate version");
});

Run("progression state survives a save and load round trip", () =>
{
    using var dir = new TempDirectory();
    var store = new AppDataStore(dir.Path);

    var campaign = new CampaignState { Id = "c", Name = "Round Trip" };
    campaign.Characters.Add(new CharacterSheet
    {
        Id = "pc-1", Name = "Vera", CharacterType = "pc", Level = 4,
        ExperiencePoints = 3100, PendingLevelUps = 1
    });
    campaign.Characters.Add(new CharacterSheet
    {
        Id = "m-1", Name = "Watcher", CharacterType = "monster",
        ChallengeRating = "1/4", ExperienceValue = 77, ExperienceAwarded = true
    });
    campaign.Quests.Add(new Quest { Id = "q-1", Name = "Paid", RewardExperience = 500, RewardsGranted = true });
    campaign.Locations.Add(new WorldLocation { Id = "l-1", Name = "Mill", Discovered = true, DiscoveryExperienceAwarded = true });

    store.SaveAsync(new AppState { Campaigns = [campaign] }).GetAwaiter().GetResult();
    var reloaded = store.LoadAsync().GetAwaiter().GetResult().Campaigns.Single();

    var pc = reloaded.Characters.Single(c => c.Id == "pc-1");
    Equal(3100, pc.ExperiencePoints, "XP");
    Equal(1, pc.PendingLevelUps, "a banked level-up survives being saved and reopened");
    Equal(4, pc.Level, "level");

    var monster = reloaded.Characters.Single(c => c.Id == "m-1");
    Equal("1/4", monster.ChallengeRating, "authored challenge rating");
    Equal(77, monster.ExperienceValue, "explicit XP value");
    True(monster.ExperienceAwarded, "the paid-out flag survives, so a reload cannot re-kill for XP");

    True(reloaded.Quests.Single().RewardsGranted, "quest payout flag");
    True(reloaded.Locations.Single().DiscoveryExperienceAwarded, "discovery payout flag");

    var raw = File.ReadAllText(Path.Combine(dir.Path, "state.json"));
    using var document = JsonDocument.Parse(raw);
    Equal(AppDataStore.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32(), "persisted schema version");
});

Run("a save-scummed re-kill pays nothing after a reload", () =>
{
    using var dir = new TempDirectory();
    var store = new AppDataStore(dir.Path);
    var engine = new GameEngine();

    var campaign = engine.CreateCampaign("Scum");
    for (var i = 1; i <= 4; i++)
        engine.AddCharacter(campaign, new CharacterSheet { Name = $"PC {i}", CharacterType = "pc", MaxHp = 20 });
    var watcher = engine.AddCharacter(campaign, Monster("Watcher", 12, 9, 3, "1d6+1"));
    engine.ApplyDamageDetailed(campaign, watcher.Id, 40);

    store.SaveAsync(new AppState { Campaigns = [campaign] }).GetAwaiter().GetResult();
    var reloaded = store.LoadAsync().GetAwaiter().GetResult().Campaigns.Single();
    var before = reloaded.Characters.Where(c => c.CharacterType == "pc").Sum(c => c.ExperiencePoints);

    engine.AwardDefeatExperience(reloaded, reloaded.Characters.Single(c => c.Id == watcher.Id));
    var after = reloaded.Characters.Where(c => c.CharacterType == "pc").Sum(c => c.ExperiencePoints);

    Equal(25, before, "the kill paid once before the save");
    Equal(before, after, "and cannot be made to pay again after a reload");
});

// ---------------------------------------------------------------------------

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Progression tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}

Console.WriteLine($"Progression tests passed: {passed}");

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
    }
}

// The design target and the common case. Everything it exercises is written for N members.
static (GameEngine Engine, CampaignState Campaign, List<CharacterSheet> Party) FourPcParty() => Party(4);

static (GameEngine Engine, CampaignState Campaign, List<CharacterSheet> Party) Party(int size)
{
    var names = new[] { "Vera", "Bram", "Nell", "Osric", "Perrin", "Sable", "Tam" };
    var engine = new GameEngine();
    var campaign = engine.CreateCampaign("Progression");
    var party = new List<CharacterSheet>();
    for (var i = 0; i < size; i++)
    {
        party.Add(engine.AddCharacter(campaign, new CharacterSheet
        {
            Name = i < names.Length ? names[i] : $"Adventurer {i + 1}",
            CharacterType = "pc",
            Level = 1,
            MaxHp = 20,
            CurrentHp = 20,
            HitDieSides = 8,
            HitDiceMaximum = 1,
            HitDiceRemaining = 1
        }));
    }
    return (engine, campaign, party);
}

static CharacterSheet Monster(string name, int armorClass, int maxHp, int attackBonus, string damage)
{
    var monster = new CharacterSheet
    {
        Name = name,
        CharacterType = "monster",
        ArmorClass = armorClass,
        MaxHp = maxHp,
        CurrentHp = maxHp,
        AttacksPerAction = 1
    };
    monster.Attacks.Add(new AttackProfile { Name = "Attack", AttackBonus = attackBonus, DamageExpression = damage });
    return monster;
}

static void True(bool value, string label)
{
    if (!value) throw new Exception(label);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dmai-progression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
