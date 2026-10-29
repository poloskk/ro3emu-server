using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Population;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Fake attackers walk from where the siege puts them to its crystal (scene point 44) on the nav grid.</summary>
public sealed class SiegePathTests
{
    [ClientDataFact]
    public void AttackersCanWalkFromTheirArrivalAndReviveToTheCrystal()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        var world = new WorldData(options, new GameData(options, NullLogger<GameData>.Instance), NullLogger<WorldData>.Instance);
        var map = world.Map(FakePlayer.SiegeScene)!;
        Assert.True(map.Points.TryGetValue(44, out var crystal));
        var foot = map.Standable(map.Grounded(crystal));
        float reach = MathF.Sqrt((foot.X - crystal.X) * (foot.X - crystal.X) + (foot.Z - crystal.Z) * (foot.Z - crystal.Z));
        Assert.True(reach <= 3, $"the nearest standable cell is {reach:F1} m from the crystal");
        foreach (var from in new[] { map.Born, map.Revive }.OfType<MapPoint>().Select(map.Grounded))
            Assert.True(NavPath.Find(map, from, foot) is { Count: > 0 },
                $"no path from {from.X:F0},{from.Z:F0} to {foot.X:F0},{foot.Z:F0} by the crystal at {crystal.X:F0},{crystal.Z:F0}");
    }
}
