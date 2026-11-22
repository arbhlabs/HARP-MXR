namespace MixDock.Audio;

/// <summary>Whole-file waveform for a file deck (decoded once in the background when the song loads), or a cached track for a recognised live deck.</summary>
public sealed partial record TrackWaveform(string Path, WaveColumn[] Columns, BeatGrid Grid, double DurationSec, float Gain)
{
    public TrackAnalysis? Analysis { get; init; }
}
