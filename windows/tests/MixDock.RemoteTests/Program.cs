using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using MixDock.Remote;

int passed = 0, failed = 0;
void Check(bool ok, string name, string detail = "")
{
    if (ok) { passed++; Console.WriteLine("PASS " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
    else { failed++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
}
string tmp = Path.Combine(Path.GetTempPath(), "mixdock-remote-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
int port = FreePort();
static int FreePort()
{
    for (int i = 0; i < 50; i++)
    {
        int p = 18700 + Random.Shared.Next(2000);
        try { var a = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p); var b = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p + 1); a.Start(); b.Start(); a.Stop(); b.Stop(); return p; } catch { }
    }
    throw new InvalidOperationException("no free loopback port");
}

async Task<ClientWebSocket> Connect(int p)
{
    var c = new ClientWebSocket();
    await c.ConnectAsync(new Uri($"ws://127.0.0.1:{p}/mixdock/v1/"), CancellationToken.None);
    return c;
}
Task Tx(ClientWebSocket c, object o) => c.SendAsync(Encoding.UTF8.GetBytes(o is string s ? s : System.Text.Json.JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, CancellationToken.None);
async Task<JsonObject?> Rx(ClientWebSocket c, int ms = 2000)
{
    using var cts = new CancellationTokenSource(ms); var buf = new byte[8192]; var sb = new StringBuilder();
    try
    {
        WebSocketReceiveResult r;
        do { r = await c.ReceiveAsync(buf, cts.Token); if (r.MessageType == WebSocketMessageType.Close) return null; sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); } while (!r.EndOfMessage);
        return JsonNode.Parse(sb.ToString()) as JsonObject;
    }
    catch (OperationCanceledException) { return null; }
    catch (WebSocketException) { return null; }
}
async Task<JsonObject?> RxType(ClientWebSocket c, string t, int ms = 3000)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { var m = await Rx(c, 500); if (m == null) { if (c.State != WebSocketState.Open) return null; continue; } if ((string?)m["t"] == t) return m; }
    return null;
}

var host = new FakeHost();
var srv = new RemoteServer(new RemoteOptions { Enabled = true, Port = port, MaxMessagesPerSecond = 50 }, host, tmp);

Console.WriteLine("--- disabled by default");
{
    Check(!new RemoteOptions().Enabled, "RemoteOptions.Enabled defaults to false");
    var off = new RemoteServer(new RemoteOptions { Enabled = false, Port = port + 300 }, host, tmp); off.Start();
    Check(!off.Running, "a disabled server never listens");
}

srv.Start();
Check(srv.Running, "enabled server listens on loopback");

Console.WriteLine("--- auth");
{
    using var c = await Connect(port);
    await Tx(c, new { t = "set", p = "mute", v = 1, deck = "A" });
    var m = await Rx(c);
    Check(m == null && host.Sets.Count == 0, "control message before auth is refused and closes the socket");
}
{
    using var c = await Connect(port);
    await Tx(c, new { t = "auth", token = new string('a', 32) });
    Check(await Rx(c) == null && c.State != WebSocketState.Open || c.State == WebSocketState.CloseReceived, "unknown token closes the connection");
}
string tok;
{
    string code = srv.NewPairingCode();
    using var c = await Connect(port);
    await Tx(c, new { t = "pair", code });
    var m = await RxType(c, "paired");
    tok = (string?)m?["token"] ?? "";
    Check(tok.Length == 32, "correct code pairs and returns a 32 hex token");
    Check(File.ReadAllLines(Path.Combine(tmp, "remote-tokens.txt")).Contains(tok), "token persisted");
    Check(await RxType(c, "state") != null, "state sent after pairing");
    Check(await RxType(c, "peaks") != null, "peaks stream after pairing");
    using var c2 = await Connect(port);
    await Tx(c2, new { t = "pair", code });
    Check(await Rx(c2) == null, "pairing code is single use");
}
{
    using var c = await Connect(port);
    await Tx(c, new { t = "auth", token = tok });
    Check(await RxType(c, "ready") != null, "stored token authenticates");
}

Console.WriteLine("--- bad pairing lockout");
{
    string code = srv.NewPairingCode(); string wrong = code == "000000" ? "111111" : "000000";
    for (int i = 0; i < 5; i++) { using var c = await Connect(port); await Tx(c, new { t = "pair", code = wrong }); await Rx(c, 500); }
    using var ok = await Connect(port);
    await Tx(ok, new { t = "pair", code });
    Check(await Rx(ok, 1000) == null, "five wrong codes lock pairing out even for the right code");
}
{
    srv.NewPairingCode();
    using var c = await Connect(port);
    await Tx(c, new { t = "pair", code = "12" });
    Check(await Rx(c, 1000) == null, "short code is rejected");
}

Console.WriteLine("--- frames");
{
    using var c = await Connect(port);
    await Tx(c, new { t = "auth", token = tok });
    await RxType(c, "ready");
    await Tx(c, "not json {{{");
    await Tx(c, new { t = "ping" });
    Check(await RxType(c, "pong") != null, "malformed JSON is ignored and the connection survives");
    await Tx(c, new { t = "set", p = "mute", v = 1, deck = "Z" });
    await Tx(c, new { t = "app", deck = "Q", id = "x" });
    await Tx(c, new { t = "ping" });
    Check(await RxType(c, "pong") != null && host.Assigned.Count == 0, "unknown deck names are ignored");
    await c.SendAsync(new byte[5000], WebSocketMessageType.Text, true, CancellationToken.None);
    Check(await RxType(c, "pong", 1500) == null, "frame larger than 4096 bytes closes the connection");
}

Console.WriteLine("--- concurrent sends are serialised");
{
    using var c = await Connect(port);
    await Tx(c, new { t = "auth", token = tok });
    await RxType(c, "ready");
    // peaks stream (every ~66 ms) while replies are triggered at the same time from other tasks
    int bad = 0, n = 0; var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var reader = Task.Run(async () => { while (!stop.IsCancellationRequested) { var m = await Rx(c, 300); if (m == null) { if (c.State != WebSocketState.Open) { bad++; break; } continue; } n++; if (m["t"] == null) bad++; } });
    var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { int i = 0; while (!stop.IsCancellationRequested && i++ < 40) { await Tx(c, new { t = "apps" }); await Task.Delay(20); } })).ToArray();
    await Task.WhenAll(writers); stop.Cancel(); await reader;
    Check(bad == 0 && n > 40 && c.State == WebSocketState.Open, "interleaved state/apps/peaks replies all arrive intact, socket stays open", $"{n} messages, {bad} bad");
}

Console.WriteLine("--- rate limit");
{
    host.Sets.Clear();
    using var c = await Connect(port);
    await Tx(c, new { t = "auth", token = tok });
    await RxType(c, "ready");
    for (int i = 0; i < 400; i++) await Tx(c, new { t = "set", p = "trim", v = 0.5, deck = "A" });
    await Task.Delay(500);
    Check(host.Sets.Count > 0 && host.Sets.Count <= 50 * 2 + 5, "a flood of set messages is capped per second", $"{host.Sets.Count} applied of 400");
    await Tx(c, new { t = "ping" });
    Check(await RxType(c, "pong", 4000) != null, "ping is always answered, even right after a flood");
}

Console.WriteLine("--- token store under concurrency");
{
    // fresh server: the earlier lockout (60 s) deliberately survives new codes on the first one
    await srv.DisposeAsync();
    srv = new RemoteServer(new RemoteOptions { Enabled = true, Port = port + 1 }, host, tmp); srv.Start(); port += 1;
    int okCount = 0;
    for (int i = 0; i < 6; i++)
    {
        string code = srv.NewPairingCode();
        using var c = await Connect(port);
        await Tx(c, new { t = "pair", code });
        if (await RxType(c, "paired") != null) okCount++;
    }
    var lines = File.ReadAllLines(Path.Combine(tmp, "remote-tokens.txt"));
    Check(okCount == 6 && lines.Distinct().Count() == lines.Length && lines.Length >= 6, "sequential pairings all persist as distinct tokens", $"{lines.Length} tokens");
    srv.RevokeAll();
    Check(!File.Exists(Path.Combine(tmp, "remote-tokens.txt")), "revoke removes the token file");
    using var c3 = await Connect(port);
    await Tx(c3, new { t = "auth", token = tok });
    Check(await Rx(c3, 1000) == null, "a revoked token no longer authenticates");
}

await srv.DisposeAsync();
try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine($"=== {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeHost : IRemoteHost
{
    public List<(string, string, double)> Sets = new(); public List<(string, string)> Assigned = new();
    public JsonObject GetState() => new() { ["A"] = new JsonObject(), ["B"] = new JsonObject() };
    public void SetDeck(string deck, string param, double value) { lock (Sets) Sets.Add((deck, param, value)); }
    public void SetMaster(string param, double value) { lock (Sets) Sets.Add(("", param, value)); }
    public IReadOnlyList<(string Id, string Name)> ListApps() => new[] { ("1", "App") };
    public void AssignApp(string deck, string id) { Assigned.Add((deck, id)); }
    public (float A, float B) Peaks() => (0.1f, 0.2f);
}
