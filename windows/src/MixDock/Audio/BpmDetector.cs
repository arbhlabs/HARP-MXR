namespace MixDock.Audio;

/// <summary>
/// Immutable beat-grid snapshot in ABSOLUTE input frames (44.1 kHz frames since the stream started, the same index
/// <see cref="SampleHistory"/> uses). <see cref="OriginFrame"/> is a beat, and a downbeat when <see cref="DownKnown"/>.
/// </summary>
public sealed record TempoGrid(double Bpm, double PeriodFrames, double OriginFrame, bool DownKnown, double Confidence, int Version, double PhraseStartFrame)
{
    /// <summary>Beats since the origin beat at an input frame (fractional, may be negative).</summary>
    public double BeatPos(double frame) => (frame - OriginFrame) / PeriodFrames;
    /// <summary>0..1 inside the beat, 0 = on the beat.</summary>
    public double Phase(double frame) { double b = BeatPos(frame); return b - Math.Floor(b); }
    /// <summary>Beats since the last downbeat, 0..4 (arbitrary bar position when the downbeat is not known yet).</summary>
    public double BarBeats(double frame) { double b = BeatPos(frame) % 4; return b < 0 ? b + 4 : b; }
    /// <summary>Whole bars since the phrase start (can be negative before it).</summary>
    public long BarsSincePhrase(double frame) => double.IsNaN(PhraseStartFrame) ? 0 : (long)Math.Floor((frame - PhraseStartFrame) / (4 * PeriodFrames) + 1e-9);
    /// <summary>Bar number inside a phrase of <paramref name="phraseBars"/> bars (0 = first bar of the phrase).</summary>
    public int BarInPhrase(double frame, int phraseBars) { long b = BarsSincePhrase(frame) % phraseBars; return (int)(b < 0 ? b + phraseBars : b); }
}

/// <summary>
/// Rolling-window tempo + beat-grid estimate from the captured stereo stream (44.1 kHz).
/// 1) Coarse: onset envelope (10 ms hops, low band + full band energy rises) -> autocorrelation over ~15 s -> comb fit.
/// 2) Fine: a 1 ms-hop onset envelope refines the period (0.005 BPM steps) and the beat origin (0.25 ms steps) with a
///    32-beat comb sum, then median/IIR stabilisation. Tempo is reported at 0.01 BPM resolution.
/// 3) Downbeat: accent energy by beat slot (kick / bass weight), with hysteresis; phrase start follows energy jumps.
/// Feed() runs on the capture thread, Analyze() on the pool; <see cref="Grid"/> is a lock-free snapshot for the audio thread.
/// </summary>
public sealed class BpmDetector
{
    private const int Hop = 441;           // 10 ms
    private const int Hist = 1500;         // 15 s of coarse onsets
    private const int MinLag = 30, MaxLag = 100;   // 200..60 BPM
    private const int FineHop = 44;        // ~1 ms
    private const int FineHist = 1 << 15;  // ~32 s of fine onsets
    private const int FineMask = FineHist - 1;
    private readonly float[] _onset = new float[Hist];
    private readonly float[] _snap = new float[Hist];
    private readonly float[] _fine = new float[FineHist], _fineLow = new float[FineHist];
    private readonly float[] _fineSm = new float[FineHist], _fineLowSnap = new float[FineHist];
    private long _snapTotal, _snapFine, _snapFrames;
    private int _analyzing;
    private readonly object _gate = new();
    private long _total, _frames, _fineTotal;
    private int _accN, _fAcc;
    private double _eLow, _eFull, _lp, _prevLow, _prevFull, _fLow, _fFull, _fPrevLow, _fPrevFull;
    private readonly Queue<double> _recent = new();
    private TempoGrid? _grid;
    private double _conf;
    private long _lastAnalysis;
    private int _version;

    // downbeat / phrase state
    private readonly double[] _slotAcc = new double[4];
    private int _slotBeats, _downSlot = -1; private long _lastAccBeat = long.MinValue; private double _anchor = double.NaN;
    private double _phraseStart = double.NaN, _barStart = double.NaN, _barE; private int _barN;
    private readonly Queue<double> _bars = new();
    private int _pendingSwitch; private double _pendingPeriod;

    /// <summary>Run the analysis inline instead of on the pool (offline tests are deterministic).</summary>
    public bool Synchronous { get; set; }

    /// <summary>Detected tempo (0 = unknown), 0.01 BPM resolution.</summary>
    public double Bpm => _grid?.Bpm ?? 0;
    public double Confidence { get { lock (_gate) return _conf; } }
    /// <summary>Latest beat grid (null until locked). Safe to read from any thread.</summary>
    public TempoGrid? Grid => Volatile.Read(ref _grid);
    /// <summary>Frames fed since the start / last reset (the clock the grid is expressed in).</summary>
    public long TotalFrames => Interlocked.Read(ref _frames);

    /// <summary>Fraction (0..1) of the current beat elapsed at the newest fed frame; 0 = on the beat. NaN when unknown.</summary>
    public double Phase { get { var g = Grid; return g == null ? double.NaN : g.Phase(TotalFrames); } }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_onset); Array.Clear(_fine); Array.Clear(_fineLow);
            _total = 0; _frames = 0; _fineTotal = 0; _accN = 0; _fAcc = 0;
            _eLow = _eFull = _lp = _prevLow = _prevFull = _fLow = _fFull = _fPrevLow = _fPrevFull = 0;
            _recent.Clear(); _conf = 0; _lastAnalysis = 0; Volatile.Write(ref _grid, null);
            Array.Clear(_slotAcc); _slotBeats = 0; _downSlot = -1; _lastAccBeat = long.MinValue; _anchor = double.NaN;
            _phraseStart = _barStart = double.NaN; _barE = 0; _barN = 0; _bars.Clear(); _pendingSwitch = 0;
        }
    }

    public void Feed(ReadOnlySpan<float> stereo)
    {
        lock (_gate)
        {
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                double x = 0.5 * (stereo[i] + stereo[i + 1]);
                if (!double.IsFinite(x)) x = 0;
                _lp += 0.03 * (x - _lp);                 // ~200 Hz one-pole low-pass = kick band
                _eLow += _lp * _lp; _eFull += x * x;
                _fLow += Math.Abs(_lp); _fFull += Math.Abs(x);
                _frames++;
                if (++_fAcc >= FineHop)
                {
                    double low = _fLow / FineHop, full = _fFull / FineHop;
                    _fine[_fineTotal & FineMask] = (float)(Math.Max(0, low - _fPrevLow) * 2 + Math.Max(0, full - _fPrevFull));
                    _fineLow[_fineTotal & FineMask] = (float)low;
                    _fineTotal++; _fPrevLow = low; _fPrevFull = full; _fLow = _fFull = 0; _fAcc = 0;
                }
                if (++_accN < Hop) continue;
                double lowC = Math.Sqrt(_eLow / Hop), fullC = Math.Sqrt(_eFull / Hop);
                double onset = Math.Max(0, lowC - _prevLow) * 2 + Math.Max(0, fullC - _prevFull);
                _prevLow = lowC; _prevFull = fullC; _eLow = _eFull = 0; _accN = 0;
                _onset[_total % Hist] = (float)onset;
                _total++;
                TrackBar(fullC);
                if (_total - _lastAnalysis >= 100 && _total >= 500 && Interlocked.CompareExchange(ref _analyzing, 1, 0) == 0)
                {
                    _lastAnalysis = _total;
                    if (Synchronous) { try { Analyze(); } catch { } finally { Volatile.Write(ref _analyzing, 0); } }
                    else ThreadPool.QueueUserWorkItem(_ => { try { Analyze(); } catch { } finally { Volatile.Write(ref _analyzing, 0); } });
                }
            }
        }
    }

    /// <summary>Per-bar energy; a big jump or drop at a bar line starts a new phrase (drops and breakdowns land on phrase boundaries).</summary>
    private void TrackBar(double level)
    {
        var g = _grid;
        if (g == null || !g.DownKnown) { _barStart = double.NaN; return; }
        double bar = 4 * g.PeriodFrames, now = _frames - Hop / 2.0;
        if (double.IsNaN(_barStart)) { _barStart = g.OriginFrame + Math.Floor((now - g.OriginFrame) / bar) * bar; _barE = 0; _barN = 0; return; }
        _barE += level; _barN++;
        if (now < _barStart + bar) return;
        double e = _barN > 0 ? _barE / _barN : 0;
        double ended = _barStart; _barStart += bar; _barE = 0; _barN = 0;
        if (_bars.Count >= 2)
        {
            double prev = _bars.Average(), trend = prev > 1e-4 ? e / prev : 1;
            if ((trend > 1.6 || trend < 0.6) && e + prev > 0.02) { _phraseStart = ended; _bars.Clear(); }
        }
        _bars.Enqueue(e); while (_bars.Count > 4) _bars.Dequeue();
    }

    private float At(long absIndex) => _snap[((absIndex % Hist) + Hist) % Hist];

    private double FineAt(double pos)
    {
        long i0 = (long)Math.Floor(pos); double f = pos - i0;
        if (i0 < _snapFine - FineHist + 2 || i0 + 1 >= _snapFine) return 0;
        return _fineSm[i0 & FineMask] * (1 - f) + _fineSm[(i0 + 1) & FineMask] * f;
    }

    private void Analyze()
    {
        lock (_gate)
        {
            Array.Copy(_onset, _snap, Hist); _snapTotal = _total;
            Array.Copy(_fine, _fineSm, FineHist); Array.Copy(_fineLow, _fineLowSnap, FineHist);
            _snapFine = _fineTotal; _snapFrames = _frames;
        }
        // ---- coarse
        int n = (int)Math.Min(_snapTotal, Hist);
        double mean = 0; for (int i = 0; i < n; i++) mean += At(_snapTotal - 1 - i); mean /= n;
        double r0 = 0; for (int i = 0; i < n; i++) { double v = At(_snapTotal - 1 - i) - mean; r0 += v * v; }
        if (r0 < 1e-12) return;                          // silence: keep last estimate
        var r = new double[MaxLag + 2];
        for (int lag = MinLag - 1; lag <= MaxLag + 1; lag++)
        {
            double s = 0;
            for (int i = lag; i < n; i++) s += (At(_snapTotal - 1 - i) - mean) * (At(_snapTotal - 1 - i + lag) - mean);
            r[lag] = s / r0;
        }
        var prevGrid = _grid;
        int best = -1; double bestScore = 0;
        for (int lag = MinLag; lag <= MaxLag; lag++)
        {
            if (r[lag] < r[lag - 1] || r[lag] < r[lag + 1]) continue;      // local maxima only
            double bpmL = 6000.0 / lag;
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpmL / 120.0) / 0.75, 2));
            // once locked, stay in the same octave (prevents 87 <-> 174 flips on sparse sections)
            if (prevGrid != null)
            {
                double ratio = bpmL / prevGrid.Bpm;
                if (Math.Abs(ratio - 1) < 0.04) prior *= 1.6; else if (Math.Abs(ratio - 2) < 0.08 || Math.Abs(ratio - 0.5) < 0.04) prior *= 0.6;
            }
            double harm = (lag * 2 <= MaxLag + 1 ? Math.Max(0, r[Math.Min(lag * 2, MaxLag + 1)]) * 0.3 : 0);
            double score = (r[lag] + harm) * prior;
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        if (best < 0) return;
        double a = r[best - 1], b = r[best], c = r[best + 1];
        double denom = a - 2 * b + c;
        double lagF = best + (Math.Abs(denom) > 1e-12 ? 0.5 * (a - c) / denom : 0);
        double bpm = 6000.0 / lagF;
        double conf = r[best];
        if (conf < 0.08) return;
        double p0 = 6000.0 / bpm, bestSum = -1, bestP = p0, bestOff = 0;
        for (double p = p0 * 0.985; p <= p0 * 1.015; p += 0.04)
        {
            int beats = (int)Math.Min(24, (n - 2) / p);
            if (beats < 4) continue;
            for (double off = 0; off < p; off += 0.5)
            {
                double sum = 0;
                for (int k = 0; k < beats; k++) sum += Interp(_snapTotal - 1 - (off + k * p));
                if (sum > bestSum + 1e-9) { bestSum = sum; bestP = p; bestOff = off; }
            }
        }
        // ---- fine: 1 ms envelope, period +-0.35 %, latest beat +-18 ms around the coarse estimate
        SmoothFine();
        double pf0 = bestP * Hop / FineHop;
        double tLatest = ((_snapTotal - 1 - bestOff) * Hop + Hop / 2.0) / FineHop;   // coarse latest beat, fine-hop units
        int availFine = (int)Math.Min(_snapFine - 8, FineHist - 8);
        double fp = pf0, foff = 0, fbest = -1;
        int K = (int)Math.Min(32, (availFine - 40) / pf0);
        if (K < 6) return;
        for (double p = pf0 * 0.9965; p <= pf0 * 1.0035; p += 0.01)
        {
            for (double off = -18; off <= 18; off += 0.25)
            {
                double sum = 0, t = tLatest + off;
                for (int k = 0; k < K; k++) sum += FineAt(t - k * p);
                if (sum > fbest) { fbest = sum; fp = p; foff = off; }
            }
        }
        double newPeriod = fp * FineHop, newLatest = (tLatest + foff) * FineHop;
        // the newest beats may sit past the end of the data; step back to the newest beat that has >= 25 ms after it
        long endFrame = _snapFine * FineHop;
        while (newLatest > endFrame - 1500) newLatest -= newPeriod;
        Stabilize(newPeriod, newLatest, conf);
    }

    private void SmoothFine()
    {
        // [1 2 3 2 1]/9 smoothing of the fine onset (about +-2 ms), written to _fineSm (currently a raw copy)
        var raw = (float[])_fineSm.Clone();
        for (long i = Math.Max(2, _snapFine - FineHist + 4); i < _snapFine - 2; i++)
        {
            float v = raw[(i - 2) & FineMask] + 2 * raw[(i - 1) & FineMask] + 3 * raw[i & FineMask] + 2 * raw[(i + 1) & FineMask] + raw[(i + 2) & FineMask];
            _fineSm[i & FineMask] = v / 9f;
        }
    }

    private void Stabilize(double newPeriod, double newLatest, double conf)
    {
        lock (_gate) StabilizeLocked(newPeriod, newLatest, conf);
    }

    private void StabilizeLocked(double newPeriod, double newLatest, double conf)
    {
        var old = _grid;
        double period = newPeriod, latest = newLatest;
        if (old != null)
        {
            double rel = newPeriod / old.PeriodFrames - 1;
            if (Math.Abs(rel) < 0.004)
            {
                _pendingSwitch = 0;
                _recent.Enqueue(newPeriod); while (_recent.Count > 5) _recent.Dequeue();
                var sorted = _recent.OrderBy(v => v).ToArray();
                double med = sorted[sorted.Length / 2];
                period = old.PeriodFrames * 0.7 + med * 0.3;
            }
            else
            {
                // a different tempo (or octave): take it only when two analyses in a row agree
                if (_pendingSwitch > 0 && Math.Abs(newPeriod / _pendingPeriod - 1) < 0.006) _pendingSwitch++; else { _pendingSwitch = 1; _pendingPeriod = newPeriod; }
                if (_pendingSwitch < 2) return;
                _pendingSwitch = 0; _recent.Clear(); _recent.Enqueue(newPeriod);
                _slotBeats = 0; Array.Clear(_slotAcc); _downSlot = -1; _lastAccBeat = long.MinValue; _anchor = double.NaN; _bars.Clear(); _phraseStart = double.NaN;
            }
            // origin: stay on the old grid unless the new estimate really moved (hysteresis keeps the PLL quiet)
            double predicted = old.OriginFrame + Math.Round((latest - old.OriginFrame) / old.PeriodFrames) * old.PeriodFrames;
            double d = latest - predicted;
            if (Math.Abs(d) < 0.3 * period) latest = predicted + (Math.Abs(d) < 66 ? 0.25 : 0.6) * d;
        }
        else { _recent.Clear(); _recent.Enqueue(newPeriod); }
        if (double.IsNaN(_anchor)) _anchor = latest;
        // ---- downbeat
        long nLatest = (long)Math.Round((latest - _anchor) / period);
        long first = Math.Max(nLatest - 28, _lastAccBeat == long.MinValue ? long.MinValue : _lastAccBeat + 1);
        if (first < nLatest - 28) first = nLatest - 28;
        for (long nb = first; nb <= nLatest - 1; nb++)
        {
            double t = (_anchor + nb * period) / FineHop;
            double acc = 0, lowPk = 0, onPk = 0;
            for (int j = -2; j <= 3; j++) onPk = Math.Max(onPk, FineAt(t + j));
            for (int j = 0; j <= 10; j++) lowPk = Math.Max(lowPk, _fineLowSnap[(long)(t + j) & FineMask]);
            acc = onPk + 0.5 * lowPk;
            int slot = (int)(((nb % 4) + 4) % 4);
            _slotAcc[slot] += acc; _slotBeats++;
            if (_slotBeats % 16 == 0) for (int s = 0; s < 4; s++) _slotAcc[s] *= 0.8;
            _lastAccBeat = nb;
        }
        int bestSlot = 0; for (int s = 1; s < 4; s++) if (_slotAcc[s] > _slotAcc[bestSlot]) bestSlot = s;
        double second = 0; for (int s = 0; s < 4; s++) if (s != bestSlot) second = Math.Max(second, _slotAcc[s]);
        if (_slotBeats >= 12 && _slotAcc[bestSlot] > 1e-9)
        {
            if (_downSlot < 0 && _slotAcc[bestSlot] >= second * 1.06) _downSlot = bestSlot;
            else if (_downSlot >= 0 && bestSlot != _downSlot && _slotAcc[bestSlot] >= _slotAcc[_downSlot] * 1.18) _downSlot = bestSlot;
        }
        bool known = _downSlot >= 0;
        double origin = latest;
        if (known)
        {
            long back = (((nLatest - _downSlot) % 4) + 4) % 4;
            origin = latest - back * period;
            if (double.IsNaN(_phraseStart)) _phraseStart = origin;
            else _phraseStart = origin + Math.Round((_phraseStart - origin) / (4 * period)) * 4 * period;
        }
        var g = new TempoGrid(Math.Round(60.0 * 44100 / period, 2), period, origin, known, conf, ++_version, known ? _phraseStart : double.NaN);
        lock (_gate) { _conf = conf; }
        Volatile.Write(ref _grid, g);
    }

    private double Interp(double absPos)
    {
        long i0 = (long)Math.Floor(absPos); double f = absPos - i0;
        return At(i0) * (1 - f) + At(i0 + 1) * f;
    }
}
