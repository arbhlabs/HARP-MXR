using NAudio.Wave;

namespace MixDock.Audio;

/// <summary>Original synthesized DJ arrangements for the offline transition gate.</summary>
internal static class SmartMixFixtures
{
    internal const int SampleRate = 44100;
    private const double Tau = 2 * Math.PI;
    private readonly record struct Phrase(int Bars, DjSection Section, double Energy, double VocalLikelihood = 0);

    internal sealed record Song(string Name, double Bpm, MusicalKey Key, DjGenre Genre, int Seed, TrackAnalysis Analysis,
        double DriftBpmPerSec = 0, MusicalKey? Key2 = null, int KeyChangeBar = int.MaxValue)
    {
        public double BeatSeconds => 60 / Bpm;
        public double BarSeconds => BeatSeconds * 4;
        public double BpmAt(double sec) => Bpm + DriftBpmPerSec * sec;
        /// <summary>Beat clock of the stream: the tempo moves linearly when DriftBpmPerSec is set.</summary>
        public double BeatsAt(double sec) => Bpm * sec / 60 + DriftBpmPerSec * sec * sec / 120;
        public double SecAt(double beats) => SecAtBeats(Bpm, DriftBpmPerSec, beats);
        public MusicalKey KeyAtBar(int bar) => Key2 is { } k && bar >= KeyChangeBar ? k : Key;
    }

    internal static double SecAtBeats(double bpm, double drift, double beats)
    {
        if (drift == 0) return beats * 60 / bpm;
        double a = drift / 120, b = bpm / 60;
        return (-b + Math.Sqrt(b * b + 4 * a * beats)) / (2 * a);
    }

    /// <summary>One long stream whose tempo ramps 124 to 125.5 BPM (a DJ set that drifts), leaving into Glass Steps.</summary>
    internal static Pair BpmRamp() => new("bpm-ramp-drift",
        Make("Drift Set", 124, 0, false, DjGenre.House, 211,
            [new(8, DjSection.Intro, 4), new(16, DjSection.Verse, 6), new(8, DjSection.Drop, 7), new(24, DjSection.Outro, 5)],
            drift: 1.5 / 108),
        Pair0In(), TransitionStyle.BassSwap,
        "Listen for the house groove staying on the grid while its tempo creeps up, then one clean bass swap.");

    /// <summary>One stream that changes key (C to G, bar 24) before it leaves into a track in G.</summary>
    internal static Pair KeyChange() => new("key-change",
        Make("Pivot Set", 124, 0, false, DjGenre.House, 223,
            [new(8, DjSection.Intro, 4), new(16, DjSection.Verse, 6), new(8, DjSection.Drop, 7), new(24, DjSection.Outro, 5)],
            key2: new MusicalKey(7, false), keyChangeBar: 24),
        Pair0In(), TransitionStyle.BassSwap,
        "Listen for the pad and bassline moving up a fifth before the next track arrives in the new key.");

    private static Song Pair0In() => Create()[0].In;

    internal sealed record Pair(string Slug, Song Out, Song In, TransitionStyle Expected, string ListeningNotes);

    internal static Pair[] Create() =>
    [
        new("house-to-house",
            Make("Amber Key", 124, 0, false, DjGenre.House, 7,
                [new(8, DjSection.Intro, 4), new(16, DjSection.Verse, 6), new(8, DjSection.Drop, 7), new(32, DjSection.Outro, 5)]),
            Make("Glass Steps", 126, 7, false, DjGenre.House, 19,
                [new(32, DjSection.Intro, 5), new(16, DjSection.Verse, 6), new(8, DjSection.Build, 7), new(16, DjSection.Drop, 8)]),
            TransitionStyle.BassSwap,
            "Listen for four-on-the-floor kicks and plucks, with an incoming top layer and one clean change of bass."),
        new("techno-to-trance",
            Make("Relay Coil", 132, 9, true, DjGenre.Techno, 37,
                [new(8, DjSection.Intro, 4), new(16, DjSection.Verse, 6), new(8, DjSection.Drop, 7), new(24, DjSection.Outro, 6)]),
            Make("Clear Current", 142, 4, true, DjGenre.Trance, 51,
                [new(24, DjSection.Intro, 6), new(8, DjSection.Build, 7), new(16, DjSection.Drop, 8), new(8, DjSection.Outro, 4)]),
            TransitionStyle.TempoRamp,
            "Listen for the shift from a straight machine pulse into the higher arpeggio, with tempo movement during the handoff."),
        new("hip-hop-to-pop",
            Make("Late Receipt", 90, 9, true, DjGenre.HipHop, 61,
                [new(8, DjSection.Intro, 4), new(16, DjSection.Verse, 6, .8), new(8, DjSection.Outro, 5)]),
            Make("Neon Exit", 115, 1, false, DjGenre.Pop, 73,
                [new(8, DjSection.Intro, 5), new(16, DjSection.Verse, 7, .8), new(8, DjSection.Breakdown, 4), new(8, DjSection.Outro, 5)]),
            TransitionStyle.EchoOut,
            "Listen for the swung, dry boom-bap giving way to bright pop chords. The outgoing rhythm should leave a short echo tail."),
        new("drum-and-bass-double-drop",
            Make("Underpass Signal", 174, 4, true, DjGenre.DrumAndBass, 97,
                [new(8, DjSection.Intro, 4), new(8, DjSection.Verse, 5), new(8, DjSection.Build, 7), new(16, DjSection.Drop, 9), new(8, DjSection.Outro, 5)]),
            Make("Night Rail", 174, 11, true, DjGenre.DrumAndBass, 113,
                [new(8, DjSection.Intro, 4), new(8, DjSection.Build, 7), new(16, DjSection.Drop, 9), new(8, DjSection.Verse, 6), new(8, DjSection.Outro, 5)]),
            TransitionStyle.DoubleDrop,
            "Listen for both breakbeats landing together at the drop, with room in the low end for one bassline."),
        new("dubstep-to-drum-and-bass",
            Make("Pressure Plate", 140, 2, true, DjGenre.Dubstep, 127,
                [new(8, DjSection.Intro, 4), new(8, DjSection.Build, 7), new(16, DjSection.Drop, 9), new(8, DjSection.Outro, 5)]),
            Make("Flight Path", 174, 2, true, DjGenre.DrumAndBass, 139,
                [new(8, DjSection.Intro, 4), new(8, DjSection.Build, 7), new(16, DjSection.Drop, 9), new(8, DjSection.Outro, 4)]),
            TransitionStyle.DropSwap,
            "Listen for a halftimed snare build resolving on the one into the faster breakbeat, without competing drum grids."),
        new("pop-to-afro",
            Make("Blue Hour", 106, 5, false, DjGenre.Pop, 157,
                [new(8, DjSection.Intro, 4), new(16, DjSection.Verse, 6, .8), new(16, DjSection.Breakdown, 3), new(16, DjSection.Verse, 5)]),
            Make("Lime Shaker", 109, 0, false, DjGenre.AfroLatin, 179,
                [new(24, DjSection.Intro, 5), new(16, DjSection.Verse, 6), new(8, DjSection.Drop, 8), new(8, DjSection.Outro, 4)]),
            TransitionStyle.BreakdownEntry,
            "Listen for the pop drums thinning out before the shakers and syncopated hand percussion take over.")
    ];

    private static Song Make(string name, double bpm, int pitch, bool minor, DjGenre genre, int seed, Phrase[] phrases,
        double drift = 0, MusicalKey? key2 = null, int keyChangeBar = int.MaxValue)
    {
        var key = new MusicalKey(pitch, minor);
        double barSeconds = 240 / bpm;
        int bar = 0;
        var segments = new TrackSegment[phrases.Length];
        for (int i = 0; i < phrases.Length; i++)
        {
            var phrase = phrases[i];
            double start = SecAtBeats(bpm, drift, bar * 4);
            bar += phrase.Bars;
            segments[i] = new(start, SecAtBeats(bpm, drift, bar * 4), phrase.Section, phrase.Energy, phrase.VocalLikelihood, -14);
        }
        double period = SampleRate * 60 / bpm;
        var grid = new TempoGrid(bpm, period, 0, true, 1, 1, 0);
        var exit = segments.LastOrDefault(segment => segment.Kind == DjSection.Outro)
            ?? segments.FirstOrDefault(segment => segment.Kind == DjSection.Breakdown) ?? segments[^1];
        var analysis = new TrackAnalysis(grid, key, 1, genre, segments[^1].EndSec, segments,
            segments[0].StartSec, exit.StartSec);
        return new Song(name, bpm, key, genre, seed, analysis, drift, key2, keyChangeBar);
    }

    internal static double SubFrequency(Song song, bool incoming) => SubFrequency(song.Key, incoming);

    internal static double SubFrequency(MusicalKey key, bool incoming)
    {
        double root = 32.70319566 * Math.Pow(2, key.PitchClass / 12.0);
        return incoming ? root * 2 : root;
    }

    /// <summary>Synthesize stereo frames [startFrame, startFrame + n) of a song.</summary>
    internal static void Fill(Song song, bool incoming, long startFrame, Span<float> stereo)
    {
        var track = new Arrangement(song, incoming);
        for (int i = 0; i < stereo.Length / 2; i++)
        {
            var (l, r) = track.Render(startFrame + i);
            stereo[2 * i] = (float)l; stereo[2 * i + 1] = (float)r;
        }
    }

    /// <summary>Decode pre-roll from the same continuous synthesis clock as the score.
    /// A cue at seconds zero can still receive complete decoder pre-roll.</summary>
    internal static void WriteExcerpt(string path, Song song, double startSec, double endSec, bool incoming,
        double gain = 1)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 2));
        long start = (long)Math.Round(startSec * SampleRate), end = (long)Math.Ceiling(endSec * SampleRate);
        var chunk = new short[8192];
        var track = new Arrangement(song, incoming);
        while (start < end)
        {
            int frames = (int)Math.Min(chunk.Length / 2, end - start);
            for (int i = 0; i < frames; i++)
            {
                var (left, right) = track.Render(start + i);
                chunk[2 * i] = Pcm(left * gain);
                chunk[2 * i + 1] = Pcm(right * gain);
            }
            writer.WriteSamples(chunk, 0, frames * 2);
            start += frames;
        }
    }

    internal static short Pcm(double sample) => (short)Math.Round(short.MaxValue * Math.Clamp(sample, -1, 1));

    private sealed class Arrangement
    {
        private static readonly double[] Notes = Enumerable.Range(0, 128)
            .Select(note => 440 * Math.Pow(2, (note - 69) / 12.0)).ToArray();
        private static readonly int[] MinorRoots = [0, 8, 3, 10];
        private static readonly int[] MajorRoots = [0, 7, 9, 5];
        private static readonly int[] Arp = [0, 1, 2, 4, 2, 1, 3, 4];
        private readonly Song _song;
        private readonly double _sub, _sub2;
        private readonly int _kick, _snare, _hat, _extra;
        private double _beatSec;
        private readonly double _chordGain, _arpGain, _grit;

        internal Arrangement(Song song, bool incoming)
        {
            _song = song;
            _sub = SubFrequency(song.Key, incoming);
            _sub2 = SubFrequency(song.Key2 ?? song.Key, incoming);
            // Event masks index the sixteen semiquavers of a bar, starting at the least significant bit.
            (_kick, _snare, _hat, _extra, _chordGain, _arpGain, _grit) = song.Genre switch
            {
                DjGenre.House => (0x1111, 0x1010, 0x4444, 0x8204, .32, .43, .09),
                DjGenre.Techno => (0x1111, 0x1010, 0x5555, 0x2222, .20, .34, .30),
                DjGenre.Trance => (0x1111, 0x1010, 0x5555, 0x0404, .35, .62, .12),
                DjGenre.HipHop => (0x0421, 0x1010, 0x4545, 0x2220, .58, .21, .07),
                DjGenre.Pop => (0x1511, 0x1010, 0x5555, 0x4020, .55, .43, .12),
                DjGenre.DrumAndBass => (0x0101, 0x1010, 0x5555, 0x1010, .30, .38, .32),
                DjGenre.Dubstep => (0x2401, 0x0100, 0x4245, 0x0808, .27, .36, .62),
                DjGenre.AfroLatin => (0x1111, 0x1040, 0x4444, 0x9249, .47, .39, .13),
                _ => (0x1111, 0x1010, 0x4444, 0x8282, .35, .40, .10)
            };
        }

        internal (double Left, double Right) Render(long frame)
        {
            double seconds = frame / (double)SampleRate;
            double beats = _song.BeatsAt(seconds);
            _beatSec = 60 / _song.BpmAt(seconds);
            int bar = (int)Math.Floor(beats / 4);
            var key = _song.KeyAtBar(bar);
            double subHz = key == _song.Key ? _sub : _sub2;
            double beat = beats - 4 * bar;
            var phrase = _song.Analysis.Segments[0];
            foreach (var segment in _song.Analysis.Segments)
            {
                if (seconds < segment.StartSec) break;
                phrase = segment;
            }
            double build = phrase.Kind == DjSection.Build
                ? Math.Clamp((seconds - phrase.StartSec) / (phrase.EndSec - phrase.StartSec), 0, 1) : 0;
            double drums = phrase.Kind switch
            {
                DjSection.Breakdown => .15,
                DjSection.Build => 1 - .55 * build,
                DjSection.Intro => .83,
                _ => 1
            };
            double sub = phrase.Kind switch
            {
                DjSection.Breakdown => .04,
                DjSection.Build => .82 - .75 * build,
                DjSection.Drop => 1.22,
                _ => 1
            };

            double kickAge = Age(beat, _kick);
            double snareAge = Age(beat, _snare);
            double hatAge = Age(beat, _hat);
            double extraAge = Age(beat, _extra);
            double noise = Noise(frame, _song.Seed);
            double treble = (noise - Noise(frame - 1, _song.Seed)) * .65;
            double kickPhase = Tau * (53 * kickAge + 1.55 * (1 - Math.Exp(-31 * kickAge)));
            double kick = drums * .29 * Math.Sin(kickPhase) * Envelope(kickAge, 28, .20);
            double snare = drums * .11 *
                (.76 * noise + .24 * Math.Sin(Tau * 182 * snareAge)) * Envelope(snareAge, 31, .16);
            double hat = .042 * treble * Envelope(hatAge, 53, .14);
            double perc = .065 * (.75 * Math.Sin(Tau * (255 + 11 * (_song.Seed % 9)) * extraAge) + .25 * noise)
                * Envelope(extraAge, 44, .10);

            double beatPulse = 0.6 + .4 * Envelope(Age(beat, 0x5555), 10, .24);
            double bassPhase = Tau * subHz * seconds + .17 * _grit * Math.Sin(Tau * beat);
            double bass = sub * (.098 * Math.Sin(bassPhase) * beatPulse
                + _grit * .077 * Math.Sin(bassPhase * 3.0) * (.65 + .35 * Math.Sin(Tau * beat * 2)));

            int chordIndex = ((bar + _song.Seed) % 4 + 4) % 4;
            int chord = 48 + key.PitchClass + (key.Minor ? MinorRoots[chordIndex] : MajorRoots[chordIndex]);
            bool minorChord = key.Minor ? chordIndex == 0 : chordIndex == 2;
            int third = minorChord ? 3 : 4;
            double chordEnvelope = .55 + .45 * Envelope(Age(beat, 0x0101), 7, .44);
            double texture = .22 * Osc(chord, seconds, .18) + .16 * Osc(chord + third, seconds, .58)
                + .16 * Osc(chord + 7, seconds, .79) + .09 * Osc(chord + 12, seconds, .27);
            double chordL = _chordGain * chordEnvelope * (texture + .07 * Osc(chord + 12 + third, seconds, .2));
            double chordR = _chordGain * chordEnvelope * (texture - .07 * Osc(chord + 12 + third, seconds, .2));
            int step = (int)Math.Floor(beat * 4);
            int degree = Arp[(step + _song.Seed + ((bar % 4 + 4) % 4)) % Arp.Length];
            int pitch = chord + 12 + (degree switch { 0 => 0, 1 => third, 2 => 7, 3 => 12, _ => 12 + third });
            int arpMask = _song.Genre == DjGenre.Trance ? 0xFFFF : _song.Genre == DjGenre.AfroLatin ? 0x4A49 : 0x4425;
            double arpAge = Age(beat, arpMask);
            double arp = _arpGain * Envelope(arpAge, 13, .17) *
                (.21 * Osc(pitch, arpAge, .15) + .05 * Osc(pitch + 12, arpAge, .15));
            if (phrase.Kind == DjSection.Build)
            {
                int rolls = build < .5 ? 0x1111 : build < .75 ? 0x5555 : 0xFFFF;
                snare += .082 * build * treble * Envelope(Age(beat, rolls), 57, .07);
                hat += .03 * build * treble;
            }
            double centre = kick + snare + bass;
            double pan = ((step + _song.Seed) % 3 - 1) * .23;
            return (centre + chordL + arp * (1 + pan) + hat * .85 + perc * 1.2,
                centre + chordR + arp * (1 - pan) + hat * 1.15 + perc * .8);
        }

        private double Age(double beat, int mask)
        {
            int step = (int)Math.Floor(beat * 4);
            int elapsed = 0;
            while ((mask & (1 << ((step - elapsed) & 15))) == 0) elapsed++;
            double eventBeat = .25 * (step - elapsed);
            if (_song.Genre == DjGenre.HipHop && ((step - elapsed) & 1) == 1)
            {
                eventBeat += .040;
                if (eventBeat > beat)
                {
                    do { elapsed++; }
                    while ((mask & (1 << ((step - elapsed) & 15))) == 0);
                    eventBeat = .25 * (step - elapsed) + (((step - elapsed) & 1) == 1 ? .040 : 0);
                }
            }
            return (beat - eventBeat) * _beatSec;
        }

        private static double Envelope(double seconds, double decay, double end) => seconds >= end ? 0
            : Math.Min(1, seconds * 900) * Math.Exp(-decay * seconds);

        private static double Osc(int note, double time, double phase) => Math.Sin(Tau * (Notes[note] * time + phase));

        private static double Noise(long sample, int seed)
        {
            uint x = unchecked((uint)(sample + seed * 173_831));
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x * (2.0 / uint.MaxValue) - 1;
        }
    }
}
