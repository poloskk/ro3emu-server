using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// The guild siege (TerritoryWarsService.Siege*, the game's War of Emperium): while its window is open a member of a
/// paired guild enters the siege instance (30400, scene 2230101) for the city its guild is paired in (44402
/// currentCityId). Attackers (camp 1) go for the crystal (NPC 4900009 at scene point 44) and hit defenders on the way;
/// defenders (camp 2) hold the crystal and hit attackers. A refused swing is usually death: the player revives (31013).
/// It leaves (30402) when the city's fight is over. How bots fight is a population choice (inferred).
/// </summary>
public sealed partial class FakePlayer
{
    public const long SiegeScene = 2230101;
    private const ulong SiegeCrystal = 4900009;
    private const int CrystalPoint = 44, AttackCamp = 1;
    /// <summary>In the siege a player sees this many others (outside it the bots see one, FakePlayer.ViewCap).</summary>
    private const ulong SiegeViewCap = 30;
    private DateTime _nextSiegeTry;

    /// <summary>44402: the siege panel as this player's guild sees it.</summary>
    public async Task<SiegeBaseInfoNtf?> SiegeInfoAsync(CancellationToken ct) =>
        GetSiegeBaseInfoRsp.Parser.ParseFrom(await RequestAsync(TerritoryWarsService.SiegeBaseQuery, new GetSiegeBaseInfoReq(), ct)).SiegeBaseInfo;

    /// <summary>Joins the open siege of its guild's city and fights it out; false when there is none to join.</summary>
    private async Task<bool> SiegeAsync(CancellationToken ct)
    {
        if (!ctx.SiegeOpen || entry.GuildId == 0 || DateTime.UtcNow < _nextSiegeTry) return false;
        _nextSiegeTry = DateTime.UtcNow.AddMinutes(2);
        try
        {
            if (await SiegeInfoAsync(ct) is not { CurrentCityId: > 0 } info
                || !info.BattleInfos.TryGetValue(info.CurrentCityId, out var city) || city.ActivityState != SiegeCityActivityState.Fighting)
                return false;
            _viewCap = SiegeViewCap;
            bool entered = await SwitchAsync(30400, new EnterDungeonReq
            {
                SceneEntry = SiegeScene, InitData = new DungeonInitData { SiegeInitData = new SiegeInitData { CityId = info.CurrentCityId } },
            }, body => EnterDungeonRsp.Parser.ParseFrom(body).Result, ct, refusalThrows: false);
            if (!entered) { _viewCap = ViewCap; return false; }
            PopulationContext.Log($"{entry.Name}: in the siege of city {info.CurrentCityId} as {(_camp == AttackCamp ? "attacker" : "defender")}");
            try { await FightSiegeAsync(info.CurrentCityId, ct); }
            finally
            {
                _viewCap = ViewCap;
                if (_scene == SiegeScene)
                    await SwitchAsync(30402, new LeaveDungeonReq(), _ => 0, ct, refusalThrows: false);
                await SendAsync(MsgId.ChangeViewCfg, new ChangeViewCfgReq { ViewMaxCount = ViewCap }, ct);
            }
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private async Task FightSiegeAsync(int cityId, CancellationToken ct)
    {
        var crystalAt = Map?.Points.TryGetValue(CrystalPoint, out var point) == true ? point : (MapPoint?)null;
        var check = DateTime.UtcNow.AddSeconds(30);
        long skill = ctx.Plan.NormalAttack(_job) is > 0 and var s ? s : FallbackAttack;
        while (!ct.IsCancellationRequested && Online && _scene == SiegeScene)
        {
            if (DateTime.UtcNow >= check)
            {
                check = DateTime.UtcNow.AddSeconds(30);
                if (await SiegeInfoAsync(ct) is not { } info || !info.BattleInfos.TryGetValue(cityId, out var city)
                    || city.ActivityState != SiegeCityActivityState.Fighting) return;
            }
            var target = SiegeTarget(crystalAt);
            if (target == null)
            {
                // Attackers march on the crystal; defenders keep near it.
                if (crystalAt is { } c && (_camp == AttackCamp || Distance(new Vec3 { X = c.X, Z = c.Z }) > 12))
                    await GoAsync(Jitter(c), ct, short_: 3f);
                else await ShuffleAsync(4, ct);
                await Task.Delay(TimeSpan.FromSeconds(1 + _random.NextDouble()), ct);
                continue;
            }
            // The crystal stands on a cell of its own that is not walkable: walk to the nearest one that is.
            var at = target.MovementInfo.Pos;
            if (Distance(at) > 3f && Map is { } map) await GoAsync(map.Standable(map.Grounded(new MapPoint(at.X, at.Y, at.Z, 0)), radius: 4), ct, short_: 1.5f);
            if (!await SwingAsync(skill, (long)target.Uuid, ct)) await ReviveAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(1 + _random.NextDouble() * 0.5), ct);
        }
    }

    /// <summary>Attackers: the crystal in sight, else the nearest defender within 12 m. Defenders: the nearest attacker
    /// within 15 m. Players of the other camp are the enemy (entityCamp).</summary>
    private Romsg.EntityBase? SiegeTarget(MapPoint? crystalAt)
    {
        var enemies = _view.Players().Where(p => p.EntityCamp != _camp && p.State != EntityState.Dead)
            .OrderBy(p => Distance(p.MovementInfo.Pos)).ToArray();
        if (_camp == AttackCamp)
            return _view.Monsters().FirstOrDefault(m => m.EntityTemplateID == SiegeCrystal)
                ?? enemies.FirstOrDefault(p => Distance(p.MovementInfo.Pos) <= 12);
        return enemies.FirstOrDefault(p => Distance(p.MovementInfo.Pos) <= 15);
    }

    /// <summary>31102 at a target; false when refused (the caster is dead). A target another player just killed
    /// (SkillTargetInvalid) is no death: in a party several bots swing at the one the leader marked.</summary>
    private async Task<bool> SwingAsync(long skill, long target, CancellationToken ct)
    {
        var reply = CastSkillRsp.Parser.ParseFrom(await RequestAsync(CombatService.UseSkill, new CastSkillReq { SkillId = (int)skill, TargetId = target }, ct));
        return reply.Result is 0 or ErrorCodes.SkillTargetInvalid;
    }

    /// <summary>31013: back on its feet where the server put it: its 31038 teleport, else the map's revive point or
    /// born point (CombatService.ReviveClaimedAsync).</summary>
    private async Task ReviveAsync(CancellationToken ct)
    {
        var ack = EntityReliveAck.Parser.ParseFrom(await RequestAsync(CombatService.Relive, new EntityReliveReq(), ct));
        if (ack.Result != 0) return;
        if (Map is { } map && (map.Revive ?? map.Born) is { } at) _pos = at;
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        SyncPosition(); // the server's own 31038 says exactly where
    }
}
