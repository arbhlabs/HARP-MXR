using MixDock.Audio;
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

    private void InitWaveforms()
    {
        _waves.Input = WaveInputFor;
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
            var w = ds.Wave;
            double head = (w.Count - rd.BufferedMs / 10.0) / WaveformAnalyzer.ColumnsPerSecond;
            double bpm = ds.Bpm.Bpm, ph = DeckSync.HeardPhase(_engine, d);
            // live grid: anchor the heard beat phase at the playhead
            var grid = bpm > 0 && !double.IsNaN(ph) ? new BeatGrid(bpm, head - ph * 60.0 / bpm) : default;
            return new WaveInput(null, w, head, true, 1, grid, null, null, null, false);
        }
        return default;
    }
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
                    if (_trackPaths[d] != path) return;
                    Volatile.Write(ref _tracks[d], tr);
                    _cueSec[d] = tr.Grid.Valid ? tr.Grid.FirstBeatSec : 0;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("waveform analysis failed: " + ex.Message); }
        });
    }
}
