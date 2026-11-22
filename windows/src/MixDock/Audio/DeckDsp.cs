using System.Threading;

namespace MixDock.Audio;

public sealed record DeckParameters(double LowDb = 0, double MidDb = 0,
    double HighDb = 0, double Filter = 0, double Trim = 0.85, bool Mute = false,
    bool LowKill = false, bool MidKill = false, bool HighKill = false, bool BassCut = false);

/// <summary>
/// Per-deck EQ / filter / auto-gain / trim. Runs on the render thread; the UI swaps immutable parameters atomically.
/// AUTO MIX adds its own low-shelf cut (bass swap) and filter offset (sweeps) on top of the user's settings.
/// </summary>
public sealed class DeckDsp
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    /// <summary>Loudness target for auto gain (RMS, dBFS). About -14 LUFS for typical club music.</summary>
    public const double AutoGainTargetDb = -16;
    private readonly Biquad[,] _eq = new Biquad[2, 3];
    private readonly Biquad[] _filter = [new(), new()];
    private DeckParameters _desired = new();
    private DeckParameters? _configured;
    private double _confAutoLow, _confAutoMid, _confAutoHigh;
    private double _autoLow, _autoMid, _autoHigh, _autoFilter;
    private double _smoothGain = 0.85;
    private volatile float _peak;
    // auto gain state
    private double _agDb, _rmsAcc; private int _rmsN;
    private volatile bool _autoGain;          // off by default; the engine turns it on from settings
    private double _agSmooth = 1;

    /// <summary>Extra low-shelf gain (dB, <= 0) applied by AUTO MIX for the bass swap.</summary>
    public double AutoLowDb { get => Volatile.Read(ref _autoLow); set => Volatile.Write(ref _autoLow, Math.Clamp(value, -60, 0)); }
    internal double AutoMidDb { get => _autoMid; set => _autoMid = Math.Clamp(value, -60, 0); }
    internal double AutoHighDb { get => _autoHigh; set => _autoHigh = Math.Clamp(value, -60, 0); }
    /// <summary>Filter offset (-1 low-pass .. +1 high-pass) applied by AUTO MIX sweeps, added to the user's filter.</summary>
    public double AutoFilter { get => Volatile.Read(ref _autoFilter); set => Volatile.Write(ref _autoFilter, Math.Clamp(value, -1, 1)); }
    /// <summary>Slow loudness levelling so two sources sit at the same volume (-12..+6 dB).</summary>
    public bool AutoGain { get => _autoGain; set => _autoGain = value; }
    /// <summary>Current auto-gain correction in dB.</summary>
    public double AutoGainDb => Volatile.Read(ref _agDb);
    public float Peak => _peak;
    /// <summary>Called by the UI timer: capture sends no packets during silence, so let the meter fall.</summary>
    public void DecayPeak() => _peak *= 0.8f;
    public DeckParameters Parameters
    {
        get => Volatile.Read(ref _desired);
        set => Volatile.Write(ref _desired, value);
    }
    public DeckDsp()
    {
        for (var ch = 0; ch < 2; ch++)
            for (var i = 0; i < 3; i++) _eq[ch, i] = new Biquad();
    }

    /// <summary>Forget the loudness history (new source on this deck).</summary>
    public void ResetAutoGain() { Volatile.Write(ref _agDb, 0); _agSmooth = 1; _rmsAcc = 0; _rmsN = 0; }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < input.Length) throw new ArgumentException("Destination too small");
        var p = Parameters;
        double autoLow = AutoLowDb, autoFilter = AutoFilter;
        double autoMid = _autoMid, autoHigh = _autoHigh;
        double target = Math.Clamp(p.Filter + autoFilter, -1, 1);
        if (_configured != p || Math.Abs(autoLow - _confAutoLow) > 0.25 ||
            Math.Abs(autoMid - _confAutoMid) > .25 || Math.Abs(autoHigh - _confAutoHigh) > .25)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                _eq[ch, 0].Shelf(SampleRate, 120, p.LowKill ? -96 : Math.Max(-96, p.LowDb + autoLow - (p.BassCut ? 24 : 0)), false);
                _eq[ch, 1].Peak(SampleRate, 1050, 0.8, p.MidKill ? -96 : p.MidDb + autoMid);
                _eq[ch, 2].Shelf(SampleRate, 7500, p.HighKill ? -96 : p.HighDb + autoHigh, true);
            }
            _configured = p; _confAutoLow = autoLow; _confAutoMid = autoMid; _confAutoHigh = autoHigh;
        }
        bool ag = _autoGain;
        double agTarget = ag ? Math.Pow(10, Volatile.Read(ref _agDb) / 20) : 1;
        double localPeak = 0;
        // Filter runs on a continuous (double) value that glides toward the target in 64-frame steps, with
        // coefficients rebuilt per step: fractional pad sweeps and fine creeps are heard without zipper steps.
        for (int start = 0; start < input.Length; start += FilterStep * 2)
        {
            if (_filterNow != target || _filterBuilt != _filterNow) StepFilter(target);
            bool useFilter = Math.Abs(_filterNow) > 0.002;
            int end = Math.Min(input.Length, start + FilterStep * 2);
            for (int i = start; i + 1 < end; i += 2)
            {
                // ~8ms smoothing prevents abrupt trim/mute changes from hard clicks.
                _smoothGain += ((p.Mute ? 0 : p.Trim) - _smoothGain) * 0.0028;
                _agSmooth += (agTarget - _agSmooth) * 0.0005;
                double l0 = input[i], r0 = input[i + 1];
                if (!double.IsFinite(l0)) l0 = 0; if (!double.IsFinite(r0)) r0 = 0;
                _rmsAcc += 0.5 * (l0 * l0 + r0 * r0); _rmsN++;
                for (int ch = 0; ch < 2; ch++)
                {
                    double x = ch == 0 ? l0 : r0;
                    for (int band = 0; band < 3; band++) x = _eq[ch, band].Process((float)x);
                    if (useFilter) x = _filter[ch].Process((float)x);
                    float v = (float)(x * _agSmooth * _smoothGain);
                    output[i + ch] = v;
                    localPeak = Math.Max(localPeak, Math.Abs(v));
                }
            }
        }
        if (_rmsN >= SampleRate / 10) UpdateAutoGain();
        _peak = (float)Math.Max(localPeak, Peak * 0.89);
    }

    private const int FilterStep = 64;
    private static readonly double FilterGlide = 1 - Math.Exp(-FilterStep / (SampleRate * 0.010));   // ~10 ms glide
    private double _filterNow, _filterBuilt = double.NaN;
    /// <summary>Filter value actually applied right now (-1..1), after the glide.</summary>
    public double FilterNow => _filterNow;

    private void StepFilter(double target)
    {
        double next = _filterNow + (target - _filterNow) * FilterGlide;
        if (Math.Abs(target - next) < 1e-6) next = target;
        if (Math.Sign(next) != Math.Sign(_filterNow)) { _filter[0].Reset(); _filter[1].Reset(); }
        _filterNow = next;
        if (Math.Abs(next) <= 0.002 || next == _filterBuilt) return;
        bool high = next > 0;
        double fraction = Math.Clamp(Math.Abs(next), 0, 1);
        double min = high ? 40 : 180, max = high ? 8000 : 18000;
        double freq = high ? min * Math.Pow(max / min, fraction) : max * Math.Pow(min / max, fraction);
        _filter[0].Pass(SampleRate, freq, high); _filter[1].Pass(SampleRate, freq, high);
        _filterBuilt = next;
    }

    /// <summary>Every 100 ms: move the correction toward the target, ~3 s time constant; frozen in silence/breakdowns.</summary>
    private void UpdateAutoGain()
    {
        double rmsDb = 10 * Math.Log10(Math.Max(1e-12, _rmsAcc / _rmsN));
        _rmsAcc = 0; _rmsN = 0;
        if (rmsDb < -45) return;                                  // silence or a near-silent breakdown: hold
        double want = Math.Clamp(AutoGainTargetDb - rmsDb, -12, 6);
        double cur = Volatile.Read(ref _agDb);
        double step = (want - cur) * 0.033;                       // 100 ms / 3 s
        Volatile.Write(ref _agDb, cur + Math.Clamp(step, -0.6, 0.6));
    }
}
