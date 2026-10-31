using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Population;

/// <summary>
/// The stats a fake player raises (31500), by its class line: Ragnarok Online's classic builds (a population choice,
/// inferred), as weights of STR, AGI, VIT, INT, DEX and LUK (AttributeConfig 1-6). The free points are spent one at a
/// time on the weighted stat furthest behind its share, at BasePointConfig's own cost of each value, up to 99.
/// </summary>
public sealed class StatBuild(GameData data)
{
    private const int MaxStat = 99;

    /// <summary>By the line's first job (JobConfig: 1100 Swordsman ... 6100 Merchant) and whether the line is its
    /// branch (the x210/x211 jobs: Crusader, Sage, Bard and Dancer, Monk, Rogue, and their later jobs).</summary>
    private static int[] Weights(long first, bool branch) => (first / 1000, branch) switch
    {
        (1, false) => [5, 3, 3, 0, 2, 0], // Knight: STR, AGI for attack speed, VIT
        (1, true) => [4, 0, 5, 2, 2, 0],  // Crusader: VIT first, a little INT for Heal
        (2, false) => [0, 0, 1, 6, 4, 0], // Wizard: INT, DEX for cast time
        (2, true) => [0, 2, 1, 5, 3, 0],  // Sage
        (3, false) => [0, 5, 1, 0, 3, 5], // Hunter: AGI and LUK, an auto-warg Ranger's (the player's wish), DEX
        (3, true) => [0, 3, 1, 2, 5, 0],  // Bard and Dancer
        (4, false) => [0, 0, 3, 6, 3, 0], // Priest: INT for Heal, VIT, DEX
        (4, true) => [5, 3, 2, 0, 2, 0],  // Monk
        (5, false) => [4, 5, 1, 0, 1, 2], // Assassin: AGI, STR, LUK for crits
        (5, true) => [4, 3, 1, 0, 3, 1],  // Rogue
        (6, _) => [5, 1, 2, 0, 3, 1],     // Blacksmith: STR, DEX
        _ => [3, 2, 3, 0, 2, 0],          // Novice
    };

    /// <summary>The weights of a job's line, found up its JobConfig _iParentId chain.</summary>
    public int[] For(long job)
    {
        var chain = new List<long>();
        for (long c = job; c > 0 && JobConfig.Get(data, c) is { } row && chain.Count < 8; c = row.ParentId) chain.Add(c);
        long first = chain.Count >= 2 ? chain[^2] : job; // the job right below the root (Novice)
        bool branch = chain.Any(c => c % 100 is 10 or 11);
        return Weights(first, branch);
    }

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
