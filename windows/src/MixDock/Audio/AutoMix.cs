namespace MixDock.Audio;

/// <summary>What AutoMix needs from the mixer (lets the state machine be tested without audio).</summary>
public interface IMixHost
{
    bool HasSource(int deck);
    float Level(int deck);              // post-EQ level 0..1 (meter)
    double Bpm(int deck);               // 0 = unknown
    double Phase(int deck);             // heard beat phase 0..1, NaN unknown
    double Crossfader { get; set; }     // 0 = A, 1 = B
    void SetFadeGain(int deck, double gain);
    void SetAutoLowDb(int deck, double db);
    void SyncTempo(int slave);          // tempo + beat alignment of slave to the other deck
    void GlideTempo(int deck, double toward1);   // 0 = keep synced rate, 1 = back to normal speed
}

/// <summary>
/// One switch: fades from the playing deck to the other. Equal-power fade, bass swap (outgoing lows cut as the
/// incoming lows return, so the bass lines never stack), beat-aligned and 4-beat-snapped when both tempos are
/// known, otherwise a plain timed fade. Starts after a user-set time or when the outgoing source goes quiet.
/// </summary>
public sealed class AutoMix
{
    private enum St { Idle, Prep, Wait, Fading }
    private readonly IMixHost _h;
    private St _st = St.Idle;
    private double _stMs, _mainMs, _quietMs, _fadeMs;
    private int _out = -1, _in = -1;
    private bool _synced;
    public bool Enabled { get; set; }
    public double FadeSeconds { get; set; } = 8;      // 4..30
    public double EveryMinutes { get; set; } = 3;     // 1..8
    public string Status { get; private set; } = "";
    public bool Fading => _st == St.Fading;
    public double FadeProgress { get; private set; }

    public AutoMix(IMixHost host) { _h = host; }

    /// <summary>Start the transition now (used by tests).</summary>
    public void ForceFade()
    {
        int m = MainDeck(); if (m < 0 || !Enabled || _st != St.Idle) return;
        Begin(m, 1 - m, quick: false);
    }

    public void Cancel()
    {
        if (_st != St.Idle) Finish(false);
        _st = St.Idle; _mainMs = 0; _quietMs = 0;
    }

    private int MainDeck()
    {
        bool a = _h.HasSource(0), b = _h.HasSource(1);
        if (a && !b) return 0; if (b && !a) return 1; if (!a) return -1;
        double x = _h.Crossfader;
        if (x < 0.45) return 0; if (x > 0.55) return 1;
        return _h.Level(0) >= _h.Level(1) ? 0 : 1;
    }

    /// <summary>Call every ~33 ms with the elapsed milliseconds.</summary>
    public void Tick(double dtMs)
    {
        if (!Enabled) { if (_st != St.Idle) Cancel(); Status = ""; return; }
        _stMs += dtMs;
        switch (_st)
        {
            case St.Idle:
            {
                int m = MainDeck();
                if (m < 0 || !(_h.HasSource(0) && _h.HasSource(1))) { Status = "AUTO MIX: pick an app on both decks"; _mainMs = _quietMs = 0; return; }
                int o = 1 - m;
                if (_h.Level(m) > 0.005f) { _mainMs += dtMs; _quietMs = 0; } else _quietMs += dtMs;
                double remain = EveryMinutes * 60000 - _mainMs;
                Status = $"AUTO MIX on · next fade to Deck {(o == 0 ? "A" : "B")} in {Math.Max(0, remain) / 1000:F0} s";
                if (_quietMs > 2500 && _h.Level(o) > 0.01f) { Begin(m, o, quick: true); return; }
                if (remain <= 4000) Begin(m, o, quick: false);
                break;
            }
            case St.Prep:    // sync the incoming deck while the outgoing one is still alone
                if (!_synced) { _synced = true; if (_h.Bpm(_out) > 0 && _h.Bpm(_in) > 0) _h.SyncTempo(_in); }
                Status = $"AUTO MIX: lining up Deck {(_in == 0 ? "A" : "B")}…";
                if (_stMs >= 3000) { _st = St.Wait; _stMs = 0; }
                break;
            case St.Wait:    // start on the next beat of the outgoing deck
            {
                double ph = _h.Phase(_out);
                if (_h.Bpm(_out) <= 0 || double.IsNaN(ph) || ph < 0.06 || _stMs > 1500) StartFade();
                break;
            }
            case St.Fading:
            {
                _fadeMs += dtMs;
                double p = Math.Clamp(_fadeMs / (FadeLen * 1000), 0, 1);
                FadeProgress = p;
                _h.SetFadeGain(_out, Math.Cos(p * Math.PI / 2));
                _h.SetFadeGain(_in, Math.Sin(p * Math.PI / 2));
                // bass swap: outgoing lows leave around 40-60 %, incoming lows arrive around 50-70 %
                _h.SetAutoLowDb(_out, -40 * Smooth((p - 0.40) / 0.2));
                _h.SetAutoLowDb(_in, -40 * (1 - Smooth((p - 0.50) / 0.2)));
                if (_synced) _h.GlideTempo(_in, Math.Clamp((p - 0.5) * 2, 0, 1));
                Status = $"AUTO MIX: fading to Deck {(_in == 0 ? "A" : "B")} {p * 100:F0}%";
                if (p >= 1) Finish(true);
                break;
            }
        }
    }

    private double FadeLen;
    private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    private void Begin(int o, int i, bool quick)
    {
        _out = o; _in = i; _synced = false; _stMs = 0; _fadeMs = 0;
        if (quick) StartFade(); else _st = St.Prep;
    }

    private void StartFade()
    {
        double bpm = _h.Bpm(_out);
        FadeLen = FadeSeconds;
        if (bpm > 0)    // snap to whole bars (4 beats) so the fade ends on a beat
        {
            double bar = 4 * 60.0 / bpm;
            FadeLen = Math.Max(bar, Math.Round(FadeSeconds / bar) * bar);
        }
        _h.Crossfader = 0.5;   // both decks full; the equal-power gains below do the fading
        _h.SetFadeGain(_out, 1); _h.SetFadeGain(_in, 0);
        _st = St.Fading; _fadeMs = 0; _stMs = 0; FadeProgress = 0;
        Log.Write($"AutoMix fade {_out}->{_in} over {FadeLen:F1}s (bpm {bpm:F1}, synced={_synced})");
    }

    private void Finish(bool completed)
    {
        if (_in < 0 || _out < 0) { _st = St.Idle; return; }
        _h.SetFadeGain(_out, 1); _h.SetFadeGain(_in, 1);
        _h.SetAutoLowDb(_out, 0); _h.SetAutoLowDb(_in, 0);
        if (completed) _h.Crossfader = _in == 0 ? 0.0 : 1.0;
        else _h.Crossfader = _out == 0 ? 0.0 : 1.0;
        _st = St.Idle; _mainMs = 0; _quietMs = 0; _stMs = 0; FadeProgress = 0;
        if (completed) Log.Write($"AutoMix done, deck {_in} is now main");
    }
}
