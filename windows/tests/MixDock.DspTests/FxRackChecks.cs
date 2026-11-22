using System.Diagnostics;
using System.Globalization;
using System.Text;
using MixDock.Audio;

/// <summary>
/// Stick FX rack checks (R3): every effect at 0 / 0.25 / 0.5 / 1 on sine, noise, impulses and a beat; a 0→1→0 sweep in
/// 50 ms; set switching and circling mid-signal; return to dry after release; the distinctness (distance) matrix; CPU
/// per 10 ms block; and a 60 s random-stick soak. With "--fx-evidence &lt;dir&gt;" the numbers are also written as JSON/CSV.
/// </summary>
internal static class FxRackChecks
{
    private const int Sr = 44100, Block = 441;
    private const float ClickMargin = .02f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Run(Action<bool, string> assert, string[] args)
    {
        // every check runs and prints; the first failure is raised at the end, so one run shows all of them
        var failures = new List<string>();
        void check(bool ok, string t) { if (ok) assert(true, t); else { Console.WriteLine("FAIL " + t); failures.Add(t); } }
        try { RunAll(check, args); }
        finally { if (failures.Count > 0) assert(false, $"{failures.Count} FX rack check(s) failed, first: {failures[0]}"); }
    }

    private static void RunAll(Action<bool, string> check, string[] args)
    {
        string? evidence = null;
        int ix = Array.IndexOf(args, "--fx-evidence");
        if (ix >= 0 && ix + 1 < args.Length) { evidence = args[ix + 1]; Directory.CreateDirectory(evidence); }
        var report = new StringBuilder();
        var all = Enum.GetValues<StickFx>();
        check(all.Length >= 20 && FxCatalog.All.Count == all.Length && FxCatalog.All.Select((f, i) => (int)f.Id == i).All(b => b), $"catalog: {all.Length} effects, one entry each, in chain order");
        check(FxCatalog.Sets.Count >= 5 && FxCatalog.Sets.All(s => s.All.Distinct().Count() == 6), $"{FxCatalog.Sets.Count} sets of 4 + 2, every set has six different effects");
        var reachable = FxCatalog.Sets.SelectMany(s => s.All).Distinct().Count();
        check(reachable >= 20 && FxCatalog.Sets.SelectMany(s => new[] { s.Up, s.Down, s.Left, s.Right }).Distinct().Count() >= 20, $"{reachable} effects reachable from the sticks, 20+ on the right stick alone");
        check(FxCatalog.All.Select(f => f.Name).Distinct().Count() == all.Length, "effect names are unique");
        Morph(check);

        // ---------------------------------------------------------------- signals
        int len = Sr * 3;
        var signals = new (string Name, float[] Data)[]
        {
            ("sine220", Sine(220, .5, len)), ("sine2k", Sine(2000, .5, len)), ("noise", Noise(.5, len, 7)),
            ("impulses", Impulses(len, Sr / 2, .8f)), ("beat", Beat(len)),
        };
        var peaks = new StringBuilder("effect,signal,amount,dry_peak,out_peak,over_db,dc,guard_min_gain\n");
        var tails = new Dictionary<StickFx, double>();
        var steady = new Dictionary<StickFx, double>();
        foreach (var fx in all) try
        {
            var info = FxCatalog.Info(fx);
            bool gen = info.Family == FxFamily.Generator;
            float cap = fx == StickFx.NoiseRiser ? NoiseRiserUnitCap : fx == StickFx.SubDrop ? SubDropUnitCap : 0;
            double worstOver = double.NegativeInfinity, worstDc = 0; float worstGuard = 1; bool finite = true, exact = true;
            foreach (var (sname, sig) in signals)
                foreach (float a in new[] { 0f, .25f, .5f, 1f })
                {
                    var rack = new FxRack();
                    var o = Render(rack, sig, b => b < 30 ? Idle() : Ctl(fx, a, a > 0 ? 1 : 0));   // engage after 0.3 s, so time effects have history
                    finite &= o.All(float.IsFinite);
                    if (a == 0) { exact &= o.AsSpan().SequenceEqual(sig); continue; }
                    int from = Sr * 4 / 5;
                    double dp = Peak(sig, from), op = Peak(o, from);
                    double over = gen ? 20 * Math.Log10(Math.Max(1e-9, op) / Math.Max(1e-9, dp * FxRack.Ceiling + cap)) + 1 : 20 * Math.Log10(Math.Max(1e-9, op) / Math.Max(1e-9, dp));
                    double dc = Math.Abs(Mean(o, from) - Mean(sig, from));
                    worstOver = Math.Max(worstOver, over); worstDc = Math.Max(worstDc, dc); worstGuard = Math.Min(worstGuard, rack.GuardMinGain);
                    peaks.AppendLine(string.Join(",", fx, sname, a.ToString(Inv), dp.ToString("0.0000", Inv), op.ToString("0.0000", Inv), over.ToString("0.00", Inv), dc.ToString("0.000000", Inv), rack.GuardMinGain.ToString("0.000", Inv)));
                }
            check(finite, $"{info.Name}: no NaN / Inf on sine, noise, impulses and beat at every amount");
            check(exact, $"{info.Name}: amount 0 is bit-exact dry");
            check(worstOver <= 1.0 + 1e-6, $"{info.Name}: peak within +1 dB of dry at 0.25 / 0.5 / 1{(gen ? " (plus its fixed generator level)" : "")} (worst {worstOver:+0.00;-0.00} dB, guard min gain {worstGuard:0.00})");
            check(worstDc < 1e-3, $"{info.Name}: DC offset below -60 dB (worst {20 * Math.Log10(Math.Max(1e-12, worstDc)):0} dB)");

            // 0 -> 1 -> 0 in 50 ms each way, on a two-partial tone: no step harder than the effect itself makes
            var tone = Tone(Sr * 2);
            var full = Render(new FxRack(), tone, b => b < 30 ? Idle() : Ctl(fx, 1, 1));
            double fullStep = MaxStep(full, Sr, tone.Length / 2), dryStep = MaxStep(tone, 0, tone.Length / 2);
            steady[fx] = Math.Max(fullStep, dryStep);
            int rampBlocks = 5;     // 5 x 10 ms = 50 ms
            var swept = Render(new FxRack(), tone, b =>
            {
                float v = b < 20 ? 0 : b < 20 + rampBlocks ? (b - 20 + 1f) / rampBlocks : b < 60 ? 1 : b < 60 + rampBlocks ? 1 - (b - 60 + 1f) / rampBlocks : 0;
                return Ctl(fx, v, v > 0 ? 1 : 0);
            });
            double sweepStep = MaxStep(swept, 0, swept.Length / 2), limit = steady[fx] * 1.25 + ClickMargin;
            check(swept.All(float.IsFinite) && sweepStep <= limit, $"{info.Name}: 0→1→0 in 50 ms without a click (max step {sweepStep:0.000}, effect itself {fullStep:0.000}, dry {dryStep:0.000})");

            // release: back to bit-exact dry (or below -90 dB) within the tail time
            double tailLimit = info.Family switch { FxFamily.Delay => 25, FxFamily.Reverb => 12, FxFamily.Generator => 4, _ => 1.5 };
            var longTone = Tone((int)(Sr * (tailLimit + 1.5)));
            var rel = Render(new FxRack(), longTone, b => b >= 30 && b < 100 ? Ctl(fx, 1, 1) : Idle());
            int last = -1;
            for (int i = 0; i < rel.Length; i++) if (Math.Abs(rel[i] - longTone[i]) > 3.2e-5f) last = i;
            double tailSec = Math.Max(0, (last / 2 + 1) / (double)Sr - 1.0);
            tails[fx] = tailSec;
            check(tailSec <= tailLimit, $"{info.Name}: returns to dry within {tailSec:0.00} s of release (limit {tailLimit} s)");
        }
        catch (Exception ex) { check(false, $"{fx}: threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        if (evidence != null) File.WriteAllText(Path.Combine(evidence, "fx-peaks.csv"), peaks.ToString());

        SetSwitching(check, steady);
        Distinctness(check, report, evidence);
        Cpu(check, report, evidence, tails);
        Soak(check, report, evidence);
        Console.WriteLine(report.ToString());
    }

    private const float NoiseRiserUnitCap = .2f, SubDropUnitCap = .4f;

    // ---------------------------------------------------------------- stick geometry
    private static void Morph(Action<bool, string> check)
    {
        Span<double> s = stackalloc double[4];
        check(!StickMorph.Directions(.05, .1, s) && s.ToArray().All(v => v == 0), "stick centre: no direction (caller keeps the last one)");
        StickMorph.Directions(0, 1, s); check(s[0] == 1 && s[1] + s[2] + s[3] == 0, "straight up = 100 % up slot");
        StickMorph.Directions(-1, 0, s); check(s[2] == 1 && s[0] + s[1] + s[3] == 0, "straight left = 100 % left slot");
        StickMorph.Directions(.7071, -.7071, s); check(Math.Abs(s[1] - .5) < 1e-3 && Math.Abs(s[3] - .5) < 1e-3, "down-right diagonal = half down, half right");
        bool sum = true, smooth = true; double[] prev = new double[4];
        for (int k = 0; k <= 720; k++)
        {
            double a = k / 720.0 * 2 * Math.PI; StickMorph.Directions(Math.Cos(a), Math.Sin(a), s);
            sum &= Math.Abs(s[0] + s[1] + s[2] + s[3] - 1) < 1e-9;
            if (k > 0) for (int j = 0; j < 4; j++) smooth &= Math.Abs(s[j] - prev[j]) < .02;
            s.CopyTo(prev);
        }
        check(sum && smooth, "circling the stick: shares always sum to 1 and never jump (≤ 2 % per half degree)");
        check(StickMorph.Side(-.4) == (1, 0) && StickMorph.Side(.4) == (0, 1) && StickMorph.Side(0) == (0, 0), "left stick X: left slot / right slot");
    }

    // ---------------------------------------------------------------- set switching and circling
    private static void SetSwitching(Action<bool, string> check, Dictionary<StickFx, double> steady)
    {
        var tone = Tone(Sr * 4);
        double refStep = FxCatalog.Sets.SelectMany(x => x.All).Max(f => steady[f]) * 1.25 + ClickMargin;
        // full up, switching sets every 300 ms
        var o = Render(new FxRack(), tone, b => b < 30 ? Idle() : SetCtl(FxCatalog.Sets[b / 30 % FxCatalog.Sets.Count], 0, 1, 1));
        double step = MaxStep(o, 0, o.Length / 2);
        check(o.All(float.IsFinite) && step <= refStep, $"switching sets every 300 ms with the stick held: no click (max step {step:0.000}, limit {refStep:0.000})");
        // circling at full distance, two turns per second, on every set
        foreach (var set in FxCatalog.Sets)
        {
            double setRef = set.All.Max(f => steady[f]) * 1.25 + ClickMargin;
            var c = Render(new FxRack(), tone, b =>
            {
                if (b < 30) return Idle();
                double a = b * .01 * 4 * Math.PI; Span<double> s = stackalloc double[4];
                StickMorph.Directions(Math.Cos(a), Math.Sin(a), s);
                var ctl = new FxRack.Controls { Bpm = 120 };
                for (int k = 0; k < 4; k++) { int u = (int)set.Slot(k); ctl.Drive[u] = Math.Max(ctl.Drive[u], 1); ctl.Mix[u] = Math.Max(ctl.Mix[u], (float)s[k]); }
                return ctl;
            });
            double cs = MaxStep(c, 0, c.Length / 2);
            check(c.All(float.IsFinite) && cs <= setRef, $"set {set.Name}: circling the stick morphs without a click (max step {cs:0.000}, limit {setRef:0.000})");
        }
    }

    private static FxRack.Controls SetCtl(FxSet set, int slot, float drive, float mix)
    {
        var c = new FxRack.Controls { Bpm = 120 }; int u = (int)set.Slot(slot); c.Drive[u] = drive; c.Mix[u] = mix; return c;
    }

    // ---------------------------------------------------------------- distinctness
    /// <summary>Feature vector per effect (fixed before looking at results): 24 third-octave band levels and 12 envelope
    /// modulation bands (both in dB relative to the dry), stereo correlation and side/mid ratio, similarity to the dry
    /// (x20). Distance = RMS difference over all 40 features; every pair must be at least 1.0 apart.</summary>
    public const double DistanceThreshold = 1.0;
    private static void Distinctness(Action<bool, string> check, StringBuilder report, string? evidence)
    {
        var all = Enum.GetValues<StickFx>();
        var music = Beat(Sr * 4);
        var dryF = Features(music, music);
        var feats = new Dictionary<StickFx, double[]>();
        foreach (var fx in all)
        {
            var o = Render(new FxRack(), music, b => b < 50 ? Idle() : Ctl(fx, .75f, 1));
            var f = Features(o, music);
            for (int i = 0; i < f.Length; i++) f[i] -= dryF[i];
            feats[fx] = f;
        }
        double min = double.MaxValue; (StickFx A, StickFx B) closest = default;
        var csv = new StringBuilder("effect," + string.Join(",", all.Select(a => a.ToString())) + "\n");
        foreach (var a in all)
        {
            csv.Append(a);
            foreach (var b in all)
            {
                double d = Math.Sqrt(feats[a].Zip(feats[b], (x, y) => (x - y) * (x - y)).Average());
                csv.Append(',').Append(d.ToString("0.00", Inv));
                if (a < b && d < min) { min = d; closest = (a, b); }
            }
            csv.Append('\n');
        }
        double fromDry = all.Min(a => Math.Sqrt(feats[a].Select(x => x * x).Average()));
        if (evidence != null) File.WriteAllText(Path.Combine(evidence, "fx-distance-matrix.csv"), csv.ToString());
        report.AppendLine($"DISTANCE matrix: closest pair {FxCatalog.Name(closest.A)} / {FxCatalog.Name(closest.B)} = {min:0.00} (threshold {DistanceThreshold}); closest to dry {fromDry:0.00}");
        check(min >= DistanceThreshold, $"every pair of effects is measurably different (closest: {FxCatalog.Name(closest.A)} vs {FxCatalog.Name(closest.B)} = {min:0.00} ≥ {DistanceThreshold})");
        check(fromDry >= DistanceThreshold, $"every effect is measurably different from dry at 75 % (closest {fromDry:0.00})");
    }

    private static double[] Features(float[] o, float[] dry)
    {
        int from = Sr, n = o.Length / 2 - from;
        var f = new List<double>();
        // 24 third-octave bands 63 Hz .. 12.5 kHz (average power spectrum, 4096-point Hann frames, mid channel)
        int N = 4096; var acc = new double[N / 2]; int frames = 0;
        var re = new double[N]; var im = new double[N];
        for (int s = from; s + N <= from + n; s += N / 2, frames++)
        {
            for (int k = 0; k < N; k++) { double w = .5 - .5 * Math.Cos(2 * Math.PI * k / N); re[k] = (o[(s + k) * 2] + o[(s + k) * 2 + 1]) * .5 * w; im[k] = 0; }
            Fft(re, im);
            for (int k = 0; k < N / 2; k++) acc[k] += re[k] * re[k] + im[k] * im[k];
        }
        for (int b = 0; b < 24; b++)
        {
            double lo = 63 * Math.Pow(2, b / 3.0 - 1 / 6.0), hi = 63 * Math.Pow(2, b / 3.0 + 1 / 6.0); double e = 1e-12;
            for (int k = (int)(lo * N / Sr); k <= (int)(hi * N / Sr) && k < N / 2; k++) e += acc[k] / Math.Max(1, frames);
            f.Add(10 * Math.Log10(e));
        }
        // envelope modulation spectrum: 5 ms RMS frames, 12 log bands 0.5 .. 32 Hz, normalised by the mean envelope
        int hop = Sr / 200, m = n / hop; var env = new double[m];
        for (int j = 0; j < m; j++) { double s2 = 0; for (int k = 0; k < hop; k++) { int i = from + j * hop + k; s2 += o[i * 2] * o[i * 2] + o[i * 2 + 1] * o[i * 2 + 1]; } env[j] = Math.Sqrt(s2 / (2 * hop)); }
        double mean = env.Average() + 1e-9; int M = 1; while (M < m) M <<= 1;
        var er = new double[M]; var ei = new double[M];
        for (int j = 0; j < m; j++) er[j] = (env[j] / mean - 1) * (.5 - .5 * Math.Cos(2 * Math.PI * j / m));
        Fft(er, ei);
        for (int b = 0; b < 12; b++)
        {
            double lo = .5 * Math.Pow(2, b / 2.0), hi = .5 * Math.Pow(2, (b + 1) / 2.0); double e = 1e-9;
            for (int k = (int)(lo * M / 200); k <= (int)(hi * M / 200) && k < M / 2; k++) e += er[k] * er[k] + ei[k] * ei[k];
            f.Add(10 * Math.Log10(e));
        }
        // stereo: L/R correlation and side/mid ratio; similarity to the dry signal (normalised correlation at lag 0)
        double ll = 0, rr = 0, lr = 0, mid = 0, side = 0, od = 0, oo = 0, dd = 0;
        for (int i = from; i < from + n; i++)
        {
            double l = o[i * 2], r = o[i * 2 + 1], x = (dry[i * 2] + dry[i * 2 + 1]) * .5, y = (l + r) * .5;
            ll += l * l; rr += r * r; lr += l * r; mid += (l + r) * (l + r); side += (l - r) * (l - r); od += x * y; oo += y * y; dd += x * x;
        }
        f.Add(10 * lr / Math.Sqrt(ll * rr + 1e-12));
        f.Add(Math.Max(-40, 10 * Math.Log10((side + 1e-9) / (mid + 1e-9))) * .25);
        f.Add(20 * od / Math.Sqrt(oo * dd + 1e-12));
        return f.ToArray();
    }

    // ---------------------------------------------------------------- CPU
    private static void Cpu(Action<bool, string> check, StringBuilder report, string? evidence, Dictionary<StickFx, double> tails)
    {
        var music = Beat(Sr * 6);
        var rows = new StringBuilder("effect,family,tempo_synced,us_per_10ms_block,tail_s\n");
        report.AppendLine("CPU per 10 ms block (441 frames, stereo, one deck, effect at 75 %):");
        double worst = 0;
        foreach (var fx in Enum.GetValues<StickFx>())
        {
            double us = Time(music, b => b < 30 ? Idle() : Ctl(fx, .75f, 1));
            worst = Math.Max(worst, us);
            var info = FxCatalog.Info(fx);
            rows.AppendLine($"{info.Name},{info.Family},{(info.TempoSynced ? "yes" : "no")},{us.ToString("0.0", Inv)},{tails.GetValueOrDefault(fx, double.NaN).ToString("0.00", Inv)}");
            report.AppendLine($"  {info.Name,-12} {us,7:0.0} µs");
        }
        double idle = Time(music, _ => new FxRack.Controls { Bpm = 120 });
        double everything = Time(music, _ => { var c = new FxRack.Controls { Bpm = 120 }; for (int u = 0; u < FxCatalog.Count; u++) { c.Drive[u] = .75f; c.Mix[u] = 1f / 6; } return c; });
        report.AppendLine($"  idle rack {idle:0.0} µs; all {FxCatalog.Count} effects at once {everything:0.0} µs");
        if (evidence != null) File.WriteAllText(Path.Combine(evidence, "fx-cpu.csv"), rows.ToString() + $"IDLE,,,{idle.ToString("0.0", Inv)},\nALL AT ONCE,,,{everything.ToString("0.0", Inv)},\n");
        check(worst < 250, $"every effect costs under 250 µs per 10 ms block (worst {worst:0.0} µs = {worst / 100:0.0} % of the block)");
        check(idle < 25, $"idle rack costs {idle:0.0} µs per block (history write only)");
        check(everything < 2500, $"all {FxCatalog.Count} effects at once: {everything:0.0} µs per 10 ms block");
    }

    private static double Time(float[] input, Func<int, FxRack.Controls> ctl)
    {
        var rack = new FxRack(); var buf = new float[Block * 2]; int blocks = input.Length / 2 / Block;
        for (int b = 0; b < 60; b++) { rack.State = ctl(b); Array.Copy(input, b * Block * 2, buf, 0, buf.Length); rack.Process(buf); }
        long t = 0; int count = 0;
        for (int rep = 0; rep < 2; rep++)
            for (int b = 60; b < blocks; b++)
            {
                rack.State = ctl(b); Array.Copy(input, b * Block * 2, buf, 0, buf.Length);
                long t0 = Stopwatch.GetTimestamp(); rack.Process(buf); t += Stopwatch.GetTimestamp() - t0; count++;
            }
        return t * 1e6 / Stopwatch.Frequency / count;
    }

    // ---------------------------------------------------------------- 60 s soak
    private static void Soak(Action<bool, string> check, StringBuilder report, string? evidence)
    {
        const int seconds = 60; int blocks = seconds * 100;
        var music = Beat(Sr * seconds);
        var rack = new FxRack(); var rnd = new Random(2026);
        double x = 0, y = 0, sx = 0, vx = 0, vy = 0; int set = 0, nextSwitch = 300, buttonUntil = -1; StickFx button = StickFx.Cut;
        var buf = new float[Block * 2]; var times = new List<double>(blocks);
        double prePeak = 0, postPeak = 0; long hot = 0; bool finite = true; int momentaries = 0;
        Span<double> s = stackalloc double[4]; var last = new double[4];
        for (int b = 0; b < blocks; b++)
        {
            // random-walk sticks with returns to centre, set switches every 2-6 s, momentary buttons now and then
            vx = vx * .9 + (rnd.NextDouble() - .5) * .08; vy = vy * .9 + (rnd.NextDouble() - .5) * .08;
            x = Math.Clamp(x + vx, -1, 1); y = Math.Clamp(y + vy, -1, 1);
            if (rnd.NextDouble() < .004) { x = y = vx = vy = 0; }
            sx = Math.Clamp(sx + (rnd.NextDouble() - .5) * .1, -1, 1);
            if (b >= nextSwitch) { set = (set + (rnd.Next(2) == 0 ? 1 : -1) + FxCatalog.Sets.Count) % FxCatalog.Sets.Count; nextSwitch = b + 200 + rnd.Next(400); }
            if (buttonUntil < b && rnd.NextDouble() < .01) { button = new[] { StickFx.Cut, StickFx.Transformer, StickFx.BeatRepeat, StickFx.Freeze, StickFx.SubDrop }[rnd.Next(5)]; buttonUntil = b + 10 + rnd.Next(60); momentaries++; }
            var c = new FxRack.Controls { Bpm = 120 + 6 * Math.Sin(b * .001) };
            var fs = FxCatalog.Sets[set];
            double amount = FxCurves.Amount(x, y);
            if (StickMorph.Directions(x, y, s)) s.CopyTo(last); else last.CopyTo(s);
            for (int k = 0; k < 4; k++) { int u = (int)fs.Slot(k); c.Drive[u] = Math.Max(c.Drive[u], (float)amount); c.Mix[u] = Math.Max(c.Mix[u], amount > 0 ? (float)s[k] : 0); }
            double side = FxCurves.Amount(sx, 0); int su = (int)(sx < 0 ? fs.SideLeft : fs.SideRight);
            if (side > 0) { c.Drive[su] = Math.Max(c.Drive[su], (float)side); c.Mix[su] = 1; }
            if (b <= buttonUntil) { int u = (int)button; c.Drive[u] = 1; c.Mix[u] = 1; c.Param[u] = button == StickFx.BeatRepeat ? .125f : button == StickFx.SubDrop ? momentaries : 0; }
            rack.State = c;
            Array.Copy(music, b * Block * 2, buf, 0, buf.Length);
            long t0 = Stopwatch.GetTimestamp(); rack.Process(buf); times.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            foreach (var v in buf)
            {
                finite &= float.IsFinite(v);
                double pre = Math.Abs(v) * .88;                       // master at its default 88 %
                double post = Math.Abs(.944 * Math.Tanh(pre / .944));  // the mixer's output limiter
                prePeak = Math.Max(prePeak, pre); postPeak = Math.Max(postPeak, post); if (pre > .7) hot++;
            }
        }
        times.Sort();
        double mean = times.Average(), p99 = times[(int)(times.Count * .99)], max = times[^1];
        report.AppendLine($"SOAK 60 s random sticks across all {FxCatalog.Sets.Count} sets + {momentaries} button hits: finite={finite}, peak before limiter {prePeak:0.000}, after {postPeak:0.000}, {hot} of {blocks * Block * 2} samples in the limiter knee; CPU mean {mean:0.0} µs, p99 {p99:0.0} µs, max {max:0.0} µs per 10 ms block");
        if (evidence != null) File.WriteAllText(Path.Combine(evidence, "fx-soak.json"),
            $"{{\"seconds\":{seconds},\"sets\":{FxCatalog.Sets.Count},\"buttonHits\":{momentaries},\"finite\":{finite.ToString().ToLowerInvariant()},\"peakBeforeLimiter\":{prePeak.ToString("0.0000", Inv)},\"peakAfterLimiter\":{postPeak.ToString("0.0000", Inv)},\"samplesInKnee\":{hot},\"cpuMeanUs\":{mean.ToString("0.0", Inv)},\"cpuP99Us\":{p99.ToString("0.0", Inv)},\"cpuMaxUs\":{max.ToString("0.0", Inv)}}}");
        check(finite, "60 s soak: every sample finite");
        check(postPeak < 1.0, $"60 s soak: no clipping after the limiter (peak {postPeak:0.000})");
        check(p99 < 1000, $"60 s soak: p99 CPU {p99:0.0} µs per 10 ms block");
    }

    // ---------------------------------------------------------------- helpers
    private static FxRack.Controls Idle() => new() { Bpm = 120 };
    private static FxRack.Controls Ctl(StickFx fx, float drive, float mix)
    {
        var c = new FxRack.Controls { Bpm = 120 }; c.Drive[(int)fx] = drive; c.Mix[(int)fx] = mix; return c;
    }
    private static float[] Render(FxRack rack, float[] input, Func<int, FxRack.Controls> ctl)
    {
        var o = (float[])input.Clone();
        for (int b = 0, off = 0; off < o.Length; b++, off += Block * 2)
        {
            rack.State = ctl(b);
            rack.Process(o.AsSpan(off, Math.Min(Block * 2, o.Length - off)));
        }
        return o;
    }
    private static float[] Sine(double hz, double amp, int frames)
    { var v = new float[frames * 2]; for (int i = 0; i < frames; i++) v[i * 2] = v[i * 2 + 1] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Sr)); return v; }
    private static float[] Noise(double amp, int frames, int seed)
    { var r = new Random(seed); var v = new float[frames * 2]; for (int i = 0; i < v.Length; i++) v[i] = (float)(amp * (r.NextDouble() * 2 - 1)); float m = v.Average(); for (int i = 0; i < v.Length; i++) v[i] -= m; return v; }
    private static float[] Impulses(int frames, int every, float amp)
    { var v = new float[frames * 2]; for (int i = every / 4; i + 1 < frames; i += every) { v[i * 2] = v[i * 2 + 1] = amp; v[i * 2 + 2] = v[i * 2 + 3] = -amp; } return v; }   // zero-mean doublets
    private static float[] Tone(int frames)
    { var v = new float[frames * 2]; for (int i = 0; i < frames; i++) { double t = i / (double)Sr; v[2 * i] = v[2 * i + 1] = (float)(.3 * Math.Sin(2 * Math.PI * 220 * t) + .15 * Math.Sin(2 * Math.PI * 220 * 2.76 * t)); } return v; }
    /// <summary>120 BPM groove: kick on every beat, offbeat hats, a sustained minor chord, slight stereo spread.</summary>
    private static float[] Beat(int frames)
    {
        var v = new float[frames * 2]; var r = new Random(3); int beat = Sr / 2;
        double[] chord = { 220, 261.63, 329.63 };
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)Sr; int pb = i % beat; double tb = pb / (double)Sr;
            double kick = Math.Sin(2 * Math.PI * (45 * tb + 60 * (1 - Math.Exp(-tb / .04)) * .04)) * Math.Exp(-tb / .18) * .55;
            int ph = (i + beat / 2) % beat; double hat = (r.NextDouble() * 2 - 1) * Math.Exp(-ph / (double)Sr / .025) * .18;
            double pad = 0; foreach (var f in chord) pad += Math.Sin(2 * Math.PI * f * t) * .07;
            v[i * 2] = (float)(kick + hat * .8 + pad); v[i * 2 + 1] = (float)(kick + hat + pad * .9);
        }
        return v;
    }
    private static double Peak(float[] x, int fromFrame) { double p = 0; for (int i = fromFrame * 2; i < x.Length; i++) p = Math.Max(p, Math.Abs(x[i])); return p; }
    private static double Mean(float[] x, int fromFrame) { double s = 0; int n = 0; for (int i = fromFrame * 2; i < x.Length; i++, n++) s += x[i]; return s / Math.Max(1, n); }
    private static double MaxStep(float[] x, int fromFrame, int toFrame)
    {
        double m = 0;
        for (int i = Math.Max(1, fromFrame); i < toFrame; i++) for (int ch = 0; ch < 2; ch++) m = Math.Max(m, Math.Abs(x[i * 2 + ch] - x[(i - 1) * 2 + ch]));
        return m;
    }
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    double ur = re[i + k], ui = im[i + k], vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci, vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr; im[i + k] = ui + vi; re[i + k + len / 2] = ur - vr; im[i + k + len / 2] = ui - vi;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }
}
