namespace MixDock.Audio;

public sealed partial class TrackAnalysisBuilder
{
    private sealed record Bar(double Start, double Power, double LowShare, double Onsets, double Snares,
        double Flux, double Centroid, double Vocals, double[] Chroma, double[] Cepstra)
    {
        public double Level => Loudness(Power);
        public double Energy => 1 + 9 * Math.Clamp(.63 * (Level + 39) / 27 +
            .21 * Math.Sqrt(LowShare) + .16 * Math.Clamp(Onsets / 7, 0, 1), 0, 1);
    }

    private List<Bar> BuildBars(double duration, TempoGrid? tempo)
    {
        var bars = new List<Bar>();
        double origin = tempo?.OriginFrame / SampleRate ?? 0;
        double barSeconds = 4 * tempo?.PeriodFrames / SampleRate ?? 2;
        for (double start = 0, next = origin + barSeconds; start < duration; start = next, next += barSeconds)
            bars.Add(DescribeBar(start, Math.Min(next, duration)));
        return bars;
    }

    private Bar DescribeBar(double start, double end)
    {
        int pulseFrom = Math.Min(_pulses.Count, (int)Math.Round(start * PulsesPerSecond));
        int pulseUntil = Math.Min(_pulses.Count, (int)Math.Round(end * PulsesPerSecond));
        double low = 0, full = 0, weighted = 0, strong = 0, snarePk = 0;
        for (int i = pulseFrom; i < pulseUntil; i++)
        {
            var pulse = _pulses[i];
            low += pulse.Low;
            full += pulse.Full;
            weighted += pulse.Weighted;
            strong = Math.Max(strong, pulse.Onset);
            snarePk = Math.Max(snarePk, pulse.Snare);
        }
        double duration = Math.Max(.05, end - start);
        double onsets = CountAttacks(pulseFrom, pulseUntil, Math.Max(.007, .16 * strong), false) / duration;
        double snares = CountAttacks(pulseFrom, pulseUntil, Math.Max(.008, .24 * snarePk), true) / duration;

        var spectra = _spectrum.Features;
        int first = Math.Min(spectra.Count, (int)Math.Round(start / Spectrum.StepSeconds));
        int last = Math.Min(spectra.Count, (int)Math.Round(end / Spectrum.StepSeconds));
        var chroma = new double[12];
        var cepstra = new double[6];
        double flux = 0, centroid = 0, vocalPresence = 0, vocalMid = 0, vocalMid2 = 0;
        int tonalFrames = 0, vocalFrames = 0;
        for (int i = first; i < last; i++)
        {
            var frame = spectra[i];
            flux += frame.Flux;
            centroid += frame.Centroid;
            for (int c = 0; c < cepstra.Length; c++) cepstra[c] += frame.Cepstra[c];
            if (frame.PitchedPower < .00001) continue;
            tonalFrames++;
            for (int c = 0; c < chroma.Length; c++) chroma[c] += frame.Chroma[c];
            if (frame.MidShare < .27 || frame.HarmonicRatio < .45) continue;

            // Peak-to-residual energy in 300 Hz–3 kHz. Add temporal centroid stability below.
            vocalPresence += Math.Sqrt(frame.MidShare * frame.HarmonicRatio);
            vocalMid += frame.MidCentroid;
            vocalMid2 += frame.MidCentroid * frame.MidCentroid;
            vocalFrames++;
        }
        double n = Math.Max(1, last - first);
        for (int i = 0; i < chroma.Length; i++) chroma[i] /= Math.Max(1, tonalFrames);
        for (int i = 0; i < cepstra.Length; i++) cepstra[i] /= n;
        double vocals = 0;
        if (vocalFrames > 2)
        {
            double centre = vocalMid / vocalFrames;
            double variation = Math.Sqrt(Math.Max(0, vocalMid2 / vocalFrames - centre * centre)) / Math.Max(100, centre);
            double stability = 1 - Math.Clamp((variation - .22) / .55, 0, 1);
            double staticTonePenalty = .54 + .46 * Math.Clamp(variation / .055, 0, 1);
            vocals = Math.Clamp(1.55 * vocalPresence / n * stability * staticTonePenalty, 0, 1);
        }
        return new Bar(start, weighted / Math.Max(1, pulseUntil - pulseFrom), Math.Clamp(low / Math.Max(1e-10, full), 0, 1),
            onsets, snares, flux / n, centroid / n, vocals, chroma, cepstra);
    }

    private int CountAttacks(int from, int until, double threshold, bool snare)
    {
        int count = 0, last = -20;
        for (int i = Math.Max(1, from); i < Math.Min(_pulses.Count - 1, until); i++)
        {
            if (i - last < 13) continue;
            double current = SnareOrOnset(_pulses[i]);
            if (current <= threshold || current < SnareOrOnset(_pulses[i - 1]) || current <= SnareOrOnset(_pulses[i + 1])) continue;
            last = i;
            count++;
        }
        return count;

        double SnareOrOnset(Pulse pulse) => snare ? pulse.Snare : pulse.Onset;
    }

    private static TrackSegment[] FindSections(List<Bar> bars, TempoGrid? tempo, double duration, bool complete)
    {
        if (bars.Count == 0) return [];
        int width = 4, stride = 8;
        // The diagonal strip of a self-similarity matrix is sufficient for four-bar checkerboard novelty.
        var matrix = new double[bars.Count, width * 2];
        for (int i = 0; i < bars.Count; i++)
        for (int lag = 0; lag < width * 2 && i + lag < bars.Count; lag++)
            matrix[i, lag] = Similarity(bars[i], bars[i + lag]);
        var splits = new List<int> { 0 };
        for (int bar = stride; bar + width <= bars.Count; bar += stride)
        {
            double novelty = 0;
            for (int i = bar - width; i < bar + width; i++)
            for (int j = i + 1; j < bar + width; j++)
                novelty += ((i < bar) == (j < bar) ? 1 : -1) * (matrix[i, j - i] - 1);
            if (novelty / (width * width) > .17 || bar - splits[^1] >= 32) splits.Add(bar);
        }
        splits.Add(bars.Count);

        var summaries = new Bar[splits.Count - 1];
        var builds = new bool[summaries.Length];
        for (int i = 0; i < summaries.Length; i++)
        {
            var slice = bars.GetRange(splits[i], splits[i + 1] - splits[i]);
            summaries[i] = AverageBar(slice);
            if (slice.Count < 8) continue;
            // Look across the final eight bars so a build inside a longer section still has a signature.
            var early = AverageBar(slice.GetRange(slice.Count - 8, 4));
            var late = AverageBar(slice.GetRange(slice.Count - 4, 4));
            builds[i] = late.Flux > early.Flux * 1.09 && late.Snares >= early.Snares + .35 &&
                late.Centroid > early.Centroid * 1.18 && late.LowShare < early.LowShare * .85;
        }
        var result = new TrackSegment[summaries.Length];
        double typical = bars.Select(b => b.Energy).Order().ElementAt(bars.Count / 2);
        for (int i = 0; i < result.Length; i++)
        {
            var current = summaries[i];
            var previous = i > 0 ? summaries[i - 1] : current;
            bool lowEnergy = current.Energy < Math.Max(3.1, typical - .7);
            bool lowReturn = current.LowShare > previous.LowShare * 1.24;
            bool lift = i > 0 && (current.Energy >= previous.Energy + 1.05 || current.Level > previous.Level + 4);
            var kind = i > 0 && (builds[i - 1] && lowReturn || lift && current.LowShare >= .12) ? DjSection.Drop
                : builds[i] ? DjSection.Build
                : i == 0 ? DjSection.Intro
                : complete && i == result.Length - 1 && (lowEnergy || current.Level <= previous.Level - 2.5) ? DjSection.Outro
                : lowEnergy && (current.LowShare < .32 || current.Onsets < 2.2) ? DjSection.Breakdown
                : DjSection.Verse;
            double end = i + 1 < result.Length ? summaries[i + 1].Start : duration;
            result[i] = new TrackSegment(current.Start, end, kind, current.Energy, current.Vocals, current.Level);
        }
        return result;
    }

    private static Bar AverageBar(IReadOnlyList<Bar> bars)
    {
        var first = bars[0];
        return first with
        {
            Power = bars.Average(b => b.Power), LowShare = bars.Average(b => b.LowShare),
            Onsets = bars.Average(b => b.Onsets), Snares = bars.Average(b => b.Snares), Flux = bars.Average(b => b.Flux),
            Vocals = bars.Max(b => b.Vocals), Centroid = bars.Average(b => b.Centroid)
        };
    }

    private static double Similarity(Bar a, Bar b)
    {
        double chroma = 0, cepstra = 0;
        for (int i = 0; i < a.Chroma.Length; i++) chroma += Math.Abs(a.Chroma[i] - b.Chroma[i]) / 2;
        for (int i = 0; i < a.Cepstra.Length; i++) cepstra += Math.Pow(a.Cepstra[i] - b.Cepstra[i], 2);
        return 1 - .27 * Math.Clamp(Math.Abs(a.Level - b.Level) / 15, 0, 1) - .23 * chroma
            - .30 * Math.Clamp(Math.Sqrt(cepstra / a.Cepstra.Length) * 2, 0, 1)
            - .12 * Math.Clamp(Math.Abs(a.LowShare - b.LowShare) * 2, 0, 1)
            - .08 * Math.Clamp(Math.Abs(a.Flux - b.Flux) * 3, 0, 1);
    }

    private static DjGenre DetectGenre(TempoGrid? tempo, List<Bar> bars)
    {
        if (tempo is not { Confidence: >= .2 } || bars.Count == 0) return DjGenre.OpenFormat;
        double bpm = tempo.Bpm, energy = bars.Average(b => b.Energy), bass = bars.Average(b => b.LowShare);
        double density = bars.Average(b => b.Onsets) / (bpm / 60), vocals = bars.Average(b => b.Vocals);
        if (bpm >= 155) return DjGenre.DrumAndBass;
        if (bpm >= 134) return (bass > .53 && density < 1.35) ? DjGenre.Dubstep : energy >= 7 ? DjGenre.Techno : DjGenre.Trance;
        if (bpm <= 103 && bass >= .34) return DjGenre.HipHop;
        if (vocals >= .55 && bass < .52) return DjGenre.Pop;
        if (bpm is >= 96 and <= 126 && density > 1.65) return DjGenre.AfroLatin;
        if (bpm is >= 113 and <= 133) return energy >= 7.5 && bass >= .45 ? DjGenre.Techno : DjGenre.House;
        return DjGenre.OpenFormat;
    }
}
