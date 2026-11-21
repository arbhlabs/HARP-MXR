using MixDock.Audio;

// `dotnet run -- --wav out.wav` writes the kick + tone test track used for the screenshot, then exits.
int wi = Array.IndexOf(args, "--wav");
if (wi >= 0) { WriteWav(args[wi + 1], Pattern(128, 0.25, 40)); Console.WriteLine("wrote " + args[wi + 1]); return 0; }

var count = 0;
void Assert(bool ok, string test)
{
    if (!ok) throw new Exception("FAIL: " + test);
    Console.WriteLine("PASS " + test); count++;
}
static float[] Tone(double hz, double amp, double seconds)
{
    int n = (int)(44100 * seconds); var v = new float[n * 2];
    for (int i = 0; i < n; i++) v[i * 2] = v[i * 2 + 1] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / 44100));
    return v;
}
static (float l, float m, float h) MeanBands(WaveColumn[] c, int from)
{
    float l = 0, m = 0, h = 0; int k = 0;
    for (int i = from; i < c.Length; i++) { l += c[i].Low; m += c[i].Mid; h += c[i].High; k++; }
    return (l / k, m / k, h / k);
}

// ---- peak columns ----
var cols = WaveformAnalyzer.AnalyzeAll(Tone(1000, 0.5, 1.0));
Assert(cols.Length == 100, $"1 s of audio = 100 columns of 10 ms (got {cols.Length})");
Assert(cols.Skip(5).All(c => Math.Abs(c.Peak - 0.5f) < 0.02f), "column peak = tone amplitude 0.5");
Assert(WaveformAnalyzer.AnalyzeAll(new float[44100 * 2]).All(c => c.Peak == 0 && c.Low == 0 && c.Mid == 0 && c.High == 0), "silence -> empty columns");
var loud = new float[44100 * 2]; loud[44100] = 0.9f; loud[44101] = 0.9f;   // one click at 0.5 s
var click = WaveformAnalyzer.AnalyzeAll(loud);
Assert(click[50].Peak > 0.85f && click[49].Peak == 0 && click.Where((_, i) => i < 49).All(c => c.Peak == 0), "click lands in column 50 (0.50 s), earlier columns stay silent");
{
    var sig = Tone(220, 0.4, 3.0); var live = new WaveformAnalyzer(10);
    for (int i = 0; i < sig.Length; i += 256) live.Feed(sig.AsSpan(i, Math.Min(256, sig.Length - i)));
    var whole = WaveformAnalyzer.AnalyzeAll(sig);
    Assert(live.Count == whole.Length && Enumerable.Range(0, whole.Length).All(i => live.TryGet(i, out var c) && c == whole[i]),
        "chunked live feed (capture packets) = whole-buffer analysis");
    var small = new WaveformAnalyzer(1);   // 100-column ring
    var s5 = Tone(220, 0.4, 5.0); small.Feed(s5);
    Assert(small.Count == 500 && !small.TryGet(10, out _) && small.TryGet(499, out _), "live ring drops old columns, keeps the newest");
}

// ---- band split ----
var lowB = MeanBands(WaveformAnalyzer.AnalyzeAll(Tone(60, 0.5, 1)), 20);
var midB = MeanBands(WaveformAnalyzer.AnalyzeAll(Tone(1000, 0.5, 1)), 20);
var hiB = MeanBands(WaveformAnalyzer.AnalyzeAll(Tone(9000, 0.5, 1)), 20);
Assert(lowB.l > 2 * lowB.m && lowB.l > 4 * lowB.h, $"60 Hz -> low band (L {lowB.l:F2} M {lowB.m:F2} H {lowB.h:F2})");
Assert(midB.m > midB.l * 2 && midB.m > midB.h, $"1 kHz -> mid band (L {midB.l:F2} M {midB.m:F2} H {midB.h:F2})");
Assert(hiB.h > hiB.l * 4 && hiB.h > hiB.m, $"9 kHz -> high band (L {hiB.l:F2} M {hiB.m:F2} H {hiB.h:F2})");

// ---- beat grid ----
foreach (var (bpm, first) in new[] { (120.0, 0.25), (128.0, 0.25), (174.0, 0.10), (90.0, 0.40) })
{
    var track = Pattern(bpm, first, 60);
    var grid = BeatGrid.Estimate(WaveformAnalyzer.AnalyzeAll(track));
    double period = 60 / bpm;
    Assert(Math.Abs(grid.Bpm - bpm) < 0.15, $"grid tempo {bpm} BPM (got {grid.Bpm:F3})");
    double off = ((grid.FirstBeatSec - first) % period + period) % period; if (off > period / 2) off -= period;
    Assert(Math.Abs(off) < 0.015, $"grid first beat {first:F2} s at {bpm} BPM (got {grid.FirstBeatSec:F3}, off {off * 1000:F1} ms)");
    // no drift: the grid beat nearest 55 s is within 20 ms of the true kick
    double trueKick = first + Math.Round((55 - first) / period) * period;
    double gridBeat = grid.Beats(55 - period, 55 + period).Select(b => b.t).MinBy(t => Math.Abs(t - trueKick));
    Assert(Math.Abs(gridBeat - trueKick) < 0.02, $"no drift after 55 s at {bpm} BPM ({(gridBeat - trueKick) * 1000:F1} ms)");
}
Assert(!BeatGrid.Estimate(WaveformAnalyzer.AnalyzeAll(new float[44100 * 2 * 10])).Valid, "silence -> no beat grid");
Assert(!BeatGrid.Estimate(WaveformAnalyzer.AnalyzeAll(Tone(440, 0.3, 2))).Valid, "under 4 s -> no beat grid");
{
    var g = new BeatGrid(120, 0.25);
    var beats = g.Beats(0, 2.0).ToList();
    Assert(beats.Count == 4 && beats.Select(b => b.t).SequenceEqual(new[] { 0.25, 0.75, 1.25, 1.75 }) && beats[0].k == 0,
        "Beats() lists 0.25, 0.75, 1.25, 1.75 s for 120 BPM from 0.25 s");
    Assert(g.Beats(10, 12).All(b => Math.Abs(b.t - (0.25 + b.k * 0.5)) < 1e-9) && g.Beats(10, 12).First().k == 20, "beat index counts from the first beat (bar = k % 4)");
    Assert(g.Beats(-1.2, 0).Select(b => b.k).SequenceEqual(new long[] { -2, -1 }), "grid extends before the first beat");
}
// look-ahead view: the delay line is drawn to the right of the playhead
{
    var (w0, f0) = WaveView.Layout(0);
    Assert(w0 == 8 && f0 == .5, "no look-ahead: centred playhead, 8 s window");
    var (w10, f10) = WaveView.Layout(10.12);
    Assert(w10 * (1 - f10) >= 10.12 && w10 * f10 >= 2.9, $"10 s look-ahead: {w10 * (1 - f10):0.0} s visible ahead, {w10 * f10:0.0} s behind");
    var (w30, f30) = WaveView.Layout(30);
    Assert(w30 * (1 - f30) >= 30, "30 s look-ahead fits in the window");
}
Console.WriteLine($"ALL {count} TESTS PASSED");
return 0;

// kick (pitch-dropping sine, 150 ms) on every beat + off-beat hats + a mid tone stab every bar
static float[] Pattern(double bpm, double first, double seconds)
{
    int n = (int)(44100 * seconds); var v = new float[n * 2]; double period = 60 / bpm; var rng = new Random(7);
    for (int b = 0; first + b * period < seconds; b++)
    {
        int s0 = (int)Math.Round((first + b * period) * 44100);
        double ph = 0;
        for (int i = 0; i < 6615 && s0 + i < n; i++)
        {
            double t = i / 44100.0, f = 50 + 90 * Math.Exp(-t * 40); ph += 2 * Math.PI * f / 44100;
            Add(v, s0 + i, 0.8 * Math.Sin(ph) * Math.Exp(-t * 18));
        }
        int h0 = s0 + (int)(period / 2 * 44100);
        for (int i = 0; i < 1800 && h0 + i < n; i++) Add(v, h0 + i, 0.18 * (rng.NextDouble() * 2 - 1) * Math.Exp(-i / 300.0) * (i % 2 == 0 ? 1 : -1));
        if (b % 4 == 2)
            for (int i = 0; i < (int)(period * 44100) && s0 + i < n; i++)
            { double t = i / 44100.0; Add(v, s0 + i, 0.16 * (Math.Sin(2 * Math.PI * 440 * t) + Math.Sin(2 * Math.PI * 660 * t)) * Math.Exp(-t * 3)); }
    }
    return v;
    static void Add(float[] v, int frame, double x) { v[frame * 2] += (float)x; v[frame * 2 + 1] += (float)x; }
}
static void WriteWav(string path, float[] stereo)
{
    using var w = new BinaryWriter(File.Create(path));
    int bytes = stereo.Length * 2;
    w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
    w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
    foreach (var x in stereo) w.Write((short)Math.Round(Math.Clamp(x, -1f, 1f) * 32000));
}
