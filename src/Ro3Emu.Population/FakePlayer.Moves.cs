using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

public sealed partial class FakePlayer
{
    private float StepSeconds => ctx.Options.StepMs / 1000f;

    private MapWorld? Map => ctx.World.Map(_scene);

    /// <summary>World-map travel (30012) to a map's born point, then the client's scene load (30005).</summary>
    private async Task TravelAsync(long target, CancellationToken ct)
    {
        var bot = _bot ?? throw new IOException("not connected");
        await SwitchAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = target },
            body => SceneTeleportRsp.Parser.ParseFrom(body).Result, ct);
        await bot.JoinChatAsync(MapChat);
        // Everyone arrives on the map's born point: step off it so arrivals do not pile up there.
        await ShuffleAsync(8, ct);
    }

    /// <summary>
    /// A request that moves the player to another scene (30012 travel, 30400/30402 instance entry and exit): its answer,
    /// then the 70010 switch and the client's scene load. False when the answer refuses.
    /// </summary>
    private async Task<bool> SwitchAsync(uint msgId, Google.Protobuf.IMessage request, Func<byte[], int> result, CancellationToken ct,
        bool refusalThrows = true)
    {
        var bot = _bot ?? throw new IOException("not connected");
        using var reply = bot.Inbox.Expect(e => e.IsGame(msgId));
        using var switchScene = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        using var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        using var end = bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
        Interlocked.Increment(ref _switching);
        try
        {
            await SendAsync(msgId, request, ct);
            int code = result((await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            if (code != 0)
            {
                if (refusalThrows) throw new InvalidOperationException($"scene change {msgId} refused: {code}");
                return false;
            }
            var scene = SwitchSceneLineResp.Parser.ParseFrom((await switchScene.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            await LoadAsync(scene.SceneEntry, self, end, ct);
            return true;
        }
        finally { Interlocked.Decrement(ref _switching); }
    }

    /// <summary>The client's half of a scene switch: 30005 with the view cap, then its own entity and the end frame.</summary>
    private async Task LoadAsync(long sceneEntry, BotExpectation self, BotExpectation end, CancellationToken ct)
    {
        var bot = _bot ?? throw new IOException("not connected");
        using var loaded = bot.Inbox.Expect(e => e.IsGame(MsgId.SceneLoaded));
        await SendAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sceneEntry, ViewMaxCount = ViewCapIn(sceneEntry) }, ct);
        var load = SceneLoadedResp.Parser.ParseFrom((await loaded.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
        if (load.Result != 0) throw new InvalidOperationException($"scene {sceneEntry} load refused: {load.Result}");
        var me = EntitySelf.Parser.ParseFrom((await self.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body).Base;
        await end.WaitAsync(TimeSpan.FromSeconds(30), ct);
        _scene = sceneEntry;
        _camp = me.EntityCamp;
        _pos = new MapPoint(me.MovementInfo.Pos.X, me.MovementInfo.Pos.Y, me.MovementInfo.Pos.Z, 0);
    }

    /// <summary>A 70010 the player did not ask for (an instance sending it home): load the scene as the client does.</summary>
    private void OnSwitchScene(byte[] body)
    {
        if (!Online || Volatile.Read(ref _switching) != 0 || _bot is not { } bot) return; // the login loads its own scene
        var scene = SwitchSceneLineResp.Parser.ParseFrom(body);
        if (scene.Result != 0) return;
        var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        var end = bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
        _ = Task.Run(async () =>
        {
            using (self) using (end)
                try { await LoadAsync(scene.SceneEntry, self, end, _life); }
                catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or InvalidOperationException) { }
        });
    }

    /// <summary>
    /// The players a fake player is shown: the client's own view cap (30005/30007 ViewMaxCount, World.SetViewLimitAsync)
    /// at its least, so the server does not stream every crowd's movement to bots that never look at it.
    /// </summary>
    private const ulong ViewCap = 1;
    private ulong _viewCap = ViewCap;

    /// <summary>The view cap a scene is loaded with: more players on a PK map (FakePlayer.Raid.PkViewCap).</summary>
    private ulong ViewCapIn(long sceneEntry) => ctx.PkMaps.Contains(sceneEntry) ? Math.Max(_viewCap, PkViewCap) : _viewCap;
    private int _switching;
    /// <summary>The player's camp in the current scene (EntityBase.entityCamp: a siege's attackers 1, defenders 2).</summary>
    private long _camp;

    /// <summary>Map chat group of the current map ("4_&lt;sceneId&gt;", BotCli).</summary>
    private string MapChat => $"4_{_scene}";

    /// <summary>Walks a nav path to <paramref name="target"/>, stopping <paramref name="short_"/> metres before it.
    /// Each bot takes its corners a little off the shortest line and now and then stops on the way, so bots bound for
    /// one spot do not walk it in single file.</summary>
    private async Task<bool> GoAsync(MapPoint target, CancellationToken ct, float short_ = 0)
    {
        SyncPosition();
        if (Map is not { } map || NavPath.Find(map, _pos, target) is not { } corners) return false;
        for (int i = 0; i < corners.Count; i++)
        {
            bool last = i == corners.Count - 1;
            var to = last ? corners[i] : Swerve(map, corners[i], corners[i + 1]);
            await WalkLegAsync(map, to, last ? short_ : 0, stop: last, ct);
            if (!last && _random.NextDouble() < 0.1) await Task.Delay(TimeSpan.FromSeconds(1 + _random.NextDouble() * 3), ct);
        }
        return true;
    }

    /// <summary>A path corner moved up to 2 m, kept only when both legs through it still pass the walk check.</summary>
    private MapPoint Swerve(MapWorld map, MapPoint corner, MapPoint next)
    {
        if (map.Nav is not { } nav) return corner;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            float x = corner.X + (float)(_random.NextDouble() * 4 - 2), z = corner.Z + (float)(_random.NextDouble() * 4 - 2);
            if (nav.WalkHeight(x, z, corner.Y) is not { } y || !nav.IsWalkable(x, y, z)) continue;
            if (nav.CanWalk(_pos.X, _pos.Y, _pos.Z, x, z) && nav.CanWalk(x, y, z, next.X, next.Z)) return new MapPoint(x, y, z, 0);
        }
        return corner;
    }

    /// <summary>One straight leg in client-sized steps (WALK every 0.1 s, BotCli), height kept on the nav layer.</summary>
    private async Task WalkLegAsync(MapWorld map, MapPoint to, float short_, bool stop, CancellationToken ct)
    {
        float dx = to.X - _pos.X, dz = to.Z - _pos.Z, length = MathF.Sqrt(dx * dx + dz * dz) - short_;
        if (length <= 0.05f) return;
        float dir = (MathF.Atan2(dx, dz) * 180f / MathF.PI + 360f) % 360f;
        int steps = Math.Max(1, (int)MathF.Ceiling(length / (_walkSpeed * StepSeconds)));
        float ux = dx / (length + short_), uz = dz / (length + short_);
        var start = _pos;
        for (int s = 1; s <= steps; s++)
        {
            float d = length * s / steps;
            var next = map.Grounded(new MapPoint(start.X + ux * d, _pos.Y, start.Z + uz * d, dir));
            bool final = stop && s == steps;
            await SendAsync(MsgId.Walk, new WalkReq
            {
                Pos = new Vec3 { X = next.X, Y = next.Y, Z = next.Z }, Dir = dir, Face = dir, BStop = final, SpeedRate = 1,
            }, ct);
            _pos = next;
            await Task.Delay(TimeSpan.FromSeconds(StepSeconds), ct);
        }
    }

    /// <summary>Takes the position the server moved the player to (a revive, a teleport), if it did.</summary>
    private void SyncPosition()
    {
        if (_view.TakeSelfMove() is { } p) _pos = new MapPoint(p.X, p.Y, p.Z, 0);
    }

    /// <summary>A few steps around a point: a walkable spot within <paramref name="radius"/> m in a straight line.</summary>
    private async Task ShuffleAsync(float radius, CancellationToken ct)
    {
        SyncPosition();
        if (Map is not { } map) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float a = (float)(_random.NextDouble() * MathF.Tau), r = 1 + (float)_random.NextDouble() * radius;
            float x = _pos.X + r * MathF.Cos(a), z = _pos.Z + r * MathF.Sin(a);
            if (map.Nav?.WalkHeight(x, z, _pos.Y) is not { } y || !map.IsWalkable(x, y, z)) continue;
            if (map.Nav is { } nav && !nav.CanWalk(_pos.X, _pos.Y, _pos.Z, x, z)) continue;
            await WalkLegAsync(map, new MapPoint(x, y, z, 0), 0, stop: true, ct);
            return;
        }
    }

    private async Task FaceAsync(float degrees, CancellationToken ct) =>
        await SendAsync(MsgId.ChangeFace, new ChangeFaceReq { Angle = degrees }, ct);
}
