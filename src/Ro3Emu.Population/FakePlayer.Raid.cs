using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A rival raid (MvpRaidCoordinator) and fights between players on the PK MVP maps (Ro3Emu FieldPk). The leader creates
/// a raid party (42001, TeamType.Forty) and invites the others (42003), who take its invitation and from then on follow
/// it as a party member follows a real player's lead (FakePlayer.Party). The leader goes to its MVP's map (30012), walks
/// to where the MVP stands and fights it with the party's boss fighting; anyone on those maps whom the server shows in
/// the enemy camp (FieldPk.EnemyCamp: not of its party or guild) is fought first when close. A population choice
/// (inferred): the original server had only real players.
/// </summary>
public sealed partial class FakePlayer
{
    /// <summary>The camp the server shows a player in that this one may fight (Ro3Emu.Server FieldPk.EnemyCamp).</summary>
    private const long EnemyCamp = FieldPk.EnemyCamp;
    /// <summary>An enemy player this close (to the bot, or to the host it guards) is fought before any monster.</summary>
    private const float PkReach = 9;
    /// <summary>On a PK map a bot is shown this many other players, so it sees who it may fight (elsewhere one, ViewCap).</summary>
    private const ulong PkViewCap = 30;
    private static readonly TimeSpan InviteAgain = TimeSpan.FromSeconds(30);
    private MvpRaid? _raid;
    private DateTime _invitedAt;

    /// <summary>Online, out of any party, raid and siege, and not a vendor: the coordinator may take it.</summary>
    public bool FreeForRaid => Online && entry.Prepared && entry.Persona != Persona.Vendor && !InParty
        && Volatile.Read(ref _raid) == null && _scene != SiegeScene;

    public void Enlist(MvpRaid raid) => Volatile.Write(ref _raid, raid);

    /// <summary>Leads its raid (the leader), waits for the leader's invitation (the others), or leaves an ended raid;
    /// false when a member is in the raid's party and FakePlayer.Party leads it.</summary>
    private async Task<bool> RaidStepAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _raid) is not { } raid) return false;
        if (raid.Over || !raid.Members.Contains(this)) { await LeaveRaidAsync(ct); return true; }
        if (raid.Leader != this)
        {
            if (InParty) return false;
            await PauseAsync(2, 4, ct); // the leader's invitation is taken as it comes (OnPartyInvite)
            return true;
        }
        if (!await RaidPartyAsync(raid, ct)) { await PauseAsync(5, 10, ct); return true; }
        if (raid.Target is not { } target) { await PauseAsync(5, 10, ct); return true; }
        if (_scene != target.Map)
        {
            try { await TravelAsync(target.Map, ct); }
            catch (InvalidOperationException) { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            return true;
        }
        await HuntAsync(target, ct);
        return true;
    }

    /// <summary>The leader's party: created once, its missing members invited every half minute; false until it exists.</summary>
    private async Task<bool> RaidPartyAsync(MvpRaid raid, CancellationToken ct)
    {
        long party;
        lock (_partyLock) party = _partyId;
        if (party == 0)
        {
            var created = TeamCreateResp.Parser.ParseFrom(await RequestAsync(TeamService.TeamCreate,
                new TeamCreateReq { TeamType = TeamType.Forty }, ct));
            if (created.Result == ErrorCodes.AlreadyInParty)
            {
                await SendAsync(TeamService.TeamLeave, new Google.Protobuf.WellKnownTypes.Empty(), ct); // an old party
                return false;
            }
            if (created.Result != 0) return false;
            lock (_partyLock) _partyId = created.TeamId;
            PopulationContext.Log($"{entry.Name}: leads a rival raid");
        }
        if (DateTime.UtcNow - _invitedAt < InviteAgain) return true;
        _invitedAt = DateTime.UtcNow;
        HashSet<long> joined;
        lock (_partyLock) joined = [.. _members.Keys];
        foreach (var member in raid.Members.Where(m => m != this && m.Online && m.Entry.RoleId != 0 && !joined.Contains(m.Entry.RoleId)))
            await SendAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = member.Entry.RoleId }, ct);
        return true;
    }

    /// <summary>On its MVP's map: the enemy close by, else the boss in sight, else on towards where it stands.</summary>
    private async Task HuntAsync(MvpTarget target, CancellationToken ct)
    {
        if (await DodgeAsync(ct)) return;
        if (await EvadeAsync(ct)) { await DrinkAsync(ct); return; }
        if (await DrinkAsync(ct)) return;
        SyncPosition();
        if (EnemyPlayer(_pos.X, _pos.Z) is { } enemy) { await StrikePlayerAsync(enemy, ct); return; }
        var boss = _view.Monsters().Where(m => ctx.Support.IsBoss((long)m.EntityTemplateID))
            .OrderBy(m => Distance(m.MovementInfo.Pos)).FirstOrDefault();
        if (boss != null) { await StrikeAsync(boss, ct); return; }
        if (Map is not { } map) { await PauseAsync(2, 4, ct); return; }
        var at = map.Standable(map.Grounded(new MapPoint(target.X, _pos.Y, target.Z, 0)), radius: 4);
        if (Distance(new Vec3 { X = at.X, Z = at.Z }) < 6 || !await GoAsync(at, ct, short_: 4f)) await ShuffleAsync(8, ct);
    }

    /// <summary>Out of an ended raid: its party left (42008), back to its own life at home.</summary>
    private async Task LeaveRaidAsync(CancellationToken ct)
    {
        Volatile.Write(ref _raid, null);
        bool inParty;
        lock (_partyLock) inParty = _partyId != 0;
        if (inParty) await SendAsync(TeamService.TeamLeave, new Google.Protobuf.WellKnownTypes.Empty(), ct);
        ClearParty();
        PopulationContext.Log($"{entry.Name}: back from the rival raid");
        await GoHomeAsync(ct);
    }

    /// <summary>The leader of this bot's rival raid, while it is one's member (it takes that leader's invitation).</summary>
    private long RaidLeader => Volatile.Read(ref _raid) is { Over: false } raid && raid.Leader != this ? raid.Leader.Entry.RoleId : 0;

    private bool LeadsRaid => Volatile.Read(ref _raid) is { Over: false } raid && raid.Leader == this;

    /// <summary>On a PK map, the nearest living player the server shows this bot in the enemy camp within
    /// <see cref="PkReach"/> of (x, z): the bot itself, or the host it guards.</summary>
    private EntityBase? EnemyPlayer(float x, float z)
    {
        if (!ctx.PkMaps.Contains(_scene)) return null;
        return _view.Players()
            .Where(p => p.EntityCamp == EnemyCamp && p.State != EntityState.Dead && Apart(p.MovementInfo.Pos, new Vec3 { X = x, Z = z }) <= PkReach)
            .OrderBy(p => Distance(p.MovementInfo.Pos)).FirstOrDefault();
    }

    /// <summary>Fights an enemy player (skills and swings, as at a monster) while it stays in sight and in reach; a
    /// refused swing is death, so it revives.</summary>
    private async Task StrikePlayerAsync(EntityBase target, CancellationToken ct)
    {
        float reach = Math.Max(2.5f, AttackReach() - 0.5f);
        var at = target.MovementInfo.Pos;
        if (Distance(at) > reach && !await GoAsync(new MapPoint(at.X, at.Y, at.Z, 0), ct, short_: reach - 1)) { await ShuffleAsync(6, ct); return; }
        long skill = ctx.Plan.NormalAttack(_job) is > 0 and var s ? s : FallbackAttack;
        for (int swing = 0; swing < 8; swing++)
        {
            var current = _view.Players().FirstOrDefault(p => p.Uuid == target.Uuid && p.EntityCamp == EnemyCamp && p.State != EntityState.Dead);
            if (current == null || Distance(current.MovementInfo.Pos) > reach + 2) return; // gone, dead or out of reach: look again
            if (await DodgeAsync(ct)) return;
            if (await EvadeAsync(ct)) { await DrinkAsync(ct); return; }
            await DrinkAsync(ct);
            if (!await AttackOnceAsync(current, skill, ct)) { await ReviveAsync(ct); return; }
        }
    }
}
