using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A player in auto battle whose skill is refused because a wall is in the way (3026 "Invalid skill wall-piercing
/// level", ValidateTarget's _iPassBlock 2 line check) keeps casting at the same monster from the same spot: the client
/// has no such check. The server walks the player to the nearest point within 8 m that it can walk to in a straight
/// line and that has a clear line to the monster; with none, a short random step. The walk is the client's own:
/// MovementChangeNtf MoveTransServerControl makes LC_Scene_Sync._syncUnitMoveState call SetMoveTargetPos on the
/// controlled unit, and the client's walks then move it on the server as usual. At most once per
/// <see cref="StepInterval"/>. A player's choice, not the original server's.
/// </summary>
public sealed partial class CombatService
{
    private static readonly float[] StepRadii = [1.5f, 2.5f, 3.5f, 5f, 6.5f, 8f];
    private const float RandomStep = 2.5f;
    private const int StepDirections = 16;
    private static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(2);

    private async Task StepPastWallAsync(GameSession s, CastSkillReq req)
    {
        if (!s.AutoBattle || DateTime.UtcNow < s.NextBlockedStepUtc || _worldData.Map(s.SceneEntry)?.Nav is not { } nav
            || FindMonster(s.WorldScene, req.TargetId) is not { } monster) return;
        s.NextBlockedStepUtc = DateTime.UtcNow + StepInterval;
        var from = s.Position;
        var clear = ClearSpot(nav, from, monster.Pos, Random.Shared);
        var to = clear ?? RandomStepEnd(nav, from, Random.Shared);
        _log.LogInformation("{Role} blocked by a wall at ({X:F1}, {Z:F1}) from monster {Monster} at ({MX:F1}, {MZ:F1}): {Step}",
            s.Role!.Name, from.X, from.Z, monster.Template.NpcId, monster.X, monster.Z,
            to == null ? "no way to step" : $"{(clear != null ? "walks to a clear spot" : "random step")} ({to.X:F1}, {to.Z:F1})");
        if (to == null) return;
        await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        {
            Uid = s.Role.Id, TransType = MovementTransitionType.MoveTransServerControl,
            MovementInfo = new MovementInfo
            {
                Pos = to, Face = MathF.Atan2(monster.X - to.X, monster.Z - to.Z) * 180 / MathF.PI,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        });
    }

    /// <summary>
    /// The point nearest <paramref name="from"/> (rings of <see cref="StepRadii"/>, <see cref="StepDirections"/> each)
    /// reached from it in a straight walk and with a clear line to <paramref name="target"/>; among a ring's points,
    /// the one nearest the target. Null when none.
    /// </summary>
    internal static Vec3? ClearSpot(NavGrid nav, Vec3 from, Vec3 target, Random random)
    {
        double turn = random.NextDouble() * Math.PI * 2; // not always the same side first
        foreach (float radius in StepRadii)
        {
            Vec3? best = null;
            float bestToTarget = float.MaxValue;
            for (int i = 0; i < StepDirections; i++)
            {
                double angle = turn + i * Math.PI * 2 / StepDirections;
                float x = from.X + radius * (float)Math.Sin(angle), z = from.Z + radius * (float)Math.Cos(angle);
                if (!nav.CanWalk(from.X, from.Y, from.Z, x, z) || nav.WalkHeight(x, z, from.Y) is not { } y
                    || !nav.CanWalk(x, y, z, target.X, target.Z)) continue;
                float toTarget = MathF.Sqrt((target.X - x) * (target.X - x) + (target.Z - z) * (target.Z - z));
                if (toTarget < bestToTarget) (best, bestToTarget) = (new Vec3 { X = x, Y = y, Z = z }, toTarget);
            }
            if (best != null) return best;
        }
        return null;
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
