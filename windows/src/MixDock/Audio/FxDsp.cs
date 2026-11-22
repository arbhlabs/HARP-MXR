namespace MixDock.Audio;

/// <summary>Small allocation-free DSP building blocks for the FX rack (render thread only).</summary>
internal static class Dsp
{
    public const int Sr = FxCurves.SampleRate;
    public const int ControlEvery = 16;          // coefficient updates every 16 samples (0.36 ms)

    /// <summary>Padé tanh, exact at 0, saturates to ±1 beyond ±3. Cheap and monotonic.</summary>
    public static float Tanh(float x)
    {
        if (x > 3f) return 1f; if (x < -3f) return -1f;
        float x2 = x * x; return x * (27f + x2) / (27f + 9f * x2);
    }
    public static float Smooth01(float v) { v = v < 0 ? 0 : v > 1 ? 1 : v; return v * v * (3 - 2 * v); }
    /// <summary>Wet fade-in near the centre of the stick: 0 at drive 0, 1 from <paramref name="knee"/> on, smooth.</summary>
    public static float Ramp(float drive, float knee) => Smooth01(drive / knee);
    public static float Coef(double ms) => (float)FxCurves.Coef(ms);
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>Topology-preserving state-variable filter (Simper). Stable under fast cutoff modulation.</summary>
internal sealed class Svf
{
    private float _ic1, _ic2, _a1, _a2, _a3, _k = 1.414f;
    public void Set(double hz, double q)
    {
        hz = Math.Clamp(hz, 10, Dsp.Sr * .45);
        double g = Math.Tan(Math.PI * hz / Dsp.Sr), k = 1 / Math.Max(.3, q);
        double a1 = 1 / (1 + g * (g + k));
        _a1 = (float)a1; _a2 = (float)(g * a1); _a3 = (float)(g * g * a1); _k = (float)k;
    }
    public float K => _k;
    public void Run(float v0, out float lp, out float bp, out float hp)
    {
        float v3 = v0 - _ic2, v1 = _a1 * _ic1 + _a2 * v3, v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2 * v1 - _ic1; _ic2 = 2 * v2 - _ic2;
        lp = v2; bp = v1; hp = v0 - _k * v1 - v2;
    }
    public void Clear() { _ic1 = _ic2 = 0; }
}

/// <summary>One-pole DC blocker (about 3.5 Hz).</summary>
internal sealed class DcBlock
{
    private float _x1, _y1;
    public float Run(float x) { float y = x - _x1 + .9995f * _y1; _x1 = x; _y1 = y; return y; }
    public void Clear() { _x1 = _y1 = 0; }
}

internal struct Rng
{
    private uint _s;
    public Rng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }
    public float Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s / (float)uint.MaxValue * 2f - 1f; }
    public float Next01() => Next() * .5f + .5f;
}

/// <summary>Mono delay line with linear-interpolated fractional reads.</summary>
internal sealed class DelayLine
{
    private readonly float[] _b; private int _w;
    public DelayLine(int frames) { _b = new float[frames]; }
    public int Length => _b.Length;
    public void Write(float v) { _b[_w] = v; if (++_w >= _b.Length) _w = 0; }
    /// <summary>Sample written <paramref name="d"/> writes ago (d >= 1).</summary>
    public float Read(float d)
    {
        d = Math.Clamp(d, 1f, _b.Length - 2);
        float rp = _w - d; if (rp < 0) rp += _b.Length;
        int i0 = (int)rp; if (i0 >= _b.Length) i0 -= _b.Length;     // float rounding can land exactly on Length
        float f = rp - (int)rp; int i1 = i0 + 1; if (i1 >= _b.Length) i1 = 0;
        return _b[i0] + (_b[i1] - _b[i0]) * f;
    }
    public float ReadInt(int d) { int r = _w - d; if (r < 0) r += _b.Length; return _b[r]; }
    public void Clear() { Array.Clear(_b); _w = 0; }
    /// <summary>Refill the whole line with the dry audio that preceded frame <c>ctx.Abs0</c> (one channel).</summary>
    public void Prime(FxContext ctx, int ch)
    {
        _w = 0;
        for (int k = _b.Length; k > 0; k--) Write(ctx.H(ctx.Abs0 - k, ch));
    }
}

/// <summary>Stereo DC blocker for the rack's effect difference signal.</summary>
internal sealed class DcBlock2
{
    public readonly DcBlock L = new(), R = new();
    public void Clear() { L.Clear(); R.Clear(); }
}

/// <summary>8-line feedback delay network reverb with Householder mixing, per-line damping, predelay and two input
/// diffusers. Decorrelated stereo out. All buffers are allocated once.</summary>
internal sealed class Fdn
{
    private const int N = 8;
    private static readonly double[] BaseMs = { 29.7, 37.1, 41.1, 43.7, 53.3, 59.9, 67.7, 73.1 };
    private readonly DelayLine[] _lines = new DelayLine[N];
    private readonly int[] _len = new int[N];
    private readonly float[] _g = new float[N], _lp = new float[N], _o = new float[N];
    private readonly DelayLine _pre = new(4096);
    private readonly DelayLine _ap1 = new(400), _ap2 = new(300);
    private float _damp = .3f, _rt = -1, _size;
    private readonly int _preFrames;
    public double Energy;

    public Fdn(double size, double predelayMs)
    {
        _size = (float)size;
        for (int i = 0; i < N; i++) { _len[i] = (int)(BaseMs[i] * size * .001 * Dsp.Sr); _lines[i] = new DelayLine(_len[i] + 4); }
        _preFrames = Math.Clamp((int)(predelayMs * .001 * Dsp.Sr), 1, 4000);
    }

    /// <summary>RT60 in seconds and damping 0 (bright) .. 0.9 (dark). Cheap to call every control block.</summary>
    public void Set(double rt60, double damp)
    {
        _damp = (float)Math.Clamp(damp, 0, .95);
        if (Math.Abs(rt60 - _rt) < .01) return;
        _rt = (float)rt60;
        for (int i = 0; i < N; i++) _g[i] = (float)Math.Pow(10, -3.0 * _len[i] / (Math.Max(.1, rt60) * Dsp.Sr));
    }

    /// <summary>One frame: mono input in, stereo wet out.</summary>
    public void Run(float input, out float l, out float r)
    {
        _pre.Write(input);
        float x = _pre.ReadInt(_preFrames);
        // two Schroeder allpass diffusers on the input
        float d1 = _ap1.ReadInt(142); float y1 = -.6f * x + d1; _ap1.Write(x + .6f * y1);
        float d2 = _ap2.ReadInt(107); float y2 = -.6f * y1 + d2; _ap2.Write(y1 + .6f * y2);
        float sum = 0;
        for (int i = 0; i < N; i++)
        {
            float v = _lines[i].ReadInt(_len[i]);
            _lp[i] = v + (_lp[i] - v) * _damp;          // damping low-pass in the loop
            _o[i] = _lp[i] * _g[i];
            sum += _o[i];
        }
        float h = sum * (2f / N);
        l = 0; r = 0;
        for (int i = 0; i < N; i++)
        {
            float fb = _o[i] - h + y2 * (i % 2 == 0 ? .5f : -.5f);
            _lines[i].Write(Math.Clamp(fb, -4f, 4f));
            if ((i & 1) == 0) l += _o[i]; else r += _o[i];
        }
        l *= .5f; r *= .5f;
        Energy += l * l + r * r;
    }

    public void Clear()
    {
        foreach (var d in _lines) d.Clear();
        _pre.Clear(); _ap1.Clear(); _ap2.Clear();
        Array.Clear(_lp); Array.Clear(_o); Energy = 0;
    }
}

/// <summary>Two-tap granular pitch shifter on its own delay line (sin² windows that sum to one).</summary>
internal sealed class PitchShifter
{
    private readonly DelayLine _d;
    private readonly float _window;
    private float _lag;
    private const float MinLag = 48;
    public PitchShifter(int windowFrames) { _window = windowFrames; _d = new DelayLine(windowFrames + 256); _lag = MinLag; }
    public void Prime(FxContext ctx, int ch) { _d.Prime(ctx, ch); _lag = MinLag; }
    /// <summary>ratio > 1 shifts up, &lt; 1 down.</summary>
    public float Run(float x, float ratio)
    {
        _d.Write(x);
        _lag += 1 - ratio;
        if (_lag < MinLag) _lag += _window; else if (_lag >= MinLag + _window) _lag -= _window;
        float l2 = _lag + _window * .5f; if (l2 >= MinLag + _window) l2 -= _window;
        float p1 = (_lag - MinLag) / _window, p2 = (l2 - MinLag) / _window;
        float w1 = MathF.Sin(MathF.PI * p1), w2 = MathF.Sin(MathF.PI * p2);
        return _d.Read(_lag) * w1 * w1 + _d.Read(l2) * w2 * w2;
    }
    public void Clear() { _d.Clear(); _lag = MinLag; }
}
