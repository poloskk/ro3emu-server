using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>A player on the first hunting quests kills a monster and goes on playing: the session still answers.</summary>
    [ClientDataFact]
    public async Task AKillThatCountsForQuestsLeavesTheSessionAnswering()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest accept 301010");
        await Gm("alice", "!quest accept 10101220");
        for (int kill = 0; kill < 3; kill++)
        {
            var target = await Target("alice", stationary: true);
            await Loop.InvokeAsync(() => { target.Hp = 1; return Task.CompletedTask; });
            for (int swing = 0; ; swing++)
            {
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == target.Uid));
                if (hit.Dsts.Any(d => d.DstID == target.Uid && d.IsDead)) break;
                Assert.True(swing < 20, "the basic attack never landed");
            }
            var pong = alice.HeartbeatAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await pong;
        }
    }
}
