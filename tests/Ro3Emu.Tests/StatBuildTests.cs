using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Population;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>The stat builds fake players spend their points on, on the client's own JobConfig and BasePointConfig.</summary>
public sealed class StatBuildTests
{
    private static readonly Lazy<StatBuild> Build = new(() => new StatBuild(new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance)));

    private static readonly int[] Fresh = [1, 1, 1, 1, 1, 1];

    private int Spent(int[] from, int[] to) =>
        Enumerable.Range(0, 6).Sum(i => Enumerable.Range(from[i] + 1, to[i] - from[i]).Sum(Build.Value.Cost));

    /// <summary>Every class line has two builds and bots split between them by role id; the third jobs' second ones are
    /// Ro3Base's (a Warlock's INT 99 / DEX 80 / VIT 28), and the bow Ranger's fights on foot.</summary>
    [ClientDataFact]
    public void EachLineHasTwoBuildsThatBotsSplitBetween()
    {
        var build = Build.Value;
        foreach (long job in new long[] { 1400, 1410, 2400, 2210, 3400, 3211, 4400, 4210, 5400, 5210, 6400 })
        {
            var plans = build.Plans(job);
            Assert.Equal(2, plans.Count);
            Assert.Equal(plans.Count, Enumerable.Range(100000, 40).Select(id => build.Pick(job, id).Name).Distinct().Count());
        }
        var warlock = build.Plans(2400)[1];
        Assert.Equal("Tetra Cataclysm", warlock.Name);
        var stats = build.Spend(Fresh, 600, warlock.Weights);
        Assert.Equal(3, Array.IndexOf(stats, stats.Max())); // INT
        Assert.True(stats[4] > stats[2] && stats[2] > 1 && stats[0] == 1, string.Join('/', stats)); // DEX over VIT, no STR
        Assert.Contains(build.Plans(3400), p => !p.Rides);
        Assert.Contains(build.Plans(3400), p => p.Rides);
    }

    [ClientDataFact]
    public void AnArchbishopPutsItsPointsInIntelligenceAndStaysWithinThem()
    {
        var build = Build.Value;
        var stats = build.Spend(Fresh, 1200, build.For(4400)); // STR AGI VIT INT DEX LUK
        Assert.Equal(stats.Max(), stats[3]); // INT first: Heal and the buffs scale with it
        Assert.True(stats[2] > 1 && stats[4] > 1, "some VIT and DEX");
        Assert.Equal(1, stats[0]); // no STR
        Assert.InRange(Spent(Fresh, stats), 1, 1200);
    }

    [ClientDataFact]
    public void EachLineHasItsOwnMainStat()
    {
        var build = Build.Value;
        int Main(long job) => Array.IndexOf(build.For(job), build.For(job).Max());
        Assert.Equal(0, Main(1300)); // Lord Knight: STR
        Assert.Equal(2, Main(1310)); // Paladin: VIT
        Assert.Equal(3, Main(2300)); // High Wizard: INT
        Assert.Equal(1, Main(3300)); // Sniper: AGI (and LUK), the auto-warg build
        Assert.Equal(build.For(3400)[1], build.For(3400)[5]);
        Assert.Equal(1, Main(5300)); // Assassin Cross: AGI
        Assert.Equal(0, Main(4210)); // Monk: STR
    }

    [ClientDataFact]
    public void PointsAreAddedNeverTakenAway()
    {
        var build = Build.Value;
        int[] earlier = [30, 1, 40, 1, 1, 1]; // spent before on another build
        var stats = build.Spend(earlier, 300, build.For(2200));
        Assert.All(Enumerable.Range(0, 6), i => Assert.True(stats[i] >= earlier[i]));
        Assert.True(stats[3] > 1);
    }

    /// <summary>A level-capped character's points (FakePlayer.Boost) go on the build: high main stats, not six at 99.</summary>
    [ClientDataFact]
    public void ALevelCappedBuildRaisesItsMainStatsNotAllSix()
    {
        var build = Build.Value;
        int points = build.LevelPoints(1, 99);
        Assert.True(points > 0);
        var stats = build.Spend(Fresh, points, build.For(4400));
        Assert.Equal(1, stats[0]); // still no STR
        Assert.True(stats[3] >= 90, $"INT {stats[3]}");
        Assert.InRange(Spent(Fresh, stats), points - 200, points);
    }
}
