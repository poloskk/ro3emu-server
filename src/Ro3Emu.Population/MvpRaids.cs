using System.Text.Json.Nodes;

namespace Ro3Emu.Population;

/// <summary>A live field MVP: its MVPConfig row, map and where it stands (the server's loopback /ro3emu/mvps).</summary>
public sealed record MvpTarget(int Cfg, long Map, float X, float Z);

/// <summary>One rival raid: a fake leader's party and the MVP it hunts.</summary>
public sealed class MvpRaid
{
    public required FakePlayer Leader { get; init; }
    public required IReadOnlyList<FakePlayer> Members { get; init; }
    public required DateTime Until { get; init; }
    /// <summary>The MVP the raid goes for (null while none is alive).</summary>
    public MvpTarget? Target { get; set; }
    /// <summary>The coordinator ended it: the party breaks up and its players go home.</summary>
    public bool Over { get; set; }
}

/// <summary>
/// Rival raids on the PK MVP maps (Ro3Emu ServerOptions.MvpMapsPk): parties of fake players that hunt the live field
/// MVPs and fight the other players there. Every minute the raids go to an alive MVP, first one on a map a real player
/// is on (contest is the point), at most two raids to that one and one to any other; a raid lasts 30-45 minutes, then
/// its players go back to their lives and others form the next. Not original content: the original server had only real
/// players, so the whole of it is a population choice (inferred).
/// </summary>
public sealed class MvpRaidCoordinator(PopulationContext ctx, IReadOnlyList<FakePlayer> players)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    /// <summary>Raids at an MVP that a real player is on, and at any other.</summary>
    private const int ContestedRaids = 2, OtherRaids = 1;
    /// <summary>The lowest level a raid takes (it is boosted to the cap once it is in, FakePlayer.Boost).</summary>
    private const int RaidLevel = 40;
    private readonly List<MvpRaid> _raids = [];
    private readonly Random _random = new(ctx.Options.Seed + 41);

    public async Task RunAsync(CancellationToken ct)
    {
        if (ctx.Options.MvpRaids <= 0) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
            try { await StepAsync(ct); }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or System.Text.Json.JsonException
                || e is OperationCanceledException && !ct.IsCancellationRequested)
            { }
        }
        foreach (var raid in _raids) raid.Over = true;
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var alive = await AliveAsync(ct);
        var contested = await RealPlayerMapsAsync(ct);
        foreach (var raid in _raids.Where(r => DateTime.UtcNow > r.Until || !r.Leader.Online || r.Members.Count(m => m.Online) < 2))
            End(raid);
        _raids.RemoveAll(r => r.Over);
        // Contested MVPs first; the rest in turn of the random order.
        var order = alive.OrderByDescending(m => contested.Contains(m.Map)).ThenBy(_ => _random.Next()).ToList();
        int Cap(MvpTarget m) => contested.Contains(m.Map) ? ContestedRaids : OtherRaids;
        foreach (var raid in _raids)
        {
            bool Open(MvpTarget m) => _raids.Count(r => r != raid && r.Target?.Cfg == m.Cfg) < Cap(m);
            // Its MVP where it stands now; a raid away from one a real player is on goes there while it wants raiders.
            var current = raid.Target is { } t ? alive.FirstOrDefault(m => m.Cfg == t.Cfg) : null;
            bool contestWaits = order.Any(m => contested.Contains(m.Map) && Open(m));
            raid.Target = current != null && (contested.Contains(current.Map) || !contestWaits) ? current : order.FirstOrDefault(Open);
        }
        while (_raids.Count < ctx.Options.MvpRaids
            && order.FirstOrDefault(m => _raids.Count(r => r.Target?.Cfg == m.Cfg) < Cap(m)) is { } target
            && Form(target) is { } formed)
            _raids.Add(formed);
    }

    /// <summary>A new raid of the strongest free players, its leader the first; null when too few are free.</summary>
    private MvpRaid? Form(MvpTarget target)
    {
        var free = players.Where(p => p.FreeForRaid && p.Entry.Level >= RaidLevel)
            .OrderByDescending(p => p.Entry.Level).ThenBy(_ => _random.Next()).Take(ctx.Options.MvpRaidSize).ToList();
        if (free.Count < Math.Min(3, ctx.Options.MvpRaidSize)) return null;
        var raid = new MvpRaid
        {
            Leader = free[0], Members = free, Target = target,
            Until = DateTime.UtcNow + TimeSpan.FromMinutes(30 + _random.Next(16)),
        };
        foreach (var p in free) p.Enlist(raid);
        PopulationContext.Log($"Rival raid of {free.Count} led by {free[0].Entry.Name} goes for MVP {target.Cfg} on map {target.Map}");
        return raid;
    }

    private static void End(MvpRaid raid)
    {
        raid.Over = true;
        PopulationContext.Log($"Rival raid of {raid.Leader.Entry.Name} breaks up");
    }

    private async Task<List<MvpTarget>> AliveAsync(CancellationToken ct)
    {
        if (await ctx.GetJsonAsync("/ro3emu/mvps", ct) is not JsonArray rows) return [];
        return [.. rows.OfType<JsonObject>().Select(r => new MvpTarget((int)r["cfg"]!, (long)r["map"]!, (float)r["x"]!, (float)r["z"]!))
            .Where(m => ctx.PkMaps.Contains(m.Map))];
    }

    /// <summary>The maps real players are on (accounts outside the fake players' prefix).</summary>
    private async Task<HashSet<long>> RealPlayerMapsAsync(CancellationToken ct)
    {
        if (await ctx.GetJsonAsync("/ro3emu/online", ct) is not JsonArray rows) return [];
        return [.. rows.OfType<JsonObject>()
            .Where(r => r["account"]?.GetValue<string>() is { } account
                && !account.StartsWith(Ro3Emu.Server.ServerOptions.FakePlayerAccountPrefix, StringComparison.Ordinal)
                && r["scene"] is JsonValue)
            .Select(r => (long)r["scene"]!)];
    }
}
