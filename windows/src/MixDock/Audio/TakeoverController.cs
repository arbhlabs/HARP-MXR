using MixDock.Platform;

namespace MixDock.Audio;

/// <summary>
/// One-click takeover for a deck: capture the app, move its raw sound to an unheard endpoint,
/// play the processed copy on the device the app was really using. Everything is undone on Release.
/// </summary>
public sealed class TakeoverController
{
    private readonly AudioEngine _engine;
    private readonly DeckSource _deck;
    private readonly AppRedirect _redirect = new();
    private int? _pid;
    private string? _realDevice;
    private static readonly List<TakeoverController> All = new();

    public TakeoverController(AudioEngine engine, DeckSource deck)
    {
        _engine = engine; _deck = deck;
        lock (All) All.Add(this);
    }

    public bool RawSilenced => _redirect.Active;
    public string? DeviceName { get; private set; }
    public string? SinkId => _redirect.SinkId;

    /// <summary>Restore every redirect made by any controller (process exit, crash handlers).</summary>
    public static void ReleaseAll()
    {
        lock (All) foreach (var c in All) { try { c._redirect.Restore(); } catch { } }
    }

    /// <returns>Plain-English status for the UI.</returns>
    public async Task<string> EngageAsync(int pid, string appName)
    {
        if (MixDock.Platform.ProtectedApps.IsProtected(appName))
            throw new InvalidOperationException($"{appName} is in ProtectedApps: MixDock leaves it untouched.");
        Release();
        Log.Write($"Engage {appName} pid={pid}");
        var tree = SessionTakeover.ProcessTree(pid);
        var found = AudioDiscovery.FindPlayback(tree);
        Log.Write("sessions found: " + (found.Count == 0 ? "none" : string.Join("; ", found.Select(f => $"{f.endpointName} pid={f.pid} peak={f.peak:F3}"))));
        // Real device = where the app already plays (loudest/active session); otherwise the system default.
        var real = found.OrderByDescending(f => f.peak).FirstOrDefault();
        string realId = real.endpointId ?? AudioDiscovery.DefaultOutputId() ?? throw new InvalidOperationException("No audio output available");
        string realName = real.endpointName ?? "default output";
        var outs = AudioDiscovery.Outputs();
        if (!OutputPolicy.IsAllowed(outs.FirstOrDefault(o => o.Id == realId), _redirect.SinkId))
        {
            // the app was playing to a cable / Steam Streaming sink: never use that as MixDock's output
            var safe = OutputPolicy.Pick(outs, OutputPolicy.PreferredId, AudioDiscovery.DefaultOutputId(), _redirect.SinkId)
                ?? throw new InvalidOperationException("No real audio output found (only virtual devices are available)");
            Log.Write($"output {realName} is virtual; using {safe.Name} instead");
            realId = safe.Id; realName = safe.Name;
        }

        var preferred = outs.FirstOrDefault(o=>o.Id==OutputPolicy.PreferredId && OutputPolicy.IsAllowed(o,_redirect.SinkId));
        if(preferred!=null){realId=preferred.Id;realName=preferred.Name;}

        await _deck.StartAsync(pid);                       // capture first so nothing is lost
        await _engine.SetOutputAsync(realId);              // play on the same device the app used
        _realDevice = realId; DeviceName = realName; _pid = pid;
        Log.Write($"output device = {realName} ({realId})");

        string? sink = AppRedirect.ChooseSink(realId, null, _engine.CueId);
        if (sink == null)
        {
            Log.Write("no spare endpoint for sink: raw audio left audible");
            return $"LIVE on {realName} — note: {appName} is still audible (no spare output found to hide it)";
        }
        var (ok, failed, _) = _redirect.Take(pid, sink, realId);
        Log.Write($"redirected tree of {pid} to sink {sink}: {ok} ok, {failed} failed");
        if (ok == 0 && failed > 0) return $"LIVE on {realName} — note: {appName} is still audible (Windows refused to hide it)";
        return $"LIVE on {realName}";
    }

    /// <summary>Call ~every 500 ms: picks up new app processes (new tabs) so they are redirected too.</summary>
    public void Tick()
    {
        if (_pid is int pid && _redirect.Active && _redirect.SinkId is string sink && _realDevice is string real)
        {
            try { _redirect.Take(pid, sink, real); } catch (Exception ex) { Log.Write("tick redirect: " + ex.Message); }
        }
    }

    public void Release()
    {
        _redirect.Restore();
        _pid = null; DeviceName = null;
    }
}
