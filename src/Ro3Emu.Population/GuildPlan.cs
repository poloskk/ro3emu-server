namespace Ro3Emu.Population;

/// <summary>
/// Which fake players are in guilds (inferred population choice, not original content): most belong to fake guilds of
/// 12-35 members, each founded by its first member; a few strong ones stay free to join real players' guilds. Every
/// player is planned once and keeps the plan (Roster).
/// </summary>
public static class GuildPlan
{
    public const double GuildShare = 0.6, FreeAgentShare = 0.08;

    /// <summary>Guild names are 2-7 letters, digits, spaces, '_' or '-' (GuildNameNumNew, GuildService.CheckName).</summary>
    private static readonly string[] Names =
    [
        "Valhala", "Asgard", "Midgard", "Yggdra", "Bifrost", "Odin", "Thor", "Loki", "Freyja", "Fenrir", "Garm", "Ymir",
        "Surtr", "Mjolnir", "Einherj", "Skuld", "Urd", "Norn", "Rune", "Rune Co", "Prontra", "Geffen", "Payon", "Morroc",
        "Alberta", "Izlude", "Aldebar", "Lutie", "Comodo", "Juno", "Rachel", "Lighthl", "Glast", "Niflhm", "Baphy",
        "Osiris", "Doppel", "Eddga", "Maya", "Pharaoh", "Orc", "Poring", "Drops", "Poporin", "Angelng", "Ghostng",
        "Deviruc", "Zeny", "Emperio", "Crusadr", "Paladin", "Templar", "Knights", "Wizards", "Sages", "Rogues", "Hunters",
        "Bards", "Monks", "Smiths", "Alchemy", "Priests", "Assasin", "Stalker", "Sniper", "Clowns", "Gypsy", "Lords",
    ];

    /// <summary>Plans the first <paramref name="count"/> players that are not planned yet; vendors keep to their stalls.</summary>
    public static void Assign(Roster roster, int count, Random random)
    {
        var used = roster.Players.Select(p => p.Guild).Where(g => g != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? open = null;
        int left = 0;
        bool changed = false;
        foreach (var player in roster.Players.Take(count).Where(p => !p.GuildPlanned))
        {
            player.GuildPlanned = changed = true;
            if (player.Persona == Persona.Vendor) continue;
            double roll = random.NextDouble();
            if (roll < FreeAgentShare) { player.FreeAgent = true; continue; }
            if (roll >= FreeAgentShare + GuildShare) continue;
            if (left == 0 && NewName(random, used) is { } name)
            {
                open = name;
                used.Add(name);
                left = random.Next(12, 36);
                player.GuildLeader = true;
            }
            if (open == null) continue;
            player.Guild = open;
            left--;
        }
        if (changed) roster.Save();
    }

    /// <summary>A name a real player took first: the fake guild's founder and members move to a free one.</summary>
    public static bool Rename(Roster roster, string old, Random random)
    {
        var used = roster.Players.Select(p => p.Guild).Where(g => g != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (NewName(random, used) is not { } name) return false;
        foreach (var player in roster.Players.Where(p => p.Guild == old)) player.Guild = name;
        roster.Save();
        return true;
    }

    private static string? NewName(Random random, HashSet<string> used)
    {
        var free = Names.Where(n => !used.Contains(n)).ToArray();
        return free.Length == 0 ? null : free[random.Next(free.Length)];
    }
}
