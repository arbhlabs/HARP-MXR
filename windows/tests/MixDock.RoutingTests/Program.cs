using MixDock.Audio;


int passed = 0, failed = 0;
void Check(bool ok, string name, string detail = "")
{
    if (ok) { passed++; Console.WriteLine("PASS " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
    else { failed++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
}
string Id(string mm) => "POLICY:" + mm;
string tmp = Path.Combine(Path.GetTempPath(), "mixdock-routing-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
RedirectJournal NewJournal() => new(Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".json"));

Console.WriteLine("--- redirect: success, exact restore");
{
    var pol = new FakePolicy(); pol.Seed(100, 1, "POLICY:HEADSET"); pol.Seed(100, 0, "POLICY:SPEAKERS");
    var j = NewJournal(); var core = new RedirectCore(pol, j, Id);
    var r = core.Take(new[] { 100, 101 }, "SINK");
    Check(r.ok == 2 && r.failed == 0, "both processes redirected", $"{r}");
    Check(pol.Cur(100, 1) == "POLICY:SINK" && pol.Cur(100, 0) == "POLICY:SINK", "pid 100 points at the sink");
    Check(j.Load().Count == 2, "journal lists both processes");
    Check(core.Restore(), "restore reports success");
    Check(pol.Cur(100, 1) == "POLICY:HEADSET" && pol.Cur(100, 0) == "POLICY:SPEAKERS", "pid 100 gets its OWN previous endpoints back (not default)");
    Check(pol.Cur(101, 1) == "" && pol.Cur(101, 0) == "", "pid 101 (had none) is cleared");
    Check(j.Load().Count == 0, "journal emptied after verified restore");
}

Console.WriteLine("--- redirect: failed HRESULT is not recorded as redirected");
{
    var pol = new FakePolicy(); pol.Seed(200, 1, "POLICY:HEADSET"); pol.FailSet.Add((200, 0));
    var j = NewJournal(); var core = new RedirectCore(pol, j, Id);
    var r = core.Take(new[] { 200 }, "SINK");
    Check(r.ok == 0 && r.failed == 1, "reported as failed", $"{r}");
    Check(core.RedirectedCount == 0, "not tracked as redirected");
    Check(pol.Cur(200, 1) == "POLICY:HEADSET", "role that did succeed was rolled back to the original");
    Check(j.Load().Count == 0, "no journal entry left for a rolled-back change");
}

Console.WriteLine("--- redirect: Set returns OK but does not stick (verification)");
{
    var pol = new FakePolicy(); pol.Ignore.Add((300, 0));
    var core = new RedirectCore(pol, NewJournal(), Id);
    var r = core.Take(new[] { 300 }, "SINK");
    Check(r.failed == 1 && r.ok == 0, "unverified redirect counts as failure", $"{r}");
    Check(pol.Cur(300, 1) == "", "partial change reverted");
}

Console.WriteLine("--- restore failure is retained");
{
    var pol = new FakePolicy(); pol.Seed(400, 1, "POLICY:HEADSET");
    var j = NewJournal(); var core = new RedirectCore(pol, j, Id);
    core.Take(new[] { 400 }, "SINK");
    pol.FailSet.Add((400, 1));
    Check(!core.Restore(), "restore reports failure");
    Check(j.Load().Count == 1 && core.RedirectedCount == 1, "entry stays journaled and tracked");
    pol.FailSet.Clear();
    Check(core.Restore() && pol.Cur(400, 1) == "POLICY:HEADSET" && j.Load().Count == 0, "retry restores the exact endpoint and clears the journal");
}

Console.WriteLine("--- two decks share one journal");
{
    var pol = new FakePolicy(); pol.Seed(500, 1, "POLICY:X");
    var j = NewJournal(); var a = new RedirectCore(pol, j, Id); var b = new RedirectCore(pol, j, Id);
    a.Take(new[] { 500 }, "SINK"); b.Take(new[] { 600 }, "SINK");
    Check(j.Load().Count == 2, "second deck does not overwrite the first deck entries");
    var r = b.Take(new[] { 500 }, "SINK");
    Check(r.foreign == 1 && r.ok == 0, "a process owned by the other deck is not re-taken (original kept)", $"{r}");
    Check(b.Restore(), "deck B releases");
    Check(pol.Cur(500, 1) == "POLICY:SINK" && j.Load().Count == 1 && j.Load()[0].Pid == 500, "deck A redirect and journal entry survive deck B release");
    Check(a.Restore() && pol.Cur(500, 1) == "POLICY:X" && j.Load().Count == 0, "deck A releases and restores the original");
}

Console.WriteLine("--- crash recovery from the journal");
{
    var pol = new FakePolicy(); pol.Seed(700, 1, "POLICY:HEADSET"); pol.Seed(700, 0, "POLICY:HEADSET");
    var j = NewJournal(); new RedirectCore(pol, j, Id).Take(new[] { 700, 701 }, "SINK");   // process "dies" without Restore
    var fresh = new RedirectCore(pol, j, Id);
    Check(fresh.Recover() == 0, "recovery leaves nothing behind");
    Check(pol.Cur(700, 1) == "POLICY:HEADSET" && pol.Cur(700, 0) == "POLICY:HEADSET" && pol.Cur(701, 1) == "", "originals restored after a crash");
    Check(j.Load().Count == 0, "journal removed");
}
{
    var pol = new FakePolicy(); pol.Seed(800, 1, "POLICY:HEADSET");
    var j = NewJournal(); new RedirectCore(pol, j, Id).Take(new[] { 800 }, "SINK");
    pol.FailSet.Add((800, 1)); var alive = true;
    var fresh = new RedirectCore(pol, j, Id, _ => alive);
    Check(fresh.Recover() == 1 && j.Load().Count == 1, "failed recovery for a live process keeps the entry");
    Check(fresh.Recover() == 1 && fresh.Recover() == 1, "live process is retried indefinitely");
    alive = false; var g = new RedirectCore(pol, j, Id, _ => alive);
    int left = 1; for (int i = 0; i < 5 && left > 0; i++) left = g.Recover();
    Check(left == 0 && j.Load().Count == 0, "entry for a dead process is dropped after a bounded number of attempts");
}
{
    var path = Path.Combine(tmp, "legacy.json"); File.WriteAllText(path, "[900, 901]");
    var pol = new FakePolicy(); pol.Seed(900, 1, "POLICY:SINK");
    var core = new RedirectCore(pol, new RedirectJournal(path), Id);
    Check(core.Recover() == 0 && pol.Cur(900, 1) == "", "legacy pid-list journal still recovers (clears)");
}

Console.WriteLine("--- sink protection");
{
    var devs = new[] { ("hp", "Headphones"), ("spk", "Speakers"), ("hdmi", "NVIDIA High Definition Audio"), ("cable", "CABLE Input (VB-Audio)"), ("steam", "Steam Streaming Speakers") };
    Check(SinkPolicy.Choose(devs, null, "spk") == "steam", "prefers Steam Streaming sink");
    Check(SinkPolicy.Choose(devs.Where(d => d.Item1 != "steam"), null, "spk") == "cable", "then a cable");
    Check(SinkPolicy.Choose(devs, null, "spk", "steam", "cable") == null, "protected ids skipped and real HDMI spare rejected");
    Check(SinkPolicy.Choose(devs, null, "spk", "steam", "cable", "hdmi") == null, "no spare output -> null (caller warns that the app stays audible)");
    Check(SinkPolicy.Choose(devs, "hdmi", "hdmi") != "hdmi", "a preferred sink that is protected is refused");
    Check(SinkPolicy.Choose(new[] { ("hp", "Headphones"), ("spk", "Speakers") }, null) == null, "headphones and speakers are never picked automatically");
    Check(SinkPolicy.Choose(new[] { ("hdmi", "NVIDIA High Definition Audio") }, null) == null, "HDMI-only spare stays audible");
    Check(SinkPolicy.Choose(devs, "hdmi", "spk") == "hdmi", "explicit HDMI preference allowed");
    Check(!MixDock.Platform.OutputPolicy.IsAllowed(new MixDock.Platform.OutputEndpoint("steam", "Steam Streaming Speakers")), "OutputPolicy still refuses virtual outputs");
}

try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine($"=== {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakePolicy : IEndpointPolicy
{
    private readonly Dictionary<(uint, int), string> _s = new();
    public HashSet<(int, int)> FailSet = new(), Ignore = new();
    public void Seed(int pid, int role, string id) => _s[((uint)pid, role)] = id;
    public string Cur(int pid, int role) => _s.GetValueOrDefault(((uint)pid, role), "");
    public int Get(uint pid, int role, out string id) { id = Cur((int)pid, role); return 0; }
    public int Set(uint pid, int role, string? id)
    {
        if (FailSet.Contains(((int)pid, role))) return unchecked((int)0x80070057);
        if (Ignore.Contains(((int)pid, role))) return 0;
        _s[(pid, role)] = id ?? ""; return 0;
    }
}

