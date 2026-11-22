using MixDock.Platform;

namespace MixDock.Audio;

public sealed class EngineMixHost : IMixHost
{
    private readonly AudioEngine _e;
    private readonly double[] _timelineLeft = { double.PositiveInfinity, double.PositiveInfinity };
    private readonly long[] _timelineAt = new long[2], _timelinePolled = new long[2];
    private readonly bool[] _polling = new bool[2];
    public EngineMixHost(AudioEngine e) { _e = e; }

    /// <summary>Live-deck hooks owned by the main window (buffered pause, media session resume, look-ahead, app name).</summary>
    public Func<int, bool>? PausedLive;
    public Action<int>? Resume;
    public Func<int, double>? LookAhead;
    public Func<int, string?>? AppName;

    private DeckSource Src(int d) => d == 0 ? _e.DeckA : _e.DeckB;
    private DeckReader Rd(int d) => d == 0 ? _e.ReaderA : _e.ReaderB;
    public bool HasSource(int deck) => Src(deck).ActivePid != null;
    public bool IsLive(int deck) => HasSource(deck) && !Src(deck).IsFile;
    public bool IsPlaying(int deck) => Src(deck).IsFile ? !Src(deck).Paused : HasSource(deck) && Level(deck) > .001f;
    public bool IsPausedLive(int deck) => IsLive(deck) && PausedLive?.Invoke(deck) == true;
    public bool IsCued(int deck) => Src(deck).AutoMixCued;
    public void RollCue(int deck) => Src(deck).RollAutoMixCue();
    public void ReleaseCue(int deck) => Src(deck).ReleaseAutoMixCue();
    public Task LoadFileAsync(int deck, string path, AutoMix scheduler) =>
        Src(deck).LoadFileAsync(path, () => scheduler.ShouldCueNewFile(deck));

    public float Level(int deck)
    {
        var source = Src(deck);
        var dsp = source.Dsp;
        return source.IsFile && source.Paused || dsp.Parameters.Mute ? 0 : dsp.Peak;
    }
    public TempoGrid? Grid(int deck) => Src(deck).Bpm.Grid;
    public double PlayheadSec(int deck) { double p = Rd(deck).PlayheadFrame; return double.IsFinite(p) ? p / 44100 : double.NaN; }
    public double PlayheadFrame(int deck) => Rd(deck).PlayheadFrame;
    public double Rate(int deck) => Rd(deck).EffectiveRate;
    public (MusicalKey? Key, double Confidence) Key(int deck) { var k = _e.Mixer.Key(deck); return (k.Key, k.Confidence); }
    /// <summary>Waveform memory: the cached analysis of a live deck's recognised track, set by the main window.</summary>
    public Func<int, TrackAnalysis?>? CachedAnalysis;
    public TrackAnalysis? Analysis(int deck) => CachedAnalysis?.Invoke(deck);
    public double Crossfader => _e.Mixer.Crossfader;
    public long OutputFrame => _e.Mixer.Automation.OutputFrame;
    public TransitionRenderState Snapshot => _e.Mixer.Automation.Snapshot;
    public void Schedule(TransitionPlan plan, int outgoing, long startFrame) => _e.Mixer.Automation.Schedule(plan, outgoing, startFrame);
    public void CancelTransition() => _e.Mixer.Automation.Cancel();
    public MixLane ManualLanes => _e.Mixer.Automation.Manual;
    public void ResumeLive(int deck) => Resume?.Invoke(deck);
    public double ResumeLatencySec(int deck) => Src(deck).ResumeLatencySec;
    public double LookAheadSec(int deck) => LookAhead?.Invoke(deck) ?? 0;

    public double RemainingSeconds(int deck)
    {
        var source = Src(deck);
        var reader = Rd(deck);
        double buffered = Math.Max(0, source.History.Written - reader.PlayheadFrame) / 44100;
        if (!source.IsFile) return LiveRemaining(deck, buffered);
        return Math.Max(0, (source.Duration - source.Position).TotalSeconds + buffered) / reader.EffectiveRate;
    }

    /// <summary>A live app's track end from its media session timeline (what the app plays, plus what the deck still has
    /// buffered behind it). Infinite when the app publishes no timeline.</summary>
    private double LiveRemaining(int deck, double buffered)
    {
        long now = Environment.TickCount64;
        if (!_polling[deck] && now - _timelinePolled[deck] > 2000 && AppName?.Invoke(deck) is { } name)
        {
            _polling[deck] = true; _timelinePolled[deck] = now;
            _ = Task.Run(async () =>
            {
                try
                {
                    var left = await MediaControl.GetRemainingAsync(name);
                    _timelineLeft[deck] = left ?? double.PositiveInfinity;
                    _timelineAt[deck] = Environment.TickCount64;
                }
                finally { _polling[deck] = false; }
            });
        }
        double leftSec = _timelineLeft[deck];
        if (!double.IsFinite(leftSec)) return leftSec;
        return Math.Max(0, leftSec - (now - _timelineAt[deck]) / 1000d + buffered) / Math.Max(.25, Rd(deck).EffectiveRate);
    }

    public bool SyncTempo(int slave)
    {
        if (_e.Sync.Active) return false;
        _e.Sync.Enable(slave);
        return true;
    }
    public void ReleaseTempo(int deck, double seconds) => _e.Sync.HandOver(deck, seconds);
}
