using System.Text.Json;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player readied attack commits Reaction and requests supplied d20", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: false);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");

    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    Equal("readied_attack", pending.ResolutionKey, "readied attack pending key");
    Equal(f.Reactor.Id, pending.ActorCharacterId, "reactor owns readied attack roll");
    True(!f.ReactorCombatant.ReactionAvailable, "triggering the readied attack commits the Reaction");
    True(f.ReactorCombatant.ReadiedAction is null, "committed readied action is cleared before dice are shown");
});

Run("supplied readied attack hit pauses for player damage then applies exact damage", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: false);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    var attack = f.Engine.ResolvePendingReadiedAttackRoll(f.Campaign, pending.Id, 19, null, MinimumDice());
    True(attack.Attack.Hit, "supplied d20 should hit");

    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("readied attack damage pending missing");
    Equal("readied_attack_damage", damagePending.ResolutionKey, "hit requests readied attack damage");
    var hpBefore = f.Target.CurrentHp;
    var completed = f.Engine.ResolvePendingReadiedAttackDamageRoll(f.Campaign, damagePending.Id, 7, MinimumDice());
    Equal(hpBefore - 7, f.Target.CurrentHp, "supplied readied attack damage is authoritative");
    Equal(7, completed.Damage?.RequestedDamage, "result preserves supplied damage");
    True(f.Campaign.PendingPlayerRoll is null, "damage completion clears readied attack pending roll");
});

Run("natural 1 readied attack miss ends without damage request", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: false);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    var result = f.Engine.ResolvePendingReadiedAttackRoll(f.Campaign, pending.Id, 1, null, MinimumDice());
    True(!result.Attack.Hit, "natural 1 misses");
    True(f.Campaign.PendingPlayerRoll is null, "miss creates no damage request");
});

Run("readied attack Disadvantage requires two supplied d20 results", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: false);
    f.Reactor.Conditions.Add("Poisoned");
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    Equal("disadvantage", pending.RollMode, "Poisoned readied attack roll mode");

    var rejected = false;
    try { f.Engine.ResolvePendingReadiedAttackRoll(f.Campaign, pending.Id, 18, null, MinimumDice()); }
    catch (InvalidOperationException) { rejected = true; }
    True(rejected, "one d20 cannot satisfy readied attack Disadvantage");
    Equal(pending.Id, f.Campaign.PendingPlayerRoll?.Id, "invalid submission leaves readied attack pending");
    True(!f.ReactorCombatant.ReactionAvailable, "Reaction remains spent after invalid dice submission");

    var result = f.Engine.ResolvePendingReadiedAttackRoll(f.Campaign, pending.Id, 18, 3, MinimumDice());
    Equal(3, result.Attack.D20, "lower supplied d20 is authoritative for Disadvantage");
});

Run("natural 20 readied attack requests critical damage formula", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: false);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    var result = f.Engine.ResolvePendingReadiedAttackRoll(f.Campaign, pending.Id, 20, null, MinimumDice());
    True(result.Attack.Hit && result.Attack.Critical, "natural 20 is critical");
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("critical damage request missing");
    Equal("readied_attack_damage", damagePending.ResolutionKey, "critical readied attack requests damage");
    True(damagePending.Formula.StartsWith("2d6", StringComparison.OrdinalIgnoreCase), "critical formula doubles Blade damage dice");
});

Run("readied attack damage hands off to player Concentration save", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: true);
    f.Engine.BeginConcentration(f.Campaign, f.Target.Id, "Bless");
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    f.Engine.ResolvePendingReadiedAttackRoll(f.Campaign, pending.Id, 19, null, MinimumDice());
    var damagePending = f.Campaign.PendingPlayerRoll ?? throw new Exception("damage pending missing");
    f.Engine.ResolvePendingReadiedAttackDamageRoll(f.Campaign, damagePending.Id, 4, MinimumDice());

    var concentrationPending = f.Campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending missing");
    Equal("concentration_check", concentrationPending.ResolutionKey, "readied damage hands off to Concentration");
    Equal(f.Target.Id, concentrationPending.ActorCharacterId, "damaged PC owns Concentration save");
});

Run("pending readied attack survives campaign JSON serialization", () =>
{
    var f = CreateFixture(playerReactor: true, playerTarget: false);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var pending = f.Engine.RequestReadiedAttackRoll(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id);
    var json = JsonSerializer.Serialize(f.Campaign);
    var restored = JsonSerializer.Deserialize<CampaignState>(json) ?? throw new Exception("campaign restore failed");
    var restoredPending = restored.PendingPlayerRoll ?? throw new Exception("readied attack pending missing after restore");
    Equal(pending.Id, restoredPending.Id, "pending readied attack id survives restore");

    var result = f.Engine.ResolvePendingReadiedAttackRoll(restored, restoredPending.Id, 1, null, MinimumDice());
    True(!result.Attack.Hit, "restored supplied natural 1 resolves as miss");
    True(restored.PendingPlayerRoll is null, "restored readied attack completes cleanly");
});

Run("NPC readied attack retains deterministic automatic resolution", () =>
{
    var f = CreateFixture(playerReactor: false, playerTarget: true);
    f.Engine.TakeReadyAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, f.TargetCombatant.Id, "target enters reach", "Blade");
    var result = f.Engine.TriggerReadiedAttack(f.Campaign, f.Encounter.Id, f.ReactorCombatant.Id, MaximumDice());
    True(result.UsedReaction, "NPC readied attack uses Reaction");
    True(f.Campaign.PendingPlayerRoll is null, "NPC readied attack creates no player attack roll request when target is not Concentrating");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Readied attack player-roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}
Console.WriteLine($"Readied attack player-roll tests passed: {passed}");

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

static Fixture CreateFixture(bool playerReactor, bool playerTarget)
{
    var engine = new GameEngine();
    var reactor = NewCharacter("reactor", playerReactor ? "Sentinel" : "Guard", playerReactor ? "pc" : "monster");
    reactor.Attacks.Add(new AttackProfile
    {
        Name = "Blade",
        AttackBonus = 6,
        DamageExpression = "1d6+3",
        DamageType = "Slashing",
        ReachFeet = 5
    });
    var target = NewCharacter("target", playerTarget ? "Aric" : "Raider", playerTarget ? "pc" : "monster");
    target.ArmorClass = 12;
    target.Abilities["constitution"] = 14;

    var campaign = new CampaignState
    {
        Id = "readied-attack-campaign",
        Name = "Readied Attack Campaign",
        Characters = [reactor, target]
    };
    var encounter = engine.StartEncounter(campaign, "Readied Attack Encounter");
    var reactorCombatant = engine.AddCombatant(campaign, encounter.Id, reactor.Id, side: "party");
    var targetCombatant = engine.AddCombatant(campaign, encounter.Id, target.Id, side: "opposition");
    engine.SetCombatantPosition(campaign, encounter.Id, reactorCombatant.Id, 0, 0);
    engine.SetCombatantPosition(campaign, encounter.Id, targetCombatant.Id, 0, 1);
    engine.SetInitiative(campaign, encounter.Id, reactorCombatant.Id, 20);
    engine.SetInitiative(campaign, encounter.Id, targetCombatant.Id, 10);
    engine.FinalizeInitiative(campaign, encounter.Id);
    return new Fixture(engine, campaign, reactor, target, encounter, reactorCombatant, targetCombatant);
}

static CharacterSheet NewCharacter(string id, string name, string type) => new()
{
    Id = id,
    Name = name,
    CharacterType = type,
    MaxHp = 40,
    CurrentHp = 40,
    ArmorClass = 14,
    Speed = 30,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 12,
        ["dexterity"] = 12,
        ["constitution"] = 12,
        ["wisdom"] = 10,
        ["intelligence"] = 10,
        ["charisma"] = 10
    }
};

static DiceService MinimumDice() => new((min, max) => min);
static DiceService MaximumDice() => new((min, max) => max - 1);

static void True(bool value, string label)
{
    if (!value) throw new Exception(label);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

sealed record Fixture(
    GameEngine Engine,
    CampaignState Campaign,
    CharacterSheet Reactor,
    CharacterSheet Target,
    EncounterState Encounter,
    CombatantState ReactorCombatant,
    CombatantState TargetCombatant);
