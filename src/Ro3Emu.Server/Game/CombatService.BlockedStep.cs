using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A player in auto battle whose skill is refused because a wall is in the way (3026 "Invalid skill wall-piercing
/// level", ValidateTarget's _iPassBlock 2 line check) keeps casting at the same monster, or the same ground point
/// (_iRelease 7 and 8, aimed at the monster's feet), from the same spot: the client has no such check (in-game 30.10:
/// 11609 refusals in a day, an unwalkable rock between the player and its target). The server walks the player to the
/// nearest walkable cell within <see cref="StepSearchRadius"/> with a clear line to the target (the client paths round
/// the obstacle itself); with none, a short random step. The walk is the client's own: MovementChangeNtf MoveTransServerControl makes
/// LC_Scene_Sync._syncUnitMoveState call SetMoveTargetPos on the controlled unit, and the client's walks then move it
/// on the server as usual. At most once per <see cref="StepInterval"/>. A player's choice, not the original server's.
/// </summary>
public sealed partial class CombatService
{
    private const int StepSearchRadius = 10, StepPathRadius = 15;
    private const float RandomStep = 2.5f;
    private static readonly (int, int)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private const int StepDirections = 16; // the random step's tries
    private static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(2);

    private async Task StepPastWallAsync(GameSession s, CastSkillReq req)
    {
        if (!s.AutoBattle || DateTime.UtcNow < s.NextBlockedStepUtc || _worldData.Map(s.SceneEntry)?.Nav is not { } nav) return;
        // The monster aimed at, else the ground point of a ground skill.
        var target = FindMonster(s.WorldScene, req.TargetId)?.Pos ?? req.TargetPos;
        if (target == null) return;
        s.NextBlockedStepUtc = DateTime.UtcNow + StepInterval;
        var from = s.Position;
        var clear = ClearSpot(nav, from, target);
        var to = clear ?? RandomStepEnd(nav, from, Random.Shared);
        _log.LogInformation("{Role} blocked by a wall at ({X:F1}, {Z:F1}) casting {Skill} at ({TX:F1}, {TZ:F1}): {Step}",
            s.Role!.Name, from.X, from.Z, req.SkillId, target.X, target.Z,
            to == null ? "no way to step" : $"{(clear != null ? "walks to a clear spot" : "random step")} ({to.X:F1}, {to.Z:F1})");
        if (to == null) return;
        await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        {
            Uid = s.Role.Id, TransType = MovementTransitionType.MoveTransServerControl,
            MovementInfo = new MovementInfo
            {
                Pos = to, Face = MathF.Atan2(target.X - to.X, target.Z - to.Z) * 180 / MathF.PI,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        });
    }

    /// <summary>
    /// The centre of the walkable cell nearest <paramref name="from"/> (1 m to <see cref="StepSearchRadius"/> away) with
    /// a clear line to <paramref name="target"/>, among the cells reached from <paramref name="from"/> by a walk that stays
    /// within <see cref="StepPathRadius"/>; null when none. Not a straight walk: the client's SetMoveTargetPos paths round
    /// the obstacle (in-game 30.10: 2097 of 2098 refused casts had such a cell, the clear side of the rock seldom a
    /// straight walk away).
    /// </summary>
    internal static Vec3? ClearSpot(NavGrid nav, Vec3 from, Vec3 target)
    {
        Vec3? best = null;
        float bestDistance = float.MaxValue;
        foreach (var (x, y, z) in Reachable(nav, from))
        {
            float distance = MathF.Sqrt((x - from.X) * (x - from.X) + (z - from.Z) * (z - from.Z));
            if (distance < 1 || distance > StepSearchRadius || distance >= bestDistance || !nav.CanWalk(x, y, z, target.X, target.Z)) continue;
            (best, bestDistance) = (new Vec3 { X = x, Y = y, Z = z }, distance);
        }
        return best;
    }

    /// <summary>Cell centres (with their ground height) a walk from <paramref name="from"/> reaches, cell to neighbouring
    /// cell (NavGrid.CanWalk: walkable, steps within its tolerance), without leaving <see cref="StepPathRadius"/>.</summary>
    private static IEnumerable<(float X, float Y, float Z)> Reachable(NavGrid nav, Vec3 from)
    {
        int cx = (int)MathF.Floor(from.X), cz = (int)MathF.Floor(from.Z);
        if (nav.WalkHeight(cx + 0.5f, cz + 0.5f, from.Y) is not { } y0) yield break;
        var seen = new HashSet<(int, int)> { (cx, cz) };
        var open = new Queue<(int X, int Z, float Y)>([(cx, cz, y0)]);
        while (open.TryDequeue(out var cell))
        {
            yield return (cell.X + 0.5f, cell.Y, cell.Z + 0.5f);
            foreach (var (dx, dz) in Neighbours)
            {
                int x = cell.X + dx, z = cell.Z + dz;
                if (Math.Abs(x - cx) > StepPathRadius || Math.Abs(z - cz) > StepPathRadius || !seen.Add((x, z))) continue;
                if (nav.CanWalk(cell.X + 0.5f, cell.Y, cell.Z + 0.5f, x + 0.5f, z + 0.5f) && nav.WalkHeight(x + 0.5f, z + 0.5f, cell.Y) is { } y)
                    open.Enqueue((x, z, y));
            }
        }
    }

    /// <summary>A walkable point <see cref="RandomStep"/> away in a random direction, or null when every way is closed.</summary>
    internal static Vec3? RandomStepEnd(NavGrid nav, Vec3 from, Random random)
    {
        double turn = random.NextDouble() * Math.PI * 2;
        for (int i = 0; i < StepDirections; i++)
        {
            double angle = turn + i * Math.PI * 2 / StepDirections;
            float x = from.X + RandomStep * (float)Math.Sin(angle), z = from.Z + RandomStep * (float)Math.Cos(angle);
            if (nav.CanWalk(from.X, from.Y, from.Z, x, z) && nav.WalkHeight(x, z, from.Y) is { } y) return new Vec3 { X = x, Y = y, Z = z };
        }
        return null;
    }
}
