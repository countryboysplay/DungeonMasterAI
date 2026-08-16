using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var failures = new List<string>();
var passed = 0;

Run("player damage creates a required Concentration roll without auto-resolving it", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    var damage = engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);

    Equal(12, pc.CurrentHp, "HP after damage");
    True(damage.Concentration is null, "PC Concentration should wait for the player roll");
    Equal("Bless", pc.ConcentrationEffect, "Concentration should remain until the save resolves");
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");
    Equal("concentration_check", pending.ResolutionKey, "resolution key");
    Equal(10, pending.TargetNumber, "Concentration DC");
    Equal(4, pending.Modifier, "static Constitution save modifier");
    True(pending.Required, "Concentration roll should be required");
});

Run("supplied player d20 is authoritative and can break Concentration", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");

    var result = engine.ResolvePendingConcentrationCheckRoll(campaign, pending.Id, 5, null, dice);
    Equal(5, result.SavingThrow.ChosenRoll, "authoritative Concentration d20");
    Equal(9, result.SavingThrow.Total, "Concentration total");
    True(!result.Maintained, "Concentration should fail at 9 vs DC 10");
    True(string.IsNullOrWhiteSpace(pc.ConcentrationEffect), "Concentration effect should end");
    True(campaign.PendingPlayerRoll is null, "pending Concentration roll should clear");
});

Run("supplied player d20 can maintain Concentration", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice);
    var pending = campaign.PendingPlayerRoll ?? throw new Exception("Concentration pending roll missing");

    var result = engine.ResolvePendingConcentrationCheckRoll(campaign, pending.Id, 15, null, dice);
    Equal(19, result.SavingThrow.Total, "Concentration total");
    True(result.Maintained, "Concentration should succeed");
    Equal("Bless", pc.ConcentrationEffect, "Concentration effect should remain");
    True(campaign.PendingPlayerRoll is null, "pending Concentration roll should clear");
});

Run("another required player roll blocks damage before state mutates", () =>
{
    var (engine, campaign, pc) = CreateConcentratingPc();
    var dice = new DiceService((min, max) => min);
    engine.RequestAbilityCheckRoll(campaign, pc.Id, "wisdom", 10);
    var beforeHp = pc.CurrentHp;
    var threw = false;
    try { engine.ApplyDamageWithConcentration(campaign, pc.Id, 8, dice); }
    catch (InvalidOperationException) { threw = true; }
    True(threw, "damage should be blocked while another required roll is unresolved");
    Equal(beforeHp, pc.CurrentHp, "HP must remain unchanged when damage is blocked");
    Equal("Bless", pc.ConcentrationEffect, "Concentration must remain unchanged when damage is blocked");
});

Run("NPC Concentration still resolves immediately", () =>
{
    var engine = new GameEngine();
    var npc = new CharacterSheet
    {
        Id = "npc-caster",
        Name = "Watcher Mage",
        CharacterType = "npc",
        MaxHp = 20,
        CurrentHp = 20,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["constitution"] = 14 },
        SavingThrowProficiencies = ["constitution"]
    };
    var campaign = new CampaignState { Id = "campaign-npc-concentration", Name = "NPC Concentration", Characters = [npc] };
    engine.BeginConcentration(campaign, npc.Id, "Fog Cloud");
    var dice = new DiceService((min, max) => max - 1);

    var damage = engine.ApplyDamageWithConcentration(campaign, npc.Id, 8, dice);
    True(damage.Concentration is not null, "NPC Concentration should resolve immediately");
    True(damage.Concentration!.Maintained, "NPC high deterministic roll should maintain Concentration");
    True(campaign.PendingPlayerRoll is null, "NPC Concentration must not create a player roll");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Concentration roll tests failed: {failures.Count}");
    foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
    Environment.Exit(1);
}

Console.WriteLine($"Concentration roll tests passed: {passed}");

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

static (GameEngine Engine, CampaignState Campaign, CharacterSheet Pc) CreateConcentratingPc()
{
    var engine = new GameEngine();
    var pc = new CharacterSheet
    {
        Id = "pc-caster",
        Name = "Aric",
        CharacterType = "pc",
        MaxHp = 20,
        CurrentHp = 20,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
  ["constitution"] = 14,
  ["wisdom"] = 12
        },
        SavingThrowProficiencies = ["constitution"]
    };
    var campaign = new CampaignState { Id = "campaign-concentration", Name = "Concentration Campaign", Characters = [pc] };
    engine.BeginConcentration(campaign, pc.Id, "Bless");
    return (engine, campaign, pc);
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
