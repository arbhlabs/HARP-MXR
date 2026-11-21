namespace MixDock.Audio;

/// <summary>
/// Counts beats, bars and phrases on a deck from its heard beat phase. Estimates the downbeat from where the
/// low-end accent falls within the bar, and re-anchors the phrase counter when the energy jumps or drops at a
/// bar line (drops, breakdowns and new sections almost always start on a phrase boundary).
/// Update() is called from the UI timer; everything here is plain arithmetic and unit-testable.
/// </summary>
public sealed class PhraseTracker
{
    private double _prevPhase = double.NaN;
    private readonly double[] _accent = new double[4];
    private long _beats;                 // beats counted since lock
    private long _anchorBeat;            // beat index where the current phrase began (a downbeat)
    private double _barEnergy, _barEnergyN;
    private readonly Queue<double> _bars = new();
    private double _accentNow; private int _accentN;

    public int PhraseBars { get; set; } = 16;
    public double Bpm { get; private set; }
    /// <summary>True once the tempo is known and at least two bars were counted.</summary>
    public bool Locked => Bpm > 0 && _beats >= 8;
    public long Beats => _beats;
    /// <summary>Which beat of the bar was estimated to be "1" (0..3, relative to the first counted beat).</summary>
    public int DownbeatOffset { get; private set; }
    /// <summary>0..3, 0 = downbeat.</summary>
    public int BeatInBar => (int)(((_beats - 1 - DownbeatOffset) % 4 + 4) % 4);
    public long Bar => (_beats - 1 - DownbeatOffset) < 0 ? 0 : (_beats - 1 - DownbeatOffset) / 4;
    public int BarInPhrase => (int)(((Bar - _anchorBeat / 4) % PhraseBars + PhraseBars) % PhraseBars);
    /// <summary>Whole bars left until the next phrase boundary (0 = the next bar line starts a phrase).</summary>
    public int BarsToPhrase => PhraseBars - 1 - BarInPhrase;
    /// <summary>Beats until the next phrase boundary, counting the rest of the current bar.</summary>
    public int BeatsToPhrase => BarsToPhrase * 4 + (3 - BeatInBar) + 1;
    /// <summary>Energy of the last full bar relative to the bars before it (1 = steady, &lt;0.7 = dropping).</summary>
    public double EnergyTrend { get; private set; } = 1;
    /// <summary>Energy of the last full bar, 0..1.</summary>
    public double Energy { get; private set; }
    /// <summary>Raised on each counted beat (beatInBar, barInPhrase).</summary>
    public event Action<int, int>? Beat;
    /// <summary>Set when the last bar line re-anchored the phrase (section change).</summary>
    public bool SectionChangeAtLastBar { get; private set; }

    public void Reset()
    {
        _prevPhase = double.NaN; Array.Clear(_accent); _beats = 0; _anchorBeat = 0; _barEnergy = _barEnergyN = 0;
        _bars.Clear(); Bpm = 0; DownbeatOffset = 0; EnergyTrend = 1; Energy = 0; _accentNow = 0; _accentN = 0; SectionChangeAtLastBar = false;
    }

    /// <param name="bpm">heard tempo (0 = unknown)</param>
    /// <param name="phase">heard beat phase 0..1 (NaN = unknown)</param>
    /// <param name="lowLevel">current low-band level 0..1 (kick/bass accent)</param>
    /// <param name="level">current overall level 0..1 (energy)</param>
    public void Update(double bpm, double phase, double lowLevel, double level)
    {
        if (bpm <= 0 || double.IsNaN(phase)) { _prevPhase = double.NaN; return; }
        if (Bpm > 0 && Math.Abs(bpm / Bpm - 1) > 0.04) Reset();   // new tempo: start counting again
        Bpm = bpm;
        _barEnergy += level; _barEnergyN++;
        if (phase < 0.15 || phase > 0.92) { _accentNow = Math.Max(_accentNow, lowLevel); _accentN++; }
        if (!double.IsNaN(_prevPhase) && phase < _prevPhase - 0.5) OnBeat();
        _prevPhase = phase;
    }

    private void OnBeat()
    {
        _beats++;
        int slot = (int)((_beats - 1) % 4);
        _accent[slot] = _accent[slot] * 0.85 + _accentNow;
        _accentNow = 0; _accentN = 0;
        if (_beats >= 8 && _beats % 4 == 0)
        {
            int best = 0; for (int i = 1; i < 4; i++) if (_accent[i] > _accent[best] * 1.04) best = i;
            DownbeatOffset = best;
        }
        SectionChangeAtLastBar = false;
        if (BeatInBar == 0 && _beats > DownbeatOffset + 1) OnBar();
        Beat?.Invoke(BeatInBar, BarInPhrase);
    }

    private void OnBar()
    {
        double e = _barEnergyN > 0 ? _barEnergy / _barEnergyN : 0;
        _barEnergy = _barEnergyN = 0;
        Energy = e;
        if (_bars.Count >= 2)
        {
            double prev = _bars.Average();
            EnergyTrend = prev > 1e-4 ? e / prev : 1;
            // big change at a bar line: a new section starts here -> it is bar 1 of a phrase
            if ((EnergyTrend > 1.6 || EnergyTrend < 0.6) && e + prev > 0.02)
            {
                _anchorBeat = Math.Max(0, Bar - 1) * 4;   // the bar that just ended was the first of the new section
                SectionChangeAtLastBar = true;
                _bars.Clear();
            }
        }
        _bars.Enqueue(e); while (_bars.Count > 4) _bars.Dequeue();
    }
}
