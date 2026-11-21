using MixDock.Audio;

var count = 0;
void Assert(bool ok, string test)
{
    if (!ok) throw new Exception("FAIL: " + test);
    Console.WriteLine("PASS " + test); count++;
}
static double Rms(float[] x)
{
    double sum = 0;
    foreach (var v in x) { if (!float.IsFinite(v)) throw new Exception("non-finite audio sample"); sum += v * v; }
    return Math.Sqrt(sum / x.Length);
}
static float[] Tone(double hz, int stereoFrames = 44100)
{
    var v = new float[stereoFrames * 2];
    for (int i = 0; i < stereoFrames; i++)
    {
        v[i * 2] = v[i * 2 + 1] = (float)(.2 * Math.Sin(2 * Math.PI * hz * i / 44100));
    }
    return v;
}
static double Level(DeckParameters p, double hz)
{
    var dsp = new DeckDsp { Parameters = p };
    var input = Tone(hz);
    var output = new float[input.Length];
    dsp.Process(input, output);
    return Rms(output.AsSpan(22050).ToArray());
}
Assert(Level(new(Trim:1), 1000) > 0.1, "unity signal present");
Assert(Level(new(LowDb:12, Trim:1), 60) > Level(new(LowDb:0, Trim:1), 60) * 2.5,
    "low shelf +12 dB boosts bass");
Assert(Level(new(MidDb:12, Trim:1), 1000) > Level(new(MidDb:0, Trim:1), 1000) * 2.5,
    "mid peak +12 dB boosts mids");
Assert(Level(new(HighDb:12, Trim:1), 12000) > Level(new(HighDb:0, Trim:1), 12000) * 2.5,
    "high shelf +12 dB boosts treble");
Assert(Level(new(Filter:-1, Trim:1), 9000) < Level(new(Filter:0, Trim:1), 9000) / 12,
    "low-pass filter suppresses 9kHz");
Assert(Level(new(Filter:1, Trim:1), 100) < Level(new(Filter:0, Trim:1), 100) / 12,
    "high-pass filter suppresses 100Hz");
Assert(Level(new(Mute:true, Trim:1), 1000) < .003,
    "mute smooths to silence");
var ring = new SampleRing(4);
ring.Write(new float[] {1,1,2,2,3,3,4,4});
ring.Write(new float[] {5,5,6,6});
var dest = new float[8];
Assert(ring.Read(dest) == 8 && dest.SequenceEqual(new float[] {3,3,4,4,5,5,6,6}),
    "FIFO overrun discards oldest stereo frames");
Assert(ring.Read(dest) == 0 && dest.All(x => x == 0), "underrun returns silence");
var changing = new DeckDsp();
var signal = Tone(300);
var result = new float[signal.Length];
for (int i = -50; i <= 50; i++)
{
    changing.Parameters = new DeckParameters(i / 4.0, 0, 0, i / 50.0, 1);
    changing.Process(signal, result);
    Assert(result.All(float.IsFinite), "finite filter after parameter change " + i);
}
// ---------- 1.1: fractional filter, slip roll, echo freeze, riser ----------
Assert(Math.Abs(Level(new(Filter:-0.501, Trim:1), 3000) - Level(new(Filter:-0.5, Trim:1), 3000)) > 1e-5, "filter accepts fractional values (0.001 step is heard)");
{
    var g = new DeckDsp { Parameters = new(Filter: -0.2, Trim: 1) }; var tone = Tone(2000, 4410); var o = new float[tone.Length];
    g.Process(tone, o); g.Parameters = new(Filter: -0.9, Trim: 1);
    var blk = new float[256]; g.Process(tone.AsSpan(0, 256), blk);
    Assert(g.FilterNow > -0.9 && g.FilterNow < -0.2, $"filter jump glides instead of stepping (after 128 frames at {g.FilterNow:0.000})");
    for (int i = 0; i < 40; i++) g.Process(tone.AsSpan(0, 256), blk);
    Assert(Math.Abs(g.FilterNow + 0.9) < 1e-3, "glide reaches the target");
}
{
    var ring2 = new SampleRing(44100 * 4); var rd = new DeckReader(ring2, new DeckDsp { Parameters = new(Trim: 0.85) });
    var ramp = new float[44100 * 2 * 3]; for (int i = 0; i < ramp.Length / 2; i++) ramp[2 * i] = ramp[2 * i + 1] = (float)Math.Sin(i * 0.0137) * 0.5f;
    ring2.Write(ramp.AsSpan(0, 44100 * 2));
    var outBuf = new List<float>(); var b2 = new float[512];
    for (int i = 0; i < 20; i++) { rd.Read(b2); outBuf.AddRange(b2); ring2.Write(ramp.AsSpan(44100 * 2 + i * 512, 512)); }
    int before = outBuf.Count / 2; const int L = 3000;
    rd.StartRoll(0, L); Assert(rd.Rolling, "roll flagged");
    for (int i = 0; i < 100; i++) { rd.Read(b2); outBuf.AddRange(b2); ring2.Write(ramp.AsSpan(44100 * 2 + (20 + i) * 512, 512)); }
    double maxDiff = 0; for (int n = before + 2 * L; n < before + 5 * L; n++) { if ((n - before) % L > L - 140) continue; maxDiff = Math.Max(maxDiff, Math.Abs(outBuf[2 * n] - outBuf[2 * (n + L)])); }
    Assert(maxDiff < 1e-4, $"slip roll repeats the captured beat slice exactly (live deck, no seek), diff {maxDiff:g3}");
    double seam = 0; int seamAt = 0; for (int n = before + 1; n < before + 6 * L; n++) { double st = Math.Abs(outBuf[2 * n] - outBuf[2 * n - 2]); if (st > seam) { seam = st; seamAt = n - before; } }
    Assert(seam < 0.05, $"no click at loop seams (max step {seam:0.0000} at {seamAt})");
    rd.StopRoll(); int at = outBuf.Count / 2;
    for (int i = 0; i < 10; i++) { rd.Read(b2); outBuf.AddRange(b2); ring2.Write(ramp.AsSpan(44100 * 2 + (120 + i) * 512, 512)); }
    double exitStep = 0; for (int n = at; n < at + 1000; n++) exitStep = Math.Max(exitStep, Math.Abs(outBuf[2 * n] - outBuf[2 * n - 2]));
    Assert(!rd.Rolling && exitStep < 0.05, $"roll exit crossfades back to the live stream (max step {exitStep:0.0000})");
}
{
    var echo = new EchoFx(); echo.SetBeats(0.5, 120); echo.Send = 1;
    var src = Tone(500, 4410); var acc = new float[src.Length];
    for (int i = 0; i < 10; i++) echo.Process(src, acc);
    echo.Send = 0; echo.Freeze = true; var silent = new float[8820]; var o1 = new float[8820]; var o2 = new float[8820];
    for (int i = 0; i < 20; i++) { Array.Clear(o1); echo.Process(silent, o1); }
    for (int i = 0; i < 20; i++) { Array.Clear(o2); echo.Process(silent, o2); }
    Assert(Rms(o2) > 0.01 && Math.Abs(Rms(o2) - Rms(o1)) < Rms(o1) * 0.05, "echo freeze holds the tail without decay");
    echo.Freeze = false; for (int i = 0; i < 200; i++) { Array.Clear(o1); echo.Process(silent, o1); }
    Assert(Rms(o1) < 1e-3, "unfrozen tail rings out to silence");
    var post = new EchoFx(); post.Send = 1; var o3 = new float[8820]; post.Process(src.AsSpan(0, 8820), o3, 0f); post.Process(silent, o3, 0f);
    Assert(Rms(o3) == 0, "post-fader send: a cut deck feeds no new echo");
}
{
    var rz = new RiserFx { Amount = 1, Build = 0.5f }; rz.Sync(120, 0); var o = new float[44100];
    for (int i = 0; i < 4; i++) { Array.Clear(o); rz.Process(o); }
    Assert(Rms(o) > 0.01, "riser makes noise while held");
    rz.StopAfter(2000); var o4 = new float[2 * 1900]; rz.Process(o4); Assert(Rms(o4) > 0.01, "riser keeps going until the quantized stop");
    for (int i = 0; i < 4; i++) { Array.Clear(o); rz.Process(o); }
    Assert(Rms(o) < 1e-4 && !rz.Active, "riser stops at the beat and goes idle");
}

// ---------------- v1.2 right-stick FX: parameter curves ----------------
{
    Assert(FxCurves.Amount(0.19, 0) == 0 && FxCurves.Amount(0, -0.2) == 0, "FX amount: radial deadzone 0.2 is exactly zero");
    Assert(FxCurves.Amount(0.2001, 0) < 0.001, "FX amount: no jump leaving the deadzone");
    double prev = 0; bool mono = true; for (int k = 20; k <= 100; k++) { double a = FxCurves.Amount(k / 100.0, 0); if (a < prev - 1e-12) mono = false; prev = a; }
    Assert(mono && Math.Abs(FxCurves.Amount(1, 0) - 1) < 1e-9, "FX amount: monotonic, reaches 1 at full tilt");
    Assert(Math.Abs(FxCurves.Amount(0.6, 0.0) - FxCurves.Amount(0, 0.6)) < 1e-12 && FxCurves.Amount(0.8, 0.8) == 1, "FX amount: radial (direction independent), clamps at the rim");
    Assert(FxCurves.Amount(0.6, 0) < 0.6, "FX amount: soft curve is gentler than linear at low tilt");
    Assert(FxCurves.Amount(double.NaN, 0) == 0 && FxCurves.Param(double.NaN) == .5, "FX curves: NaN input is safe");
    Assert(FxCurves.Param(-1) == 0 && FxCurves.Param(0) == .5 && FxCurves.Param(1) == 1, "FX param: stick -1..1 -> 0..1");
    Assert(Math.Abs(FxCurves.LfoBeats(0) - 8) < 1e-9 && Math.Abs(FxCurves.LfoBeats(1) - 1) < 1e-9, "flanger/phaser LFO: 8 beats .. 1 beat");
    Assert(FxCurves.FlangerSweep(1).CentreMs + FxCurves.FlangerSweep(1).SweepMs * .5 < 8, "flanger delay stays inside the 8 ms line");
    Assert(FxCurves.PhaserHz(0, 0) == 250 && FxCurves.PhaserHz(1, 1) > FxCurves.PhaserHz(1, 0), "phaser sweep grows with depth");
    Assert(FxCurves.CrushDivisor(0) == 1 && FxCurves.CrushDivisor(1) == 32 && FxCurves.CrushBits(0) == 16 && FxCurves.CrushBits(1) == 3, "crush: divisor 1..32, bits 16..3");
    Assert(FxCurves.GateDivision(.2) == .5 && FxCurves.GateDivision(.8) == .25, "gate: X picks 1/8 or 1/16");
    var (rv, ec) = FxCurves.SpaceMix(1); var (rv0, ec0) = FxCurves.SpaceMix(0);
    Assert(rv > .999 && ec < .001 && ec0 > .999 && rv0 < .001, "space pad: Y up = reverb, Y down = delay");
    var (rm, em) = FxCurves.SpaceMix(.5); Assert(Math.Abs(rm * rm + em * em - 1) < 1e-9, "space pad: equal-power morph");
    Assert(FxCurves.BendSpeed(.5) == .5 && FxCurves.BendSpeed(1) == 0 && FxCurves.BendSpeed(0) == 1, "time bend: half pressure = half speed, full = stop");
    Assert(FxCurves.BrakeSeconds(1) < FxCurves.BrakeSeconds(0), "tape stop: harder press brakes faster");
    Assert(FxCurves.BendGain(1) == 1 && FxCurves.BendGain(0) == 0 && FxCurves.BendGain(.5) == 1, "time bend: gain only fades near a full stop");
    Assert(FxCurves.RollBeats(0) == 1 && FxCurves.RollBeats(.3) == .5 && FxCurves.RollBeats(.6) == .25 && FxCurves.RollBeats(1) == .125, "beat roll depth 1/4 .. 1/32 by pressure");
    Assert(FxCurves.Next(PadFx.Echo) == PadFx.Space && FxCurves.Name(PadFx.Crush) == "CRUSH", "R3 cycles through every effect and wraps");
    Assert(FxCurves.PadFrames(120) == 22050 && FxCurves.PadFrames(60) == 44100, "pad capture is exactly one beat");
}
// ---------------- gate timing ----------------
{
    Assert(FxCurves.GateOpen(0, .5, .5) == 1 && FxCurves.GateOpen(.24, .5, .5) == 1 && FxCurves.GateOpen(.26, .5, .5) == 0 && FxCurves.GateOpen(.5, .5, .5) == 1, "gate 1/8: open first half of each half-beat");
    Assert(FxCurves.GateOpen(.1, .25, .5) == 1 && FxCurves.GateOpen(.13, .25, .5) == 0 && FxCurves.GateOpen(.25, .25, .5) == 1, "gate 1/16: steps every quarter beat");
    Assert(Math.Abs(FxCurves.GateStepFrames(120, .5) - 11025) < 1e-6 && Math.Abs(FxCurves.GateStepFrames(120, .25) - 5512.5) < 1e-6, "gate step length at 120 BPM: 1/8 = 11025, 1/16 = 5512.5 frames");
    Assert(FxCurves.FramesToGrid(0, 120) == 0 && FxCurves.FramesToGrid(.1, 120) == (int)Math.Round(.15 * 22050) && FxCurves.FramesToGrid(.5, 120) == 0, "frames to the next 1/16 grid line");
    var gfx = new PerformanceFx(); gfx.State = new(PadFx.Gate, 1, 0.8, 0.5, 120, 0, false, 1);
    for (int i = 0; i < 4; i++) { var blk = new float[8820 * 2]; Array.Fill(blk, .5f); gfx.Process(blk, new float[blk.Length], 1); }
    var gin = new float[44100 * 2]; Array.Fill(gin, .5f); var gsend = new float[gin.Length];
    gfx.Process(gin, gsend, 1);
    var env = new double[44100]; for (int i = 0; i < env.Length; i++) env[i] = gin[2 * i] / .5;
    double mn = env.Min(), mx = env.Max(); double maxStep = 0; for (int i = 1; i < env.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(env[i] - env[i - 1]));
    var fallings = new List<int>(); for (int i = 1; i < env.Length; i++) if (env[i - 1] >= .5 && env[i] < .5) fallings.Add(i);
    double spacing = fallings.Count > 3 ? (fallings[^1] - fallings[1]) / (double)(fallings.Count - 2) : 0;
    Assert(mx > .95 && mn < .05, $"rendered gate opens and closes fully (min {mn:0.00}, max {mx:0.00})");
    Assert(maxStep < .12, $"gate edges are ramped, not hard (max step {maxStep:0.000})");
    Assert(Math.Abs(spacing - 5512.5) < 12, $"rendered 1/16 gate period is 5512.5 frames, got {spacing:0.0}");
}
// ---------------- inserts and reverb ----------------
{
    static float[] Music(int frames, double hz = 220) { var v = new float[frames * 2]; for (int i = 0; i < frames; i++) { double t = i / 44100.0; v[2 * i] = v[2 * i + 1] = (float)(.3 * Math.Sin(2 * Math.PI * hz * t) + .15 * Math.Sin(2 * Math.PI * hz * 2.76 * t)); } return v; }
    static double MaxStep(float[] x) { double m = 0; for (int i = 2; i < x.Length; i += 2) m = Math.Max(m, Math.Abs(x[i] - x[i - 2])); return m; }
    foreach (var fx in new[] { PadFx.Flanger, PadFx.Phaser, PadFx.Crush, PadFx.Gate, PadFx.Reverb, PadFx.Space })
    {
        var p = new PerformanceFx(); var src = Music(44100 * 2); var dry = (float[])src.Clone(); var send = new float[src.Length];
        p.State = new(fx, 1, .6, .6, 120, 0, false);
        for (int off = 0; off < src.Length; off += 1024) { p.State = p.State with { Phase = off / 2 / 44100.0 * 2 % 1 }; p.Process(src.AsSpan(off, Math.Min(1024, src.Length - off)), send.AsSpan(off, Math.Min(1024, src.Length - off)), 1); }
        double diff = 0, sendRms = Rms(send.AsSpan(44100).ToArray()); for (int i = 44100; i < src.Length; i++) diff += Math.Abs(src[i] - dry[i]);
        bool finite = src.All(float.IsFinite) && send.All(float.IsFinite);
        Assert(finite && (diff / (src.Length - 44100) > .005 || sendRms > .005), $"{fx}: audible effect, finite output");
        Assert(src.Max(Math.Abs) < 2, $"{fx}: output stays bounded");
    }
    var rv = new PerformanceFx(); var m = Music(44100); var snd = new float[m.Length]; rv.State = new(PadFx.Reverb, 1, .6, .5, 120, 0, false);
    for (int off = 0; off < m.Length; off += 1024) rv.Process(m.AsSpan(off, Math.Min(1024, m.Length - off)), snd.AsSpan(off, Math.Min(1024, m.Length - off)), 1);
    double wetRms = Rms(snd.AsSpan(22050).ToArray());
    rv.State = new(PadFx.Reverb, 0, .6, .5, 120, 0, false);
    var quiet = new float[4096]; var tail1 = new float[4096]; for (int i = 0; i < 12; i++) { Array.Clear(tail1); rv.Process(quiet, tail1, 1); }
    double early = Rms(tail1); var tailLater = new float[4096]; for (int i = 0; i < 150; i++) { Array.Clear(tailLater); rv.Process(quiet, tailLater, 1); }
    Assert(wetRms > .01 && wetRms < .8, $"reverb level is musical (wet rms {wetRms:0.000})");
    Assert(early > 1e-4 && Rms(tailLater) < early * .1, $"reverb tail rings out naturally after release ({early:0.0000} -> {Rms(tailLater):0.00000})");
    var fz = new PerformanceFx(); var fm = Music(22050); var fs = new float[fm.Length]; fz.State = new(PadFx.Reverb, 1, .6, .5, 120, 0, false);
    for (int off = 0; off < fm.Length; off += 1024) fz.Process(fm.AsSpan(off, Math.Min(1024, fm.Length - off)), fs.AsSpan(off, Math.Min(1024, fm.Length - off)), 1);
    fz.State = new(PadFx.Reverb, 0, .6, .5, 120, 0, true);
    var z1 = new float[4096]; for (int i = 0; i < 20; i++) { Array.Clear(z1); fz.Process(new float[4096], z1, 1); }
    double r1 = Rms(z1); for (int i = 0; i < 60; i++) { Array.Clear(z1); fz.Process(new float[4096], z1, 1); }
    Assert(r1 > 1e-3 && Rms(z1) > r1 * .5, "reverb freeze holds the tail");
    var sw = new PerformanceFx(); var sm = Music(44100 * 2); var ss = new float[sm.Length]; double dryStep = MaxStep(sm);
    for (int off = 0, k = 0; off < sm.Length; off += 512, k++)
    {
        var fxk = (PadFx)(k / 20 % 7); sw.State = new(fxk, 1, .5, .5, 120, 0, false);
        sw.Process(sm.AsSpan(off, Math.Min(512, sm.Length - off)), ss.AsSpan(off, Math.Min(512, sm.Length - off)), 1);
    }
    Assert(sm.All(float.IsFinite) && MaxStep(sm) < dryStep * 3 + .1, $"effect switching is click-free (max step {MaxStep(sm):0.000} vs dry {dryStep:0.000})");
    var rp = new PerformanceFx(); var rmm = Music(44100 * 2); var rsend = new float[rmm.Length]; double rdry = MaxStep(rmm);
    for (int off = 0, k = 0; off < rmm.Length; off += 512, k++) { rp.State = new(PadFx.Crush, k > 40 && k < 80 ? 1 : 0, .9, .9, 120, 0, false, 60); rp.Process(rmm.AsSpan(off, Math.Min(512, rmm.Length - off)), rsend.AsSpan(off, Math.Min(512, rmm.Length - off)), 1); }
    double inWin = MaxStep(rmm.AsSpan(41 * 512, 2048).ToArray()), fullWin = MaxStep(rmm.AsSpan(55 * 512, 8192).ToArray()), outWin = MaxStep(rmm.AsSpan(80 * 512, 2048).ToArray());
    Assert(inWin <= fullWin + .02 && outWin <= fullWin + .02, $"60 ms amount smoothing: crush in/out never steps harder than the effect itself (in {inWin:0.000}, out {outWin:0.000}, full {fullWin:0.000})");
}
// ---------------- slip engine: time bend, reverse, pads ----------------
{
    static float[] Wave(int frames, int start = 0) { var v = new float[frames * 2]; for (int i = 0; i < frames; i++) v[2 * i] = v[2 * i + 1] = (float)(.5 * Math.Sin(2 * Math.PI * 440 * (i + start) / 44100.0)); return v; }
    static double Step(float[] arr) { double s = 0; for (int i = 2; i < arr.Length; i += 2) s = Math.Max(s, Math.Abs(arr[i] - arr[i - 2])); return s; }
    var sp = new PerformanceSampler(); sp.SetTempo(120, 0); int pos = 0;
    float[] Run(int frames) { var o = new float[frames * 2]; for (int off = 0; off < frames; off += 256) { var b = Wave(Math.Min(256, frames - off), pos); pos += b.Length / 2; sp.SetTempo(120, pos / 44100.0 * 2 % 1); sp.Process(b); Array.Copy(b, 0, o, off * 2, b.Length); } return o; }
    var idle = Run(2048); var refSig = Wave(2048); double idleDiff = 0; for (int i = 0; i < idle.Length; i++) idleDiff = Math.Max(idleDiff, Math.Abs(idle[i] - refSig[i]));
    Assert(idleDiff < 1e-6 && sp.State == 0, "slip engine is transparent when idle");
    sp.Bend = 1; var stop = Run(44100 / 2);
    Assert(Rms(stop.AsSpan(stop.Length - 2000).ToArray()) < .01, "full LT press stops the tape");
    Assert(Step(stop) < .15, $"tape stop is click-free (max step {Step(stop):0.000})");
    sp.Bend = 0; var up = Run(44100 * 2);
    Assert(sp.State == 0 && Rms(up.AsSpan(up.Length - 4000).ToArray()) > .2, "release spins up and re-locks to the live stream on a beat");
    Assert(Step(up) < .35, $"spin-up and re-lock are click-free (max step {Step(up):0.000})");
    sp.Bend = .5f; var half = Run(8820); sp.Bend = 0; Run(44100 * 2);
    Assert(Rms(half.AsSpan(half.Length - 2000).ToArray()) > .15, "half press keeps slow audible tape");
    sp.Reverse = true; var rev = Run(11025); sp.Reverse = false; var after = Run(44100 * 2);
    Assert(sp.State == 0 && Math.Max(Step(rev), Step(after)) < .4, $"reverse hold then release re-locks, no click (max step {Math.Max(Step(rev), Step(after)):0.000})");
    sp.SpinBack(); var spin = Run(44100 * 2); Run(44100);
    Assert(sp.State == 0 && Rms(spin) > .02, "spin-back plays and returns to live");
    Assert(PerformanceSampler.SpinSpeed(0.0) < -2 && PerformanceSampler.SpinSpeed(.3) < 0 && PerformanceSampler.SpinSpeed(.6) == 1, "spin-back profile: whips backwards, decays, hands over");
    var pp = new PerformanceSampler(); pp.SetTempo(120, 0); pos = 0;
    float[] RunP(int frames) { var o = new float[frames * 2]; for (int off = 0; off < frames; off += 256) { var b = Wave(Math.Min(256, frames - off), pos); pos += b.Length / 2; pp.SetTempo(120, pos / 44100.0 * 2 % 1); pp.Process(b); Array.Copy(b, 0, o, off * 2, b.Length); } return o; }
    RunP(30000); pp.Capture(1, 0, FxCurves.PadFrames(120)); RunP(512);
    Assert(pp.Length(1) == 22050 && pp.Length(0) == 0, "pad capture stores exactly one beat");
    pp.Play(1, 1000, false); var played = RunP(22050 + 3000);
    Assert(Rms(played.AsSpan(2000 * 2, 8000).ToArray()) > .1 && pp.PlayingPad == -1, "pad one-shot plays and ends");
    pp.Play(1, 0, true); RunP(22050 * 3); Assert(pp.PlayingPad == 1, "held pad gate-repeats (loops)");
    pp.StopPad(); RunP(2000); Assert(pp.PlayingPad == -1, "releasing the pad stops the repeat");
    pp.Play(1, 0, true); var o1 = RunP(22050 * 2); pp.StopPad(); var o2 = RunP(2000);
    Assert(Math.Max(Step(o1), Step(o2)) < .2, $"pad repeat and release have no clicks (max step {Math.Max(Step(o1), Step(o2)):0.000})");
}
// ---------------- output safety ----------------
{
    var devs = new List<MixDock.Platform.OutputEndpoint> { new("c", "CABLE Input (VB-Audio Virtual Cable)"), new("s", "Steam Streaming Speakers"), new("h", "Headphones (USB)"), new("d", "Speakers (Realtek)") };
    Assert(MixDock.Platform.OutputPolicy.IsVirtual("CABLE Output (VB-Audio Virtual Cable)") && MixDock.Platform.OutputPolicy.IsVirtual("Steam Streaming Microphone") && !MixDock.Platform.OutputPolicy.IsVirtual("Speakers (Realtek)"), "virtual device names are recognised");
    Assert(MixDock.Platform.OutputPolicy.Pick(devs, "c", "h")!.Id == "h", "saved CABLE output falls back to the real Windows default");
    Assert(MixDock.Platform.OutputPolicy.Pick(devs, "c", "c")!.Id == "h", "virtual default: first real device");
    Assert(MixDock.Platform.OutputPolicy.Pick(devs.Where(d => d.Id != "h").ToList(), "s", "s")!.Id == "d", "without headphones: first real device, never Steam Streaming");
    Assert(MixDock.Platform.OutputPolicy.Pick(devs.Where(d => d.Id != "h").ToList(), "d", "d")!.Id == "d", "a valid saved real output is kept");
    Assert(MixDock.Platform.OutputPolicy.Pick(devs, "d", "d", "d")!.Id == "h" && MixDock.Platform.OutputPolicy.Pick(devs.Where(d => d.Id != "h").ToList(), "d", "d", "d") == null, "output never equals the redirect sink");
    Assert(MixDock.Platform.OutputPolicy.Pick(new List<MixDock.Platform.OutputEndpoint> { devs[0], devs[1] }, null, "c") == null, "only virtual devices: no output chosen");
}

Console.WriteLine($"ALL {count} TESTS PASSED");
