using System.Runtime.InteropServices;

namespace MixDock.Audio;

/// <summary>What the media session says a deck's source app is playing. PositionSec is null when no timeline is published.</summary>
public sealed record NowPlaying(TrackMetadata Meta, double? PositionSec);

/// <summary>A live deck recognised in the cache. Capture frames are absolute history frames of the deck.</summary>
public sealed record LiveMatch(TrackMatch Match, TrackWaveform Waveform, long BaseFrame, bool Realigned, long VerifiedFrame = 0)
{
    /// <summary>Longest a match may go without a successful re-check (one re-check interval plus the worker poll).</summary>
    public const double MaxAgeSec = 4.5;
    public const double MinConfidence = .6;
    /// <summary>Added to a cached-track time to get the deck playhead clock (absolute input frames / 44100).</summary>
    public double ShiftSec => BaseFrame / 44100.0 - Match.OffsetSec;
    /// <summary>The cached analysis in the deck playhead clock, or null when the match is partial, weak or older than one re-check.</summary>
    public TrackAnalysis? AnalysisAt(double playheadFrame) =>
        Analysis is { } a && !Match.Partial && Match.Confidence >= MinConfidence && double.IsFinite(playheadFrame) &&
        playheadFrame - VerifiedFrame <= MaxAgeSec * 44100 ? a.Shifted(ShiftSec) : null;
    public Guid TrackId => Match.Track.Id;
    public TrackAnalysis? Analysis => Match.Track.Analysis;
    /// <summary>Position in the cached track for an absolute capture frame.</summary>
    public double CachedSec(double captureFrame) => (captureFrame - BaseFrame) / 44100.0 + Match.OffsetSec;
}

/// <summary>
/// Waveform memory for one live deck. A background worker reads the deck's <see cref="SampleHistory"/> (the audio thread
/// only writes it, as before), fingerprints it, looks the track up in the <see cref="TrackCache"/> and re-checks the alignment
/// every few seconds. Unrecognised tracks are stored when they end. Only derived data (peaks, fingerprint, analysis) is
/// kept; no sample ever reaches the disk.
/// </summary>
public sealed class LiveTrackMemory : IDisposable
{
    private const int Rate = 44100, Hop = AcousticFingerprint.HopFrames;
    private const int Tps = (int)AcousticFingerprint.TicksPerSecond;
    private const int WindowFrames = AcousticFingerprint.LookaheadFrames + 6 * Rate;
    private const int FirstQueryTicks = 8 * Tps, RetryTicks = 3 * Tps, RecheckTicks = 4 * Tps;
    private const int QueryTicks = 30 * Tps, RecentTicks = 10 * Tps, MaxTicks = 900 * Tps, MinSaveTicks = 20 * Tps;
    private const int MaxFails = 3;

    private readonly SampleHistory _history;
    private readonly TrackCache _cache;
    private readonly Func<bool> _enabled, _active;
    private readonly List<FingerprintPoint> _points = [];
    private float[] _win = [];
    private float[] _chunk = [];

    private LiveMatch? _current;
    private volatile NowPlaying? _now;
    private long _nowFrame;
    private volatile bool _forget, _discardAll;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private bool _open, _discard;
    private int _epoch, _fpTick, _lastQuery, _lastRecheck, _fails;
    private long _base, _ingested;
    private Guid _ownId;
    private TrackMetadata? _meta;
    private double? _startPos;
    private WaveformAnalyzer? _wave;
    private TrackAnalysisBuilder? _builder;

    public LiveTrackMemory(SampleHistory history, TrackCache cache, Func<bool> enabled, Func<bool> active)
    {
        _history = history; _cache = cache; _enabled = enabled; _active = active;
    }

    /// <summary>Asks the media session for the current track; polled every few seconds by the worker.</summary>
    public Func<Task<NowPlaying?>>? Probe { get; set; }
    public LiveMatch? Current => Volatile.Read(ref _current);

    /// <summary>The media-session reading and the capture frame it was taken at.</summary>
    public void Observe(NowPlaying? now, long atFrame) { _nowFrame = atFrame; _now = now; }

    /// <summary>Drops the matched / recording track from the cache and does not store this play again.</summary>
    public void Forget() { if (Current is { } c) _cache.Forget(c.TrackId); _forget = true; }
    /// <summary>Stops using or recording anything held for this deck (call after the cache was cleared).</summary>
    public void Discard() => _discardAll = true;

    public void Start()
    {
        if (_loop != null) return;
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(async () =>
        {
            long probed = 0;
            while (!cts.IsCancellationRequested)
            {
                Step();
                if (Probe != null && _enabled() && _active() && Environment.TickCount64 - probed > 3000)
                {
                    probed = Environment.TickCount64;
                    try { var np = await Probe(); Observe(np, _history.Written); }
                    catch (Exception ex) { Log.Write("media probe: " + ex.Message); }
                }
                try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { break; }
            }
            Finish();
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(4000); } catch { /* worker already logged */ }
        _loop = null;
    }

    /// <summary>One worker iteration. Public so tests can drive it deterministically; use from one thread.</summary>
    public void Step()
    {
        try
        {
            if (_discardAll) { _discardAll = false; Drop(); }
            if (_forget) { _forget = false; ForgetCurrent(); }
            if (!_enabled()) { Drop(); return; }
            if (!_active()) { Finish(); return; }
            long written = _history.Written;
            int epoch = _history.Epoch;
            if (!_open) Open(written, epoch);
            else if (epoch != _epoch || written < _ingested || written - _ingested > SampleHistory.CapacityFrames - 3 * Rate)
            { Flush(); Open(written, epoch); }
            if (TrackChanged()) { Flush(); Open(written, epoch); }
            AdoptMetadata();
            Ingest(written);
            Identify();
        }
        catch (Exception ex) { Log.Write("track memory: " + ex.Message); Drop(); }
    }

    /// <summary>Saves what was learnt (if anything) and goes idle.</summary>
    public void Finish()
    {
        try { Flush(); } catch (Exception ex) { Log.Write("track memory save: " + ex.Message); Drop(); }
    }

    private void Open(long written, int epoch)
    {
        _open = true; _discard = false;
        _epoch = epoch; _base = _ingested = written; _fpTick = _lastQuery = _lastRecheck = _fails = 0;
        _ownId = Guid.NewGuid(); _meta = null; _startPos = null;
        _points.Clear();
        _wave = new WaveformAnalyzer(MaxTicks / Tps);
        _builder = new TrackAnalysisBuilder();
        Volatile.Write(ref _current, null);
    }

    private void Drop()
    {
        _open = false; _points.Clear(); _wave = null; _builder = null; _meta = null;
        Volatile.Write(ref _current, null);
        _chunk = []; _win = [];
    }

    private bool TrackChanged()
    {
        var now = _now;
        return now != null && _meta != null && !string.IsNullOrWhiteSpace(_meta.Title) && !string.IsNullOrWhiteSpace(now.Meta.Title) && !now.Meta.SameSong(_meta);
    }

    private void AdoptMetadata()
    {
        var now = _now;
        if (now == null || _meta != null && !string.IsNullOrWhiteSpace(_meta.Title) && _startPos != null) return;
        _meta = now.Meta;
        if (now.PositionSec is double p && double.IsFinite(p) && _startPos == null)
            _startPos = Math.Max(0, p - Math.Max(0, _nowFrame - _base) / (double)Rate);
    }

    private bool Collecting => !_discard && (Current is not { } c || !c.Match.Track.Complete);

    private void Ingest(long written)
    {
        while (written - _ingested >= Rate / 2)
        {
            int n = (int)Math.Min(Rate, written - _ingested);
            if (Collecting && _wave!.Count < _wave.Capacity - 1000 && ReadFrames(_ingested, n, ref _chunk))
            {
                _wave.Feed(_chunk.AsSpan(0, n * 2));
                _builder!.Feed(_chunk.AsSpan(0, n * 2));
            }
            _ingested += n;
        }
        while (_fpTick < MaxTicks)
        {
            long from = _base + (long)_fpTick * Hop;
            if (written - from < WindowFrames) break;
            if (!ReadFrames(from, WindowFrames, ref _win)) { _fpTick = (int)((written - _base) / Hop); break; }
            _points.AddRange(AcousticFingerprint.Extract(_win.AsSpan(0, WindowFrames * 2), (long)_fpTick * Hop));
            _fpTick += 1 + (WindowFrames - 4096) / Hop - 15;
        }
    }

    /// <summary>Copies frames out of the history ring; false if the writer overwrote them meanwhile.</summary>
    private bool ReadFrames(long from, int frames, ref float[] buffer)
    {
        if (buffer.Length < frames * 2) buffer = new float[frames * 2];
        long written = _history.Written;
        if (!_history.Has(from, written) || !_history.Has(from + frames - 1, written)) return false;
        for (int i = 0; i < frames; i++) { buffer[2 * i] = _history.L(from + i); buffer[2 * i + 1] = _history.R(from + i); }
        return _history.Written - from <= SampleHistory.CapacityFrames - 4096;
    }

    private ReadOnlySpan<FingerprintPoint> Window(int ticks)
    {
        int min = _fpTick - ticks, i = _points.Count;
        while (i > 0 && _points[i - 1].Tick >= min) i--;
        return CollectionsMarshal.AsSpan(_points)[i..];
    }

    private void Identify()
    {
        if (_discard) return;
        var cur = Current;
        double? duration = _meta?.DurationSec > 0 ? _meta.DurationSec : null;
        if (cur == null)
        {
            if (_fpTick < FirstQueryTicks || _fpTick - _lastQuery < RetryTicks) return;
            _lastQuery = _fpTick;
            if (_cache.Find(Window(QueryTicks), _meta, duration) is { } m && m.Track.Id != _ownId) Adopt(m, false);
            return;
        }
        if (_fpTick - _lastRecheck < RecheckTicks) return;
        _lastRecheck = _fpTick;
        if (_cache.Recheck(cur.Match, Window(5 * RecheckTicks), _meta) is { } same)
        {
            _fails = 0;
            Volatile.Write(ref _current, cur with { Match = same, VerifiedFrame = _ingested });
            return;
        }
        // The playhead may have moved (a seek) or the audio is another edit: look again before giving up.
        if (_cache.Find(Window(RecentTicks), _meta, duration) is { } found && found.Track.Id != _ownId)
        {
            _fails = 0;
            if (found.Track.Id == cur.TrackId) Volatile.Write(ref _current, cur with { Match = found, Realigned = true, VerifiedFrame = _ingested });
            else Adopt(found, true);
        }
        else if (++_fails >= MaxFails)
        {
            Log.Write("track memory: alignment lost, live waveform only");
            Flush(); Open(_history.Written, _history.Epoch);
        }
    }

    private void Adopt(TrackMatch match, bool realigned)
    {
        var cols = match.Track.Columns;
        float max = 0; foreach (var c in cols) max = Math.Max(max, c.Peak);
        var wf = new TrackWaveform("", cols, BeatGrid.Estimate(cols), match.Track.DurationSec, max > .01f ? Math.Min(4f, .95f / max) : 1f);
        _fails = 0; _lastRecheck = _fpTick;
        Volatile.Write(ref _current, new LiveMatch(match, wf, _base, realigned, _ingested));
        Log.Write($"track memory: matched {match.Track.Metadata?.Title ?? "unknown"} offset {match.OffsetSec:F2}s confidence {match.Confidence:P0}{(match.Partial ? " partial" : "")}");
    }

    private void ForgetCurrent()
    {
        if (Current is { } c) _cache.Forget(c.TrackId);
        _cache.Forget(_ownId);
        _discard = true;
        Volatile.Write(ref _current, null);
    }

    private void Flush()
    {
        if (!_open) return;
        bool save = !_discard && _enabled() && _wave != null && _fpTick >= MinSaveTicks && _points.Count > 0;
        var cur = Current;
        if (save && cur == null) SaveNew();
        else if (save && cur is { Realigned: false } && !cur.Match.Track.Complete) Merge(cur);
        Drop();
    }

    private double CapturedSec => _wave!.Count / (double)WaveformAnalyzer.ColumnsPerSecond;

    private void SaveNew()
    {
        double shift = _startPos is > 3 ? _startPos.Value : 0;
        bool knownStart = _startPos is <= 3;
        double duration = _meta?.DurationSec > 0 ? _meta.DurationSec : shift + CapturedSec;
        bool complete = knownStart && _meta?.DurationSec > 0 && CapturedSec >= duration - 3;
        int pad = (int)(shift * WaveformAnalyzer.ColumnsPerSecond), tickShift = (int)(shift * Tps);
        var live = _wave!.ToArray();
        var cols = new WaveColumn[pad + live.Length];
        live.CopyTo(cols, pad);
        var points = _points.Select(p => p with { Tick = p.Tick + tickShift }).ToArray();
        var analysis = _builder!.Analyze(DjGenre.Auto, complete);
        _cache.Put(new TrackCacheEntry(_ownId, _meta, Math.Max(duration, cols.Length / (double)WaveformAnalyzer.ColumnsPerSecond),
            cols, points, analysis, complete));
    }

    /// <summary>A repeated play fills in what an earlier partial capture was missing.</summary>
    private void Merge(LiveMatch cur)
    {
        var old = cur.Match.Track;
        int offsetTicks = cur.Match.OffsetTicks, offsetCols = offsetTicks * (WaveformAnalyzer.ColumnsPerSecond / Tps);
        var live = _wave!.ToArray();
        int start = Math.Min(0, offsetCols), end = Math.Max(old.Columns.Length, offsetCols + live.Length);
        if (end - start <= old.Columns.Length + 2 * WaveformAnalyzer.ColumnsPerSecond) return;
        int shift = -start;
        var cols = new WaveColumn[end - start];
        old.Columns.CopyTo(cols, shift);
        for (int i = 0; i < live.Length; i++) if (offsetCols + i >= 0 && offsetCols + i + shift < cols.Length) cols[offsetCols + i + shift] = live[i];
        int tickShift = shift / (WaveformAnalyzer.ColumnsPerSecond / Tps), last = old.Fingerprint.Length == 0 ? -1 : old.Fingerprint.Max(p => p.Tick);
        var points = old.Fingerprint.Select(p => p with { Tick = p.Tick + tickShift })
            .Concat(_points.Select(p => p with { Tick = p.Tick + offsetTicks + tickShift }).Where(p => p.Tick > last + tickShift)).ToArray();
        bool complete = offsetCols <= 150 && (offsetCols + live.Length) / (double)WaveformAnalyzer.ColumnsPerSecond >= old.DurationSec - 3;
        var analysis = old.Analysis ?? (complete ? _builder!.Analyze(DjGenre.Auto, true) : null);
        _cache.Put(new TrackCacheEntry(old.Id, old.Metadata ?? _meta, Math.Max(old.DurationSec, cols.Length / (double)WaveformAnalyzer.ColumnsPerSecond),
            cols, points, analysis, old.Complete || complete));
    }
}
