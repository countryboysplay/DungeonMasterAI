// Golden-file / seeded-replay test, per docs/unity-migration-plan.md 6.2.
//
// Domain, Engine and Data are multi-targeted net10.0;netstandard2.1 so they can load in Unity.
// The other 27 test projects already run twice under -p:UseNetStandardEngine=true and assert the
// same expected values against both engine builds. This project exists for the thing those
// assertions do not do: capture a long scripted run of engine OUTPUT -- not just pass or fail --
// and pin it to a committed file, so that any difference between the two builds is a diff of
// actual values rather than a suite that happens to still be green.
//
// Everything here is deterministic by construction:
//   - dice come from a hand-written LCG defined below, not System.Random. Random's algorithm is a
//     runtime implementation detail and not something to bet cross-target reproducibility on; a
//     dozen lines of unchecked uint arithmetic behave identically on every runtime by definition.
//   - the five engine code paths that construct an unseeded `new DiceService()` internally
//     (GameEngine.cs FinalizeInitiative/NextTurn/CommitCombatMove, GameEngine.PlayerDecisions.cs
//     readied-spell fallback, StealthReady.cs CommitReadiedCombatMove) are avoided or reached only
//     with no battlefield effects present, where they cannot roll anything. Nothing in this script
//     commits a combat move or advances a turn without explicit dice.
//   - no id or timestamp is recorded. Guids and DateTimeOffset.UtcNow differ run to run on a
//     single build, so recording them would make the golden file worthless.
//
// Run with --update to regenerate the golden file after a deliberate engine change. CI never does.

using System.Globalization;
using System.Text;
using System.Text.Json;
using DungeonMasterAI.Data;
using DungeonMasterAI.Domain;
using DungeonMasterAI.Engine;

var update = args.Contains("--update", StringComparer.OrdinalIgnoreCase);
var recorder = new Recorder();

// ---------------------------------------------------------------------------------------------
// A. DiceService: the seeded stream, the expression grammar, and the two regex implementations.
//
// This is the section the migration's riskiest change lives under. On net10.0 the two dice
// regexes come from the [GeneratedRegex] source generator; on netstandard2.1 they are ordinary
// Regex instances built from the same const pattern strings. If those two ever disagree -- about a
// capture group, about IgnoreCase, about what counts as a valid expression -- it shows up here as
// a changed value, not as a silently different roll in a real session.
// ---------------------------------------------------------------------------------------------
var rng = new Lcg(20260903u);
var dice = new DiceService(rng.Next);

recorder.Add("dice.raw-stream", string.Join(",", Enumerable.Range(0, 40).Select(_ => rng.Next(1, 21))));

rng.Reset();
foreach (var expression in new[] { "1d20", "3d6+2", "2d8-1", "1D12+5", " 4d4 ", "7", "100d6", "1d1000" })
{
    var roll = dice.Roll(expression);
    recorder.Add($"dice.roll[{expression}]",
        $"expr='{roll.Expression}' rolls=[{string.Join(",", roll.Rolls)}] mod={roll.Modifier} total={roll.Total}");
}

foreach (var mode in new[] { D20RollMode.Normal, D20RollMode.Advantage, D20RollMode.Disadvantage })
{
    var (one, two, chosen) = dice.RollD20(mode);
    recorder.Add($"dice.d20[{mode}]", $"one={one} two={(two?.ToString(CultureInfo.InvariantCulture) ?? "null")} chosen={chosen}");
}

foreach (var (expression, critical) in new[] { ("1d8+3", false), ("1d8+3", true), ("2d6", true), ("5", true) })
    recorder.Add($"dice.damage[{expression},crit={critical}]", dice.RollDamage(expression, critical).ToString(CultureInfo.InvariantCulture));

for (var armorClass = 8; armorClass <= 20; armorClass += 4)
{
    var attack = dice.Attack(attackModifier: 5, armorClass: armorClass, damageExpression: "1d8+3", mode: D20RollMode.Advantage);
    recorder.Add($"dice.attack[ac={armorClass}]",
        $"d20={attack.D20} mod={attack.Modifier} total={attack.Total} hit={attack.Hit} crit={attack.Critical} dmg={attack.Damage} | {attack.Summary}");
}

// The expression grammar itself, valid and invalid. Both regexes, both capture-group sets.
foreach (var candidate in new[]
{
    "1d20", "d20", "3d6+2", "3d6-2", "1D8", "  2d10+1  ", "0d6", "101d6", "1d1", "1d1001",
    "7", "1000000", "1000001", "", "  ", "2d", "d", "2x6", "1d6+", "1d6++1", "-1d6", "1d6 +1",
})
{
    recorder.Add($"dice.validate['{candidate}']", DiceService.TryValidateExpression(candidate).ToString());
}

foreach (var bad in new[] { "banana", "2d", "1d6+", "1000001", "0d6", "1d1001" })
    recorder.Add($"dice.roll-throws['{bad}']", Describe(() => dice.Roll(bad)));

// ---------------------------------------------------------------------------------------------
// B. The ArgumentNullException.ThrowIfNull -> Guard.NotNull rewrite.
//
// 145 call sites were rewritten mechanically. The rewrite is only correct if every one of them
// still throws ArgumentNullException with the same ParamName, and ParamName is exactly the part a
// mechanical rewrite could get wrong: the real ThrowIfNull captures the argument's source text via
// [CallerArgumentExpression], while Guard.NotNull is handed nameof(x) at the call site.
// ---------------------------------------------------------------------------------------------
recorder.Add("guard.EligiblePartyMembers", Describe(() => GameEngine.EligiblePartyMembers(null!)));
recorder.Add("guard.ExperienceValueOf", Describe(() => GameEngine.ExperienceValueOf(null!)));
recorder.Add("guard.DeriveChallengeRating", Describe(() => Progression.DeriveChallengeRating(null!)));
recorder.Add("guard.ExpectedDamagePerRound", Describe(() => Progression.ExpectedDamagePerRound(null!)));
recorder.Add("guard.TacticalMapGeometry.Validate", Describe(() => TacticalMapGeometry.Validate(null!)));
recorder.Add("guard.CampaignCloneService.Clone", Describe(() => new CampaignCloneService().Clone(null!)));

// ---------------------------------------------------------------------------------------------
// C. Progression. No dice at all on this path, so it should agree across the two targets with
// zero seeding -- which makes any divergence here completely unambiguous.
// ---------------------------------------------------------------------------------------------
for (var level = 1; level <= 20; level++)
{
    recorder.Add($"progression.threshold[{level}]",
        $"xp={Progression.ExperienceThresholdForLevel(level)} band={Progression.LevelBandWidth(level)}");
}

foreach (var xp in new[] { 0, 1, 299, 300, 899, 900, 6499, 48000, 355000, 1_000_000 })
{
    recorder.Add($"progression.level[{xp}]",
        $"level={Progression.LevelForExperience(xp)} toNext={Progression.ExperienceToNextLevel(xp)} " +
        $"fraction={Progression.LevelProgressFraction(xp).ToString("F6", CultureInfo.InvariantCulture)}");
}

foreach (var cr in new[] { "0", "1/8", "1/4", "1/2", "1", "5", "17", "30", "banana", "" })
{
    var ok = Progression.TryExperienceForChallengeRating(cr, out var xp);
    recorder.Add($"progression.cr['{cr}']", $"ok={ok} xp={xp}");
}

var engine = new GameEngine();
var campaign = BuildCampaign(engine);
var party = campaign.Characters.Where(c => c.CharacterType == "pc").ToList();

foreach (var award in engine.AwardExperience(campaign, 1200, "test", "Cleared the crypt"))
    recorder.Add($"xp.award[{award.CharacterName}]", DescribeAward(award));

foreach (var award in engine.AwardExperienceToEachPartyMember(campaign, 250, "test", "Milestone"))
    recorder.Add($"xp.each[{award.CharacterName}]", DescribeAward(award));

// A single award large enough to bank two levels at once, which is the coalesced multi-threshold
// case the progression rules make easy to get wrong.
foreach (var award in engine.AwardExperience(campaign, 8000, "test", "Slew the wyrm"))
    recorder.Add($"xp.bulk[{award.CharacterName}]", DescribeAward(award));

foreach (var pc in party)
{
    while (pc.PendingLevelUps > 0)
    {
        var levelUp = engine.ApplyLevelUp(campaign, pc.Id);
        recorder.Add($"levelup[{levelUp.CharacterName}->{levelUp.NewLevel}]",
            $"gained={levelUp.HitPointsGained} maxHp={levelUp.NewMaxHp} prof={levelUp.ProficiencyBonus} " +
            $"remaining={levelUp.PendingLevelUpsRemaining} | {levelUp.Summary}");
    }
    recorder.Add($"party.final[{pc.Name}]", $"level={pc.Level} xp={pc.ExperiencePoints} maxHp={pc.MaxHp} hp={pc.CurrentHp}");
}

// ---------------------------------------------------------------------------------------------
// D. Combat, driven entirely by the seeded dice above.
// ---------------------------------------------------------------------------------------------
var brute = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Ashen Brute",
    CharacterType = "monster",
    ChallengeRating = "2",
    MaxHp = 45,
    CurrentHp = 45,
    ArmorClass = 14,
    Speed = 30,
    ProficiencyBonus = 2,
    AttacksPerAction = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 16, ["dexterity"] = 10, ["constitution"] = 15, ["wisdom"] = 11,
    },
    SavingThrowProficiencies = ["strength"],
    SkillProficiencies = ["athletics"],
    Attacks = [new AttackProfile { Name = "Maul", AttackBonus = 5, DamageExpression = "2d6+3", DamageType = "Bludgeoning", ReachFeet = 5 }],
});

var quarry = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Crypt Stalker",
    CharacterType = "monster",
    ChallengeRating = "1",
    MaxHp = 30,
    CurrentHp = 30,
    ArmorClass = 13,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 12, ["dexterity"] = 14, ["constitution"] = 12, ["wisdom"] = 10,
    },
});

recorder.Add("combat.cr-derivation", $"brute={Progression.DeriveChallengeRating(brute)} quarry={Progression.DeriveChallengeRating(quarry)}");
recorder.Add("combat.expected-dpr", $"brute={Progression.ExpectedDamagePerRound(brute).ToString("F4", CultureInfo.InvariantCulture)}");
recorder.Add("combat.xp-value", $"brute={GameEngine.ExperienceValueOf(brute)} quarry={GameEngine.ExperienceValueOf(quarry)}");

var encounter = engine.StartEncounter(campaign, "Crypt Ambush");
var bruteCombatant = engine.AddCombatant(campaign, encounter.Id, brute.Id, side: "opposition");
var quarryCombatant = engine.AddCombatant(campaign, encounter.Id, quarry.Id, side: "party");
engine.SetCombatantPosition(campaign, encounter.Id, bruteCombatant.Id, 0, 0);
engine.SetCombatantPosition(campaign, encounter.Id, quarryCombatant.Id, 0, 1);
engine.SetInitiative(campaign, encounter.Id, bruteCombatant.Id, 18);
engine.SetInitiative(campaign, encounter.Id, quarryCombatant.Id, 9);
engine.FinalizeInitiative(campaign, encounter.Id);

recorder.Add("combat.initiative-order",
    string.Join(" > ", campaign.Encounters.Single(e => e.Id == encounter.Id).Combatants
        .Select(c => $"{campaign.Characters.Single(ch => ch.Id == c.CharacterId).Name}:{c.Initiative}")));

for (var swing = 1; swing <= 6 && !quarry.Dead && quarry.CurrentHp > 0; swing++)
{
    var result = engine.ResolveEncounterAttack(campaign, encounter.Id, bruteCombatant.Id, quarryCombatant.Id, "Maul", dice);
    recorder.Add($"combat.attack[{swing}]",
        $"d20={result.Attack.D20} total={result.Attack.Total} hit={result.Attack.Hit} crit={result.Attack.Critical} " +
        $"dmg={result.Attack.Damage} targetHp={quarry.CurrentHp} | {result.Summary}");
    if (!campaign.Encounters.Single(e => e.Id == encounter.Id).Combatants.Single(c => c.Id == bruteCombatant.Id).AttackActionInProgress)
        break;
}

foreach (var (ability, dc, mode) in new[]
{
    ("strength", 12, D20RollMode.Normal),
    ("dexterity", 15, D20RollMode.Advantage),
    ("constitution", 18, D20RollMode.Disadvantage),
})
{
    var save = engine.ResolveSavingThrowWithDice(campaign, brute.Id, ability, dc, dice, mode);
    recorder.Add($"combat.save[{ability},dc{dc},{mode}]", DescribeD20(save));
}

var athletics = engine.ResolveAbilityCheckWithDice(campaign, brute.Id, "strength", 14, dice, skill: "athletics");
recorder.Add("combat.check[athletics]", DescribeD20(athletics));

engine.SetExhaustion(campaign, brute.Id, 3);
var exhausted = engine.ResolveAbilityCheckWithDice(campaign, brute.Id, "strength", 14, dice, skill: "athletics");
recorder.Add("combat.check[athletics,exhaustion3]", DescribeD20(exhausted));
engine.SetExhaustion(campaign, brute.Id, 0);

engine.BeginConcentration(campaign, brute.Id, "Blur");
var concentration = engine.ApplyDamageWithConcentration(campaign, brute.Id, 14, dice, "Force");
recorder.Add("combat.concentration",
    $"damage={concentration.Damage.EffectiveDamage} hp={concentration.Damage.CurrentHp} " +
    $"maintained={concentration.Concentration?.Maintained} dc={concentration.Concentration?.DifficultyClass} " +
    $"save={(concentration.Concentration is null ? "none" : DescribeD20(concentration.Concentration.SavingThrow))}");

// Kill the quarry. A monster at 0 HP dies outright, and the engine pays the defeat XP itself as
// part of the damage resolution -- so the party total before and after is the real assertion here.
var xpBeforeKill = party.Sum(p => p.ExperiencePoints);
var lethal = engine.ApplyDamageDetailed(campaign, quarry.Id, quarry.CurrentHp);
recorder.Add("combat.lethal",
    $"requested={lethal.RequestedDamage} effective={lethal.EffectiveDamage} hp={lethal.CurrentHp} " +
    $"dropped={lethal.DroppedToZero} dead={lethal.Dead} failures={lethal.DeathSaveFailures}");
recorder.Add("xp.defeat-automatic",
    $"before={xpBeforeKill} after={party.Sum(p => p.ExperiencePoints)} awarded={quarry.ExperienceAwarded}");

// The flag that stops a corpse paying twice.
recorder.Add("xp.defeat-repeat",
    $"awards={engine.AwardDefeatExperience(campaign, quarry).Count} total={party.Sum(p => p.ExperiencePoints)}");

// Death saves need a subject that survives reaching 0 HP, which a monster does not: an NPC does.
var acolyte = engine.AddCharacter(campaign, new CharacterSheet
{
    Name = "Crypt Acolyte",
    CharacterType = "npc",
    ChallengeRating = "1/4",
    MaxHp = 16,
    CurrentHp = 16,
    ArmorClass = 12,
    ProficiencyBonus = 2,
    Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = 10, ["dexterity"] = 12, ["constitution"] = 12, ["wisdom"] = 14,
    },
});

var downed = engine.ApplyDamageDetailed(campaign, acolyte.Id, acolyte.CurrentHp);
recorder.Add("combat.downed",
    $"hp={downed.CurrentHp} dropped={downed.DroppedToZero} dead={downed.Dead} failures={downed.DeathSaveFailures}");

for (var attempt = 1; attempt <= 8 && !acolyte.Dead && !acolyte.Stable && acolyte.CurrentHp == 0; attempt++)
{
    var save = engine.ResolveDeathSavingThrowWithDice(campaign, acolyte.Id, dice);
    recorder.Add($"combat.death-save[{attempt}]",
        $"roll={save.Roll} successes={save.Successes} failures={save.Failures} stable={save.Stable} dead={save.Dead} hp={save.CurrentHp} | {save.Summary}");
}
recorder.Add("combat.death-save-outcome", $"dead={acolyte.Dead} stable={acolyte.Stable} hp={acolyte.CurrentHp}");

engine.EndEncounter(campaign, encounter.Id);
recorder.Add("combat.encounter-status", campaign.Encounters.Single(e => e.Id == encounter.Id).Status);

// ---------------------------------------------------------------------------------------------
// E. Serialization through each assembly's own System.Text.Json.
//
// This is the one place where the two legs genuinely run different code that nothing else here
// would catch. net10.0 gets System.Text.Json from its shared framework; netstandard2.1 gets the
// pinned 8.0.5 package. AppDataStore's save file (schema v5) and CampaignCloneService's clone are
// both full round-trips of the entire campaign object graph through that serializer, so if the
// two versions disagree about anything this codebase actually stores, it surfaces here.
// ---------------------------------------------------------------------------------------------
var clone = new CampaignCloneService().Clone(campaign);
recorder.Add("serialize.clone", DescribeCampaign(clone));
recorder.Add("serialize.clone-is-isolated", ReferenceEquals(clone.Characters, campaign.Characters).ToString());

var temporary = Path.Combine(Path.GetTempPath(), "dmai-crosstarget-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new AppDataStore(temporary);
    await store.SaveAsync(new AppState { SchemaVersion = AppDataStore.CurrentSchemaVersion, SelectedCampaignId = campaign.Id, Campaigns = [campaign] });
    var reloaded = await store.LoadAsync();
    recorder.Add("serialize.appstate-schema", reloaded.SchemaVersion.ToString(CultureInfo.InvariantCulture));
    recorder.Add("serialize.appstate-campaigns", reloaded.Campaigns.Count.ToString(CultureInfo.InvariantCulture));
    recorder.Add("serialize.appstate-roundtrip", DescribeCampaign(reloaded.Campaigns.Single()));
    recorder.Add("serialize.appstate-matches-clone", (DescribeCampaign(reloaded.Campaigns.Single()) == DescribeCampaign(clone)).ToString());
}
finally
{
    if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
}

// DmToolRouter parses its arguments with the Engine assembly's own System.Text.Json and hands the
// result back through the same object graph the DM model sees.
var router = new DmToolRouter(engine, dice, new RulesSearchService());
recorder.Add("tools.count", router.Definitions.Count.ToString(CultureInfo.InvariantCulture));
recorder.Add("tools.names", string.Join(",", router.Definitions.Select(d => d.Name)));

foreach (var (tool, arguments) in new[]
{
    ("roll_dice", """{"expression":"3d8+4"}"""),
    ("roll_dice", """{"expression":"not-a-roll"}"""),
    ("roll_dice", "{}"),
    ("get_character", """{"character_id":"does-not-exist"}"""),
    // Deliberately not list_characters: its payload carries the engine-generated character ids,
    // which are fresh Guids on every run and would make this golden file differ from itself.
    ("list_quests", "{}"),
    ("list_locations", "{}"),
    ("no_such_tool", "{}"),
})
{
    var result = router.Execute(campaign, tool, arguments);
    var payload = result.Result is null
        ? "-"
        : JsonSerializer.Serialize(result.Result, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    recorder.Add($"tools.execute[{tool}:{arguments}]",
        $"ok={result.Ok} code={result.ErrorCode ?? "-"} error={result.Error ?? "-"} result={Truncate(payload, 600)}");
}

// ---------------------------------------------------------------------------------------------
// Compare against the golden file.
// ---------------------------------------------------------------------------------------------
var actual = recorder.ToJson();
var sourceGoldenPath = Path.Combine(FindProjectDirectory(), "golden", "engine-replay.json");
// Prefer the committed file over the copy in bin/. The copy is there so the test still works if
// the binary is run detached from the source tree, but reading it first would mean a stale copy
// from an earlier build could fail a run for a reason that has nothing to do with the engine.
var goldenPath = File.Exists(sourceGoldenPath)
    ? sourceGoldenPath
    : Path.Combine(AppContext.BaseDirectory, "golden", "engine-replay.json");

if (update)
{
    Directory.CreateDirectory(Path.GetDirectoryName(sourceGoldenPath)!);
    File.WriteAllText(sourceGoldenPath, actual, new UTF8Encoding(false));
    Console.WriteLine($"Golden file rewritten with {recorder.Count} steps: {sourceGoldenPath}");
    return 0;
}

if (!File.Exists(goldenPath))
{
    Console.Error.WriteLine($"FAIL: golden file missing at {goldenPath}. Run with --update to create it.");
    return 1;
}

var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
if (string.Equals(expected, actual.Replace("\r\n", "\n"), StringComparison.Ordinal))
{
    Console.WriteLine($"Cross-target golden replay: {recorder.Count} recorded steps match {Path.GetFileName(goldenPath)} exactly.");
    return 0;
}

Console.Error.WriteLine("FAIL: the recorded engine replay does not match the golden file.");
foreach (var line in FirstDifferences(expected, actual.Replace("\r\n", "\n"), 25)) Console.Error.WriteLine(line);
var actualPath = Path.Combine(AppContext.BaseDirectory, "engine-replay.actual.json");
File.WriteAllText(actualPath, actual, new UTF8Encoding(false));
Console.Error.WriteLine($"Full actual output written to {actualPath}.");
return 1;

// ---------------------------------------------------------------------------------------------

static string Describe(Action action)
{
    try
    {
        action();
        return "no-throw";
    }
    catch (ArgumentNullException ex)
    {
        return $"ArgumentNullException(ParamName='{ex.ParamName}')";
    }
    catch (Exception ex)
    {
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}

static string Truncate(string value, int limit) =>
    value.Length <= limit ? value : value[..limit] + $"...(+{value.Length - limit} chars)";

static string DescribeAward(ExperienceAward award) =>
    $"amount={award.Amount} total={award.NewTotal} level={award.Level} toNext={award.ExperienceToNextLevel} " +
    $"crossed={award.CrossedThreshold} kind={award.SourceKind} source={award.SourceName} | {award.Summary}";

static string DescribeD20(D20TestResult result) =>
    $"one={result.RollOne} two={(result.RollTwo?.ToString(CultureInfo.InvariantCulture) ?? "null")} chosen={result.ChosenRoll} " +
    $"ability={result.AbilityModifier} prof={result.ProficiencyModifier} exhaustion={result.ExhaustionPenalty} " +
    $"total={result.Total} dc={result.DifficultyClass} success={result.Success} | {result.Summary}";

// A projection, not the raw object graph: ids and timestamps are regenerated per run and would
// make the golden file differ from itself.
static string DescribeCampaign(CampaignState state)
{
    var builder = new StringBuilder();
    builder.Append(CultureInfo.InvariantCulture, $"name={state.Name} system={state.System} day={state.Day} minute={state.MinuteOfDay} ");
    builder.Append(CultureInfo.InvariantCulture, $"characters={state.Characters.Count} encounters={state.Encounters.Count} events={state.Events.Count}; ");
    foreach (var character in state.Characters.OrderBy(c => c.Name, StringComparer.Ordinal))
    {
        builder.Append(CultureInfo.InvariantCulture,
            $"[{character.Name} type={character.CharacterType} lvl={character.Level} xp={character.ExperiencePoints} " +
            $"pending={character.PendingLevelUps} hp={character.CurrentHp}/{character.MaxHp} ac={character.ArmorClass} " +
            $"exhaustion={character.ExhaustionLevel} dead={character.Dead} stable={character.Stable} " +
            $"conditions={string.Join("|", character.Conditions.OrderBy(x => x, StringComparer.Ordinal))} " +
            $"attacks={string.Join("|", character.Attacks.Select(a => $"{a.Name}:{a.AttackBonus}:{a.DamageExpression}"))} " +
            $"awarded={character.ExperienceAwarded}]");
    }
    builder.Append("; events=");
    builder.Append(string.Join("|", state.Events.Select(e => e.Type)));
    return builder.ToString();
}

static string FindProjectDirectory()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && directory.GetFiles("*.csproj").Length == 0) directory = directory.Parent;
    return directory?.FullName ?? AppContext.BaseDirectory;
}

static IEnumerable<string> FirstDifferences(string expected, string actual, int limit)
{
    var expectedLines = expected.Split('\n');
    var actualLines = actual.Split('\n');
    var reported = 0;
    for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length) && reported < limit; i++)
    {
        var e = i < expectedLines.Length ? expectedLines[i] : "(missing)";
        var a = i < actualLines.Length ? actualLines[i] : "(missing)";
        if (string.Equals(e, a, StringComparison.Ordinal)) continue;
        yield return $"  line {i + 1}:";
        yield return $"    golden: {e.Trim()}";
        yield return $"    actual: {a.Trim()}";
        reported++;
    }
}

static CampaignState BuildCampaign(GameEngine engine)
{
    var state = new CampaignState
    {
        Id = "campaign-cross-target-golden",
        Name = "Cross-Target Golden Replay",
        Summary = "A fixed scripted run used to compare the net10.0 and netstandard2.1 engine builds.",
        Tone = "grim",
        PartyName = "The Constant",
    };

    foreach (var (name, constitution) in new[] { ("Vera", 14), ("Bram", 16), ("Nell", 12), ("Osric", 10) })
    {
        engine.AddCharacter(state, new CharacterSheet
        {
            Name = name,
            CharacterType = "pc",
            Level = 1,
            MaxHp = 20,
            CurrentHp = 20,
            ArmorClass = 15,
            HitDieSides = 10,
            HitDiceMaximum = 1,
            HitDiceRemaining = 1,
            ProficiencyBonus = 2,
            Abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["strength"] = 14, ["dexterity"] = 13, ["constitution"] = constitution, ["wisdom"] = 11,
            },
            Attacks = [new AttackProfile { Name = "Longsword", AttackBonus = 5, DamageExpression = "1d8+3", DamageType = "Slashing", ReachFeet = 5 }],
        });
    }

    return state;
}

/// <summary>
/// A 32-bit linear congruential generator, written out longhand on purpose.
/// </summary>
/// <remarks>
/// System.Random would have been shorter, but its algorithm is a runtime implementation detail:
/// .NET 6 changed the unseeded generator outright, and nothing promises Unity's Mono and .NET 10
/// agree forever about the seeded one either. Reproducibility across two targets is the entire
/// point of this file, so the generator is defined here in unchecked uint arithmetic, which is
/// specified to the bit by the language and therefore identical on every runtime that can run it.
/// The constants are Numerical Recipes'.
/// </remarks>
internal sealed class Lcg(uint seed)
{
    private readonly uint _seed = seed;
    private uint _state = seed;

    public void Reset() => _state = _seed;

    public int Next(int minimumInclusive, int maximumExclusive)
    {
        _state = unchecked((_state * 1664525u) + 1013904223u);
        var range = (uint)(maximumExclusive - minimumInclusive);
        // The high bits of an LCG are far better distributed than the low ones.
        return minimumInclusive + (int)((_state >> 8) % range);
    }
}

internal sealed class Recorder
{
    private readonly List<Step> _steps = [];

    public int Count => _steps.Count;

    public void Add(string step, string value) => _steps.Add(new Step(step, value));

    public string ToJson() => JsonSerializer.Serialize(_steps, new JsonSerializerOptions
    {
        WriteIndented = true,
        // The escaping and number formatting here must not vary, because this string is compared
        // byte for byte. It is produced by the TEST assembly's serializer, which is net10.0 in both
        // passes -- the engine's own serializer is exercised through section E's round-trips
        // instead, where a difference would show up as a changed value rather than as noise.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private sealed record Step(string Name, string Value);
}
