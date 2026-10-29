using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ro3Emu.Setup;

namespace Ro3Emu.Play;

/// <summary>
/// Runs RO3Emu.exe's "start" flow in process with this window as its <see cref="Ui.Host"/>: every line, progress,
/// phase and fake player count goes to the page; the page's buttons become the console's keys (S starts the game
/// early, R starts it again, Q stops the server) and its dialogs answer the flow's questions. A poll of the server's
/// /ro3emu/metrics feeds the status panel.
/// </summary>
internal sealed class PlayController(string[] args, Action<object> post, Action closeWindow, Action restart) : IUiHost, IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Action<object> _post = post;
    private readonly Action _closeWindow = closeWindow;
    private readonly Action _restart = restart;
    private readonly Updater _updater = new();
    private Updater.Release? _release;
    private bool _updating;
    private readonly Options _options = Options.Parse(args);
    private readonly ConcurrentQueue<ConsoleKey> _keys = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _questions = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _flow;
    private string _phase = "starting";
    private int _questionIds;
    private bool _closeAfter;

    /// <summary>The flow is still running (the server may be up).</summary>
    public bool Running => _flow is { IsCompleted: false };

    /// <summary>The game folder RO3Emu remembers, for the game's own cursor.</summary>
    public string? ClientDir => Settings.Load().ClientDir;

    /// <summary>A message from the page.</summary>
    public void Handle(string cmd, JsonElement message)
    {
        switch (cmd)
        {
            case "ready": Ready(); break;
            case "start": Start(); break;
            case "play": _keys.Enqueue(_phase == "bots" ? ConsoleKey.S : ConsoleKey.R); break;
            case "stop": Stop(closeAfter: false); break;
            case "answer":
                if (_questions.TryRemove(message.GetProperty("id").GetInt32(), out var waiting))
                    waiting.TrySetResult(message.GetProperty("value").GetString() ?? "");
                break;
            case "settings": SaveSettings(message); break;
            case "update": _ = UpdateAsync(); break;
        }
    }

    /// <summary>The page has loaded: its settings, then the flow (started at once, as a double-click of RO3Emu.exe does).</summary>
    private void Ready()
    {
        PostSettings();
        _ = PollAsync(_stop.Token);
        if (_release == null) _ = CheckUpdateAsync();
        else PostUpdate(_release);
        if (_flow == null) Start();
        else _post(new { type = "phase", phase = _phase });
    }

    private void Start()
    {
        if (Running) return;
        Program.StopRequested = false;
        _phase = "starting";
        _post(new { type = "phase", phase = _phase });
        Ui.Host = this;
        Ui.Interactive = true;
        _flow = Task.Run(() => Program.RunAsync(_options));
        _ = _flow.ContinueWith(t =>
        {
            int code = t.IsCompletedSuccessfully ? t.Result : 2;
            _phase = code == 0 ? "done" : "failed";
            _post(new { type = "ended", code });
            if (_closeAfter) _closeWindow();
        }, TaskScheduler.Default);
    }

    /// <summary>Stops the server cleanly (the flow's Q); with <paramref name="closeAfter"/> the window closes once it has.</summary>
    public void Stop(bool closeAfter)
    {
        _closeAfter |= closeAfter;
        if (closeAfter) _post(new { type = "closing" });
        if (!Running)
        {
            if (_closeAfter) _closeWindow();
            return;
        }
        _post(new { type = "phase", phase = "stopping" });
        Program.StopRequested = true;
        _keys.Enqueue(ConsoleKey.Q);
        foreach (var waiting in _questions.Values) waiting.TrySetResult("");
    }

    // ---- IUiHost: what the flow shows ----

    public void Line(UiLine kind, string text) => _post(new { type = "line", kind = kind.ToString().ToLowerInvariant(), text });

    public void Progress(string text, double? fraction) => _post(new { type = "progress", text, fraction });

    public void EndProgress() => _post(new { type = "progress-end" });

    public void Phase(string phase)
    {
        _phase = phase;
        _post(new { type = "phase", phase });
    }

    public void Bots(int online, int total) => _post(new { type = "bots", online, total });

    public ConsoleKey? TakeKey() => _keys.TryDequeue(out var key) ? key : null;

    public bool Confirm(string question, bool defaultYes)
    {
        string answer = AskPage(question, defaultYes ? "yes" : "no", yesNo: true);
        return answer.Length == 0 ? defaultYes : answer == "yes";
    }

    public string Ask(string question, string defaultValue) => AskPage(question, defaultValue, yesNo: false);

    /// <summary>Shows the question in the page and waits (on the flow's thread) for its answer.</summary>
    private string AskPage(string question, string defaultValue, bool yesNo)
    {
        int id = Interlocked.Increment(ref _questionIds);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[id] = answer;
        _post(new { type = "ask", id, question, @default = defaultValue, yesNo });
        return answer.Task.GetAwaiter().GetResult();
    }

    // ---- status and settings ----

    /// <summary>Every two seconds: the server's loop and sessions (/ro3emu/metrics) and whether the game runs.</summary>
    private async Task PollAsync(CancellationToken ct)
    {
        string url = $"http://127.0.0.1:{_options.Ports.Http}/ro3emu/metrics";
        while (!ct.IsCancellationRequested)
        {
            object status;
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
                var root = doc.RootElement;
                var loop = root.GetProperty("loop");
                var sessions = root.GetProperty("sessions");
                status = new
                {
                    type = "status", server = true,
                    tps = loop.GetProperty("ticksPerSecond").GetDouble(),
                    busy = loop.GetProperty("busyRatio").GetDouble(),
                    inWorld = sessions.GetProperty("inWorld").GetInt32(),
                    uptime = root.GetProperty("elapsedSeconds").GetDouble(),
                    game = Process.GetProcessesByName("ro3").Length > 0,
                };
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                or InvalidOperationException)
            {
                status = new { type = "status", server = false, game = Process.GetProcessesByName("ro3").Length > 0 };
            }
            _post(status);
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void PostSettings()
    {
        var s = Settings.Load();
        _post(new
        {
            type = "settings", bots = s.Bots, expRate = s.ExpRate, bonusTimeDrain = s.BonusTimeDrain,
            crystalDropChance = s.CrystalDropChance, clientDir = s.ClientDir,
            version = typeof(Program).Assembly.GetName().Version?.ToString(3),
        });
    }

    /// <summary>The page's settings form, saved to RO3Emu.settings.json for the next start.</summary>
    private void SaveSettings(JsonElement message)
    {
        var s = Settings.Load();
        double Number(string name, double min, double max, double fallback) =>
            message.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) && double.IsFinite(d) ? Math.Clamp(d, min, max) : fallback;
        s.Bots = (int)Number("bots", 0, 10000, s.Bots);
        s.ExpRate = Number("expRate", 0, 1000, s.ExpRate);
        s.BonusTimeDrain = Number("bonusTimeDrain", 0, 1, s.BonusTimeDrain);
        s.CrystalDropChance = Number("crystalDropChance", 0, 1, s.CrystalDropChance);
        s.Save();
        SetupLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"settings: bots {s.Bots}, exp {s.ExpRate}, bonus time drain {s.BonusTimeDrain}, crystals {s.CrystalDropChance}"));
        PostSettings();
        _post(new { type = "saved" });
    }

    // ---- the launcher's update (Updater) ----

    /// <summary>A newer release, offered on the page; a failed check (offline, GitHub unreachable) only goes to the log.</summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            if (await _updater.CheckAsync(_stop.Token) is { } release) PostUpdate(_release = release);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            SetupLog.Write($"update check: {e.Message}");
        }
    }

    private void PostUpdate(Updater.Release release) =>
        _post(new { type = "update", version = release.Version.ToString(3), current = Updater.Current.ToString(3) });

    /// <summary>The page's Update: stops the server (the characters are saved), downloads the release, puts its files in
    /// place and starts the new launcher.</summary>
    private async Task UpdateAsync()
    {
        if (_release is not { } release || _updating) return;
        _updating = true;
        string version = release.Version.ToString(3);
        try
        {
            if (Running)
            {
                _post(new { type = "updating", text = "Saving your characters and stopping the server…" });
                Stop(closeAfter: false);
                await _flow!.ContinueWith(_ => { }, TaskScheduler.Default);
            }
            int shown = -1;
            string file = await _updater.DownloadAsync(release, f =>
            {
                int percent = (int)(f * 100);
                if (percent == shown) return;
                shown = percent;
                _post(new { type = "updating", text = $"Downloading RO3Emu {version}… {percent}%" });
            }, _stop.Token);
            _post(new { type = "updating", text = $"Installing RO3Emu {version}…" });
            await Task.Run(() => Updater.Apply(file, SetupHome.AppDir));
            File.Delete(file);
            SetupLog.Write($"updated to {version}");
            _restart();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            SetupLog.Write($"update to {version} failed: {e}");
            _post(new { type = "update-failed", text = e.Message });
            _updating = false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _http.Dispose();
        _updater.Dispose();
    }
}
