using MessagePack;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// Parties and friends with real players. A fake player accepts a real player's party invitation (42005 → 42006) and
/// then goes where that player goes: to its map when it is on another (30012), else beside it, fighting what is near
/// it. It leaves when that player leaves, when it is kicked, or when the player has been offline for five minutes. A
/// friend request from a real player (FriendService push onFriendApply, or one waiting from while it was away) is
/// accepted after a few seconds. How bots answer is a population choice (inferred).
/// </summary>
public sealed partial class FakePlayer
{
    private static readonly TimeSpan HostAway = TimeSpan.FromMinutes(5);
    private const float HostReach = 12;
    private readonly object _partyLock = new();
    private long _partyId, _partyHost, _hostScene;
    /// <summary>The party leader's locked enemy (TeamTargetAndEnemy.LeaderTargetId, WorldCoreService.TeamTargets): what
    /// the bot fights first, as a player follows the leader's mark.</summary>
    private long _hostTarget;
    private Vec3? _hostPos;
    /// <summary>The host's life as its party card reports it (TeamMemberBattleInfo hpPercent holds the value, maxHpValue the cap).</summary>
    private double _hostLife = 1;
    private bool _hostDead;
    private DateTime? _hostOfflineSince;
    private bool _partyOver;

    private void ObserveParty(BotEvent e)
    {
        if (e.Kind == BotEventKind.Rpc && e.Name == "push:FriendService.onFriendApply") { OnFriendApply(e.Body); return; }
        if (e.Kind != BotEventKind.Game) return;
        try
        {
            switch (e.MsgId)
            {
                case TeamService.TeamNotifyInvitee: OnPartyInvite(NotifyInvitee.Parser.ParseFrom(e.Body)); break;
                case TeamService.TeamAll: OnPartyAll(TeamTotalDetailInfo.Parser.ParseFrom(e.Body)); break;
                case TeamService.TeamMemberUpdate:
                    foreach (var (role, card) in TeamMemberChangeResp.Parser.ParseFrom(e.Body).MemberList) OnHostCard(role, card);
                    break;
                case TeamService.TeamMemberBattleUpdate:
                    var battle = TeamMemberBattleInfoChangeRes.Parser.ParseFrom(e.Body);
                    lock (_partyLock) if (battle.BattleInfo is { } info) MemberBattle(battle.Guid, info);
                    break;
                case TeamService.TeamBroadcastLeave:
                    var leave = BroadCastMemberLeave.Parser.ParseFrom(e.Body);
                    lock (_partyLock)
                    {
                        _members.Remove(leave.Guid);
                        if (leave.TeamId == _partyId && leave.Guid == _partyHost) _partyOver = true;
                    }
                    break;
                case WorldCoreService.TeamTargets:
                    long marked = TeamTargetAndEnemy.Parser.ParseFrom(e.Body).LeaderTargetId;
                    lock (_partyLock) _hostTarget = marked;
                    break;
                case TeamService.TeamOperateBroadcast:
                    var op = TeamOperateNotify.Parser.ParseFrom(e.Body);
                    if (op.Oper == TeamOperationType.TeamOperTypeKick && op.TargetGuid == _view.Self) ClearParty();
                    break;
                case DungeonService.ReadyQueue:
                    var queue = TeamEnterReadyQueueNotify.Parser.ParseFrom(e.Body);
                    if (!queue.Cancel && queue.ReadyQueue.TryGetValue(_view.Self, out bool ready) && !ready) ConfirmDungeon();
                    break;
                case DungeonService.ConfirmReminderNotify: ConfirmDungeon(); break;
                case CombatService.UseInSkill or CombatService.UseInSkillHigh: OnBossSkill(UseInSkillInfo.Parser.ParseFrom(e.Body)); break;
            }
        }
        catch (Google.Protobuf.InvalidProtocolBufferException) { }
    }

    /// <summary>
    /// The party leader starts a dungeon (42033 ready vote, or 42046 its reminder): a fake player in a real player's
    /// party is always ready (30406 agree) after a moment; the server then moves it in (70010, OnSwitchScene).
    /// </summary>
    private void ConfirmDungeon()
    {
        if (!InParty || Interlocked.Exchange(ref _confirming, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 1.5), _life);
                await SendAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true }, _life);
                PopulationContext.Log($"{entry.Name}: ready for the party's dungeon");
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _confirming, 0); }
        });
    }

    private int _confirming;

    private void OnPartyInvite(NotifyInvitee invite)
    {
        bool free;
        lock (_partyLock) free = _partyId == 0;
        // A real player's invitation, or its rival raid's leader's (FakePlayer.Raid).
        bool welcome = !ctx.IsBot(invite.InviteGuid) || invite.InviteGuid == RaidLeader;
        if (free && welcome && _scene != SiegeScene) _ = AcceptPartyAsync(invite);
    }

    private async Task AcceptPartyAsync(NotifyInvitee invite)
    {
        var ct = _life;
        long teamId = invite.TeamTotalSimpleInfo?.Info?.TeamId ?? 0;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 2), ct);
            var reply = InviteeRespResp.Parser.ParseFrom(await RequestAsync(TeamService.TeamInviteeResp,
                new InviteeResp { TeamId = teamId, IsAgree = true, InviteGuid = invite.InviteGuid }, ct));
            if (reply.Result != 0) return;
            lock (_partyLock)
            {
                _partyId = teamId;
                _partyHost = invite.InviteGuid;
                _partyOver = false;
                _hostOfflineSince = null;
            }
            PopulationContext.Log($"{entry.Name}: joined the party of {invite.InviteGuid}");
            _gearAfter = _growAfter = DateTime.UtcNow; // a real player's party member gears up now, not at its turn in the spread
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException) { }
    }

    /// <summary>The whole party (on joining, and again at each login): its host is the real player in it, in a rival
    /// raid its leader; the leader itself has no host.</summary>
    private void OnPartyAll(TeamTotalDetailInfo all)
    {
        long raidLeader = RaidLeader;
        bool leads = LeadsRaid;
        lock (_partyLock)
        {
            if (_partyId == 0 && all.Info?.Info is { } info && all.MemberList.ContainsKey(_view.Self))
            {
                long host = all.MemberList.Keys.FirstOrDefault(m => !ctx.IsBot(m));
                if (host == 0 && all.MemberList.ContainsKey(raidLeader)) host = raidLeader;
                if (host != 0 || leads)
                {
                    _partyId = info.TeamId;
                    _partyHost = host;
                    _partyOver = false;
                }
            }
        }
        foreach (var (role, card) in all.MemberList) OnHostCard(role, card);
    }

    private void OnHostCard(long role, TeamMemberDetailInfo card)
    {
        lock (_partyLock)
        {
            if (card.BattleInfo is { } battle) MemberBattle(role, battle);
            if (role != _partyHost) return;
            if (card.MapId != 0) _hostScene = card.MapId;
            bool offline = card.OnlineState == TeamMemberOnlineState.Offline;
            _hostOfflineSince = offline ? _hostOfflineSince ?? DateTime.UtcNow : null;
        }
    }

    /// <summary>A party member's life (0-1), death, position and maximum HP, from its battle card (the healers' watch list).</summary>
    private readonly record struct Member(double Life, bool Dead, Vec3? Pos, long MaxHp = 0);
    private readonly Dictionary<long, Member> _members = [];

    /// <summary>Under _partyLock: any member's battle card; the host's also moves the party.</summary>
    private void MemberBattle(long guid, TeamMemberBattleInfo battle)
    {
        var old = _members.GetValueOrDefault(guid, new Member(1, false, null));
        _members[guid] = new Member(battle.MaxHpValue > 1 ? Math.Clamp((double)battle.HpPercent / battle.MaxHpValue, 0, 1) : old.Life,
            battle.PlayerLifeStatus == EPlayerLifeStatus.Dead, battle.Pos ?? old.Pos, battle.MaxHpValue > 1 ? battle.MaxHpValue : old.MaxHp);
        if (guid == _partyHost) HostBattle(battle);
    }

    /// <summary>Under _partyLock: the host's position, life and death from its battle card.</summary>
    private void HostBattle(TeamMemberBattleInfo battle)
    {
        if (battle.Pos != null) _hostPos = battle.Pos;
        if (battle.MaxHpValue > 1) _hostLife = Math.Clamp((double)battle.HpPercent / battle.MaxHpValue, 0, 1);
        _hostDead = battle.PlayerLifeStatus == EPlayerLifeStatus.Dead;
    }

    /// <summary>In a party with a real player, or a rival raid's (FakePlayer.Raid): it fights as a party member does.</summary>
    private bool InParty
    {
        get
        {
            lock (_partyLock) if (_partyHost != 0) return true;
            return LeadsRaid;
        }
    }

    /// <summary>In a party or picked for a raid: long errands end early so the party or raid step can take over.</summary>
    private bool Called => InParty || Volatile.Read(ref _raid) != null;

    private void ClearParty()
    {
        lock (_partyLock)
        {
            _partyId = _partyHost = _hostScene = _hostTarget = 0;
            _hostPos = null;
            _hostOfflineSince = null;
            _partyOver = false;
            _hostLife = 1;
            _hostDead = false;
            _members.Clear();
        }
    }

    /// <summary>One step with the party's host; false when not in a party with one.</summary>
    private async Task<bool> PartyStepAsync(CancellationToken ct)
    {
        long host, scene;
        Vec3? at;
        bool over;
        lock (_partyLock)
        {
            host = _partyHost;
            scene = _hostScene;
            at = _hostPos;
            over = _partyOver || DateTime.UtcNow - _hostOfflineSince > HostAway;
        }
        if (host == 0) return false;
        if (over)
        {
            await SendAsync(TeamService.TeamLeave, new Google.Protobuf.WellKnownTypes.Empty(), ct);
            ClearParty();
            PopulationContext.Log($"{entry.Name}: left the party of {host}");
            await GoHomeAsync(ct);
            return true;
        }
        if (scene != 0 && scene != _scene)
        {
            // An instance or a map the bot cannot travel to: it waits for the host to come back out.
            try { await TravelAsync(scene, ct); }
            catch (InvalidOperationException) { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            return true;
        }
        if (at is not { } p) { Trace("host", () => "no position"); await PauseAsync(2, 4, ct); return true; }
        // Out of a boss's marked area (FakePlayer.Dodge); run from a boss when low, then drink (FakePlayer.Potions); low
        // anywhere, drink.
        if (await DodgeAsync(ct)) { Trace("dodge", () => "out of an area"); return true; }
        if (await EvadeAsync(ct)) { Trace("evade", () => $"ran at {OwnLife():P0}"); await DrinkAsync(ct); return true; }
        if (await DrinkAsync(ct)) return true;
        if (await SupportAsync(host, ct)) { Trace("support", () => "cast"); return true; } // heals are for the host; revives reach the whole party
        bool hostDead;
        lock (_partyLock) hostDead = _hostDead;
        // A support class stays by its host (its buffs reach only so far), except at a boss the leader marked, where it
        // takes its own place (BossPlace: a ranged one would else run between the host and its ring); nobody fights on
        // while the host lies dead.
        bool atBoss = Marked() is { } mark && ctx.Support.IsBoss((long)mark.EntityTemplateID);
        bool keepClose = hostDead || _support.Count > 0 && Distance(p) > SupportReach && !atBoss;
        // On a PK map an enemy player at the host comes first (FakePlayer.Raid), the leader's mark if it is one.
        if (!keepClose && (MarkedPlayer() ?? EnemyPlayer(p.X, p.Z)) is { } enemy) { await StrikePlayerAsync(enemy, ct); return true; }
        // The leader's mark first, else the monster nearest the bot around the host.
        var target = keepClose ? null : Marked() ?? _view.Monsters().Where(m => Apart(m.MovementInfo.Pos, p) <= HostReach)
            .OrderBy(m => Distance(m.MovementInfo.Pos)).FirstOrDefault();
        Trace("step", () => $"host {Distance(p):F1} m, keepClose {keepClose}, target {(target == null ? "none" : $"{target.EntityTemplateID} at {Distance(target.MovementInfo.Pos):F1} m")}");
        if (target != null) await StrikeAsync(target, ct);
        else if (Distance(p) > 5) await FollowAsync(p, ct);
        else await PauseAsync(1, 3, ct);
        return true;
    }

    /// <summary>
    /// To the host's side. A host far off (a teleport across the map) can be past the path search (NavPath.MaxExpanded):
    /// the bot then walks <see cref="FollowLeg"/> m its way and searches again from there, else it shuffles. A failed
    /// walk returns at once, so it never loops on the spot sending nothing (it spun 4 cores on a host 270 m away).
    /// </summary>
    private async Task FollowAsync(Vec3 host, CancellationToken ct)
    {
        if (await GoAsync(Near(new MapPoint(host.X, host.Y, host.Z, 0), 2, 4) ?? new MapPoint(host.X, host.Y, host.Z, 0), ct, short_: 1f)) return;
        SyncPosition();
        float dx = host.X - _pos.X, dz = host.Z - _pos.Z, length = MathF.Max(0.1f, MathF.Sqrt(dx * dx + dz * dz));
        var leg = new MapPoint(_pos.X + dx / length * FollowLeg, _pos.Y, _pos.Z + dz / length * FollowLeg, 0);
        if (length > FollowLeg && Near(leg, 0, 8) is { } partway && await GoAsync(partway, ct)) return;
        await ShuffleAsync(6, ct);
    }

    private const float FollowLeg = 30;

    private readonly Dictionary<string, DateTime> _traced = [];

    /// <summary>A decision of a bot named in --trace (PopulationOptions.Trace), at most once per 3 s for each kind.</summary>
    private void Trace(string kind, Func<string> text)
    {
        if (!ctx.Options.Trace.Contains(entry.Name)) return;
        var now = DateTime.UtcNow;
        lock (_traced)
        {
            if (_traced.TryGetValue(kind, out var last) && now - last < TimeSpan.FromSeconds(3)) return;
            _traced[kind] = now;
        }
        PopulationContext.Log($"{entry.Name} [trace] {kind}: {text()}");
    }

    /// <summary>The monster the party leader has locked, while the bot sees it.</summary>
    private EntityBase? Marked()
    {
        long marked;
        lock (_partyLock) marked = _hostTarget;
        return marked == 0 ? null : _view.Monsters().FirstOrDefault(m => (long)m.Uuid == marked);
    }

    /// <summary>The enemy player the party leader has locked, while the bot sees it in the enemy camp on a PK map.</summary>
    private EntityBase? MarkedPlayer()
    {
        long marked;
        lock (_partyLock) marked = _hostTarget;
        return marked == 0 || !ctx.PkMaps.Contains(_scene) ? null
            : _view.Players().FirstOrDefault(p => (long)p.Uuid == marked && p.EntityCamp == EnemyCamp && p.State != EntityState.Dead);
    }

    /// <summary>The bot's own life as its party card reports it (1 out of a party).</summary>
    private double OwnLife()
    {
        lock (_partyLock) return _members.TryGetValue(_view.Self, out var me) ? me.Life : 1;
    }

    /// <summary>
    /// Walks up to a monster and fights it (skills and swings) until it is gone; a refused swing is death, so it revives.
    /// In a party it turns to a new mark of the leader's at once.
    /// </summary>
    private async Task StrikeAsync(EntityBase target, CancellationToken ct)
    {
        var at = target.MovementInfo.Pos;
        // Alone a boss is fought with skills from afar (FakePlayer.Evade); in a party, up close beside its players.
        bool anyBoss = ctx.Support.IsBoss((long)target.EntityTemplateID), boss = !InParty && anyBoss;
        float reach = boss ? BossRange : 3f;
        if (InParty && anyBoss)
        {
            // Round a boss each party member takes its own place, not one heap on the boss (the player's wish). It moves
            // only when out of its reach, or a ranged one closer than its ring (it backs off): every walk is time not
            // spent attacking.
            float own = AttackReach() is >= RangedReach and var far ? far : 3f, now = Distance(at);
            bool move = now > own - 0.4f || own >= RangedReach && now < Ring(own) - RangedSlack;
            var place = BossPlace(at);
            Trace("place", () => $"reach {own:F1}, at {now:F1} m, move {move}, place {Apart(place, at):F1} m out, {Distance(place):F1} m away");
            // Not back into a marked area while it lasts (FakePlayer.Dodge): it waits at the edge.
            if (move && InDanger(place)) { Trace("danger", () => "waits at the edge"); await Task.Delay(500, ct); return; }
            if (move && Distance(place) > 1f && !await GoAsync(new MapPoint(place.X, place.Y, place.Z, 0), ct))
            {
                Trace("place-walk", () => "no path: shuffles");
                await ShuffleAsync(6, ct);
                return;
            }
        }
        else if (Distance(at) > reach && !await GoAsync(new MapPoint(at.X, at.Y, at.Z, 0), ct, short_: reach - 1)) { await ShuffleAsync(6, ct); return; }
        long skill = ctx.Plan.NormalAttack(_job) is > 0 and var s ? s : FallbackAttack;
        await SendAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)target.Uuid }, ct);
        // Its job's attack skills when ready, else the normal attack (FakePlayer.Support.AttackOnceAsync).
        for (int swing = 0; swing < (InParty ? PartyStrikes : 6) && _view.Monsters().FirstOrDefault(m => m.Uuid == target.Uuid) is { } current; swing++)
        {
            if (await DodgeAsync(ct)) return; // out of a boss's marked area first: its place is found again next round
            if (await EvadeAsync(ct)) { await DrinkAsync(ct); return; }
            if (InParty) await DrinkAsync(ct);
            if (Marked() is { } mark && mark.Uuid != current.Uuid) return;
            if (!await AttackOnceAsync(current, skill, ct, skillsOnly: boss)) { await ReviveAsync(ct); return; }
        }
    }

    /// <summary>
    /// This bot's own place round a boss at <paramref name="at"/>: its angle by the golden angle of its role id (members
    /// fall all round, none on another's spot), close in for those who swing (one of three rings within the 3 m of a
    /// normal attack), kept at a distance by those who fight from afar (<see cref="AttackReach"/> 5 m or more: mages,
    /// archers, a riding Ranger's Warg Strike) on <see cref="Ring"/>, a few metres inside their reach (the player's wish:
    /// they never go near a boss). A walkable point with a clear walk from the boss is taken, the angle turned a little
    /// at a time round a wall; failing that a ranged bot stays where it is if its attacks reach from there, else the boss.
    /// </summary>
    private Vec3 BossPlace(Vec3 at)
    {
        const double GoldenAngle = 2.39996322972865332, Turn = 0.6; // radians
        long id = entry.RoleId;
        double angle = id % 997 * GoldenAngle;
        float reach = AttackReach();
        bool ranged = reach >= RangedReach;
        float ring = ranged ? Ring(reach) : 1.6f + id % 3 * 0.5f;
        for (int k = 0; k < (ranged ? 8 : 1); k++)
        {
            double a = angle + (k % 2 == 0 ? 1 : -1) * (k + 1) / 2 * Turn;
            float x = at.X + ring * (float)Math.Sin(a), z = at.Z + ring * (float)Math.Cos(a);
            if (Map?.Nav is { } nav && nav.WalkHeight(x, z, at.Y) is { } y && nav.CanWalk(at.X, at.Y, at.Z, x, z))
                return new Vec3 { X = x, Y = y, Z = z };
        }
        SyncPosition();
        return ranged && Distance(at) <= reach - 0.5f && Distance(at) >= RangedReach ? new Vec3 { X = _pos.X, Y = _pos.Y, Z = _pos.Z } : at;
    }

    /// <summary>A ranged bot's distance from a boss: 3-5 m inside its reach (one of three by its role id), at least
    /// <see cref="RangedReach"/>: an 18 m bow stands 13-15 m out, a 10 m spell 5-7 m.</summary>
    private float Ring(float reach) => Math.Max(RangedReach, reach - 3f - entry.RoleId % 3);

    /// <summary>A reach from which a party member keeps its distance from a boss.</summary>
    private const float RangedReach = 5;
    /// <summary>How far inside its ring a ranged bot lets a boss come before it backs off again.</summary>
    private const float RangedSlack = 3;
    /// <summary>Attacks a party member makes before it looks round again (its mark, its place, its potions).</summary>
    private const int PartyStrikes = 16;

    /// <summary>How far this bot hits from: a caster's spells (the shortest of its aimed attack skills: every one must
    /// reach; those round the caster wait for a boss that comes close), anyone else's normal attack (a bow's, a gun's,
    /// a riding Ranger's Warg Strike or a weapon's).</summary>
    private float AttackReach()
    {
        if (Caster && _attacks.Count > 0)
            return _attacks.Where(a => a.Aim != AttackAim.Self)
                .Select(a => ctx.Support.Row(a.Group, a.Level)?.DistanceMax is > 0 and var cm ? cm / 100f : 0)
                .Where(r => r > 0).DefaultIfEmpty(0).Min();
        return ctx.Plan.NormalAttack(_job) is > 0 and var normal ? ctx.Support.Reach(normal) : 0;
    }

    private static float Apart(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void OnFriendApply(byte[] body)
    {
        try
        {
            var args = MessagePackSerializer.Deserialize<object?[]>(body);
            if (args.Length > 0 && long.TryParse(args[0]?.ToString(), out long applicant) && !ctx.IsBot(applicant))
                _ = AcceptFriendAsync(applicant, TimeSpan.FromSeconds(2 + Random.Shared.Next(6)));
        }
        catch (MessagePackSerializationException) { }
    }

    /// <summary>Requests that came while the bot was away (fetchApplicants), a while after it logs in.</summary>
    private async Task AcceptWaitingFriendsAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30 + Random.Shared.Next(90)), ct);
            var bot = _bot ?? throw new IOException("not connected");
            var reply = await bot.MicroAsync("FriendService", "fetchApplicants", new() { ["Uid"] = _view.Self.ToString() });
            if (reply.GetValueOrDefault("Applicants") is not object[] rows) return;
            foreach (var row in rows)
                if (row is Dictionary<object, object?> map && long.TryParse(map.GetValueOrDefault("uid")?.ToString(), out long applicant)
                    && !ctx.IsBot(applicant))
                    await AcceptFriendAsync(applicant, TimeSpan.Zero);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or InvalidCastException) { }
    }

    private async Task AcceptFriendAsync(long applicant, TimeSpan delay)
    {
        var ct = _life;
        try
        {
            await Task.Delay(delay, ct);
            var bot = _bot ?? throw new IOException("not connected");
            await bot.MicroAsync("FriendService", "accept", new() { ["Uid"] = _view.Self.ToString(), ["Applicant"] = applicant.ToString() });
            PopulationContext.Log($"{entry.Name}: accepted the friend request of {applicant}");
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException) { }
    }
}
