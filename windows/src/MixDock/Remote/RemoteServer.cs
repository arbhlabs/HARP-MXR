using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MixDock.Remote;

/// <summary>
/// Remote control listener for the MixDock phone app (protocol: docs/REMOTE_PROTOCOL.md in the Android repo).
/// DISABLED BY DEFAULT and bound to 127.0.0.1 only. LAN exposure is deliberately not implemented
/// until the user enables it; a phone reaches the loopback port with `adb reverse tcp:PORT tcp:PORT`.
/// </summary>
public sealed class RemoteOptions
{
    public bool Enabled { get; init; } = false;
    public int Port { get; init; } = 8765;
    /// <summary>Control messages (set/app/apps) accepted per second per connection; the excess is dropped.</summary>
    public int MaxMessagesPerSecond { get; init; } = 120;
}

/// <summary>What the remote may do to the engine. Implemented by the app (see EngineRemoteHost).</summary>
public interface IRemoteHost
{
    JsonObject GetState();
    void SetDeck(string deck, string param, double value);
    void SetMaster(string param, double value);
    IReadOnlyList<(string Id, string Name)> ListApps();
    void AssignApp(string deck, string id);
    (float A, float B) Peaks();
}

public sealed class RemoteServer : IAsyncDisposable
{
    private const int MaxFrame = 4096;
    private readonly RemoteOptions _opt;
    private readonly IRemoteHost _host;
    private readonly string _tokenFile;
    private readonly HashSet<string> _tokens = new();
    private readonly object _authGate = new();      // guards _tokens, the pairing code state and the token file
    private HttpListener? _http;
    private CancellationTokenSource? _cts;
    private string? _code; private DateTime _codeUntil; private int _bad; private DateTime _lockUntil;

    public RemoteServer(RemoteOptions opt, IRemoteHost host, string dataDir)
    {
        _opt = opt; _host = host; _tokenFile = Path.Combine(dataDir, "remote-tokens.txt");
        if (File.Exists(_tokenFile)) foreach (var l in File.ReadAllLines(_tokenFile)) if (l.Length == 32) _tokens.Add(l);
    }

    public bool Running => _http?.IsListening == true;

    /// <summary>One connection. WebSocket permits one outstanding send, and the state, peaks and pong replies come from different
    /// tasks, so every send goes through this async gate and is awaited to completion before the next starts.</summary>
    private sealed class Conn
    {
        public readonly WebSocket Ws;
        private readonly SemaphoreSlim _gate = new(1, 1);
        public Conn(WebSocket ws) { Ws = ws; }
        public async Task Send(JsonObject o, CancellationToken ct = default)
        {
            var bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
            await _gate.WaitAsync(ct);
            try { if (Ws.State == WebSocketState.Open) await Ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Starts listening on loopback. Does nothing unless Enabled.</summary>
    public void Start()
    {
        if (!_opt.Enabled || Running) return;
        _http = new HttpListener();
        _http.Prefixes.Add($"http://127.0.0.1:{_opt.Port}/mixdock/v1/");
        _http.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    /// <summary>Returns a fresh single-use 6-digit code valid for 120 s (show it on the PC).</summary>
    public string NewPairingCode()
    {
        lock (_authGate)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _codeUntil = DateTime.UtcNow.AddSeconds(120); _bad = 0;
            return _code;
        }
    }

    public void RevokeAll() { lock (_authGate) { _tokens.Clear(); try { if (File.Exists(_tokenFile)) File.Delete(_tokenFile); } catch { } } }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _http is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { break; }
            if (!ctx.Request.IsWebSocketRequest || !IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address))
            { ctx.Response.StatusCode = 403; ctx.Response.Close(); continue; }
            _ = Task.Run(() => Session(ctx, ct), ct);
        }
    }

    private async Task Session(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket; } catch { return; }
        using var _ = ws;
        var conn = new Conn(ws);
        var buf = new byte[MaxFrame];
        bool authed = false;
        long windowStart = Environment.TickCount64; int inWindow = 0;
        using var peaks = new CancellationTokenSource();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType != WebSocketMessageType.Text) { if (!r.EndOfMessage) { await Close(ws, WebSocketCloseStatus.InvalidMessageType); return; } continue; }
                if (!r.EndOfMessage) { await Close(ws, WebSocketCloseStatus.MessageTooBig); return; }
                JsonObject? m;
                try { m = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)) as JsonObject; } catch { continue; }
                if (m is null) continue;
                var t = (string?)m["t"];
                if (!authed)
                {
                    if (t == "auth" && TokenOk((string?)m["token"]))
                    { authed = true; await conn.Send(new JsonObject { ["t"] = "ready", ["v"] = 1 }); await conn.Send(StateMsg()); StartPeaks(conn, peaks.Token); }
                    else if (t == "pair" && TryPair((string?)m["code"], out var tok))
                    { authed = true; await conn.Send(new JsonObject { ["t"] = "paired", ["token"] = tok }); await conn.Send(StateMsg()); StartPeaks(conn, peaks.Token); }
                    else { await Close(ws, WebSocketCloseStatus.PolicyViolation); return; }
                    continue;
                }
                long now = Environment.TickCount64;
                if (now - windowStart >= 1000) { windowStart = now; inWindow = 0; }
                if (t != "ping" && ++inWindow > _opt.MaxMessagesPerSecond) continue;      // flood: drop, never act on it
                await Handle(conn, t, m);
            }
        }
        catch { /* socket dropped */ }
        finally { peaks.Cancel(); }
    }

    private async Task Handle(Conn c, string? t, JsonObject m)
    {
        switch (t)
        {
            case "set":
                var p = (string?)m["p"]; var v = (double?)m["v"] ?? 0;
                if (p is null) return;
                var deck = (string?)m["deck"];
                if (deck is "A" or "B") _host.SetDeck(deck, p, v); else _host.SetMaster(p, v);
                await c.Send(StateMsg()); break;
            case "app":
                var d = (string?)m["deck"];
                if (d is "A" or "B") _host.AssignApp(d, (string?)m["id"] ?? "");
                await c.Send(StateMsg()); break;
            case "apps":
                var arr = new JsonArray();
                foreach (var (id, name) in _host.ListApps()) arr.Add(new JsonObject { ["id"] = id, ["name"] = name });
                await c.Send(new JsonObject { ["t"] = "apps", ["list"] = arr }); break;
            case "ping": await c.Send(new JsonObject { ["t"] = "pong" }); break;
        }
    }

    private JsonObject StateMsg() { var s = _host.GetState(); s["t"] = "state"; return s; }

    private void StartPeaks(Conn c, CancellationToken ct) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested && c.Ws.State == WebSocketState.Open)
        {
            try { var (a, b) = _host.Peaks(); await c.Send(new JsonObject { ["t"] = "peaks", ["A"] = Math.Round(a, 3), ["B"] = Math.Round(b, 3) }, ct); await Task.Delay(66, ct); }
            catch { break; }
        }
    });

    private bool TokenOk(string? tok)
    {
        if (tok is null || tok.Length != 32) return false;
        bool ok = false;
        lock (_authGate)
            foreach (var known in _tokens)
                ok |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(known), Encoding.ASCII.GetBytes(tok));
        return ok;
    }

    private bool TryPair(string? code, out string token)
    {
        token = "";
        lock (_authGate)
        {
            if (DateTime.UtcNow < _lockUntil || _code is null || DateTime.UtcNow > _codeUntil || code is null || code.Length != 6) return false;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(_code), Encoding.ASCII.GetBytes(code)))
            { if (++_bad >= 5) { _lockUntil = DateTime.UtcNow.AddSeconds(60); _code = null; } return false; }
            _code = null;
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _tokens.Add(token);
            try { File.WriteAllLines(_tokenFile, _tokens); }
            catch { _tokens.Remove(token); token = ""; return false; }       // cannot persist the pairing: do not grant it
            return true;
        }
    }

    private static async Task Close(WebSocket ws, WebSocketCloseStatus s)
    { try { await ws.CloseOutputAsync(s, null, CancellationToken.None); } catch { } }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel(); try { _http?.Stop(); } catch { }
        await Task.CompletedTask;
    }
}
