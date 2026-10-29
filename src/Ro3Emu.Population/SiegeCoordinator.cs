using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// Pairs guilds for the weekly siege and tells the fake players when it is open. Inferred population choice: the
/// original pairing came from a season of territory battles (TerritoryWarsService.SiegeGuilds), and on a local server
/// that season is long over, so the population pairs guilds itself with the server's <c>!siege seed</c> test aid. A
/// city missing a side gets one; the royal city (the first SiegeConfig city) takes a real player's guild as attacker
/// when no real guild is paired yet, every other side is a fake guild. A finished siege leaves its winner owning the
/// city and its loser as next week's attacker, so pairings carry on by themselves.
/// </summary>
public sealed class SiegeCoordinator(PopulationContext ctx, IReadOnlyList<FakePlayer> players, IReadOnlySet<int> siegeCities)
{
    /// <summary>The cities the server holds sieges for: SiegeConfig rows of the first area (_iBelongArea), the only one
    /// with territory zones (TerritoryWarsService.SiegeZone: the royal city and its three capitals). Each pairing is a
    /// GM line and resends the siege panel to everyone online, so the other areas' 36 rows are left alone.</summary>
    public static IReadOnlySet<int> FirstArea(Ro3Emu.Server.Data.GameData data)
    {
        var rows = data.Table("SiegeConfig").Values.ToList();
        if (rows.Count == 0) return new HashSet<int>();
        long area = rows.Min(r => Ro3Emu.Server.Data.GameData.Int(r, "_iBelongArea"));
        return rows.Where(r => Ro3Emu.Server.Data.GameData.Int(r, "_iBelongArea") == area)
            .Select(r => (int)Ro3Emu.Server.Data.GameData.Int(r, "_iID")).ToHashSet();
    }

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    /// <summary>Cities seeded once: one that still misses a side afterwards is not a siege city of this server
    /// (TerritoryWarsService.SiegeZone has no zone for it) and is left alone from then on.</summary>
    private readonly HashSet<int> _seeded = [];
    private readonly HashSet<int> _skip = [];

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
            var herald = players.FirstOrDefault(p => p.Online && p.Entry.GuildLeader && p.Entry.GuildId > 0)
                ?? players.FirstOrDefault(p => p.Online);
            if (herald == null) continue;
            try
            {
                if (await herald.SiegeInfoAsync(ct) is not { } info) continue;
                ctx.SiegeOpen = info.BattleInfos.Values.Any(c => c.ActivityState == SiegeCityActivityState.Fighting);
                await PairAsync(herald, info, ct);
            }
            catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException) { }
        }
    }

    private async Task PairAsync(FakePlayer herald, SiegeBaseInfoNtf info, CancellationToken ct)
    {
        foreach (var missing in info.BattleInfos.Values.Where(c => (c.AttackGuildInfo?.Guid ?? 0) == 0 || (c.DefenseGuildInfo?.Guid ?? 0) == 0))
            if (_seeded.Contains(missing.CityId)) _skip.Add(missing.CityId);
        var cities = info.BattleInfos.Values.Where(c => siegeCities.Contains(c.CityId) && !_skip.Contains(c.CityId)).OrderBy(c => c.CityId).ToList();
        var used = cities.SelectMany(c => new[] { c.AttackGuildInfo?.Guid ?? 0, c.DefenseGuildInfo?.Guid ?? 0 }).Where(g => g > 0).ToHashSet();
        var real = await herald.RealGuildsAsync(ct);
        var fake = new Queue<long>(FakeGuilds().Where(g => !used.Contains(g)));
        bool realPaired = real.Any(used.Contains);
        for (int i = 0; i < cities.Count; i++)
        {
            var city = cities[i];
            long attack = city.AttackGuildInfo?.Guid ?? 0, defense = city.DefenseGuildInfo?.Guid ?? 0;
            long challenger = i == 0 && !realPaired ? real.FirstOrDefault(g => g != defense && !used.Contains(g)) : 0;
            if (attack > 0 && defense > 0 && challenger == 0) continue;
            if (defense == 0 && !fake.TryDequeue(out defense)) continue;
            if (challenger > 0) attack = challenger;
            else if (attack == 0 && !fake.TryDequeue(out attack)) continue;
            if (attack == defense) continue;
            if (!await ctx.GmAsync(herald.Entry.Account, $"!siege seed {city.CityId} {attack} {defense}", ct)) continue;
            _seeded.Add(city.CityId);
            used.Add(attack);
            used.Add(defense);
            realPaired |= challenger > 0;
            PopulationContext.Log($"Siege of city {city.CityId}: guild {attack} attacks guild {defense}");
        }
    }

    /// <summary>The founded fake guilds, largest first.</summary>
    private IEnumerable<long> FakeGuilds()
    {
        var roster = ctx.Roster.Players;
        return roster.Where(p => p.GuildLeader && p.GuildId > 0).Select(p => p.GuildId)
            .OrderByDescending(id => roster.Count(p => p.GuildId == id)).ToArray();
    }
}
