namespace MixDock.Audio;

/// <summary>Offline meters that read the actual mastered PCM from the production mix bus.</summary>
internal sealed class SmartMixMeasurements
{
    private const int Rate = SmartMixFixtures.SampleRate;
    private readonly List<Observation> _blocks = [];
    private readonly BassTag _out, _in;
    private TrackAnalysisBuilder.KWeight _left, _right;
    private PeakMeter _peakL, _peakR;
    private double _energy;
    private long _frames;

    internal SmartMixMeasurements(double outHz, double inHz) => (_out, _in) = (new(outHz), new(inHz));

    internal readonly record struct Observation(double TimeSec, double OutSub, double InSub, long End, double Power);
    internal sealed record Results(double BeforeLufs, double AfterLufs, double MinimumDeltaDb, double MaximumDeltaDb,
        double LongestBassOverlapMs, double OutBassReference, double InBassReference,
        double SamplePeak, double InterSamplePeakEstimate, bool Finite);

    internal void Measure(ReadOnlySpan<float> stereo)
    {
        for (int i = 0; i < stereo.Length; i += 2)
        {
            double l = stereo[i], r = stereo[i + 1];
            double a = _left.Filter(l), b = _right.Filter(r);
            _energy += a * a + b * b;
            double mid = (l + r) * .5;
            _out.Add(mid);
            _in.Add(mid);
            _peakL.Add(l);
            _peakR.Add(r);
        }
        int blockFrames = stereo.Length / 2;
        _frames += blockFrames;
        _blocks.Add(new((_frames - blockFrames * .5) / Rate, _out.Envelope, _in.Envelope, _frames, _energy));
    }

    internal Results Analyze(double start, double end, double landing, double beat)
    {
        double before = WindowLufs(start - 3.2, start - .2);
        double after = WindowLufs(end + .2, end + 3.2);
        double reference = (before + after) * .5;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (double sec = start; sec <= end + .05; sec += .05)
        {
            // The target is the straight line from the level before the mix to the level after it.
            double line = before + (after - before) * Math.Clamp((sec - start) / Math.Max(.001, end - start), 0, 1);
            double relative = WindowLufs(sec - 1.5, sec + 1.5) - line;
            min = Math.Min(relative, min);
            max = Math.Max(relative, max);
        }

        // A tagged bassline is active above −12 dB against its audible solo phrase. The two
        // independent sub carriers survive key lock while the matched filters reject the other deck.
        double outRef = Median(_blocks.Where(b => b.TimeSec >= start - 2.8 && b.TimeSec < start - .1).Select(b => b.OutSub));
        double inRef = Median(_blocks.Where(b => b.TimeSec >= end + .2 && b.TimeSec < end + 2.8).Select(b => b.InSub));
        double activeOut = .25118864 * outRef, activeIn = .25118864 * inRef;
        double overlapStart = double.NaN, longest = 0;
        foreach (var b in _blocks)
        {
            if (b.TimeSec < start - beat * .15 || b.TimeSec > Math.Max(end, landing + beat * .2) + beat * .15) continue;
            if (b.OutSub >= activeOut && b.InSub >= activeIn)
            {
                if (double.IsNaN(overlapStart)) overlapStart = b.TimeSec;
                longest = Math.Max(longest, b.TimeSec - overlapStart);
            }
            else overlapStart = double.NaN;
        }
        return new(before, after, min, max, longest * 1000, outRef, inRef,
            Math.Max(_peakL.SamplePeak, _peakR.SamplePeak), Math.Max(_peakL.OversampledPeak, _peakR.OversampledPeak),
            !_peakL.Invalid && !_peakR.Invalid && double.IsFinite(_energy));
    }

    internal double WindowLufs(double firstSec, double endSec)
    {
        long first = (long)Math.Round(Math.Max(0, firstSec) * Rate);
        long end = (long)Math.Round(Math.Min(_frames / (double)Rate, endSec) * Rate);
        double power = (EnergyAt(end) - EnergyAt(first)) / Math.Max(1, end - first);
        return -.691 + 10 * Math.Log10(Math.Max(1e-12, power));
    }

    private double EnergyAt(long frame)
    {
        if (frame <= 0) return 0;
        if (frame >= _frames) return _energy;
        int lower = 0, upper = _blocks.Count - 1;
        while (lower < upper)
        {
            int mid = (lower + upper) / 2;
            if (frame <= _blocks[mid].End) upper = mid;
            else lower = mid + 1;
        }
        var point = _blocks[lower];
        var previous = lower > 0 ? _blocks[lower - 1] : default;
        return previous.Power + (point.Power - previous.Power) * (frame - previous.End) / (point.End - previous.End);
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }

    private sealed class BassTag(double hz)
    {
        private readonly double _step = Math.PI * 2 * hz / Rate;
        private static readonly double Weight = 1 - Math.Exp(-Math.PI * 2 * 7 / Rate);
        private double _phase, _i, _q, _hi, _hq;
        public double Envelope => 2 * Math.Sqrt(_hi * _hi + _hq * _hq);

        public void Add(double sample)
        {
            var (sin, cos) = Math.SinCos(_phase);
            _i += Weight * (sample * cos - _i);
            _q += Weight * (sample * sin - _q);
            _hi += Weight * (_i - _hi);
            _hq += Weight * (_q - _hq);
            _phase += _step;
            if (_phase >= Math.PI * 2) _phase -= Math.PI * 2;
        }
    }

    private struct PeakMeter
    {
        private double _a, _b, _c;
        public double SamplePeak, OversampledPeak;
        public bool Invalid;

        public void Add(double d)
        {
            if (!double.IsFinite(d))
            {
                Invalid = true;
                return;
            }
            SamplePeak = Math.Max(SamplePeak, Math.Abs(d));
            OversampledPeak = Math.Max(OversampledPeak, Math.Abs(d));
            // Four-point interpolation, checked at quarter-sample offsets. A peak diagnostic,
            // separate from the exact sample clipping check and not a certified true-peak meter.
            double k1 = .5 * (_c - _a), k2 = _a - 2.5 * _b + 2 * _c - .5 * d;
            double k3 = .5 * (d - _a) + 1.5 * (_b - _c);
            foreach (double frac in Fractions)
            {
                double v = _b + frac * (k1 + frac * (k2 + frac * k3));
                OversampledPeak = Math.Max(OversampledPeak, Math.Abs(v));
            }
            (_a, _b, _c) = (_b, _c, d);
        }

        private static ReadOnlySpan<double> Fractions => [.25, .50, .75];
    }
}
