using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Population;

/// <summary>A build a fake player follows: its name, the weights of STR, AGI, VIT, INT, DEX and LUK, and whether it fights
/// on its class's battle mount (a Ranger's bow build fights on foot, its warg following; an auto-warg one rides).</summary>
public sealed record ClassBuild(string Name, int[] Weights, bool Rides = true);

/// <summary>
/// The stats a fake player raises (31500), by its class line, in variations (a population choice, inferred): each line
/// has two builds and each bot keeps one by its role id, so a realm's Rangers or Warlocks are not all alike. The
/// classic ones are Ragnarok Online's; the third-job ones from Ro3Base's community builds (ro3base.com/builds.html,
/// 2026-10-31: its stat spreads at level 99, the weights here). The free points are spent one at a time on the weighted
/// stat furthest behind its share, at BasePointConfig's own cost of each value, up to 99.
/// </summary>
public sealed class StatBuild(GameData data)
{
    private const int MaxStat = 99;

    /// <summary>By the line's first job (JobConfig: 1100 Swordsman ... 6100 Merchant) and whether the line is its
    /// branch (the x210/x211 jobs: Crusader, Sage, Bard and Dancer, Monk, Rogue, and their later jobs).</summary>
    private static ClassBuild[] Plans(long first, bool branch) => (first / 1000, branch) switch
    {
        (1, false) =>
        [
            new("Two-Handed STR", [5, 3, 3, 0, 2, 0]),           // Knight: STR, AGI for attack speed, VIT
            new("Dragon Breath", [28, 0, 99, 80, 0, 0]),         // Ro3Base Rune Knight: VIT 99, INT 80, STR 28
        ],
        (1, true) =>
        [
            new("Holy VIT", [4, 0, 5, 2, 2, 0]),                 // Crusader: VIT first, a little INT for Heal
            new("Aegis Vanguard", [28, 0, 99, 0, 80, 0]),        // Ro3Base Royal Guard: VIT 99, DEX 80, STR 28
        ],
        (2, false) =>
        [
            new("INT/DEX", [0, 0, 1, 6, 4, 0]),                  // Wizard: INT, DEX for cast time
            new("Tetra Cataclysm", [0, 0, 28, 99, 80, 0]),       // Ro3Base Warlock: INT 99, DEX 80, VIT 28
        ],
        (2, true) =>
        [
            new("Battle Sage", [0, 2, 1, 5, 3, 0]),              // Sage: INT, DEX, some AGI
            new("INT/VIT Sage", [0, 0, 3, 5, 3, 0]),             // a sturdier caster
        ],
        (3, false) =>
        [
            new("Auto-Warg", [0, 5, 1, 0, 3, 5]),                // riding Ranger: AGI and LUK (the player's wish), DEX
            new("Apex Predator", [0, 80, 0, 0, 99, 28], Rides: false), // Ro3Base Ranger: DEX 99, AGI 80, LUK 28, bow on foot
        ],
        (3, true) =>
        [
            new("DEX Performer", [0, 3, 1, 2, 5, 0]),            // Bard and Dancer
            new("VIT Performer", [0, 0, 3, 2, 5, 0]),
        ],
        (4, false) =>
        [
            new("Healer", [0, 0, 3, 6, 3, 0]),                   // Priest: INT for Heal, VIT, DEX
            new("Sanctum", [0, 0, 80, 99, 28, 0]),               // Ro3Base Arch Bishop: INT 99, VIT 80, DEX 28
        ],
        (4, true) =>
        [
            new("Combo Monk", [5, 3, 2, 0, 2, 0]),               // Monk: STR, AGI
            new("Asura Monk", [5, 0, 2, 3, 2, 0]),               // STR and INT for the SP of Asura Strike
        ],
        (5, false) =>
        [
            new("Critical Assassin", [4, 5, 1, 0, 1, 2]),        // Assassin: AGI, STR, LUK for crits
            new("Shadow Venom", [80, 99, 0, 0, 0, 28]),          // Ro3Base Guillotine Cross: AGI 99, STR 80, LUK 28
        ],
        (5, true) =>
        [
            new("STR Rogue", [4, 3, 1, 0, 3, 1]),
            new("DEX Rogue", [2, 2, 1, 0, 5, 1]),                // the bow Rogue
        ],
        (6, _) =>
        [
            new("STR/DEX Smith", [5, 1, 2, 0, 3, 1]),            // Blacksmith: STR, DEX
            new("Iron Fortress", [99, 0, 28, 0, 80, 0], Rides: false), // Ro3Base Mechanic: STR 99, DEX 80, VIT 28, axes on foot
        ],
        _ => [new("Novice", [3, 2, 3, 0, 2, 0])],
    };

    /// <summary>The builds of a job's line, found up its JobConfig _iParentId chain.</summary>
    public IReadOnlyList<ClassBuild> Plans(long job)
    {
        var chain = new List<long>();
        for (long c = job; c > 0 && JobConfig.Get(data, c) is { } row && chain.Count < 8; c = row.ParentId) chain.Add(c);
        long first = chain.Count >= 2 ? chain[^2] : job; // the job right below the root (Novice)
        bool branch = chain.Any(c => c % 100 is 10 or 11);
        return Plans(first, branch);
    }

    /// <summary>The build a bot follows in its job's line: one of the line's, by its role id (it keeps it; halved so it
    /// does not follow the id's parity, which picks a two- or one-handed weapon, FakePlayer.Gear).</summary>
    public ClassBuild Pick(long job, long roleId)
    {
        var plans = Plans(job);
        return plans[(int)(Math.Abs(roleId / 2) % plans.Count)];
    }

    /// <summary>The weights of a job line's first build.</summary>
    public int[] For(long job) => Plans(job)[0].Weights;

    /// <summary>Points to raise a stat to <paramref name="value"/> from the value below (BasePointConfig._iStatusPoint).</summary>
    public int Cost(int value) => (int)(BasePointConfig.Get(data, value)?.StatusPoint ?? 0);

    /// <summary>The stat points a character earns from level <paramref name="from"/> to <paramref name="to"/>
    /// (BaseLvConfig._iStatusPoint of each level gained, as CharacterService.TotalPoints counts them).</summary>
    public int LevelPoints(int from, int to)
    {
        int points = 0;
        for (int level = from + 1; level <= to; level++) points += (int)(BaseLvConfig.Get(data, level)?.StatusPoint ?? 0);
        return points;
    }

    /// <summary><paramref name="current"/> raised with <paramref name="points"/> after <paramref name="weights"/>; never lower.</summary>
    public int[] Spend(int[] current, int points, int[] weights)
    {
        var stats = (int[])current.Clone();
        while (true)
        {
            int pick = -1;
            for (int i = 0; i < 6; i++)
            {
                if (weights[i] <= 0 || stats[i] >= MaxStat || Cost(stats[i] + 1) is var cost && (cost <= 0 || cost > points)) continue;
                if (pick < 0 || (double)stats[i] / weights[i] < (double)stats[pick] / weights[pick]) pick = i;
            }
            if (pick < 0) return stats;
            points -= Cost(stats[pick] + 1);
            stats[pick]++;
        }
    }
}
