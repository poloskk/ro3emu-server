using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Population;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The jobs fake players advance into, on the client's own JobConfig and ItemConfig.</summary>
public sealed class WorldPlanJobTests
{
    private static readonly Lazy<(WorldPlan Plan, GameData Data)> Kit = new(() =>
    {
        var options = Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        var data = new GameData(options, NullLogger<GameData>.Instance);
        return (new WorldPlan(data, new WorldData(options, data, NullLogger<WorldData>.Instance)), data);
    });

    /// <summary>The rank-3 jobs with _iNotTransformJob 2 (no weapon lists them) are never a fake player's next job.</summary>
    [ClientDataFact]
    public void NoFakePlayerAdvancesIntoAJobNobodyCanTake()
    {
        var (plan, data) = Kit.Value;
        var random = new Random(7);
        foreach (var job in JobConfig.All(data).Where(j => j.NotTransformJob == 1 || j.Id == 1000))
            for (int i = 0; i < 20; i++)
                if (plan.NextJob(job.Id, 1 + i % 2, random) is { } next)
                    Assert.Equal(1, JobConfig.Get(data, next.Job)!.Value.NotTransformJob);
    }

    /// <summary>A role already in such a job (3210, the archer line's) moves to a sibling a weapon is made for.</summary>
    [ClientDataFact]
    public void ARoleInAnUntakeableJobMovesToASiblingThatHasWeapons()
    {
        var (plan, data) = Kit.Value;
        Assert.Null(plan.TakeableSibling(3200, 1, 5)); // a job anyone can take stays
        var sibling = plan.TakeableSibling(3210, 1, 5);
        Assert.NotNull(sibling);
        Assert.Equal(3100, JobConfig.Get(data, sibling!.Value)!.Value.ParentId);
        Assert.Contains(ItemConfig.All(data), i => i.Type == 5 && i.EquipPart is 1 or 2 && i.JobNeed.Contains(sibling.Value));
    }
}
