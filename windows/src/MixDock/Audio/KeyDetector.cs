namespace MixDock.Audio;

/// <summary>A musical key (pitch class 0 = C, minor flag) with its Camelot wheel code.</summary>
public readonly record struct MusicalKey(int PitchClass, bool Minor)
{
    private static readonly string[] Names = { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
    public int CamelotNumber => ((7 * ((Minor ? PitchClass + 3 : PitchClass) % 12)) + 7) % 12 + 1;
    public string Camelot => $"{CamelotNumber}{(Minor ? 'A' : 'B')}";
    public string Name => Names[PitchClass % 12] + (Minor ? "m" : "");
    public override string ToString() => $"{Camelot} ({Name})";

    /// <summary>Harmonic-mixing rule (Camelot): same key, one step around the wheel, or relative major/minor.</summary>
    public static bool Compatible(MusicalKey a, MusicalKey b)
    {
        int na = a.CamelotNumber, nb = b.CamelotNumber;
        if (a.Minor == b.Minor) { int d = Math.Abs(na - nb); return d == 0 || d == 1 || d == 11; }
        return na == nb;
    }
}

/// <summary>
/// Rolling key estimate: decimate to 11 kHz, Goertzel energy for every semitone E2..E6 on Hann-windowed
/// 4096-sample blocks, fold into a 12-bin chroma with ~20 s memory, correlate with Krumhansl-Schmuckler
/// major/minor profiles. Analysis runs on the thread pool; Feed() only decimates and copies.
/// </summary>
public sealed class KeyDetector
{
    private const int Decim = 4, Fs = 44100 / Decim, Block = 4096;
    private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    private static readonly double[] Window = Enumerable.Range(0, Block).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Block - 1))).ToArray();
    private readonly object _gate = new();
    private readonly float[] _buf = new float[Block];
    private int _bufN, _decN; private float _decAcc;
    private readonly double[] _chroma = new double[12];
    private int _blocks, _busy;
    private MusicalKey? _key; private double _conf;

    public MusicalKey? Key { get { lock (_gate) return _key; } }
    public double Confidence { get { lock (_gate) return _conf; } }
    /// <summary>Set false in tests to analyse synchronously.</summary>
    public bool Background { get; set; } = true;

    public void Reset() { lock (_gate) { Array.Clear(_chroma); _bufN = _decN = 0; _decAcc = 0; _blocks = 0; _key = null; _conf = 0; } }

    public void Feed(ReadOnlySpan<float> stereo)
    {
        float[]? ready = null;
        lock (_gate)
        {
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                float x = 0.5f * (stereo[i] + stereo[i + 1]);
                if (!float.IsFinite(x)) x = 0;
                _decAcc += x;
                if (++_decN < Decim) continue;
                _buf[_bufN++] = _decAcc / Decim; _decAcc = 0; _decN = 0;
                if (_bufN == Block) { ready ??= (float[])_buf.Clone(); _bufN = Block / 2; Array.Copy(_buf, Block / 2, _buf, 0, Block / 2); }
            }
        }
        if (ready == null) return;
        if (!Background) { Analyze(ready); return; }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // still busy: skip this block
        ThreadPool.QueueUserWorkItem(_ => { try { Analyze(ready); } catch { } finally { Volatile.Write(ref _busy, 0); } });
    }

    private void Analyze(float[] block)
    {
        double rms = 0; foreach (var v in block) rms += v * v;
        if (rms / Block < 1e-7) return;                       // silence carries no key information
        var c = new double[12];
        for (int midi = 40; midi <= 88; midi++)
        {
            double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
            double w = 2 * Math.PI * f / Fs, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < Block; i++) { double s = block[i] * Window[i] + coeff * s1 - s2; s2 = s1; s1 = s; }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            c[midi % 12] += Math.Sqrt(Math.Max(0, power));
        }
        double max = c.Max(); if (max <= 0) return;
        lock (_gate)
        {
            for (int k = 0; k < 12; k++) _chroma[k] = _chroma[k] * 0.97 + c[k] / max;
            _blocks++;
            if (_blocks < 4) return;
            double best = double.MinValue, second = double.MinValue; MusicalKey bestKey = default;
            for (int root = 0; root < 12; root++)
                foreach (bool minor in new[] { false, true })
                {
                    double r = Correlate(_chroma, minor ? MinorProfile : MajorProfile, root);
                    if (r > best) { second = best; best = r; bestKey = new MusicalKey(root, minor); }
                    else if (r > second) second = r;
                }
            _key = bestKey; _conf = Math.Clamp(best - second + best * 0.5, 0, 1);
        }
    }

    public static double Correlate(double[] chroma, double[] profile, int root)
    {
        double mc = chroma.Average(), mp = profile.Average(), num = 0, dc = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double a = chroma[(i + root) % 12] - mc, b = profile[i] - mp;
            num += a * b; dc += a * a; dp += b * b;
        }
        return dc <= 0 || dp <= 0 ? 0 : num / Math.Sqrt(dc * dp);
    }
}
