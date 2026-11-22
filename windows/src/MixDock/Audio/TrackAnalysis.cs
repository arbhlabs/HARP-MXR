namespace MixDock.Audio;

public enum DjGenre { Auto, House, Techno, Trance, DrumAndBass, Dubstep, HipHop, Pop, AfroLatin, OpenFormat }
/// <summary>Moves every time field by the given seconds (grid anchors included).</summary>
public static class TrackAnalysisShift
{
    public static TrackAnalysis Shifted(this TrackAnalysis a, double sec)
    {
        if (sec == 0) return a;
        double frames = sec * 44100;
        return a with
        {
            Tempo = a.Tempo is { } t ? t with { OriginFrame = t.OriginFrame + frames, PhraseStartFrame = t.PhraseStartFrame + frames } : null,
            Segments = [.. a.Segments.Select(s => s with { StartSec = s.StartSec + sec, EndSec = s.EndSec + sec })],
            DurationSec = a.DurationSec + sec, MixInSec = a.MixInSec + sec, MixOutSec = a.MixOutSec + sec,
        };
    }
}

public enum DjSection { Intro, Verse, Build, Drop, Breakdown, Outro }

public sealed record TrackSegment(double StartSec, double EndSec, DjSection Kind, double Energy, double VocalLikelihood,
    double LoudnessLufs);

public sealed record TrackAnalysis(TempoGrid? Tempo, MusicalKey? Key, double KeyConfidence, DjGenre Genre,
    double DurationSec, TrackSegment[] Segments, double MixInSec, double MixOutSec);

/// <summary>
/// Incremental analysis of 44.1 kHz stereo before deck processing. Retains the onset and feature frames
/// needed for musical structure, plus one short FFT window in memory. Call on a worker, one thread per builder.
/// </summary>
public sealed partial class TrackAnalysisBuilder
{
    private const int SampleRate = 44100;
    private const int PulseHop = 220; // roughly 5 ms; use the exact hop duration for grid calculations
    private const double PulsesPerSecond = (double)SampleRate / PulseHop;
    private readonly record struct Pulse(float Onset, float Low, float Full, float Weighted, float Snare);
    private readonly List<Pulse> _pulses = [];
    private readonly Spectrum _spectrum = new();
    private long _frames;
    private int _pulseFrames;
    private double _low, _high, _lowPower, _fullPower, _weightedPower, _snarePower;
    private double _previousLow, _previousFull, _previousSnare;
    private KWeight _left, _right;

    public void Feed(ReadOnlySpan<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            double left = float.IsFinite(stereo[i]) ? stereo[i] : 0;
            double right = float.IsFinite(stereo[i + 1]) ? stereo[i + 1] : 0;
            double mono = .5 * (left + right);
            _low += .026 * (mono - _low);
            _high += .32 * (mono - _high);
            double snare = _high - _low;
            double kl = _left.Filter(left), kr = _right.Filter(right);
            _lowPower += _low * _low;
            _fullPower += .5 * (left * left + right * right);
            _weightedPower += kl * kl + kr * kr;
            _snarePower += snare * snare;
            _spectrum.Feed(mono);
            _frames++;
            if (++_pulseFrames == PulseHop) AddPulse();
        }
    }

    private void AddPulse()
    {
        double low = Math.Sqrt(_lowPower / PulseHop);
        double full = Math.Sqrt(_fullPower / PulseHop);
        double snare = Math.Sqrt(_snarePower / PulseHop);
        double onset = 2 * Math.Max(0, low - _previousLow) + Math.Max(0, full - _previousFull);
        _pulses.Add(new((float)onset, (float)(_lowPower / PulseHop), (float)(_fullPower / PulseHop),
            (float)(_weightedPower / PulseHop), (float)Math.Max(0, snare - _previousSnare)));
        _previousLow = low;
        _previousFull = full;
        _previousSnare = snare;
        _pulseFrames = 0;
        _lowPower = _fullPower = _weightedPower = _snarePower = 0;
    }

    /// <summary>An ongoing stream has no known outro. Use complete=false until its end is confirmed.</summary>
    public TrackAnalysis Analyze(DjGenre genre = DjGenre.Auto, bool complete = true)
    {
        double duration = _frames / (double)SampleRate;
        var tempo = EstimateTempo(genre);
        var bars = BuildBars(duration, tempo);
        var (key, keyConfidence) = EstimateKey();
        if (genre == DjGenre.Auto) genre = DetectGenre(tempo, bars);
        var segments = FindSections(bars, tempo, duration, complete);
        double firstDownbeat = Math.Clamp((tempo?.OriginFrame ?? 0) / SampleRate, 0, duration);
        var entry = segments.FirstOrDefault(s => s.Kind == DjSection.Intro && s.VocalLikelihood < .5)
            ?? segments.FirstOrDefault(s => s.VocalLikelihood < .45 && s.Kind is DjSection.Verse or DjSection.Outro);
        double mixIn = entry == null ? firstDownbeat : Math.Max(firstDownbeat, entry.StartSec);
        var outro = segments.LastOrDefault(s => s.Kind == DjSection.Outro);
        int breakdown = Array.FindLastIndex(segments, s => s.Kind == DjSection.Breakdown);
        double mixOut = outro?.StartSec ?? (breakdown >= 1 ? segments[breakdown - 1].StartSec : duration);
        return new TrackAnalysis(tempo, key, keyConfidence, genre, duration, segments, mixIn, mixOut);
    }

    private (MusicalKey? Key, double Confidence) EstimateKey()
    {
        var chroma = new double[12];
        int frames = 0;
        foreach (var frame in _spectrum.Features)
        {
            if (frame.PitchedPower < .00001) continue;
            frames++;
            for (int i = 0; i < chroma.Length; i++) chroma[i] += frame.Chroma[i];
        }
        return frames < 5 ? (null, 0) : KeyDetector.EstimateChroma(chroma);
    }

    internal static double Loudness(double stereoPower) => Math.Max(-90, -.691 + 10 * Math.Log10(Math.Max(1e-12, stereoPower)));

    internal struct KWeight
    {
        private double _head1, _head2, _rlb1, _rlb2;

        public double Filter(double sample)
        {
            // BS.1770 head shelf and RLB high-pass, bilinear coefficients evaluated at 44.1 kHz.
            double head = 1.530841230050347 * sample + _head1;
            _head1 = -2.650979995154729 * sample + 1.663655113256020 * head + _head2;
            _head2 = 1.169079079921586 * sample - .712595428073225 * head;
            double weighted = head + _rlb1;
            _rlb1 = -2 * head + 1.989169673629796 * weighted + _rlb2;
            _rlb2 = head - .989199035787039 * weighted;
            return weighted;
        }
    }
}
