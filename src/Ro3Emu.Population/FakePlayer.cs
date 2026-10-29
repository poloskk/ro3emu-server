using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>What every fake player shares: the server, its key, the world data and the roster.</summary>
public sealed class PopulationContext(PopulationOptions options, string pem, WorldData world, WorldPlan plan,
    IReadOnlyDictionary<long, Town> towns, Roster roster, HttpClient gm, IReadOnlyList<StallGood> goods, long[] openingFee)
{
    /// <summary>What vendors sell (StallGoods.Catalogue) and the stall opening fee (StallsExpenses).</summary>
    public IReadOnlyList<StallGood> Goods { get; } = goods;
    public long[] OpeningFee { get; } = openingFee;
    /// <summary>What vendors list in the trade exchange (StallGoods.Exchange) and its listing fee for a value.</summary>
    public IReadOnlyList<StallGood> Exchange { get; init; } = [];
    public Func<long, long> ListingFee { get; init; } = _ => 0;
    /// <summary>The emblem a fake guild is founded with (PopulationRunner.DefaultGuildIcon).</summary>
    public GuildHeadIcon GuildIcon { get; init; } = new();
    /// <summary>The support skills party members cast for their host (FakePlayer.Support).</summary>
    public required SupportKit Support { get; init; }
    /// <summary>The stat builds fake players spend their points on (FakePlayer.Stats).</summary>
    public required StatBuild Stats { get; init; }
    /// <summary>The gear and talents fake players grow into (FakePlayer.Gear.cs).</summary>
    public required GearPlan Gear { get; init; }

    /// <summary>A siege window is open (SiegeCoordinator): members of paired guilds go and fight it.</summary>
    public bool SiegeOpen { get => _siegeOpen; set => _siegeOpen = value; }
    private volatile bool _siegeOpen;

    /// <summary>A role of this population (else a real player's).</summary>
    public bool IsBot(long roleId) => roleId != 0 && Roster.Players.Any(p => p.RoleId == roleId);

    public PopulationOptions Options { get; } = options;
    public string Pem { get; } = pem;
    public WorldData World { get; } = world;
    public WorldPlan Plan { get; } = plan;
    public IReadOnlyDictionary<long, Town> Towns { get; } = towns;
    public Roster Roster { get; } = roster;
    public SemaphoreSlim Logins { get; } = new(options.LoginsAtOnce);
    public ServerLoad Load { get; } = new(gm, options.MaxBusy);
    /// <summary>Gear steps (FakePlayer.Gear: a kit, appraisal, nine pieces, strengthening, talents) in flight at
    /// once across the realm: hundreds at a time kept the server two thirds busy and dropped real players.</summary>
    public SemaphoreSlim GearLane { get; } = new(4);

    /// <summary>
    /// A GM line as this player (the loopback /ro3emu/gm tool, docs/guide/gm-access.md); false when refused (not on
    /// the server's machine, or the account is not in GmAccounts).
    /// </summary>
    /// <remarks>A GM line holds the whole realm on the server (every player's moves and heartbeats wait for it), so one at
    /// a time, at least <see cref="GmSpacing"/> apart, and a late answer is a refusal for now (the caller tries again
    /// later), not a lost connection.</remarks>
    /// <param name="ownRole">A line the server runs in the caller's own scope (GmCommands.IsEventOwnerCommand: !kit, !lvl, !quest done):
    /// no realm hold, so neither the one-at-a-time lane nor its spacing.</param>
    /// <param name="background">A one-off chore no one waits for: <see cref="BackgroundSpacing"/> after the last line.</param>
    public async Task<bool> GmAsync(string account, string line, CancellationToken ct, bool ownRole = false, bool background = false)
    {
        if (ownRole) return await PostGmAsync(account, line, ct);
        await _gmCalls.WaitAsync(ct);
        try
        {
            var wait = _lastGm + (background ? BackgroundSpacing : GmSpacing) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastGm = DateTime.UtcNow;
            return await PostGmAsync(account, line, ct);
        }
        finally { _gmCalls.Release(); }
    }

    private async Task<bool> PostGmAsync(string account, string line, CancellationToken ct)
    {
        try
        {
            using var body = new StringContent(line);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GmTimeout);
            using var response = await gm.PostAsync($"/ro3emu/gm?account={Uri.EscapeDataString(account)}", body, timeout.Token);
            if (!response.IsSuccessStatusCode) Log($"{account}: GM '{line}' refused ({(int)response.StatusCode})");
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log($"{account}: GM '{line}' not answered in {GmTimeout.TotalSeconds:F0} s; later");
            return false;
        }
    }

    private static readonly TimeSpan GmTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GmSpacing = TimeSpan.FromMilliseconds(500), BackgroundSpacing = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _gmCalls = new(1);
    private DateTime _lastGm;

    public static void Log(string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");
}

/// <summary>
/// One fake player: a protocol bot (Ro3Emu.Bot) that logs in like the client, keeps its session alive and lives a
/// persona (FakePlayer.Life.cs) until stopped; a dropped connection logs in again after a pause.
/// </summary>
public sealed partial class FakePlayer(RosterEntry entry, PopulationContext ctx, int seed, bool active = true) : IAsyncDisposable
{
    private const string Password = "population";
    private const float WalkSpeed = 5f;
    private readonly Random _random = new(seed);
    // Each bot ambles at its own pace, never above the client's walk speed (the server's walk check).
    private readonly float _walkSpeed = WalkSpeed * (0.75f + 0.25f * new Random(seed ^ 0x5EED).NextSingle());
    private BotClient? _bot;
    private WorldView _view = new();
    private BotSendQueue? _sender;
    private MapPoint _pos;
    private long _scene;
    private long _job;
    private long _lastPongMs;

    /// <summary>No Pong for this long while pinging every 5 s: the server stopped handling this session's messages
    /// (the client shows "Reconnecting" after its own heartbeat timeout).</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(20);

    public RosterEntry Entry => entry;
    public bool Online { get; private set; }
    public bool Stalled => Online && Environment.TickCount64 - Interlocked.Read(ref _lastPongMs) > StallAfter.TotalMilliseconds;

    public async Task RunAsync(CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(5);
        _life = ct;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                pause = TimeSpan.FromSeconds(5);
                await LiveAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                PopulationContext.Log($"{entry.Name}: {e.GetType().Name}: {e.Message}; again in {pause.TotalSeconds:F0} s");
            }
            await DisconnectAsync();
            if (ct.IsCancellationRequested) return;
            try { await Task.Delay(pause + TimeSpan.FromSeconds(_random.Next(5)), ct); } catch (OperationCanceledException) { return; }
            pause = TimeSpan.FromSeconds(Math.Min(120, pause.TotalSeconds * 2));
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await ctx.Load.WaitBelowAsync(ct);
        await ctx.Logins.WaitAsync(ct);
        try
        {
            _view = new WorldView();
            ClearParty(); // the party comes back with its 42048 at login
            _bot = new BotClient(ctx.Options.Server, ctx.Pem) { Timeout = TimeSpan.FromSeconds(45) };
            var view = _view;
            _bot.EnableLoadMode(512, e =>
            {
                if (e.IsGame(MsgId.Heartbeat)) Interlocked.Exchange(ref _lastPongMs, Environment.TickCount64);
                else if (e.IsGame(GuildService.InviteNtf)) OnGuildInvite(e.Body);
                else if (e.IsGame(MsgId.SwitchSceneLine)) OnSwitchScene(e.Body);
                else ObserveParty(e);
                view.Observe(e); _bag.Observe(e);
            });
            await _bot.LoginAsync(entry.Account, Password);
            await _bot.EnterGateAsync();
            long roleId = await RoleAsync(_bot);
            view.Self = roleId;
            var self = await _bot.EnterWorldAsync(roleId);
            _pos = new MapPoint(self.MovementInfo.Pos.X, self.MovementInfo.Pos.Y, self.MovementInfo.Pos.Z, 0);
            _scene = _view.SceneEntry;
            _job = (long)self.EntityTemplateID;
            _sender = new BotSendQueue(_bot.DisposeAsync);
            await SendAsync(MsgId.ChangeViewCfg, new ChangeViewCfgReq { ViewMaxCount = ViewCap }, ct);
            Interlocked.Exchange(ref _lastPongMs, Environment.TickCount64);
            Online = true;
            _ = HeartbeatAsync(ct);
            _ = AcceptWaitingFriendsAsync(ct);
        }
        finally { ctx.Logins.Release(); }
    }

    /// <summary>The roster's role: the account's first, else a new one (renamed while the name is taken).</summary>
    private async Task<long> RoleAsync(BotClient bot)
    {
        if (bot.Roles.FirstOrDefault() is { } existing)
        {
            entry.RoleId = existing.Id;
            entry.Level = existing.Level;
            entry.JobLevel = existing.JobLevel;
            return existing.Id;
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var created = await bot.CreateRoleAsync(entry.Name, entry.Sex);
                entry.RoleId = created.RoleBaseInfo.Id;
                entry.Prepared = false;
                ctx.Roster.Save();
                PopulationContext.Log($"{entry.Name}: new role {entry.RoleId}");
                return entry.RoleId;
            }
            catch (BotRefusedException) when (attempt < 5)
            {
                ctx.Roster.Rename(entry, _random);
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var bot = _bot;
        while (!ct.IsCancellationRequested && Online && bot == _bot)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await SendAsync(MsgId.Heartbeat, new Ping(), ct);
            }
            catch { return; }
        }
    }

    private Task SendAsync(uint msgId, Google.Protobuf.IMessage body, CancellationToken ct) =>
        (_sender ?? throw new IOException("not connected")).SendAsync(() => _bot!.SendGameAsync(msgId, body), ct);

    private async Task DisconnectAsync()
    {
        Online = false;
        if (_sender != null) await _sender.DisposeAsync();
        if (_bot != null) try { await _bot.DisposeAsync(); } catch { }
        _sender = null;
        _bot = null;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
