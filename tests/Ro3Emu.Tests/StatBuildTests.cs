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
        Assert.Equal(4, Main(3300)); // Sniper: DEX
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
