using System.Globalization;

namespace Ro3Emu.Population;

/// <summary>Command line of the population runner (Program.Usage).</summary>
public sealed class PopulationOptions
{
    public string Server { get; set; } = "http://127.0.0.1:5380";
    public string GameDataDir { get; set; } = "data";
    public int Bots { get; set; } = 30;
    public string? RosterPath { get; set; }
    public List<long> Towns { get; } = [];
    public double HunterShare { get; set; } = 0.25;
    public double VendorShare { get; set; } = 0.15;
    /// <summary>At most this many vendors: stalls keep PublicParamConfig.StallsIntervalRadius apart in one stall map.</summary>
    public int MaxVendors { get; set; } = 80;
    /// <summary>At most this many townies and hunters move about; the others of those idle (a big crowd costs the server
    /// little while it stands). 0 = no limit.</summary>
    public int Active { get; set; }
    /// <summary>ItemConfig._iQuality values vendors sell; empty: the two highest qualities stalls accept.</summary>
    public HashSet<long> VendQualities { get; } = [];
    /// <summary>Ro3Emu:CrystalGear's rate on the server (0: off): some vendors sell red gear for crystals at it.</summary>
    public long CrystalGearRate { get; set; }
    public double ChatPerMinute { get; set; } = 0.4;
    public int LoginsAtOnce { get; set; } = 8;
    /// <summary>Time between two walk steps; the client sends one every 100 ms, a crowd of bots needs fewer.</summary>
    public int StepMs { get; set; } = 200;
    /// <summary>Above this many players the defaults turn thrifty: 400 movers, 400 ms steps (each step is an AOI refresh
    /// of the mover on the server's one game loop, the largest share of a crowd's cost).</summary>
    public const int LargePopulation = 800;
    /// <summary>Game loop busy share above which no more fake players log in (ServerLoad).</summary>
    public double MaxBusy { get; set; } = 0.6;
    public int Seed { get; set; } = 20261026;
    /// <summary>Seconds between a fake player's guild steps (FakePlayer.Guild.cs).</summary>
    public int GuildEverySeconds { get; set; } = 600;
    /// <summary>Rival raids hunting the field MVPs at once (MvpRaidCoordinator; 0: none), and players in each.</summary>
    public int MvpRaids { get; set; } = 3;
    public int MvpRaidSize { get; set; } = 8;

    public string Roster => RosterPath ?? Path.Combine(GameDataDir, "population", "roster.json");

    public static PopulationOptions Parse(string[] args)
    {
        var o = new PopulationOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--server": o.Server = value; i++; break;
                case "--data": o.GameDataDir = value; i++; break;
                case "--bots": o.Bots = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--roster": o.RosterPath = value; i++; break;
                case "--towns": o.Towns.AddRange(value.Split(',').Select(t => long.Parse(t, CultureInfo.InvariantCulture))); i++; break;
                case "--hunters": o.HunterShare = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--vendors": o.VendorShare = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--active": o.Active = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--max-vendors": o.MaxVendors = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--vend-quality": o.VendQualities.UnionWith(value.Split(',').Select(q => long.Parse(q, CultureInfo.InvariantCulture))); i++; break;
                case "--crystal-gear-rate": o.CrystalGearRate = long.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--chat": o.ChatPerMinute = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--logins": o.LoginsAtOnce = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--step-ms": o.StepMs = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--max-busy": o.MaxBusy = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--seed": o.Seed = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--guild-every": o.GuildEverySeconds = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--mvp-raids": o.MvpRaids = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--mvp-raid-size": o.MvpRaidSize = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        if (o.Bots > LargePopulation)
        {
            if (!args.Contains("--active")) o.Active = 400;
            if (!args.Contains("--step-ms")) o.StepMs = 400;
        }
        if (o.Bots < 0 || o.GuildEverySeconds < 1 || o.LoginsAtOnce < 1 || o.StepMs is < 50 or > 1000 || o.Active < 0 || o.MaxBusy is <= 0 or > 1 || o.HunterShare is < 0 or > 1 || o.VendorShare is < 0 or > 1 || o.HunterShare + o.VendorShare > 1 || o.MvpRaids < 0 || o.MvpRaidSize is < 2 or > 40) throw new ArgumentException("bad option value");
        return o;
    }
}
