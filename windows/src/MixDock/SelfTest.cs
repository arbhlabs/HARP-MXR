using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MixDock.Audio;

namespace MixDock;

/// <summary>Headless proofs: `--tone` (child tone), `--selftest`, `--probe exe`. Output: %LOCALAPPDATA%\MixDock\selftest.txt.</summary>
public static class SelfTest
{
    private static readonly StringBuilder Out = new();
    private static void P(string s) { Out.AppendLine(s); Log.Write("SELFTEST " + s); }

    static void DumpSessions(string title)
    {
        P($"-- sessions: {title}");
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            P($"endpoint {dev.FriendlyName} id={dev.ID}");
            var ss = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < ss.Count; i++)
            {
                using var s = ss[i];
                int pid = (int)s.GetProcessID;
                string exe = ""; try { exe = Process.GetProcessById(pid).ProcessName; } catch { }
                P($"   pid={pid} {exe} state={s.State} mute={s.SimpleAudioVolume.Mute} vol={s.SimpleAudioVolume.Volume:F2} peak={s.AudioMeterInformation.MasterPeakValue:F3} sys={s.IsSystemSoundsSession}");
            }
        }
    }

    // Peak of everything rendered on the default endpoint (what the speakers get) over ms.
    static async Task<float> EndpointPeak(int ms)
    {
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var cap = new WasapiLoopbackCapture(dev);
        float peak = 0; var wf = cap.WaveFormat;
        cap.DataAvailable += (_, e) =>
        {
            if (wf.Encoding == WaveFormatEncoding.IeeeFloat || wf.BitsPerSample == 32)
                for (int i = 0; i + 4 <= e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            else
                for (int i = 0; i + 2 <= e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
        };
        cap.StartRecording();
        await Task.Delay(ms);
        cap.StopRecording();
        return peak;
    }

    // Peak of one app's own session on one endpoint (excludes other apps such as a music player).
    static async Task<float> SessionPeak(string endpointId, int pid, int ms)
    {
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDevice(endpointId);
        float peak = 0; var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            var ss = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < ss.Count; i++)
            {
                using var s = ss[i];
                if ((int)s.GetProcessID == pid) peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue);
            }
            await Task.Delay(20);
        }
        return peak;
    }

    static async Task<(float rawPeak, double kbps, float dspPeak)> Deck(DeckSource d, int ms)
    {
        d.TakeRawPeak(); long b0 = d.TotalBytes;
        await Task.Delay(ms);
        long b1 = d.TotalBytes;
        return (d.TakeRawPeak(), (b1 - b0) / 1024.0 / (ms / 1000.0), d.Dsp.Peak);
    }

    static int ParentPid(int pid)
    {
        using var q = new System.Management.ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
        foreach (System.Management.ManagementObject o in q.Get()) return Convert.ToInt32(o["ParentProcessId"]);
        return 0;
    }

    /// <summary>Silent offline DSP check: gain of a sine at several freqs under each setting.</summary>
    static double Gain(DeckParameters p, double hz)
    {
        var dsp = new DeckDsp { Parameters = p };
        int n = 44100 * 2; var inb = new float[n]; var outb = new float[n];
        for (int i = 0; i < n / 2; i++) { float v = (float)(0.25 * Math.Sin(2 * Math.PI * hz * i / 44100)); inb[2 * i] = v; inb[2 * i + 1] = v; }
        dsp.Process(inb, outb);
        double rin = 0, rout = 0;
        for (int i = n / 2; i < n; i++) { rin += inb[i] * inb[i]; rout += outb[i] * outb[i]; }
        return 20 * Math.Log10(Math.Sqrt(rout / rin));
    }

    // ---- silent offline tests (no audio device touched) ----
    static float[] Track(double bpm, double seconds)
    {
        int n = (int)(seconds * 44100); var x = new float[n * 2]; var rnd = new Random(7);
        double period = 60.0 / bpm * 44100;
        for (double t = 0; t < n; t += period)                 // kick on the beat, hat on the off-beat
        {
            int i0 = (int)t;
            for (int i = 0; i < 2600 && i0 + i < n; i++) { float v = (float)(0.7 * Math.Exp(-i / 700.0) * Math.Sin(2 * Math.PI * 70 * i / 44100)); x[2 * (i0 + i)] += v; x[2 * (i0 + i) + 1] += v; }
            int h0 = (int)(t + period / 2);
            for (int i = 0; i < 900 && h0 + i < n; i++) { float v = (float)(0.15 * Math.Exp(-i / 200.0) * (rnd.NextDouble() * 2 - 1)); x[2 * (h0 + i)] += v; x[2 * (h0 + i) + 1] += v; }
        }
        return x;
    }


    private sealed class SimSyncDeck : ISyncDeck
    {
        public readonly SampleRing Ring = new(44100 * 4); public readonly SampleHistory History = new();
        public readonly BpmDetector Detector = new() { Synchronous = true }; public readonly DeckReader Reader;
        public BpmDetector Bpm => Detector; public bool IsFile => false; public bool Active => true;
        public SimSyncDeck() { Reader = new DeckReader(Ring, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { History = History }; }
        public void Feed(ReadOnlySpan<float> b) { Detector.Feed(b); lock (History.Gate) { History.Write(b); Ring.Write(b); } }
    }

    // Two simulated live decks (124 BPM master, 124 BPM slave offset by 137 ms) in virtual time; continuous SYNC; measure beat offset.
    static void SyncSimulation()
    {
        int total = 90 * 100;                                           // 90 s in 10 ms steps
        var tA = Track(124, 92); var tB = Track(124, 93);
        int offB = (int)(0.137 * 44100) * 2;                            // slave starts 137 ms later in its song
        var A = new SimSyncDeck(); var B = new SimSyncDeck(); var ctl = new SyncController(A, A.Reader, B, B.Reader);
        var onA = new List<double>(); var onB = new List<double>();
        bool aHigh = false, bHigh = false; var oa = new float[882]; var ob = new float[882];
        int syncAt = 18 * 100;
        for (int step = 0; step < total; step++)
        {
            A.Feed(tA.AsSpan(step * 882, 882)); B.Feed(tB.AsSpan(offB + step * 882, 882));
            if (step == syncAt) ctl.Enable(1);
            ctl.Process(441);
            A.Reader.Read(oa); B.Reader.Read(ob);
            double ea = 0, eb = 0; for (int i = 0; i < 882; i += 2) { ea += oa[i] * oa[i]; eb += ob[i] * ob[i]; }
            ea = Math.Sqrt(ea / 441); eb = Math.Sqrt(eb / 441);
            if (!aHigh && ea > 0.2) { onA.Add(step * 10); aHigh = true; } else if (aHigh && ea < 0.08) aHigh = false;
            if (!bHigh && eb > 0.2) { onB.Add(step * 10); bHigh = true; } else if (bHigh && eb < 0.08) bHigh = false;
        }
        P("[sync] " + ctl.Status);
        var lastB = onB.Where(t => t > 60000).ToList(); double sum = 0;
        foreach (var t in lastB) sum += onA.Min(a => Math.Abs(a - t));
        double mean = lastB.Count > 0 ? sum / lastB.Count : double.NaN;
        var before = onB.Where(t => t > 10000 && t < 17000).ToList(); double sb = 0;
        foreach (var t in before) sb += onA.Min(a => Math.Abs(a - t));
        P($"[sync] mean beat offset slave vs master: before sync {(before.Count > 0 ? sb / before.Count : double.NaN):F0} ms, after sync {mean:F0} ms over {lastB.Count} beats  {(mean < 35 && B.Reader.Underruns == 0 ? "OK" : "FAIL")}");
    }

    private sealed class FakeHost : IMixHost
    {
        public float[] Lvl = { 0.3f, 0.3f }; public double[] Bp = { 120, 120 }; public double Ph = double.NaN;
        public double X { get; set; } = 0; public double Crossfader { get => X; set => X = value; }
        public double[] G = { 1, 1 }, Low = { 0, 0 }; public int Syncs; public double Glide;
        public bool HasSource(int d) => true; public float Level(int d) => Lvl[d]; public double Bpm(int d) => Bp[d]; public double Phase(int d) => Ph;
        public void SetFadeGain(int d, double g) => G[d] = g; public void SetAutoLowDb(int d, double db) => Low[d] = db;
        public void SyncTempo(int s) => Syncs++; public void GlideTempo(int d, double t) => Glide = t;
    }

    static void AutoMixTests()
    {
        // 1) timed trigger, tempo known: prep -> sync -> bar-snapped equal-power fade A->B
        var h = new FakeHost(); var am = new AutoMix(h) { Enabled = true, FadeSeconds = 7, EveryMinutes = 0.25 };
        double t = 0, fadeStart = -1, fadeEnd = -1, maxPowerErr = 0, lowOutMid = 0, lowInMid = 0; bool wasFading = false;
        for (; t < 60000; t += 33)
        {
            h.Ph = (t / 500.0) % 1.0;      // 120 BPM beat phase
            am.Tick(33);
            if (am.Fading && !wasFading) fadeStart = t;
            if (!am.Fading && wasFading) fadeEnd = t;
            wasFading = am.Fading;
            if (am.Fading) { maxPowerErr = Math.Max(maxPowerErr, Math.Abs(h.G[0] * h.G[0] + h.G[1] * h.G[1] - 1)); if (Math.Abs(am.FadeProgress - 0.5) < 0.01) { lowOutMid = h.Low[0]; lowInMid = h.Low[1]; } }
            if (fadeEnd > 0) break;
        }
        P($"[automix] timed: fade {fadeStart / 1000:F1}s..{fadeEnd / 1000:F1}s (len {(fadeEnd - fadeStart) / 1000:F1}s, 7s asked, snapped to whole bars), syncs={h.Syncs}, power error {maxPowerErr:F3}, bass at mid-fade out={lowOutMid:F0}dB in={lowInMid:F0}dB, final crossfader={h.X:F1}, gains={h.G[0]:F1}/{h.G[1]:F1}, low reset={h.Low[0]:F0}/{h.Low[1]:F0}  {(fadeStart > 0 && fadeEnd > fadeStart && Math.Abs((fadeEnd - fadeStart) / 1000 - 8) < 0.3 && h.Syncs == 1 && maxPowerErr < 0.01 && h.X == 1 && h.G[0] == 1 && h.G[1] == 1 && lowOutMid < -15 && lowInMid < -15 ? "OK" : "FAIL")}");
        // 2) outgoing source goes quiet, tempo unknown: plain timed fade of the asked length
        var h2 = new FakeHost { Bp = { [0] = 0, [1] = 0 } }; var am2 = new AutoMix(h2) { Enabled = true, FadeSeconds = 6, EveryMinutes = 5 };
        double q0 = -1, f0 = -1, f1 = -1; wasFading = false;
        for (t = 0; t < 40000; t += 33)
        {
            if (q0 < 0 && t >= 3000) { h2.Lvl[0] = 0; q0 = t; }
            am2.Tick(33);
            if (am2.Fading && !wasFading) f0 = t; if (!am2.Fading && wasFading) { f1 = t; break; }
            wasFading = am2.Fading;
        }
        P($"[automix] quiet: source went silent at {q0 / 1000:F1}s -> fade {f0 / 1000:F1}s..{f1 / 1000:F1}s (len {(f1 - f0) / 1000:F1}s), final crossfader={h2.X:F1}  {(f0 > 0 && f0 - q0 < 3000 && Math.Abs((f1 - f0) / 1000 - 6) < 0.3 && h2.X == 1 ? "OK" : "FAIL")}");
        // 3) switch off mid-fade restores everything
        var h3 = new FakeHost(); var am3 = new AutoMix(h3) { Enabled = true, FadeSeconds = 8, EveryMinutes = 0.1 };
        for (t = 0; t < 30000 && !am3.Fading; t += 33) { h3.Ph = 0; am3.Tick(33); }
        for (int i = 0; i < 60; i++) am3.Tick(33);
        am3.Enabled = false; am3.Tick(33);
        P($"[automix] switched off mid-fade: gains={h3.G[0]:F1}/{h3.G[1]:F1}, low={h3.Low[0]:F0}/{h3.Low[1]:F0}, crossfader back to {h3.X:F1}  {(h3.G[0] == 1 && h3.G[1] == 1 && h3.Low[0] == 0 && h3.Low[1] == 0 && h3.X == 0 ? "OK" : "FAIL")}");
    }

    static async Task FileDeckTest()
    {
        string wav = Path.Combine(Path.GetTempPath(), "mixdock-quiet.wav");
        using (var w = new NAudio.Wave.WaveFileWriter(wav, new NAudio.Wave.WaveFormat(22050, 16, 1)))
        {
            var smp = new short[22050 * 4];
            for (int i = 0; i < smp.Length; i++) smp[i] = (short)(0.02 * 32767 * Math.Sin(2 * Math.PI * 440 * i / 22050));
            w.WriteSamples(smp, 0, smp.Length);
        }
        var ring = new SampleRing(44100 * 4); var deck = new DeckSource(ring, new DeckDsp());
        await deck.LoadFileAsync(wav);
        P($"[file] loaded {deck.FileName}, duration {deck.Duration.TotalSeconds:F1}s, IsFile={deck.IsFile}, ActivePid={deck.ActivePid}");
        await Task.Delay(500);
        P($"[file] pump stalls at ~180 ms buffered when nobody reads: {ring.Count / 2 * 1000 / 44100} ms");
        long frames = 0; float peak = 0; var buf = new float[882 * 2]; var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000 && !deck.Ended)
        {
            int got = ring.Read(buf); frames += got / 2;
            for (int i = 0; i < got; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
            await Task.Delay(10);
        }
        await Task.Delay(300); while (true) { int got = ring.Read(buf); if (got == 0) break; frames += got / 2; }
        P($"[file] 4.0 s mono 22.05 kHz file -> {frames / 44100.0:F2} s of 44.1 kHz stereo, peak {peak:F3}, ended={deck.Ended}  {(Math.Abs(frames / 44100.0 - 4.0) < 0.1 && peak > 0.015 && peak < 0.025 && deck.Ended ? "OK" : "FAIL")}");
        deck.SeekTo(TimeSpan.FromSeconds(2)); deck.Paused = false; await Task.Delay(200);
        var pos = deck.Position.TotalSeconds; deck.Paused = true; await Task.Delay(100); ring.Clear(); await Task.Delay(300);
        P($"[file] seek to 2 s -> position {pos:F2} s; paused: ring stays empty = {ring.Count == 0}  {(pos > 1.9 && pos < 2.6 && ring.Count == 0 ? "OK" : "FAIL")}");
        await deck.StopAsync();
        P($"[file] stopped: IsFile={deck.IsFile}, ActivePid={(deck.ActivePid?.ToString() ?? "null")}  {(!deck.IsFile && deck.ActivePid == null ? "OK" : "FAIL")}");
        try { File.Delete(wav); } catch { }
    }

    static void OfflineTests()
    {
        foreach (double bpm in new[] { 90.0, 124.0, 140.0 })
        {
            var d = new BpmDetector(); var tr = Track(bpm, 20);
            for (int i = 0; i < tr.Length; i += 882) d.Feed(tr.AsSpan(i, Math.Min(882, tr.Length - i)));
            double period = 60.0 / bpm; double expPhase = (20.0 % period) / period;
            double diff = Math.Abs(d.Phase - expPhase); diff = Math.Min(diff, 1 - diff);
            P($"[bpm] synthetic {bpm:F0} BPM -> detected {d.Bpm:F1} (conf {d.Confidence:F2}); beat phase {d.Phase:F2} expected {expPhase:F2} (err {diff:F2})  {(Math.Abs(d.Bpm - bpm) < 1.5 && diff < 0.1 ? "OK" : "FAIL")}");
        }
        SyncSimulation();
        AutoMixTests();
        // resampler: 440 Hz at rate 1.05 -> 462 Hz
        var ring = new SampleRing(44100 * 4); var dsp = new DeckDsp { Parameters = new DeckParameters(Trim: 1) };
        var rd = new DeckReader(ring, dsp) { Rate = 1.05 };
        var o = new float[2 * 8820]; int ph = 0; var blk = new float[2 * 441]; var rb = new float[2 * 441];
        rd.TargetMs = 600;
        for (int pre = 0; pre < 65; pre++) { for (int i = 0; i < 441; i++, ph++) { float v = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * ph / 44100)); blk[2 * i] = v; blk[2 * i + 1] = v; } ring.Write(blk); }
        for (int step = 0; step < 20; step++)
        {
            for (int i = 0; i < 441; i++, ph++) { float v = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * ph / 44100)); blk[2 * i] = v; blk[2 * i + 1] = v; }
            ring.Write(blk); rd.Read(rb);
            Array.Copy(rb, 0, o, 2 * 441 * step, 2 * 441);
        }
        int cross = 0; for (int i = 1 + 441 * 4; i < 8820; i++) if (o[2 * (i - 1)] < 0 && o[2 * i] >= 0) cross++;
        double f = cross / ((8820 - 441 * 4) / 44100.0);
        P($"[rate] 440 Hz read at 1.05x -> {f:F0} Hz (expect ~462)  {(Math.Abs(f - 462) < 12 ? "OK" : "FAIL")}");
        // delay target: output stays silent until 500 ms is buffered
        var ring2 = new SampleRing(44100 * 4); var rd2 = new DeckReader(ring2, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { TargetMs = 500 };
        int fedMs = 0, firstMs = -1; var chunk = new float[2 * 441]; Array.Fill(chunk, 0.2f); var outb = new float[2 * 441];
        for (int ms = 0; ms < 1000; ms += 10)
        {
            ring2.Write(chunk); fedMs += 10; rd2.Read(outb);
            if (firstMs < 0 && outb.Any(v => Math.Abs(v) > 0.01f)) firstMs = ms;
        }
        P($"[delay] 500 ms target: first output after {firstMs} ms of input  {(firstMs >= 480 && firstMs <= 560 ? "OK" : "FAIL")}");
        // cue mixer: deck A only to cue, blend 0 -> cue level; blend 1 -> master tap
        var rA = new DeckReader(new SampleRing(44100), new DeckDsp { Parameters = new DeckParameters(Trim: 1) });
        var rB = new DeckReader(new SampleRing(44100), new DeckDsp { Parameters = new DeckParameters(Trim: 1) });
        var mm = new MasterMixer(rA, rB) { CueA = true, TapMaster = true };
        var cm = new CueMixer(mm) { Volume = 1, Blend = 0 };
        mm.CueRingA.Write(chunk); mm.MasterTap.Write(chunk.Select(v => v * 0.5f).ToArray());
        var co = new float[2 * 441]; cm.Read(co);
        float c0 = co.Max(Math.Abs);
        cm.Blend = 1; mm.CueRingA.Write(chunk); mm.MasterTap.Write(chunk.Select(v => v * 0.5f).ToArray()); cm.Read(co);
        float c1 = co.Max(Math.Abs);
        P($"[cue] blend 0 (cue only) peak {c0:F2}; blend 1 (master only) peak {c1:F2}  {(c0 > 0.15f && c1 > 0.05f && c1 < c0 ? "OK" : "FAIL")}");
    }

    public static async Task<int> Run(string[] args)
    {
        int rc = 0;
        string exeName = args.Length > 1 && args[0] == "--probe" ? args[1] : "vivaldi";
        var path = Path.Combine(Log.Dir, "selftest.txt");
        var redir = new AppRedirect();
        if (args.Contains("--offline")) { OfflineTests(); await FileDeckTest(); File.WriteAllText(path, Out.ToString()); return Out.ToString().Contains("FAIL") ? 1 : 0; }
        try
        {
            P($"MixDock selftest {DateTime.Now:s} OS={Environment.OSVersion.Version} target exe={exeName}");
            var flat = new DeckParameters(Trim: 1);
            foreach (var (name, p) in new (string, DeckParameters)[] {
                ("flat", flat), ("low -12dB", flat with { LowDb = -12 }), ("mid -12dB", flat with { MidDb = -12 }),
                ("high -12dB", flat with { HighDb = -12 }), ("lowKill", flat with { LowKill = true }),
                ("filter LP(-0.8)", flat with { Filter = -0.8 }), ("filter HP(+0.8)", flat with { Filter = 0.8 }) })
                P($"[dsp] {name,-16} gain@60Hz={Gain(p, 60):F1}dB @1kHz={Gain(p, 1000):F1}dB @10kHz={Gain(p, 10000):F1}dB");

            OfflineTests();
            await FileDeckTest();
            var procs = Process.GetProcessesByName(exeName);
            var set = procs.Select(p => p.Id).ToHashSet();
            var parentOf = new Dictionary<int, int>();
            foreach (var p in procs) { try { parentOf[p.Id] = ParentPid(p.Id); } catch { } }
            var roots = procs.Where(p => !set.Contains(parentOf.GetValueOrDefault(p.Id))).Select(p => p.Id).ToList();
            P($"{exeName}: {procs.Length} processes, roots=[{string.Join(",", roots)}]");
            if (roots.Count == 0) { P("target not running"); return 2; }
            int pid = roots[0];
            var tree = SessionTakeover.ProcessTree(pid);
            DumpSessions("before");
            var found = Platform.AudioDiscovery.FindPlayback(tree);
            P($"playback endpoint(s) for tree: {string.Join("; ", found.Select(f => f.endpointName + " pid=" + f.pid + " peak=" + f.peak.ToString("F3")))}");
            var real = found.OrderByDescending(f => f.peak).FirstOrDefault();
            if (real == default) { P("no session found for target"); return 2; }
            string devId = real.endpointId;
            P($"real device = {real.endpointName} ({devId})");

            await using var eng = new AudioEngine();
            eng.Mixer.Crossfader = 0; eng.Mixer.Master = 1f;
            eng.A.Parameters = new DeckParameters(Trim: 1.0);
            await eng.DeckA.StartAsync(pid);
            await eng.SetOutputAsync(devId);
            await Task.Delay(800);
            var r0 = await Deck(eng.DeckA, 2000);
            P($"[1] capture before takeover: raw peak={r0.rawPeak:F3} rate={r0.kbps:F0} KB/s dsp peak={r0.dspPeak:F3}");

            string? sink = AppRedirect.ChooseSink(devId);
            P($"sink endpoint = {sink}");
            bool ok = false;
            if (sink != null)
            {
                try
                {
                    redir.Take(pid, sink, devId);
                    await Task.Delay(2000);
                    DumpSessions("after redirect");
                    var r1 = await Deck(eng.DeckA, 2000);
                    P($"[2] capture AFTER redirect: raw peak={r1.rawPeak:F3} rate={r1.kbps:F0} KB/s");
                    float appReal = 0, appSink = 0;
                    foreach (var t in tree) { appReal = Math.Max(appReal, await SessionPeak(devId, t, 300)); appSink = Math.Max(appSink, await SessionPeak(sink, t, 300)); }
                    float mix = await SessionPeak(devId, Environment.ProcessId, 800);
                    P($"[2] app peak on real device={appReal:F3}, on sink={appSink:F3}; MixDock peak on real device={mix:F3}");
                    ok = r1.rawPeak > 0.01f && appReal < 0.01f && mix > 0.01f;
                    P($"RESULT takeover: capture survives={(r1.rawPeak > 0.01f ? "YES" : "NO")}, raw silenced on real device={(appReal < 0.01f ? "YES" : "NO")}, MixDock plays on real device={(mix > 0.01f ? "YES" : "NO")}");
                    async Task<float> Avg(DeckParameters dp)
                    {
                        eng.A.Parameters = dp; await Task.Delay(700);
                        float sum = 0; int n = 0; var end = Environment.TickCount64 + 1500;
                        while (Environment.TickCount64 < end) { sum += await SessionPeak(devId, Environment.ProcessId, 40); n++; }
                        return sum / n;
                    }
                    float fl = await Avg(new DeckParameters(Trim: 1.0));
                    float lowk = await Avg(new DeckParameters(Trim: 1.0, LowKill: true, MidKill: true));
                    float hp = await Avg(new DeckParameters(Trim: 1.0, Filter: 0.85));
                    float mute = await Avg(new DeckParameters(Trim: 1.0, Mute: true));
                    float half = await Avg(new DeckParameters(Trim: 0.25));
                    P($"[3] MixDock output level on real device: flat={fl:F3} low+mid kill={lowk:F3} highpass={hp:F3} trim25%={half:F3} mute={mute:F3}");
                    bool eq = lowk < fl * 0.7f && hp < fl * 0.95f && half < fl * 0.5f && mute < fl * 0.1f;
                    P($"RESULT live EQ/trim/mute change the output level: {(eq ? "YES" : "NO")}");
                    ok &= eq;
                }
                finally { redir.Restore(); }
                await Task.Delay(1500);
                DumpSessions("after restore");
            }
            rc = ok ? 0 : 1;
            await eng.DeckA.StopAsync();
            P("RESULT pipeline " + (rc == 0 ? "PASS" : "FAIL"));
        }
        catch (Exception ex) { P("EXCEPTION " + ex); rc = 3; }
        finally
        {
            try { redir.Restore(); } catch { }
            File.WriteAllText(path, Out.ToString());
        }
        return rc;
    }
}
