using MixDock.Audio;

int passed = 0, failed = 0;
void Check(bool ok, string name, string detail = "")
{
    if (ok) { passed++; Console.WriteLine("PASS " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
    else { failed++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
}

Console.WriteLine("--- latency profiles");
Check(LatencyProfile.Standard.RequestedMs == 120, "default profile keeps the shipping 120 ms request");
Check(LatencyProfile.Low.RequestedMs < LatencyProfile.Standard.RequestedMs && LatencyProfile.Standard.RequestedMs < LatencyProfile.Safe.RequestedMs, "Low < Standard < Safe");
Check(LatencyProfile.Custom(1).RequestedMs == LatencyProfile.MinMs && LatencyProfile.Custom(99999).RequestedMs == LatencyProfile.MaxMs, "custom requests are clamped to a sane range");
Check(LatencyProfile.All.Select(p => p.Name).Distinct().Count() == 3, "profiles are distinctly named");

Console.WriteLine("--- percentiles");
{
    var s = CallbackStats.Percentile(Enumerable.Range(1, 100).Select(i => (double)i).ToArray(), 0.5);
    Check(s == 50, "nearest-rank p50 of 1..100 is 50", $"{s}");
    Check(CallbackStats.Percentile(Enumerable.Range(1, 100).Select(i => (double)i).ToArray(), 0.99) == 99, "p99 of 1..100 is 99");
    Check(CallbackStats.Percentile(new double[0], 0.5) == 0, "empty input gives 0");
    Check(CallbackStats.Percentile(new[] { 3.0 }, 0.99) == 3, "single value");
}

Console.WriteLine("--- callback stats");
{
    var st = new CallbackStats();
    Check(st.Snapshot().Count == 0 && st.Snapshot().ToString().Contains("no callbacks"), "empty snapshot");
    for (int i = 1; i <= 100; i++) st.RecordMs(i / 100.0, 10.0);
    var a = st.Snapshot();
    Check(a.Count == 100 && Math.Abs(a.P50Ms - 0.50) < 1e-9 && Math.Abs(a.P95Ms - 0.95) < 1e-9 && Math.Abs(a.MaxMs - 1.0) < 1e-9, "percentiles from 100 samples", a.ToString());
    Check(a.Overruns == 0 && a.BudgetMs == 10.0, "no overruns under budget");
    st.RecordMs(12.0, 10.0);
    Check(st.Snapshot().Overruns == 1 && st.Snapshot().MaxMs == 12.0, "a block slower than its own duration counts as an overrun");
    st.RecordMs(double.NaN, 10); st.RecordMs(-1, 10);
    Check(st.Snapshot().Count == 101, "NaN and negative durations are ignored");
    for (int i = 0; i < CallbackStats.Capacity * 3; i++) st.RecordMs(0.1, 10);
    var b = st.Snapshot();
    Check(b.Count == CallbackStats.Capacity, "memory is bounded: window never exceeds the ring capacity", $"{b.Count}");
    Check(b.MaxMs == 0.1 && b.Overruns == 1, "old samples age out of the percentiles; lifetime overruns persist");
    st.Reset();
    Check(st.Snapshot().Count == 0 && st.Snapshot().Overruns == 0, "reset clears");
}
{
    var st = new CallbackStats();
    st.Record(441, System.Diagnostics.Stopwatch.Frequency / 1000 * 2);          // 2 ms for a 10 ms block
    var s = st.Snapshot();
    Check(Math.Abs(s.P50Ms - 2.0) < 0.01 && Math.Abs(s.BudgetMs - 10.0) < 0.01 && s.Overruns == 0, "Record converts Stopwatch ticks and block size to ms", s.ToString());
    st.Record(441, System.Diagnostics.Stopwatch.Frequency / 1000 * 15);
    Check(st.Snapshot().Overruns == 1, "15 ms for a 10 ms block overruns");
}
{
    var st = new CallbackStats(); var tasks = new List<Task>();
    var writer = Task.Run(() => { for (int i = 0; i < 200000; i++) st.RecordMs(i % 7 * 0.1, 5); });
    int reads = 0; while (!writer.IsCompleted) { var s = st.Snapshot(); if (s.Count > CallbackStats.Capacity || s.MaxMs > 0.61) { reads = -1; break; } reads++; }
    writer.Wait();
    Check(reads != -1, "concurrent snapshots during writes stay sane", $"{reads} reads");
}

Console.WriteLine("--- cue/main separation");
Check(!OutputSeparation.CueAllowed("dev1", "dev1"), "cue equal to main is refused");
Check(!OutputSeparation.CueAllowed("DEV1", "dev1"), "comparison ignores case");
Check(OutputSeparation.CueAllowed("dev1", "dev2"), "different devices are allowed");
Check(OutputSeparation.CueAllowed(null, "dev2") && OutputSeparation.CueAllowed("dev1", null), "no main yet / cue off is allowed");

Console.WriteLine($"=== {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
