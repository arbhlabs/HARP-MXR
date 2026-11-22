using MixDock.Audio;

internal static class TrackCacheChecks
{
    private const int Rate = 44100;

    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine("dist", "wave-cache-tests-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "match");
        string evict = Path.Combine(root, "eviction");
        var original = Song(44, 21);
        var stranger = Song(10, 342);
        var columns = WaveformAnalyzer.AnalyzeAll(original);
        var reference = AcousticFingerprint.Extract(original);
        var songId = Guid.NewGuid();
        var metadata = new TrackMetadata("Prairie Glass", "Test Fixture", "Night Set", 44);
        var entry = new TrackCacheEntry(songId, metadata, 44, columns, reference, null, true);
        var cache = new TrackCache(directory);
        try
        {
            check(reference.Length > 1000, "Fingerprint extracts time-frequency landmarks from generated music");
            check(columns.Any(c => c.Rms > .02 && c.LowRms > .02 && c.MidRms > .02) &&
                columns.All(c => c.Rms <= c.Peak + 1e-6f), "Waveform contains true band and whole-signal RMS alongside peaks");
            check(cache.Put(entry), "Derived track cache writes a versioned entry");
            CheckMatch(cache.Find(Query(original, 0, 8, gain: .27), metadata), 0, partial: false, check,
                "Fingerprint tolerates a signal gain change");
            CheckMatch(cache.Find(Query(original, 0, 9, gain: .65, noise: .016f)), 0, partial: false, check,
                "Fingerprint tolerates an added noise floor without metadata");
            CheckMatch(cache.Find(Query(original, 8.173, 30), liveDurationSec: 30), 8.173, partial: true, check,
                "Thirty-second edit aligns and is marked partial");
            var match = cache.Find(Query(original, 15.311, 8, firstSec: 4.427));
            CheckMatch(match, 15.311 - 4.427, partial: true, check, "Different start times align on the capture clock");
            var otherMetadata = new TrackMetadata("Browser tab", "", "", 44);
            CheckMatch(cache.Find(Query(original, 0, 8), otherMetadata), 0, partial: true, check,
                "Inconsistent media-session metadata keeps a verified partial match");
            check(cache.Find(AcousticFingerprint.Extract(stranger), metadata) == null, "Same metadata cannot match a different song");
            if (match == null) throw new InvalidOperationException("The offset fixture could not be aligned.");
            CheckMatch(cache.Recheck(match, Query(original, 22.311, 8, firstSec: 11.427)),
                15.311 - 4.427, partial: true, check, "Recheck validates alignment later in playback");
            check(cache.Recheck(match, Query(stranger, 0, 8, firstSec: 11.427)) == null, "Recheck drops diverging audio");
            var editAudio = JoinEdit(original, stranger);
            CheckMatch(cache.Find(AcousticFingerprint.Extract(editAudio), liveDurationSec: 20), 0,
                partial: true, check, "A long contiguous section matches despite an edited ending");
            check(AcousticFingerprint.Extract(new float[Rate * 6 * 2]).Length == 0, "Silence does not produce a track fingerprint");

            var reopened = new TrackCache(directory);
            var loaded = reopened.TryGet(songId);
            check(loaded is { Complete: true } && loaded.Metadata == metadata && loaded.Fingerprint.SequenceEqual(reference),
                "Cache reopens persisted identity and landmarks");
            check(loaded?.Overview.Select(l => l.ColumnsPerSlice).SequenceEqual([5, 25]) == true &&
                loaded.Overview[1].Columns.Length == 176 && Math.Abs(loaded.Columns[19].Rms - columns[19].Rms) < .004 &&
                Math.Abs(loaded.Columns[19].HighRms - columns[19].HighRms) < .004, "Peak and RMS pyramid round-trips at 10, 50 and 250 ms");
            var shifted = Query(original, 27.642, 8);
            CheckMatch(reopened.Find(shifted), 27.642, partial: true, check, "Reopened cache matches later audio without metadata");

            var files = Directory.GetFiles(directory);
            check(files.Length == 1 && Path.GetExtension(files[0]) == ".hmxc" &&
                File.ReadAllBytes(files[0]).AsSpan(0, 8).SequenceEqual("HMXRCHE1"u8) &&
                new FileInfo(files[0]).Length < original.LongLength / 15, "Cache writes only compact derived data and no playable audio file");

            long fileBytes = new FileInfo(files[0]).Length;
            var small = new TrackCache(evict, fileBytes * 2 + 128);
            var b = entry with { Id = Guid.NewGuid() };
            var c = entry with { Id = Guid.NewGuid() };
            check(small.Put(entry) && small.Put(b), "Two derived tracks fit inside the cache cap");
            check(small.TryGet(songId) != null, "Reading an entry refreshes its LRU position");
            check(small.Put(c) && small.TryGet(songId) != null && small.TryGet(b.Id) == null && small.TryGet(c.Id) != null &&
                Directory.GetFiles(evict).Sum(p => new FileInfo(p).Length) <= fileBytes * 2 + 128, "Byte cap evicts the least-recently used entry");
            check(small.Forget(c.Id) && small.TryGet(c.Id) == null, "Forget removes the selected track");
            small.Clear();
            check(Directory.GetFiles(evict).Length == 0 && new TrackCache(evict).TryGet(songId) == null, "Clear removes the derived cache across sessions");
        }
        finally
        {
            foreach (string subdirectory in new[] { directory, evict })
            {
                if (!Directory.Exists(subdirectory)) continue;
                foreach (string file in Directory.GetFiles(subdirectory)) File.Delete(file);
                Directory.Delete(subdirectory);
            }
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    private static void CheckMatch(TrackMatch? match, double offset, bool partial, Action<bool, string> check, string label)
    {
        check(match != null && Math.Abs(match.OffsetSec - offset) <= .05 && match.Partial == partial && match.Confidence >= .55,
            $"{label} (offset {match?.OffsetSec.ToString("F3") ?? "none"} s, expected {offset:F3} s, confidence {match?.Confidence:P0}, partial {match?.Partial})");
    }

    private static FingerprintPoint[] Query(float[] source, double start, int seconds, double firstSec = 0, double gain = 1, float noise = 0)
    {
        int from = (int)Math.Round(start * Rate) * 2;
        var stereo = new float[seconds * Rate * 2];
        var random = new Random(7843);
        for (int i = 0; i < stereo.Length; i++) stereo[i] = (float)(source[from + i] * gain) + noise * (2 * random.NextSingle() - 1);
        return AcousticFingerprint.Extract(stereo, (long)Math.Round(firstSec * Rate));
    }

    private static float[] JoinEdit(float[] original, float[] substitution)
    {
        var audio = new float[20 * Rate * 2];
        original.AsSpan(0, 12 * Rate * 2).CopyTo(audio);
        substitution.AsSpan(0, 8 * Rate * 2).CopyTo(audio.AsSpan(12 * Rate * 2));
        return audio;
    }

    internal static float[] Song(int seconds, int seed)
    {
        var result = new float[seconds * Rate * 2];
        var random = new Random(seed);
        int frame = 0, note = 0;
        while (frame < result.Length / 2)
        {
            double length = .15 + random.NextDouble() * .27;
            int end = Math.Min(result.Length / 2, frame + (int)(length * Rate));
            double bass = 56 + 8 * random.Next(13);
            double mid = 295 + 18 * random.Next(16);
            double upper = 680 + 27 * random.Next(17);
            double high = 1630 + 41 * random.Next(17);
            double treble = 2700 + 53 * random.Next(25);
            int duration = end - frame;
            for (int i = 0; i < duration; i++)
            {
                double t = i / (double)Rate, phase = 2 * Math.PI * t;
                double e = Math.Min(1, t / .025) * Math.Min(1, (duration - i) / (.035 * Rate));
                double left = .23 * Math.Sin(bass * phase) + .16 * Math.Sin(mid * phase) +
                    .12 * Math.Sin(upper * phase) + .07 * Math.Sin(high * phase);
                double sparkle = (note % 3 == 1 ? .07 : .11) * Math.Sin(treble * phase);
                result[(frame + i) * 2] = (float)(e * (left + sparkle));
                result[(frame + i) * 2 + 1] = (float)(e * (left + .65 * sparkle));
            }
            frame = end;
            note++;
        }
        return result;
    }
}
