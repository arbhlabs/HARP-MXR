namespace MixDock.Audio;

/// <summary>Every stick / button effect of the FX rack, in signal-chain order (time and buffer effects first, then
/// rhythm, filters, drive, modulation, delays, reverbs; the two generators are added last).</summary>
public enum StickFx
{
    BeatRepeat, Reverse, Brake, Freeze, PitchDown,
    Cut, Transformer, TranceGate, Pump, AutoPan,
    LowPass, HighPass, Comb, Vowel, Wobble,
    Drive, Wavefold, BitCrush, RingMod,
    Phaser, Flanger, Chorus,
    TapeEcho, PingPong,
    Hall, Shimmer, GatedVerb,
    NoiseRiser, SubDrop,
}

public enum FxFamily { Time, Rhythm, Filter, Drive, Modulation, Delay, Reverb, Generator }

/// <summary>One effect: display name, family, whether it locks to the deck's beat grid, what the stick distance does,
/// and how fast it engages (momentary effects attack in a few ms; continuous ones glide).</summary>
public sealed record FxInfo(StickFx Id, string Name, FxFamily Family, bool TempoSynced, string Amount, bool Fast = false);

/// <summary>Four right-stick slots (up, down, left, right) and two left-stick slots (left, right).</summary>
public sealed record FxSet(string Name, StickFx Up, StickFx Down, StickFx Left, StickFx Right, StickFx SideLeft, StickFx SideRight)
{
    public StickFx Slot(int i) => i switch { 0 => Up, 1 => Down, 2 => Left, 3 => Right, 4 => SideLeft, _ => SideRight };
    public IEnumerable<StickFx> All => [Up, Down, Left, Right, SideLeft, SideRight];
}

public static class FxCatalog
{
    public static readonly int Count = Enum.GetValues<StickFx>().Length;

    public static readonly IReadOnlyList<FxInfo> All = new FxInfo[]
    {
        new(StickFx.BeatRepeat, "BEAT REPEAT", FxFamily.Time, true, "push = shorter repeat: 1/4 · 1/8 · 1/16 · 1/32 · 1/64 note", true),
        new(StickFx.Reverse, "REVERSE", FxFamily.Time, true, "each beat plays backwards; push = more reversed"),
        new(StickFx.Brake, "VINYL BRAKE", FxFamily.Time, false, "push = slower, full = stop; release spins back up"),
        new(StickFx.Freeze, "FREEZE", FxFamily.Time, true, "holds the last 1/4 beat as a grain cloud"),
        new(StickFx.PitchDown, "PITCH DROP", FxFamily.Time, false, "push = lower, full = one octave down"),
        new(StickFx.Cut, "CUT", FxFamily.Rhythm, false, "silence while held (2 ms ramps)", true),
        new(StickFx.Transformer, "TRANSFORMER", FxFamily.Rhythm, true, "hard 1/16 gate, past half = 1/32", true),
        new(StickFx.TranceGate, "TRANCE GATE", FxFamily.Rhythm, true, "16-step gate pattern; push = deeper, shorter"),
        new(StickFx.Pump, "PUMP", FxFamily.Rhythm, true, "sidechain duck on every beat; push = deeper"),
        new(StickFx.AutoPan, "AUTO PAN", FxFamily.Rhythm, true, "left-right sweep, 1 beat → 1/4 beat"),
        new(StickFx.LowPass, "LOW-PASS", FxFamily.Filter, false, "push = darker, resonance rises"),
        new(StickFx.HighPass, "HIGH-PASS", FxFamily.Filter, false, "push = thinner, resonance rises"),
        new(StickFx.Comb, "COMB", FxFamily.Filter, false, "metallic resonator, 220 → 880 Hz"),
        new(StickFx.Vowel, "VOWEL", FxFamily.Filter, true, "talking formant filter, a-e-i-o-u per bar"),
        new(StickFx.Wobble, "WOBBLE", FxFamily.Filter, true, "LFO low-pass, 1/2 → 1/8 beat"),
        new(StickFx.Drive, "DRIVE", FxFamily.Drive, false, "tube saturation, level-matched"),
        new(StickFx.Wavefold, "WAVEFOLD", FxFamily.Drive, false, "folds the wave into metallic overtones"),
        new(StickFx.BitCrush, "BIT CRUSH", FxFamily.Drive, false, "fewer bits, lower sample rate"),
        new(StickFx.RingMod, "RING MOD", FxFamily.Drive, false, "carrier 55 → 880 Hz"),
        new(StickFx.Phaser, "PHASER", FxFamily.Modulation, true, "sweep 4 beats → 1 beat, deeper"),
        new(StickFx.Flanger, "FLANGER", FxFamily.Modulation, true, "jet sweep 4 beats → 1 beat, more feedback"),
        new(StickFx.Chorus, "CHORUS", FxFamily.Modulation, false, "three-voice stereo widen"),
        new(StickFx.TapeEcho, "DUB ECHO", FxFamily.Delay, true, "dotted 1/8 tape echo, more feedback"),
        new(StickFx.PingPong, "PING-PONG", FxFamily.Delay, true, "1/8 echo bouncing left-right"),
        new(StickFx.Hall, "HALL", FxFamily.Reverb, false, "big hall, longer as you push"),
        new(StickFx.Shimmer, "SHIMMER", FxFamily.Reverb, false, "reverb that rises an octave"),
        new(StickFx.GatedVerb, "GATED VERB", FxFamily.Reverb, true, "dense room cut on every 1/8 note"),
        new(StickFx.NoiseRiser, "NOISE RISER", FxFamily.Generator, true, "white-noise sweep up, pulsing"),
        new(StickFx.SubDrop, "SUB DROP", FxFamily.Generator, true, "808 sub boom on every bar"),
    };

    public static FxInfo Info(StickFx fx) => All[(int)fx];
    public static string Name(StickFx fx) => All[(int)fx].Name;

    /// <summary>Button caption (fits a 62-unit deck FX button at every scale); the full name stays in the HUD and tooltips.</summary>
    public static string Short(StickFx fx) => ShortNames[(int)fx];
    private static readonly string[] ShortNames =
    [
        "REPEAT", "REV", "BRAKE", "FREEZE", "PITCH",
        "CUT", "TRANS", "GATE", "PUMP", "PAN",
        "LPF", "HPF", "COMB", "VOWEL", "WOB",
        "DRIVE", "FOLD", "CRUSH", "RING",
        "PHASE", "FLNG", "CHOR",
        "DUB", "PONG",
        "HALL", "SHIMR", "GATED",
        "RISER", "SUB",
    ];

    /// <summary>Curated sets. Right stick: up / down / left / right. Left stick X: left / right. Up tends to lift, down to
    /// darken or drop. Within a set all six effects are different.</summary>
    public static readonly IReadOnlyList<FxSet> Sets = new FxSet[]
    {
        new("SPACE", StickFx.Hall, StickFx.TapeEcho, StickFx.PingPong, StickFx.Shimmer, StickFx.GatedVerb, StickFx.NoiseRiser),
        new("SWEEP", StickFx.HighPass, StickFx.LowPass, StickFx.Vowel, StickFx.Comb, StickFx.Wobble, StickFx.Flanger),
        new("MOTION", StickFx.Phaser, StickFx.Flanger, StickFx.Chorus, StickFx.AutoPan, StickFx.Vowel, StickFx.PingPong),
        new("CHOP", StickFx.TranceGate, StickFx.Pump, StickFx.BeatRepeat, StickFx.Transformer, StickFx.Reverse, StickFx.GatedVerb),
        new("GRIT", StickFx.BitCrush, StickFx.Drive, StickFx.RingMod, StickFx.Wavefold, StickFx.LowPass, StickFx.Transformer),
        new("WARP", StickFx.Freeze, StickFx.Brake, StickFx.Reverse, StickFx.PitchDown, StickFx.SubDrop, StickFx.BeatRepeat),
    };

    public static int WrapSet(int i) => ((i % Sets.Count) + Sets.Count) % Sets.Count;
}

/// <summary>Stick geometry for the 4-direction effect pads (pure math, unit-tested).</summary>
public static class StickMorph
{
    /// <summary>Direction shares for up, down, left, right: cos² / sin² of the angle inside its quadrant, so exactly two
    /// neighbours share a diagonal and the shares always sum to 1. Returns false when the stick is too close to the centre
    /// to have a reliable direction (the caller then keeps the previous shares).</summary>
    public static bool Directions(double x, double y, Span<double> shares, double minRadius = .18)
    {
        shares.Clear();
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        double r = Math.Sqrt(x * x + y * y);
        if (r < minRadius) return false;
        double ax = Math.Abs(x) / r, ay = Math.Abs(y) / r;      // cos / sin of the angle to the horizontal
        double horizontal = ax * ax, vertical = ay * ay;         // sum = 1
        if (y >= 0) shares[0] = vertical; else shares[1] = vertical;
        if (x < 0) shares[2] = horizontal; else shares[3] = horizontal;
        return true;
    }

    /// <summary>Two-slot side pad (left stick X): share of the left and right slot.</summary>
    public static (double Left, double Right) Side(double x) => !double.IsFinite(x) || x == 0 ? (0, 0) : x < 0 ? (1, 0) : (0, 1);
}
