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
    /// <summary>The host below this share of its life is healed (the player's choice: 40%), the healer itself below
    /// <see cref="SelfHealBelow"/>.</summary>
    private const double HealBelow = 0.4, SelfHealBelow = 0.9;
    /// <summary>An ally skill's reach when its SkillConfig row names none (_iDistanceMax).</summary>
    private const float AllyReach = 9;
    private static readonly TimeSpan CastPause = TimeSpan.FromSeconds(1.8);
    private long _supportJob = -1;
    private IReadOnlyList<SupportSkill> _support = [];
    private readonly Dictionary<int, DateTime> _nextCast = [];

    /// <summary>
    /// One support cast when one is due, heals first: an area heal when the host in its radius is below
    /// <see cref="HealBelow"/> or the healer below <see cref="SelfHealBelow"/>, a single heal for the host below it in
    /// reach, else for the healer itself (the other members drink potions), a dead member to revive, then the host's buffs
    /// while it is close. False when none was cast.
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
            // Heals are for the host and the healer itself: the other members drink their own potions (FakePlayer.Potions).
            bool hostHurt = !hostDead && hostLife < HealBelow, selfHurt = OwnLife() < SelfHealBelow;
            float area = row.Range is [>= 100 and var cm, ..] ? cm / 100f : reach;
            // A ground heal (Sanctuary) goes where the host stands.
            var ground = skill.Use == SupportUse.GroundHeal && hostHurt && hostAt is { } spot && Distance(spot) <= reach ? spot : null;
            long? target = skill.Use switch
            {
                SupportUse.PartyHeal => hostHurt && hostAt is { } near && Distance(near) <= area || selfHurt ? 0 : null,
                SupportUse.GroundHeal => ground != null ? 0 : null,
                SupportUse.Heal => hostHurt && hostAt is { } at && Distance(at) <= reach ? host : selfHurt ? _view.Self : null,
                SupportUse.Revive => Patient(members, reach, m => m.Dead),
                SupportUse.Ally => hostNear ? host : null,
                _ => hostNear ? 0 : null,
            };
            if (target is not { } id) continue;
            var req = new CastSkillReq { SkillId = (int)row.Id, TargetId = id };
            if (ground is { } g) req.TargetPos = new Vec3 { X = g.X, Y = g.Y, Z = g.Z };
            int result = CastSkillRsp.Parser.ParseFrom(await RequestAsync(CombatService.UseSkill, req, ct)).Result;
            // On cooldown it waits a few seconds (a Resurrection was asked 52 times in 4 min), busy a second.
            _nextCast[skill.Group] = now + (result == 0 ? Recast(skill.Use)
                : TimeSpan.FromSeconds(result == ErrorCodes.SkillBusy ? 1 : result == ErrorCodes.SkillCooldown ? 5 : 20));
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
        SupportUse.Heal or SupportUse.PartyHeal or SupportUse.GroundHeal => TimeSpan.FromSeconds(1),
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
    /// left, cast strongest first. Each with its requirements at its cap, else at half, else at 1, as far as the job's skill points go (the
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
        // A tank's taunts (Provoke) before its buffs and attacks.
        var taunts = new List<AttackSkill>();
        foreach (var skill in Tank ? ctx.Support.Taunts(_job) : [])
            if (await LearnAsync(skill.Group, skill.Level, learned, ct) is var level and > 0) taunts.Add(skill with { Level = level });
        foreach (int group in ctx.Support.Buffs(_job))
            if (ctx.Support.Row(group, 1) is { } first && await LearnAsync(group, (int)first.MaxLevel, learned, ct) is var level and > 0)
                buffs.Add((group, level));
        // Those its build can cast first (on its mount or on foot, ClassBuild.Rides): of two exclusive branches it takes
        // the one it fights with (a Mechanic in its Madogear, or with an axe on foot).
        long ridden = Build.Rides ? entry.Mount : 0, held = ctx.Gear.HeldSubType(entry.Gear);
        foreach (var skill in ctx.Support.Attacks(_job).OrderBy(a => ctx.Support.Row(a.Group, 1) is { } r && ctx.Support.Usable(r.Id, ridden, held) ? 0 : 1))
            if (await LearnAsync(skill.Group, skill.Level, learned, ct) is var level and > 0) attacks.Add(skill with { Level = level });
        _supportJob = _job;
        _support = support;
        _buffs = buffs;
        _taunts = taunts;
        _attacks = Strongest(attacks);
        var all = support.Select(k => $"{k.Group}:{k.Level}").Concat(taunts.Select(t => $"{t.Group}:{t.Level}")).Concat(buffs.Select(b => $"{b.Group}:{b.Level}"))
            .Concat(attacks.Select(k => $"{k.Group}:{k.Level}")).ToList();
        if (all.Count > 0) PopulationContext.Log($"{entry.Name}: skills {string.Join(", ", all)}");
    }

    /// <summary>A strong attack's share of the strongest one's damage multiplier: weaker ones are left out.</summary>
    private const double StrongShare = 0.4;

    /// <summary>
    /// The attacks a bot uses, strongest first (when several are ready, the hardest hit goes out), without the weak
    /// fillers an earlier job taught (the player's wish: below <see cref="StrongShare"/> of the strongest multiplier,
    /// SupportKit.Power, of those it can cast often), keeping at least the two strongest. An ultimate on a long cooldown
    /// (a Mechanic's 180 s one) sets no bar, or it would leave the bot nothing between its casts. Between them it swings,
    /// or a caster waits.
    /// </summary>
    private List<AttackSkill> Strongest(IEnumerable<AttackSkill> attacks)
    {
        // Of those it can cast on its mount (it rides the one it leased if its build does, FakePlayer.Mount) with its weapon.
        long held = ctx.Gear.HeldSubType(entry.Gear), ridden = Build.Rides ? entry.Mount : 0;
        var ranked = attacks.Where(a => ctx.Support.Row(a.Group, a.Level) is { } row && ctx.Support.Usable(row.Id, ridden, held))
            .Select(a => (Skill: a, Power: ctx.Support.Power(a.Group, a.Level))).OrderByDescending(a => a.Power).ToList();
        var often = ranked.Where(a => ctx.Support.Row(a.Skill.Group, a.Skill.Level) is { Cd: <= OftenCdMs }).ToList();
        long best = (often.Count > 0 ? often : ranked).Select(a => a.Power).DefaultIfEmpty(0).Max();
        return [.. ranked.Where((a, i) => i < 2 || a.Power >= best * StrongShare).Select(a => a.Skill)];
    }

    /// <summary>The longest cooldown of an attack a bot casts in its rotation, not as an ultimate.</summary>
    private const long OftenCdMs = 15_000;

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
    private IReadOnlyList<AttackSkill> _attacks = [], _taunts = [];
    private DateTime _attacksPausedUntil, _spRefilled;

    /// <summary>A frontline class (SupportKit.IsTank): in a party it holds a boss with its taunts and stands in its area
    /// skills (FakePlayer.Dodge, FakePlayer.Evade), drinking instead (the player's wish).</summary>
    private bool Tank => ctx.Support.IsTank(_job);

    /// <summary>Whether the client would offer the skill now (SupportKit.Usable): the mount this bot rides, its weapon.</summary>
    private bool Usable(long skillId) => ctx.Support.Usable(skillId, _rode ? entry.Mount : 0, ctx.Gear.HeldSubType(entry.Gear));

    /// <summary>Whether an attack reaches a target <paramref name="distance"/> away: around the caster its area's radius,
    /// else its _iDistanceMax (half a metre spare: the server measures from the centres too).</summary>
    private bool Reaches(AttackSkill skill, long skillId, float distance) =>
        (skill.Aim == AttackAim.Self ? ctx.Support.AreaRadius(skillId) : ctx.Support.Reach(skillId)) is var reach
        && (reach <= 0 || distance <= reach + 0.5f);

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
            // A tank in a party draws a boss to itself first, whenever its taunt is ready (MonsterService.Provoke).
            if (InParty && Tank && ctx.Support.IsBoss((long)target.EntityTemplateID))
                foreach (var taunt in _taunts)
                    if (Ready(taunt, target) && await CastAsync(taunt.Group, taunt.Level, target, taunt.Aim, rest: null, ct) is { } cast)
                        return cast;
            foreach (var (group, level) in _buffs)
                if (await CastAsync(group, level, null, AttackAim.Self, rest: TimeSpan.FromSeconds(45 + _random.Next(30)), ct) is { } cast)
                    return cast;
            foreach (var skill in _attacks)
                if (Ready(skill, target) && await CastAsync(skill.Group, skill.Level, target, skill.Aim, rest: null, ct) is { } cast)
                    return cast;
        }
        Trace("attack", () => $"none ready of {_attacks.Count} (riding {(_rode ? entry.Mount : 0)}); {(Caster || skillsOnly ? "waits" : $"swings {normal}")} at {Distance(target.MovementInfo.Pos):F1} m");
        if (Caster || skillsOnly) // skills only: a boss is fought from afar (FakePlayer.Evade)
        {
            // Not a swing: the next spell that comes off cooldown, a moment from now.
            var next = _attacks.Select(a => _nextCast.GetValueOrDefault(a.Group)).Append(_attacksPausedUntil).Min();
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((next - now).TotalMilliseconds, 300, 2000)), ct);
            return true;
        }
        if (!await SwingAsync(normal, (long)target.Uuid, ct)) return false;
        // In a real player's party at the pace of its attack speed: a swing sent early waits on the server for the
        // next one (CombatService.DeferNormalAttackAsync), so a short pause loses no swing. Alone, a relaxed pace.
        await Task.Delay(InParty ? TimeSpan.FromMilliseconds(600 + _random.Next(200)) : TimeSpan.FromSeconds(1.2 + _random.NextDouble() * 0.6), ct);
        return true;
    }

    /// <summary>An attack the client would offer now (its weapon, its mount) that reaches the target from where the bot
    /// stands: the rest wait, so a ranged bot keeps its distance and casts what reaches.</summary>
    private bool Ready(AttackSkill skill, EntityBase target) => ctx.Support.Row(skill.Group, skill.Level) is { } row
        && Usable(row.Id) && Reaches(skill, row.Id, Distance(target.MovementInfo.Pos));

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
        Trace($"cast {group}", () => $"skill {row.Id} at {(target == null ? 0 : Distance(target.MovementInfo.Pos)):F1} m: {result}");
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
        // Busy (the last skill's action still runs) or on cooldown: soon again; anything else rests half a minute.
        _nextCast[group] = now + (result == 0 ? (rest is { } r && r > cooldown ? r : cooldown)
            : TimeSpan.FromSeconds(result == ErrorCodes.SkillBusy ? 1 : result == ErrorCodes.SkillCooldown ? 2 : 30));
        if (result != 0) return null;
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(row.ActionTime, 800, 2500)), ct);
        return true;
    }
}
