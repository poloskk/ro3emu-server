namespace Ro3Emu.Population;

/// <summary>
/// Growth (inferred population choice, not original content: the original server had only real players). A fake player
/// gains base levels while it plays, hunters fastest, up to the game's cap (WorldPlan.MaxLevel), and takes the next job
/// of its line once PublicParamConfig.TransformJobLevelLimit allows it. Time offline counts at a third, at most ten
/// levels' worth per login, so a server that was off a week does not hand everyone the cap.
/// </summary>
public sealed partial class FakePlayer
{
    private const int OfflineLevels = 10;
    /// <summary>Levels are given two at a time: a GM line holds the whole realm, so fewer and larger steps.</summary>
    private const int LevelsPerStep = 2;
    private DateTime _growthFrom;

    private static TimeSpan LevelEvery(Persona persona) => persona switch
    {
        Persona.Hunter => TimeSpan.FromMinutes(20),
        Persona.Townie => TimeSpan.FromMinutes(45),
        _ => TimeSpan.FromMinutes(60),
    };

    /// <summary>At login: where this session's growth counts from, with the offline credit.</summary>
    private void StartGrowth()
    {
        var now = DateTime.UtcNow;
        var every = LevelEvery(entry.Persona);
        var offline = entry.LastGrowthUtc is { } last && last < now ? (now - last) / 3 : TimeSpan.Zero;
        _growthFrom = now - (offline < every * OfflineLevels ? offline : every * OfflineLevels);
    }

    private async Task GrowAsync(CancellationToken ct)
    {
        if (entry.Level == 0 || !entry.Prepared) return; // a role whose level is not known, or not set up yet
        var every = LevelEvery(entry.Persona);
        int levels = (int)((DateTime.UtcNow - _growthFrom) / every);
        if (levels < LevelsPerStep) return;
        int level = Math.Min(entry.Level + levels, ctx.Plan.MaxLevel), jobLevel = Math.Max(entry.JobLevel, level / 2);
        var next = ctx.Plan.NextJob(_job, entry.Sex, _random);
        bool advance = next is { } n && level >= n.Base;
        if (advance) jobLevel = Math.Max(jobLevel, next!.Value.JobLevel);
        // Only for a base level gained (the job level rides along): a GM line holds the whole realm, and a bot at the cap
        // has no use for job levels alone.
        if (level > entry.Level && !await ctx.GmAsync(entry.Account, $"!lvl {level} {jobLevel}", ct, ownRole: true)) return;
        if (advance && await ctx.GmAsync(entry.Account, $"!job {next!.Value.Job}", ct))
        {
            _job = next.Value.Job;
            PopulationContext.Log($"{entry.Name}: now job {_job} at level {level}");
        }
        _growthFrom += every * levels;
        entry.Level = level;
        entry.JobLevel = jobLevel;
        entry.LastGrowthUtc = DateTime.UtcNow;
        ctx.Roster.Save();
    }

    private bool _jobChecked;

    /// <summary>
    /// Once a session: a role GM !job sent into a job nobody can take (WorldPlan.TakeableSibling: no weapon fits it, the
    /// job tree goes no further) moves to a sibling it can take. A background GM line (the realm's fake players, one
    /// every few seconds), at once for a real player's party member.
    /// </summary>
    private async Task FixJobAsync(CancellationToken ct)
    {
        if (_jobChecked || entry.Level == 0 || !entry.Prepared) return;
        _jobChecked = true;
        if (ctx.Plan.TakeableSibling(_job, entry.Sex, entry.RoleId) is not { } job) return;
        if (!await ctx.GmAsync(entry.Account, $"!job {job}", ct, background: !InParty)) { _jobChecked = false; return; }
        PopulationContext.Log($"{entry.Name}: job {_job} cannot be taken; now job {job}");
        _job = job;
        entry.Gear = ""; // its new job's set
        ctx.Roster.Save();
    }
}
