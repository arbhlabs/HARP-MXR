namespace MixDock.Audio;

public enum DrumSound { Kick, Snare, Hat, Clap, Tom, Rim, OpenHat, Crash }

/// <summary>Built-in synthesised one-shots (kick, snare, hat, clap and an alternative set), rendered once to mono buffers. No samples are bundled.</summary>
public static class DrumKit
{
    private const int Sr = FxCurves.SampleRate;
    private static readonly Lazy<float[][]> Cache = new(() => Enum.GetValues<DrumSound>().Select(Render).ToArray());
    public static float[] Get(DrumSound d) => Cache.Value[(int)d];
    public static string Name(DrumSound d) => d switch
    {
        DrumSound.Kick => "KICK", DrumSound.Snare => "SNARE", DrumSound.Hat => "HAT", DrumSound.Clap => "CLAP",
        DrumSound.Tom => "TOM", DrumSound.Rim => "RIM", DrumSound.OpenHat => "OPEN HAT", _ => "CRASH"
    };
    /// <summary>Pad 0..3 to a sound; the alternative (Shift) set is tom, rim, open hat, crash.</summary>
    public static DrumSound ForPad(int pad, bool alt) => (DrumSound)(Math.Clamp(pad, 0, 3) + (alt ? 4 : 0));

    private sealed class Noise { private uint _s = 0x9E3779B9; public float Next() { _s = _s * 1664525u + 1013904223u; return (_s >> 8) / 8388608f - 1f; } }

    private static float[] Render(DrumSound d)
    {
        var rnd = new Noise();
        int n = d switch { DrumSound.Kick => (int)(Sr * .42), DrumSound.Snare => (int)(Sr * .24), DrumSound.Hat => (int)(Sr * .08), DrumSound.Clap => (int)(Sr * .26),
            DrumSound.Tom => (int)(Sr * .34), DrumSound.Rim => (int)(Sr * .05), DrumSound.OpenHat => (int)(Sr * .34), _ => (int)(Sr * .9) };
        var o = new float[n];
        double ph = 0, lp = 0, hp1 = 0, hp2 = 0;
        double[] metal = { 205.3, 304.4, 369.6, 522.7, 800.0, 540.0 };
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Sr, x = 0;
            switch (d)
            {
                case DrumSound.Kick:
                {
                    double f = 45 + 120 * Math.Exp(-t / .045);
                    ph += 2 * Math.PI * f / Sr;
                    x = Math.Sin(ph) * Math.Exp(-t / .17) + .35 * rnd.Next() * Math.Exp(-t / .004);
                    break;
                }
                case DrumSound.Snare:
                {
                    double f = 180 - 40 * Math.Min(1, t / .05); ph += 2 * Math.PI * f / Sr;
                    double nz = rnd.Next(); hp1 += (nz - hp1) * .25; double bright = nz - hp1;
                    x = .45 * Math.Sin(ph) * Math.Exp(-t / .07) + .6 * bright * Math.Exp(-t / .065);
                    break;
                }
                case DrumSound.Hat:
                case DrumSound.OpenHat:
                {
                    double s = 0; foreach (var m in metal) s += Math.Sign(Math.Sin(2 * Math.PI * m * 2 * t));
                    double nz = rnd.Next(); hp1 += (nz - hp1) * .5;
                    double bright = (nz - hp1) * .6 + s / 12.0;
                    x = bright * Math.Exp(-t / (d == DrumSound.Hat ? .018 : .09));
                    break;
                }
                case DrumSound.Clap:
                {
                    double burst = 0; foreach (var off in new[] { 0.0, .011, .022 }) { double u = t - off; if (u >= 0) burst += Math.Exp(-u / .006); }
                    double tail = t > .033 ? Math.Exp(-(t - .033) / .06) : 0;
                    double nz = rnd.Next(); lp += (nz - lp) * .45; double band = lp - hp2; hp2 += (lp - hp2) * .08;
                    x = band * 2.2 * (burst * .45 + tail * .5);
                    break;
                }
                case DrumSound.Tom:
                {
                    double f = 95 + 90 * Math.Exp(-t / .06); ph += 2 * Math.PI * f / Sr;
                    x = Math.Sin(ph) * Math.Exp(-t / .13) + .12 * rnd.Next() * Math.Exp(-t / .006);
                    break;
                }
                case DrumSound.Rim:
                {
                    x = (Math.Sin(2 * Math.PI * 1700 * t) * .5 + Math.Sin(2 * Math.PI * 480 * t) * .5) * Math.Exp(-t / .006);
                    break;
                }
                default: // crash
                {
                    double nz = rnd.Next(); hp1 += (nz - hp1) * .35;
                    x = (nz - hp1) * Math.Exp(-t / .28) * .75;
                    break;
                }
            }
            o[i] = (float)Math.Clamp(x * .8, -1, 1);
        }
        // declick the very end of every buffer
        int fade = Math.Min(n, 128);
        for (int i = 0; i < fade; i++) o[n - 1 - i] *= i / (float)fade;
        return o;
    }
}

/// <summary>
/// Chord voice: every note is a small supersaw (5 PolyBLEP saws, detuned and spread across the stereo field) through a
/// 12 dB low-pass whose cutoff opens with velocity and plucks open at the start, under a short-attack amp envelope that
/// decays to a pad-like sustain. Render-thread only; allocation-free after construction.
/// </summary>
public sealed class ChordVoiceDsp
{
    private const int Sr = FxCurves.SampleRate, MaxNotes = 8, Saws = 5;
    private static readonly double[] Detune = { -.0125, -.0055, 0, .0060, .0130 };   // fraction of a semitone-ish ratio (about +-14 cents)
    private static readonly float[] Pan = { -.8f, -.4f, 0, .4f, .8f };
    private readonly double[] _inc = new double[MaxNotes * Saws], _phase = new double[MaxNotes * Saws];
    private int _notes;
    private double _t, _lp1L, _lp1R, _lp2L, _lp2R, _a;
    private double _velocity;
    private int _tick;

    public void Start(ReadOnlySpan<int> midiNotes, double velocity01)
    {
        _notes = Math.Min(midiNotes.Length, MaxNotes); _velocity = Math.Clamp(velocity01, 0, 1);
        _t = 0; _lp1L = _lp1R = _lp2L = _lp2R = 0; _tick = 0;
        for (int n = 0; n < _notes; n++)
        {
            double hz = 440.0 * Math.Pow(2, (midiNotes[n] - 69) / 12.0);
            for (int s = 0; s < Saws; s++)
            {
                int k = n * Saws + s;
                _inc[k] = hz * (1 + Detune[s]) / Sr;
                _phase[k] = (n * 0.173 + s * 0.311) % 1.0;   // fixed spread so the voice never starts from a hard sum of aligned edges
            }
        }
    }

    /// <summary>Filter cutoff in Hz at <paramref name="t"/> seconds into the note: darker when soft, brighter when hard, plus a pluck sweep.</summary>
    public static double Cutoff(double velocity01, double t)
        => 900 + 7000 * Math.Clamp(velocity01, 0, 1) + 3500 * Math.Clamp(velocity01, 0, 1) * Math.Exp(-t / .16);
    /// <summary>Amplitude body: decays from 1 to a .72 pad sustain.</summary>
    public static double Body(double t) => .72 + .28 * Math.Exp(-t / .35);

    /// <summary>Returns the next stereo sample (without the voice's attack/release envelope or velocity gain).</summary>
    public void Next(out float l, out float r)
    {
        if ((_tick++ & 31) == 0) _a = 1 - Math.Exp(-2 * Math.PI * Math.Min(Cutoff(_velocity, _t), 16000) / Sr);
        double sl = 0, sr = 0;
        for (int n = 0; n < _notes; n++)
            for (int s = 0; s < Saws; s++)
            {
                int k = n * Saws + s;
                double p = _phase[k], dt = _inc[k];
                double v = 2 * p - 1;
                // PolyBLEP: remove the aliasing step at the wrap
                if (p < dt) { double u = p / dt; v -= u + u - u * u - 1; }
                else if (p > 1 - dt) { double u = (p - 1) / dt; v -= u * u + u + u + 1; }
                p += dt; if (p >= 1) p -= 1; _phase[k] = p;
                float pan = Pan[s];
                sl += v * (1 - pan) * .5; sr += v * (1 + pan) * .5;
            }
        double norm = .55 / Math.Max(1, _notes * 2.2);
        sl *= norm; sr *= norm;
        _lp1L += (sl - _lp1L) * _a; _lp1R += (sr - _lp1R) * _a;
        _lp2L += (_lp1L - _lp2L) * _a; _lp2R += (_lp1R - _lp2R) * _a;
        double body = Body(_t); _t += 1.0 / Sr;
        l = (float)(_lp2L * body); r = (float)(_lp2R * body);
    }
}
