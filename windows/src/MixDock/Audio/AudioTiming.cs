using System.Diagnostics;

namespace MixDock.Audio;

/// <summary>Requested WASAPI shared-mode buffer for the main and cue outputs. A request, not a measurement: the device,
/// driver and Bluetooth stack decide the real delay, so profiles are named by intent and never promise a number.</summary>
public sealed record LatencyProfile(string Name, int RequestedMs)
{
    /// <summary>The shipping value (unchanged default).</summary>
    public static readonly LatencyProfile Standard = new("Standard", 120);
    /// <summary>Smaller request for wired/USB outputs; underruns are likelier, so it is opt-in.</summary>
    public static readonly LatencyProfile Low = new("Low latency", 60);
    /// <summary>Larger request for Bluetooth or busy systems.</summary>
    public static readonly LatencyProfile Safe = new("Safe", 200);

    public const int MinMs = 20, MaxMs = 500;
    public static LatencyProfile Custom(int ms) => new("Custom", Math.Clamp(ms, MinMs, MaxMs));
    public static IReadOnlyList<LatencyProfile> All { get; } = new[] { Low, Standard, Safe };
}

/// <summary>Percentiles of recent render-callback durations.</summary>
public readonly record struct TimingSnapshot(int Count, double P50Ms, double P95Ms, double P99Ms, double MaxMs, long Overruns, double BudgetMs)
{
    public override string ToString() => Count == 0 ? "timing: no callbacks yet"
        : $"callback p50 {P50Ms:F2} / p95 {P95Ms:F2} / p99 {P99Ms:F2} / max {MaxMs:F2} ms of {BudgetMs:F1} ms budget, overruns {Overruns}";
}

/// <summary>
/// Bounded render-callback timing for the diagnostics line: a fixed ring of the last <see cref="Capacity"/> callback
/// durations (written only by the audio thread, no allocation, no locks) plus a lifetime overrun counter. Measures the
/// time the mixer needs to fill a block against the block's own duration; it is NOT end-to-end output latency.
/// </summary>
public sealed class CallbackStats
{
    public const int Capacity = 512;
    private readonly double[] _ms = new double[Capacity];
    private int _next; private int _count; private long _overruns; private double _budgetMs;

    /// <summary>Audio thread: one finished block of <paramref name="frames"/> frames that took <paramref name="elapsedTicks"/> Stopwatch ticks.</summary>
    public void Record(int frames, long elapsedTicks, int sampleRate = 44100)
    {
        double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency, budget = frames * 1000.0 / sampleRate;
        RecordMs(ms, budget);
    }

    public void RecordMs(double ms, double budgetMs)
    {
        if (double.IsNaN(ms) || ms < 0) return;
        int i = _next; _ms[i] = ms; _next = (i + 1) % Capacity;
        if (_count < Capacity) _count++;
        Volatile.Write(ref _budgetMs, budgetMs);
        if (ms > budgetMs) Interlocked.Increment(ref _overruns);
    }

    public void Reset() { _next = 0; _count = 0; Interlocked.Exchange(ref _overruns, 0); }

    /// <summary>Any thread; a torn read only skews a diagnostic by one sample.</summary>
    public TimingSnapshot Snapshot()
    {
        int n = Math.Min(_count, Capacity);
        if (n == 0) return new(0, 0, 0, 0, 0, 0, 0);
        var copy = new double[n]; Array.Copy(_ms, copy, n); Array.Sort(copy);
        return new(n, Percentile(copy, 0.50), Percentile(copy, 0.95), Percentile(copy, 0.99), copy[n - 1], Interlocked.Read(ref _overruns), Volatile.Read(ref _budgetMs));
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int rank = (int)Math.Ceiling(Math.Clamp(p, 0, 1) * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>Engine-boundary rule for the two outputs: the headphone cue is never the main device.</summary>
public static class OutputSeparation
{
    /// <summary>True when <paramref name="cueId"/> may be used while <paramref name="mainId"/> is the main output.</summary>
    public static bool CueAllowed(string? mainId, string? cueId) => cueId == null || !string.Equals(mainId, cueId, StringComparison.OrdinalIgnoreCase);
}
