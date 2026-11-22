namespace MixDock.Audio;

/// <summary>What Auto Mix needs from the engine. Times on a deck are seconds in that deck's history clock (the clock its
/// beat grid is expressed in); output frames are the master clock the automation runs on.</summary>
public interface IMixHost
{
    bool HasSource(int deck);
    /// <summary>A captured app (browser, Spotify): it cannot seek, its playhead only moves forward.</summary>
    bool IsLive(int deck);
    float Level(int deck);
    bool IsPlaying(int deck);
    /// <summary>A live deck held by the buffered pause (it resumes through the media session).</summary>
    bool IsPausedLive(int deck);
    bool IsCued(int deck);
    void RollCue(int deck);
    void ReleaseCue(int deck);
    TempoGrid? Grid(int deck);
    double PlayheadSec(int deck);
    double Rate(int deck);
    (MusicalKey? Key, double Confidence) Key(int deck);
    /// <summary>Wall seconds until the deck's track ends; infinite when unknown (live app without a media timeline).</summary>
    double RemainingSeconds(int deck);
    /// <summary>Offline analysis of a song file when one exists (waveform memory); null for live decks.</summary>
    TrackAnalysis? Analysis(int deck);
    double Crossfader { get; }
    long OutputFrame { get; }
    TransitionRenderState Snapshot { get; }
    void Schedule(TransitionPlan plan, int outgoing, long startFrame);
    void CancelTransition();
    bool SyncTempo(int slave);
    void ReleaseTempo(int deck, double seconds);
    /// <summary>Start a paused live source through its media session.</summary>
    void ResumeLive(int deck);
    double LookAheadSec(int deck);
    double ResumeLatencySec(int deck) => 0;
    /// <summary>Lanes the owner has taken back by touching a control during the transition.</summary>
    MixLane ManualLanes { get; }
}

/// <summary>What the transition card shows.</summary>
public sealed record AutoMixCard(string State, string Style, string Why, string Count, string Next, MixLane Automated,
    double Progress, bool Running, string Genre, MixLane Manual = MixLane.None)
{
    public static readonly AutoMixCard Off = new("AUTO MIX OFF", "", "Press AUTO to mix on the beat and phrase", "", "",
        MixLane.None, 0, false, "");
}

/// <summary>
/// Auto Mix director. No timers and no wall-clock fades: it reads each deck's beat grid, windowed tempo and key, picks a
/// transition with <see cref="TransitionPlanner"/> on a phrase boundary and hands it to the render-thread
/// <see cref="TransitionAutomation"/>, which runs every lane sample-accurately on the beat. Live decks are never seeked:
/// the incoming live deck is phase-locked by beat sync (forward-only) and a paused one is started through its media
/// session ahead of the phrase, allowing for its look-ahead delay. Touching a lane hands that lane to the owner.
/// </summary>
public sealed class AutoMix
{
    private enum Stage { Waiting, Planned, Running }
    private const double ArmSec = 8, LockLeadSec = 10, CommitBars = 2, QuietMs = 2500;
    private readonly IMixHost _host;
    private readonly TempoKeyMap[] _maps = { new(), new() };
    private readonly double[] _lastSnap = { double.NaN, double.NaN };
    private Stage _stage;
    private TransitionPlan? _plan;
    private int _out, _in, _seed, _mixes;
    private TransitionStyle? _skip;
    private bool _now, _ownsSync, _armed, _enabled, _rolled;
    private double _armOutSec = double.NaN, _lastBeatIdx = double.NaN;
    private long _startFrame;
    private double _quietMs, _sincePlanMs = double.MaxValue;

    public AutoMix(IMixHost host) => _host = host;

    public MixPreferences Preferences { get; set; } = new();
    public TempoKeyMap Map(int deck) => _maps[deck];
    public int Mixes => _mixes;
    public TransitionPlan? Plan => _plan;
    public int Outgoing => _out;
    public string Status { get; private set; } = "";
    public AutoMixCard Card { get; private set; } = AutoMixCard.Off;
    public bool Fading => _stage == Stage.Running;
    public double FadeProgress => _stage == Stage.Running ? _host.Snapshot.Progress : 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) { Cancel(); Card = AutoMixCard.Off; }
            else { _seed = Environment.TickCount & 0xffff; Card = new("AUTO MIX ON", "", "Listening for the beat…", "", "", MixLane.None, 0, false, ""); }
        }
    }

    /// <summary>A file loaded onto the off-air deck while Auto Mix runs waits parked at its start for the next transition.</summary>
    public bool ShouldCueNewFile(int deck) => Enabled && _stage != Stage.Running
        && _host.Crossfader == 1 - deck && _host.IsPlaying(1 - deck);

    /// <summary>Fastest good transition from here, on the next beat or bar.</summary>
    public void MixNow()
    {
        if (!Enabled || _stage == Stage.Running) return;
        _now = true; _plan = null; _stage = Stage.Waiting; _sincePlanMs = double.MaxValue;
        Tick(0);
    }

    /// <summary>Old name kept for callers: Mix now.</summary>
    public void ForceFade() => MixNow();

    /// <summary>Pick a different transition style for the upcoming mix.</summary>
    public void SkipStyle()
    {
        if (!Enabled || _stage == Stage.Running || _plan == null) return;
        _skip = _plan.Style; _plan = null; _stage = Stage.Waiting; _sincePlanMs = double.MaxValue;
        Tick(0);
    }

    public void Cancel()
    {
        if (_stage == Stage.Running) _host.CancelTransition();
        if (_ownsSync) { _host.ReleaseTempo(_in, 2); _ownsSync = false; }
        _host.ReleaseCue(0); _host.ReleaseCue(1);
        _stage = Stage.Waiting; _plan = null; _now = _armed = false; _skip = null; _quietMs = 0;
        Status = "";
    }

    private static string Name(int deck) => deck == 0 ? "A" : "B";

    private int MainDeck()
    {
        double x = _host.Crossfader;
        return Math.Abs(x - .5) < .005 ? (_host.Level(0) >= _host.Level(1) ? 0 : 1) : x < .5 ? 0 : 1;
    }

    /// <summary>Record each deck's tempo and key once per second of its own playback: the windowed history that lets a
    /// long DJ mix change tempo and key inside one stream.</summary>
    private void Track()
    {
        for (int d = 0; d < 2; d++)
        {
            double sec = _host.HasSource(d) ? _host.PlayheadSec(d) : double.NaN;
            if (!double.IsFinite(sec)) { if (!_host.HasSource(d)) { _maps[d].Clear(); _lastSnap[d] = double.NaN; } continue; }
            if (double.IsFinite(_lastSnap[d]) && sec < _lastSnap[d] - 2) _maps[d].Clear();   // new source: new clock
            if (double.IsFinite(_lastSnap[d]) && sec - _lastSnap[d] < 1 && sec >= _lastSnap[d]) continue;
            var g = _host.Grid(d);
            var (key, conf) = _host.Key(d);
            _maps[d].Add(new TempoKeyPoint(sec, g?.Bpm ?? 0, g?.Confidence ?? 0, (g?.OriginFrame ?? 0) / 44100, g?.DownKnown ?? false, key, conf));
            _lastSnap[d] = sec;
        }
    }

    public void Tick(double dtMs)
    {
        if (!double.IsFinite(dtMs) || dtMs < 0) return;
        Track();
        if (!Enabled) return;
        if (_stage == Stage.Running) { TryRoll(); TrySync(); Follow(); return; }

        if (!_host.HasSource(0) || !_host.HasSource(1))
        {
            _host.ReleaseCue(0); _host.ReleaseCue(1);
            _plan = null; _stage = Stage.Waiting; _quietMs = 0;
            Show("WAITING", "", "Load a source on both decks.", "", "");
            return;
        }
        for (int d = 0; d < 2; d++)
            if (_host.IsCued(d) && _host.Crossfader != 1 - d) _host.ReleaseCue(d);   // a hand on the fader takes the deck back
        int main = MainDeck();
        if (_plan == null || !_armed) { _out = main; _in = 1 - main; }
        if (_host.Level(_out) > .001f) _quietMs = 0; else _quietMs += dtMs;
        if (_quietMs >= QuietMs && (_host.IsCued(_in) || _host.Level(_in) > .001f || _host.IsPausedLive(_in))) _now = true;

        _sincePlanMs += dtMs;
        if (_plan == null || _sincePlanMs >= 250) { _sincePlanMs = 0; Replan(); }
        if (_plan == null) return;

        double outSec = _host.PlayheadSec(_out);
        double rate = Math.Max(.25, _host.Rate(_out));
        double toStart = (_plan.StartSec - outSec) / rate;
        double lead = Math.Max(_host.IsCued(_in) ? LockLeadSec : ArmSec, _host.IsPausedLive(_in) ? _host.LookAheadSec(_in) + _host.ResumeLatencySec(_in) + _plan.BeatSec : 0);
        if (!_armed && (toStart <= lead || _now)) Arm();
        else if (_armed) { TryRoll(); TrySync(); }
        if (toStart <= Math.Max(_plan.BeatSec * 4 * CommitBars, 3) / rate || _now && toStart <= _plan.BeatSec * 4)
        {
            if (!_armed) Arm();
            _startFrame = _host.OutputFrame + (long)Math.Round(Math.Max(0, toStart) * 44100);
            _host.Schedule(_plan, _out, _startFrame);
            _stage = Stage.Running;
            Follow();
            return;
        }
        ShowPlanned(toStart);
    }

    /// <summary>Get the incoming deck ready a few bars before the phrase: roll a parked file, start a paused live source
    /// through its media session, lock its beat to the outgoing deck (forward-only for live decks).</summary>
    private void Arm()
    {
        _armed = true; _rolled = false; _armOutSec = double.NaN; _lastBeatIdx = double.NaN;
        TryRoll();
        TrySync();
    }

    // Start a parked incoming file on the outgoing deck's next bar line (beat when no downbeat is known), so the lock only has
    // a small offset left to jump; a source with no grid, a silent outgoing deck or a 5 s wait rolls it at once.
    private void TryRoll()
    {
        if (!_armed || _rolled || _plan == null) return;
        bool need = _host.IsCued(_in) || _host.IsPausedLive(_in);
        if (!need) { _rolled = true; return; }
        double sec = _host.PlayheadSec(_out);
        if (_host.IsPausedLive(_in) && !_now && double.IsFinite(sec))
        {
            double lead = _host.LookAheadSec(_in) + _host.ResumeLatencySec(_in) + _plan.BeatSec;
            if ((_plan.StartSec - sec) / Math.Max(.25, _host.Rate(_out)) > lead) return;
        }
        var g = _host.Grid(_out);
        bool go = _now || g == null || !double.IsFinite(sec) || _host.IsPausedLive(_in) ||
            (_plan.StartSec - sec) / Math.Max(.25, _host.Rate(_out)) <= 5;
        if (!go)
        {
            if (double.IsNaN(_armOutSec)) _armOutSec = sec;
            double idx = Math.Floor(g!.BeatPos(sec * 44100) / (g.DownKnown ? 4 : 1));
            go = (double.IsFinite(_lastBeatIdx) && idx > _lastBeatIdx) || sec - _armOutSec > 5;
            _lastBeatIdx = idx;
        }
        if (!go) return;
        _rolled = true;
        if (_host.IsCued(_in)) _host.RollCue(_in);
        if (_host.IsPausedLive(_in)) _host.ResumeLive(_in);
    }

    // A just-rolled deck has no beat grid yet; keep retrying while armed so the lock still starts once its detector finds one.
    private void TrySync()
    {
        if (_ownsSync || _plan is not { } p) return;
        if ((p.Blends || p.Style is TransitionStyle.DropSwap or TransitionStyle.DoubleDrop) &&
            Math.Abs(p.TempoRatio - 1) <= .08 && _host.Grid(_in) != null)
            _ownsSync = _host.SyncTempo(_in);
    }

    private void Replan()
    {
        double outSec = _host.PlayheadSec(_out), inSec = _host.PlayheadSec(_in);
        var prefs = Preferences;
        var outA = _host.Analysis(_out) ?? _maps[_out].LiveAnalysis(outSec, _host.Grid(_out), prefs.Genre);
        var inA = _host.Analysis(_in) ?? _maps[_in].LiveAnalysis(double.IsFinite(inSec) ? inSec : 0, _host.Grid(_in), prefs.Genre);
        double rate = Math.Max(.25, _host.Rate(_out));
        double remaining = _host.RemainingSeconds(_out);
        double bar = outA.Tempo is { Bpm: > 0 } t ? 240 / t.Bpm : 2;
        if (double.IsFinite(remaining))
        {
            double duration = outSec + remaining * rate;
            var profile = TransitionPlanner.Profile(outA.Genre == DjGenre.Auto ? DjGenre.OpenFormat : outA.Genre);
            double need = (Math.Max(profile.SwapBars, 16) + 2) * bar;
            outA = outA with { DurationSec = duration, MixOutSec = outA.MixOutSec > outSec && _host.Analysis(_out) != null ? outA.MixOutSec : Math.Max(outSec, duration - need) };
        }
        if (!_now && !double.IsFinite(remaining))
        {
            _plan = null;
            Show("READY", "", $"Deck {Name(_out)} live · no track end known · press MIX NOW",
                "", NextText());
            return;
        }
        double toMix = (outA.MixOutSec - outSec) / rate;
        if (!_now && toMix > 40)
        {
            _plan = null;
            Show("NEXT MIX", "", $"Deck {Name(_out)} → {Name(_in)} near the end of this track.", $"{FormatTime(toMix)}", NextText());
            return;
        }
        var plan = TransitionPlanner.Choose(outA, inA, outSec, inSec, prefs, now: _now, seed: _seed + _mixes, skip: _skip,
            canCueIncoming: false, driftBpmPerSec: _maps[_out].DriftPerSec(outSec));
        if (plan == null && _skip != null)
        {
            _skip = null;
            plan = TransitionPlanner.Choose(outA, inA, outSec, inSec, prefs, now: _now, seed: _seed + _mixes, canCueIncoming: false,
                driftBpmPerSec: _maps[_out].DriftPerSec(outSec));
        }
        // No beat grid on the outgoing deck yet: a quick echo out, never a blind fade.
        if (plan == null && (_now || double.IsFinite(remaining) && remaining < 12))
            plan = Fallback(outSec);
        // A beat-locked blend needs time to roll the parked file and let its grid appear: start on a later phrase instead of unlocked.
        for (int i = 0; i < 3 && plan != null && !_armed && !_now && _host.IsCued(_in) && NeedsLock(plan) &&
             (plan.StartSec - outSec) / rate < LockLeadSec - 1; i++)
        {
            var later = TransitionPlanner.Choose(outA with { MixOutSec = plan.StartSec + bar * .5 }, inA, outSec, inSec, prefs, now: false,
                seed: _seed + _mixes, skip: _skip, canCueIncoming: false, driftBpmPerSec: _maps[_out].DriftPerSec(outSec));
            if (later == null || later.StartSec <= plan.StartSec) break;
            plan = later;
        }
        // A stream's phrase grid is re-anchored to the playhead on every look, so a fresh plan would recede forever: hold the first one.
        if (plan != null && _plan != null && !_armed && !_now && _host.Analysis(_out) == null && (_plan.StartSec - outSec) / rate > 3)
            plan = _plan;
        _plan = plan;
        if (plan == null) Show("LISTENING", "", "Waiting for a beat grid on both decks.", "", NextText());
    }

    private static bool NeedsLock(TransitionPlan p) =>
        (p.Blends || p.Style is TransitionStyle.DropSwap or TransitionStyle.DoubleDrop) && Math.Abs(p.TempoRatio - 1) <= .08;

    private static TransitionPlan Fallback(double outSec)
    {
        const double beat = .5;
        double start = outSec + .05;
        return new TransitionPlan(TransitionStyle.EchoOut, start, start + beat, 0, beat * 1.75, beat, 1, false, 1, 0,
            "no beat grid yet · echo out", 1, 1, .6, false);
    }

    private void Follow()
    {
        var snap = _host.Snapshot;
        var plan = _plan!;
        long end = _startFrame + (long)Math.Round(plan.DurationSec * 44100);
        if (snap.MissedCue)
        {
            Finish(false);
            Show("MISSED", "", "Incoming deck not ready on the beat · planning again", "", NextText());
            return;
        }
        if (_host.OutputFrame >= end || snap.Plan == null && _host.OutputFrame > _startFrame + 4410)
        {
            Finish(true);
            return;
        }
        double beatsLeft = (end - _host.OutputFrame) / (44100 * plan.BeatSec);
        bool before = _host.OutputFrame < _startFrame;
        string count = before ? $"in {Math.Ceiling((_startFrame - _host.OutputFrame) / (44100 * plan.BeatSec)):0} beats"
            : beatsLeft >= 8 ? $"{Math.Ceiling(beatsLeft / 4):0} bars left" : $"{Math.Ceiling(beatsLeft):0} beats left";
        Card = new(before ? "STARTING" : "MIXING", plan.Label, plan.Why, count, NextText(),
            snap.Automated, snap.Progress, true, TransitionPlanner.Profile(plan.Genre).Name, _host.ManualLanes);
        Status = $"{plan.Label} · {count}";
    }

    private void Finish(bool done)
    {
        var plan = _plan;
        if (_ownsSync) { _host.ReleaseTempo(_in, plan == null ? 4 : plan.BeatSec * 32); _ownsSync = false; }
        if (done)
        {
            _mixes++;
            Show("ON AIR", "", $"Deck {Name(_in)} on air{(plan == null ? "" : $" after {plan.Label.ToLowerInvariant()}")}.", "", "");
        }
        else _host.CancelTransition();
        _stage = Stage.Waiting; _plan = null; _now = _armed = false; _skip = null; _quietMs = 0; _sincePlanMs = double.MaxValue;
    }

    private void ShowPlanned(double toStart)
    {
        var p = _plan!;
        double beats = Math.Max(0, toStart * Math.Max(.25, _host.Rate(_out)) / p.BeatSec);
        string count = beats >= 16 ? $"in {Math.Ceiling(beats / 4):0} bars" : $"in {Math.Ceiling(beats):0} beats";
        Card = new(_now ? "MIX NOW" : "NEXT", p.Label, p.Bars > 0 ? $"{p.Bars} bars · {p.Why}" : p.Why, count, NextText(),
            MixLane.None, 0, false, TransitionPlanner.Profile(p.Genre).Name);
        Status = $"{p.Label} {count}";
        _stage = Stage.Planned;
    }

    private void Show(string state, string style, string why, string count, string next)
    {
        Card = new(state, style, why, count, next, MixLane.None, 0, false, "");
        Status = why;
    }

    /// <summary>The incoming deck with its local tempo and key (windowed, so a long mix shows where it is now).</summary>
    private string NextText()
    {
        string text = $"{Name(_out)} → {Name(_in)}";
        double sec = _host.PlayheadSec(_in);
        if (!double.IsFinite(sec)) return text;
        double bpm = _maps[_in].LocalBpm(sec);
        if (!(bpm > 0) && _host.Grid(_in) is { } g) bpm = g.Bpm;
        if (bpm > 0) text += $" · {bpm * Math.Max(.25, _host.Rate(_in)):0.#} BPM";
        var (key, conf) = _maps[_in].KeyAt(sec);
        if (key is { } k && conf >= .18) text += $" · {k.Camelot}";
        return text;
    }

    private static string FormatTime(double sec) => sec >= 60 ? $"in {(int)(sec / 60)}:{(int)(sec % 60):00}" : $"in {Math.Ceiling(sec):0} s";
}
