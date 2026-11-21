namespace MixDock.Audio;

/// <summary>Chord voicings the right stick Y axis picks in a Chord bank.</summary>
public enum Voicing { Close, Open, Add9, OctaveStack }

/// <summary>
/// Scale-locked chord maths (pure, unit-tested). Degrees are 0-based scale degrees (0 = I, 3 = IV, 4 = V, 5 = vi, 7 = I an octave up).
/// Major keys use the major scale, minor keys the natural minor scale, so every chord is diatonic to the key.
/// MIDI notes are built around a base octave (C3 = 48) so chords sit in a warm pad range.
/// </summary>
public static class ChordTheory
{
    public const int BaseMidi = 48;
    private static readonly int[] MajorSteps = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] MinorSteps = { 0, 2, 3, 5, 7, 8, 10 };
    private static readonly string[] Roman = { "I", "II", "III", "IV", "V", "VI", "VII" };
    private static readonly string[] PcNames = { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
    public static readonly string[] InversionNames = { "root", "1st inv", "2nd inv" };
    public static readonly string[] VoicingNames = { "close", "open", "add9", "stack" };

    /// <summary>All 24 keys in a fixed order: 12 majors then 12 minors (pitch class 0..11).</summary>
    public static IEnumerable<MusicalKey> AllKeys() => Enumerable.Range(0, 24).Select(i => new MusicalKey(i % 12, i >= 12));

    /// <summary>Semitones above the tonic of a scale degree (degree 7 = octave up, 8 = ii an octave up ...).</summary>
    public static int DegreeOffset(MusicalKey key, int degree)
    {
        var steps = key.Minor ? MinorSteps : MajorSteps;
        degree = Math.Max(0, degree);
        return steps[degree % 7] + 12 * (degree / 7);
    }

    /// <summary>Root-position triad (stacked scale thirds) as semitone offsets above the tonic, ascending.</summary>
    public static int[] TriadOffsets(MusicalKey key, int degree)
        => new[] { DegreeOffset(key, degree), DegreeOffset(key, degree + 2), DegreeOffset(key, degree + 4) };

    /// <summary>Triad quality from its intervals: "" major, "m" minor, "dim" diminished.</summary>
    public static string Quality(MusicalKey key, int degree)
    {
        var t = TriadOffsets(key, degree);
        int third = t[1] - t[0], fifth = t[2] - t[0];
        return third == 4 ? "" : fifth == 6 ? "dim" : "m";
    }

    /// <summary>Chord name such as "Am", "F", "Bdim" (no inversion or voicing).</summary>
    public static string ChordName(MusicalKey key, int degree)
        => PcNames[(((key.PitchClass + DegreeOffset(key, degree)) % 12) + 12) % 12] + Quality(key, degree);

    /// <summary>Roman numeral, lower case for minor/diminished ("I", "vi", "vii°"; "'" marks an octave up).</summary>
    public static string Numeral(MusicalKey key, int degree)
    {
        string r = Roman[Math.Max(0, degree) % 7], q = Quality(key, degree);
        if (q != "") r = r.ToLowerInvariant();
        if (q == "dim") r += "°";
        return degree >= 7 ? r + "'" : r;
    }

    /// <summary>MIDI notes for a chord, ascending. Inversion 0..2 moves the lowest note(s) up an octave; the voicing then shapes it:
    /// Close = the triad as is; Open = bass doubled an octave down, middle note up an octave (1-5-10 spread);
    /// Add9 = triad plus the 9th above the root; OctaveStack = triad plus the same triad an octave up.</summary>
    public static int[] Build(MusicalKey key, int degree, int inversion, Voicing voicing, int baseMidi = BaseMidi)
    {
        int tonic = baseMidi + ((key.PitchClass % 12) + 12) % 12;
        var t = TriadOffsets(key, degree).Select(o => tonic + o).ToArray();
        int root = t[0];
        inversion = Math.Clamp(inversion, 0, 2);
        var n = (int[])t.Clone();
        for (int i = 0; i < inversion; i++) { n[0] += 12; Array.Sort(n); }   // lift the current lowest note an octave
        int[] result;
        switch (voicing)
        {
            case Voicing.Open: result = new[] { n[0] - 12, n[0], n[2], n[1] + 12 }; Array.Sort(result); break;
            case Voicing.Add9: result = n.Append(root + 14).OrderBy(x => x).ToArray(); break;
            case Voicing.OctaveStack: result = n.Concat(n.Select(x => x + 12)).OrderBy(x => x).ToArray(); break;
            default: result = n; break;
        }
        // keep every note in a playable MIDI range
        return result.Select(x => Math.Clamp(x, 24, 108)).ToArray();
    }

    /// <summary>"Am / 1st inv / open" for the overlay.</summary>
    public static string Describe(MusicalKey key, int degree, int inversion, Voicing voicing)
        => $"{ChordName(key, degree)} / {InversionNames[Math.Clamp(inversion, 0, 2)]} / {VoicingNames[(int)voicing]}";

    public static string KeyName(MusicalKey key) => PcNames[((key.PitchClass % 12) + 12) % 12] + (key.Minor ? " minor" : " major");
}

/// <summary>
/// Picks a discrete zone from a stick axis with hysteresis: a value must cross a boundary by <see cref="Hysteresis"/>
/// before the zone changes, so a stick resting on a boundary cannot flicker between two chords' shapes.
/// </summary>
public sealed class ZoneSelector
{
    private readonly double[] _bounds;
    public double Hysteresis { get; }
    public int Zone { get; private set; }

    /// <param name="bounds">ascending boundaries between zones (n bounds = n+1 zones)</param>
    /// <param name="initialZone">zone at rest</param>
    public ZoneSelector(double[] bounds, int initialZone, double hysteresis = .08)
    {
        _bounds = bounds; Zone = Math.Clamp(initialZone, 0, bounds.Length); Hysteresis = hysteresis;
    }

    public int Update(double value)
    {
        if (!double.IsFinite(value)) return Zone;
        // move up while the value is above the upper boundary of the current zone (+hysteresis), down while below the lower one (-hysteresis)
        while (Zone < _bounds.Length && value > _bounds[Zone] + Hysteresis) Zone++;
        while (Zone > 0 && value < _bounds[Zone - 1] - Hysteresis) Zone--;
        return Zone;
    }

    /// <summary>Right stick X: rest/left = root position, right = 1st inversion, far right = 2nd. (Left of centre stays root.)</summary>
    public static ZoneSelector ForInversion() => new(new[] { .45, .8 }, 0);
    /// <summary>Right stick Y: down = octave stack, rest = close, up = open, far up = add9.</summary>
    public static ZoneSelector ForVoicing() => new(new[] { -.5, .35, .75 }, 1);
}
