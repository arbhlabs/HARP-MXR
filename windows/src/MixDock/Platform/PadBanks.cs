using MixDock.Audio;

namespace MixDock.Platform;

public enum BankMode { Sample, OneShot, Chord, Drum }

/// <summary>Grid the pads launch on. Off = immediate.</summary>
public enum QuantGrid { Off, Sixteenth, Eighth, Quarter, Bar }

/// <summary>Pure pad-bank logic: bank/pad numbering, mode cycling, velocity curve, MIDI note numbers (unit-tested, no UI).</summary>
public static class PadBanks
{
    public const int BankCount = 4, PadsPerBank = 4, PadCount = 16;

    /// <summary>D-pad to bank: Up = 1, Right = 2, Down = 3, Left = 4 (clockwise). 0 = not a D-pad button.</summary>
    public static int BankFromDpad(PadButton b)
        => (b & PadButton.Up) != 0 ? 1 : (b & PadButton.Right) != 0 ? 2 : (b & PadButton.Down) != 0 ? 3 : (b & PadButton.Left) != 0 ? 4 : 0;

    /// <summary>Global pad index 0..15 = bank * 4 + pad (bank and pad are 0-based).</summary>
    public static int Index(int bank, int pad) => Math.Clamp(bank, 0, 3) * PadsPerBank + Math.Clamp(pad, 0, 3);

    public static BankMode NextMode(BankMode m, bool drumEnabled = true)
    {
        var n = (BankMode)(((int)m + 1) % 4);
        return n == BankMode.Drum && !drumEnabled ? BankMode.Sample : n;
    }
    public static BankMode DefaultMode(int bank) => bank switch { 0 => BankMode.Sample, 1 => BankMode.OneShot, 2 => BankMode.Chord, _ => BankMode.Drum };
    public static string ModeName(BankMode m) => m switch { BankMode.Sample => "SAMPLE", BankMode.OneShot => "ONE-SHOT", BankMode.Chord => "CHORD", _ => "DRUM" };

    /// <summary>MIDI note of a pad: base + bank * 4 + pad (Push / Launchpad drum layout starts at 36).</summary>
    public static int Note(int noteBase, int bank, int pad) => Math.Clamp(noteBase + bank * PadsPerBank + pad, 0, 127);
    /// <summary>Inverse of <see cref="Note"/>: global pad index 0..15, or -1 when the note is outside the 16 pads.</summary>
    public static int PadFromNote(int noteBase, int note) => note >= noteBase && note < noteBase + PadCount ? note - noteBase : -1;

    /// <summary>
    /// Velocity (1..127) from trigger depth 0..1: min + (127 - min) * depth^gamma. gamma &lt; 1 = soft (loud sooner), 1 = linear, &gt; 1 = hard.
    /// An idle trigger (below the trigger deadzone) gives the minimum velocity. Non-finite depth counts as idle.
    /// </summary>
    public static int Velocity(double rt, double gamma = 1, int min = 30)
    {
        min = Math.Clamp(min, 1, 126);
        if (!double.IsFinite(rt) || rt <= 0) return min;
        gamma = double.IsFinite(gamma) ? Math.Clamp(gamma, .2, 4) : 1;
        double v = min + (127 - min) * Math.Pow(Math.Clamp(rt, 0, 1), gamma);
        return Math.Clamp((int)Math.Round(v), min, 127);
    }
    public static double CurveGamma(string? name) => (name ?? "").Trim().ToLowerInvariant() switch { "soft" => .6, "hard" => 1.7, _ => 1.0 };
    /// <summary>Amplitude from MIDI velocity: linear, 127 = 1.0.</summary>
    public static float Gain(int velocity) => Math.Clamp(velocity, 0, 127) / 127f;

    /// <summary>Right stick zone to the chord shape (see <see cref="ZoneSelector"/>).</summary>
    public static Voicing VoicingFromZone(int zone) => zone switch { 0 => Voicing.OctaveStack, 1 => Voicing.Close, 2 => Voicing.Open, _ => Voicing.Add9 };
}

/// <summary>
/// Quantize-to-beat launching. <see cref="Beats"/> is the grid in beats; the wait comes from a continuous beat position (a
/// <see cref="BeatClock"/>). A hit up to 30 ms (or a quarter of the grid) late after a line fires immediately.
/// </summary>
public static class PadQuant
{
    public const double LateToleranceMs = 30;
    public static double Beats(QuantGrid g) => g switch { QuantGrid.Sixteenth => .25, QuantGrid.Eighth => .5, QuantGrid.Quarter => 1, QuantGrid.Bar => 4, _ => 0 };
    public static string Label(QuantGrid g) => g switch { QuantGrid.Sixteenth => "1/16", QuantGrid.Eighth => "1/8", QuantGrid.Quarter => "1/4", QuantGrid.Bar => "1 BAR", _ => "OFF" };
    public static QuantGrid Next(QuantGrid g) => (QuantGrid)(((int)g + 1) % 5);

    /// <summary>Milliseconds until the next grid line. 0 = fire now (Off, unknown tempo, or just past a line).</summary>
    public static double MsToNext(double beatPos, double bpm, QuantGrid g)
    {
        double grid = Beats(g);
        if (grid <= 0 || !double.IsFinite(bpm) || bpm <= 0 || !double.IsFinite(beatPos)) return 0;
        double beatMs = 60000.0 / bpm, gridMs = grid * beatMs;
        double since = beatPos % grid; if (since < 0) since += grid;
        double sinceMs = since * beatMs;
        if (sinceMs <= Math.Min(LateToleranceMs, gridMs * .25)) return 0;
        double rem = gridMs - sinceMs;
        return rem < .5 ? 0 : rem;
    }
    public static int FramesToNext(double beatPos, double bpm, QuantGrid g, int sampleRate = 44100)
        => (int)Math.Round(MsToNext(beatPos, bpm, g) * sampleRate / 1000.0);
}

/// <summary>
/// Continuous beat position from a deck's (bpm, phase 0..1) readings. Counts phase wraps so the position keeps growing
/// (needed for 1/4 and 1-bar grids and for the MIDI clock). A bar is 4 beats counted from the first lock.
/// </summary>
public sealed class BeatClock
{
    private double _lastPhase = double.NaN, _count;
    public bool Valid { get; private set; }
    public double Bpm { get; private set; }
    /// <summary>Beat position: whole beats since lock + current phase.</summary>
    public double Position { get; private set; }
    public long StampMs { get; private set; }

    public void Update(double bpm, double phase, long nowMs)
    {
        if (!double.IsFinite(bpm) || bpm <= 0 || !double.IsFinite(phase)) { Valid = false; _lastPhase = double.NaN; return; }
        phase = phase - Math.Floor(phase);
        if (!Valid || double.IsNaN(_lastPhase)) { _count = Valid ? Math.Floor(Position) : 0; }
        else
        {
            double d = phase - _lastPhase;
            if (d < -.5) _count++; else if (d > .5) _count--;
        }
        _lastPhase = phase; Valid = true; Bpm = bpm;
        Position = _count + phase; StampMs = nowMs;
    }
    /// <summary>Position extrapolated to <paramref name="nowMs"/> at the current tempo.</summary>
    public double At(long nowMs) => Valid ? Position + (nowMs - StampMs) * Bpm / 60000.0 : double.NaN;
    public void Reset() { Valid = false; _lastPhase = double.NaN; _count = 0; Position = 0; }
}

/// <summary>One-line controller hints for the footer, built from the LIVE bindings so they always match a remapped pad.
/// Pure and unit-tested.</summary>
public static class PadHints
{
    private static readonly (string Key, string Glyph)[] Order =
    [
        ("A", "A"), ("B", "B"), ("X", "X"), ("Y", "Y"), ("LeftShoulder", "LB"), ("RightShoulder", "RB"),
        ("Up", "D↑"), ("Down", "D↓"), ("Left", "D←"), ("Right", "D→"), ("LeftThumb", "L3"), ("RightThumb", "R3"),
        ("Back", "View"), ("Start", "Menu"),
    ];

    /// <summary>Short name of a bound action: the catalog label without its "(…)" detail, deck prefix dropped.</summary>
    public static string Short(string action)
    {
        string id = action.StartsWith("Focus.") ? action[6..] : action.StartsWith("A.") || action.StartsWith("B.") ? action[2..] : action;
        string label = ActionCatalog.Buttons.FirstOrDefault(a => a.Id == id)?.Label ?? id;
        int cut = label.IndexOf(" (", StringComparison.Ordinal);
        return (cut > 0 ? label[..cut] : label).Replace("Crossfader toward ", "fader→");
    }

    public static string For(bool shift, IReadOnlyDictionary<string, string> buttons)
    {
        var parts = Order.Where(o => buttons.TryGetValue(o.Key, out var a) && !string.IsNullOrWhiteSpace(a))
                         .Select(o => $"{o.Glyph} {Short(buttons[o.Key])}");
        return (shift ? "SHIFT: " : "") + string.Join(" · ", parts);
    }

    public static string Connected(int slot) =>
        $"Controller {slot} connected. Hold View for SHIFT: this line then lists every shift button. Shift+F1 shows the full map.";
}
