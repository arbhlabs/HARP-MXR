namespace MixDock.Audio;

public sealed partial class TrackAnalysisBuilder
{
    private sealed record SpectralFrame(float[] Chroma, float[] Cepstra, float PitchedPower,
        float Flux, float Centroid, float MidShare, float HarmonicRatio, float MidCentroid);

    private sealed class Spectrum
    {
        private const int Decimation = 4, Size = 1024, Hop = 1103, Bands = 14, CepstraCount = 6;
        private const double BinHz = (double)SampleRate / (Decimation * Size);
        public const double StepSeconds = (double)(Hop * Decimation) / SampleRate;
        private static readonly double[] Window = Enumerable.Range(0, Size)
            .Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (Size - 1))).ToArray();
        private static readonly double[] Cos = Enumerable.Range(0, Size / 2).Select(i => Math.Cos(-2 * Math.PI * i / Size)).ToArray();
        private static readonly double[] Sin = Enumerable.Range(0, Size / 2).Select(i => Math.Sin(-2 * Math.PI * i / Size)).ToArray();
        private static readonly int[] Reversed = Enumerable.Range(0, Size).Select(ReverseBits).ToArray();
        private static readonly double[] Filters = Enumerable.Range(0, Bands + 2)
            .Select(i => 700 * (Math.Pow(1 + 5350.0 / 700, i / (double)(Bands + 1)) - 1)).ToArray();
        private static readonly double[,] CepstralBasis = MakeCepstralBasis();
        private readonly double[] _real = new double[Size], _imaginary = new double[Size], _samples = new double[Size];
        private readonly double[] _previousBands = new double[Bands];
        private long _decimated;
        private int _decimationPhase;
        private double _antiAlias1, _antiAlias2, _average;
        public List<SpectralFrame> Features { get; } = [];

        public void Feed(double x)
        {
            _antiAlias1 += .55 * (x - _antiAlias1);
            _antiAlias2 += .55 * (_antiAlias1 - _antiAlias2);
            _average += _antiAlias2;
            if (++_decimationPhase < Decimation) return;
            _samples[(int)(_decimated & (Size - 1))] = _average / Decimation;
            _decimationPhase = 0;
            _average = 0;
            _decimated++;
            if (_decimated % Hop == 0) AnalyzeSpectrum();
        }

        private void AnalyzeSpectrum()
        {
            for (int i = 0; i < Size; i++)
            {
                int index = Reversed[i];
                _real[index] = _samples[(int)((_decimated - Size + i) & (Size - 1))] * Window[i];
                _imaginary[index] = 0;
            }
            for (int size = 2; size <= Size; size *= 2)
            {
                int half = size / 2, step = Size / size;
                for (int start = 0; start < Size; start += size)
                for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half, twiddle = k * step;
                    double real = _real[b] * Cos[twiddle] - _imaginary[b] * Sin[twiddle];
                    double imaginary = _real[b] * Sin[twiddle] + _imaginary[b] * Cos[twiddle];
                    _real[b] = _real[a] - real;
                    _imaginary[b] = _imaginary[a] - imaginary;
                    _real[a] += real;
                    _imaginary[a] += imaginary;
                }
            }

            Span<double> power = stackalloc double[Size / 2];
            for (int i = 1; i < power.Length; i++)
                power[i] = (_real[i] * _real[i] + _imaginary[i] * _imaginary[i]) / (Size * Size);
            Features.Add(Describe(power));
        }

        private SpectralFrame Describe(ReadOnlySpan<double> power)
        {
            var chroma = new float[12];
            var cepstra = new float[CepstraCount];
            Span<double> bands = stackalloc double[Bands];
            bands.Clear();
            double total = 0, weightedFrequency = 0, pitchedPower = 0, midPower = 0, midWeighted = 0, harmonicPower = 0;
            int filter = 0;
            for (int i = 2; i < power.Length - 1; i++)
            {
                double f = i * BinHz, p = power[i];
                total += p;
                weightedFrequency += f * p;
                while (filter < Bands && f > Filters[filter + 1]) filter++;
                if (filter < Bands)
                    bands[filter] += p * (f - Filters[filter]) / (Filters[filter + 1] - Filters[filter]);
                if (filter > 0 && filter <= Bands && f <= Filters[filter + 1])
                    bands[filter - 1] += p * (Filters[filter + 1] - f) / (Filters[filter + 1] - Filters[filter]);

                bool mid = f is >= 300 and <= 3000;
                if (mid) { midPower += p; midWeighted += f * p; }
                if (p < 1e-11 || p <= power[i - 1] || p <= power[i + 1] || f is < 82 or > 3000) continue;
                double floor = (power[i - 2] + power[i + 2]) * .5;
                if (p < 4 * floor) continue; // weak or broad noise peaks carry little pitch evidence
                if (mid) harmonicPower += p + .65 * (power[i - 1] + power[i + 1]);
                double previous = Math.Log(Math.Max(1e-15, power[i - 1]));
                double peak = Math.Log(p);
                double next = Math.Log(Math.Max(1e-15, power[i + 1]));
                double fraction = Math.Clamp(.5 * (previous - next) / (previous - 2 * peak + next), -.5, .5);
                double note = 69 + 12 * Math.Log2((i + fraction) * BinHz / 440);
                int semitone = (int)Math.Round(note);
                double weight = Math.Sqrt(p) * (1 - .75 * Math.Abs(note - semitone));
                chroma[semitone % 12] += (float)weight;
                pitchedPower += p;
            }
            double pitchSum = chroma.Sum();
            if (pitchSum > 0)
                for (int i = 0; i < chroma.Length; i++) chroma[i] /= (float)pitchSum;

            double flux = 0;
            for (int i = 0; i < Bands; i++)
            {
                double compressed = Math.Sqrt(bands[i] / Math.Max(1e-8, total));
                flux += Math.Max(0, compressed - _previousBands[i]);
                _previousBands[i] = compressed;
                bands[i] = Math.Log(.004 + bands[i] / Math.Max(1e-8, total));
            }
            for (int k = 0; k < cepstra.Length; k++)
            {
                double sum = 0;
                for (int band = 0; band < Bands; band++) sum += bands[band] * CepstralBasis[k, band];
                cepstra[k] = (float)(sum / Bands);
            }
            return new SpectralFrame(chroma, cepstra, (float)pitchedPower, (float)flux,
                (float)(weightedFrequency / Math.Max(1e-10, total)), (float)(midPower / Math.Max(1e-10, total)),
                (float)Math.Clamp(harmonicPower / Math.Max(1e-10, midPower), 0, 1),
                (float)(midWeighted / Math.Max(1e-10, midPower)));
        }

        private static double[,] MakeCepstralBasis()
        {
            var basis = new double[CepstraCount, Bands];
            for (int c = 0; c < CepstraCount; c++)
            for (int b = 0; b < Bands; b++)
                basis[c, b] = Math.Cos(Math.PI * (c + 1) * (b + .5) / Bands);
            return basis;
        }

        private static int ReverseBits(int i)
        {
            int reversed = 0;
            for (int bits = Size; bits > 1; bits /= 2)
            {
                reversed = (reversed << 1) | (i & 1);
                i >>= 1;
            }
            return reversed;
        }
    }
}
