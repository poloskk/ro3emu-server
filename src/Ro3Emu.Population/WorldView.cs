using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// What a fake player needs to know of the world, from the messages the client gets: the scene it is in (70010
/// SwitchSceneLine), the monsters and other players in view (AOI 31037 enter/leave, 31038 movement). A slimmer kin
/// of the load test's WorldReducer, which also counts every message for its reports.
/// </summary>
public sealed class WorldView
{
    /// <summary>EntityBase.EntityType of a monster and of a player (UT_Monster, UT_Player).</summary>
    private const int Monster = 1, Player = 3;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, EntityBase> _monsters = [];
    private readonly Dictionary<ulong, EntityBase> _players = [];

    public long SceneEntry { get; private set; }
    /// <summary>The player's own role id, which is not one of the others in view.</summary>
    public long Self { get; set; }
    private Vec3? _selfMoved;

    /// <summary>Where the server last put the player itself (a 31038 to its own id: a revive or another teleport,
    /// SceneService.TeleportInMapAsync), once; null when it has not moved it since.</summary>
    public Vec3? TakeSelfMove()
    {
        lock (_lock) { var moved = _selfMoved; _selfMoved = null; return moved; }
    }

    public void Observe(BotEvent e)
    {
        if (e.Kind != BotEventKind.Game) return;
        try
        {
            lock (_lock)
            {
                if (e.MsgId == MsgId.SwitchSceneLine) SwitchScene(SwitchSceneLineResp.Parser.ParseFrom(e.Body));
                else if (e.MsgId == GameAoiSink.SyncAoiView) View(AoIViewChangeData.Parser.ParseFrom(e.Body));
                else if (e.MsgId == GameAoiSink.MovementChange) Move(MovementChangeNtf.Parser.ParseFrom(e.Body));
            }
        }
        catch (Google.Protobuf.InvalidProtocolBufferException) { }
    }

    private void SwitchScene(SwitchSceneLineResp scene)
    {
        if (scene.Result != 0) return; // a refusal leaves the loaded world as it was
        _monsters.Clear();
        _players.Clear();
        _selfMoved = null; // the new scene's own entity (EntitySelf) places the player
        SceneEntry = scene.SceneEntry;
    }

    private void View(AoIViewChangeData change)
    {
        if (change.BReset) { _monsters.Clear(); _players.Clear(); }
        foreach (var uid in change.LeaveAoi) { _monsters.Remove(uid); _players.Remove(uid); }
        foreach (var entity in change.Bases)
            if (entity.EntityType == Monster) _monsters[entity.Uuid] = entity;
            else if (entity.EntityType == Player && (long)entity.Uuid != Self) _players[entity.Uuid] = entity;
    }

    private void Move(MovementChangeNtf movement)
    {
        if (movement.MovementInfo == null) return;
        if (movement.Uid == Self) { _selfMoved = movement.MovementInfo.Pos; return; }
        if (_monsters.TryGetValue((ulong)movement.Uid, out var monster)) monster.MovementInfo = movement.MovementInfo;
        else if (_players.TryGetValue((ulong)movement.Uid, out var player)) player.MovementInfo = movement.MovementInfo;
    }

    public EntityBase[] Monsters()
    {
        lock (_lock) return [.. _monsters.Values.Where(x => x.MovementInfo?.Pos != null).Select(x => x.Clone())];
    }

    public EntityBase[] Players()
    {
        lock (_lock) return [.. _players.Values.Where(x => x.MovementInfo?.Pos != null).Select(x => x.Clone())];
    }
}
