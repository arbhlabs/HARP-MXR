namespace MixDock.Audio;

/// <summary>A time-frequency landmark pair. Ticks are 20 ms on the capture's sample clock.</summary>
public readonly record struct FingerprintPoint(int Tick, uint Hash);

/// <summary>
/// In-process, level-independent fingerprints. A hash contains the two peak frequencies and the
/// time between them. Matches can vote for a track offset without knowing its title or starting time.
/// </summary>
public static class AcousticFingerprint
{
    private const int FftSize = 1024;
    private const int Decimation = 4;
    private const int BandCount = 5;
    private const int FarPair = 15;
    public const int HopFrames = 882;
    public const double TicksPerSecond = 50;
    public const int LookaheadFrames = FftSize * Decimation + FarPair * HopFrames;

    private static readonly int[] BandEdges = [5, 26, 57, 112, 223, 458];
    private static readonly int[] Reversed = Enumerable.Range(0, FftSize).Select(ReverseBits).ToArray();
    private static readonly double[] Window = Enumerable.Range(0, FftSize)
        .Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (FftSize - 1))).ToArray();
    private static readonly (double Re, double Im)[] Twiddles = Enumerable.Range(0, FftSize / 2)
        .Select(i => (Math.Cos(-2 * Math.PI * i / FftSize), Math.Sin(-2 * Math.PI * i / FftSize))).ToArray();

    /// <param name="stereo">Interleaved 44.1 kHz stereo capture. Sample values are consumed in memory.</param>
    /// <param name="firstFrame">Absolute capture clock at the start of this block.</param>
    public static FingerprintPoint[] Extract(ReadOnlySpan<float> stereo, long firstFrame = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        long tick = firstFrame / HopFrames;
        int start = (int)(firstFrame % HopFrames);
        if (start != 0)
        {
            start = HopFrames - start;
            tick++;
        }

        int available = stereo.Length / 2 - start;
        if (available < LookaheadFrames || tick > int.MaxValue) return [];
        int frames = 1 + (available - FftSize * Decimation) / HopFrames;
        if (tick + frames > int.MaxValue) return [];
        var peaks = new ushort[frames * BandCount];
        Span<double> real = stackalloc double[FftSize];
        Span<double> imag = stackalloc double[FftSize];
        Span<double> magnitudes = stackalloc double[FftSize / 2];

        for (int i = 0; i < frames; i++)
        {
            int from = (start + i * HopFrames) * 2;
            Spectrum(stereo.Slice(from, FftSize * Decimation * 2), real, imag, magnitudes);
            PickPeaks(magnitudes, peaks.AsSpan(i * BandCount, BandCount));
        }

        var points = new List<FingerprintPoint>(frames * BandCount);
        for (int i = 0; i < frames - FarPair; i++)
        {
            for (int band = 0; band < BandCount; band++)
            {
                int anchor = peaks[i * BandCount + band];
                if (anchor == 0) continue;
                // Keep one temporal pair and one cross-band pair per anchor. The longer pair
                // discriminates notes shared by songs with different progressions or rhythms.
                Add(anchor, peaks[(i + 6) * BandCount + band], 6, (int)tick + i);
                int target = peaks[(i + FarPair) * BandCount + (band + 1) % BandCount];
                if (target == 0) target = peaks[(i + FarPair) * BandCount + band];
                Add(anchor, target, FarPair, (int)tick + i);
            }
        }
        return points.ToArray();

        void Add(int anchor, int target, int delay, int at)
        {
            if (target != 0) points.Add(new FingerprintPoint(at, (uint)(anchor | (target << 9) | (delay << 18))));
        }
    }

    private static int ReverseBits(int n)
    {
        int value = 0;
        for (int size = FftSize; size > 1; size >>= 1)
        {
            value = (value << 1) | (n & 1);
            n >>= 1;
        }
        return value;
    }

    private static void Spectrum(ReadOnlySpan<float> samples, Span<double> real, Span<double> imag,
        Span<double> magnitudes)
    {
        imag.Clear();
        for (int i = 0, from = 0; i < FftSize; i++, from += Decimation * 2)
        {
            double sum = 0;
            for (int j = 0; j < Decimation * 2; j++)
            {
                float value = samples[from + j];
                if (float.IsFinite(value)) sum += Math.Clamp(value, -4, 4);
            }
            real[Reversed[i]] = sum / (Decimation * 2) * Window[i];
        }

        for (int size = 2; size <= FftSize; size *= 2)
        {
            int half = size / 2, stride = FftSize / size;
            for (int group = 0; group < FftSize; group += size)
            {
                for (int i = 0; i < half; i++)
                {
                    var (wr, wi) = Twiddles[i * stride];
                    int a = group + i, b = a + half;
                    double tr = real[b] * wr - imag[b] * wi;
                    double ti = real[b] * wi + imag[b] * wr;
                    real[b] = real[a] - tr;
                    imag[b] = imag[a] - ti;
                    real[a] += tr;
                    imag[a] += ti;
                }
            }
        }
        for (int i = 0; i < magnitudes.Length; i++) magnitudes[i] = real[i] * real[i] + imag[i] * imag[i];
    }

    private static void PickPeaks(ReadOnlySpan<double> powers, Span<ushort> peaks)
    {
        double strongest = 0;
        for (int i = BandEdges[0]; i < BandEdges[^1]; i++) strongest = Math.Max(strongest, powers[i]);
        if (strongest < .002) return;

        // Relative threshold rejects noisy bins and remains stable after linear gain changes.
        double threshold = Math.Max(.002, strongest * .0025);
        for (int band = 0; band < BandCount; band++)
        {
            int best = 0;
            double power = threshold;
            for (int i = BandEdges[band]; i < BandEdges[band + 1]; i++)
            {
                if (powers[i] <= power || powers[i] < powers[i - 1] || powers[i] < powers[i + 1]) continue;
                power = powers[i];
                best = i;
            }
            peaks[band] = (ushort)best;
        }
    }
}
