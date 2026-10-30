using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player in a real player's party fights beside it at its best, once per job: its job level at the cap (GM !lvl
/// clamps it to JobLvConfig _iMaxJobLv; the job level is what earns skill points, SkillService.SkillPoints), its skills
/// learned again at their caps with those points, and the stat points of a character at the level cap (GM !points: what
/// BaseLvConfig gives from its level to WorldPlan.MaxLevel) spent on its build (31500), as a real player's finished
/// build: its main stats high, not all six at 99. A population choice (inferred): the original server had only real
/// players.
/// </summary>
public sealed partial class FakePlayer
{
    /// <summary>Above every job's _iMaxJobLv: the server clamps it.</summary>
    private const int AnyMaxJobLevel = 999;
    private long _boostedJob = -1;

    private async Task BoostStepAsync(CancellationToken ct)
    {
        if (!InParty || _boostedJob == _job || entry.Level == 0 || !entry.Prepared) return;
        _boostedJob = _job;
        if (await ctx.GmAsync(entry.Account, $"!lvl {entry.Level} {AnyMaxJobLevel}", ct, ownRole: true))
        {
            entry.JobLevel = AnyMaxJobLevel; // the next !lvl sends it again; the server keeps the cap
            _supportJob = -1; // LearnSkillsAsync again, with the new points
        }
        await MaxStatsAsync(ct);
        ctx.Roster.Save();
        PopulationContext.Log($"{entry.Name}: boosted for its party (job {_job}: job level and skills at their caps, a level-capped build)");
    }

    /// <summary>The free points topped up to a level-capped character's (once: the bonus stays), then spent on the build.</summary>
    private async Task MaxStatsAsync(CancellationToken ct)
    {
        var plan = AllocateAttrPointRsp.Parser.ParseFrom(await RequestAsync(CharacterService.AllocateAttrPoints,
            new AllocateAttrPointReq { PlanIndex = StatPlan }, ct)).PlanBaseProperty;
        if (plan == null) return;
        int points = plan.AttrPoints;
        if (!entry.CapStats)
        {
            int bonus = ctx.Stats.LevelPoints(entry.Level, ctx.Plan.MaxLevel);
            if (bonus > 0 && !await ctx.GmAsync(entry.Account, $"!points {bonus}", ct, ownRole: true)) return;
            entry.CapStats = true;
            points += bonus;
        }
        var current = Enumerable.Range(1, 6).Select(i => plan.BaseProperty.GetValueOrDefault(i, 1)).ToArray();
        var wanted = ctx.Stats.Spend(current, points, ctx.Stats.For(_job));
        if (wanted.SequenceEqual(current)) return;
        var req = new AllocateAttrPointReq { PlanIndex = StatPlan };
        for (int i = 0; i < 6; i++) req.Points[i + 1] = wanted[i];
        int result = AllocateAttrPointRsp.Parser.ParseFrom(await RequestAsync(CharacterService.AllocateAttrPoints, req, ct)).Result;
        if (result == 0) PopulationContext.Log($"{entry.Name}: stats {string.Join('/', wanted)}");
        else PopulationContext.Log($"{entry.Name}: stats refused ({result})");
    }
}
