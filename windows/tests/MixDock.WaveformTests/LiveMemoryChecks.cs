using MixDock.Audio;

namespace MixDock
{
    // The app's file log is not part of this test project; the worker only reports through it.
    internal static class Log { public static void Write(string message) { } }
}

/// <summary>Waveform memory for live decks: synthetic capture is pushed through a SampleHistory like the capture thread does.</summary>
internal static class LiveMemoryChecks
{
    private const int Rate = 44100;

    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "harp-live-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var song = TrackCacheChecks.Song(80, 21);
            var stranger = TrackCacheChecks.Song(40, 342);
            var songId = Guid.NewGuid();
            var cache = new TrackCache(directory);
            var metadata = new TrackMetadata("Synthetic", "Test", "", 80);
            cache.Put(new TrackCacheEntry(songId, metadata, 80, WaveformAnalyzer.AnalyzeAll(song),
                AcousticFingerprint.Extract(song), null, true));

            var (history, memory) = Deck(cache, () => true);
            Play(history, memory, song, 0, 20);
            check(memory.Current is { } start && start.TrackId == songId && Math.Abs(start.CachedSec(history.Written) - 20) < .05,
                "Live deck from track start is matched and its playhead aligns within 50 ms");

            (history, memory) = Deck(cache, () => true);
            Play(history, memory, song, 25.4, 20);
            check(memory.Current is { } mid && mid.TrackId == songId && Math.Abs(mid.CachedSec(history.Written) - 45.4) < .05,
                "Live deck joining mid-track is placed at the right cached position");

            (history, memory) = Deck(cache, () => true);
            Play(history, memory, stranger, 0, 24);
            check(memory.Current == null, "A different live track is not matched");

            (history, memory) = Deck(cache, () => true);
            Play(history, memory, song, 0, 20);
            Play(history, memory, song, 50, 24);
            check(memory.Current is { Realigned: true } seek && seek.TrackId == songId && Math.Abs(seek.CachedSec(history.Written) - 74) < .05,
                "Seek inside the app re-aligns the cached waveform to the new playhead");

            Play(history, memory, stranger, 0, 40);
            check(memory.Current == null, "Alignment that diverges (another song) drops back to live-only");

            (history, memory) = Deck(cache, () => true);
            Play(history, memory, song, 0, 20);
            memory.Forget();
            check(cache.TryGet(songId) == null, "Forget this track removes the matched entry");
            memory.Step();
            check(memory.Current == null, "Forgotten track is no longer drawn from memory");

            string offDirectory = Path.Combine(directory, "off");
            var offCache = new TrackCache(offDirectory);
            bool on = false;
            (history, memory) = Deck(offCache, () => on);
            Play(history, memory, stranger, 0, 30);
            memory.Finish();
            check(memory.Current == null && !Directory.Exists(offDirectory) || Directory.GetFiles(offDirectory).Length == 0,
                "Cache switch off stores nothing");

            on = true;
            (history, memory) = Deck(offCache, () => on);
            Play(history, memory, stranger, 0, 30);
            memory.Finish();
            var files = Directory.Exists(offDirectory) ? Directory.GetFiles(offDirectory, "*.hmxc") : [];
            check(files.Length == 1 && files[0] != null && new FileInfo(files[0]).Length < 30 * Rate * 8 / 8,
                "An unknown track is stored as derived data only, far smaller than its audio");
            check(offCache.Find(AcousticFingerprint.Extract(stranger.AsSpan(10 * Rate * 2, 12 * Rate * 2), 0)) != null,
                "A stored track is recognised the next time it plays");

            (history, memory) = Deck(offCache, () => on);
            Play(history, memory, stranger, 0, 24);
            check(memory.Current != null, "A replayed stored track is matched on the live deck");
            offCache.Clear();
            memory.Discard();
            memory.Step();
            check(memory.Current == null && Directory.GetFiles(offDirectory, "*.hmxc").Length == 0, "Clear cache removes every track and live matches");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
        PlayheadClock(check);
    }

    // A stream joined 37.5 s into a cached track: Auto Mix reads the analysis in the playhead clock, only while the match is fresh and trustworthy.
    private static void PlayheadClock(Action<bool, string> check)
    {
        const double offset = 37.5;
        long baseFrame = 10 * Rate;
        var grid = new TempoGrid(120, 22050, 44100 * 1.25, true, 1, 1, 44100 * 1.25);
        var analysis = new TrackAnalysis(grid, null, 0, DjGenre.House, 240, [new TrackSegment(180, 240, DjSection.Outro, 3, 0, -14)], 8, 200);
        var entry = new TrackCacheEntry(Guid.NewGuid(), null, 240, [], [], analysis, true);
        LiveMatch Live(double confidence, bool partial, long verified) =>
            new(new TrackMatch(entry, (int)(offset * AcousticFingerprint.TicksPerSecond), confidence, partial),
                new TrackWaveform("", [], default, 240, 1), baseFrame, false, verified);
        double now = baseFrame + 30.0 * Rate;
        double shift = baseFrame / (double)Rate - offset;

        var a = Live(.9, false, (long)now).AnalysisAt(now);
        check(a != null && Math.Abs(a.MixOutSec - (200 + shift)) < 1e-6 && Math.Abs(a.MixInSec - (8 + shift)) < 1e-6 &&
              Math.Abs(a.DurationSec - (240 + shift)) < 1e-6 && Math.Abs(a.Segments[0].StartSec - (180 + shift)) < 1e-6 &&
              Math.Abs(a.Tempo!.OriginFrame - (grid.OriginFrame + shift * Rate)) < 1e-3 &&
              Math.Abs(a.Tempo.PhraseStartFrame - (grid.PhraseStartFrame + shift * Rate)) < 1e-3,
            "A matched deck at a 37.5 s offset gives MixOut, sections and grid in playhead time");
        // the cached track position 200 s is heard when the playhead reaches MixOutSec
        check(a != null && Math.Abs(Live(.9, false, 0).CachedSec(a.MixOutSec * Rate) - 200) < 1e-6,
            "The shifted MixOut is the playhead moment the cached track reaches that time");
        check(Live(.9, true, (long)now).AnalysisAt(now) == null, "A partial match gives Auto Mix no analysis");
        check(Live(.4, false, (long)now).AnalysisAt(now) == null, "A low-confidence match gives Auto Mix no analysis");
        check(Live(.9, false, (long)now).AnalysisAt(now + 6 * Rate) == null && Live(.9, false, (long)now).AnalysisAt(now + 3 * Rate) != null,
            "A match older than one re-check gives Auto Mix no analysis");
    }

    private static (SampleHistory, LiveTrackMemory) Deck(TrackCache cache, Func<bool> enabled)
    {
        var history = new SampleHistory();
        return (history, new LiveTrackMemory(history, cache, enabled, () => true));
    }

    private static void Play(SampleHistory history, LiveTrackMemory memory, float[] source, double from, int seconds)
    {
        int start = (int)Math.Round(from * Rate) * 2;
        for (int s = 0; s < seconds; s++)
        {
            history.Write(source.AsSpan(start + s * Rate * 2, Rate * 2));
            memory.Step();
        }
    }
}
