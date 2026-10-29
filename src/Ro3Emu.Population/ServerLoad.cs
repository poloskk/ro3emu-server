using System.Text.Json.Nodes;

namespace Ro3Emu.Population;

/// <summary>
/// How busy the server's game loop is: the change of /ro3emu/metrics loop.busyMs over wall time, sampled every few
/// seconds. Logins wait while it is above the limit, so a big roster fills the server up to what it can carry and no
/// further; fake players already online stay.
/// </summary>
public sealed class ServerLoad(HttpClient http, double maxBusy)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _sample = new(1);
    private (DateTime At, double BusyMs)? _last;
    private DateTime _next;
    private bool _held;

    /// <summary>The game loop's busy share over the last sample (0-1), or null before two samples.</summary>
    public double? Busy { get; private set; }

    public async Task WaitBelowAsync(CancellationToken ct)
    {
        while (true)
        {
            await SampleAsync(ct);
            double busy = Busy ?? 0;
            if (busy <= maxBusy)
            {
                if (_held) PopulationContext.Log($"server loop {busy * 100:F0}% busy again: logins go on");
                _held = false;
                return;
            }
            if (!_held) PopulationContext.Log($"server loop {busy * 100:F0}% busy (limit {maxBusy * 100:F0}%): holding logins");
            _held = true;
            await Task.Delay(Every, ct);
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        await _sample.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _next) return;
            _next = DateTime.UtcNow + Every;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var node = JsonNode.Parse(await http.GetStringAsync("/ro3emu/metrics", deadline.Token));
            if (node?["loop"]?["busyMs"]?.GetValue<double>() is not { } busyMs) return;
            var now = DateTime.UtcNow;
            if (_last is { } last && busyMs >= last.BusyMs && now > last.At)
                Busy = (busyMs - last.BusyMs) / (now - last.At).TotalMilliseconds;
            _last = (now, busyMs);
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // a slow answer: sample next time
        finally { _sample.Release(); }
    }
}
