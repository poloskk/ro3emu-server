using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>CombatService's step past a wall (auto battle, 3026) on a small invented nav grid.</summary>
public sealed class BlockedStepTests
{
    private const int Size = 30;

    /// <summary>
    /// A 30 x 30 one-block grid (NavGrid's layout: 48-byte header, one 16-byte block entry, 4 bytes per cell), flat at
    /// height 0, with the cells (15, 0..<paramref name="wallTo"/>) closed.
    /// </summary>
    private static NavGrid Grid(int wallTo)
    {
        const int header = 48, cells = header + 16;
        var data = new byte[cells + Size * Size * 4];
        BitConverter.GetBytes(Size).CopyTo(data, 20);
        BitConverter.GetBytes(Size).CopyTo(data, 24);
        BitConverter.GetBytes(Size).CopyTo(data, 28);
        BitConverter.GetBytes(cells).CopyTo(data, header);
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                data[cells + (z * Size + x) * 4] = (byte)(x == 15 && z <= wallTo ? 0 : 0x80);
        string path = Path.Combine(Path.GetTempPath(), $"ro3emu-blocked-step-{Guid.NewGuid():N}.bytes");
        File.WriteAllBytes(path, data);
        try { return NavGrid.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AStepFindsAClearLineAroundTheEndOfAWall()
    {
        var nav = Grid(wallTo: 3);
        Vec3 from = new() { X = 13.5f, Z = 3.5f }, target = new() { X = 17.5f, Z = 3.5f };
        Assert.False(nav.CanWalk(from.X, from.Y, from.Z, target.X, target.Z));
        var spot = CombatService.ClearSpot(nav, from, target);
        Assert.NotNull(spot);
        Assert.NotNull(nav.WalkHeight(spot!.X, spot.Z, 0));
        Assert.True(nav.CanWalk(spot.X, spot.Y, spot.Z, target.X, target.Z));
        Assert.True(MathF.Sqrt((spot.X - from.X) * (spot.X - from.X) + (spot.Z - from.Z) * (spot.Z - from.Z)) <= 10.01f);
    }

    /// <summary>The clear side of a rock is seldom a straight walk away: the client paths there itself.</summary>
    [Fact]
    public void AClearSpotBehindTheWallIsTakenAsWell()
    {
        var nav = Grid(wallTo: 8);
        Vec3 from = new() { X = 14.5f, Z = 4.5f }, target = new() { X = 16.5f, Z = 4.5f };
        var spot = CombatService.ClearSpot(nav, from, target);
        Assert.NotNull(spot);
        Assert.True(spot!.X >= 16, "the only clear line is from the target's side of the wall");
        Assert.False(nav.CanWalk(from.X, from.Y, from.Z, spot.X, spot.Z));
    }

    [Fact]
    public void ALongWallLeavesARandomStep()
    {
        var nav = Grid(wallTo: Size - 1);
        Vec3 from = new() { X = 13.5f, Z = 14.5f }, target = new() { X = 17.5f, Z = 14.5f };
        Assert.Null(CombatService.ClearSpot(nav, from, target));
        var step = CombatService.RandomStepEnd(nav, from, new Random(1));
        Assert.NotNull(step);
        Assert.True(nav.CanWalk(from.X, from.Y, from.Z, step!.X, step.Z));
    }
}
