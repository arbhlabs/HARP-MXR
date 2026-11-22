namespace MixDock.Audio;

/// <summary>
/// Per-deck stick FX rack (render thread). One pre-allocated instance of every <see cref="StickFx"/> effect runs in a
/// fixed serial chain; each effect has its own smoothed drive (stick distance) and mix (direction share), so moving
/// between directions, switching sets or letting go is always a glide, never a switch. An effect whose drive and mix
/// are zero and whose tail has died is skipped entirely, so the rack is bit-exact dry at rest.
/// After the inserts a safety guard holds the output within +1 dB of the dry peak envelope (instant attack, 80 ms
/// release); the two generators (noise riser, sub drop) are added after it with fixed level caps. The master output
/// still passes through the mixer's own limiter.
/// UI → audio: an immutable <see cref="Controls"/> snapshot (Volatile). No locks, logging, allocation or exceptions here.
/// </summary>
public sealed class FxRack
{
    /// <summary>Targets for every effect (index = (int)StickFx). Treat as immutable once published.</summary>
    public sealed class Controls
    {
        public readonly float[] Drive = new float[FxCatalog.Count], Mix = new float[FxCatalog.Count], Param = new float[FxCatalog.Count];
        public double Bpm, Phase = double.NaN, SmoothMs = 60;
        public long Stamp;
        public bool Any() { for (int i = 0; i < Drive.Length; i++) if (Drive[i] > 0 || Mix[i] > 0) return true; return false; }
    }

    public const int Chunk = 256;
    public const int HistFrames = Dsp.Sr * 4;
    public const float Ceiling = 1.122f;      // +1 dB over the dry peak envelope
    private Controls _controls = new();
    public Controls State { get => Volatile.Read(ref _controls); set => Volatile.Write(ref _controls, value ?? new Controls()); }

    private readonly FxUnit[] _units = new FxUnit[FxCatalog.Count];
    private readonly bool[] _generator = new bool[FxCatalog.Count];
    private readonly float[] _drv = new float[FxCatalog.Count], _mix = new float[FxCatalog.Count];
    private readonly bool[] _live = new bool[FxCatalog.Count];
    private readonly float[] _kd = new float[FxCatalog.Count], _km = new float[FxCatalog.Count];
    private readonly float[] _d = new float[Chunk], _m = new float[Chunk], _dry = new float[Chunk * 2];
    private readonly float[] _hist = new float[HistFrames * 2];
    private readonly FxContext _ctx = new();
    private readonly DcBlock2 _dc = new();
    private long _abs; private int _hw;
    private double _beatPos, _smoothMs = -1;
    private Controls? _last;
    // double: a float gain creeping back to 1 stalls a few ulps short (2.8e-8 steps vanish at 1.0) and never bypasses
    private double _env, _gain = 1;
    private readonly double _envRelease = Math.Exp(-1.0 / (1.5 * Dsp.Sr)), _gainRelease = FxCurves.Coef(80);
    private volatile bool _kill;
    private bool _reset;

    /// <summary>Lowest guard gain since <see cref="ResetStats"/> (1 = the guard never acted). For tests and diagnostics.</summary>
    public float GuardMinGain { get; private set; } = 1;
    public void ResetStats() => GuardMinGain = 1;
    /// <summary>Number of effects processed in the last block (0 = bit-exact bypass).</summary>
    public int LiveCount { get; private set; }

    public FxRack()
    {
        for (int i = 0; i < _units.Length; i++)
        {
            _units[i] = FxUnit.Create((StickFx)i);
            _generator[i] = FxCatalog.All[i].Family == FxFamily.Generator;
        }
        _ctx.Hist = _hist; _ctx.HistFrames = HistFrames;
    }

    /// <summary>Panic: every effect and tail is cleared on the next audio block (safe to call from the UI thread).</summary>
    public void Kill() { State = new Controls(); _kill = true; }

    /// <summary>Smoothed drive / mix of one effect (UI meters; a torn read only shows a slightly stale value).</summary>
    public (float Drive, float Mix) Level(StickFx fx) => (_drv[(int)fx], _mix[(int)fx]);

    private void UpdateCoefficients(double smoothMs)
    {
        _smoothMs = smoothMs;
        for (int i = 0; i < _units.Length; i++)
        {
            var info = FxCatalog.All[i];
            _kd[i] = Dsp.Coef(info.Fast ? 4 : Math.Clamp(smoothMs, 5, 500));
            _km[i] = Dsp.Coef(info.Id == StickFx.Cut ? 1.5 : info.Fast ? 2.5 : 30);
        }
    }

    public void Process(Span<float> deck)
    {
        var c = State;
        if (_kill)
        {
            _kill = false; _reset = true;
        }
        if (_reset)
        {
            _reset = false;
            foreach (var u in _units) u.Reset();
            Array.Clear(_drv); Array.Clear(_mix); Array.Clear(_live); _gain = 1; _dc.Clear();
        }
        double smooth = double.IsFinite(c.SmoothMs) ? c.SmoothMs : 60;
        if (smooth != _smoothMs) UpdateCoefficients(smooth);
        double bpm = double.IsFinite(c.Bpm) && c.Bpm > 0 ? Math.Clamp(c.Bpm, 40, 300) : 120;
        if (!ReferenceEquals(c, _last))
        {
            _last = c;
            if (double.IsFinite(c.Phase))
            {
                double ph = c.Phase + (c.Stamp != 0 ? (System.Diagnostics.Stopwatch.GetTimestamp() - c.Stamp) / (double)System.Diagnostics.Stopwatch.Frequency * bpm / 60 : 0);
                double d = ph - (_beatPos - Math.Floor(_beatPos)); d -= Math.Round(d);
                if (Math.Abs(d) > .01) _beatPos += d;
            }
        }
        double inc = bpm / 60.0 / Dsp.Sr;
        int frames = deck.Length / 2, liveCount = 0;
        for (int off = 0; off < frames; off += Chunk)
        {
            int n = Math.Min(Chunk, frames - off);
            var io = deck.Slice(off * 2, n * 2);
            // dry history (time effects read it) and the dry peak envelope (guard ceiling)
            double env = _env;
            for (int i = 0; i < n; i++)
            {
                float l = io[i * 2], r = io[i * 2 + 1];
                _hist[_hw * 2] = l; _hist[_hw * 2 + 1] = r;
                if (++_hw >= HistFrames) _hw = 0;
            }
            _ctx.Abs0 = _abs; _ctx.Beat0 = _beatPos; _ctx.BeatInc = inc; _ctx.Bpm = bpm; _ctx.FramesPerBeat = Dsp.Sr * 60.0 / bpm;

            bool inserts = false, gens = false;
            for (int u = 0; u < _units.Length; u++)
            {
                bool on = Target(c.Drive, u) > 0 || Target(c.Mix, u) > 0 || _drv[u] > 0 || _mix[u] > 0 || _units[u].TailActive;
                if (!on && _live[u]) _units[u].Disengage();
                else if (on && !_live[u]) _units[u].Engage(_ctx);
                _live[u] = on;
                if (on) { liveCount++; if (_generator[u]) gens = true; else inserts = true; }
            }
            if (!inserts && !gens && _gain >= 1)
            {
                for (int i = 0; i < n; i++) { double p = Math.Max(Math.Abs(io[i * 2]), Math.Abs(io[i * 2 + 1])); env = p > env ? p : env * _envRelease; }
                _env = env; _abs += n; _beatPos += inc * n; _dc.Clear();
                continue;
            }
            io.CopyTo(_dry);
            for (int u = 0; u < _units.Length; u++) if (_live[u] && !_generator[u]) Run(u, io, n, c);
            // effects may not add DC (gating mid-cycle, ring mod on a matching tone, repeated slices): high-pass only
            // what the effects changed, so the dry itself passes untouched
            for (int i = 0; i < n; i++)
            {
                io[i * 2] = _dry[i * 2] + _dc.L.Run(io[i * 2] - _dry[i * 2]);
                io[i * 2 + 1] = _dry[i * 2 + 1] + _dc.R.Run(io[i * 2 + 1] - _dry[i * 2 + 1]);
            }
            // safety guard: output within +1 dB of the dry peak envelope
            double gain = _gain; float minGain = GuardMinGain;
            for (int i = 0; i < n; i++)
            {
                double p = Math.Max(Math.Abs(_dry[i * 2]), Math.Abs(_dry[i * 2 + 1]));
                env = p > env ? p : env * _envRelease;
                double y = Math.Max(Math.Abs(io[i * 2]), Math.Abs(io[i * 2 + 1])), ceil = Math.Max(env, 1e-6) * Ceiling;
                double t = y > ceil ? ceil / y : 1;
                if (t < gain) gain = t; else { gain += (t - gain) * _gainRelease; if (gain > 1 - 1e-7) gain = 1; }
                if (gain < 1) { io[i * 2] = (float)(io[i * 2] * gain); io[i * 2 + 1] = (float)(io[i * 2 + 1] * gain); if (gain < minGain) minGain = (float)gain; }
            }
            _env = env; _gain = gain; GuardMinGain = minGain;
            if (gens) for (int u = 0; u < _units.Length; u++) if (_live[u] && _generator[u]) Run(u, io, n, c);
            for (int i = 0; i < n * 2; i++)
                if (!float.IsFinite(io[i])) { io[i] = _dry[i]; _reset = true; }
            _abs += n; _beatPos += inc * n;
        }
        LiveCount = liveCount;
    }

    private static float Target(float[] a, int u) { float v = a[u]; return float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0; }

    private void Run(int u, Span<float> io, int n, Controls c)
    {
        float td = Target(c.Drive, u), tm = Target(c.Mix, u), kd = _kd[u], km = _km[u];
        float dv = _drv[u], mv = _mix[u];
        for (int i = 0; i < n; i++)
        {
            dv += (td - dv) * kd; mv += (tm - mv) * km;
            _d[i] = dv; _m[i] = mv;
        }
        if (td == 0 && dv < 1e-5f) dv = 0;
        if (tm == 0 && mv < 1e-5f) mv = 0;
        _drv[u] = dv; _mix[u] = mv;
        float p = c.Param[u];
        _units[u].Process(io, n, _d, _m, float.IsFinite(p) ? p : 0, _ctx);
    }
}
