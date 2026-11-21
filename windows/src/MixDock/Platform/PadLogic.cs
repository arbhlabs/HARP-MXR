namespace MixDock.Platform;

/// <summary>
/// One deck's filter as the pad sees it: a latched base value that the left stick X creeps very slowly
/// (double precision, quadratic curve) plus the left stick Y spring offset on top. The spring always returns
/// to the latched value, never to 50. Effective = clamp(latched + spring, 0, 100).
/// </summary>
public sealed class FilterLatch
{
    /// <summary>Full tilt creep speed: 4.8 units/s = 0.08 filter units per 60 Hz frame.</summary>
    public const double MaxCreepPerSecond = 4.8;
    public double Latched { get; private set; } = 50;
    public double Spring { get; private set; }
    public double Effective => Math.Clamp(Latched + Spring, 0, 100);

    /// <summary>Rate-based fine sweep: speed = sign(x)·x²·max. Returns true when the latch moved.</summary>
    public bool Creep(double x, double dtSeconds)
    {
        if (x == 0 || dtSeconds <= 0) return false;
        double before = Latched;
        Latched = Math.Clamp(Latched + Math.Sign(x) * x * x * MaxCreepPerSecond * dtSeconds, 0, 100);
        return Latched != before;
    }
    /// <summary>Spring offset from the absolute AxisDriver output (0..100, 50 = rest). null = at rest.</summary>
    public void SetSpring(double? driverValue) => Spring = driverValue is double v ? v - 50 : 0;
    /// <summary>The user dragged the knob: the shown (effective) value becomes the new base.</summary>
    public void SetFromKnob(double effective) => Latched = Math.Clamp(effective - Spring, 0, 100);
    public void ResetLatch() => Latched = 50;
    public void Load(double latched) { Latched = Math.Clamp(latched, 0, 100); Spring = 0; }
}

/// <summary>Tap tempo: average of the last taps, restarts after a 2 s pause. Phase is anchored on the last tap.</summary>
public sealed class TapTempo
{
    private readonly List<long> _taps = new();
    public double Bpm { get; private set; }
    public long LastTapMs => _taps.Count > 0 ? _taps[^1] : 0;
    public double Tap(long nowMs)
    {
        if (_taps.Count > 0 && nowMs - _taps[^1] > 2000) _taps.Clear();
        _taps.Add(nowMs);
        while (_taps.Count > 8) _taps.RemoveAt(0);
        if (_taps.Count >= 2)
        {
            double avg = (double)(_taps[^1] - _taps[0]) / (_taps.Count - 1);
            if (avg > 0) Bpm = Math.Clamp(60000.0 / avg, 40, 240);
        }
        return Bpm;
    }
    /// <summary>Beat phase 0..1 at nowMs, NaN when no tempo was tapped.</summary>
    public double Phase(long nowMs)
    {
        if (Bpm <= 0 || _taps.Count == 0) return double.NaN;
        double p = 60000.0 / Bpm, x = (nowMs - _taps[^1]) % p;
        return (x < 0 ? x + p : x) / p;
    }
    public void Reset() { _taps.Clear(); Bpm = 0; }
}

/// <summary>Beat-grid quantization from a heard tempo and beat phase.</summary>
public static class BeatQuant
{
    /// <summary>A hit up to this late after a grid line still fires immediately (human timing forgiveness).</summary>
    public const double LateToleranceMs = 30;

    public static double BeatMs(double bpm) => bpm > 0 ? 60000.0 / bpm : 0;

    /// <summary>Milliseconds until the next grid line (grid in beats, e.g. 0.25). 0 = fire now
    /// (unknown tempo, or we are just past a line). Grids of 1 beat or longer snap to the next beat.</summary>
    public static double MsToNext(double phase, double bpm, double gridBeats)
    {
        if (bpm <= 0 || double.IsNaN(phase) || gridBeats <= 0) return 0;
        double beat = 60000.0 / bpm, grid = beat * Math.Min(gridBeats, 1);
        double pos = (((phase % 1) + 1) % 1) * beat;
        double since = pos % grid;
        if (since <= Math.Min(LateToleranceMs, grid * 0.25)) return 0;
        double rem = grid - since;
        return rem < 0.5 ? 0 : rem;
    }

    /// <summary>Loop/roll length in frames at 44.1 kHz (falls back to 120 BPM).</summary>
    public static int Frames(double beats, double bpm, int sampleRate = 44100)
        => (int)Math.Round(beats * 60.0 / (bpm > 0 ? bpm : 120) * sampleRate);

    /// <summary>Loop size ladder: 1/32 .. 16 beats, doubled/halved by the D-pad.</summary>
    public static double StepLoop(double beats, bool up) => Math.Clamp(up ? beats * 2 : beats / 2, 1.0 / 32, 16);

    public static string BeatsLabel(double beats) => beats >= 1 ? $"{beats:0}" : $"1/{Math.Round(1 / beats):0}";
}
