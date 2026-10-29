namespace Ro3Emu.Population;

/// <summary>Player-looking names: plain names, gamer tags and syllable names, 2-16 characters (the server's limit).</summary>
public static class NameGenerator
{
    private static readonly string[] Male =
    [
        "Arthur", "Leon", "Kai", "Ryu", "Daniel", "Marco", "Felix", "Hugo", "Ivan", "Jin", "Lucas", "Mateo", "Nico", "Oscar",
        "Rafael", "Sven", "Theo", "Viktor", "Yuki", "Zane", "Baron", "Caio", "Dante", "Enzo", "Gabe", "Hiro", "Joon", "Kenji",
    ];

    private static readonly string[] Female =
    [
        "Aria", "Bella", "Clara", "Dara", "Elena", "Fiona", "Gina", "Hana", "Iris", "Julia", "Kira", "Luna", "Mia", "Nina",
        "Olivia", "Pia", "Rina", "Sora", "Tara", "Yumi", "Zoe", "Ayla", "Bianca", "Celia", "Eun", "Mai", "Nami", "Sakura",
    ];

    private static readonly string[] Words =
    [
        "Poring", "Drops", "Lunatic", "Baphy", "Valk", "Dark", "Shadow", "Holy", "Storm", "Frost", "Blaze", "Rune", "Sage",
        "Knight", "Priest", "Hunter", "Rogue", "Smith", "Mage", "Archer", "Kafra", "Pronty", "Payon", "Geffen", "Morroc",
        "Sky", "Moon", "Sun", "Star", "Wolf", "Fox", "Cat", "Neko", "Angel", "Demon", "Ghost", "Zeny", "Loot", "Crit", "Mvp",
    ];

    private static readonly string[] Syllables =
    [
        "ka", "ri", "to", "na", "mi", "lo", "ra", "el", "an", "zu", "ve", "sh", "yo", "ta", "ni", "ar", "is", "ul", "or", "en",
    ];

    public static string Unique(Random random, int sex, ISet<string> taken)
    {
        for (int attempt = 0; ; attempt++)
        {
            string name = Make(random, sex);
            if (attempt > 20) name += random.Next(10, 99);
            if (name.Length is >= 2 and <= 16 && !taken.Contains(name)) return name;
        }
    }

    private static string Make(Random random, int sex)
    {
        var first = sex == 2 ? Female : Male;
        return random.Next(6) switch
        {
            0 => Pick(random, first),
            1 => Pick(random, first) + Pick(random, Words),
            2 => Pick(random, Words) + Pick(random, first),
            3 => Pick(random, Words) + random.Next(1, 999),
            4 => Pick(random, first).ToLowerInvariant() + random.Next(80, 2010),
            _ => Capitalize(string.Concat(Enumerable.Range(0, random.Next(2, 4)).Select(_ => Pick(random, Syllables)))),
        };
    }

    private static string Pick(Random random, string[] list) => list[random.Next(list.Length)];

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
