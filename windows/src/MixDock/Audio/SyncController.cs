using System.Collections.Concurrent;

namespace MixDock.Audio;

/// <summary>What the sync controller needs from a deck's source (lets the sync engine run on simulated decks in tests).</summary>
public interface ISyncDeck
{
    BpmDetector Bpm { get; }
    bool IsFile { get; }
    bool Active { get; }
}

public enum SyncRole { Off, Slave, Master }

/// <summary>
/// Continuous beat sync between the two decks (Pioneer / Serato style), run on the render thread before the decks are read.
/// One deck is MASTER, the other the SLAVE. Every block it measures the beat (or bar) phase error between both decks'
/// exact playheads and steers the slave's speed with a PI loop (the master only absorbs part of a small tempo gap), so the
/// beats stay locked for as long as sync is on. Speed changes use the key-lock stretcher (no pitch change).
/// Live apps cannot play faster than real time for ever: every speed change drains or fills the deck's delay buffer
/// ("lead"). When it nears the limits the deck re-phases by exactly one bar (a 10 ms crossfaded replay / skip) at a
/// phrase boundary where possible, so the beats stay locked. File decks have no such limit.
/// </summary>
public sealed class SyncController
{
    private const double Fs = 44100;
    private readonly ISyncDeck[] _d; private readonly DeckReader[] _r;
    private readonly ConcurrentQueue<Action> _cmds = new();

    // ---- configuration
    public double LeadMinMs { get; set; } = 100;
    /// <summary>Deepest delay buffer a live deck may use (up to ~4 s, configurable).</summary>
    public double LeadMaxMs { get; set; } = 4000;
    public bool KeyLock { get; set; } = true;
    public double JumpXfadeMs { get; set; } = 10;
    /// <summary>Share of a small tempo gap the master absorbs (0 = only the slave moves).</summary>
    public double MasterShare { get; set; } = 0.5;
    /// <summary>Tempo gaps wider than this (after half/double matching) are refused.</summary>
    public double MaxTempoGap { get; set; } = 0.08;

    // ---- render-thread state
    private int _slave = -1, _master = -1;
    private bool _needInitial;
    private readonly double[] _rate = { 1, 1 }, _tgt = { 1, 1 }, _slewPerSec = { 0.05, 0.05 };
    private double _integ; private long _hold; private int _bigErr;
    private readonly double[] _prevBar = { double.NaN, double.NaN };
    private readonly bool[] _wantJump = new bool[2];
    private long _waitFrames;

    // ---- published state (UI reads)
    private volatile int _pubSlave = -1, _pubMaster = -1;
    private double _phaseMs = double.NaN, _tempoGapPct;
    private string _status = "";
    private readonly double[] _leadMs = new double[2], _pubRate = { 1, 1 };
    private int _rephases;
    public SyncRole Role(int deck) => deck == _pubSlave ? SyncRole.Slave : deck == _pubMaster ? SyncRole.Master : SyncRole.Off;
    /// <summary>Slave beat/bar offset to the master in ms (+ = slave early). NaN when not locked.</summary>
    public double PhaseMs => Volatile.Read(ref _phaseMs);
    public string Status => Volatile.Read(ref _status);
    public double LeadMs(int deck) => Volatile.Read(ref _leadMs[deck]);
    public double CurrentRate(int deck) => Volatile.Read(ref _pubRate[deck]);
    public int Rephases => Volatile.Read(ref _rephases);
    public bool Active => _pubSlave >= 0;
    public int Slave => _pubSlave;
    public int Master => _pubMaster;
    /// <summary>Wall-time ceiling of live sync at the current tempo gap: "N min before a bar re-phase" (documented limits).</summary>
    public string LimitsText { get; private set; } = "";

    public SyncController(ISyncDeck a, DeckReader ra, ISyncDeck b, DeckReader rb) { _d = new[] { a, b }; _r = new[] { ra, rb }; }

    // ---------------------------------------------------------------- UI-thread commands
    /// <summary>Make <paramref name="slave"/> follow the other deck.</summary>
    public void Enable(int slave) => _cmds.Enqueue(() =>
    {
        _slave = slave; _master = 1 - slave; _needInitial = true; _acquire = false; _relabel = 0; _lastRaw = double.NaN; _lastW = 0; _lastPs = _lastPm = 0; _relabelFreeze = 0; _integ = 0; _hold = 0; _bigErr = 0; _waitFrames = 0;
        _slewPerSec[0] = _slewPerSec[1] = 0.05; Publish();
        _status = "SYNC: waiting for the beat…"; Volatile.Write(ref _status, _status);
    });
    public void Disable() => _cmds.Enqueue(() => { _slave = _master = -1; _slewPerSec[0] = _slewPerSec[1] = 0.05; _phaseMs = double.NaN; Publish(); Volatile.Write(ref _status, ""); });
    /// <summary>Sync off; <paramref name="deck"/> (the new master) eases back to its native tempo over <paramref name="seconds"/>.</summary>
    public void HandOver(int deck, double seconds) => _cmds.Enqueue(() =>
    {
        _slave = _master = -1; _phaseMs = double.NaN; Publish();
        _slewPerSec[deck] = Math.Max(1e-4, Math.Abs(_rate[deck] - 1) / Math.Max(0.5, seconds));
        _slewPerSec[1 - deck] = 0.05;
        Volatile.Write(ref _status, "");
    });
    /// <summary>Source changed: back to the plain path and normal speed.</summary>
    public void Reset(int deck) => _cmds.Enqueue(() =>
    {
        if (_slave == deck || _master == deck) { _slave = _master = -1; _phaseMs = double.NaN; Publish(); Volatile.Write(ref _status, ""); }
        _r[deck].LeaveHistory(); _rate[deck] = _tgt[deck] = 1; _r[deck].HistoryRate = 1; _slewPerSec[deck] = 0.05; Volatile.Write(ref _pubRate[deck], 1);
    });
    private void Publish() { _pubSlave = _slave; _pubMaster = _master; }

    public double HeardBpm(int d) { var g = _d[d].Bpm.Grid; return g == null ? 0 : g.Bpm * _r[d].EffectiveRate; }
    public double HeardPhase(int d)
    {
        var g = _d[d].Bpm.Grid; if (g == null) return double.NaN;
        double p = _r[d].PlayheadFrame; return double.IsNaN(p) ? double.NaN : g.Phase(p);
    }

    // ---------------------------------------------------------------- render thread
    /// <summary>Call once per audio block BEFORE the decks are read.</summary>
    public void Process(int frames)
    {
        while (_cmds.TryDequeue(out var c)) { try { c(); } catch (Exception ex) { Log.Write("sync cmd: " + ex.Message); } }
        // live app decks (not song files) are forward-only: no replays, drift-corrected, crossfaded overflow
        for (int d = 0; d < 2; d++) { bool live = _d[d].Active && !_d[d].IsFile; if (_r[d].ForwardLive != live) _r[d].ForwardLive = live; }
        double dt = frames / Fs;
        if (_slave >= 0) { try { Lock(frames, dt); } catch (Exception ex) { Log.Write("sync lock: " + ex); } }
        else FreeRun();
        for (int d = 0; d < 2; d++)
        {
            double step = Math.Min(0.0005, _slewPerSec[d] * dt);
            _rate[d] += Math.Clamp(_tgt[d] - _rate[d], -step, step);
            if (_r[d].HistoryMode) _r[d].HistoryRate = _rate[d];
            Volatile.Write(ref _pubRate[d], _rate[d]);
            var pl = _r[d].Player;
            Volatile.Write(ref _leadMs[d], pl is { Seeded: true } ? pl.Lead * 1000 / Fs : 0);
        }
    }

    private static double Wrap(double x, double w)
    {
        x %= w; if (x >= w / 2) x -= w; if (x < -w / 2) x += w; return x;
    }

    private void FreeRun()
    {
        for (int d = 0; d < 2; d++)
        {
            _tgt[d] = 1; _wantJump[d] = false; _prevBar[d] = double.NaN;
            var pl = _r[d].Player;
            if (pl is not { Seeded: true }) continue;
            // sync was on: shrink the deep delay buffer back to normal (a slightly faster, key-locked catch-up)
            double lead = pl.Lead;
            if (Math.Abs(_rate[d] - 1) < 5e-3 && lead > Fs * 0.28 && !_d[d].IsFile) _tgt[d] = 1.02;
            else if (_tgt[d] == 1.02 && lead < Fs * 0.15) _tgt[d] = 1;
        }
    }

    private void Lock(int frames, double dt)
    {
        int s = _slave, m = _master;
        var gs = _d[s].Bpm.Grid; var gm = _d[m].Bpm.Grid;
        if (!_d[s].Active || !_d[m].Active) { Volatile.Write(ref _status, "SYNC needs an app or song on both decks."); Volatile.Write(ref _phaseMs, double.NaN); return; }
        if (gs == null || gm == null)
        {
            _waitFrames += frames;
            Volatile.Write(ref _status, _waitFrames > 20 * Fs ? "SYNC: no steady beat found on one deck." : "SYNC: waiting for the beat…");
            Volatile.Write(ref _phaseMs, double.NaN); return;
        }
        if (!_r[s].EnterHistory(KeyLock) || !_r[m].EnterHistory(KeyLock) || _r[s].Player is not { Seeded: true } ps || _r[m].Player is not { Seeded: true } pm) return;

        // ---- tempo: r_s / r_m = mult * Ps / Pm, with the multiple (1, 2, 1/2) that needs the smallest change
        double bestLn = double.MaxValue, kMul = 1, q = 1;
        foreach (double k in new[] { 1.0, 2.0, 0.5 })
        {
            double qq = k * gs.PeriodFrames / gm.PeriodFrames, ln = Math.Abs(Math.Log(qq));
            if (ln < bestLn - 1e-9) { bestLn = ln; kMul = k; q = qq; }
        }
        if (Math.Abs(q - 1) > MaxTempoGap)
        {
            Volatile.Write(ref _status, $"Tempos too far apart ({gs.Bpm:F1} vs {gm.Bpm:F1} BPM) to sync.");
            _tgt[0] = _tgt[1] = 1; Volatile.Write(ref _phaseMs, double.NaN); return;
        }
        double share = Math.Abs(q - 1) <= 0.03 ? MasterShare : 0;
        double tm = Math.Pow(q, -share), ts = q * tm;
        if (_d[s].IsFile) { tm = 1; ts = q; } else if (_d[m].IsFile) { tm = 1; ts = q; }   // file decks have no buffer limit: only the slave moves
        _tempoGapPct = (q - 1) * 100;

        // ---- phase error in slave beats (bar level when both downbeats are known)
        bool bar = gs.DownKnown && gm.DownKnown && kMul == 1;
        double W = bar ? 4 : 1;
        double hs = ps.HeadPos, hm = pm.HeadPos;
        double e = Wrap(gs.BeatPos(hs) - gm.BeatPos(hm) * kMul, W);
        if (!_d[s].IsFile)
        {
            // Live decks: the beat detector sometimes re-labels its grid (which kick is "the" beat at half/double tempo, which
            // beat is the downbeat). The audio did not move, so the lock must not chase it: a sudden step in the measured
            // phase is absorbed as an offset. A new tempo (next song) or a new bar/beat mode starts a fresh line-up instead.
            bool newTempo = _lastPs > 0 && (Math.Abs(gs.PeriodFrames / _lastPs - 1) > 0.004 || Math.Abs(gm.PeriodFrames / _lastPm - 1) > 0.004);
            if (newTempo || (_lastW > 0 && W != _lastW)) { _needInitial = true; _acquire = false; _relabel = 0; _lastRaw = double.NaN; }
            if (_relabelFreeze > 0) { _relabelFreeze -= frames; _relabel = 0; _lastRaw = double.NaN; }
            else if (!double.IsNaN(_lastRaw))
            {
                double step = Wrap(e - _lastRaw, W);
                if (Math.Abs(step) > 0.12)
                {
                    double snap = Math.Round(step * 2) / 2;            // a re-label moves the grid by whole or half beats exactly
                    if (Math.Abs(step - snap) < 0.1) { _relabel = Wrap(_relabel + snap, W); Interlocked.Increment(ref _relabels); }
                    else { _needInitial = true; _acquire = false; _relabel = 0; }
                }
            }
            _lastRaw = e; _lastW = W; _lastPs = gs.PeriodFrames; _lastPm = gm.PeriodFrames;
            e = Wrap(e - _relabel, W);
        }
        double eSec = e * gs.PeriodFrames / Math.Max(0.5, _rate[s]) / Fs;
        Volatile.Write(ref _phaseMs, eSec * 1000);

        if (_hold > 0) _hold -= frames;
        else
        {
            double a = Math.Abs(eSec);
            if (a > 0.040) _bigErr++; else _bigErr = 0;
            // Live app decks line up by a jump only once, and only forward. Afterwards the PI loop below pulls the phase in
            // by speed alone: a re-jump on every beat-grid wobble was heard as the music skipping back and replaying.
            if ((_needInitial && a > 0.003) || (_bigErr > 8 && _d[s].IsFile))
            {
                bool jumped = Rephase(s, ps, gs, -e * gs.PeriodFrames, W, initial: true);
                // a live deck without enough buffered audio to skip forward eases into place by slowing down (never replays)
                _acquire = !jumped && !_d[s].IsFile;
                _needInitial = false; _bigErr = 0; _integ = 0; _hold = jumped ? (long)(0.12 * Fs) : 0;
                if (jumped) _relabelFreeze = (long)(0.2 * Fs);
            }
            else if (_acquire && a > 0.040)
            {
                double early = e < 0 ? e + W : e;                                   // beats the slave is ahead, 0..W
                double corr = Math.Clamp(early * gs.PeriodFrames / Fs / 1.5, 0.005, 0.08);
                SetTargets(s, m, ts * (1 - corr), tm, ps, pm);
                Volatile.Write(ref _status, "SYNC: easing the beat into place…");
            }
            else
            {
                _needInitial = false; _acquire = false;
                _integ = Math.Clamp(_integ + eSec * dt / 9.0, -0.003, 0.003);
                double corr = Math.Clamp(eSec / 1.5 + _integ, -0.02, 0.02);
                SetTargets(s, m, ts * (1 - corr), tm, ps, pm);
                Volatile.Write(ref _status, a < 0.002 ? $"SYNC locked · {gm.Bpm * _rate[m]:F2} BPM" : $"SYNC locking… {eSec * 1000:+0.0;-0.0} ms");
            }
        }
        if (_hold > 0) SetTargets(s, m, ts * (1 - Math.Clamp(_integ, -0.02, 0.02)), tm, ps, pm);

        // ---- delay-buffer limits: re-phase one bar (or beat) at a phrase boundary when a deck nears either end
        LimitsText = BuildLimits(s, m, gs, gm);
        MaintainLead(s, ps, gs, bar); MaintainLead(m, pm, gm, bar);
    }

    private readonly bool[] _starved = new bool[2]; private bool _acquire;
    private double _relabel, _lastRaw = double.NaN, _lastW, _lastPs, _lastPm; private long _relabelFreeze; private int _relabels;
    /// <summary>Beat-grid re-labellings that were absorbed without moving the audio (live decks).</summary>
    public int GridRelabels => Volatile.Read(ref _relabels);
    /// <summary>A live deck whose delay buffer is nearly empty cannot run faster than real time, and it must never replay a
    /// bar to refill it. That deck is held at real time and the other deck takes the whole speed difference (same ratio,
    /// so the beats stay locked) until the buffer has recovered.</summary>
    private void SetTargets(int s, int m, double rs, double rm, HistoryPlayer ps, HistoryPlayer pm)
    {
        double low = (LeadMinMs + 150) * Fs / 1000, ok = low + 0.4 * Fs;
        Starve(s, ps.Lead, low, ok); Starve(m, pm.Lead, low, ok);
        if (_starved[s] && rs > 1) { rm /= rs; rs = 1; }
        if (_starved[m] && rm > 1) { rs /= rm; rm = 1; }
        // too little buffered audio to be safe: both decks ease 1.5 % slower together (beats stay locked) until it is rebuilt
        double build = 0.985;
        if (!_d[s].IsFile && ps.Lead < low && rs > build) { rm *= build / rs; rs = build; }
        if (!_d[m].IsFile && pm.Lead < low && rm > build) { rs *= build / rm; rm = build; }
        _tgt[s] = rs; _tgt[m] = rm;
    }
    private void Starve(int d, double lead, double low, double ok)
    {
        if (_d[d].IsFile) { _starved[d] = false; return; }
        if (lead < low) _starved[d] = true; else if (lead > ok) _starved[d] = false;
    }
    private string BuildLimits(int s, int m, TempoGrid gs, TempoGrid gm)
    {
        if (_d[s].IsFile && _d[m].IsFile) return "File decks: no limit.";
        double worst = Math.Max(Math.Abs(1 - _tgt[s]), Math.Abs(1 - _tgt[m]));
        if (worst < 1e-5) return "Same tempo: no drift.";
        double room = (LeadMaxMs - LeadMinMs) / 1000.0;
        return $"Live apps can't run faster than real time for ever: the delay buffer ({room:F1} s of room) absorbs a {worst * 100:F2}% speed change for about {room / worst / 60:F1} min, then the deck re-phases by one bar at a phrase boundary.";
    }

    /// <summary>Keeps a live deck's lead inside [LeadMin, LeadMax]; jumps exactly one bar (beat when bars are longer than 3.3 s) so the beat phase is unchanged.</summary>
    private void MaintainLead(int d, HistoryPlayer pl, TempoGrid g, bool barMode)
    {
        if (_d[d].IsFile) return;
        double lead = pl.Lead, rate = _tgt[d];
        double lo = LeadMinMs * Fs / 1000, hi = LeadMaxMs * Fs / 1000;
        bool soonLow = lead < lo + 0.6 * Fs && rate > 1 - 1e-5, soonHigh = lead > hi - 0.6 * Fs && rate < 1 + 1e-5;
        bool hardLow = lead < lo + 0.25 * Fs, hardHigh = lead > hi - 0.25 * Fs;
        double barSec = 4 * g.PeriodFrames / Fs;
        bool useBar = barMode && g.DownKnown && barSec <= 3.3;
        double unit = (useBar ? 4 : 1) * g.PeriodFrames;
        // crossing of the unit line (downbeat in bar mode, beat otherwise) by the playhead
        double pos = useBar ? g.BarBeats(pl.HeadPos) : g.Phase(pl.HeadPos) * 1.0;
        double span = useBar ? 4 : 1;
        bool crossed = !double.IsNaN(_prevBar[d]) && pos < _prevBar[d] - span / 2;
        _prevBar[d] = pos;
        if (!(soonLow || soonHigh || hardLow || hardHigh)) { _wantJump[d] = false; return; }
        if (!crossed || _hold > 0) return;
        bool phraseLine = g.DownKnown && g.BarInPhrase(pl.HeadPos, 16) == 0;
        if (!(hardLow || hardHigh || phraseLine || !g.DownKnown)) return;
        double delta = (soonLow || hardLow) ? -unit : unit;          // high: skip a bar. Low: live decks are held at real time instead (never replayed)
        if (delta < 0) return;
        if (delta > 0 && lead - delta < lo + 0.05 * Fs) return;
        if (delta < 0 && lead - delta > hi) return;
        pl.Jump(delta, (int)(JumpXfadeMs * Fs / 1000));
        _hold = (long)(0.12 * Fs);
        Interlocked.Increment(ref _rephases);
        Log.Write($"SYNC deck {d}: re-phase {(delta < 0 ? "-" : "+")}{(useBar ? "1 bar" : "1 beat")} (lead {lead / Fs * 1000:F0} ms)");
    }

    /// <summary>Move the slave so its beat (bar) lines up with the master: shift by 'want' input frames, picking the option that keeps the lead in range.</summary>
    private bool Rephase(int s, HistoryPlayer pl, TempoGrid gs, double want, double w, bool initial)
    {
        double lead = pl.Lead, lo = LeadMinMs * Fs / 1000 + 0.05 * Fs, hi = _d[s].IsFile ? 0.3 * Fs : LeadMaxMs * Fs / 1000;
        double unit = w * gs.PeriodFrames, best = double.NaN;
        foreach (double c in new[] { want, want + unit, want - unit })
        {
            double nl = lead - c;
            if (nl < lo || nl > hi) continue;
            if (c < 0 && !_d[s].IsFile) continue;                    // live audio is never replayed
            if (double.IsNaN(best) || Math.Abs(c) < Math.Abs(best)) best = c;
        }
        if (double.IsNaN(best)) { Volatile.Write(ref _status, _d[s].IsFile ? "SYNC: not enough delay buffer to line up the beat yet." : "SYNC: easing the beat into place…"); return false; }
        pl.Jump(best, (int)(JumpXfadeMs * Fs / 1000));
        Log.Write($"SYNC deck {s}: lined up the beat (jump {best / Fs * 1000:+0;-0} ms, lead {lead / Fs * 1000:F0} ms)");        return true;

    }
}
