namespace MixDock.Audio;

/// <summary>One snapshot of what a stream plays at <see cref="Sec"/> (seconds in the deck's history clock).</summary>
public readonly record struct TempoKeyPoint(double Sec, double Bpm, double Confidence, double OriginSec, bool DownKnown,
    MusicalKey? Key, double KeyConfidence);

/// <summary>Windowed tempo and key history of one stream. An hour-long DJ mix changes tempo and key inside the stream,
/// so Auto Mix never trusts one global value: it asks for the local tempo, its drift and the stable key around a moment.</summary>
public sealed class TempoKeyMap
{
    private readonly List<TempoKeyPoint> _points = new();
    private readonly object _gate = new();
    public const int Capacity = 7200;

    public int Count { get { lock (_gate) return _points.Count; } }
    public TempoKeyPoint[] Points { get { lock (_gate) return _points.ToArray(); } }
    public void Clear() { lock (_gate) _points.Clear(); }

    public void Add(TempoKeyPoint point)
    {
        lock (_gate)
        {
            if (!double.IsFinite(point.Sec) || _points.Count > 0 && point.Sec <= _points[^1].Sec) return;
            _points.Add(point);
            if (_points.Count > Capacity) _points.RemoveRange(0, _points.Count - Capacity);
        }
    }

    private List<TempoKeyPoint> Window(double from, double to)
    {
        lock (_gate) return _points.Where(p => p.Sec >= from && p.Sec <= to).ToList();
    }

    /// <summary>Median tempo of the confident snapshots in the <paramref name="windowSec"/> before <paramref name="sec"/>
    /// (0 = unknown).</summary>
    public double LocalBpm(double sec, double windowSec = 12)
    {
        var bpms = Window(sec - windowSec, sec).Where(p => p.Bpm is >= 60 and <= 200 && p.Confidence >= .1)
            .Select(p => p.Bpm).OrderBy(b => b).ToArray();
        return bpms.Length == 0 ? 0 : bpms.Length % 2 == 1 ? bpms[bpms.Length / 2] : (bpms[bpms.Length / 2 - 1] + bpms[bpms.Length / 2]) / 2;
    }

    /// <summary>Tempo change in BPM per second, by least squares over the last <paramref name="windowSec"/>. 0 when the
    /// history is too short or the tempo jumps (a jump is a new track, not drift).</summary>
    public double DriftPerSec(double sec, double windowSec = 40)
    {
        var w = Window(sec - windowSec, sec).Where(p => p.Bpm is >= 60 and <= 200 && p.Confidence >= .1).ToList();
        if (w.Count < 6 || w[^1].Sec - w[0].Sec < windowSec * .4) return 0;
        double median = LocalBpm(sec, windowSec);
        w = w.Where(p => Math.Abs(p.Bpm - median) < median * .05).ToList();
        if (w.Count < 6) return 0;
        double mx = w.Average(p => p.Sec), my = w.Average(p => p.Bpm), sxy = 0, sxx = 0;
        foreach (var p in w) { sxy += (p.Sec - mx) * (p.Bpm - my); sxx += (p.Sec - mx) * (p.Sec - mx); }
        if (sxx <= 0) return 0;
        double slope = sxy / sxx;
        return Math.Abs(slope) < .002 ? 0 : Math.Clamp(slope, -.25, .25);
    }

    /// <summary>The key heard for most of the last <paramref name="windowSec"/> (confidence-weighted vote). A key change
    /// inside the stream takes over once it holds the majority of the window; brief detector flicker never does.</summary>
    public (MusicalKey? Key, double Confidence) KeyAt(double sec, double windowSec = 8)
    {
        var votes = new Dictionary<MusicalKey, double>();
        double total = 0;
        int count = 0;
        foreach (var p in Window(sec - windowSec, sec))
        {
            if (p.Key is not MusicalKey k || p.KeyConfidence <= 0) continue;
            votes[k] = votes.GetValueOrDefault(k) + p.KeyConfidence;
            total += p.KeyConfidence;
            count++;
        }
        if (total <= 0) return (null, 0);
        var best = votes.MaxBy(v => v.Value);
        double share = best.Value / total;
        return share < .5 ? (null, 0) : (best.Key, share * total / count);
    }

    /// <summary>Stable key changes inside the stream (seconds where the windowed key switched).</summary>
    public List<(double Sec, MusicalKey From, MusicalKey To)> KeyChanges(double windowSec = 8)
    {
        var changes = new List<(double, MusicalKey, MusicalKey)>();
        MusicalKey? last = null;
        foreach (var p in Points)
        {
            var (key, _) = KeyAt(p.Sec, windowSec);
            if (key is not MusicalKey k) continue;
            if (last is MusicalKey l && l != k) changes.Add((p.Sec, l, k));
            last = k;
        }
        return changes;
    }

    /// <summary>A beat grid for planning from <paramref name="sec"/>: the local tempo and the detector's latest beat
    /// position, moved to the beat nearest <paramref name="sec"/> so the drift extrapolation starts here.</summary>
    public TempoGrid? LocalGrid(double sec, TempoGrid? detector)
    {
        double bpm = LocalBpm(sec);
        if (detector == null || bpm <= 0) return detector;
        if (Math.Abs(bpm - detector.Bpm) > detector.Bpm * .03) bpm = detector.Bpm;
        double period = 44100 * 60 / bpm, origin = detector.OriginFrame;
        double beats = Math.Round((sec * 44100 - origin) / detector.PeriodFrames);
        double down = detector.DownKnown ? Math.Round(beats / 4) * 4 : beats;
        double anchor = origin + down * detector.PeriodFrames;
        return detector with { Bpm = bpm, PeriodFrames = period, OriginFrame = anchor, PhraseStartFrame = detector.DownKnown ? anchor : detector.PhraseStartFrame };
    }

    /// <summary>Analysis of a live or streaming deck from what has been heard so far: local grid and key, no structure
    /// (no seeking, no known ending). Times are seconds in the deck's history clock.</summary>
    public TrackAnalysis LiveAnalysis(double sec, TempoGrid? detector, DjGenre genre, double durationSec = 0)
    {
        var grid = LocalGrid(sec, detector);
        var (key, conf) = KeyAt(sec);
        var g = genre == DjGenre.Auto && grid != null ? TransitionPlanner.GuessGenre(grid.Bpm, 6) : genre;
        return new TrackAnalysis(grid, key, conf, g, durationSec, Array.Empty<TrackSegment>(), Math.Max(0, sec), 0);
    }
}

/// <summary>Feeds one stream through its own beat and key detectors and snapshots both every second into a
/// <see cref="TempoKeyMap"/>. Synchronous: used offline (tests, analysis) and by the deck's analysis thread.</summary>
public sealed class TempoKeyTracker
{
    public readonly BpmDetector Bpm = new() { Synchronous = true };
    public readonly KeyDetector Key = new() { Background = false };
    public TempoKeyMap Map { get; } = new();
    public double SnapshotSec { get; init; } = 1;
    private long _frames, _next = 44100;

    public long Frames => _frames;

    public void Feed(ReadOnlySpan<float> stereo)
    {
        int i = 0;
        while (i + 1 < stereo.Length)
        {
            int n = (int)Math.Min((stereo.Length - i) / 2, _next - _frames);
            if (n <= 0) { Snapshot(); _next += (long)(SnapshotSec * 44100); continue; }
            var part = stereo.Slice(i, n * 2);
            Bpm.Feed(part); Key.Feed(part);
            _frames += n; i += n * 2;
        }
        if (_frames >= _next) { Snapshot(); _next += (long)(SnapshotSec * 44100); }
    }

    private void Snapshot()
    {
        var g = Bpm.Grid;
        Map.Add(new TempoKeyPoint(_frames / 44100d, g?.Bpm ?? 0, g?.Confidence ?? 0, (g?.OriginFrame ?? 0) / 44100d,
            g?.DownKnown ?? false, Key.Key, Key.Confidence));
    }
}

/// <summary>Beat clock of a stream whose tempo drifts linearly: tempo(t) = bpm + drift·(t − origin), origin on a beat.</summary>
public readonly record struct DriftClock(double OriginSec, double Bpm, double DriftBpmPerSec)
{
    public double BeatsAt(double sec)
    {
        double tau = sec - OriginSec;
        return (Bpm * tau + DriftBpmPerSec * tau * tau / 2) / 60;
    }

    public double SecAt(double beats)
    {
        if (Math.Abs(DriftBpmPerSec) < 1e-9) return OriginSec + beats * 60 / Bpm;
        double disc = Bpm * Bpm + 2 * DriftBpmPerSec * 60 * beats;
        if (disc <= 0) return OriginSec + beats * 60 / Bpm;
        return OriginSec + (-Bpm + Math.Sqrt(disc)) / DriftBpmPerSec;
    }

    public double BpmAt(double sec) => Bpm + DriftBpmPerSec * (sec - OriginSec);

    /// <summary>The first multiple of <paramref name="stepBeats"/> at or after <paramref name="sec"/>.</summary>
    public double NextBoundary(double sec, double stepBeats) =>
        SecAt(Math.Ceiling(BeatsAt(sec) / stepBeats - 1e-6) * stepBeats);
}
