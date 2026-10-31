using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// ServerOptions.MvpMapsPk: on a field map with an MVP (MVPConfig _iMapID) players fight each other, except party
/// members and guildmates (not original: a realm policy for an offline realm, so rival raids contest the MVP).
/// <para>The server decides every hit (CombatService.PlayerHostile). The client tells friend from enemy by camp:
/// LC_CampRelationUtils.IsEnemyByUnit compares the local player's entityCamp with the target's in the map's
/// campRelationshipConfig row (MapConfig _iCampRelationship; LC_CampRelationStrategy_Default.GetCampRelationByCampId).
/// These maps keep the default strategy (their _kPVPMod is empty, OverrideCampRelationLogic), whose row 1 makes camp 1
/// (players) and camp 2 enemies ("10", parsed base 2 = Enemy). So each viewer is shown a player it may fight in camp 2
/// and every other player in their own camp: camps are per viewer, the client keeps them as it got them
/// (LC_HeadInfoManager.SetHeadInfoDataByEntityBase).</para>
/// </summary>
public sealed class FieldPk(World world, SessionRegistry sessions, GameData data, IOptions<ServerOptions> options)
{
    /// <summary>The camp a viewer sees an enemy player in: campRelationshipConfig 1, [1][2] = "10" (Enemy).</summary>
    public const long EnemyCamp = 2;

    private HashSet<long>? _maps;

    /// <summary>The party a role is in, 0 when solo (TeamService.TeamOf, wired by the host).</summary>
    public Func<long, long>? TeamOf { get; set; }

    private HashSet<long> Maps => _maps ??= options.Value.MvpMapsPk
        ? data.Table("MVPConfig").Values.Select(row => new MVPConfig(row).MapId).Where(id => id > 0).ToHashSet()
        : [];

    /// <summary>Loaded on a public line of an MVP field map, where the PK rule holds.</summary>
    public bool On(GameSession s) => s.Role != null && !s.SceneLoading && s.InstanceId == 0 && Maps.Contains(s.SceneEntry);

    public bool Hostile(GameSession a, GameSession b)
    {
        if (ReferenceEquals(a, b) || !On(a) || !On(b) || a.WorldScene != b.WorldScene || a.Role!.Id == b.Role!.Id) return false;
        long team = TeamOf?.Invoke(a.Role.Id) ?? 0;
        if (team != 0 && team == TeamOf?.Invoke(b.Role.Id)) return false;
        long guild = a.Guild?.GuildId ?? 0;
        return guild <= 0 || guild != (b.Guild?.GuildId ?? 0);
    }

    /// <summary>
    /// ServerOptions.RivalRaidsMatchPlayers: for a fake player in a party of fake players (a rival raid) on an MVP map,
    /// a real player on that map, whose combat attributes it fights with at least (CharacterService.StrengthSource), as a
    /// real player's party bots fight with theirs; null for anyone else.
    /// </summary>
    public GameSession? Champion(GameSession s)
    {
        if (!On(s) || !TeamService.IsFakePlayer(s.Account) || (TeamOf?.Invoke(s.Role!.Id) ?? 0) == 0) return null;
        foreach (var other in world.PlayersIn(s.WorldScene))
            if (!ReferenceEquals(other, s) && other.Progress != null && !TeamService.IsFakePlayer(other.Account) && On(other)) return other;
        return null;
    }

    /// <summary>The camp <paramref name="viewer"/> is shown of <paramref name="subject"/>, or null for its own.</summary>
    public long? CampSeen(GameSession viewer, GameSession subject) => Hostile(viewer, subject) ? EnemyCamp : null;

    /// <summary><paramref name="entity"/> (a base of <paramref name="subject"/>) as <paramref name="viewer"/> is shown it.</summary>
    public EntityBase Seen(GameSession viewer, GameSession subject, EntityBase entity)
    {
        if (CampSeen(viewer, subject) is not { } camp || entity.EntityCamp == camp) return entity;
        var seen = entity.Clone();
        seen.EntityCamp = camp;
        return seen;
    }

    /// <summary>
    /// After a party roster changed (TeamService.RosterChanged): each of those roles on an MVP map and each player in
    /// view of it get the other's base again, in the camp they now see it in, so a new party member turns friendly at
    /// once and a leaver hostile. A guild change shows on the next view.
    /// </summary>
    public async Task RosterChangedAsync(IReadOnlyList<long> roles)
    {
        if (Maps.Count == 0) return;
        var changed = roles.ToHashSet();
        foreach (var s in sessions.All().Where(s => s.Role != null && changed.Contains(s.Role.Id) && On(s)).ToArray())
            foreach (var viewer in world.Viewers(s).Where(v => v.Role != null && v.WorldScene == s.WorldScene))
            {
                await SendBaseAsync(viewer, s);
                if (s.Visible.Contains(viewer)) await SendBaseAsync(s, viewer);
            }
    }

    private async Task SendBaseAsync(GameSession viewer, GameSession subject)
    {
        var entity = RoleSessionService.PlayerEntity(subject);
        entity.NotifyType = EntityBase.Types.NotifyType.Base;
        if (Hostile(viewer, subject)) entity.EntityCamp = EnemyCamp;
        try { await viewer.SendGameAsync(CombatService.EntityBaseChange, entity); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
