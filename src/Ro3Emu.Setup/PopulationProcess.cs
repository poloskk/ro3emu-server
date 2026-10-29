using System.Diagnostics;
using System.Text;

namespace Ro3Emu.Setup;

/// <summary>
/// The fake players (population\Ro3Emu.Population.exe, docs/guide/population.md) as a child process next to the
/// server: output to logs\population-*.log, stopped with a "quit" line, killed with this window by a job object.
/// </summary>
internal sealed partial class PopulationProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly IntPtr _job;

    public string LogPath { get; }
    public int Bots { get; }

    /// <summary>The fake players online, from the population's status line ("1245/3500 fake players online").</summary>
    public int Online => Volatile.Read(ref _online);
    private int _online;

    public bool HasExited => _process.HasExited;

    private PopulationProcess(Process process, StreamWriter log, string logPath, IntPtr job, int bots)
    {
        _process = process;
        _log = log;
        LogPath = logPath;
        _job = job;
        Bots = bots;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && StatusLine().Match(line) is { Success: true } m)
                Volatile.Write(ref _online, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(\d+)/\d+ fake players online")]
    private static partial System.Text.RegularExpressions.Regex StatusLine();

    /// <summary>population\Ro3Emu.Population(.exe) next to RO3Emu.exe; for developers the repository's build.</summary>
    public static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "Ro3Emu.Population.exe" : "Ro3Emu.Population";
        if (SetupHome.Shipped("population", name) is { } shipped) return shipped;
        if (SetupHome.RepositoryRoot() is { } repo)
            foreach (var config in new[] { "Release", "Debug" })
            {
                var path = Path.Combine(repo, "src", "Ro3Emu.Population", "bin", config, "net10.0", name);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <param name="crystalGearRate">The server's Ro3Emu:CrystalGear:Rate, so crystal vendors price red gear as it does.</param>
    public static PopulationProcess Start(string exe, int bots, int httpPort, long crystalGearRate = 0)
    {
        Directory.CreateDirectory(SetupHome.LogDir);
        var logPath = Path.Combine(SetupHome.LogDir, $"population-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--bots", bots.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--server", $"http://127.0.0.1:{httpPort}", "--data", SetupHome.DataDir,
                     "--crystal-gear-rate", crystalGearRate.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(a);
        start.Environment.Remove("DOTNET_ROOT");
        SetupLog.Write($"population: {exe} {string.Join(' ', start.ArgumentList)}");
        var process = new Process { StartInfo = start };
        void Line(object _, DataReceivedEventArgs e) { if (e.Data != null) lock (log) log.WriteLine(e.Data); }
        process.OutputDataReceived += Line;
        process.ErrorDataReceived += Line;
        if (!process.Start()) throw new SetupException("The fake players did not start.", "Run RO3Emu.exe again with --bots 0 to play without them.");
        var population = new PopulationProcess(process, log, logPath, OperatingSystem.IsWindows() ? ServerProcess.KillOnCloseJob(process) : IntPtr.Zero, bots);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return population;
    }

    /// <summary>Logs every fake player out ("quit"), else kills the process.</summary>
    public void Stop(TimeSpan grace)
    {
        if (_process.HasExited) return;
        try { _process.StandardInput.WriteLine("quit"); _process.StandardInput.Close(); }
        catch (Exception e) { SetupLog.Write("population quit: " + e.Message); }
        if (!_process.WaitForExit(grace))
            try { _process.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception) { }
        _process.Dispose();
        lock (_log) _log.Dispose();
        if (_job != IntPtr.Zero) ServerProcess.CloseJob(_job);
    }
}
