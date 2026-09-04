namespace DungeonMasterAI.Domain;

/// <summary>
/// The XP economy's fixed tables and its pure derivations, specified in
/// <c>docs/progression-direction.md</c>.
///
/// This lives in Domain rather than Engine because the persistence layer needs the level
/// thresholds to migrate a save file, and Data references only Domain. Everything here is a pure
/// function of its arguments: no dice, no clock, no randomness. Given the same save file and the
/// same action, the same XP is awarded.
/// </summary>
public static class Progression
{
    public const int MaximumLevel = 20;

    /// <summary>SRD 5.2.1 XP thresholds, indexed by level. Index 0 is unused padding.</summary>
    private static readonly int[] LevelThresholds =
    [
        0,
        0, 300, 900, 2700, 6500,
        14000, 23000, 34000, 48000, 64000,
        85000, 100000, 120000, 140000, 165000,
        195000, 225000, 265000, 305000, 355000
    ];

    /// <summary>
    /// The Challenge Rating ladder, in ascending order, paired with SRD encounter XP. The index
    /// into this array is the unit the derivation in <see cref="DeriveChallengeRating"/> works in:
    /// "one rung up" is a meaningful, evenly-spaced step, where CR itself is not.
    /// </summary>
    private static readonly (string Label, int Experience)[] ChallengeLadder =
    [
        ("0", 10), ("1/8", 25), ("1/4", 50), ("1/2", 100),
        ("1", 200), ("2", 450), ("3", 700), ("4", 1100), ("5", 1800),
        ("6", 2300), ("7", 2900), ("8", 3900), ("9", 5000), ("10", 5900),
        ("11", 7200), ("12", 8400), ("13", 10000), ("14", 11500), ("15", 13000),
        ("16", 15000), ("17", 18000), ("18", 20000), ("19", 22000), ("20", 25000),
        ("21", 33000), ("22", 41000), ("23", 50000), ("24", 62000), ("25", 75000),
        ("26", 90000), ("27", 105000), ("28", 120000), ("29", 135000), ("30", 155000)
    ];

    /// <summary>Defensive CR by hit points, with the AC the ladder rung expects. (upper bound, rung, expected AC)</summary>
    private static readonly (int MaxHitPoints, int Rung, int ExpectedArmorClass)[] DefensiveTable =
    [
        (6, 0, 13), (35, 1, 13), (49, 2, 13), (70, 3, 13), (85, 4, 13), (100, 5, 13),
        (115, 6, 13), (130, 7, 14), (145, 8, 15), (160, 9, 15), (175, 10, 15),
        (190, 11, 16), (205, 12, 16), (220, 13, 17), (235, 14, 17), (250, 15, 17),
        (265, 16, 18), (280, 17, 18), (295, 18, 18), (310, 19, 18), (325, 20, 19),
        (340, 21, 19), (355, 22, 19), (400, 23, 19), (445, 24, 19), (490, 25, 19),
        (535, 26, 19), (580, 27, 19), (625, 28, 19), (670, 29, 19), (715, 30, 19),
        (760, 31, 19), (805, 32, 19), (int.MaxValue, 33, 19)
    ];

    /// <summary>Offensive CR by damage per round, with the attack bonus the rung expects.</summary>
    private static readonly (double MaxDamagePerRound, int Rung, int ExpectedAttackBonus)[] OffensiveTable =
    [
        (1, 0, 3), (3, 1, 3), (5, 2, 3), (8, 3, 3), (14, 4, 3), (20, 5, 3),
        (26, 6, 4), (32, 7, 5), (38, 8, 6), (44, 9, 6), (50, 10, 6),
        (56, 11, 7), (62, 12, 7), (68, 13, 7), (74, 14, 8), (80, 15, 8),
        (86, 16, 8), (92, 17, 8), (98, 18, 8), (104, 19, 9), (110, 20, 10),
        (116, 21, 10), (122, 22, 10), (140, 23, 10), (158, 24, 11), (176, 25, 11),
        (194, 26, 11), (212, 27, 12), (230, 28, 12), (248, 29, 12), (266, 30, 13),
        (284, 31, 13), (302, 32, 13), (double.MaxValue, 33, 14)
    ];

    /// <summary>
    /// A defeated hostile never pays zero. A zero award is worse than a small one: it teaches the
    /// player that the number is decorative.
    /// </summary>
    public const int MinimumCreatureExperience = 10;

    private static readonly string[] CompletingQuestStatuses =
        ["completed", "complete", "done", "finished", "resolved"];

    public static int ExperienceThresholdForLevel(int level) =>
        LevelThresholds[Math.Clamp(level, 1, MaximumLevel)];

    public static int LevelForExperience(int experiencePoints)
    {
        var level = 1;
        for (var candidate = MaximumLevel; candidate >= 1; candidate--)
        {
            if (experiencePoints < LevelThresholds[candidate]) continue;
            level = candidate;
            break;
        }
        return level;
    }

    /// <summary>XP still owed before the next threshold; 0 at the level cap.</summary>
    public static int ExperienceToNextLevel(int experiencePoints)
    {
        var level = LevelForExperience(experiencePoints);
        if (level >= MaximumLevel) return 0;
        return Math.Max(0, LevelThresholds[level + 1] - experiencePoints);
    }

    /// <summary>
    /// How far through the current level band a total sits, 0.0 to 1.0. The presentation layer
    /// binds this rather than recomputing it, so one definition of "the bar" exists.
    /// </summary>
    public static double LevelProgressFraction(int experiencePoints)
    {
        var level = LevelForExperience(experiencePoints);
        if (level >= MaximumLevel) return 1.0;
        var floor = LevelThresholds[level];
        var band = LevelThresholds[level + 1] - floor;
        if (band <= 0) return 1.0;
        return Math.Clamp((experiencePoints - floor) / (double)band, 0.0, 1.0);
    }

    /// <summary>
    /// The width of the level band a party at <paramref name="level"/> is climbing. The
    /// level-scaled quest default is a fraction of this, which is what lets one constant hold from
    /// level 1 to level 20 without per-level tuning.
    /// </summary>
    public static int LevelBandWidth(int level)
    {
        var clamped = Math.Clamp(level, 1, MaximumLevel);
        // At the cap there is no next band, so the last real one stands in for it.
        if (clamped >= MaximumLevel) return LevelThresholds[MaximumLevel] - LevelThresholds[MaximumLevel - 1];
        return LevelThresholds[clamped + 1] - LevelThresholds[clamped];
    }

    public static bool IsCompletingQuestStatus(string? status)
    {
        var value = (status ?? "").Trim();
        return CompletingQuestStatuses.Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps authored Challenge Rating text to SRD encounter XP. Accepts the fraction forms
    /// ("1/8"), their decimal equivalents ("0.125"), a bare integer, and a "CR " prefix, because
    /// this value can arrive from an imported campaign file written by a human.
    /// </summary>
    public static bool TryExperienceForChallengeRating(string? challengeRating, out int experience)
    {
        experience = 0;
        var value = (challengeRating ?? "").Trim();
        if (value.Length == 0) return false;
        if (value.StartsWith("cr", StringComparison.OrdinalIgnoreCase)) value = value[2..].Trim();
        value = value.TrimStart(':').Trim();
        if (value.Length == 0) return false;

        value = value switch
        {
            "0.125" or ".125" => "1/8",
            "0.25" or ".25" => "1/4",
            "0.5" or ".5" => "1/2",
            _ => value
        };

        foreach (var (label, xp) in ChallengeLadder)
        {
            if (!label.Equals(value, StringComparison.OrdinalIgnoreCase)) continue;
            experience = xp;
            return true;
        }
        return false;
    }

    public static int ExperienceForChallengeRating(string? challengeRating) =>
        TryExperienceForChallengeRating(challengeRating, out var xp) ? xp : MinimumCreatureExperience;

    /// <summary>
    /// Derives a Challenge Rating from stats the domain actually carries.
    ///
    /// This is the load-bearing piece of the whole economy. Nothing in the domain and nothing in
    /// any campaign manifest records a CR: the sample manifest's Ashen Watcher has an AC, hit
    /// points, an attack bonus and a damage expression, and that is all. A design that read CR off
    /// the sheet would award the floor for every creature in every imported campaign forever,
    /// while compiling and passing every other test.
    ///
    /// The method is the standard one: a defensive rung from hit points adjusted by AC, an
    /// offensive rung from expected damage per round adjusted by attack bonus, averaged. It is
    /// validated against known SRD monsters in the progression tests -- an Ashen Watcher derives
    /// to CR 1/8 (25 XP, matching a Guard) and an orc to CR 1/2 (100 XP, matching the real orc).
    /// </summary>
    public static string DeriveChallengeRating(CharacterSheet character)
    {
        Guard.NotNull(character, nameof(character));

        var hitPoints = Math.Max(1, character.MaxHp);
        var defensive = DefensiveTable.First(entry => hitPoints <= entry.MaxHitPoints);
        // Integer division truncates toward zero in both directions, which is exactly the
        // "for every FULL 2 points" rule: one point of AC either way changes nothing.
        var defensiveRung = defensive.Rung + (character.ArmorClass - defensive.ExpectedArmorClass) / 2;

        var damagePerRound = ExpectedDamagePerRound(character);
        var offensive = OffensiveTable.First(entry => damagePerRound <= entry.MaxDamagePerRound);
        var bestAttackBonus = character.Attacks.Count == 0
            ? offensive.ExpectedAttackBonus
            : character.Attacks.Max(a => a.AttackBonus);
        var offensiveRung = offensive.Rung + (bestAttackBonus - offensive.ExpectedAttackBonus) / 2;

        var top = ChallengeLadder.Length - 1;
        defensiveRung = Math.Clamp(defensiveRung, 0, top);
        offensiveRung = Math.Clamp(offensiveRung, 0, top);

        // Integer division rounds a tie down. Rounding up is the wrong default: across a whole
        // campaign a systematic round-up compounds into a materially faster curve.
        return ChallengeLadder[(defensiveRung + offensiveRung) / 2].Label;
    }

    /// <summary>
    /// Expected damage per round: the strongest configured attack's average damage, times the
    /// number of attacks the Attack action grants. Average, never rolled -- an XP value that
    /// depended on a die would make the economy non-deterministic.
    /// </summary>
    public static double ExpectedDamagePerRound(CharacterSheet character)
    {
        Guard.NotNull(character, nameof(character));
        if (character.Attacks.Count == 0) return 0;
        var best = character.Attacks.Max(a => AverageOfDamageExpression(a.DamageExpression));
        return Math.Max(0, best) * Math.Max(1, character.AttacksPerAction);
    }

    /// <summary>
    /// Average value of a dice expression in the grammar DiceService accepts (<c>NdS+M</c>, or a
    /// fixed integer). Anything unparseable averages 0 rather than throwing: this runs against
    /// imported and model-supplied strings, and a malformed damage expression must not be able to
    /// prevent a creature from dying.
    /// </summary>
    public static double AverageOfDamageExpression(string? expression)
    {
        var raw = (expression ?? "").Trim();
        if (raw.Length == 0) return 0;
        if (int.TryParse(raw, out var fixedValue)) return Math.Max(0, fixedValue);

        var dice = raw.IndexOf('d', StringComparison.OrdinalIgnoreCase);
        if (dice < 0) return 0;

        var countText = raw[..dice].Trim();
        var count = 1;
        if (countText.Length > 0 && !int.TryParse(countText, out count)) return 0;
        if (count is < 1 or > 100) return 0;

        var rest = raw[(dice + 1)..].Trim();
        var modifier = 0;
        var sign = rest.IndexOfAny(['+', '-']);
        if (sign >= 0)
        {
            if (!int.TryParse(rest[sign..], out modifier)) return 0;
            rest = rest[..sign].Trim();
        }

        if (!int.TryParse(rest, out var sides) || sides is < 2 or > 1000) return 0;
        return Math.Max(0, count * ((sides + 1) / 2.0) + modifier);
    }
}
