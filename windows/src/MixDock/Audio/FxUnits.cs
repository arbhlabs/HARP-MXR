namespace MixDock.Audio;

/// <summary>Per-chunk context the rack hands to every unit: tempo clock and the shared dry history (already holding
/// this chunk). Render thread only.</summary>
public sealed class FxContext
{
    public float[] Hist = Array.Empty<float>();
    public int HistFrames;
    public long Abs0;
    public double Beat0, BeatInc, Bpm = 120, FramesPerBeat = 22050;
    public double Beat(int i) => Beat0 + i * BeatInc;
    public float H(long abs, int ch) => Hist[(int)(((abs % HistFrames) + HistFrames) % HistFrames) * 2 + ch];
    public float HFrac(double pos, int ch)
    {
        long i0 = (long)Math.Floor(pos); float f = (float)(pos - i0);
        float a = H(i0, ch), b = H(i0 + 1, ch);
        return a + (b - a) * f;
    }
}

/// <summary>One effect. <c>io</c> is interleaved stereo (n frames); drive (stick distance) and mix (direction share)
/// are smoothed per sample by the rack. Units with a tail keep ringing while drive and mix are zero.</summary>
public abstract class FxUnit
{
    public virtual bool TailActive => false;
    public abstract void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext ctx);
    /// <summary>The unit was idle and runs from this chunk on. Delay-line units fill their line with the recent dry audio
    /// here, so the delayed path never starts from silence (that onset would be a click).</summary>
    public virtual void Engage(FxContext ctx) { }
    /// <summary>Drive and mix reached zero and no tail is left: forget gesture state (capture points, phases).</summary>
    public virtual void Disengage() { }
    /// <summary>Panic / NaN recovery: clear everything.</summary>
    public virtual void Reset() => Disengage();

    public static FxUnit Create(StickFx fx) => fx switch
    {
        StickFx.BeatRepeat => new BeatRepeatUnit(), StickFx.Reverse => new ReverseUnit(), StickFx.Brake => new BrakeUnit(),
        StickFx.Freeze => new FreezeUnit(), StickFx.PitchDown => new PitchDownUnit(), StickFx.Cut => new CutUnit(),
        StickFx.Transformer => new TransformerUnit(), StickFx.TranceGate => new TranceGateUnit(), StickFx.Pump => new PumpUnit(),
        StickFx.AutoPan => new AutoPanUnit(), StickFx.LowPass => new SweepUnit(false), StickFx.HighPass => new SweepUnit(true),
        StickFx.Comb => new CombUnit(), StickFx.Vowel => new VowelUnit(), StickFx.Wobble => new WobbleUnit(),
        StickFx.Drive => new DriveUnit(), StickFx.Wavefold => new WavefoldUnit(), StickFx.BitCrush => new CrushUnit(),
        StickFx.RingMod => new RingModUnit(), StickFx.Phaser => new PhaserUnit(), StickFx.Flanger => new FlangerUnit(),
        StickFx.Chorus => new ChorusUnit(), StickFx.TapeEcho => new TapeEchoUnit(), StickFx.PingPong => new PingPongUnit(),
        StickFx.Hall => new HallUnit(), StickFx.Shimmer => new ShimmerUnit(), StickFx.GatedVerb => new GatedVerbUnit(),
        StickFx.NoiseRiser => new NoiseRiserUnit(), StickFx.SubDrop => new SubDropUnit(),
        _ => throw new ArgumentOutOfRangeException(nameof(fx))
    };

    /// <summary>Equal-power blend of two uncorrelated signals keeps the loudness, but where they happen to line up the
    /// peaks add to +3 dB at the half-way point. This trim (1 at the ends, 0.8 half-way) keeps that worst case near +1 dB
    /// while the loudness dips at most 1.9 dB mid-blend.</summary>
    protected static float EqualPowerTrim(float angle) => 1f / (1f + .25f * MathF.Sin(2 * angle));

    /// <summary>Peak envelope helper (instant attack, ~300 ms release) used for level-matched waveshapers.</summary>
    protected static float Env(ref float env, float l, float r)
    {
        float p = Math.Max(Math.Abs(l), Math.Abs(r));
        env = p > env ? p : env * .99992f;
        return Math.Max(env, 1e-5f);
    }
}

// ======================================================================== time / buffer

/// <summary>Beat repeat: grabs the slice that starts on the last 1/16 grid line and repeats it. Stick distance picks
/// the slice length (1/4 … 1/64 note), changed only at a slice wrap so it rolls on the grid. Every wrap crossfades the
/// slice head with what would have followed it (3 ms), so the loop seam never clicks. Param &gt; 0 fixes the length (beats).</summary>
internal sealed class BeatRepeatUnit : FxUnit
{
    private const int Xf = 128, MaxFrames = Dsp.Sr * 2;
    private readonly float[] _slice = new float[(MaxFrames + Xf) * 2];
    private bool _on, _wrapped, _copied;
    private long _anchor, _copyAt;
    private int _len, _prevLen, _pos, _captured;
    private readonly DcBlock[] _dc = { new(), new() };
    /// <summary>Repeat length in beats from stick distance: 1/4, 1/8, 1/16, 1/32, 1/64 note.</summary>
    public static double SizeBeats(float drive) => drive < .35f ? 1 : drive < .55f ? .5 : drive < .72f ? .25 : drive < .88f ? .125 : .0625;

    public override void Disengage() { _on = false; _wrapped = false; _copied = false; _pos = 0; _dc[0].Clear(); _dc[1].Clear(); }
    private float Src(FxContext c, int k, int ch) => _copied ? _slice[Math.Min(k, _captured + Xf - 1) * 2 + ch] : c.H(_anchor + k, ch);

    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float m = param > 0 ? mix[i] : mix[i] * Dsp.Ramp(drive[i], .15f);
            long abs = c.Abs0 + i;
            double beats = param > 0 ? param : SizeBeats(drive[i]);
            int want = (int)Math.Clamp(Math.Round(beats * c.FramesPerBeat), 256, MaxFrames);
            if (!_on)
            {
                if (m <= 0) continue;
                double b = c.Beat(i), since = (b - Math.Floor(b / .25) * .25) / Math.Max(1e-9, c.BeatInc);
                _anchor = abs - (long)Math.Min(since, c.HistFrames / 2);
                _pos = (int)(abs - _anchor); _len = Math.Max(want, _pos + 1); _on = true; _wrapped = _copied = false; _captured = 0;
            }
            if (!_wrapped && _pos >= _len) { _wrapped = true; _prevLen = _len; _pos = 0; _copyAt = abs + Xf; _captured = _len; _len = Math.Min(want, _captured); }
            else if (_wrapped && _pos >= _len) { _prevLen = _len; _pos = 0; _len = Math.Min(want, _captured); }
            if (_wrapped && !_copied && abs >= _copyAt)
            {
                for (int k = 0; k < _captured + Xf; k++) { _slice[k * 2] = c.H(_anchor + k, 0); _slice[k * 2 + 1] = c.H(_anchor + k, 1); }
                _copied = true;
            }
            for (int ch = 0; ch < 2; ch++)
            {
                float y = Src(c, _pos, ch);
                if (_wrapped && _pos < Xf) { float f = Dsp.Smooth01(_pos / (float)Xf); y = y * f + Src(c, _prevLen + _pos, ch) * (1 - f); }
                y = _dc[ch].Run(y);     // a short slice repeated has its own mean: keep it off the speakers
                float x = io[i * 2 + ch];
                io[i * 2 + ch] = x + m * (y - x);
            }
            _pos++;
        }
    }
}

/// <summary>Reverse: on every beat line a new voice plays the previous beat backwards; voices crossfade over 6 ms.
/// Equal-power against the dry (the reversed audio is uncorrelated with it).</summary>
internal sealed class ReverseUnit : FxUnit
{
    private const int Xf = 256;
    private bool _on; private long _cur = -1, _old = -1; private int _k, _kOld, _xf = Xf; private long _slice = long.MinValue;
    private readonly DcBlock[] _dc = { new(), new() };
    public override void Disengage() { _on = false; _cur = _old = -1; _slice = long.MinValue; _xf = Xf; _dc[0].Clear(); _dc[1].Clear(); }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        double sliceBeats = param > 0 ? param : 1;
        for (int i = 0; i < n; i++)
        {
            float m = mix[i] * Dsp.Smooth01(drive[i] / .6f);
            long abs = c.Abs0 + i;
            long idx = (long)Math.Floor(c.Beat(i) / sliceBeats);
            if (!_on) { if (m <= 0) continue; _on = true; _cur = abs; _k = 0; _old = -1; _slice = idx; _xf = Xf; }
            else if (idx != _slice) { _slice = idx; _old = _cur; _kOld = _k; _cur = abs; _k = 0; _xf = 0; }
            float a = m * MathF.PI * .5f, tr = EqualPowerTrim(a), dryG = MathF.Cos(a) * tr, wetG = MathF.Sin(a) * tr;
            float f = _xf < Xf ? Dsp.Smooth01(_xf / (float)Xf) : 1;
            int maxBack = c.HistFrames - 4096;
            for (int ch = 0; ch < 2; ch++)
            {
                float y = c.H(_cur - Math.Min(_k, maxBack), ch);
                if (f < 1 && _old >= 0) y = y * f + c.H(_old - Math.Min(_kOld, maxBack), ch) * (1 - f);
                io[i * 2 + ch] = io[i * 2 + ch] * dryG + _dc[ch].Run(y) * wetG;
            }
            _k++; _kOld++; if (_xf < Xf) _xf++;
        }
    }
}

/// <summary>Vinyl brake: playback slows with stick distance (full = stop) and lags behind the live stream; on release it
/// spins back up and crossfades to the live audio over 10 ms. The level fades only in the last 12 % of speed.</summary>
internal sealed class BrakeUnit : FxUnit
{
    private double _speed = 1, _lag; private int _resync = -1; private const int Xf = 441;
    public override bool TailActive => _lag > 0 || _speed < 1 || _resync >= 0;
    public override void Disengage() { _speed = 1; _lag = 0; _resync = -1; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        double down = 1.0 / (.45 * Dsp.Sr), up = 1.0 / (.25 * Dsp.Sr);
        for (int i = 0; i < n; i++)
        {
            double target = mix[i] > 0 ? 1 - Math.Clamp(drive[i] * 1.05, 0, 1) : 1;
            _speed += Math.Clamp(target - _speed, -down, up);
            if (_resync < 0) _lag = Math.Max(0, _lag + 1 - _speed);
            if (_resync < 0 && _lag > 0 && (_speed >= .9999 && target >= .9999 || _lag > c.HistFrames - 8192)) _resync = 0;
            long abs = c.Abs0 + i;
            float g = (float)FxCurves.BendGain(_speed), m = mix[i];
            float f = _resync >= 0 ? Dsp.Smooth01(_resync / (float)Xf) : 0;
            for (int ch = 0; ch < 2; ch++)
            {
                float x = io[i * 2 + ch];
                float y = _lag > 0 ? c.HFrac(abs - _lag, ch) * g : x * g;
                if (_resync >= 0) y = y * (1 - f) + x * f;
                io[i * 2 + ch] = x + m * (y - x);
            }
            if (_resync >= 0 && ++_resync > Xf) { _resync = -1; _lag = 0; _speed = 1; }
        }
    }
}

/// <summary>Freeze: captures the last quarter beat when engaged and plays it as an overlapping Hann grain cloud
/// (4 grains, random start points), so the sound hangs in the air. Equal-power against the dry.</summary>
internal sealed class FreezeUnit : FxUnit
{
    private const int MaxW = Dsp.Sr / 2;
    private readonly float[] _buf = new float[MaxW * 2];
    private readonly int[] _off = new int[4]; private readonly int[] _ph = new int[4];
    private int _w, _g; private bool _on; private Rng _rng = new(0x51ED270B);
    private readonly DcBlock _dl = new(), _dr = new();
    public override void Disengage() { _on = false; _dl.Clear(); _dr.Clear(); }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float m = mix[i] * Dsp.Ramp(drive[i], .2f);
            if (!_on)
            {
                if (m <= 0) continue;
                _w = (int)Math.Clamp(c.FramesPerBeat * .25, Dsp.Sr * .06, MaxW - 1);
                long end = c.Abs0 + i;
                for (int k = 0; k < _w; k++) { _buf[k * 2] = c.H(end - _w + k, 0); _buf[k * 2 + 1] = c.H(end - _w + k, 1); }
                _g = Math.Min(_w, (int)(Dsp.Sr * .07));
                for (int j = 0; j < 4; j++) { _ph[j] = j * _g / 4; _off[j] = (int)(_rng.Next01() * (_w - _g)); }
                _on = true;
            }
            float l = 0, r = 0;
            for (int j = 0; j < 4; j++)
            {
                float p = _ph[j] / (float)_g, win = .5f - .5f * MathF.Cos(2 * MathF.PI * p);
                int at = (_off[j] + _ph[j]) * 2; l += _buf[at] * win; r += _buf[at + 1] * win;
                if (++_ph[j] >= _g) { _ph[j] = 0; _off[j] = (int)(_rng.Next01() * (_w - _g)); }
            }
            float a = m * MathF.PI * .5f, tr = EqualPowerTrim(a), dryG = MathF.Cos(a) * tr, wetG = MathF.Sin(a) * .62f * tr;
            io[i * 2] = io[i * 2] * dryG + _dl.Run(l) * wetG; io[i * 2 + 1] = io[i * 2 + 1] * dryG + _dr.Run(r) * wetG;
        }
    }
}

/// <summary>Pitch drop: two-tap granular shifter, 0 to −12 semitones with stick distance.</summary>
internal sealed class PitchDownUnit : FxUnit
{
    private readonly PitchShifter _l = new(2205), _r = new(2205);
    public override void Reset() { _l.Clear(); _r.Clear(); }
    public override void Engage(FxContext c) { _l.Prime(c, 0); _r.Prime(c, 1); }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float ratio = 1;
        for (int i = 0; i < n; i++)
        {
            if (i % Dsp.ControlEvery == 0) ratio = MathF.Pow(2, -drive[i]);
            float m = mix[i] * Dsp.Ramp(drive[i], .15f), a = m * MathF.PI * .5f, tr = EqualPowerTrim(a), dryG = MathF.Cos(a) * tr, wetG = MathF.Sin(a) * tr;
            float l = io[i * 2], r = io[i * 2 + 1];
            io[i * 2] = l * dryG + _l.Run(l, ratio) * wetG;
            io[i * 2 + 1] = r * dryG + _r.Run(r, ratio) * wetG;
        }
    }
}

// ======================================================================== rhythm / amplitude

internal sealed class CutUnit : FxUnit
{
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++) { float g = 1 - mix[i]; io[i * 2] *= g; io[i * 2 + 1] *= g; }
    }
}

/// <summary>Transformer: hard on/off gate on 1/16 notes (1/32 past half), 1 ms ramps. Param &gt; 0 = division in beats.</summary>
internal sealed class TransformerUnit : FxUnit
{
    private float _env = 1;
    public override void Disengage() { _env = 1; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float k = Dsp.Coef(1);
        for (int i = 0; i < n; i++)
        {
            double div = param > 0 ? param : drive[i] < .55f ? .25 : .125;
            double p = c.Beat(i) / div; p -= Math.Floor(p);
            _env += ((p < .5 ? 1f : 0f) - _env) * k;
            float m = param > 0 ? mix[i] : mix[i] * Dsp.Ramp(drive[i], .08f), g = 1 + m * (_env - 1);
            io[i * 2] *= g; io[i * 2 + 1] *= g;
        }
    }
}

/// <summary>Trance gate: 16-step pattern of 1/16 notes; each open step plucks (2 ms attack, decay shorter as you push).</summary>
internal sealed class TranceGateUnit : FxUnit
{
    private static readonly bool[] Pattern = { true, false, true, true, false, true, true, false, true, false, true, true, true, true, false, true };
    private float _env; private long _step = long.MinValue; private float _peak;
    public override void Disengage() { _env = 0; _step = long.MinValue; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float att = Dsp.Coef(2), rel = Dsp.Coef(4), dec = 0;
        for (int i = 0; i < n; i++)
        {
            if (i % Dsp.ControlEvery == 0) dec = Dsp.Coef(220 - 180 * drive[i]);
            double b = c.Beat(i) * 4; long step = (long)Math.Floor(b);
            if (step != _step) { _step = step; _peak = 1; }
            bool open = Pattern[(int)(((step % 16) + 16) % 16)];
            float target = open ? _peak : 0;
            _env += (target - _env) * (target > _env ? att : open ? dec : rel);
            if (open) _peak += (.15f - _peak) * dec * .35f;
            float depth = MathF.Pow(drive[i], .7f), m = mix[i] * Dsp.Ramp(drive[i], .05f);
            float g = 1 - m * depth * (1 - Math.Min(1, _env / .999f));
            io[i * 2] *= g; io[i * 2 + 1] *= g;
        }
    }
}

/// <summary>Pump: sidechain-style duck on every beat (7 ms down, back up by 60 % of the beat).</summary>
internal sealed class PumpUnit : FxUnit
{
    public static float Shape(double phase)
    {
        const double a = .015;
        if (phase < a) return (float)(phase / a);
        return 1 - Dsp.Smooth01((float)((phase - a) / .6));
    }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            double b = c.Beat(i); double ph = b - Math.Floor(b);
            float g = 1 - mix[i] * .88f * drive[i] * Shape(ph);
            io[i * 2] *= g; io[i * 2 + 1] *= g;
        }
    }
}

/// <summary>Auto pan: balance LFO (never boosts a side), one beat per cycle down to 1/4 beat.</summary>
internal sealed class AutoPanUnit : FxUnit
{
    private double _ph = -1;
    public override void Disengage() { _ph = -1; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        if (_ph < 0) { double b = c.Beat(0); _ph = b - Math.Floor(b); }
        for (int i = 0; i < n; i++)
        {
            double period = Math.Pow(2, -2 * drive[i]);
            _ph += c.BeatInc / period; if (_ph >= 1) _ph -= 1;
            float p = mix[i] * Dsp.Ramp(drive[i], .1f) * (float)Math.Sin(2 * Math.PI * _ph);
            io[i * 2] *= Math.Min(1, 1 - p); io[i * 2 + 1] *= Math.Min(1, 1 + p);
        }
    }
}

// ======================================================================== filters

/// <summary>Resonant 24 dB/oct sweep (two cascaded SVFs). Low-pass: 20 kHz → 140 Hz; high-pass: 20 Hz → 3 kHz.
/// Resonance rises with the sweep; the level is compensated so the resonant peak stays within the dry level.</summary>
internal sealed class SweepUnit : FxUnit
{
    private readonly bool _hp;
    private readonly Svf[] _a = { new(), new() }, _b = { new(), new() };
    private float _comp = 1;
    public SweepUnit(bool highPass) { _hp = highPass; }
    public override void Reset() { foreach (var f in _a) f.Clear(); foreach (var f in _b) f.Clear(); }
    public override void Disengage() => Reset();
    public static double Cutoff(bool hp, double d) => hp ? 20 * Math.Pow(150, Math.Pow(d, .9)) : 20000 * Math.Pow(140 / 20000.0, Math.Pow(d, .85));
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float d = drive[i];
            if (i % Dsp.ControlEvery == 0)
            {
                double fc = Cutoff(_hp, d), q2 = .54 + .95 * d;
                for (int ch = 0; ch < 2; ch++) { _a[ch].Set(fc, .54); _b[ch].Set(fc, q2); }
                _comp = 1f / (1f + (_hp ? .5f : .32f) * d);
            }
            float m = mix[i] * Dsp.Ramp(d, .06f);
            for (int ch = 0; ch < 2; ch++)
            {
                float x = io[i * 2 + ch];
                _a[ch].Run(x, out float lp1, out _, out float hp1);
                _b[ch].Run(_hp ? hp1 : lp1, out float lp2, out _, out float hp2);
                float y = (_hp ? hp2 : lp2) * _comp;
                io[i * 2 + ch] = x + m * (y - x);
            }
        }
    }
}

/// <summary>Tuned feedback comb (metallic resonator), peak-normalised; 220 → 880 Hz with stick distance.</summary>
internal sealed class CombUnit : FxUnit
{
    private readonly DelayLine _l = new(512), _r = new(512);
    private float _delay = 200;
    public override void Reset() { _l.Clear(); _r.Clear(); }
    public override void Engage(FxContext c) { _l.Prime(c, 0); _r.Prime(c, 1); }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float d = drive[i], g = .55f + .35f * d;
            float target = Dsp.Sr / (220f * MathF.Pow(2, 2 * d));
            _delay += (target - _delay) * .002f;
            float m = mix[i] * Dsp.Ramp(d, .1f) * .85f;
            float xl = io[i * 2], xr = io[i * 2 + 1];
            float yl = xl + g * _l.Read(_delay), yr = xr + g * _r.Read(_delay * 1.003f);
            _l.Write(Math.Clamp(yl, -8, 8)); _r.Write(Math.Clamp(yr, -8, 8));
            io[i * 2] = xl + m * (yl * (1 - g) - xl); io[i * 2 + 1] = xr + m * (yr * (1 - g) - xr);
        }
    }
}

/// <summary>Talking vowel filter: three formant band-passes glide a-e-i-o-u-o-i-e over one bar.</summary>
internal sealed class VowelUnit : FxUnit
{
    private static readonly float[,] F = { { 800, 1150, 2900 }, { 400, 1600, 2700 }, { 270, 2290, 3010 }, { 450, 800, 2830 }, { 325, 700, 2530 } };
    private static readonly float[] Gain = { 1f, .55f, .32f }, Q = { 5, 8, 10 };
    private readonly Svf[,] _f = new Svf[2, 3];
    public VowelUnit() { for (int c = 0; c < 2; c++) for (int k = 0; k < 3; k++) _f[c, k] = new Svf(); }
    public override void Reset() { foreach (var f in _f) f.Clear(); }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            if (i % Dsp.ControlEvery == 0)
            {
                double v = 2 - 2 * Math.Cos(2 * Math.PI * c.Beat(i) / 4);      // 0..4 and back once per bar
                int a = Math.Min(3, (int)v); float t = (float)(v - a);
                for (int k = 0; k < 3; k++)
                {
                    double hz = Math.Exp(Math.Log(F[a, k]) * (1 - t) + Math.Log(F[a + 1, k]) * t);
                    _f[0, k].Set(hz, Q[k]); _f[1, k].Set(hz * 1.01, Q[k]);
                }
            }
            float m = mix[i] * MathF.Pow(drive[i], .7f);
            for (int ch = 0; ch < 2; ch++)
            {
                float x = io[i * 2 + ch], y = 0;
                for (int k = 0; k < 3; k++) { _f[ch, k].Run(x, out _, out float bp, out _); y += bp * _f[ch, k].K * Gain[k]; }
                io[i * 2 + ch] = x + m * (y * 1.45f - x);
            }
        }
    }
}

/// <summary>Wobble: resonant low-pass swept by a tempo LFO, 1/2 beat down to 1/8 beat per wobble.</summary>
internal sealed class WobbleUnit : FxUnit
{
    private readonly Svf _l = new(), _r = new();
    private double _ph = -1;
    public override void Reset() { _l.Clear(); _r.Clear(); _ph = -1; }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        if (_ph < 0) { double b = c.Beat(0) * 2; _ph = b - Math.Floor(b); }
        for (int i = 0; i < n; i++)
        {
            float d = drive[i];
            double period = .5 * Math.Pow(2, -2 * d);
            _ph += c.BeatInc / period; if (_ph >= 1) _ph -= 1;
            if (i % Dsp.ControlEvery == 0)
            {
                double lfo = .5 - .5 * Math.Cos(2 * Math.PI * _ph), hz = 180 * Math.Pow(2, lfo * 5 * (.4 + .6 * d));
                _l.Set(hz, 2.2); _r.Set(hz, 2.2);
            }
            float m = mix[i] * Dsp.Ramp(d, .1f);
            float xl = io[i * 2], xr = io[i * 2 + 1];
            _l.Run(xl, out float ll, out _, out _); _r.Run(xr, out float lr, out _, out _);
            io[i * 2] = xl + m * (ll * .55f - xl); io[i * 2 + 1] = xr + m * (lr * .55f - xr);
        }
    }
}

// ======================================================================== drive / lo-fi

/// <summary>Tube-style saturation, level-matched to the input peak (never louder), DC-blocked, softened top.</summary>
internal sealed class DriveUnit : FxUnit
{
    private float _env; private readonly DcBlock _dl = new(), _dr = new(); private float _lpl, _lpr;
    public override void Reset() { _env = 0; _dl.Clear(); _dr.Clear(); _lpl = _lpr = 0; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float xl = io[i * 2], xr = io[i * 2 + 1], env = Env(ref _env, xl, xr), d = drive[i];
            float g = 1 + 14 * d * d, norm = 1 / Dsp.Tanh(g), asym = .12f * d;
            float ul = xl / env, ur = xr / env;
            float yl = Dsp.Tanh(g * (ul + asym * ul * ul)) * norm * env, yr = Dsp.Tanh(g * (ur + asym * ur * ur)) * norm * env;
            yl = _dl.Run(yl); yr = _dr.Run(yr);
            _lpl += (yl - _lpl) * .55f; _lpr += (yr - _lpr) * .55f;
            float m = mix[i] * Dsp.Ramp(d, .1f);
            io[i * 2] = xl + m * (_lpl * .92f - xl); io[i * 2 + 1] = xr + m * (_lpr * .92f - xr);
        }
    }
}

/// <summary>Sine wavefolder on the level-normalised signal (output never exceeds the input peak).</summary>
internal sealed class WavefoldUnit : FxUnit
{
    private float _env; private readonly DcBlock _dl = new(), _dr = new();
    public override void Reset() { _env = 0; _dl.Clear(); _dr.Clear(); }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float xl = io[i * 2], xr = io[i * 2 + 1], env = Env(ref _env, xl, xr), g = (1 + 5 * drive[i]) * MathF.PI * .5f;
            float yl = _dl.Run(MathF.Sin(g * xl / env) * env * .9f), yr = _dr.Run(MathF.Sin(g * xr / env) * env * .9f);
            float m = mix[i] * Dsp.Ramp(drive[i], .1f);
            io[i * 2] = xl + m * (yl - xl); io[i * 2 + 1] = xr + m * (yr - xr);
        }
    }
}

/// <summary>Bit crusher: sample-and-hold rate reduction (÷1 … ÷40) and quantisation (16 … 4 bits) relative to the
/// input peak, rounded to nearest (no DC).</summary>
internal sealed class CrushUnit : FxUnit
{
    private float _env, _hl, _hr; private double _acc = 1e9;
    public override void Reset() { _env = 0; _hl = _hr = 0; _acc = 1e9; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float xl = io[i * 2], xr = io[i * 2 + 1], env = Env(ref _env, xl, xr), d = drive[i];
            double div = 1 + 39 * d * d;
            if (++_acc >= div)
            {
                _acc -= div; if (_acc > div) _acc = 0;
                float q = env / MathF.Pow(2, 15 - 11 * d);
                _hl = Math.Clamp(MathF.Round(xl / q) * q, -env, env); _hr = Math.Clamp(MathF.Round(xr / q) * q, -env, env);
            }
            float m = mix[i] * Dsp.Ramp(d, .08f);
            io[i * 2] = xl + m * (_hl - xl); io[i * 2 + 1] = xr + m * (_hr - xr);
        }
    }
}

/// <summary>Ring modulator, carrier 55 → 880 Hz.</summary>
internal sealed class RingModUnit : FxUnit
{
    private double _ph;
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        double hz = 55;
        for (int i = 0; i < n; i++)
        {
            if (i % Dsp.ControlEvery == 0) hz = 55 * Math.Pow(2, 4 * drive[i]);
            _ph += hz / Dsp.Sr; if (_ph >= 1) _ph -= 1;
            float car = (float)Math.Sin(2 * Math.PI * _ph), m = mix[i] * Dsp.Ramp(drive[i], .1f) * .9f;
            float xl = io[i * 2], xr = io[i * 2 + 1];
            io[i * 2] = xl + m * (xl * car - xl); io[i * 2 + 1] = xr + m * (xr * car - xr);
        }
    }
}

// ======================================================================== modulation

/// <summary>Six-stage phaser, LFO 4 beats → 1 beat, notch sweep 200 Hz → 6.4 kHz, peak-normalised feedback.</summary>
internal sealed class PhaserUnit : FxUnit
{
    private readonly float[,] _z = new float[2, 6]; private readonly float[] _fb = new float[2], _a = new float[2];
    private double _ph = -1;
    public override void Reset() { Array.Clear(_z); Array.Clear(_fb); _ph = -1; }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        if (_ph < 0) { double b = c.Beat(0) / 4; _ph = b - Math.Floor(b); }
        for (int i = 0; i < n; i++)
        {
            float d = drive[i];
            _ph += c.BeatInc / (4 * Math.Pow(2, -2 * d)); if (_ph >= 1) _ph -= 1;
            if (i % Dsp.ControlEvery == 0)
                for (int ch = 0; ch < 2; ch++)
                {
                    double lfo = .5 - .5 * Math.Cos(2 * Math.PI * (_ph + ch * .25)), hz = 200 * Math.Pow(2, lfo * (1 + 4 * d));
                    double t = Math.Tan(Math.PI * Math.Min(hz, 16000) / Dsp.Sr); _a[ch] = (float)((t - 1) / (t + 1));
                }
            float fb = .15f + .35f * d, norm = 1 / (.5f * (1 + 1 / (1 - fb))), m = mix[i] * Dsp.Ramp(d, .1f);
            for (int ch = 0; ch < 2; ch++)
            {
                float x = io[i * 2 + ch], v = x + _fb[ch] * fb, a = _a[ch];
                for (int k = 0; k < 6; k++) { float o = a * v + _z[ch, k]; _z[ch, k] = v - a * o; v = o; }
                _fb[ch] = Math.Clamp(v, -4, 4);
                io[i * 2 + ch] = x + m * ((x + v) * .5f * norm - x);
            }
        }
    }
}

/// <summary>Flanger: 0.4–6 ms swept delay, LFO 4 beats → 1 beat, feedback rises with stick distance, peak-normalised.</summary>
internal sealed class FlangerUnit : FxUnit
{
    private readonly DelayLine _l = new(1024), _r = new(1024);
    private double _ph = -1;
    public override void Reset() { _l.Clear(); _r.Clear(); _ph = -1; }
    public override void Engage(FxContext c) { _l.Prime(c, 0); _r.Prime(c, 1); }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        if (_ph < 0) { double b = c.Beat(0) / 4; _ph = b - Math.Floor(b); }
        for (int i = 0; i < n; i++)
        {
            float d = drive[i];
            _ph += c.BeatInc / (4 * Math.Pow(2, -2 * d)); if (_ph >= 1) _ph -= 1;
            float sweep = 1.5f + 4 * d, fb = .3f + .45f * d, norm = 1 / (.5f * (1 + 1 / (1 - fb))), m = mix[i] * Dsp.Ramp(d, .1f);
            float dl = (.4f + sweep * (.5f - .5f * MathF.Cos(2 * MathF.PI * (float)_ph))) * .001f * Dsp.Sr;
            float dr = (.4f + sweep * (.5f - .5f * MathF.Cos(2 * MathF.PI * (float)(_ph + .2)))) * .001f * Dsp.Sr;
            float xl = io[i * 2], xr = io[i * 2 + 1], yl = _l.Read(dl), yr = _r.Read(dr);
            _l.Write(Math.Clamp(xl + yl * fb, -4, 4)); _r.Write(Math.Clamp(xr + yr * fb, -4, 4));
            io[i * 2] = xl + m * ((xl + yl) * .5f * norm - xl); io[i * 2 + 1] = xr + m * ((xr + yr) * .5f * norm - xr);
        }
    }
}

/// <summary>Three-voice stereo chorus (free-running LFOs, 14–24 ms), linear blend with a small makeup gain.</summary>
internal sealed class ChorusUnit : FxUnit
{
    private readonly DelayLine _l = new(2048), _r = new(2048);
    private readonly double[] _ph = { 0, .33, .66 };
    private static readonly double[] Rate = { .31, .47, .63 }; private static readonly float[] Base = { 14, 19, 24 };
    public override void Reset() { _l.Clear(); _r.Clear(); }
    public override void Engage(FxContext c) { _l.Prime(c, 0); _r.Prime(c, 1); }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        for (int i = 0; i < n; i++)
        {
            float d = drive[i], depth = 1 + 2 * d;
            float xl = io[i * 2], xr = io[i * 2 + 1];
            _l.Write(xl); _r.Write(xr);
            float wl = 0, wr = 0;
            for (int v = 0; v < 3; v++)
            {
                _ph[v] += Rate[v] / Dsp.Sr; if (_ph[v] >= 1) _ph[v] -= 1;
                float s = MathF.Sin(2 * MathF.PI * (float)_ph[v]), cs = MathF.Cos(2 * MathF.PI * (float)_ph[v]);
                wl += _l.Read((Base[v] + depth * s) * .001f * Dsp.Sr); wr += _r.Read((Base[v] + depth * cs) * .001f * Dsp.Sr);
            }
            float w = .5f * mix[i] * Dsp.Ramp(d, .1f) * (.6f + .4f * d), mk = 1 + .2f * w;
            io[i * 2] = (xl * (1 - w) + wl / 3 * w) * mk; io[i * 2 + 1] = (xr * (1 - w) + wr / 3 * w) * mk;
        }
    }
}

// ======================================================================== delays

/// <summary>Dub tape echo: dotted 1/8 (3/4 beat), feedback 0.35 → 0.82, darker and saturated in the loop, slight wow.
/// The send drops on release and the repeats ring out.</summary>
internal sealed class TapeEchoUnit : FxUnit
{
    private readonly DelayLine _l = new(Dsp.Sr * 2 + 8), _r = new(Dsp.Sr * 2 + 8);
    private float _lpL, _lpR, _hpL, _hpR, _time = -1; private double _wow; private double _energy;
    public override bool TailActive => _energy > 1e-9;
    public override void Reset() { _l.Clear(); _r.Clear(); _lpL = _lpR = _hpL = _hpR = 0; _energy = 0; _time = -1; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float target = (float)Math.Clamp(.75 * c.FramesPerBeat, 64, _l.Length - 64);
        if (_time < 0) _time = target;
        double e = 0;
        for (int i = 0; i < n; i++)
        {
            float d = drive[i], send = mix[i] * Dsp.Ramp(d, .08f), fb = .35f + .47f * d, lpk = .45f - .2f * d;
            _time += (target - _time) * .0002f;
            _wow += .7 / Dsp.Sr; if (_wow >= 1) _wow -= 1;
            float t = _time + 15f * MathF.Sin(2 * MathF.PI * (float)_wow);
            float el = _l.Read(t), er = _r.Read(t + 7);
            _lpL += (el - _lpL) * lpk; _lpR += (er - _lpR) * lpk;
            _hpL += (_lpL - _hpL) * .017f; _hpR += (_lpR - _hpR) * .017f;
            float ll = Dsp.Tanh(1.2f * (_lpL - _hpL)) / 1.2f, rr = Dsp.Tanh(1.2f * (_lpR - _hpR)) / 1.2f;
            float xl = io[i * 2], xr = io[i * 2 + 1];
            _l.Write(xl * send * .9f + ll * fb); _r.Write(xr * send * .9f + rr * fb);
            float duck = 1 - .35f * send;
            io[i * 2] = xl * duck + el * .6f; io[i * 2 + 1] = xr * duck + er * .6f;
            e += el * el + er * er;
        }
        _energy = e / Math.Max(1, n);
    }
}

/// <summary>Ping-pong: 1/8-note echo (half a beat) that bounces left-right, bright, feedback 0.3 → 0.7.</summary>
internal sealed class PingPongUnit : FxUnit
{
    private readonly DelayLine _l = new(Dsp.Sr * 2 + 8), _r = new(Dsp.Sr * 2 + 8);
    private float _lpL, _lpR, _time = -1; private double _energy;
    public override bool TailActive => _energy > 1e-9;
    public override void Reset() { _l.Clear(); _r.Clear(); _lpL = _lpR = 0; _energy = 0; _time = -1; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float target = (float)Math.Clamp(.5 * c.FramesPerBeat, 64, _l.Length - 64);
        if (_time < 0) _time = target;
        double e = 0;
        for (int i = 0; i < n; i++)
        {
            float d = drive[i], send = mix[i] * Dsp.Ramp(d, .08f), fb = .3f + .4f * d;
            _time += (target - _time) * .0002f;
            float el = _l.Read(_time), er = _r.Read(_time);
            _lpL += (el - _lpL) * .6f; _lpR += (er - _lpR) * .6f;
            float xl = io[i * 2], xr = io[i * 2 + 1], mono = (xl + xr) * .5f * send;
            _l.Write(mono + _lpR * fb); _r.Write(_lpL * fb);
            float duck = 1 - .35f * send;
            io[i * 2] = xl * duck + el * .58f; io[i * 2 + 1] = xr * duck + er * .58f;
            e += el * el + er * er;
        }
        _energy = e / Math.Max(1, n);
    }
}

// ======================================================================== reverbs

/// <summary>Hall: 8-line FDN, RT60 1.8 → 5 s with stick distance, 18 ms predelay; the dry ducks slightly as it opens.</summary>
internal class HallUnit : FxUnit
{
    protected readonly Fdn Tank;
    protected double TailEnergy;
    public HallUnit() : this(1.25, 18) { }
    protected HallUnit(double size, double predelay) { Tank = new Fdn(size, predelay); }
    public override bool TailActive => TailEnergy > 1e-9;
    public override void Reset() { Tank.Clear(); TailEnergy = 0; }
    protected virtual (double Rt, double Damp) Room(float d) => (1.8 + 3.2 * d, .35 - .1 * d);
    protected virtual float Wet(float l, float r, int i, FxContext c, out float wl, out float wr) { wl = l; wr = r; return .62f; }
    protected virtual float Input(float mono, float d) => mono;
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        Tank.Energy = 0;
        for (int i = 0; i < n; i++)
        {
            float d = drive[i];
            if (i % Dsp.ControlEvery == 0) { var (rt, damp) = Room(d); Tank.Set(rt, damp); }
            float send = mix[i] * Dsp.Ramp(d, .08f), xl = io[i * 2], xr = io[i * 2 + 1];
            Tank.Run(Input((xl + xr) * .5f * send, d), out float l, out float r);
            float g = Wet(l, r, i, c, out float wl, out float wr), duck = 1 - .3f * send;
            io[i * 2] = xl * duck + wl * g; io[i * 2 + 1] = xr * duck + wr * g;
        }
        TailEnergy = Tank.Energy / Math.Max(1, n);
    }
}

/// <summary>Shimmer: a long FDN whose own output is pitched up an octave and fed back in, so the tail climbs.</summary>
internal sealed class ShimmerUnit : HallUnit
{
    private readonly PitchShifter _up = new(1764);
    private float _last;
    public ShimmerUnit() : base(1.4, 30) { }
    public override void Reset() { base.Reset(); _up.Clear(); _last = 0; }
    protected override (double Rt, double Damp) Room(float d) => (3 + 2 * d, .25);
    protected override float Input(float mono, float d) => mono + Math.Clamp(_up.Run(_last, 2f), -1, 1) * (.18f + .3f * d);
    protected override float Wet(float l, float r, int i, FxContext c, out float wl, out float wr) { _last = (l + r) * .5f; wl = l; wr = r; return .45f; }
}

/// <summary>Gated verb: a dense short room whose output is cut on every 1/8 note (open for 55 % of it, 2 ms ramps).</summary>
internal sealed class GatedVerbUnit : HallUnit
{
    private float _gate;
    public GatedVerbUnit() : base(.7, 4) { }
    protected override (double Rt, double Damp) Room(float d) => (1.6, .2);
    protected override float Wet(float l, float r, int i, FxContext c, out float wl, out float wr)
    {
        double p = c.Beat(i) * 2; p -= Math.Floor(p);
        _gate += ((p < .55 ? 1f : 0f) - _gate) * .011f;
        wl = l * _gate; wr = r * _gate; return .85f;
    }
}

// ======================================================================== generators (added after the safety guard)

/// <summary>Noise riser: stereo white noise through a band-pass that climbs 400 Hz → 12.8 kHz, pulsing on 1/16 notes as
/// you push. Fixed level cap (-14 dBFS); the dry ducks a little underneath.</summary>
internal sealed class NoiseRiserUnit : FxUnit
{
    private readonly Svf _l = new(), _r = new(); private Rng _a = new(0x1234567u), _b = new(0x89ABCDEu); private float _pulse = 1;
    public const float Cap = .2f;
    public override void Reset() { _l.Clear(); _r.Clear(); }
    public override void Disengage() => Reset();
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float k = Dsp.Coef(3);
        for (int i = 0; i < n; i++)
        {
            float d = drive[i], m = mix[i] * d;
            if (i % Dsp.ControlEvery == 0) { double hz = 400 * Math.Pow(2, 5 * d); _l.Set(hz, 1.3); _r.Set(hz * 1.02, 1.3); }
            double p = c.Beat(i) * 4; p -= Math.Floor(p);
            _pulse += ((p < .5 ? 1f : 1 - .65f * d) - _pulse) * k;
            _l.Run(_a.Next(), out _, out float bl, out _); _r.Run(_b.Next(), out _, out float br, out _);
            float g = Cap * m * _pulse * 1.7f, duck = 1 - .15f * m;
            io[i * 2] = io[i * 2] * duck + Math.Clamp(bl * _l.K * g, -Cap, Cap);
            io[i * 2 + 1] = io[i * 2 + 1] * duck + Math.Clamp(br * _r.K * g, -Cap, Cap);
        }
    }
}

/// <summary>Sub drop: an 808-style sine boom that falls 90 → 35 Hz, fired on the next 1/16 line when engaged and then on
/// every bar while held. Param changes (a button press counter) fire one boom each. Level cap -8 dBFS.</summary>
internal sealed class SubDropUnit : FxUnit
{
    public const float Cap = .4f;
    private double _ph, _t = 1e9; private float _env, _level, _choke = 1; private bool _armed, _first, _button; private float _lastParam; private long _lastSix;
    private readonly DcBlock _dc = new();
    public override bool TailActive => _env * _choke > 2e-5f;
    public override void Disengage() { _armed = false; }
    public override void Reset() { Disengage(); _env = 0; _t = 1e9; _dc.Clear(); _choke = 1; }
    public override void Process(Span<float> io, int n, float[] drive, float[] mix, float param, FxContext c)
    {
        float att = Dsp.Coef(4), choke = Dsp.Coef(60);
        double beatSec = c.FramesPerBeat / Dsp.Sr;
        for (int i = 0; i < n; i++)
        {
            float m = mix[i] * drive[i];
            bool fire = false;
            if (param != _lastParam) { _lastParam = param; if (param > 0) { fire = true; m = Math.Max(m, 1); } }
            else if (param <= 0)
            {
                long six = (long)Math.Floor(c.Beat(i) * 4);
                if (m <= 0) _armed = false;
                else if (!_armed) { _armed = true; _first = true; _lastSix = six; }
                else if (six != _lastSix)
                {
                    _lastSix = six;
                    if (_first || ((six % 16) + 16) % 16 == 0) { fire = true; _first = false; }
                }
            }
            if (fire) { _t = 0; _level = Math.Clamp(m, 0, 1); _button = param > 0; _choke = 1; }
            // letting go of the stick chokes the boom over ~60 ms (a button hit always rings out)
            _choke += ((m > 0 || _button ? 1f : 0f) - _choke) * choke;
            float hz = 35 + 55 * (float)Math.Exp(-_t / (beatSec * .35));
            float target = _t < 1e8 ? (float)Math.Exp(-_t / (beatSec * 1.2)) * _level : 0;
            _env += (target - _env) * (target > _env ? att : .02f);
            _ph += hz / Dsp.Sr; if (_ph >= 1) _ph -= 1;
            _t += 1.0 / Dsp.Sr;
            float e = _env * _choke, s = _dc.Run((float)Math.Sin(2 * Math.PI * _ph) * e * Cap), duck = 1 - .3f * e;
            io[i * 2] = io[i * 2] * duck + s; io[i * 2 + 1] = io[i * 2 + 1] * duck + s;
        }
    }
}
