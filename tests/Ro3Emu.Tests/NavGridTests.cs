using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The walkability grid's straight-line check (skills that do not pierce walls, walk checks) on client data.</summary>
public sealed class NavGridTests
{
    private static NavGrid Desert() =>
        NavGrid.Load(Path.Combine(ClientData.RepoRoot, "data", "nav", "sc_moc_fild04_001_dynamicmapdata.bytes"));

    /// <summary>
    /// Sograt Desert (10514): the live client reported y 0 on the +8 m plateau, and every Fire Bolt there was refused as
    /// blocked (3026). The line starts on the walkable layer nearest the reported height, so it passes.
    /// </summary>
    [ClientDataFact]
    public void ALineStartsOnTheGroundNearestTheReportedHeight()
    {
        var nav = Desert();
        Assert.Equal(8f, nav.WalkHeight(65.62f, 54.42f, 0)!.Value, 1);
        Assert.True(nav.CanWalk(65.62f, 0, 54.42f, 70.61f, 47.14f));
    }

    [ClientDataFact]
    public void ALineDownASixteenMetreDropIsStillBlocked()
    {
        var nav = Desert();
        // From the -8 m born point (23, 212) to the +8 m plateau where the player fought: some cell on the way rises
        // more than the client's step tolerance unless the ground ramps up, so a straight line across is checked.
        Assert.Equal(-8f, nav.WalkHeight(23, 212, 0)!.Value, 1);
        Assert.False(nav.CanWalk(23, 0, 212, 65.62f, 54.42f));
    }
}
