using NAudio.Wave;

namespace MixDock.Audio;

/// <summary>Whole-file waveform for a file deck: decoded once in the background when the song loads.</summary>
public sealed record TrackWaveform(string Path, WaveColumn[] Columns, BeatGrid Grid, double DurationSec, float Gain)
{
    public static TrackWaveform Analyze(string path, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var fr = new AudioFileReader(path);
        ISampleProvider p = fr;
        if (p.WaveFormat.Channels == 1) p = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(p);
        if (p.WaveFormat.SampleRate != 44100) p = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(p, 44100);
        var a = new WaveformAnalyzer((int)Math.Ceiling(fr.TotalTime.TotalSeconds) + 4);
        var buf = new float[44100 * 2];
        int got;
        while ((got = p.Read(buf.AsSpan())) > 0) { ct.ThrowIfCancellationRequested(); a.Feed(buf.AsSpan(0, got & ~1)); }
        var cols = a.ToArray();
        var grid = BeatGrid.Estimate(cols);
        float max = 0; foreach (var c in cols) max = Math.Max(max, c.Peak);
        Log.Write($"waveform: {System.IO.Path.GetFileName(path)} {cols.Length} columns, grid {grid.Bpm:F2} BPM first beat {grid.FirstBeatSec:F3}s, peak {max:F2}, {sw.ElapsedMilliseconds} ms");
        return new TrackWaveform(path, cols, grid, cols.Length / (double)WaveformAnalyzer.ColumnsPerSecond, max > 0.01f ? Math.Min(4f, 0.95f / max) : 1f);
    }
}
