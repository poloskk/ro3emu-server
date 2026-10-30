using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// ServerOptions.PartyBotsMatchLeader: a fake player (account "pop...") fights with at least its real party leader's
    /// combat attributes, and only while in that party; a real player's own sheet is untouched.
    /// </summary>
    [ClientDataFact]
    public async Task APartysFakePlayerFightsWithItsLeadersAttributes()
    {
        await using var alice = await InWorld("Alice");
        await using var bot = await InWorld("pop0001");
        await Gm("alice", "!lvl 90 50");
        var character = _app.Services.GetRequiredService<CharacterService>();
        int[] combat = [101, 103, 104, 105, 106, 109, 110, 111];
        long[] Sheet(string name) => Loop.InvokeAsync(() =>
        {
            var stats = character.Stats(Session(name));
            return Task.FromResult(combat.Select(id => stats[id]).ToArray());
        }).GetAwaiter().GetResult();
        var leader = Sheet("alice");
        var alone = Sheet("pop0001");
        Assert.True(leader[0] > alone[0], $"a level 90 player's HP {leader[0]} over a new role's {alone[0]}");

        await PartyOf(alice, bot);
        var partied = Sheet("pop0001");
        for (int i = 0; i < combat.Length; i++) Assert.True(partied[i] >= leader[i], $"attribute {combat[i]}: {partied[i]} < {leader[i]}");
        Assert.Equal(leader, Sheet("alice"));

        await bot.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(bot, TeamService.TeamLeave, LeaveTeamResp.Parser)).Result);
        Assert.Equal(alone, Sheet("pop0001"));
    }

    private async Task PartyOf(BotClient leader, BotClient member)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
        Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
        Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
    }
}
