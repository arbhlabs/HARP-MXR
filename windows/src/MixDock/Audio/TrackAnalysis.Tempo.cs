namespace MixDock.Audio;

public sealed partial class TrackAnalysisBuilder
{
    private TempoGrid? EstimateTempo(DjGenre genre)
    {
        int count = _pulses.Count;
        if (count < 4 * PulsesPerSecond) return null;
        var onset = new double[count];
        double peak = 0, mean = 0;
        for (int i = 2; i < count - 2; i++)
        {
            onset[i] = (PulseOnset(i - 2) + 2 * PulseOnset(i - 1) + 3 * PulseOnset(i) +
                2 * PulseOnset(i + 1) + PulseOnset(i + 2)) / 9;
            mean += onset[i];
            peak = Math.Max(peak, onset[i]);
        }
        if (peak < .003) return null;
        mean /= count;

        double shortest = 60 * PulsesPerSecond / 200, longest = PulsesPerSecond;
        int minLag = (int)Math.Floor(shortest), maxLag = (int)Math.Ceiling(longest);
        var correlation = new double[maxLag + 2];
        for (int lag = minLag / 2; lag < correlation.Length; lag++)
        {
            double dot = 0, left = 0, right = 0;
            for (int i = lag; i < count; i++)
            {
                double a = onset[i] - mean, b = onset[i - lag] - mean;
                dot += a * b;
                left += a * a;
                right += b * b;
            }
            correlation[lag] = dot / Math.Sqrt(Math.Max(1e-16, left * right));
        }

        int best = -1;
        double bestScore = 0, tempoCentre = GenreTempo(genre);
        double lowDensity = _pulses.Average(p => p.Low) / Math.Max(1e-10, _pulses.Average(p => p.Full));
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double current = correlation[lag];
            if (current < .12 || current < correlation[lag - 1] || current < correlation[lag + 1]) continue;
            double bpm = 60 * PulsesPerSecond / lag;
            double doublePeriod = lag * 2 < correlation.Length ? Math.Max(0, correlation[lag * 2]) : 0;
            double halfPeriod = lag / 2.0 >= shortest - 1 ? At(correlation, lag / 2.0) : 0;
            double harmonic = Math.Max(0, doublePeriod) * .14;
            // Longer autocorrelation peaks also occur at integer multiples of the tactus.
            // Penalize an octave only if there is independent evidence between those slower beats.
            double octave = halfPeriod > .58 * current ? .09 * halfPeriod : 0;
            double prior = Math.Exp(-.5 * Math.Pow(Math.Log2(bpm / tempoCentre) / .34, 2));
            double genreWeight = genre is DjGenre.Auto or DjGenre.OpenFormat ? .023 : .3;
            double energyBias = (bpm > 150 ? .015 : -.008) * Math.Clamp(lowDensity, 0, 1);
            double score = current + harmonic - octave + genreWeight * prior + energyBias;
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        if (best < 0) return null;

        double previous = correlation[best - 1], next = correlation[best + 1];
        double denominator = previous - 2 * correlation[best] + next;
        double period = best + (Math.Abs(denominator) > 1e-8 ? .5 * (previous - next) / denominator : 0);
        var events = GetPulsePeaks(onset, peak * .09);
        if (events.Count < 4) return null;
        var (fitted, phase, coherence) = FitComb(events, period, shortest, longest);
        if (coherence < .16) return null;
        (fitted, phase) = RefineBeatLine(events, fitted, phase);

        // RMS accent by beat slot to distinguish the downbeat from the other three beats.
        phase = (phase % fitted + fitted) % fitted;
        if (phase > fitted - 2 && events[0].Time < 2) phase -= fitted;
        var (slot, known) = Downbeat(phase, fitted);
        double origin = Math.Clamp((phase + slot * fitted + .4) * PulseHop, 0, _frames);
        double periodFrames = fitted * PulseHop;
        double confidence = Math.Clamp(.6 * correlation[best] + .4 * coherence, 0, 1);
        return new TempoGrid(60 * SampleRate / periodFrames, periodFrames, origin, known, confidence, 1, origin);
    }

    private double PulseOnset(int i) => _pulses[i].Onset;

    private static double GenreTempo(DjGenre genre) => genre switch
    {
        DjGenre.House => 124, DjGenre.Techno => 136, DjGenre.Trance => 138, DjGenre.DrumAndBass => 174,
        DjGenre.Dubstep => 140, DjGenre.HipHop => 90, DjGenre.Pop => 113, DjGenre.AfroLatin => 106, _ => 120
    };

    private static double At(double[] values, double index)
    {
        int left = (int)index;
        double fraction = index - left;
        return (1 - fraction) * values[left] + fraction * values[left + 1];
    }

    private readonly record struct PulsePeak(double Time, double Weight);

    private static List<PulsePeak> GetPulsePeaks(double[] onset, double threshold)
    {
        var positions = new List<PulsePeak>();
        for (int i = 1; i < onset.Length - 1; i++)
        {
            double a = onset[i - 1], b = onset[i], c = onset[i + 1];
            if (b < threshold || b < a || b <= c) continue;
            double fraction = Math.Clamp(.5 * (a - c) / (a - 2 * b + c), -.5, .5);
            var candidate = new PulsePeak(i + fraction, Math.Sqrt(b));
            // A kick's first few partials may produce nearby maxima. Treat that cluster as one attack.
            if (positions.Count > 0 && candidate.Time - positions[^1].Time < .055 * PulsesPerSecond)
            {
                if (positions[^1].Weight < candidate.Weight) positions[^1] = candidate;
            }
            else positions.Add(candidate);
        }
        return positions;
    }

    private static (double Period, double Phase, double Coherence) FitComb(List<PulsePeak> events, double period,
        double shortest, double longest)
    {
        // Fit over a strong, bounded excerpt before refining against the whole track.
        const int excerptSeconds = 24;
        int bestFrom = 0, bestUntil = 0;
        double sum = 0, bestWeight = 0;
        for (int from = 0, until = 0; from < events.Count; from++)
        {
            while (until < events.Count && events[until].Time - events[from].Time < excerptSeconds * PulsesPerSecond)
                sum += events[until++].Weight;
            if (sum > bestWeight) { bestWeight = sum; bestFrom = from; bestUntil = until; }
            sum -= events[from].Weight;
        }
        double reference = events[bestFrom].Time;
        double bestPeriod = period, phase = 0, bestPower = 0, coherence = 0;
        double start = Math.Max(shortest, period - Math.Max(1.3, period * .018));
        double end = Math.Min(longest, period + Math.Max(1.3, period * .018));
        for (double p = start; p <= end; p += .025)
        {
            double angle = 2 * Math.PI / p, real = 0, imaginary = 0, doubleReal = 0, doubleImaginary = 0;
            for (int i = bestFrom; i < bestUntil; i++)
            {
                var pulse = events[i];
                var (sin, cos) = Math.SinCos((pulse.Time - reference) * angle);
                real += pulse.Weight * cos;
                imaginary += pulse.Weight * sin;
                doubleReal += pulse.Weight * (cos * cos - sin * sin);
                doubleImaginary += pulse.Weight * (2 * sin * cos);
            }
            double fundamental = real * real + imaginary * imaginary;
            double subdivision = doubleReal * doubleReal + doubleImaginary * doubleImaginary;
            double power = fundamental + .25 * subdivision;
            if (power > bestPower)
            {
                bestPower = power;
                bestPeriod = p;
                double phaseAngle = Math.Atan2(imaginary, real);
                if (fundamental < .09 * subdivision)
                {
                    // With strong eighth notes, their first harmonic locks the beat period.
                    // The weaker quarter-note component still identifies which half is accented.
                    double subdivisionAngle = Math.Atan2(doubleImaginary, doubleReal) / 2;
                    if (Math.Cos(phaseAngle - subdivisionAngle) < 0) subdivisionAngle += Math.PI;
                    phaseAngle = subdivisionAngle;
                }
                phase = reference + phaseAngle / angle;
                coherence = Math.Sqrt(Math.Max(fundamental, .49 * subdivision)) / bestWeight;
            }
        }
        return (bestPeriod, phase, coherence);
    }

    private static (double Period, double Phase) RefineBeatLine(List<PulsePeak> events, double period, double phase)
    {
        // Match transients within 25 ms of their comb teeth and regress an exact fractional period.
        // The tolerance also prevents most offbeat hats and rolls from biasing a song's grid.
        double weight = 0, x = 0, y = 0, xx = 0, xy = 0;
        foreach (var pulse in events)
        {
            double n = Math.Round((pulse.Time - phase) / period);
            double error = Math.Abs(pulse.Time - phase - n * period);
            if (error > .025 * PulsesPerSecond) continue;
            double w = pulse.Weight * pulse.Weight;
            weight += w;
            x += w * n;
            y += w * pulse.Time;
            xx += w * n * n;
            xy += w * n * pulse.Time;
        }
        double denominator = weight * xx - x * x;
        if (denominator < 1e-8) return (period, phase);
        double slope = (weight * xy - x * y) / denominator;
        return Math.Abs(slope / period - 1) < .008 ? (slope, (y - slope * x) / weight) : (period, phase);
    }

    private (int Slot, bool Known) Downbeat(double phase, double period)
    {
        Span<double> accents = stackalloc double[4];
        Span<int> counts = stackalloc int[4];
        accents.Clear();
        counts.Clear();
        for (int beat = 0; phase + beat * period + 12 < _pulses.Count; beat++)
        {
            int index = (int)Math.Round(phase + beat * period);
            double low = 0, onset = 0;
            for (int i = Math.Max(0, index - 2); i <= index + 12; i++)
            {
                low += _pulses[i].Low;
                onset = Math.Max(onset, _pulses[i].Onset);
            }
            int slot = beat % 4;
            accents[slot] += Math.Sqrt(low) + onset;
            counts[slot]++;
        }

        int best = 0;
        for (int i = 0; i < 4; i++)
        {
            accents[i] /= Math.Max(1, counts[i]);
            if (accents[i] > accents[best]) best = i;
        }
        double next = 0;
        for (int i = 0; i < 4; i++) if (i != best) next = Math.Max(next, accents[i]);
        bool known = counts[best] >= 3 && accents[best] >= 1.06 * next && accents[best] > .002;
        return (known ? best : 0, known);
    }
}
