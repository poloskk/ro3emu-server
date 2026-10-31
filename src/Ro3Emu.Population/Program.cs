using Ro3Emu.Population;

// Fake players for a local RO3Emu server: protocol bots that log in like the client, live in the towns, walk between
// NPCs, chat, and hunt in the fields next to town. Run it on the server's machine (it uses the loopback GM tool).
// usage: Ro3Emu.Population [--bots 30] [--server http://127.0.0.1:5380] [--data <exported data dir>]
//        [--towns 10100,...] [--hunters 0.25] [--vendors 0.15] [--vend-quality 5,6] [--chat 0.4 (lines per player per minute)]
//        [--logins 8] [--step-ms 200] [--max-busy 0.7] [--roster <file>] [--mvp-raids 3] [--mvp-raid-size 8]
PopulationOptions options;
try { options = PopulationOptions.Parse(args); }
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.WriteLine($"{e.Message}\nusage: Ro3Emu.Population [--bots 30] [--server http://127.0.0.1:5380] [--data data] [--towns 10100,...] [--hunters 0.25] [--vendors 0.15] [--vend-quality q,q] [--chat 0.4] [--logins 8] [--step-ms 200] [--max-busy 0.7] [--roster file] [--mvp-raids 3] [--mvp-raid-size 8]");
    return 2;
}
if (options.GameDataDir == "data") options.GameDataDir = FindDataDir();

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
// RO3Emu.exe runs this with its input redirected and stops it with a "quit" line. An input that is closed or empty
// from the start (a background shell) is not a stop.
if (Console.IsInputRedirected)
    _ = Task.Run(() =>
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
            if (line.Trim() == "quit") { stop.Cancel(); return; }
    });
else Console.WriteLine("Ctrl+C stops the fake players.");
return await new PopulationRunner(options).RunAsync(stop.Token);

static string FindDataDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return Path.Combine(dir.FullName, "data");
    }
    return "data";
}
