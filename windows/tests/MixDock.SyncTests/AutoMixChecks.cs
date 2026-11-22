using MixDock.Audio;

/// <summary>Auto Mix: the tempo/key tracker over a drifting, key-changing stream and the director driven by a scripted host.</summary>
internal static class AutoMixChecks
{
    public static void Run(Action<bool, string, string> check)
    {
        var pair = SmartMixFixtures.Create()[0];
        var tracks = new[] { ("loaded", "one", pair.In.Analysis), ("recent", "two", pair.In.Analysis),
            ("repeat artist", "one", pair.In.Analysis), ("new", "three", pair.In.Analysis) };
        var picked = TransitionPlanner.PickNext(pair.Out.Analysis, new(), 1, tracks, ["recent"], "one",
            new HashSet<string>(["loaded"], StringComparer.OrdinalIgnoreCase));
        check(picked == "new", "Library pick avoids loaded tracks, recent selections and the same artist", picked ?? "none");
        Tracker(check);
        Director(check);
        MatchedOffset(check);
    }

    // A stream joined 37.5 s into a cached track: the analysis is shifted into the playhead clock, so the plan lands on the same phrase.
    private static void MatchedOffset(Action<bool, string, string> check)
    {
        TransitionPlan? Plan(double shift)
        {
            var host = new Host(SmartMixFixtures.Create()[0]) { Shift = shift };
            var mix = new AutoMix(host) { Enabled = true };
            host.OutSec = Math.Max(0, host.Pair.Out.Analysis.DurationSec - 90) - shift;
            for (int i = 0; i < 4000 && host.ScheduleCalls == 0; i++)
            {
                mix.Tick(50);
                host.OutSec += .05;
                if (host.InRolled) host.InSec += .05;
                host.Auto.Advance(2205);
            }
            return host.Scheduled;
        }
        const double offset = 37.5;
        var plain = Plan(0);
        var shifted = Plan(offset);
        if (plain == null || shifted == null) { check(false, "Auto Mix plans for a track matched at a 37.5 s offset", "no plan"); return; }
        var g = SmartMixFixtures.Create()[0].Out.Analysis.Shifted(-offset).Tempo!;
        double phrases = (shifted.StartSec * 44100 - g.OriginFrame) / (16 * g.PeriodFrames);
        check(Math.Abs(shifted.StartSec - (plain.StartSec - offset)) < .06 && Math.Abs(phrases - Math.Round(phrases)) < .03,
            "Auto Mix plans in playhead time for a track matched at a 37.5 s offset (same phrase as the unshifted deck)",
            $"plain {plain.StartSec:F2}s, shifted {shifted.StartSec:F2}s, phrase {phrases:F3}");
    }

    private static void Tracker(Action<bool, string, string> check)
    {
        var song = SmartMixFixtures.KeyChange().Out;
        double dur = song.Analysis.DurationSec;
        var tracker = new TempoKeyTracker();
        var block = new float[4096 * 2];
        for (long f = 0; f < (long)(dur * 44100); f += 4096)
        {
            SmartMixFixtures.Fill(song, false, f, block);
            tracker.Feed(block);
        }
        double changeSec = song.SecAt(song.KeyChangeBar * 4.0 + 0);
        double worst = 0; int n = 0;
        for (double t = 25; t <= dur - 2; t += 5)
        {
            double got = tracker.Map.LocalBpm(t), want = song.BpmAt(t - 6);
            if (got <= 0) { worst = double.PositiveInfinity; continue; }
            worst = Math.Max(worst, Math.Abs(got - want)); n++;
        }
        check(n >= 8 && worst <= 0.5, "TempoKeyTracker follows a 124→125.5 BPM drift within ±0.5 BPM", $"{n} windows, worst {worst:F2} BPM over {dur:F0} s");
        var before = tracker.Map.KeyAt(changeSec - 4);
        var after = tracker.Map.KeyAt(dur - 1);
        check(before.Key == song.Key, "TempoKeyTracker names the key before the change", $"{before.Key} vs {song.Key}");
        // C-to-G pad/bass: the detector may land on the relative minor (same Camelot number, harmonically identical for mixing)
        check(after.Key is { } ak && song.Key2 is { } k2 && ak.CamelotNumber == k2.CamelotNumber && after.Key != before.Key,
            "TempoKeyTracker names the new key (same Camelot number) after the change", $"{after.Key} vs {song.Key2}");
        check(tracker.Map.KeyChanges().Any(c => Math.Abs(c.Sec - changeSec) < 15), "TempoKeyTracker reports the key change near the bar where it happens",
            string.Join(", ", tracker.Map.KeyChanges().Select(c => $"{c.Sec:F0}s")));
    }

    private sealed class Host : IMixHost
    {
        public readonly TransitionAutomation Auto = new();
        public readonly SmartMixFixtures.Pair Pair;
        public double OutSec, InSec, Shift;
        public bool InRolled, Synced;
        public int ScheduleCalls, CrossfaderReads;
        public TransitionPlan? Scheduled; public long ScheduledStart;
        public Host(SmartMixFixtures.Pair pair) => Pair = pair;
        public bool HasSource(int deck) => true;
        public bool IsLive(int deck) => false;
        public float Level(int deck) => deck == 0 || InRolled ? .5f : 0;
        public bool IsPlaying(int deck) => deck == 0 || InRolled;
        public bool IsPausedLive(int deck) => false;
        public bool IsCued(int deck) => deck == 1 && !InRolled;
        public void RollCue(int deck) { InRolled = true; }
        public void ReleaseCue(int deck) { }
        private TrackAnalysis OutAnalysis => Pair.Out.Analysis.Shifted(-Shift);
        public TempoGrid? Grid(int deck) => deck == 0 ? OutAnalysis.Tempo : InRolled ? Pair.In.Analysis.Tempo : null;
        public double PlayheadSec(int deck) => deck == 0 ? OutSec : InRolled ? InSec : double.NaN;
        public double Rate(int deck) => 1;
        public (MusicalKey? Key, double Confidence) Key(int deck) => ((deck == 0 ? Pair.Out : Pair.In).Key, .9);
        public double RemainingSeconds(int deck) => deck == 0 ? Pair.Out.Analysis.DurationSec - Shift - OutSec : Pair.In.Analysis.DurationSec - InSec;
        public TrackAnalysis? Analysis(int deck) => deck == 0 ? OutAnalysis : Pair.In.Analysis;
        public double Crossfader { get { CrossfaderReads++; return 0; } }
        public long OutputFrame => Auto.OutputFrame;
        public TransitionRenderState Snapshot => Auto.Snapshot;
        public void Schedule(TransitionPlan plan, int outgoing, long startFrame) { ScheduleCalls++; Scheduled = plan; ScheduledStart = startFrame; Auto.Schedule(plan, outgoing, startFrame); }
        public void CancelTransition() => Auto.Cancel();
        public bool SyncTempo(int slave) { Synced = true; return true; }
        public void ReleaseTempo(int deck, double seconds) { }
        public void ResumeLive(int deck) { }
        public double LookAheadSec(int deck) => 0;
        public MixLane ManualLanes => Auto.Manual;
    }

    private static void Director(Action<bool, string, string> check)
    {
        var pair = SmartMixFixtures.Create()[0];
        var host = new Host(pair);
        var mix = new AutoMix(host) { Enabled = true };
        double lastOut = double.NegativeInfinity, lastIn = double.NegativeInfinity;
        bool backwards = false;
        const double dt = .05;
        host.OutSec = Math.Max(0, pair.Out.Analysis.DurationSec - 90);
        for (int i = 0; i < 4000 && host.ScheduleCalls == 0; i++)
        {
            mix.Tick(dt * 1000);
            host.OutSec += dt;
            if (host.InRolled) host.InSec += dt;
            host.Auto.Advance((int)(dt * 44100));
            if (host.OutSec < lastOut || host.InRolled && host.InSec < lastIn) backwards = true;
            lastOut = host.OutSec; lastIn = host.InSec;
        }
        var plan = host.Scheduled;
        check(plan != null && mix.Fading, "director commits a transition from the scripted host", plan == null ? "none" : $"{plan.Style} {plan.Why}");
        if (plan == null) return;
        var g = pair.Out.Analysis.Tempo!;
        double bars = (plan.StartSec * 44100 - g.OriginFrame) / (4 * g.PeriodFrames);
        double phrases = bars / 4;
        check(Math.Abs(bars - Math.Round(bars)) < .03 && Math.Abs(phrases - Math.Round(phrases)) < .03,
            "director starts on a phrase boundary (4 bars) of the outgoing grid", $"start {plan.StartSec:F2}s = bar {bars:F3}");
        check(host.CrossfaderReads > 0 && host.Auto.Pending is { } job && job.Plan == plan, "director only schedules: the crossfader is read, never written, and the plan is the one scheduled", $"{host.CrossfaderReads} reads");
        check(!backwards, "the live playhead never goes backward while the director arms and commits", $"out {lastOut:F2}s, in {lastIn:F2}s");

        // owner grabs one lane: only that lane is handed back, the others keep running
        var pending = host.Auto.Pending!;
        host.Auto.Advance((int)(Math.Max(0, pending.Start - host.Auto.OutputFrame) + 44100));
        host.Auto.Publish(pending, MixLane.All);
        host.Auto.TakeOver(MixLane.EqA);
        long frame = host.Auto.OutputFrame;
        bool eqAHeld = !host.Auto.Owns(MixLane.EqA, frame);
        bool othersRun = new[] { MixLane.Fader, MixLane.EqB, MixLane.FilterA, MixLane.FilterB, MixLane.FxA, MixLane.FxB, MixLane.TempoA, MixLane.TempoB }
            .All(l => host.Auto.Owns(l, frame));
        check(eqAHeld && othersRun && host.Auto.Manual == MixLane.EqA, "taking over one lane leaves every other lane automated", $"manual {host.Auto.Manual}");
        mix.Tick(dt * 1000);
        check(mix.Fading && host.ScheduleCalls == 1, "the director keeps the transition running after a single-lane takeover", $"{host.ScheduleCalls} schedule call(s)");
    }
}
