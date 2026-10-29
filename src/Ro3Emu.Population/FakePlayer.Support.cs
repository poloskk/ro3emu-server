using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player in a real player's party (SupportKit) learns its job's skills once per job through the client's 31151
/// skill plan request. A support class keeps its buffs and heals on the host and revives the whole party; every
/// class fights with its attack skills between normal attacks (31102 casts as a player's, the server's ordinary
/// checks). A population choice (inferred).
/// </summary>
public sealed partial class FakePlayer
{
    /// <summary>The buffs are cast within this distance of the host (inside their radius, SkillConfig ${1}).</summary>
    private const float SupportReach = 6;
    /// <summary>The host below this share of its life is healed.</summary>
    private const double HealBelow = 0.9;
    /// <summary>An ally skill's reach when its SkillConfig row names none (_iDistanceMax).</summary>
    private const float AllyReach = 9;
    private static readonly TimeSpan CastPause = TimeSpan.FromSeconds(1.8);
    private long _supportJob = -1;
    private IReadOnlyList<SupportSkill> _support = [];
    private readonly Dictionary<int, DateTime> _nextCast = [];

    /// <summary>
    /// One support cast when one is due, heals first: the host below <see cref="HealBelow"/> in the heal's reach (the
    /// other members drink potions), a dead member to revive, then the host's buffs while it is close. False when none
    /// was cast.
    /// </summary>
    private async Task<bool> SupportAsync(long host, CancellationToken ct)
    {
        var kit = await SupportSkillsAsync(ct);
        Dictionary<long, Member> members;
        bool hostDead;
        double hostLife;
        Vec3? hostAt;
        lock (_partyLock) (members, hostDead, hostLife, hostAt) = (new(_members), _hostDead, _hostLife, _hostPos);
        bool hostNear = !hostDead && hostAt is { } h && Distance(h) <= SupportReach;
        var now = DateTime.UtcNow;
        foreach (var skill in kit)
        {
            if (now < _nextCast.GetValueOrDefault(skill.Group) || ctx.Support.Row(skill.Group, skill.Level) is not { } row) continue;
            float reach = row.DistanceMax > 0 ? row.DistanceMax / 100f : AllyReach;
            long? target = skill.Use switch
            {
                // Heals are for the host alone: the other members drink their own potions (FakePlayer.Potions).
                SupportUse.Heal => !hostDead && hostLife < HealBelow && hostAt is { } at && Distance(at) <= reach ? host : null,
                SupportUse.Revive => Patient(members, reach, m => m.Dead),
                SupportUse.Ally => hostNear ? host : null,
                _ => hostNear ? 0 : null,
            };
            if (target is not { } id) continue;
            var req = new CastSkillReq { SkillId = (int)row.Id, TargetId = id };
            int result = CastSkillRsp.Parser.ParseFrom(await RequestAsync(CombatService.UseSkill, req, ct)).Result;
            _nextCast[skill.Group] = now + (result == 0 ? Recast(skill.Use) : TimeSpan.FromSeconds(result == ErrorCodes.SkillCooldown ? 1 : 20));
            if (result != 0) continue;
            await Task.Delay(CastPause, ct);
            return true;
        }
        return false;
    }

    /// <summary>The member a revive is for: the one lowest on life that needs it, within reach (never the bot itself).</summary>
    private long? Patient(Dictionary<long, Member> members, float reach, Func<Member, bool> needs)
    {
        long me = _view.Self;
        foreach (var (id, member) in members.OrderBy(m => m.Value.Life))
            if (needs(member) && id != me && member.Pos is { } at && Distance(at) <= reach) return id;
        return null;
    }

    /// <summary>A buff again before it wears off (inferred: the original buffs last minutes), a heal or revive soon.</summary>
    private TimeSpan Recast(SupportUse use) => use switch
    {
        SupportUse.Heal => TimeSpan.FromSeconds(1),
        SupportUse.Revive => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(50 + _random.Next(30)),
    };

    private async Task<IReadOnlyList<SupportSkill>> SupportSkillsAsync(CancellationToken ct)
    {
        await LearnSkillsAsync(ct);
        return _support;
    }

    /// <summary>
    /// The job's skills, learned on first need: its class items' skills, its support skills, its own buffs, then its attack skills with the points
    /// left. Each with its requirements at its cap, else at half, else at 1, as far as the job's skill points go (the
    /// server refuses what they do not cover). Once per job.
    /// </summary>
    private async Task LearnSkillsAsync(CancellationToken ct)
    {
        if (_supportJob == _job) return;
        var learned = new Dictionary<int, int>();
        var support = new List<SupportSkill>();
        var buffs = new List<(int Group, int Level)>();
        var attacks = new List<AttackSkill>();
        // The class items' skills first (Peco Peco Riding, Falcon Mastery, Pushcart): a point or two each.
        foreach (var item in ctx.Support.ClassItems(_job))
            if (ctx.Support.UnlockSkill(item) is var (group, level) && learned.GetValueOrDefault(group) < level)
                await LearnAsync(group, level, learned, ct);
        foreach (var skill in ctx.Support.For(_job))
            if (await LearnAsync(skill.Group, skill.Level, learned, ct) is var level and > 0) support.Add(skill with { Level = level });
        foreach (int group in ctx.Support.Buffs(_job))
            if (ctx.Support.Row(group, 1) is { } first && await LearnAsync(group, (int)first.MaxLevel, learned, ct) is var level and > 0)
                buffs.Add((group, level));
        foreach (var skill in ctx.Support.Attacks(_job))
            if (await LearnAsync(skill.Group, skill.Level, learned, ct) is var level and > 0) attacks.Add(skill with { Level = level });
        _supportJob = _job;
        _support = support;
        _buffs = buffs;
        _attacks = attacks;
        var all = support.Select(k => $"{k.Group}:{k.Level}").Concat(buffs.Select(b => $"{b.Group}:{b.Level}"))
            .Concat(attacks.Select(k => $"{k.Group}:{k.Level}")).ToList();
        if (all.Count > 0) PopulationContext.Log($"{entry.Name}: skills {string.Join(", ", all)}");
    }

    /// <summary>One skill (with its requirements) learned on top of <paramref name="learned"/>: the level reached, or 0.</summary>
    private async Task<int> LearnAsync(int group, int maxLevel, Dictionary<int, int> learned, CancellationToken ct)
    {
        foreach (int level in new[] { maxLevel, (maxLevel + 1) / 2, 1 }.Distinct())
        {
            if (ctx.Support.Ask(_job, group, level, learned) is not { } ask) return 0;
            var req = new SkillGrowthSkillPlanUpdateReq();
            foreach (var (g, at) in ask) req.LevelUpList[g] = new SkillData { SkillId = g, SkillLevel = at };
            if (SkillGrowthSkillPlanUpdateResp.Parser.ParseFrom(await RequestAsync(SkillService.PlanUpdate, req, ct)).Result != 0) continue;
            foreach (var (g, at) in ask) learned[g] = at;
            return level;
        }
        return 0;
    }

    private IReadOnlyList<(int Group, int Level)> _buffs = [];
    private IReadOnlyList<AttackSkill> _attacks = [];
    private DateTime _attacksPausedUntil, _spRefilled;

    /// <summary>A class that fights with spells (SupportKit.Casts) never swings.</summary>
    private bool Caster => SupportKit.Casts(_attacks);

    /// <summary>
    /// One action at a monster: one of its own buffs that is due, else a ready attack skill (31102 aimed as it is cast),
    /// else the job's normal attack; a caster (or anyone at a boss, <paramref name="skillsOnly"/>) waits for its next
    /// skill instead. False when the server refuses because
    /// the bot is dead. A skill on cooldown waits for it; one the bot cannot use here (weapon, mount, range, wall) rests
    /// a while. Out of SP, a bot in a real player's party is topped up (GM !hp; a population choice) at most every half
    /// minute; otherwise its skills rest while its SP comes back.
    /// </summary>
    private async Task<bool> AttackOnceAsync(EntityBase target, long normal, CancellationToken ct, bool skillsOnly = false)
    {
        await LearnSkillsAsync(ct);
        // A support class heals and revives between its blows (SupportAsync, heals first).
        long host;
        lock (_partyLock) host = _partyHost;
        if (_support.Count > 0 && host != 0 && await SupportAsync(host, ct)) return true;
        var now = DateTime.UtcNow;
        if (now >= _attacksPausedUntil)
        {
            foreach (var (group, level) in _buffs)
                if (await CastAsync(group, level, null, AttackAim.Self, rest: TimeSpan.FromSeconds(45 + _random.Next(30)), ct) is { } cast)
                    return cast;
            foreach (var skill in _attacks)
                if (await CastAsync(skill.Group, skill.Level, target, skill.Aim, rest: null, ct) is { } cast)
                    return cast;
        }
        if (Caster || skillsOnly) // skills only: a boss is fought from afar (FakePlayer.Evade)
        {
            // Not a swing: the next spell that comes off cooldown, a moment from now.
            var next = _attacks.Select(a => _nextCast.GetValueOrDefault(a.Group)).Append(_attacksPausedUntil).Min();
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((next - now).TotalMilliseconds, 300, 2000)), ct);
            return true;
        }
        if (!await SwingAsync(normal, (long)target.Uuid, ct)) return false;
        await Task.Delay(TimeSpan.FromSeconds(1.2 + _random.NextDouble() * 0.6), ct);
        return true;
    }

    /// <summary>
    /// A skill cast if it is due: true when cast, false when the server says the bot is dead, null when it was not cast
    /// (not due, or refused: it then rests). <paramref name="rest"/>: how long a cast buff lasts before it is cast again
    /// (null: its cooldown, an attack).
    /// </summary>
    private async Task<bool?> CastAsync(int group, int level, EntityBase? target, AttackAim aim, TimeSpan? rest, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _nextCast.GetValueOrDefault(group) || ctx.Support.Row(group, level) is not { } row) return null;
        var at = target?.MovementInfo.Pos;
        var req = new CastSkillReq
        {
            SkillId = (int)row.Id, TargetId = aim == AttackAim.Target && target != null ? (long)target.Uuid : 0,
            TargetPos = aim == AttackAim.Ground && at != null ? new Vec3 { X = at.X, Y = at.Y, Z = at.Z } : null,
        };
        int result = CastSkillRsp.Parser.ParseFrom(await RequestAsync(CombatService.UseSkill, req, ct)).Result;
        if (result == ErrorCodes.SkillBadState) return false;
        if (result == ErrorCodes.SkillCostShort)
        {
            // In a real player's party the bot is topped up to keep supporting it (!hp, in its own scope); alone it rests
            // its skills while its SP comes back.
            if (InParty && now - _spRefilled > TimeSpan.FromSeconds(30) && await ctx.GmAsync(entry.Account, "!hp 99999999 99999999", ct, ownRole: true))
                _spRefilled = now;
            else _attacksPausedUntil = now + TimeSpan.FromSeconds(InParty ? 10 : 45 + _random.Next(30));
            return null;
        }
        var cooldown = TimeSpan.FromMilliseconds(Math.Max(row.Cd, 1500));
        _nextCast[group] = now + (result == 0 ? (rest is { } r && r > cooldown ? r : cooldown)
            : TimeSpan.FromSeconds(result == ErrorCodes.SkillCooldown ? 2 : 30));
        if (result != 0) return null;
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(row.ActionTime, 800, 2500)), ct);
        return true;
    }
}
