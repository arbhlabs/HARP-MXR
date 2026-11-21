using MixDock.Audio;

namespace MixDock { public static class Log { public static void Write(string s) { } } }

/// <summary>Procedural 4/4 track addressed by absolute stream frame: kick on every beat (louder on the downbeat), hat on the off-beat,
/// optional swing, kick breakdowns and humanisation. Beat n is at Offset + n * period (fractional, sample-accurate rendering).</summary>
public sealed class SynthTrack
{
    public double Bpm, Offset, Swing, Jitter; public float Accent = 1f, Plain = .7f;
    public (int From, int To)[] Breakdowns = Array.Empty<(int, int)>();   // bar ranges without kick
    public int Seed = 7;
    public double Period => 60.0 / Bpm * 44100;
    public SynthTrack(double bpm, double offsetFrames) { Bpm = bpm; Offset = offsetFrames; }
    private double Jit(long n, int k) { if (Jitter <= 0) return 0; var r = new Random(unchecked((int)(n * 7919 + k * 104729 + Seed))); return (r.NextDouble() * 2 - 1) * Jitter * 44.1; }
    public double BeatTime(long n) => Offset + n * Period;
    public bool KickOn(long n) { long bar = n >= 0 ? n / 4 : (n - 3) / 4; foreach (var (a, b) in Breakdowns) if (bar >= a && bar < b) return false; return true; }
    public void Fill(Span<float> dst, long start)
    {
        dst.Clear(); int frames = dst.Length / 2; double P = Period;
        long n0 = (long)Math.Floor((start - Offset - 3000) / P) - 1, n1 = (long)Math.Ceiling((start + frames - Offset) / P) + 1;
        for (long n = n0; n <= n1; n++)
        {
            double t = BeatTime(n) + Jit(n, 0);
            if (KickOn(n))
            {
                float amp = (n % 4 + 4) % 4 == 0 ? Accent : Plain;
                int i0 = (int)Math.Max(start, Math.Ceiling(t)), i1 = (int)Math.Min(start + frames, Math.Ceiling(t) + 3000);
                for (int i = i0; i < i1; i++)
                {
                    double x = i - t; float v = (float)(0.7 * amp * Math.Exp(-x / 700.0) * Math.Sin(2 * Math.PI * 70 * x / 44100));
                    dst[2 * (int)(i - start)] += v; dst[2 * (int)(i - start) + 1] += v;
                }
            }
            double h = BeatTime(n) + P / 2 * (1 + Swing) + Jit(n, 1);
            var rnd = new Random(unchecked((int)(n * 31 + 5)));
            int h0 = (int)Math.Max(start, Math.Ceiling(h)), h1 = (int)Math.Min(start + frames, Math.Ceiling(h) + 900);
            for (int i = h0; i < h1; i++)
            {
                var r2 = new Random(unchecked((int)(i * 2654435761u ^ (uint)n)));
                float v = (float)(0.15 * Math.Exp(-(i - h) / 200.0) * (r2.NextDouble() * 2 - 1));
                dst[2 * (int)(i - start)] += v; dst[2 * (int)(i - start) + 1] += v;
            }
        }
    }
}

/// <summary>A simulated live deck: the same ring + history + detector + reader the engine builds for a captured app.</summary>
public sealed class SimDeck : ISyncDeck
{
    public readonly SampleRing Ring = new(44100 * 4);
    public readonly SampleHistory History = new();
    public readonly BpmDetector Detector = new() { Synchronous = true };
    public readonly DeckReader Reader;
    public SynthTrack Track;
    public long Fed;
    public bool Playing = true;
    public BpmDetector Bpm => Detector;
    public bool IsFile { get; set; }
    public bool Active => Playing;
    public SimDeck(SynthTrack t)
    {
        Track = t;
        Reader = new DeckReader(Ring, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { History = History };
    }
    private float[] _blk = new float[0];
    public void Feed(int frames)
    {
        if (_blk.Length < frames * 2) _blk = new float[frames * 2];
        var b = _blk.AsSpan(0, frames * 2);
        Track.Fill(b, Fed);
        Detector.Feed(b);
        lock (History.Gate) { History.Write(b); Ring.Write(b); }
        Fed += frames;
    }
}

/// <summary>Beat-crossing recorder: maps output time to the deck's NOMINAL playhead and lists the output time of every true track beat.</summary>
public sealed class BeatLog
{
    public readonly List<(long n, double t)> Beats = new();
    private double _pos0 = double.NaN; private long _o0;
    public void BlockStart(SimDeck d, long outFrame) { _pos0 = d.Reader.PlayheadFrame; _o0 = outFrame; }
    private double _prevPos = double.NaN; private long _prevO;
    public void BlockEnd(SimDeck d, long outFrame, int frames, double expectRate)
    {
        double p1 = d.Reader.PlayheadFrame;
        if (!double.IsNaN(_pos0) && !double.IsNaN(p1) && Math.Abs(p1 - _pos0 - expectRate * frames) < 120)
        {
            var tr = d.Track; double P = tr.Period;
            long n0 = (long)Math.Ceiling((_pos0 - tr.Offset) / P), n1 = (long)Math.Floor((p1 - tr.Offset) / P);
            for (long n = n0; n <= n1; n++)
            {
                double b = tr.BeatTime(n); if (b < _pos0 || b >= p1) continue;
                Beats.Add((n, _o0 + (b - _pos0) / (p1 - _pos0) * frames));
            }
        }
    }
}
