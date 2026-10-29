using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Population;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>The gear and talents fake players grow into, resolved from the exported client data.</summary>
public sealed class GearPlanTests
{
    private static readonly Lazy<(GearPlan Plan, GameData Data)> Kit = new(() =>
    {
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }),
            NullLogger<GameData>.Instance);
        return (new GearPlan(data), data);
    });

    /// <summary>Each class wears its own weapon and the armour anyone may wear, at its level, every grid filled.</summary>
    [ClientDataFact]
    public void AKnightWearsItsLevelsWeaponAndArmourInEveryGrid()
    {
        var (plan, data) = Kit.Value;
        var set = plan.For(1300, 90, 4, twoHanded: false);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], set.Select(p => p.Grid).Order());
        Assert.All(set, p =>
        {
            var row = ItemConfig.Get(data, p.Item)!.Value;
            Assert.True(row.LevelNeed is <= 90 and >= 70, $"{p.Item} level {row.LevelNeed}");
            Assert.True(row.JobNeed.Length == 0 || row.JobNeed.Contains(0) || row.JobNeed.Contains(1300), $"{p.Item} for a Lord Knight");
        });
        var twoHanded = plan.For(1300, 90, 4, twoHanded: true);
        Assert.DoesNotContain(twoHanded, p => p.Grid == 2); // a two-handed weapon leaves the off-hand empty
        Assert.NotEmpty(plan.For(2300, 40, 3, twoHanded: false)); // a High Wizard at 40
        Assert.All(plan.For(1300, 20, 4, false), p => Assert.True(ItemConfig.Get(data, p.Item)!.Value.LevelNeed <= 20));
    }

    [ClientDataFact]
    public void StrengtheningCostsItsTableAndStopsAtTheBaseLevel()
    {
        var (plan, _) = Kit.Value;
        var cost = plan.StrengthenCost(1, 0, 2, 99);
        Assert.NotNull(cost);
        Assert.Contains(cost!, c => c.Item == 2 && c.Count > 0); // Minted Coin with the diamonds
        Assert.Null(plan.StrengthenCost(1, 40, 1, 30)); // level 41 needs base 41 (StrengthenTable _iLev)
    }

    /// <summary>Talents in the order a player opens them (start points first), only plain stat nodes, each with a price.</summary>
    [ClientDataFact]
    public void TalentsStartAtTheTreesStartPointsAndHaveAPrice()
    {
        var (plan, data) = Kit.Value;
        var talents = plan.Talents(1300);
        Assert.NotEmpty(talents);
        Assert.Equal(1, SeasonTalentConfig.Get(data, talents[0].Id)!.Value.IsStartPoint);
        Assert.All(talents.Take(10), n => Assert.NotNull(plan.TalentCost(n, 1, 1300)));
    }

    /// <summary>A bot drinks its level's HP potion: the level 90 one at 90, the level 50 one at 55, a level 1 one below 30.</summary>
    [ClientDataFact]
    public void ABotsPotionIsItsLevelsHpPotion()
    {
        var (plan, data) = Kit.Value;
        int high = plan.Potion(90), mid = plan.Potion(55), low = plan.Potion(10);
        Assert.Equal((90L, 50L, 1L), (ItemConfig.Get(data, high)!.Value.LevelNeed, ItemConfig.Get(data, mid)!.Value.LevelNeed,
            ItemConfig.Get(data, low)!.Value.LevelNeed));
        Assert.All(new[] { high, mid, low }, p => Assert.Equal("100", ItemConfig.Get(data, p)!.Value.UseEffect!.Value[0][0].ToString()));
    }
}
