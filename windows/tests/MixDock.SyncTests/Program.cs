using MixDock.Audio;

int passed = 0, failed = 0;
void Check(bool ok, string name, string detail = "")
{
    if (ok) { passed++; Console.WriteLine("PASS " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
    else { failed++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
}
double Bar(SynthTrack t, double frame) { double b = (frame - t.Offset) / t.Period; b %= 4; return b < 0 ? b + 4 : b; }

(BpmDetector d, SynthTrack t) Detect(double bpm, double seconds, Action<SynthTrack>? cfg = null)
{
    var t = new SynthTrack(bpm, 12345.6); cfg?.Invoke(t);
    var d = new BpmDetector { Synchronous = true };
    var buf = new float[882];
    for (long f = 0; f < seconds * 44100; f += 441) { t.Fill(buf, f); d.Feed(buf); }
    return (d, t);
}
// error of the detected downbeat grid vs the true downbeats, in ms (signed, wrapped to +-half a beat)
double OriginErrMs(BpmDetector d, SynthTrack t)
{
    var g = d.Grid!; double e = (g.OriginFrame - t.Offset) / t.Period; e -= Math.Round(e); return e * t.Period / 44.1;
}
double BarErrBeats(BpmDetector d, SynthTrack t)    // 0 when the detected downbeat is a true downbeat
{
    var g = d.Grid!; double b = (g.OriginFrame - t.Offset) / t.Period; double m = ((b % 4) + 4) % 4; return Math.Min(m, 4 - m);
}

Console.WriteLine("--- tempo and grid detection (0.01 BPM resolution, fractional BPM)");
var biases = new List<double>();
foreach (double bpm in new[] { 87.0, 100.0, 120.0, 124.37, 128.0, 140.5, 150.0, 174.0 })
{
    var (d, t) = Detect(bpm, 30);
    var g = d.Grid;
    // above ~150 BPM the tempo can be read at half speed (87 or 174 are the same grid at 2:1); the sync locks either at 2:1
    bool octave = bpm > 150;
    Check(g != null && (Math.Abs(g.Bpm - bpm) < 0.05 || (octave && Math.Abs(g.Bpm * 2 - bpm) < 0.1)), $"BPM {bpm:F2} detected within 0.05{(octave ? " (or its half)" : "")}", g == null ? "no grid" : $"got {g.Bpm:F2}");
    if (g != null) { biases.Add(OriginErrMs(d, t)); Check((octave || g.DownKnown) && (BarErrBeats(d, t) < 0.05 || (octave && Math.Abs(BarErrBeats(d, t) - Math.Round(BarErrBeats(d, t))) < 0.05)), $"downbeat found at {bpm:F2} BPM", $"origin err {OriginErrMs(d, t):F2} ms, bar offset {BarErrBeats(d, t):F2} beats"); }
}
Check(biases.Count > 0 && biases.Max() - biases.Min() < 0.8, "grid phase bias is constant across tempos (spread < 0.8 ms)", $"bias {biases.Min():F2}..{biases.Max():F2} ms");

Console.WriteLine("--- real-like material: swing, drops and breakdowns, humanised timing");
{
    var (d, t) = Detect(124.37, 40, x => { x.Swing = 0.18; });
    Check(d.Grid != null && Math.Abs(d.Grid.Bpm - 124.37) < 0.05, "swung hats (18%) still read 124.37", $"got {d.Grid?.Bpm:F2}");
    var (d2, t2) = Detect(126.0, 50, x => { x.Breakdowns = new[] { (4, 8), (16, 20) }; });
    Check(d2.Grid != null && Math.Abs(d2.Grid.Bpm - 126.0) < 0.05, "breakdowns (kick drops out) keep 126.00", $"got {d2.Grid?.Bpm:F2}");
    var (d3, t3) = Detect(122.5, 40, x => { x.Jitter = 1.2; });
    Check(d3.Grid != null && Math.Abs(d3.Grid.Bpm - 122.5) < 0.05, "humanised +-1.2 ms timing keeps 122.50", $"got {d3.Grid?.Bpm:F2}");
    Check(d3.Grid != null && Math.Abs(OriginErrMs(d3, t3) - biases.Average()) < 1.5, "humanised grid phase within 1.5 ms of the clean bias", $"{OriginErrMs(d3, t3):F2} ms");
}

// ====================================================================== continuous sync simulation
const int Blk = 441;
double NearestDiff(List<double> master, double t)
{
    int lo = 0, hi = master.Count - 1;
    while (lo < hi) { int mid = (lo + hi) / 2; if (master[mid] < t) lo = mid + 1; else hi = mid; }
    double best = double.MaxValue;
    for (int k = Math.Max(0, lo - 1); k <= Math.Min(master.Count - 1, lo); k++) { double d = t - master[k]; if (Math.Abs(d) < Math.Abs(best)) best = d; }
    return best;
}
SyncResult RunSync(double bpmM, double bpmS, double seconds, double enableAt = 12, bool print = true, int mult = 1, Func<SimDeck, SimDeck, SyncController, int, bool>? hook = null, Action<SynthTrack>? cfgS = null, bool slaveFile = false)
{
    var A = new SimDeck(new SynthTrack(bpmM, 5000.3)); var B = new SimDeck(new SynthTrack(bpmS, 31234.7)) { IsFile = slaveFile };
    cfgS?.Invoke(B.Track);
    var ctl = new SyncController(A, A.Reader, B, B.Reader);
    var logA = new BeatLog(); var logB = new BeatLog();
    var oa = new float[Blk * 2]; var ob = new float[Blk * 2];
    var res = new SyncResult(); long o = 0; int steps = (int)(seconds * 44100 / Blk), enableStep = (int)(enableAt * 44100 / Blk);
    double prevRateS = 1, prevRateM = 1; float pa = 0, pb = 0; double minLead = 1e9, maxLead = 0;
    for (int s = 0; s < steps; s++)
    {
        A.Feed(Blk); B.Feed(Blk);
        if (s == enableStep) ctl.Enable(1);
        if (hook != null && hook(A, B, ctl, s)) { }
        ctl.Process(Blk);
        logA.BlockStart(A, o); logB.BlockStart(B, o);
        A.Reader.Read(oa); B.Reader.Read(ob);
        logA.BlockEnd(A, o, Blk, A.Reader.EffectiveRate); logB.BlockEnd(B, o, Blk, B.Reader.EffectiveRate);
        o += Blk;
        if (s > enableStep)
        {
            res.MaxRateStep = Math.Max(res.MaxRateStep, Math.Max(Math.Abs(ctl.CurrentRate(1) - prevRateS), Math.Abs(ctl.CurrentRate(0) - prevRateM)));
            if (B.Reader.HistoryMode && s > enableStep + 300) { double lead = B.Reader.Player!.Lead / 44.1; minLead = Math.Min(minLead, lead); maxLead = Math.Max(maxLead, lead); }
            if (A.Reader.HistoryMode && s > enableStep + 300) { double lead = A.Reader.Player!.Lead / 44.1; minLead = Math.Min(minLead, lead); maxLead = Math.Max(maxLead, lead); }
        }
        prevRateS = ctl.CurrentRate(1); prevRateM = ctl.CurrentRate(0);
        for (int i = 0; i < Blk; i++)
        {
            res.MaxStepA = Math.Max(res.MaxStepA, Math.Abs(oa[2 * i] - pa)); pa = oa[2 * i];
            res.MaxStepB = Math.Max(res.MaxStepB, Math.Abs(ob[2 * i] - pb)); pb = ob[2 * i];
        }
        if (s == enableStep + 100) res.Role = $"{ctl.Role(0)}/{ctl.Role(1)}";
    }
    res.Underruns = A.Reader.Underruns + B.Reader.Underruns; res.Rephases = ctl.Rephases; res.MinLeadMs = minLead; res.MaxLeadMs = maxLead;
    res.RateS = ctl.CurrentRate(1); res.RateM = ctl.CurrentRate(0); res.Status = ctl.Status;
    // phase error: every slave beat after the lock-in, against the nearest master beat (or half-beat for a 2:1 lock)
    var mb = logA.Beats.Select(x => x.t).ToList();
    if (mult == 2) { var half = new List<double>(); for (int i = 0; i + 1 < mb.Count; i++) { half.Add(mb[i]); half.Add((mb[i] + mb[i + 1]) / 2); } mb = half; }
    // mult == -2: the master (174) is read at half time, so which of its kicks carry the bar-level downbeat is ambiguous;
    // what must hold is that every slave kick lands on a master kick (any parity), checked against all master beats.
    double lockFrom = (enableAt + 45) * 44100;
    var errs = new List<double>(); var barOk = 0; var barTot = 0;
    foreach (var (n, t) in logB.Beats)
    {
        if (t < lockFrom || mb.Count < 4 || t > mb[^2]) continue;
        errs.Add(NearestDiff(mb, t) / 44.1);
        if (mult == 1 && ((n % 4) + 4) % 4 == 0)
        {
            var near = logA.Beats.OrderBy(x => Math.Abs(x.t - t)).First();
            barTot++; if (((near.n % 4) + 4) % 4 == 0) barOk++;
        }
    }
    res.Beats = errs.Count; res.MaxErrMs = errs.Count > 0 ? errs.Max(Math.Abs) : double.NaN; res.MeanErrMs = errs.Count > 0 ? errs.Average() : double.NaN;
    res.BarAligned = barTot == 0 ? 1 : barOk / (double)barTot;
    if (print) Console.WriteLine($"    {bpmM} vs {bpmS}: roles {res.Role}, beats {res.Beats}, phase err max {res.MaxErrMs:F3} ms mean {res.MeanErrMs:+0.000;-0.000} ms, bars aligned {res.BarAligned:P0}, rates M {res.RateM:F5} S {res.RateS:F5}, max rate step {res.MaxRateStep * 100:F4}%/block, lead {res.MinLeadMs:F0}..{res.MaxLeadMs:F0} ms, rephases {res.Rephases}, underruns {res.Underruns}, max step A {res.MaxStepA:F3} B {res.MaxStepB:F3}; {res.Status}");
    return res;
}

Console.WriteLine("--- continuous sync on LIVE decks (5 simulated minutes each, 4 s max delay buffer)");
foreach (var (m, sl, mult) in new[] { (125.0, 124.0, 1), (124.0, 125.0, 1), (120.0, 128.0, 1), (128.0, 120.0, 1), (87.0, 174.0, 2), (174.0, 87.0, -2) })
{
    var r = RunSync(m, sl, 300, mult: mult);
    string tag = $"{m} vs {sl}";
    Check(r.MaxErrMs <= 1.0 && r.Beats > 300, $"{tag}: beats stay within 1 ms for 5 min", $"max {r.MaxErrMs:F3} ms over {r.Beats} beats");
    Check(r.Underruns == 0, $"{tag}: no dropouts", $"underruns {r.Underruns}");
    Check(r.MinLeadMs >= 90 && r.MaxLeadMs <= 4100, $"{tag}: delay buffer stays inside 100 ms..4 s", $"{r.MinLeadMs:F0}..{r.MaxLeadMs:F0} ms");
    Check(r.MaxRateStep < 0.001, $"{tag}: speed never steps more than 0.1% per block", $"{r.MaxRateStep * 100:F4}%");
    if (mult == 1) Check(r.BarAligned > 0.99, $"{tag}: downbeats line up (bar level)", $"{r.BarAligned:P0}");
}

Console.WriteLine("--- AutoMix hand-over (DeckSync.Glide -> SyncController.HandOver)");
{
    int handAt = (int)(40.0 * 44100 / Blk);
    var r = RunSync(120, 126, 60, hook: (a, b, c, s) => { if (s == handAt) c.HandOver(1, 4); return false; });
    Check(r.Role.StartsWith("Master") || r.Role.Contains("Slave"), "hand-over test: sync was locked before hand-over", r.Role);
    Check(Math.Abs(r.RateS - 1.0) < 0.01, "hand-over: incoming deck eased back to native speed (within the 0.5% lead catch-up)", $"rate {r.RateS:F5}");
    Check(r.Underruns == 0 && r.MaxRateStep < 0.001, "hand-over: no dropouts or speed steps", $"underruns {r.Underruns}, step {r.MaxRateStep * 100:F4}%");
}

Console.WriteLine($"=== {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class SyncResult
{
    public double MaxErrMs, MeanErrMs, MaxRateStep, MinLeadMs, MaxLeadMs, RateS, RateM, MaxStepA, MaxStepB, BarAligned; public int Beats, Underruns, Rephases; public string Role = "", Status = "";
}
