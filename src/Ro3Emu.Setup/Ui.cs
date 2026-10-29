using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ro3Emu.Setup;

/// <summary>The console the player sees: short steps, plain messages, one progress line. Everything also goes to the log.</summary>
internal static class Ui
{
    private static readonly object Gate = new();
    private static bool _progressShown;

    /// <summary>False when nobody can answer (stdin redirected, --yes): questions take their default.</summary>
    public static bool Interactive { get; set; } = !Console.IsInputRedirected;

    /// <summary>The launcher window, when the flows run in it instead of a console.</summary>
    public static IUiHost? Host { get; set; }

    public static void Title(string text)
    {
        Write(ConsoleColor.Cyan, "\n" + text + "\n" + new string('=', text.Length), UiLine.Title, text);
    }

    public static void Step(string text) => Write(ConsoleColor.Cyan, "\n> " + text, UiLine.Step, text);

    public static void Info(string text) => Write(null, "  " + text, UiLine.Info, text);

    public static void Ok(string text) => Write(ConsoleColor.Green, "  " + text, UiLine.Ok, text);

    public static void Warn(string text) => Write(ConsoleColor.Yellow, "  " + text, UiLine.Warn, text);

    public static void Error(string text) => Write(ConsoleColor.Red, "\n" + text, UiLine.Error, text);

    /// <summary>Where the flow is, for the window (no console output).</summary>
    public static void Phase(string phase) => Host?.Phase(phase);

    /// <summary>Fake players online, for the window (the console shows its own lines).</summary>
    public static void Bots(int online, int total) => Host?.Bots(online, total);

    /// <summary>A key pressed in the window or the console since the last call, or null.</summary>
    public static ConsoleKey? TakeKey()
    {
        if (Host != null) return Host.TakeKey();
        return Interactive && !Console.IsInputRedirected && Console.KeyAvailable ? Console.ReadKey(intercept: true).Key : null;
    }

    private static void Write(ConsoleColor? color, string text, UiLine kind, string plain)
    {
        if (Host is { } host)
        {
            host.Line(kind, plain);
            SetupLog.Write(text.Trim('\n'));
            return;
        }
        lock (Gate)
        {
            EndProgress();
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine(text);
            if (color != null) Console.ResetColor();
            SetupLog.Write(text.Trim('\n'));
        }
    }

    /// <summary>Rewrites one status line (no log); <paramref name="fraction"/> (0-1) feeds the window's bar.</summary>
    public static void Progress(string text, double? fraction = null)
    {
        if (Host is { } host)
        {
            host.Progress(text, fraction);
            return;
        }
        lock (Gate)
        {
            if (Console.IsOutputRedirected) return;
            int width = Math.Max(20, SafeWidth() - 1);
            if (text.Length > width) text = text[..width];
            Console.Write("\r  " + text.PadRight(width - 2));
            _progressShown = true;
        }
    }

    public static void EndProgress()
    {
        Host?.EndProgress();
        lock (Gate)
        {
            if (!_progressShown) return;
            Console.WriteLine();
            _progressShown = false;
        }
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch (IOException) { return 100; }
    }

    /// <summary>A yes/no question; Enter (or no one to ask) gives the default.</summary>
    public static bool Confirm(string question, bool defaultYes = true)
    {
        if (!Interactive) return defaultYes;
        if (Host is { } host)
        {
            bool yes = host.Confirm(question, defaultYes);
            SetupLog.Write($"{question} -> {(yes ? "yes" : "no")}");
            return yes;
        }
        lock (Gate) EndProgress();
        Console.Write("  " + question + (defaultYes ? " [Y/n] " : " [y/N] "));
        var answer = Console.ReadLine()?.Trim() ?? "";
        SetupLog.Write($"{question} -> '{answer}'");
        if (answer.Length == 0) return defaultYes;
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    /// <summary>A free answer; Enter (or no one to ask) gives the default.</summary>
    public static string Ask(string question, string defaultValue = "")
    {
        if (!Interactive) return defaultValue;
        if (Host is { } host)
        {
            string given = host.Ask(question, defaultValue).Trim();
            SetupLog.Write($"{question} -> '{given}'");
            return given.Length == 0 ? defaultValue : given;
        }
        lock (Gate) EndProgress();
        Console.Write("  " + question + (defaultValue.Length > 0 ? $" [{defaultValue}] " : " "));
        var answer = Console.ReadLine()?.Trim() ?? "";
        SetupLog.Write($"{question} -> '{answer}'");
        return answer.Length == 0 ? defaultValue : answer;
    }

    /// <summary>Human sizes: 1.2 GB, 340 MB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{Math.Max(0, t.Seconds)} s";

    /// <summary>
    /// True when this program owns its console window (double-clicked in Explorer): the window closes with the
    /// program, so the last message needs a pause to be read.
    /// </summary>
    public static bool OwnsConsole() => _ownsConsole ??= DetectOwnConsole();

    private static bool? _ownsConsole;

    private static bool DetectOwnConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return false;
        try
        {
            var list = new uint[4];
            return GetConsoleProcessList(list, (uint)list.Length) <= 1;
        }
        catch (Exception) { return false; }
    }

    public static void PauseIfOwnConsole()
    {
        if (!OwnsConsole()) return;
        Console.WriteLine();
        Console.Write("  Press Enter to close this window.");
        Console.ReadLine();
    }

    /// <summary>Opens Explorer on a file (Windows), so the player sees the zip to attach.</summary>
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsWindows() || !Interactive) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) { SetupLog.Write("explorer: " + e.Message); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
