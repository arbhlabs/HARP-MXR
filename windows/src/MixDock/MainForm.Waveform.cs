using MixDock.Audio;
using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

/// <summary>Feeds the deck waveforms: whole-file analysis for file decks, the capture analyzer for live app decks.</summary>
public sealed partial class MainForm
{
    private readonly WaveformStrip _waves = new();
    private readonly TrackWaveform?[] _tracks = new TrackWaveform?[2];
    private readonly string?[] _trackPaths = new string?[2];
    private readonly CancellationTokenSource?[] _trackCts = new CancellationTokenSource?[2];
    /// <summary>Cue point per deck in track seconds (auto-cue: first beat of the analysed grid).</summary>
    private readonly double?[] _cueSec = new double?[2];
    /// <summary>Loop region per deck in track seconds; drawn when set (the engine has no looping yet).</summary>
    private readonly (double a, double b)?[] _loop = new (double, double)?[2];

    private TrackCache _trackCache = null!;
    private readonly LiveTrackMemory?[] _memory = new LiveTrackMemory?[2];
    private readonly string?[] _memoryApp = new string?[2];

    private void InitWaveforms()
    {
        // The offline UI check must never touch the real cache.
        _trackCache = new TrackCache(_preview ? Path.Combine(Path.GetTempPath(), "harp-mxr-preview-" + Guid.NewGuid().ToString("N")) : null);
        for (int i = 0; i < 2; i++)
        {
            int d = i;
            var src = d == 0 ? _engine.DeckA : _engine.DeckB;
            _memory[d] = new LiveTrackMemory(src.History, _trackCache, () => _config.WaveformMemory, () => src.ActivePid is > 0 && !src.IsFile)
            { Probe = () => MediaControl.GetNowPlayingAsync(Volatile.Read(ref _memoryApp[d])) };
            if (!_preview) _memory[d]!.Start();
        }
        _waves.Input = d =>
        {
            var input = WaveInputFor(d);
            if (_autoMix?.Plan is not { } plan || input.Empty) return input;
            int outgoing = _autoMix.Outgoing;
            double until = (plan.StartSec - _mixHost!.PlayheadSec(outgoing)) / Math.Max(.25, _mixHost.Rate(outgoing));
            if (!double.IsFinite(until)) return input;
            return input with { Transition = plan, TransitionStart = input.HeadSec + until * _mixHost.Rate(d), Outgoing = d == outgoing };
        };
        _waves.SeekRequested += (d, f) => { var ds = d == 0 ? _engine.DeckA : _engine.DeckB; if (ds.IsFile) ds.SeekTo(TimeSpan.FromSeconds(ds.Duration.TotalSeconds * f)); };
    }

    private WaveInput WaveInputFor(int d)
    {
        var ds = d == 0 ? _engine.DeckA : _engine.DeckB;
        var rd = d == 0 ? _engine.ReaderA : _engine.ReaderB;
        EnsureTrack(d, ds.IsFile ? ds.FilePath : null);
        if (ds.IsFile)
        {
            var tr = Volatile.Read(ref _tracks[d]);
            if (tr != null && tr.Path != ds.FilePath) tr = null;
            double head = Math.Max(0, ds.Position.TotalSeconds - rd.BufferedMs / 1000.0);   // what is heard, not what is read
            var loop = _loop[d];
            return new WaveInput(tr ?? Empty, null, head, !ds.Paused && !ds.Ended, rd.Rate, tr?.Grid ?? default, _cueSec[d], loop?.a, loop?.b, tr == null);
        }
        if (ds.ActivePid is > 0)
        {
            Volatile.Write(ref _memoryApp[d], Panel(d).SelectedApp?.Name);
            var w = ds.Wave;
            if (_memory[d]?.Current is { } known)
            {
                // recognised: the whole cached track, positioned by the measured offset
                double heardFrame = (w.Count - rd.BufferedMs / 10.0) * WaveformAnalyzer.FramesPerColumn;
                return new WaveInput(known.Waveform, null, known.CachedSec(heardFrame), !_livePaused[d], 1, known.Waveform.Grid, null, null, null, false);
            }
            double head = (w.Count - rd.BufferedMs / 10.0) / WaveformAnalyzer.ColumnsPerSecond;
            double bpm = ds.Bpm.Bpm, ph = DeckSync.HeardPhase(_engine, d);
            // live grid: anchor the heard beat phase at the playhead
            var grid = bpm > 0 && !double.IsNaN(ph) ? new BeatGrid(bpm, head - ph * 60.0 / bpm) : default;
            // the look-ahead delay line is already analysed: draw it to the right of the playhead
            return new WaveInput(null, w, head, !_livePaused[d], 1, grid, null, null, null, false, rd.BufferedMs / 1000.0);
        }
        return default;
    }
    /// <summary>Drops the focused deck's remembered track (and does not store this play again).</summary>
    private void ForgetFocusedTrack() => _memory[Math.Clamp(_focus, 0, 1)]?.Forget();

    private void ClearTrackCache()
    {
        _trackCache.Clear();
        foreach (var m in _memory) m?.Discard();
    }

    private string? MemoryTag(int d) => _config.WaveformMemory && _memory[d]?.Current is { } m
        ? (m.Match.Partial ? "PARTIAL MATCH " : "MATCH ") + (int)Math.Round(m.Match.Confidence * 100) + "%" : null;
    private static readonly TrackWaveform Empty = new("", [], default, 0, 1);

    private void EnsureTrack(int d, string? path)
    {
        if (path == _trackPaths[d]) return;
        _trackCts[d]?.Cancel(); _trackCts[d] = null;
        _trackPaths[d] = path; Volatile.Write(ref _tracks[d], null); _cueSec[d] = null; _loop[d] = null;
        if (path == null) return;
        var cts = new CancellationTokenSource(); _trackCts[d] = cts;
        _ = Task.Run(() =>
        {
            try
            {
                var tr = TrackWaveform.Analyze(path, cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (_closing || cts.IsCancellationRequested || _trackPaths[d] != path) return;
                    Volatile.Write(ref _tracks[d], tr);
                    _cueSec[d] = tr.Grid.Valid ? tr.Grid.FirstBeatSec : 0;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("waveform analysis failed: " + ex.Message); }
        });
    }
}
