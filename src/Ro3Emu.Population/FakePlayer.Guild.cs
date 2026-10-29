using Google.Protobuf;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// Guild life (GuildPlan), through the client's own guild messages: a founder creates its guild (32210) and lets
/// members in on application (32230 auto-accept); members apply (32223); a free agent applies to real players'
/// guilds (32211 recommendations) and takes a real player's invitation (32238/32239), leaving a fake guild for it.
/// </summary>
public sealed partial class FakePlayer
{
    /// <summary>(inferred population choice) A free agent is strong: at least this base level.</summary>
    private const int StrongLevel = 70;
    private DateTime _nextGuildStep;
    private CancellationToken _life;

    /// <summary>The next guild step when one is due (--guild-every, ten minutes); a busy server's late answer waits for it.</summary>
    private async Task GuildStepAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextGuildStep) return;
        int every = ctx.Options.GuildEverySeconds;
        _nextGuildStep = DateTime.UtcNow + TimeSpan.FromSeconds(every + _random.Next(Math.Max(1, every / 5)));
        try
        {
            if (entry.GuildLeader && entry.Guild != "") await FoundAsync(ct);
            else if (entry.Guild != "" && entry.GuildId == 0) await JoinFakeGuildAsync(ct);
            else if (entry.FreeAgent && entry.GuildId == 0) await ApplyToRealGuildsAsync(ct);
        }
        catch (TimeoutException) { }
    }

    private async Task FoundAsync(CancellationToken ct)
    {
        if (entry.GuildId > 0) return;
        var create = await CreateGuildAsync(ct);
        if (create == ErrorCodes.GuildLevelCreate && await ctx.GmAsync(entry.Account, "!lvl 40 20", ct, ownRole: true))
        {
            entry.Level = Math.Max(entry.Level, 40);
            create = await CreateGuildAsync(ct);
        }
        if (create == ErrorCodes.GuildNameTaken && GuildPlan.Rename(ctx.Roster, entry.Guild, _random)) create = await CreateGuildAsync(ct);
        if (create is not (0 or ErrorCodes.AlreadyInGuild))
        {
            PopulationContext.Log($"{entry.Name}: guild {entry.Guild} not founded ({create})");
            return;
        }
        var found = GuildRecommendListResp.Parser.ParseFrom(await RequestAsync(GuildService.Recommend,
            new GuildRecommendListReq { SearchName = entry.Guild, Count = 20 }, ct));
        if (found.Guilds.FirstOrDefault(g => g.Name == entry.Guild && g.LeaderId == entry.RoleId) is not { } guild) return;
        await RequestAsync(GuildService.Edit, new GuildEditReq
        {
            Tp = GuildEditReq.Types.EditType.EditAutoAcceptApply, AutoAcceptApply = true, GuildId = guild.Guid,
        }, ct);
        entry.GuildId = guild.Guid;
        ctx.Roster.Save();
        PopulationContext.Log($"{entry.Name}: founded guild {entry.Guild}");
    }

    private async Task<int> CreateGuildAsync(CancellationToken ct) => GuildCreateResp.Parser.ParseFrom(await RequestAsync(
        GuildService.Create, new GuildCreateReq { Name = entry.Guild, Announce = $"Welcome to {entry.Guild}!", Icon = ctx.GuildIcon }, ct)).Result;

    /// <summary>A member applies to its fake guild once the founder has founded it; the guild lets it in at once.</summary>
    private async Task JoinFakeGuildAsync(CancellationToken ct)
    {
        long id = ctx.Roster.Players.FirstOrDefault(p => p.GuildLeader && p.Guild == entry.Guild)?.GuildId ?? 0;
        if (id <= 0) return;
        int code = await ApplyAsync([id], ct);
        if (code is 0 or ErrorCodes.AlreadyInGuild) entry.GuildId = id;
        else if (code == ErrorCodes.GuildFull) entry.Guild = "";
        else return;
        ctx.Roster.Save();
    }

    /// <summary>A free agent applies to up to three real players' guilds with room; their leaders decide.</summary>
    private async Task ApplyToRealGuildsAsync(CancellationToken ct)
    {
        await MakeStrongAsync(ct);
        var list = GuildRecommendListResp.Parser.ParseFrom(await RequestAsync(GuildService.Recommend,
            new GuildRecommendListReq { Count = 30, IsCanJoin = true }, ct));
        long[] real = [.. list.Guilds.Where(g => !ctx.IsBot(g.LeaderId) && g.MemberCount < g.TotalMemberCount).Take(3).Select(g => g.Guid)];
        if (real.Length == 0) return;
        if (await ApplyAsync(real, ct) == ErrorCodes.AlreadyInGuild)
        {
            entry.GuildId = -1; // a leader took it in meanwhile
            ctx.Roster.Save();
            PopulationContext.Log($"{entry.Name}: joined a player's guild");
        }
    }

    /// <summary>Real players' guilds (32211 recommendations whose leader is not a fake player), largest first.</summary>
    public async Task<long[]> RealGuildsAsync(CancellationToken ct)
    {
        var list = GuildRecommendListResp.Parser.ParseFrom(await RequestAsync(GuildService.Recommend, new GuildRecommendListReq { Count = 50 }, ct));
        return [.. list.Guilds.Where(g => !ctx.IsBot(g.LeaderId)).OrderByDescending(g => g.MemberCount).Select(g => g.Guid)];
    }

    /// <summary>32223 to these guilds: the first result that is not a success, else 0.</summary>
    private async Task<int> ApplyAsync(long[] guilds, CancellationToken ct)
    {
        var reply = GuildEnterApplyResp.Parser.ParseFrom(await RequestAsync(GuildService.Apply,
            new GuildEnterApplyReq { GuildIds = { guilds }, BatchApply = guilds.Length > 1 }, ct));
        if (reply.Result != 0) return reply.Result;
        return reply.ApplyResult.Select(r => r.Result).FirstOrDefault(r => r != 0);
    }

    /// <summary>32238 from the event stream: a real player's invitation is answered at once (it lapses in 30 s).</summary>
    private void OnGuildInvite(byte[] body)
    {
        var invite = GuildInviteNtf.Parser.ParseFrom(body);
        if (!ctx.IsBot(invite.PlayerId)) _ = AnswerInviteAsync(invite);
    }

    /// <summary>A founder stays with its guild; anyone else leaves a fake guild and joins the real player's.</summary>
    private async Task AnswerInviteAsync(GuildInviteNtf invite)
    {
        var ct = _life;
        bool accept = !entry.GuildLeader;
        try
        {
            if (accept && entry.GuildId != 0) await RequestAsync(GuildService.Leave, new Google.Protobuf.WellKnownTypes.Empty(), ct);
            var ack = GuildInviteAckResp.Parser.ParseFrom(await RequestAsync(GuildService.InviteAck, new GuildInviteAckReq
            {
                GuildID = invite.Info.Guid, Accept = accept, Reject = !accept, Token = invite.Token,
            }, ct));
            if (!accept || ack.Result != 0) return;
            entry.Guild = "";
            entry.GuildId = invite.Info.Guid;
            ctx.Roster.Save();
            PopulationContext.Log($"{entry.Name}: joined {invite.Info.Name} on invitation");
            await MakeStrongAsync(ct);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException) { }
    }

    /// <summary>(inferred population choice) A free agent or a recruit of a real guild is raised to a strong level.</summary>
    private async Task MakeStrongAsync(CancellationToken ct)
    {
        if (entry.Level >= StrongLevel) return;
        int level = _random.Next(StrongLevel, 91);
        if (!await ctx.GmAsync(entry.Account, $"!lvl {level} {level / 2}", ct, ownRole: true)) return;
        entry.Level = level;
        ctx.Roster.Save();
    }

    /// <summary>Sends a request and waits for the first message with its id (the guild answers on the request id).</summary>
    private async Task<byte[]> RequestAsync(uint msgId, IMessage body, CancellationToken ct)
    {
        var bot = _bot ?? throw new IOException("not connected");
        using var reply = bot.Inbox.Expect(e => e.IsGame(msgId));
        await SendAsync(msgId, body, ct);
        return (await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body;
    }
}
