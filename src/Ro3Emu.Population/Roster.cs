using System.Text.Json;

namespace Ro3Emu.Population;

public enum Persona { Townie, Idler, Hunter, Vendor }

/// <summary>One fake player: the same account, role and habits every run.</summary>
public sealed class RosterEntry
{
    public string Account { get; set; } = "";
    public string Name { get; set; } = "";
    public int Sex { get; set; } = 1;
    public long Town { get; set; }
    public Persona Persona { get; set; }
    public long RoleId { get; set; }
    /// <summary>Set once the role got its level and job (GM !lvl / !job); a returning player keeps what it has.</summary>
    public bool Prepared { get; set; }
    /// <summary>The base and job level this population last gave or saw (0: not known yet).</summary>
    public int Level { get; set; }
    public int JobLevel { get; set; }
    /// <summary>When growth (FakePlayer.Growth.cs) last counted this player's time.</summary>
    public DateTime? LastGrowthUtc { get; set; }
    /// <summary>The level whose stat points were last spent (FakePlayer.Stats.cs): a login at the same level spends none.</summary>
    public int StatsLevel { get; set; }
    /// <summary>The class items last leased (FakePlayer.Mount.cs) as job:ids, and the mount among them (0: none): a login
    /// with the same items only mounts again.</summary>
    public string ClassItems { get; set; } = "";
    public long Mount { get; set; }
    /// <summary>The gear worn (FakePlayer.Gear.cs) as its item ids; another set is put on when the plan changes.</summary>
    public string Gear { get; set; } = "";
    /// <summary>Each equipment grid's strengthen level, as the server last answered it.</summary>
    public Dictionary<int, int> Strength { get; set; } = [];
    /// <summary>The talent panel is open (its two quests delivered), and each talent node's level.</summary>
    public bool TalentsOpen { get; set; }
    /// <summary>The appraisal function (7) is open: its quest delivered, so gold and red gear can be worn.</summary>
    public bool AppraiseOpen { get; set; }
    public Dictionary<long, int> Talents { get; set; } = [];

    /// <summary>GuildPlan ran for this player: the guild fields below are its lasting choice.</summary>
    public bool GuildPlanned { get; set; }
    /// <summary>The fake players' guild this player founds or joins ("" for none).</summary>
    public string Guild { get; set; } = "";
    public bool GuildLeader { get; set; }
    /// <summary>A strong player without a fake guild: applies to real players' guilds and takes their invites.</summary>
    public bool FreeAgent { get; set; }
    /// <summary>The guild the role is in: its id, -1 a guild the population did not see the id of, 0 none.</summary>
    public long GuildId { get; set; }
}

/// <summary>The roster file (data/population/roster.json): grows to the requested count, never reshuffles.</summary>
public sealed class Roster
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _sync = new();

    public List<RosterEntry> Players { get; }

    private Roster(string path, List<RosterEntry> players)
    {
        _path = path;
        Players = players;
    }

    public static Roster Load(string path)
    {
        var players = File.Exists(path) ? JsonSerializer.Deserialize<List<RosterEntry>>(File.ReadAllText(path), Json) ?? [] : [];
        return new Roster(path, players);
    }

    /// <summary>Adds players until there are <paramref name="count"/>, each with a fresh name, town and persona.</summary>
    /// <remarks>Vendors live in the first stall map (StallsMapID); without one they are townies.</remarks>
    public void Grow(int count, IReadOnlyList<long> towns, double hunterShare, double vendorShare, int maxVendors, long? stallMap, Random random)
    {
        int vendors = Players.Count(p => p.Persona == Persona.Vendor);
        var taken = Players.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (Players.Count < count)
        {
            int sex = random.Next(2) + 1;
            string name = NameGenerator.Unique(random, sex, taken);
            taken.Add(name);
            double roll = random.NextDouble();
            long? vendorTown = roll >= hunterShare && roll < hunterShare + vendorShare && vendors < maxVendors ? stallMap : null;
            if (vendorTown != null) vendors++;
            var persona = roll < hunterShare ? Persona.Hunter
                : vendorTown != null ? Persona.Vendor
                : random.NextDouble() < 0.3 ? Persona.Idler : Persona.Townie;
            Players.Add(new RosterEntry
            {
                Account = $"pop{Players.Count + 1:D4}",
                Name = name,
                Sex = sex,
                Town = vendorTown ?? towns[random.Next(towns.Count)],
                Persona = persona,
            });
        }
        Save();
    }

    /// <summary>A name the server refused as taken: pick another and keep it.</summary>
    public void Rename(RosterEntry entry, Random random)
    {
        lock (_sync)
        {
            var taken = Players.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            entry.Name = NameGenerator.Unique(random, entry.Sex, taken);
        }
        Save();
    }

    public void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Players, Json));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
