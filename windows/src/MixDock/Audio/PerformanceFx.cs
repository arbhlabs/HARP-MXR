namespace MixDock.Audio;

/// <summary>
/// Per-deck right-stick effects, render thread only. Reverb (Freeverb-style) is a send whose tail keeps ringing after the
/// stick is released; flanger, phaser, bitcrusher and trance gate are inserts crossfaded with the dry signal. Every
/// parameter moves through one-pole ramps (60 ms stick amount, 5 ms effect switching, ~20 ms parameters), so nothing
/// clicks. Allocation-free after construction. Echo is the existing EchoFx (driven by the same pad).
/// </summary>
public sealed class PerformanceFx
{
    public sealed record Controls(PadFx Effect = PadFx.Space, double Amount = 0, double X = .5, double Y = .5,
        double Bpm = 0, double Phase = double.NaN, bool Freeze = false, double SmoothMs = 60, long Stamp = 0);

    private Controls _controls = new();
    public Controls State { get => Volatile.Read(ref _controls); set => Volatile.Write(ref _controls, value); }
    /// <summary>The stick FX rack (sets of four, 29 effects). Runs first on the deck, before the classic XY-pad effects.</summary>
    public readonly FxRack Rack = new();

    private const int Sr = FxCurves.SampleRate;
    // Freeverb tunings (44.1 kHz), right channel spread by 23 samples
    private static readonly int[] CombLen = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllLen = { 556, 441, 341, 225 };
    private const int Spread = 23;
    private readonly float[][,] _comb = new float[2][,];
    private readonly int[,] _combPos = new int[2, 8];
    private readonly float[,] _combLp = new float[2, 8];
    private readonly float[][][] _all = new float[2][][];
    private readonly int[,] _allPos = new int[2, 4];

    private const int FlLen = 2048;
    private readonly float[,] _fl = new float[2, FlLen];
    private int _flW;
    private readonly float[,] _apZ = new float[2, 6];
    private readonly float[] _apFb = new float[2];
    private readonly float[] _held = new float[2];
    private double _crushAcc;

    private const int FxCount = 7;
    private double _amt, _x = .5, _y = .5, _revFb = .84, _revDamp = .3, _lfoBeats = 4, _gateEnv = 1;
    private readonly double[] _w = new double[FxCount];       // per-effect weight (5 ms ramps on switch)
    private double _revSend, _lfo, _beatPos, _apCoef;
    private int _apTick;
    private double _tailEnergy;
    private Controls? _lastControls;

    public PerformanceFx()
    {
        for (int c = 0; c < 2; c++)
        {
            _comb[c] = new float[8, 1700];
            _all[c] = new float[4][];
            for (int i = 0; i < 4; i++) _all[c][i] = new float[AllLen[i] + Spread];
        }
    }

    /// <summary>True while anything audible may remain (reverb tail, active effect). Idle cost is one branch.</summary>
    public bool Active => _amt > 1e-4 || _tailEnergy > 1e-9 || State.Amount > 0 || State.Freeze;

    /// <summary>Linear level of the reverb tail, for tests/meters.</summary>
    public double TailEnergy => _tailEnergy;

    public void Clear()
    {
        for (int c = 0; c < 2; c++)
        {
            Array.Clear(_comb[c]);
            foreach (var a in _all[c]) Array.Clear(a);
        }
        Array.Clear(_combLp); Array.Clear(_fl); Array.Clear(_apZ); Array.Clear(_apFb); Array.Clear(_held);
        _amt = 0; _revSend = 0; _tailEnergy = 0; Array.Clear(_w);
        State = new Controls();
        Rack.Kill();
    }

    /// <summary>In-place insert effects on <paramref name="deck"/>. The reverb is fed by the post-fader deck
    /// (<paramref name="fader"/>), so its tail rings out after the deck is cut; the tail is added to <paramref name="send"/>.</summary>
    public void Process(Span<float> deck, Span<float> send, float fader, double autoReverb = 0)
    {
        Rack.Process(deck);
        var c = State;
        if (!Active && autoReverb <= 0) return;
        double target = double.IsFinite(c.Amount) ? Math.Clamp(c.Amount, 0, 1) : 0;
        double xT = double.IsFinite(c.X) ? Math.Clamp(c.X, 0, 1) : .5, yT = double.IsFinite(c.Y) ? Math.Clamp(c.Y, 0, 1) : .5;
        double bpm = double.IsFinite(c.Bpm) && c.Bpm > 0 ? Math.Clamp(c.Bpm, 40, 300) : 120;
        if (!ReferenceEquals(c, _lastControls))
        {
            _lastControls = c;
            if (double.IsFinite(c.Phase))
            {
                // new snapshot: age the phase by the time since the UI measured it, re-lock only when it really drifted (> ~5 ms)
                double ph = c.Phase + (c.Stamp != 0 ? (System.Diagnostics.Stopwatch.GetTimestamp() - c.Stamp) / (double)System.Diagnostics.Stopwatch.Frequency * bpm / 60 : 0);
                double d = ph - (_beatPos - Math.Floor(_beatPos)); d -= Math.Round(d);
                if (Math.Abs(d) > .01) _beatPos += d;
            }
        }
        double kAmt = FxCurves.Coef(c.SmoothMs), kPar = FxCurves.Coef(20), kSw = FxCurves.Coef(5);
        var (revShare, _) = FxCurves.SpaceMix(yT);
        double revTarget = c.Effect == PadFx.Reverb ? 1 : c.Effect == PadFx.Space ? revShare : 0;
        int e = (int)c.Effect;
        double tailAcc = 0;
        for (int i = 0; i + 1 < deck.Length; i += 2)
        {
            _amt += (target - _amt) * kAmt;
            _x += (xT - _x) * kPar; _y += (yT - _y) * kPar;
            for (int k = 0; k < FxCount; k++) _w[k] += ((k == e ? 1.0 : 0.0) - _w[k]) * kSw;
            _revSend += (revTarget * _amt - _revSend) * kSw;
            _revFb += ((c.Freeze ? 1.0 : FxCurves.ReverbFeedback(_x)) - _revFb) * kPar;
            _revDamp += ((c.Freeze ? 0.0 : FxCurves.ReverbDamp(_x)) - _revDamp) * kPar;
            _lfoBeats += (FxCurves.LfoBeats(_x) - _lfoBeats) * kPar;
            _lfo += bpm / 60.0 / (_lfoBeats * Sr); _lfo -= Math.Floor(_lfo);
            _beatPos += bpm / 60.0 / Sr;
            double l = deck[i], r = deck[i + 1];

            // reverb send: frozen = no new input, feedback 1, no damping (the tail loops until released)
            double inGain = c.Freeze ? 0 : Math.Max(_revSend, autoReverb) * fader;
            float rin = (float)((l + r) * .5 * inGain * .03);
            double wl = 0, wr = 0;
            for (int k = 0; k < 8; k++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int len = CombLen[k] + (ch == 1 ? Spread : 0);
                    int p = _combPos[ch, k]; float y = _comb[ch][k, p];
                    _combLp[ch, k] = (float)(y * (1 - _revDamp) + _combLp[ch, k] * _revDamp);
                    float v = rin + _combLp[ch, k] * (float)_revFb;
                    _comb[ch][k, p] = Math.Clamp(v, -8f, 8f);
                    if (++p >= len) p = 0;
                    _combPos[ch, k] = p;
                    if (ch == 0) wl += y; else wr += y;
                }
            }
            for (int ch = 0; ch < 2; ch++)
            {
                double s = ch == 0 ? wl : wr;
                for (int k = 0; k < 4; k++)
                {
                    int len = AllLen[k] + (ch == 1 ? Spread : 0);
                    int p = _allPos[ch, k]; float buf = _all[ch][k][p];
                    float o = (float)(-s + buf);
                    _all[ch][k][p] = (float)(s + buf * .5);
                    if (++p >= len) p = 0;
                    _allPos[ch, k] = p;
                    s = o;
                }
                float outv = (float)s * 3f;
                if (float.IsFinite(outv)) { send[i + ch] += outv; tailAcc += outv * outv; }
            }

            // inserts (skipped while their weight is ~0)
            for (int ch = 0; ch < 2; ch++)
            {
                double x = ch == 0 ? l : r, dry = x;
                double wF = _w[(int)PadFx.Flanger] * _amt;
                if (wF > 1e-4)
                {
                    var (cen, sweep) = FxCurves.FlangerSweep(_y);
                    double ms = cen + sweep * .5 * Math.Sin(_lfo * 2 * Math.PI + (ch == 1 ? .5 : 0));
                    double rd = _flW - Math.Clamp(ms * .001 * Sr, 2, FlLen - 4);
                    if (rd < 0) rd += FlLen;
                    int i0 = (int)rd, i1 = (i0 + 1) % FlLen;
                    float fr = (float)(rd - i0);
                    float dl = _fl[ch, i0] * (1 - fr) + _fl[ch, i1] * fr;
                    _fl[ch, _flW] = (float)Math.Clamp(x + dl * FxCurves.FlangerFeedback(_y), -4, 4);
                    x += ((x * .6 + dl * .6) - x) * wF;
                }
                else _fl[ch, _flW] = (float)x;
                double wP = _w[(int)PadFx.Phaser] * _amt;
                if (wP > 1e-4)
                {
                    if (ch == 0 && (_apTick++ & 7) == 0)
                    {
                        double hz = FxCurves.PhaserHz(.5 - .5 * Math.Cos(_lfo * 2 * Math.PI), _y);
                        double t = Math.Tan(Math.PI * Math.Min(hz, 9000) / Sr);
                        _apCoef = (t - 1) / (t + 1);
                    }
                    double v = x + _apFb[ch] * (.35 + .35 * _y);
                    for (int k = 0; k < 6; k++)
                    {
                        double z = _apZ[ch, k], o = _apCoef * v + z;
                        _apZ[ch, k] = (float)(v - _apCoef * o);
                        v = o;
                    }
                    _apFb[ch] = (float)Math.Clamp(v, -4, 4);
                    x += ((x + v) * .5 - x) * wP;
                }
                double wC = _w[(int)PadFx.Crush] * _amt;
                if (wC > 1e-4)
                {
                    double div = FxCurves.CrushDivisor(_x);
                    if (ch == 0) _crushAcc += 1;
                    if (_crushAcc >= div)
                    {
                        if (ch == 1) _crushAcc -= div;
                        double lv = Math.Pow(2, FxCurves.CrushBits(_y) - 1);
                        _held[ch] = (float)(Math.Round(x * lv) / lv);
                    }
                    x += (_held[ch] - x) * wC;
                }
                double wG = _w[(int)PadFx.Gate] * _amt;
                if (wG > 1e-4)
                {
                    if (ch == 0)
                    {
                        double open = FxCurves.GateOpen(_beatPos, FxCurves.GateDivision(_x), FxCurves.GateDuty(_y));
                        _gateEnv += (open - _gateEnv) * (open > _gateEnv ? FxCurves.Coef(1.5) : FxCurves.Coef(5));
                    }
                    x += (x * _gateEnv - x) * wG;
                }
                float o2 = (float)x;
                deck[i + ch] = float.IsFinite(o2) ? o2 : (float)dry;
            }
            _flW = (_flW + 1) % FlLen;
        }
        _tailEnergy = tailAcc / Math.Max(1, deck.Length);
    }
}
