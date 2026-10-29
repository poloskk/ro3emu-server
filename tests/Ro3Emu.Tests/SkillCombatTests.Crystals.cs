using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Ro3Emu:CrystalDrops: a mini-boss kill drops 3-8 Sacred Relic Crystals (item 3), an ordinary kill none
    /// while NormalChance is 0.</summary>
    [ClientDataFact]
    public async Task AMiniBossKillDropsSacredRelicCrystalsAndAnOrdinaryKillNone()
    {
        await using var alice = await InWorld("Alice");
        long Crystals() => Session("alice").Progress!.Currency.GetValueOrDefault(3)
            + Session("alice").Items.Where(i => i.Entry == 3).Sum(i => (long)i.Stack);
        long before = Crystals();
        await Kill(alice, await Target("alice", stationary: true)); // 5000001, enum_MonsterType MT_Normal
        Assert.Equal(before, Crystals());
        await Kill(alice, await Target("alice", stationary: true, npcId: 5001027)); // MT_MINI
        for (int i = 0; i < 100 && Crystals() == before; i++) await Task.Delay(20);
        Assert.InRange(Crystals() - before, 3, 8);
    }

    private async Task Kill(BotClient bot, MonsterUnit target)
    {
        await Loop.InvokeAsync(() => { target.Hp = 1; return Task.CompletedTask; });
        for (int swing = 0; ; swing++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == target.Uid));
            if (hit.Dsts.Any(d => d.DstID == target.Uid && d.IsDead)) break;
            Assert.True(swing < 20, "the basic attack never landed");
        }
        await bot.HeartbeatAsync().WaitAsync(TimeSpan.FromSeconds(5)); // the kill's reward has been applied
    }
}
