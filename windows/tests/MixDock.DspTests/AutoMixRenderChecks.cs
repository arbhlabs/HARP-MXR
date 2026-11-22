using System.Text;
using System.Text.Json;
using MixDock.Audio;
using NAudio.Wave;

namespace MixDock { public static class Log { public static void Write(string s) { } } }

/// <summary>
/// Offline render gate for Auto Mix. The real director (AutoMix, TransitionPlanner) drives the real TransitionAutomation,
/// SyncController and MasterMixer on synthesized decks, block by block, with no audio device. Every pair is measured on
/// the mastered PCM and the short mono WAVs and metrics land in evidence/automix/.
/// </summary>
internal static class AutoMixRenderChecks
{
    private const int Rate = 44100, Block = 512, WavRate = Rate / 4;
    private const double MaxPhaseMs = 10, MaxLevelDb = 1.5, MaxPeak = .999;

    internal sealed class Metrics
    {
        public string Pair { get; set; } = "";
        public string ExpectedStyle { get; set; } = "";
        public string Style { get; set; } = "";
        public string Why { get; set; } = "";
        public double StartSec { get; set; }
        public double LandingSec { get; set; }
        public double EndSec { get; set; }
        public double BeatMs { get; set; }
        public bool SyncActive { get; set; }
        public bool PhaseApplies { get; set; }
        public double PhaseErrorMs { get; set; }
        public double BassOverlapMs { get; set; }
        public double BassOverlapBeats { get; set; }
        public double BeforeLufs { get; set; }
        public double AfterLufs { get; set; }
        public double MinDeltaDb { get; set; }
        public double MaxDeltaDb { get; set; }
        public double SamplePeak { get; set; }
        public double InterSamplePeak { get; set; }
        public bool Finite { get; set; }
        public int Underruns { get; set; }
        public string Wav { get; set; } = "";
        public bool Scheduled { get; set; }
    }

    private sealed class RenderDeck : ISyncDeck
    {
        public readonly SampleRing Ring = new(Rate * 4);
        public readonly SampleHistory History = new();
        public readonly BpmDetector Detector = new() { Synchronous = true };
        public readonly DeckReader Reader;
        public readonly SmartMixFixtures.Song Song;
        public readonly bool Incoming;
        public long Fed;
        public bool Playing;
        public BpmDetector Bpm => Detector;
        public bool IsFile => true;
        public bool Active => Playing;

        public RenderDeck(SmartMixFixtures.Song song, bool incoming, bool playing)
        {
            (Song, Incoming, Playing) = (song, incoming, playing);
            Reader = new DeckReader(Ring, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { History = History };
        }

        public void Keep()
        {
            if (!Playing) return;
            var block = new float[2048 * 2];
            long ahead = Reader.HistoryMode ? Rate * 350 / 1000 : Rate * 180 / 1000;
            while (double.IsNaN(Reader.PlayheadFrame) || Fed < Reader.PlayheadFrame + ahead)
            {
                SmartMixFixtures.Fill(Song, Incoming, Fed, block);
                Detector.Feed(block);
                lock (History.Gate) { History.Write(block); Ring.Write(block); }
                Fed += 2048;
            }
        }
    }

    private sealed class RenderHost(MasterMixer mixer, RenderDeck[] decks, SyncController sync, bool[] stream) : IMixHost
    {
        public bool HasSource(int deck) => true;
        public bool IsLive(int deck) => false;
        public float Level(int deck) => decks[deck].Playing ? decks[deck].Reader.Dsp.Peak : 0;
        public bool IsPlaying(int deck) => decks[deck].Playing;
        public bool IsPausedLive(int deck) => false;
        public bool IsCued(int deck) => decks[deck].Incoming && !decks[deck].Playing;
        public void RollCue(int deck) { decks[deck].Playing = true; }
        public void ReleaseCue(int deck) { }
        public TempoGrid? Grid(int deck) => decks[deck].Detector.Grid;
        public double PlayheadSec(int deck) => decks[deck].Reader.PlayheadFrame / Rate;
        double IMixHost.Rate(int deck) => decks[deck].Reader.EffectiveRate;
        public (MusicalKey? Key, double Confidence) Key(int deck) { var k = mixer.Key(deck); return (k.Key, k.Confidence); }
        public double RemainingSeconds(int deck) =>
            Math.Max(0, decks[deck].Song.Analysis.DurationSec - PlayheadSec(deck)) / Math.Max(.25, decks[deck].Reader.EffectiveRate);
        public TrackAnalysis? Analysis(int deck) => stream[deck] ? null : decks[deck].Song.Analysis;
        public double Crossfader => mixer.Crossfader;
        public long OutputFrame => mixer.Automation.OutputFrame;
        public TransitionRenderState Snapshot => mixer.Automation.Snapshot;
        public void Schedule(TransitionPlan plan, int outgoing, long startFrame) => mixer.Automation.Schedule(plan, outgoing, startFrame);
        public void CancelTransition() => mixer.Automation.Cancel();
        public bool SyncTempo(int slave) { if (sync.Active) return false; sync.Enable(slave); return true; }
        public void ReleaseTempo(int deck, double seconds) => sync.HandOver(deck, seconds);
        public void ResumeLive(int deck) { }
        public double LookAheadSec(int deck) => 0;
        public MixLane ManualLanes => mixer.Automation.Manual;
    }

    internal static string EvidenceDir()
    {
        var env = Environment.GetEnvironmentVariable("MIXDOCK_EVIDENCE");
        if (!string.IsNullOrEmpty(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "windows", "src", "MixDock"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "evidence", "automix");
    }

    private static double Frac(double beats) { double f = beats - Math.Floor(beats); return f >= .5 ? f - 1 : f; }

    internal static Metrics Render(SmartMixFixtures.Pair pair, bool outStream, string dir)
    {
        var outSong = pair.Out; var inSong = pair.In;
        var decks = new[] { new RenderDeck(outSong, false, true), new RenderDeck(inSong, true, false) };
        var mixer = new MasterMixer(decks[0].Reader, decks[1].Reader);
        mixer.KeyA.Background = false; mixer.KeyB.Background = false;
        var sync = new SyncController(decks[0], decks[0].Reader, decks[1], decks[1].Reader);
        mixer.Sync = sync;
        mixer.Crossfader = 0;
        var host = new RenderHost(mixer, decks, sync, [outStream, false]);
        var mix = new AutoMix(host);
        mix.Enabled = true;
        typeof(AutoMix).GetField("_seed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(mix, 17);

        var lastOutKey = outSong.Key2 ?? outSong.Key;
        var meter = new SmartMixMeasurements(SmartMixFixtures.SubFrequency(lastOutKey, false),
            SmartMixFixtures.SubFrequency(inSong.Key, true));
        var buffer = new float[Block * 2];
        var mono = new List<short>(Rate * 160 / 4);
        TransitionAutomation.Job? job = null;
        TransitionPlan? plan = null;
        double phaseMax = 0; bool syncAtLanding = false, landed = false;
        long frame = 0, limit = 420L * Rate;
        for (int step = 0; frame < limit; step++)
        {
            foreach (var d in decks) d.Keep();
            mixer.Read(buffer);
            meter.Measure(buffer);
            for (int i = 0; i < Block * 2; i += 8)
            {
                double s = 0;
                for (int k = 0; k < 8; k += 2) s += buffer[i + k] + buffer[i + k + 1];
                mono.Add((short)Math.Round(Math.Clamp(s / 8, -1, 1) * short.MaxValue));
            }
            frame += Block;
            if (step % 4 == 3 && (job == null || mix.Fading)) mix.Tick(4 * Block * 1000.0 / Rate);
            if (job == null && mix.Fading && mixer.Automation.Pending is { } pending)
            {
                job = pending; plan = mix.Plan;
            }
            if (job == null) continue;
            if (!landed && frame >= job.Landing) { landed = true; syncAtLanding = sync.Active; }
            if (landed && sync.Active && frame <= Math.Min(job.End, job.Landing + 8L * Rate))
            {
                var outDeck = decks[job.Outgoing]; var inDeck = decks[1 - job.Outgoing];
                double ho = outDeck.Reader.PlayheadFrame / Rate, hi = inDeck.Reader.PlayheadFrame / Rate;
                double beatMs = 60000 / (outDeck.Song.BpmAt(ho) * outDeck.Reader.EffectiveRate);
                phaseMax = Math.Max(phaseMax, Math.Abs(Frac(outDeck.Song.BeatsAt(ho) - inDeck.Song.BeatsAt(hi))) * beatMs);
            }
            if (frame > job.End + (long)(4.5 * Rate)) break;
        }

        var m = new Metrics
        {
            Pair = pair.Slug, ExpectedStyle = pair.Expected.ToString(), Scheduled = job != null && plan != null,
            Underruns = decks[0].Reader.Underruns + decks[1].Reader.Underruns
        };
        if (job == null || plan == null) return m;
        double start = job.Start / (double)Rate, end = job.End / (double)Rate, landing = job.Landing / (double)Rate;
        var r = meter.Analyze(start, end, landing, plan.BeatSec);
        m.Style = plan.Style.ToString(); m.Why = plan.Why;
        m.StartSec = start; m.LandingSec = landing; m.EndSec = end; m.BeatMs = plan.BeatSec * 1000;
        m.SyncActive = syncAtLanding;
        m.PhaseApplies = plan.Blends || plan.Style is TransitionStyle.DropSwap or TransitionStyle.DoubleDrop
            ? Math.Abs(plan.TempoRatio - 1) <= .08 && decks[1 - job.Outgoing].Detector.Grid != null : false;
        m.PhaseErrorMs = m.PhaseApplies ? (syncAtLanding ? phaseMax : double.PositiveInfinity) : double.NaN;
        m.BassOverlapMs = r.LongestBassOverlapMs; m.BassOverlapBeats = r.LongestBassOverlapMs / m.BeatMs;
        m.BeforeLufs = r.BeforeLufs; m.AfterLufs = r.AfterLufs; m.MinDeltaDb = r.MinimumDeltaDb; m.MaxDeltaDb = r.MaximumDeltaDb;
        m.SamplePeak = r.SamplePeak; m.InterSamplePeak = r.InterSamplePeakEstimate; m.Finite = r.Finite;

        m.Wav = $"{pair.Slug}.wav";
        long first = Math.Max(0, (long)((start - 4) * WavRate)), last = Math.Min(mono.Count, (long)((end + 4) * WavRate));
        using (var writer = new WaveFileWriter(Path.Combine(dir, m.Wav), new WaveFormat(WavRate, 16, 1)))
            writer.WriteSamples(mono.ToArray(), (int)first, (int)(last - first));
        return m;
    }

    internal static List<Metrics> RunAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var results = new List<Metrics>();
        foreach (var pair in SmartMixFixtures.Create()) results.Add(Render(pair, false, dir));
        results.Add(Render(SmartMixFixtures.BpmRamp(), true, dir));
        results.Add(Render(SmartMixFixtures.KeyChange(), true, dir));
        var opt = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(dir, "metrics.json"), JsonSerializer.Serialize(results.Select(x => new
        {
            x.Pair, x.ExpectedStyle, x.Style, x.Why, x.StartSec, x.LandingSec, x.EndSec, x.BeatMs, x.SyncActive, x.PhaseApplies,
            PhaseErrorMs = double.IsFinite(x.PhaseErrorMs) ? (double?)x.PhaseErrorMs : null,
            x.BassOverlapMs, x.BassOverlapBeats, x.BeforeLufs, x.AfterLufs, x.MinDeltaDb, x.MaxDeltaDb, x.SamplePeak,
            x.InterSamplePeak, x.Finite, x.Underruns, x.Wav, x.Scheduled
        }), opt));
        var csv = new StringBuilder("pair,expected,style,start_s,landing_s,end_s,beat_ms,sync,phase_err_ms,bass_overlap_ms,bass_overlap_beats,before_lufs,after_lufs,min_delta_db,max_delta_db,peak,wav\n");
        foreach (var x in results)
            csv.Append(string.Join(',', x.Pair, x.ExpectedStyle, x.Style, F(x.StartSec), F(x.LandingSec), F(x.EndSec), F(x.BeatMs),
                x.SyncActive, x.PhaseApplies ? F(x.PhaseErrorMs) : "n/a", F(x.BassOverlapMs), F(x.BassOverlapBeats), F(x.BeforeLufs),
                F(x.AfterLufs), F(x.MinDeltaDb), F(x.MaxDeltaDb), F(x.SamplePeak), x.Wav)).Append('\n');
        File.WriteAllText(Path.Combine(dir, "metrics.csv"), csv.ToString());
        return results;
        static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Run(Action<bool, string> assert)
    {
        var failed = new List<string>();
        void check(bool ok, string name) { if (ok) assert(true, name); else { failed.Add(name); Console.WriteLine("FAIL " + name); } }
        string dir = EvidenceDir();
        var results = RunAll(dir);
        foreach (var m in results)
        {
            Console.WriteLine($"  {m.Pair}: {m.Style} start {m.StartSec:F1}s beat {m.BeatMs:F0} ms; phase {(m.PhaseApplies ? m.PhaseErrorMs.ToString("F2") + " ms" : "n/a")}, " +
                $"bass overlap {m.BassOverlapBeats:F2} beats, level {m.MinDeltaDb:+0.00;-0.00}..{m.MaxDeltaDb:+0.00;-0.00} dB, peak {m.SamplePeak:F3}; {m.Why}");
            check(m.Scheduled, $"Auto Mix render {m.Pair}: the director schedules a transition");
            if (!m.Scheduled) continue;
            check(!m.PhaseApplies || m.PhaseErrorMs < MaxPhaseMs, $"Auto Mix render {m.Pair}: beat phase error {m.PhaseErrorMs:F2} ms < {MaxPhaseMs} ms");
            check(m.BassOverlapBeats <= 1.0001, $"Auto Mix render {m.Pair}: bass overlap {m.BassOverlapBeats:F2} beats <= 1 beat");
            check(Math.Abs(m.MinDeltaDb) <= MaxLevelDb && Math.Abs(m.MaxDeltaDb) <= MaxLevelDb,
                $"Auto Mix render {m.Pair}: level {m.MinDeltaDb:+0.00;-0.00}..{m.MaxDeltaDb:+0.00;-0.00} dB within {MaxLevelDb} dB of the before-after line");
            check(m.Finite && m.SamplePeak < MaxPeak, $"Auto Mix render {m.Pair}: peak {m.SamplePeak:F4} < {MaxPeak}");
        }
        check(results.Count >= 8 && results.Count(x => x.Scheduled) == results.Count, "Auto Mix render gate: six pairs plus the drift and key-change streams");
        if (failed.Count > 0) assert(false, $"{failed.Count} Auto Mix render check(s) failed, first: {failed[0]}");
    }
}
