using Ro3Emu.Bot;
using Ro3Emu.Population;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The population runner's fake players on the synthetic town: each roster entry logs in with its own role, stays
/// online and walks; a second run brings back the same roles instead of creating new ones.
/// </summary>
public sealed class PopulationTests : IAsyncLifetime
{
    private readonly string _roster = Path.Combine(Path.GetTempPath(), "ro3emu-population-" + Guid.NewGuid().ToString("N") + ".json");
    private SyntheticHost _host = null!;

    public async Task InitializeAsync() => _host = await SyntheticHost.StartAsync();

    [Fact]
    public async Task FakePlayersComeOnlineWalkAndKeepTheirRoles()
    {
        var options = PopulationOptions.Parse(["--server", _host.Http, "--data", SyntheticData.Dir, "--bots", "3",
            "--roster", _roster, "--towns", SyntheticData.Town.ToString(), "--hunters", "0"]);

        await RunUntilAsync(options, sessions => sessions.Length == 3 && sessions.All(s => Moved(s)));
        var first = Roster.Load(_roster).Players;
        Assert.Equal(3, first.Select(p => p.RoleId).Distinct().Count());
        Assert.All(first, p => Assert.True(p.Prepared));

        await RunUntilAsync(options, sessions => sessions.Length == 3);
        var second = Roster.Load(_roster).Players;
        Assert.Equal(first.Select(p => (p.Account, p.Name, p.RoleId)), second.Select(p => (p.Account, p.Name, p.RoleId)));
    }

    /// <summary>
    /// GuildPlan's founder founds its fake guild and its member gets in by applying; a free agent applies to a real
    /// player's guild, is made strong, and joins it on the player's invitation.
    /// </summary>
    [Fact]
    public async Task FakeGuildsFormAndAFreeAgentJoinsARealPlayersGuild()
    {
        var plan = new[]
        {
            new RosterEntry { Account = "pop0001", Name = "Foundr", Town = SyntheticData.Town, Persona = Persona.Townie, GuildPlanned = true, Guild = "Odin", GuildLeader = true },
            new RosterEntry { Account = "pop0002", Name = "Membr", Town = SyntheticData.Town, Persona = Persona.Townie, GuildPlanned = true, Guild = "Odin" },
            new RosterEntry { Account = "pop0003", Name = "Agentt", Town = SyntheticData.Town, Persona = Persona.Townie, GuildPlanned = true, FreeAgent = true },
        };
        File.WriteAllText(_roster, System.Text.Json.JsonSerializer.Serialize(plan));
        await using var player = await _host.InWorldAsync("Leadr");
        using (var http = new HttpClient())
            (await http.PostAsync($"{_host.Http}/ro3emu/gm?account=leadr", new StringContent("!lvl 40 20"))).EnsureSuccessStatusCode();
        await player.SendGameAsync(GuildService.Create, new Romsg.GuildCreateReq
        { Name = "Realm", Icon = new Romsg.GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        long realm = Romsg.GuildFullInfoNtf.Parser.ParseFrom((await player.Inbox.WaitAsync(
            e => e.IsGame(GuildService.Full) && Romsg.GuildFullInfoNtf.Parser.ParseFrom(e.Body).Guid > 0, TimeSpan.FromSeconds(10))).Body).Guid;

        var options = PopulationOptions.Parse(["--server", _host.Http, "--data", SyntheticData.Dir, "--bots", "3",
            "--roster", _roster, "--towns", SyntheticData.Town.ToString(), "--hunters", "0", "--vendors", "0", "--guild-every", "1"]);
        var guilds = _host.Service<Ro3Emu.Server.Accounts.GuildStore>();
        bool invited = false;
        await RunUntilAsync(options, sessions =>
        {
            var roster = Roster.Load(_roster).Players;
            long agent = roster[2].RoleId;
            if (!invited && agent != 0 && guilds.Get(realm)?.Applications.ContainsKey(agent) == true)
            {
                invited = true;
                _ = player.SendGameAsync(GuildService.Invite, new Romsg.GuildInviteReq { TarPlayerID = { agent } });
            }
            var odin = guilds.List().FirstOrDefault(g => g.Name == "Odin");
            return odin != null && odin.Members.ContainsKey(roster[1].RoleId) && agent != 0 && guilds.ForRole(agent)?.Id == realm;
        });
        var players = Roster.Load(_roster).Players;
        Assert.True(guilds.ForRole(players[0].RoleId)!.Members[players[0].RoleId].Official == 1); // the founder leads Odin
        Assert.True(players[2].Level >= 70);
        Assert.True(_host.Service<Ro3Emu.Server.Accounts.RoleStore>().LoadProgress(players[2].RoleId).Level >= 70);
    }

    /// <summary>A real player's party invitation and friend request: the fake player accepts both.</summary>
    [Fact]
    public async Task AFakePlayerJoinsARealPlayersPartyAndAcceptsItsFriendRequest()
    {
        File.WriteAllText(_roster, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new RosterEntry { Account = "pop0001", Name = "Pal", Town = SyntheticData.Town, Persona = Persona.Townie, GuildPlanned = true, Prepared = true },
        }));
        await using var player = await _host.InWorldAsync("Leadr");
        long self = (long)player.Self!.Uuid;
        await player.SendGameAsync(TeamService.TeamCreate, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
        using var joinedNotice = player.Inbox.Expect(e => e.IsGame(TeamService.TeamNewMember)
            && Romsg.BroadCastNewMemberEnter.Parser.ParseFrom(e.Body).DetailInfo.Guid != self); // not the creator's own card
        using var friendNotice = player.Inbox.Expect(e => e.Kind == BotEventKind.Rpc && e.Name == "push:FriendService.onFriendAccept");
        var joined = joinedNotice.WaitAsync(TimeSpan.FromSeconds(60));
        var friended = friendNotice.WaitAsync(TimeSpan.FromSeconds(60));
        var options = PopulationOptions.Parse(["--server", _host.Http, "--data", SyntheticData.Dir, "--bots", "1",
            "--roster", _roster, "--towns", SyntheticData.Town.ToString(), "--hunters", "0", "--vendors", "0"]);
        long asked = 0;
        await RunUntilAsync(options, sessions =>
        {
            long pal = Roster.Load(_roster).Players[0].RoleId;
            if (asked == 0 && pal != 0 && sessions.Any(s => s.Role!.Id == pal && s.SceneEntry != 0))
            {
                asked = pal;
                _ = player.SendGameAsync(TeamService.TeamInvite, new Romsg.InviteIntoTeamReq { Guid = pal });
                _ = player.MicroAsync("FriendService", "apply", new() { ["Uid"] = self.ToString(), ["Target"] = pal.ToString() });
            }
            return joined.IsCompleted && friended.IsCompleted;
        });
        Assert.Equal(asked, Romsg.BroadCastNewMemberEnter.Parser.ParseFrom((await joined).Body).DetailInfo.Guid);
        await friended;
    }

    /// <summary>A player away for ten days comes back ten levels on (a third of the time, capped at ten levels).</summary>
    [Fact]
    public async Task AFakePlayerAwayGrowsByItsCappedOfflineCredit()
    {
        File.WriteAllText(_roster, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new RosterEntry { Account = "pop0001", Name = "Grower", Town = SyntheticData.Town, Persona = Persona.Townie, GuildPlanned = true,
                Prepared = true, Level = 20, JobLevel = 10, LastGrowthUtc = DateTime.UtcNow.AddDays(-10) },
        }));
        var options = PopulationOptions.Parse(["--server", _host.Http, "--data", SyntheticData.Dir, "--bots", "1",
            "--roster", _roster, "--towns", SyntheticData.Town.ToString(), "--hunters", "0", "--vendors", "0"]);
        var roles = _host.Service<Ro3Emu.Server.Accounts.RoleStore>();
        await RunUntilAsync(options, _ => Roster.Load(_roster).Players[0] is { RoleId: > 0 } p && roles.LoadProgress(p.RoleId).Level > 20);
        var grown = Roster.Load(_roster).Players[0];
        Assert.Equal(30, roles.LoadProgress(grown.RoleId).Level);
        Assert.Equal(30, grown.Level);
    }

    private static bool Moved(GameSession s) =>
        MathF.Abs(s.Position.X - 150) + MathF.Abs(s.Position.Z - 186) > 0.5f;

    private async Task RunUntilAsync(PopulationOptions options, Func<GameSession[], bool> done)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = new PopulationRunner(options).RunAsync(stop.Token);
        try
        {
            while (true)
            {
                await Task.Delay(250, stop.Token);
                var sessions = await _host.Service<GameLoop>().InvokeAsync(() =>
                    Task.FromResult(_host.Service<SessionRegistry>().All().Where(s => s.Role != null).ToArray()));
                if (done(sessions)) break;
            }
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        // The server lets the sessions go before the next run logs the same accounts in.
        await Task.Delay(1000);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        File.Delete(_roster);
    }
}
