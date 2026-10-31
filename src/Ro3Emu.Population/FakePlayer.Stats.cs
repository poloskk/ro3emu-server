using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player spends its stat points on its class line's build (StatBuild) through the client's 31500, as a player
/// does: after each level it gains (a few minutes after logging in, so logins stay light; the roster remembers the
/// level spent for, so a login at the same level sends nothing). The first 31500 asks for nothing and reads the plan
/// back (its values and free points); the second raises the stats.
/// </summary>
public sealed partial class FakePlayer
{
    private const int StatPlan = 0; // fake players never switch stat plans
    private DateTime _statsAfter;

    private async Task StatsStepAsync(CancellationToken ct)
    {
        if (entry.StatsLevel == entry.Level || DateTime.UtcNow < _statsAfter) return;
        entry.StatsLevel = entry.Level;
        ctx.Roster.Save();
        var plan = AllocateAttrPointRsp.Parser.ParseFrom(await RequestAsync(CharacterService.AllocateAttrPoints,
            new AllocateAttrPointReq { PlanIndex = StatPlan }, ct)).PlanBaseProperty;
        if (plan == null || plan.AttrPoints <= 0) return;
        var current = Enumerable.Range(1, 6).Select(i => plan.BaseProperty.GetValueOrDefault(i, 1)).ToArray();
        var wanted = ctx.Stats.Spend(current, plan.AttrPoints, Build.Weights);
        if (wanted.SequenceEqual(current)) return;
        var req = new AllocateAttrPointReq { PlanIndex = StatPlan };
        for (int i = 0; i < 6; i++) req.Points[i + 1] = wanted[i];
        int result = AllocateAttrPointRsp.Parser.ParseFrom(await RequestAsync(CharacterService.AllocateAttrPoints, req, ct)).Result;
        if (result == 0) PopulationContext.Log($"{entry.Name}: stats {string.Join('/', wanted)} ({Build.Name})");
    }

    /// <summary>The build this bot follows in its job's line (StatBuild.Pick): its stats and whether it rides.</summary>
    private ClassBuild Build => ctx.Stats.Pick(_job, entry.RoleId);
}
