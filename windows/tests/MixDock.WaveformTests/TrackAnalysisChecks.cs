using MixDock.Audio;

internal static class TrackAnalysisChecks
{
    private const int Rate = 44100;
    private const double Offset = .183;

    public static void Run(Action<bool, string> check)
    {
        foreach (double bpm in new[] { 87, 120, 128, 140, 174 })
        {
            var result = Analyze(bpm, 12, DjGenre.Auto);
            check(result.Tempo is { } tempo && Math.Abs(tempo.Bpm - bpm) <= .5,
                $"track tempo {bpm:0} ±0.5 BPM (measured {result.Tempo?.Bpm:0.00})");
            double error = result.Tempo is { } grid
                ? Math.Abs(grid.OriginFrame / Rate - Offset) : double.PositiveInfinity;
            check(result.Tempo?.DownKnown == true && error < .01,
                $"track downbeat {bpm:0} within 10 ms (error {1_000 * error:0.0} ms)");
        }

        var half = Analyze(87, 12, DjGenre.HipHop, ambiguousOctave: true);
        var doubleTime = Analyze(87, 12, DjGenre.DrumAndBass, ambiguousOctave: true);
        check(Math.Abs((half.Tempo?.Bpm ?? 0) - 87) < .5 && Math.Abs((doubleTime.Tempo?.Bpm ?? 0) - 174) < .5,
            $"genre resolves 87 / 174 octave ({half.Tempo?.Bpm:0.00} / {doubleTime.Tempo?.Bpm:0.00})");

        var major = Analyze(120, 12, DjGenre.House, harmony: 1);
        var minor = Analyze(128, 12, DjGenre.Techno, harmony: 2);
        check(major.Key == new MusicalKey(0, false) && major.KeyConfidence > .24,
            $"C major chord progression ({major.Key}, confidence {major.KeyConfidence:0.00})");
        check(minor.Key == new MusicalKey(9, true) && minor.KeyConfidence > .24,
            $"A minor chord progression ({minor.Key}, confidence {minor.KeyConfidence:0.00})");

        var arrangement = Analyze(120, 48, DjGenre.House, harmony: 1, arrangement: true);
        double boundarySec = Offset + 16 * 4 * 60.0 / 120;
        check(arrangement.Segments.Any(s => Math.Abs(s.StartSec - boundarySec) < .08),
            $"arrangement novelty is snapped to the 16-bar boundary (segments {string.Join(", ", arrangement.Segments.Select(s => s.StartSec.ToString("0.00")))})");
        check(arrangement.Segments.Any(s => s.Kind == DjSection.Intro) && arrangement.Segments.Any(s => s.Kind == DjSection.Drop)
            && arrangement.Segments.Any(s => s.Kind == DjSection.Outro) && arrangement.MixOutSec > arrangement.MixInSec,
            $"structure labels and transition cues ({string.Join(", ", arrangement.Segments.Select(s => s.Kind))})");
        check(arrangement.Segments.All(s => s.Energy is >= 1 and <= 10 && s.VocalLikelihood is >= 0 and <= 1 &&
            double.IsFinite(s.LoudnessLufs)), "segment energy, vocal likelihood and loudness are finite and bounded");
        var incomplete = Analyze(120, 16, DjGenre.House, complete: false);
        check(incomplete.Segments.All(s => s.Kind != DjSection.Outro), "a live prefix does not assume an outro");

        var silent = new TrackAnalysisBuilder();
        silent.Feed(new float[Rate * 12]);
        var silence = silent.Analyze();
        check(silence.Tempo is null && silence.Key is null, "silence has no tempo or key");
        var c = new MusicalKey(0, false);
        check(c.Camelot == "8B" && new MusicalKey(9, true).Camelot == "8A" &&
            MusicalKey.Compatible(c, new MusicalKey(7, false)) &&
            MusicalKey.Compatible(c, new MusicalKey(5, false)) &&
            MusicalKey.Compatible(c, new MusicalKey(9, true)) &&
            !MusicalKey.Compatible(c, new MusicalKey(6, true)), "Camelot same, adjacent, relative and incompatible keys");
    }

    private static TrackAnalysis Analyze(double bpm, int bars, DjGenre genre, bool ambiguousOctave = false,
        int harmony = 0, bool arrangement = false, bool complete = true)
    {
        int[][] major = [[60, 64, 67], [60, 64, 67], [65, 69, 72], [67, 71, 74]];
        int[][] minor = [[57, 60, 64], [57, 60, 64], [62, 65, 69], [64, 68, 71]];
        double beatSeconds = 60 / bpm;
        int frames = (int)Math.Round((Offset + bars * 4 * beatSeconds) * Rate);
        var builder = new TrackAnalysisBuilder();
        var data = new float[4096 * 2];
        var random = new Random(19);
        for (int start = 0; start < frames; start += data.Length / 2)
        {
            int length = Math.Min(data.Length / 2, frames - start);
            for (int i = 0; i < length; i++)
            {
                double time = (start + i) / (double)Rate - Offset;
                if (time < 0) { data[i * 2] = data[i * 2 + 1] = 0; continue; }
                int beat = (int)(time / beatSeconds), bar = beat / 4, beatSlot = beat % 4;
                double phase = time - beat * beatSeconds;
                double half = phase - beatSeconds / 2;
                double level = arrangement ? (bar < 16 ? .34 : bar < 32 ? 1 : .23) : .75;
                double kick = Kick(phase) * (beatSlot == 0 ? .9 : .49);
                if (ambiguousOctave) kick += Kick(half) * (beatSlot == 0 ? .44 : .47);
                double hat = half is >= 0 and < .035 ? .06 * Math.Exp(-100 * half) * (random.NextDouble() * 2 - 1) : 0;
                double chord = 0;
                if (harmony > 0 && (!arrangement || bar is >= 16 and < 32))
                {
                    var notes = (harmony == 1 ? major : minor)[bar % 4];
                    double ageInBar = phase + beatSlot * beatSeconds;
                    double envelope = Math.Min(1, ageInBar * 60) * Math.Min(1, (4 * beatSeconds - ageInBar) * 60);
                    for (int note = 0; note < notes.Length; note++)
                    {
                        double hz = 440 * Math.Pow(2, (notes[note] - 69) / 12.0);
                        chord += (note == 0 ? .065 : .037) * Math.Sin(2 * Math.PI * hz * time) * envelope;
                    }
                }
                data[i * 2] = (float)(level * (kick + hat + chord));
                data[i * 2 + 1] = (float)(level * (kick + hat * .85 + chord * .9));
            }
            builder.Feed(data.AsSpan(0, length * 2));
        }
        return builder.Analyze(genre, complete);
    }

    private static double Kick(double t) => t is >= 0 and < .085
        ? Math.Sin(2 * Math.PI * (53 * t + 1.7 * (1 - Math.Exp(-50 * t)))) * Math.Exp(-44 * t) : 0;
}
