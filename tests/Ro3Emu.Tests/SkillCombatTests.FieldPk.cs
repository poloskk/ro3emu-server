using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// ServerOptions.MvpMapsPk: on an MVP field map (MVPConfig 1, Angeling's 10214) two players outside one party fight
    /// each other and each is shown the other in the enemy camp (FieldPk.EnemyCamp); in town neither can hit the other,
    /// and once in one party they are shown in their own camp again and cannot.
    /// </summary>
    [ClientDataFact]
    public async Task PlayersOnAnMvpMapFightUnlessInOneParty()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long bobId = (long)bob.Self!.Uuid;
        var attack = new CastSkillReq { SkillId = 1000001, TargetId = bobId };
        await alice.SendGameAsync(CombatService.UseSkill, attack);
        Assert.Equal(ErrorCodes.SkillTargetInvalid, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await Gm("alice", "!warp 10214");
        await Arrive(alice);
        await Gm("bob", "!warp 10214");
        await Arrive(bob);
        // Alice saw Bob in town too (in his own camp): on the map he comes into her view in the enemy camp.
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.Uuid == (ulong)bobId && b.EntityCamp == FieldPk.EnemyCamp));
        await Loop.InvokeAsync(() => { Session("alice").Position = Session("bob").Position.Clone(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, attack);
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == bobId)))
            .Dsts.Single(d => d.DstID == bobId).Damage > 0);

        await PartyOf(alice, bob);
        var friend = await Next(alice, CombatService.EntityBaseChange, EntityBase.Parser, e => e.Uuid == (ulong)bobId && e.EntityCamp != FieldPk.EnemyCamp);
        Assert.Equal(1, friend.EntityCamp);
        await Task.Delay(1500); // past the normal attack's own pace: a refusal now is the party's
        await alice.SendGameAsync(CombatService.UseSkill, attack);
        Assert.Equal(ErrorCodes.SkillTargetInvalid, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>
    /// ServerOptions.RivalRaidsMatchPlayers: two fake players in one party (a rival raid) on an MVP map fight with at
    /// least the combat attributes of the real player on that map (FieldPk.Champion); off it, their own.
    /// </summary>
    [ClientDataFact]
    public async Task ARivalRaidOnAnMvpMapFightsAsStrongAsThePlayerThere()
    {
        await using var alice = await InWorld("Alice");
        await using var rival = await InWorld("pop0001");
        await using var mate = await InWorld("pop0002");
        await Gm("alice", "!lvl 90 50");
        int[] combat = [101, 103, 104, 105, 106, 109, 110, 111];
        long[] Sheet(string name) => Loop.InvokeAsync(() =>
        {
            var stats = Character.Stats(Session(name));
            return Task.FromResult(combat.Select(id => stats[id]).ToArray());
        }).GetAwaiter().GetResult();
        await PartyOf(rival, mate);
        var own = Sheet("pop0001");
        foreach (var (bot, account) in new[] { (alice, "alice"), (rival, "pop0001") })
        {
            await Gm(account, "!warp 10214");
            await Arrive(bot);
        }
        var player = Sheet("alice");
        Assert.True(player[0] > own[0], $"a level 90 player's HP {player[0]} over a new role's {own[0]}");
        var matched = Sheet("pop0001");
        for (int i = 0; i < combat.Length; i++) Assert.True(matched[i] >= player[i], $"attribute {combat[i]}: {matched[i]} < {player[i]}");
        Assert.Equal(own, Sheet("pop0002")); // still in town
    }

    /// <summary>A server-sent map change followed as the client does: 70010, then 30005 and the new ENTITYSELF.</summary>
    private static async Task Arrive(BotClient bot)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }
}
