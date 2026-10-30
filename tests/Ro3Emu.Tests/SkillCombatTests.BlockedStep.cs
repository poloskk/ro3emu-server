using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int BasicAttack = 1000001; // _iPassBlock 2, _iDistanceMax 300
    private const int GroundSkill = 1231101, GroundSkillGroup = 12311; // _iRelease 8, _iPassBlock 2, _iDistanceMax 1200

    /// <summary>
    /// A real wall of the starting map between the player and a monster in reach: in auto battle the refused basic
    /// attack (3026) walks the player (MoveTransServerControl) to a spot with a clear line; out of auto battle it stays where it is.
    /// </summary>
    [ClientDataFact]
    public async Task AutoBattleStepsPastAWallThatBlocksItsAttack()
    {
        await using var alice = await InWorld("Alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)?.Nav;
        Assert.NotNull(nav);
        var (from, at) = WallAcross(nav!, Session("alice").Position);
        var monster = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(s, new MapPoint(from.X, from.Y, from.Z, 0));
            var monsters = _app.Services.GetRequiredService<MonsterService>();
            var template = monsters.Template(5000001)! with { RoamDistance = 0 };
            return (await monsters.SpawnSingleAsync(s.SceneEntry, 5000001, new MapPoint(at.X, at.Y, at.Z, 0), s.InstanceId,
                customTemplate: template))!;
        });
        var start = Session("alice").Position.Clone();
        Assert.False(nav!.CanWalk(start.X, start.Y, start.Z, monster.X, monster.Z));

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BasicAttack, TargetId = monster.Uid });
        Assert.Equal(ErrorCodes.SkillBlocked, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() => { Assert.Equal(start, Session("alice").Position); return Task.CompletedTask; });

        await Loop.InvokeAsync(() => { Session("alice").AutoBattle = true; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BasicAttack, TargetId = monster.Uid });
        Assert.Equal(ErrorCodes.SkillBlocked, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        // The setup's teleport sent one too: the step is the client walk the server asks for.
        var moved = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => m.Uid == (long)alice.Self!.Uuid && m.TransType == MovementTransitionType.MoveTransServerControl);
        var to = moved.MovementInfo.Pos;
        Assert.NotEqual(start, to);
        Assert.True(MonsterService.Distance(start.X, start.Z, to.X, to.Z) <= 10.01f);
        Assert.True(nav.CanWalk(to.X, to.Y, to.Z, monster.X, monster.Z));
        // The client walks there and reports its walks; the server does not move it.
        await Loop.InvokeAsync(() => { Assert.Equal(start, Session("alice").Position); return Task.CompletedTask; });

        // A ground skill aimed at the monster's feet (no target id, as auto battle casts them).
        await Teach("alice", GroundSkillGroup);
        await Loop.InvokeAsync(() => { Session("alice").NextBlockedStepUtc = default; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = GroundSkill, TargetPos = monster.Pos });
        Assert.Equal(ErrorCodes.SkillBlocked, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var walked = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => m.Uid == (long)alice.Self!.Uuid && m.TransType == MovementTransitionType.MoveTransServerControl);
        Assert.True(nav.CanWalk(walked.MovementInfo.Pos.X, walked.MovementInfo.Pos.Y, walked.MovementInfo.Pos.Z, monster.X, monster.Z));
    }

    /// <summary>
    /// Near <paramref name="around"/>: a walkable point and a walkable point 1.5-2.5 m from it with a wall in between, for
    /// which <see cref="CombatService.ClearSpot"/> finds a way round.
    /// </summary>
    private static (Vec3 From, Vec3 At) WallAcross(NavGrid nav, Vec3 around)
    {
        for (int r = 0; r <= 60; r++)
            for (int dx = -r; dx <= r; dx++)
                foreach (int dz in new[] { -r, r })
                    for (int side = 0; side < 2; side++)
                    {
                        float x = MathF.Floor(around.X) + (side == 0 ? dx : dz) + 0.5f, z = MathF.Floor(around.Z) + (side == 0 ? dz : dx) + 0.5f;
                        if (nav.WalkHeight(x, z, around.Y) is not { } y || MathF.Abs(y - around.Y) > 3) continue;
                        var from = new Vec3 { X = x, Y = y, Z = z };
                        for (int d = 0; d < 8; d++)
                            foreach (float reach in new[] { 1.5f, 2.5f })
                            {
                                float ax = x + reach * MathF.Sin(d * MathF.PI / 4), az = z + reach * MathF.Cos(d * MathF.PI / 4);
                                if (nav.WalkHeight(ax, az, y) is not { } ay || MathF.Abs(ay - y) > 0.5f || nav.CanWalk(x, y, z, ax, az)) continue;
                                var at = new Vec3 { X = ax, Y = ay, Z = az };
                                if (CombatService.ClearSpot(nav, from, at) != null) return (from, at);
                            }
                    }
        throw new InvalidOperationException("no wall near the starting point");
    }
}
