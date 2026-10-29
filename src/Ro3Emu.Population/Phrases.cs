namespace Ro3Emu.Population;

/// <summary>What fake players say in map chat. {town} and {name} are filled in.</summary>
public static class Phrases
{
    private static readonly string[] Town =
    [
        "anyone wanna party?", "lf party, any job", "hi all", "{town} is so busy today", "where do i turn in this quest?",
        "wts some potions, pm me", "wtb blue gemstone", "brb", "lol", "gl everyone", "any guild recruiting?",
        "selling loot, cheap", "how do i get to the next town?", "gm when woe", "afk 5 min", "nice outfit",
        "who wants to hunt together", "first time on this server, hi", "lag?", "this music tho", "k back",
        "lf guild, active player", "anyone know where the kafra is", "o/", "ty", "need help with the boss",
    ];

    private static readonly string[] Field =
    [
        "so many monsters here", "need heal lol", "this spot is good exp", "anyone farming here?", "almost leveled",
        "drop finally", "ks?", "omg rare drop", "pots running low", "exp is slow today",
    ];

    public static string Pick(Random random, bool inTown, string town, string name)
    {
        var list = inTown ? Town : Field;
        return list[random.Next(list.Length)].Replace("{town}", town).Replace("{name}", name);
    }
}
