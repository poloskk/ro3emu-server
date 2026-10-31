using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Population;

/// <summary>Loads the world data and the roster, then keeps every fake player of the roster alive until stopped.</summary>
public sealed class PopulationRunner(PopulationOptions options)
{
    private readonly List<FakePlayer> _players = [];
    private PopulationContext? _ctx;

    public IReadOnlyList<FakePlayer> Players => _players;

    /// <summary>Runs until <paramref name="ct"/> is cancelled; 1 when there is no town to live in.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var gameOptions = Options.Create(new ServerOptions { GameDataDir = options.GameDataDir });
        var data = new GameData(gameOptions, NullLogger<GameData>.Instance);
        var world = new WorldData(gameOptions, data, NullLogger<WorldData>.Instance);
        var plan = new WorldPlan(data, world);
        var goods = new StallGoods(data);
        long? stallMap = goods.StallMaps() is [var first, ..] ? first : null;
        // Vendors live in the stall map even when --towns leaves it out.
        var towns = plan.Towns([.. options.Towns, .. options.Towns.Count > 0 && stallMap is { } m ? [m] : Array.Empty<long>()])
            .ToDictionary(t => t.SceneEntry);
        if (towns.Count == 0)
        {
            PopulationContext.Log($"No town with a scene editor table in {options.GameDataDir}: export the game data first (RO3Emu.exe export).");
            return 1;
        }
        PopulationContext.Log($"Towns: {string.Join(", ", towns.Values.Select(t => $"{t.Name} ({t.SceneEntry}, {t.Spots.Count} spots, {t.Fields.Count} fields)"))}");
        var catalogue = Catalogue(goods);

        // Calls set their own deadlines (GM 60 s, metrics 15 s): a GM line waits for the whole realm on a busy server.
        using var http = new HttpClient { BaseAddress = new Uri(options.Server), Timeout = TimeSpan.FromSeconds(90) };
        string pem = await http.GetStringAsync("/ro3emu/metrics/public-key", ct);
        var roster = Roster.Load(options.Roster);
        var homes = towns.Keys.Where(t => options.Towns.Count == 0 || options.Towns.Contains(t)).ToList();
        roster.Grow(options.Bots, homes, options.HunterShare, options.VendorShare, options.MaxVendors,
            stallMap is { } s && towns.ContainsKey(s) && catalogue.Count > 0 ? s : null, new Random(options.Seed));
        foreach (var p in roster.Players.Where(p => !towns.ContainsKey(p.Town))) p.Town = towns.Keys.First();
        GuildPlan.Assign(roster, options.Bots, new Random(options.Seed + 7));
        var ctx = _ctx = new PopulationContext(options, pem, world, plan, towns, roster, http, catalogue, goods.OpeningFee())
        {
            GuildIcon = DefaultGuildIcon(data),
            Support = new SupportKit(data),
            Stats = new StatBuild(data),
            Gear = new GearPlan(data),
            Exchange = goods.Exchange(options.CrystalGearRate, CrystalGearQuality, CrystalCurrency),
            ListingFee = goods.ListingFee,
            PkMaps = data.Table("MVPConfig").Values.Select(r => (long)GameData.Int(r, "_iMapID")).Where(m => m > 0).ToHashSet(),
        };
        PopulationContext.Log($"Vendors list {ctx.Exchange.Count} goods in the trade exchange");
        int movers = 0;
        foreach (var (entry, i) in roster.Players.Take(options.Bots).Select((e, i) => (e, i)))
        {
            bool mover = entry.Persona is Persona.Townie or Persona.Hunter;
            bool active = !mover || options.Active == 0 || movers++ < options.Active;
            _players.Add(new FakePlayer(entry, ctx, options.Seed + i + 1, active));
        }
        PopulationContext.Log($"{_players.Count} fake players starting");
        await Task.WhenAll([.. LoginOrder(_players).Select(p => p.RunAsync(ct)), ReportAsync(ct),
            new SiegeCoordinator(ctx, _players, SiegeCoordinator.FirstArea(data)).RunAsync(ct),
            new MvpRaidCoordinator(ctx, _players).RunAsync(ct)]);
        foreach (var p in _players) await p.DisposeAsync();
        roster.Save();
        PopulationContext.Log("stopped");
        return 0;
    }

    /// <summary>
    /// Guilds taken in turn (one member of each, then the next of each): a guild member's login holds its guild
    /// (the scene publication, guild activity checks), so members of one guild log in one after another. The roster
    /// keeps a guild's members together, and in roster order the parallel logins were mostly one guild's.
    /// </summary>
    private static IEnumerable<FakePlayer> LoginOrder(IEnumerable<FakePlayer> players)
    {
        var groups = players.GroupBy(p => p.Entry.Guild == "" ? "#" + p.Entry.Account : p.Entry.Guild).Select(g => g.ToArray()).ToArray();
        for (int i = 0; groups.Any(g => i < g.Length); i++)
            foreach (var group in groups)
                if (i < group.Length) yield return group[i];
    }

    /// <summary>The first frame and design a new guild may take: GuildIconConfig _iType 1/2, unlocked by default
    /// (_iDefaultUnlock) and shown on the create widget (_iCreateShow), as GuildService.CheckIcon requires.</summary>
    private static Romsg.GuildHeadIcon DefaultGuildIcon(GameData data)
    {
        long First(int type) => data.Table("GuildIconConfig").Values
            .Where(r => GameData.Int(r, "_iType") == type && GameData.Int(r, "_iDefaultUnlock") == 1 && GameData.Int(r, "_iCreateShow") != 0)
            .Select(r => (long)GameData.Int(r, "_iID")).DefaultIfEmpty(0).Min();
        return new Romsg.GuildHeadIcon { BackgroundType = First(1), IconType = First(2) };
    }

    // Ro3Emu:CrystalGear's defaults (CrystalGearOptions): red gear sold for Sacred Relic Crystals.
    private const int CrystalGearQuality = 6, CrystalCurrency = 3;

    /// <summary>The goods of the wanted qualities; by default the two highest qualities stalls accept.</summary>
    private IReadOnlyList<StallGood> Catalogue(StallGoods goods)
    {
        var counts = goods.QualityCounts();
        var wanted = options.VendQualities.Count > 0 ? options.VendQualities : [.. counts.Keys.OrderDescending().Take(2)];
        var catalogue = goods.Catalogue(wanted);
        PopulationContext.Log($"Stall goods by quality: {string.Join(", ", counts.Select(c => $"{c.Key}: {c.Value}"))}; vendors sell quality {string.Join("/", wanted.Order())} ({catalogue.Count} items)");
        if (options.CrystalGearRate <= 0) return catalogue;
        var red = goods.CrystalGear(options.CrystalGearRate, CrystalGearQuality, CrystalCurrency);
        PopulationContext.Log($"Crystal vendors sell {red.Count} red pieces at 1 crystal per {options.CrystalGearRate} Zeny of base price");
        return [.. catalogue, .. red];
    }

    private async Task ReportAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Every 15 s while they log in (RO3Emu.exe starts the game once they are in), then every minute.
            var every = _players.All(p => p.Online) ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15);
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { return; }
            string busy = _ctx?.Load.Busy is { } b ? $", server loop {b * 100:F0}% busy" : "";
            var stalled = _players.Where(p => p.Stalled).Select(p => p.Entry.Account).ToList();
            string stalls = stalled.Count == 0 ? "" : $"; {stalled.Count} get no heartbeat reply ({string.Join(", ", stalled.Take(5))})";
            PopulationContext.Log($"{_players.Count(p => p.Online)}/{_players.Count} fake players online{busy}{stalls}");
        }
    }
}
