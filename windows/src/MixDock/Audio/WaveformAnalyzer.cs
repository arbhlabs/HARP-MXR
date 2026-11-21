namespace MixDock.Audio;

/// <summary>One 10 ms slice of a waveform: peak level per frequency band (0..1).</summary>
public readonly record struct WaveColumn(float Low, float Mid, float High, float Peak);

/// <summary>
/// Splits audio into low (&lt;200 Hz), mid (200 Hz..2.5 kHz) and high (&gt;2.5 kHz) bands with cheap one-pole
/// filters (low: 2-pole LP; mid: 1-pole LP + 2-pole HP; high: 1-pole HP) and keeps one <see cref="WaveColumn"/> per 441 frames (10 ms). Single writer (audio thread),
/// many readers (UI). Readers never block the writer: columns are plain structs in a ring indexed by an
/// absolute column number, published with a volatile counter.
/// </summary>
public sealed class WaveformAnalyzer
{
    public const int FramesPerColumn = 441;          // 10 ms at 44.1 kHz
    public const int ColumnsPerSecond = 100;
    private readonly WaveColumn[] _ring;
    private long _count;                              // columns published
    private int _n;
    private float _lp1, _lp2, _lpHi, _hp1, _hp2, _mLow, _mMid, _mHigh, _mPeak;

    public WaveformAnalyzer(int historySeconds = 180) { _ring = new WaveColumn[historySeconds * ColumnsPerSecond]; }

    public int Capacity => _ring.Length;
    /// <summary>Total columns written so far (the newest is Count-1).</summary>
    public long Count => Volatile.Read(ref _count);

    public void Reset() { Volatile.Write(ref _count, 0); _n = 0; _lp1 = _lp2 = _lpHi = _hp1 = _hp2 = _mLow = _mMid = _mHigh = _mPeak = 0; }

    /// <summary>Returns false if the column is not written yet or has been overwritten.</summary>
    public bool TryGet(long index, out WaveColumn c)
    {
        long count = Count;
        if (index < 0 || index >= count || index < count - _ring.Length + 2) { c = default; return false; }
        c = _ring[index % _ring.Length];
        return true;
    }

    /// <summary>Mean band energy over the last <paramref name="columns"/> columns (for energy tracking).</summary>
    public (double low, double mid, double high, double all) Recent(int columns)
    {
        long end = Count; double l = 0, m = 0, h = 0, a = 0; int k = 0;
        for (long i = Math.Max(0, end - columns); i < end; i++)
            if (TryGet(i, out var c)) { l += c.Low; m += c.Mid; h += c.High; a += c.Peak; k++; }
        return k == 0 ? (0, 0, 0, 0) : (l / k, m / k, h / k, a / k);
    }

    public void Feed(ReadOnlySpan<float> stereo)
    {
        // one-pole coefficients at 44.1 kHz: 200 Hz -> 0.0281, 2.5 kHz -> 0.3, used twice for 12 dB/oct lows
        const float aLow = 0.0281f, aHigh = 0.30f;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float x = 0.5f * (stereo[i] + stereo[i + 1]);
            if (!float.IsFinite(x)) x = 0;
            _lp1 += aLow * (x - _lp1); _lp2 += aLow * (_lp1 - _lp2);
            _lpHi += aHigh * (x - _lpHi);
            // mid = (x low-passed at 2.5 kHz) high-passed twice at 200 Hz; subtracting the phase-shifted low band
            // instead would leak about half of a 60 Hz kick into the mid band
            float m1 = _lpHi - (_hp1 += aLow * (_lpHi - _hp1));
            float mid = m1 - (_hp2 += aLow * (m1 - _hp2));
            float low = _lp2, high = x - _lpHi;
            float al = Math.Abs(low), am = Math.Abs(mid), ah = Math.Abs(high), ax = Math.Abs(x);
            if (al > _mLow) _mLow = al; if (am > _mMid) _mMid = am; if (ah > _mHigh) _mHigh = ah; if (ax > _mPeak) _mPeak = ax;
            if (++_n < FramesPerColumn) continue;
            long idx = _count;
            _ring[idx % _ring.Length] = new WaveColumn(Math.Min(1, _mLow), Math.Min(1, _mMid), Math.Min(1, _mHigh), Math.Min(1, _mPeak));
            Volatile.Write(ref _count, idx + 1);
            _n = 0; _mLow = _mMid = _mHigh = _mPeak = 0;
        }
    }

    /// <summary>Analyse a whole buffer offline (file decks: full-track waveform and overview).</summary>
    public static WaveColumn[] AnalyzeAll(ReadOnlySpan<float> stereo)
    {
        var a = new WaveformAnalyzer(Math.Max(1, stereo.Length / 2 / 44100 + 2));
        a.Feed(stereo);
        return a.ToArray();
    }

    /// <summary>All columns still held, oldest first (offline analysis sizes the ring to the whole track).</summary>
    public WaveColumn[] ToArray()
    {
        long end = Count, start = Math.Max(0, end - _ring.Length + 2);
        var cols = new WaveColumn[end - start];
        for (long i = start; i < end; i++) TryGet(i, out cols[i - start]);
        return cols;
    }
}

/// <summary>
/// Fixed-tempo beat grid in track seconds: beat k is at <see cref="FirstBeatSec"/> + k * period.
/// <see cref="Estimate"/> works on 10 ms <see cref="WaveColumn"/>s: onsets from rises in the low band and the
/// full-band peak, an autocorrelation for the coarse tempo (80..176 BPM), then a fine period/phase search
/// (Fourier comb over the whole track) so the grid does not drift over a long song.
/// </summary>
public readonly record struct BeatGrid(double Bpm, double FirstBeatSec)
{
    public bool Valid => Bpm > 0;
    public double PeriodSec => Bpm > 0 ? 60.0 / Bpm : 0;

    /// <summary>Beats in [t0, t1] as (time, beat index); index 0 is <see cref="FirstBeatSec"/>, index % 4 == 0 starts a bar.</summary>
    public IEnumerable<(double t, long k)> Beats(double t0, double t1)
    {
        if (!Valid || t1 < t0) yield break;
        double p = PeriodSec;
        for (long k = (long)Math.Ceiling((t0 - FirstBeatSec) / p); ; k++)
        {
            double t = FirstBeatSec + k * p;
            if (t > t1) yield break;
            yield return (t, k);
        }
    }

    public static float[] Onsets(ReadOnlySpan<WaveColumn> cols)
    {
        var o = new float[cols.Length];
        for (int i = 1; i < cols.Length; i++)
            o[i] = Math.Max(0, cols[i].Low - cols[i - 1].Low) * 2 + Math.Max(0, cols[i].Peak - cols[i - 1].Peak);
        return o;
    }

    public static BeatGrid Estimate(ReadOnlySpan<WaveColumn> cols)
    {
        const double ColSec = 1.0 / WaveformAnalyzer.ColumnsPerSecond;
        int n = cols.Length;
        if (n < 400) return default;                                     // need ~4 s of audio
        var o = Onsets(cols);
        double mean = 0; foreach (var v in o) mean += v; mean /= n;
        double energy = 0; foreach (var v in o) energy += (v - mean) * (v - mean);
        if (energy < 1e-9) return default;
        // coarse: integer lag 34..75 columns = 176..80 BPM
        var r = new double[81];
        for (int lag = 30; lag <= 80; lag++)
        {
            double s = 0;
            for (int i = lag; i < n; i++) s += (o[i] - mean) * (o[i - lag] - mean);
            r[lag] = s / (n - lag);
        }
        int bestLag = 0; double bestR = 0;
        for (int lag = 34; lag <= 75; lag++) if (r[lag] > bestR) { bestR = r[lag]; bestLag = lag; }
        if (bestLag == 0) return default;
        double center = bestLag;
        // octave check: a fast track (e.g. 174 BPM drum & bass) also peaks at twice its period; take the faster
        // tempo when the half lag is nearly as strong
        if (bestLag >= 66)
        {
            double half = bestLag / 2.0;
            // an odd lag's half falls between two columns, so its impulses split across both neighbours
            double rh = bestLag % 2 == 0 ? r[bestLag / 2] : r[bestLag / 2] + r[bestLag / 2 + 1];
            if (rh > 0.6 * bestR) center = half;
        }
        // fine: period with the strongest comb response; its phase gives the first beat
        double bestP = center, bestMag = -1, bestAng = 0;
        for (double p = center - 1.0; p <= center + 1.0; p += 0.005)
        {
            double re = 0, im = 0, w = 2 * Math.PI / p;
            for (int i = 0; i < n; i++)
            {
                double v = o[i] - mean; if (v <= 0) continue;
                re += v * Math.Cos(w * i); im += v * Math.Sin(w * i);
            }
            double mag = re * re + im * im;
            if (mag > bestMag) { bestMag = mag; bestP = p; bestAng = Math.Atan2(im, re); }
        }
        double first = (bestAng < 0 ? bestAng + 2 * Math.PI : bestAng) / (2 * Math.PI) * bestP;   // columns, in [0, P)
        return new BeatGrid(60.0 / (bestP * ColSec), first * ColSec);
    }
}
