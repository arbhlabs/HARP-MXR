using System.Text.Json;
using System.Text.Json.Serialization;
using MixDock.Platform;

namespace MixDock.Settings;

public sealed class DeckState
{
    public int Low { get; set; } = 50;
    public int Mid { get; set; } = 50;
    public int High { get; set; } = 50;
    public int Filter { get; set; } = 50;
    /// <summary>Latched (fine) filter base with full precision; null in 1.0 configs (use Filter).</summary>
    public double? FilterLatch { get; set; }
    public int Trim { get; set; } = 85;
    public bool Mute { get; set; }
    public string? LastProcessName { get; set; }
}
/// <summary>One pad bank: its mode and, for Chord mode, the scale degrees (0-based: 0 = I, 3 = IV, 4 = V, 5 = vi, 7 = I an octave up) of the 4 pads and of the Shift set.</summary>
public sealed class BankState
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public BankMode Mode { get; set; }
    public int[] Degrees { get; set; } = { 0, 3, 4, 5 };
    public int[] AltDegrees { get; set; } = { 1, 2, 6, 7 };
}
public sealed class AppConfig
{
    public DeckState A { get; set; } = new();
    public DeckState B { get; set; } = new();
    public int LatencyMs { get; set; } = 120;
    /// <summary>Process names (substring, case-insensitive) MixDock never captures or redirects.</summary>
    public List<string> ProtectedApps { get; set; } = new();
    /// <summary>Look-ahead delay per deck (A, B) in seconds, 0..30: live audio is heard this much later so the waveform
    /// shows what is coming. 0 keeps browser video in sync with the picture.</summary>
    public double[] LookAheadSec { get; set; } = [DefaultLookAheadSec, DefaultLookAheadSec];
    public const double DefaultLookAheadSec = 10, MaxLookAheadSec = 30;
    public double LookAhead(int deck) => LookAheadSec.Length > deck ? LookAheadSec[deck] : DefaultLookAheadSec;
    public void SetLookAhead(int deck, double sec)
    {
        if (LookAheadSec.Length < 2) LookAheadSec = [LookAhead(0), LookAhead(1)];
        LookAheadSec[deck] = Math.Clamp(double.IsFinite(sec) ? sec : DefaultLookAheadSec, 0, MaxLookAheadSec);
    }
    /// <summary>Waveform memory: recognise live tracks and keep their derived waveform, fingerprint and analysis (never audio) on disk.</summary>
    public bool WaveformMemory { get; set; } = true;
    public int Crossfader { get; set; } = 50;
    public int Master { get; set; } = 88;
    public bool GamepadEnabled { get; set; } = true;
    public bool GamepadRumble { get; set; } = true;
    /// <summary>Left stick Y spring filter feel: radial deadzone, expo blend (0 = linear, 1 = cubic), smoothing.</summary>
    public double FilterDeadzone { get; set; } = 0.22;
    public double FilterExpo { get; set; } = 0.6;
    public double FilterSmoothMs { get; set; } = 60;
    public string? OutputId { get; set; }
    // Bind any combination using Ctrl+Alt+Shift+<key>. Ctrl/Alt/Shift optional.
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["Ctrl+Alt+Shift+Left"] = "Crossfader-",
        ["Ctrl+Alt+Shift+Right"] = "Crossfader+",
        ["Ctrl+Alt+Shift+Up"] = "Master+",
        ["Ctrl+Alt+Shift+Down"] = "Master-",
        ["Ctrl+Alt+Shift+A"] = "A.Filter-",
        ["Ctrl+Alt+Shift+D"] = "A.Filter+",
        ["Ctrl+Alt+Shift+J"] = "B.Filter-",
        ["Ctrl+Alt+Shift+L"] = "B.Filter+",
        ["Ctrl+Alt+Shift+M"] = "MasterMute",
        ["Ctrl+Alt+Shift+Space"] = "PauseAll"
    };
    /// <summary>Config schema version. 0/1 = MixDock 1.0 (left stick X = crossfader); 2 = 1.1 pro mapping; 3 = 1.2 performance mode (right stick = FX pad); 4 = 1.3 MIDI pad (banks, chords, quantize, MIDI).</summary>
    public int ConfigVersion { get; set; }
    public const int CurrentVersion = 8;

    // ---- 1.3 "MIDI Pad"
    /// <summary>The 4 pad banks (D-pad Up/Right/Down/Left). Defaults: Sample, One-shot, Chord, Drum.</summary>
    public List<BankState> Banks { get; set; } = DefaultBanks();
    public int ActiveBank { get; set; }
    /// <summary>Quantize grid for pad launches (Shift+LB cycles Off, 1/16, 1/8, 1/4, 1 bar).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))] public QuantGrid Quantize { get; set; } = QuantGrid.Sixteenth;
    /// <summary>Velocity from RT depth: soft, normal or hard curve, minimum velocity when RT is idle, and an off switch (fixed velocity 100).</summary>
    public string VelocityCurve { get; set; } = "normal";
    public int VelocityMin { get; set; } = 30;
    public bool VelocitySensitive { get; set; } = true;
    /// <summary>Chord key: from the focus deck's detected key when KeyAuto, else this manual key (pitch class 0 = C).</summary>
    public bool KeyAuto { get; set; } = true;
    public int ManualKeyPc { get; set; }
    public bool ManualKeyMinor { get; set; }
    public bool MidiEnabled { get; set; } = true;
    /// <summary>Output port name (empty = automatic: teVirtualMIDI "MixDock", else loopMIDI / MixDock).</summary>
    public string MidiOutPort { get; set; } = "";
    /// <summary>Optional input port (empty = none): incoming notes light the pad grid, CC controls the FX amount.</summary>
    public string MidiInPort { get; set; } = "";
    public int MidiChannel { get; set; } = 1;
    public int MidiChordChannel { get; set; } = 2;
    public int MidiNoteBase { get; set; } = 36;
    public bool MidiClock { get; set; } = true;
    public int MidiFxCc { get; set; } = 1;
    public static List<BankState> DefaultBanks() => Enumerable.Range(0, 4).Select(i => new BankState { Mode = PadBanks.DefaultMode(i) }).ToList();
    // Axis actions: *.Filter = ABSOLUTE spring sweep around the latched value (release = back to the latch);
    // *.FilterFine = slow latching creep of that base value; *.EchoSend / *.EchoThrow / Riser = momentary amounts;
    // everything else (Crossfader, *.Low, Master...) = rate knob that stops dead at rest. "Focus." = the focused deck (LB/RB).
    public Dictionary<string, string> GamepadAxes { get; set; } = DefaultAxes();
    /// <summary>Axis actions while SHIFT is held (an axis not listed here keeps its normal action).</summary>
    public Dictionary<string, string> ShiftAxes { get; set; } = DefaultShiftAxes();
    /// <summary>Right-stick FX pad: radial deadzone and amount smoothing (ms).</summary>
    public double FxDeadzone { get; set; } = 0.2;
    public double FxSmoothMs { get; set; } = 60;
    public Dictionary<string, string> GamepadButtons { get; set; } = DefaultButtons();
    /// <summary>Second function of every button while SHIFT is held.</summary>
    public Dictionary<string, string> ShiftButtons { get; set; } = DefaultShiftButtons();
    /// <summary>Hold this button for the shift layer (its own action fires on a short tap).</summary>
    public string ShiftButton { get; set; } = "Back";
    /// <summary>Holding all of these also gives the shift layer.</summary>
    public string ShiftChord { get; set; } = "LeftShoulder+RightShoulder";
    /// <summary>Hold all of these for PanicHoldMs: kill / restore all audio.</summary>
    public string PanicChord { get; set; } = "Back+Start";
    public int PanicHoldMs { get; set; } = 500;
    /// <summary>Echo delay used by the echo throw / send, in beats.</summary>
    public double EchoBeats { get; set; } = 0.5;
    /// <summary>Length of the RT riser build, in beats.</summary>
    public double RiserBeats { get; set; } = 8;

    // ---- 1.4 "Live DJ" defaults: every input does something audible on a live app deck (Spotify, browser) out of the box.
    public static Dictionary<string, string> DefaultAxes()
    {
        var axes = ClassicAxes();
        axes["LeftThumbX"] = "Focus.FxBlend";
        return axes;
    }
    private static Dictionary<string, string> ClassicAxes() => new()
    {
        ["LeftThumbY"] = "Focus.Filter",
        ["LeftThumbX"] = "Crossfader",
        ["RightThumbX"] = "Focus.FxX",
        ["RightThumbY"] = "Focus.FxY",
        ["LeftTrigger"] = "Focus.EchoThrow",
        ["RightTrigger"] = "Focus.BeatRoll"
    };
    public static Dictionary<string, string> DefaultShiftAxes() => new()
    {
        ["LeftThumbX"] = "Focus.FilterFine",
        ["LeftTrigger"] = "Focus.TimeBend",
        ["RightTrigger"] = "Focus.Reverse"
    };
    public static Dictionary<string, string> DefaultButtons()
    {
        var buttons = ClassicButtons();
        buttons["Left"] = "Crossfader-";
        buttons["Right"] = "Crossfader+";
        return buttons;
    }
    private static Dictionary<string, string> ClassicButtons() => new()
    {
        ["A"] = "Focus.LowKill", ["B"] = "Focus.Loop",
        ["X"] = "Focus.Roll:1/4", ["Y"] = "Focus.Roll:1/8",
        ["LeftShoulder"] = "FocusA", ["RightShoulder"] = "FocusB",
        ["Up"] = "Focus.LoopDouble", ["Down"] = "Focus.LoopHalve",
        ["Left"] = "Crossfader-", ["Right"] = "Crossfader+",
        ["LeftThumb"] = "Focus.FilterReset", ["RightThumb"] = "FxCycle",
        ["Back"] = "Focus.TapBpm", ["Start"] = "Focus.Sync"
    };
    /// <summary>The 1.4.0 Live DJ buttons ("Classic"), kept for migration.</summary>
    public static Dictionary<string, string> V5Buttons()
    {
        var b = DefaultButtons(); b["Left"] = "Focus.MidKill"; b["Right"] = "Focus.HighKill"; return b;
    }
    public static Dictionary<string, string> V5ShiftButtons()
    {
        var b = DefaultShiftButtons(); b["Up"] = "Bank:1"; b["Right"] = "Bank:2"; b["Down"] = "Bank:3"; b["Left"] = "Bank:4"; return b;
    }
    public static Dictionary<string, string> DefaultShiftButtons() => new()
    {
        ["A"] = "Focus.Pad:1", ["B"] = "Focus.Pad:2",
        ["X"] = "Focus.Pad:3", ["Y"] = "Focus.Pad:4",
        ["LeftShoulder"] = "QuantCycle", ["RightShoulder"] = "CrossfaderReset",
        ["Up"] = "BankNext", ["Down"] = "BankPrev", ["Left"] = "Focus.MidKill", ["Right"] = "Focus.HighKill",
        ["LeftThumb"] = "Focus.EqReset", ["RightThumb"] = "FxFreeze",
        ["Back"] = "BankMode", ["Start"] = "Focus.PlayPause"
    };
    // ---- the 1.3 "MIDI Pad" layout (ConfigVersion 4), kept as the "Pad performer" preset and for migration
    public static Dictionary<string, string> PadAxes() => new()
    {
        ["LeftThumbY"] = "Focus.Filter",
        ["LeftThumbX"] = "Focus.FilterFine",
        ["RightThumbX"] = "Focus.FxX",
        ["RightThumbY"] = "Focus.FxY",
        ["LeftTrigger"] = "Focus.TimeBend",
        ["RightTrigger"] = "Focus.BeatRoll"
    };
    public static Dictionary<string, string> PadShiftAxes() => new()
    {
        ["LeftTrigger"] = "Focus.SpinBack",
        ["RightTrigger"] = "Focus.Reverse"
    };
    public static Dictionary<string, string> PadButtons() => new()
    {
        ["A"] = "Focus.Pad:1", ["B"] = "Focus.Pad:2",
        ["X"] = "Focus.Pad:3", ["Y"] = "Focus.Pad:4",
        ["LeftShoulder"] = "FocusA", ["RightShoulder"] = "FocusB",
        ["Up"] = "Bank:1", ["Right"] = "Bank:2", ["Down"] = "Bank:3", ["Left"] = "Bank:4",
        ["LeftThumb"] = "Focus.FilterFineReset", ["RightThumb"] = "FxCycle",
        ["Back"] = "Focus.TapBpm", ["Start"] = "AutoMix"
    };
    public static Dictionary<string, string> PadShiftButtons() => new()
    {
        ["A"] = "Focus.PadCapture:1", ["B"] = "Focus.PadCapture:2",
        ["X"] = "Focus.PadCapture:3", ["Y"] = "Focus.PadCapture:4",
        ["LeftShoulder"] = "QuantCycle", ["RightShoulder"] = "Focus.Sync",
        ["Up"] = "Focus.Loop", ["Down"] = "Focus.Cue",
        ["Left"] = "Focus.BeatJumpBack", ["Right"] = "Focus.BeatJumpFwd",
        ["LeftThumb"] = "Focus.EqReset", ["RightThumb"] = "FxFreeze",
        ["Back"] = "BankMode", ["Start"] = "PadGrid"
    };
    // ---- 1.5 stick FX (R3): "Stick FX" = Live DJ with the crossfader on left-stick ↕, the set's two side effects on
    // left-stick ↔, the 4-direction set pad on the right stick and L3 / R3 stepping the set. The filter moves to Shift +
    // left-stick ↕ (same spring). The crossfader axis is the rate control: it stays where you leave it.
    public static Dictionary<string, string> StickFxAxes()
    {
        var a = DefaultAxes();
        a["LeftThumbY"] = "Crossfader"; a["LeftThumbX"] = "Focus.SetSide";
        a["RightThumbX"] = "Focus.SetPadX"; a["RightThumbY"] = "Focus.SetPadY";
        return a;
    }
    public static Dictionary<string, string> StickFxShiftAxes() { var a = DefaultShiftAxes(); a["LeftThumbY"] = "Focus.Filter"; return a; }
    public static Dictionary<string, string> StickFxButtons()
    {
        var b = DefaultButtons(); b["A"] = "Focus.BassCut"; b["LeftThumb"] = "SetPrev"; b["RightThumb"] = "SetNext"; return b;
    }
    public static Dictionary<string, string> StickFxShiftButtons() { var b = DefaultShiftButtons(); b["RightThumb"] = "FxClear"; return b; }
    // ---- "FX Pad": controller 2 (R7). Clicky buttons = momentary hits, sticks = two 4-direction set pads (this set and
    // the next), triggers = pressure (echo throw, riser build; with Shift: brake depth, reverse), D-pad = set and target.
    public static Dictionary<string, string> FxPadAxes() => new()
    {
        ["LeftThumbX"] = "Focus.SetPadX", ["LeftThumbY"] = "Focus.SetPadY",
        ["RightThumbX"] = "Focus.SetPad2X", ["RightThumbY"] = "Focus.SetPad2Y",
        ["LeftTrigger"] = "Focus.EchoThrow", ["RightTrigger"] = "Riser"
    };
    public static Dictionary<string, string> FxPadShiftAxes() => new() { ["LeftTrigger"] = "Focus.TimeBend", ["RightTrigger"] = "Focus.Reverse" };
    public static Dictionary<string, string> FxPadButtons() => new()
    {
        ["A"] = "Focus.Stutter:1/8", ["B"] = "Focus.Stutter:1/16", ["X"] = "Focus.Transformer:1/16", ["Y"] = "Focus.GrainFreeze",
        ["LeftShoulder"] = "Focus.Cut", ["RightShoulder"] = "Focus.SubDrop",
        ["Up"] = "SetNext", ["Down"] = "SetPrev", ["Left"] = "FxTargetPrev", ["Right"] = "FxTargetNext",
        ["LeftThumb"] = "SetPrev", ["RightThumb"] = "SetNext", ["Back"] = "FxClear", ["Start"] = "PadOverlay"
    };
    public static Dictionary<string, string> FxPadShiftButtons() => new()
    {
        ["A"] = "Focus.Stutter:1/4", ["B"] = "Focus.Stutter:1/32", ["X"] = "Focus.Transformer:1/32", ["Y"] = "Focus.SpinBack",
        ["LeftShoulder"] = "Focus.Reverse", ["RightShoulder"] = "Focus.TimeBend",
        ["Up"] = "SetNext", ["Down"] = "SetPrev", ["Left"] = "FxTargetPrev", ["Right"] = "FxTargetNext",
        ["LeftThumb"] = "FxClear", ["RightThumb"] = "FxClear", ["Start"] = "SwapControllers"
    };
    /// <summary>Generic extra inputs (back paddles, extra top buttons) a controller exposes beyond XInput.</summary>
    public const int ExtraButtonCount = 8;
    public static readonly string[] Presets = { "Live DJ", "Pad performer", "Classic DJ", "Stick FX", "FX Pad", "GameSir G7 Pro" };
    /// <summary>Replace all four controller layers (and the modifiers) with a named preset.</summary>
    public void ApplyPreset(string name)
    {
        if (name is "Stick FX" or "GameSir G7 Pro")
        {
            GamepadAxes = StickFxAxes(); ShiftAxes = StickFxShiftAxes(); GamepadButtons = StickFxButtons(); ShiftButtons = StickFxShiftButtons();
            ShiftButton = "Back"; ShiftChord = "LeftShoulder+RightShoulder"; PanicChord = "Back+Start"; PanicHoldMs = 500;
            if (name == "GameSir G7 Pro")
            {
                // back paddles L4 / R4 = Extra1 / Extra2, top buttons L5 / R5 = Extra3 / Extra4 (see windows/docs/CONTROLLERS.md)
                ShiftButton = "Back,Extra1";
                GamepadButtons["Extra2"] = "SetNext";
                GamepadButtons["Extra3"] = "Focus.PlayPause";
                GamepadButtons["Extra4"] = "Focus.Sync";
                ShiftButtons["Extra2"] = "SetPrev";
            }
            return;
        }
        if (name == "FX Pad")
        {
            GamepadAxes = FxPadAxes(); ShiftAxes = FxPadShiftAxes(); GamepadButtons = FxPadButtons(); ShiftButtons = FxPadShiftButtons();
            ShiftButton = "Back"; ShiftChord = ""; PanicChord = "Back+Start"; PanicHoldMs = 500;
            return;
        }
        bool pad = name == "Pad performer";
        bool classic = name is "Classic" or "Classic DJ";
        GamepadAxes = pad ? PadAxes() : classic ? ClassicAxes() : DefaultAxes();
        ShiftAxes = pad ? PadShiftAxes() : DefaultShiftAxes();
        GamepadButtons = pad ? PadButtons() : classic ? V5Buttons() : DefaultButtons();
        if (name == "Live DJ") GamepadButtons["A"] = "Focus.BassCut";
        ShiftButtons = pad ? PadShiftButtons() : classic ? V5ShiftButtons() : DefaultShiftButtons();
        ShiftButton = "Back"; ShiftChord = "LeftShoulder+RightShoulder"; PanicChord = "Back+Start"; PanicHoldMs = 500;
    }
    private static Dictionary<string, string> V2Axes() => new()
    {
        ["LeftThumbY"] = "Focus.Filter", ["LeftThumbX"] = "Focus.FilterFine", ["RightThumbX"] = "Crossfader",
        ["RightThumbY"] = "Focus.EchoSend", ["LeftTrigger"] = "Focus.EchoThrow", ["RightTrigger"] = "Riser"
    };
    private static Dictionary<string, string> V2Buttons() => new()
    {
        ["A"] = "Focus.Cue", ["B"] = "Focus.Sync", ["X"] = "Focus.Roll:1/4", ["Y"] = "Focus.Roll:1/8",
        ["LeftShoulder"] = "FocusA", ["RightShoulder"] = "FocusB", ["Up"] = "Focus.LoopDouble", ["Down"] = "Focus.LoopHalve",
        ["Left"] = "Focus.BeatJumpBack", ["Right"] = "Focus.BeatJumpFwd", ["LeftThumb"] = "Focus.FilterFineReset",
        ["RightThumb"] = "Focus.Loop", ["Back"] = "Focus.TapBpm", ["Start"] = "AutoMix"
    };
    private static Dictionary<string, string> V2ShiftButtons() => new()
    {
        ["A"] = "Focus.LowKill", ["B"] = "Focus.MidKill", ["X"] = "Focus.HighKill", ["Y"] = "Focus.EchoFreeze",
        ["LeftShoulder"] = "Focus.PlayPause", ["RightShoulder"] = "CrossfaderReset", ["Up"] = "Focus.Trim+", ["Down"] = "Focus.Trim-",
        ["Left"] = "Focus.BrowsePrev", ["Right"] = "Focus.BrowseNext", ["LeftThumb"] = "Focus.EqReset", ["RightThumb"] = "Focus.Mute",
        ["Back"] = "Focus.Sync", ["Start"] = "Focus.Load"
    };
    // the 1.2 layers (ConfigVersion 3), kept only so a 1.2 config can be migrated without touching bindings the user changed
    private static Dictionary<string, string> V3Buttons() => new()
    {
        ["Up"] = "Focus.LoopDouble", ["Down"] = "Focus.LoopHalve", ["Left"] = "Focus.BeatJumpBack", ["Right"] = "Focus.BeatJumpFwd"
    };
    private static Dictionary<string, string> V3ShiftButtons() => new()
    {
        ["LeftShoulder"] = "Focus.PlayPause", ["RightShoulder"] = "CrossfaderReset",
        ["Left"] = "Focus.BrowsePrev", ["Right"] = "Focus.BrowseNext", ["Back"] = "Focus.Sync", ["Start"] = "Focus.Load"
    };
    private static readonly Dictionary<string, string> V1Axes = new()
    { ["LeftThumbY"] = "A.Filter", ["RightThumbY"] = "B.Filter", ["LeftThumbX"] = "Crossfader" };
    private static readonly Dictionary<string, string> V1Buttons = new()
    {
        ["A"] = "A.FilterReset", ["B"] = "B.FilterReset", ["X"] = "A.Mute", ["Y"] = "B.Mute",
        ["LeftShoulder"] = "Crossfader-", ["RightShoulder"] = "Crossfader+", ["Start"] = "MasterMute"
    };
    private static bool Same(Dictionary<string, string>? a, Dictionary<string, string> b)
        => a != null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Upgrade a 1.0 config: untouched 1.0 defaults become the 1.1 defaults; a customised map only loses
    /// the old LeftThumbX=Crossfader binding (moved to the right stick, left X becomes the fine filter).</summary>
    public void Migrate()
    {
        GamepadAxes ??= DefaultAxes(); GamepadButtons ??= DefaultButtons(); ShiftButtons ??= DefaultShiftButtons(); ShiftAxes ??= DefaultShiftAxes();
        if (ConfigVersion < 2)
        {
            if (Same(GamepadAxes, V1Axes)) GamepadAxes = V2Axes();
            else if (GamepadAxes.TryGetValue("LeftThumbX", out var lx) && lx == "Crossfader")
            {
                GamepadAxes["LeftThumbX"] = GamepadAxes.TryGetValue("LeftThumbY", out var ly) && ly.EndsWith(".Filter")
                    ? ly[..^".Filter".Length] + ".FilterFine" : "Focus.FilterFine";
                if (!GamepadAxes.ContainsKey("RightThumbX")) GamepadAxes["RightThumbX"] = "Crossfader";
            }
            if (Same(GamepadButtons, V1Buttons)) GamepadButtons = V2Buttons();
        }
        if (ConfigVersion < 3)
        {
            // 1.2: the right stick is the FX pad (no crossfader on it), triggers are time bend / beat roll, face buttons are pads.
            if (Same(GamepadAxes, V2Axes())) GamepadAxes = PadAxes();
            else
            {
                if (GamepadAxes.GetValueOrDefault("RightThumbX") == "Crossfader") GamepadAxes["RightThumbX"] = "Focus.FxX";
                if (GamepadAxes.GetValueOrDefault("RightThumbY") == "Focus.EchoSend") GamepadAxes["RightThumbY"] = "Focus.FxY";
                if (GamepadAxes.GetValueOrDefault("LeftTrigger") == "Focus.EchoThrow") GamepadAxes["LeftTrigger"] = "Focus.TimeBend";
                if (GamepadAxes.GetValueOrDefault("RightTrigger") == "Riser") GamepadAxes["RightTrigger"] = "Focus.BeatRoll";
            }
            if (Same(GamepadButtons, V2Buttons())) GamepadButtons = PadButtons();
            else if (GamepadButtons.GetValueOrDefault("RightThumb") == "Focus.Loop") GamepadButtons["RightThumb"] = "FxCycle";
            if (Same(ShiftButtons, V2ShiftButtons())) ShiftButtons = PadShiftButtons();
            else if (ShiftButtons.GetValueOrDefault("RightThumb") == "Focus.Mute" || !ShiftButtons.ContainsKey("RightThumb")) ShiftButtons["RightThumb"] = "FxFreeze";
            if (ShiftAxes.Count == 0) ShiftAxes = PadShiftAxes();
            if (FxDeadzone is < 0 or > 0.9 || !double.IsFinite(FxDeadzone)) FxDeadzone = 0.2;
            if (FxSmoothMs is < 5 or > 500 || !double.IsFinite(FxSmoothMs)) FxSmoothMs = 60;
        }
        if (ConfigVersion < 4)
        {
            // 1.3: the D-pad selects banks; its 1.2 actions moved to the Shift layer. Only bindings still at their 1.2 default move;
            // anything the user changed is left exactly as it is.
            foreach (var (k, v) in V3Buttons())
                if (GamepadButtons.GetValueOrDefault(k) == v) GamepadButtons[k] = PadButtons()[k];
            foreach (var (k, v) in V3ShiftButtons())
                if (ShiftButtons.GetValueOrDefault(k) == v) ShiftButtons[k] = PadShiftButtons()[k];
        }
        if (ConfigVersion < 5)
        {
            // 1.4: an untouched 1.3 layout (face buttons = empty sample pads: silent out of the box) becomes the Live DJ preset.
            // A layout the user edited is left exactly as it is.
            bool untouched = Same(GamepadAxes, PadAxes()) && Same(GamepadButtons, PadButtons()) && Same(ShiftButtons, PadShiftButtons())
                && (Same(ShiftAxes, PadShiftAxes()) || Same(ShiftAxes, DefaultShiftAxes()));
            if (untouched) ApplyPreset("Default");
        }
        if (ConfigVersion < 6)
        {
            // 1.5 Live DJ remap: D-pad left/right = crossfader, left stick X = FX blend, mid/high kill move to Shift+D-pad
            // left/right and bank select to Shift+D-pad up/down (next/previous). Only an untouched 1.4 layout changes.
            if (Same(GamepadAxes, ClassicAxes()) && Same(ShiftAxes, DefaultShiftAxes()) &&
                Same(GamepadButtons, V5Buttons()) && Same(ShiftButtons, V5ShiftButtons()))
            {
                GamepadAxes["LeftThumbX"] = "Focus.FxBlend";
                GamepadButtons = DefaultButtons(); ShiftButtons = DefaultShiftButtons();
            }
            Hotkeys.TryAdd("Ctrl+Alt+Shift+Space", "PauseAll");
        }
        SetLookAhead(0, LookAhead(0)); SetLookAhead(1, LookAhead(1));
        if (string.IsNullOrWhiteSpace(ShiftButton) && string.IsNullOrWhiteSpace(ShiftChord)) ShiftChord = "LeftShoulder+RightShoulder";
        Banks = NormalizeBanks(Banks);
        ActiveBank = Math.Clamp(ActiveBank, 0, 3);
        if (!Enum.IsDefined(Quantize)) Quantize = QuantGrid.Sixteenth;
        VelocityMin = Math.Clamp(VelocityMin, 1, 126);
        MidiChannel = Math.Clamp(MidiChannel, 1, 16); MidiChordChannel = Math.Clamp(MidiChordChannel, 1, 16);
        MidiNoteBase = Math.Clamp(MidiNoteBase, 0, 111); MidiFxCc = Math.Clamp(MidiFxCc, 0, 127);
        ManualKeyPc = ((ManualKeyPc % 12) + 12) % 12;
        NormalizeControllerProfiles();
        ConfigVersion = CurrentVersion;
    }

    /// <summary>A first-run config: Stick FX is the default profile of controller 1. Existing configs keep their active
    /// profile and edited layers; Stick FX is one switch away in Settings.</summary>
    public static AppConfig Fresh()
    {
        var c = new AppConfig { ConfigVersion = CurrentVersion, ControllerProfileName = "Stick FX" };
        c.ApplyPreset("Stick FX");
        c.ControllerProfiles["Stick FX"] = c.CaptureProfile();
        return c;
    }

    private static List<BankState> NormalizeBanks(List<BankState>? banks)
    {
        banks ??= new();
        while (banks.Count < 4) banks.Add(new BankState { Mode = PadBanks.DefaultMode(banks.Count) });
        if (banks.Count > 4) banks = banks.Take(4).ToList();
        foreach (var b in banks)
        {
            if (!Enum.IsDefined(b.Mode)) b.Mode = BankMode.Sample;
            if (b.Degrees is not { Length: 4 }) b.Degrees = new[] { 0, 3, 4, 5 };
            if (b.AltDegrees is not { Length: 4 }) b.AltDegrees = new[] { 1, 2, 6, 7 };
            for (int i = 0; i < 4; i++) { b.Degrees[i] = Math.Clamp(b.Degrees[i], 0, 13); b.AltDegrees[i] = Math.Clamp(b.AltDegrees[i], 0, 13); }
        }
        return banks;
    }

    public MixDock.Platform.PadMapper CreatePadMapper()
        => new(GamepadButtons, ShiftButtons, ShiftButton, ShiftChord, PanicChord, PanicHoldMs, profileShortcuts: true);

    public sealed record ControllerProfile(Dictionary<string, string> Axes, Dictionary<string, string> ShiftAxes,
        Dictionary<string, string> Buttons, Dictionary<string, string> ShiftButtons, string ShiftButton, string ShiftChord,
        string PanicChord, int PanicHoldMs);

    private static readonly string[] FactoryProfiles = ["Default", "Classic", "Live DJ", "Pad performer", "Stick FX", "FX Pad", "GameSir G7 Pro"];
    public string ControllerProfileName { get; set; } = "Default";
    /// <summary>Profile of controller 2 (the FX pad). Its bindings live in <see cref="ControllerProfiles"/> (or the factory preset);
    /// they never touch the top-level layers, which always belong to controller 1.</summary>
    public string FxPadProfileName { get; set; } = "FX Pad";
    /// <summary>Controller identity ("VID:PID", hex) that becomes the FX pad whenever two controllers are connected; empty = by slot.
    /// The default is the Xbox Wireless Controller over Bluetooth.</summary>
    public string FxPadDevice { get; set; } = "045E:0B13";
    /// <summary>Controller identity that is always controller 1 (the mixer) when two are connected; empty = any.</summary>
    public string MixerDevice { get; set; } = "";
    /// <summary>Stick FX set of controller 1 and of the FX pad (persisted), 0-based.</summary>
    public int FxSet { get; set; }
    public int FxPadSet { get; set; } = 1;

    /// <summary>Bindings of any profile without switching to it: the live top-level layers for the active profile, the stored
    /// copy for a saved one, the factory preset otherwise.</summary>
    public ControllerProfile ProfileFor(string name)
    {
        if (string.Equals(name, ControllerProfileName, StringComparison.OrdinalIgnoreCase)) return CaptureProfile();
        if (ControllerProfiles.TryGetValue(name, out var stored)) return stored;
        var stock = new AppConfig(); stock.ApplyPreset(ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : "FX Pad");
        return stock.CaptureProfile();
    }

    /// <summary>A pad mapper for the FX pad (controller 2) from its own profile.</summary>
    public PadMapper CreateFxPadMapper()
    {
        var p = ProfileFor(FxPadProfileName);
        return new(p.Buttons, p.ShiftButtons, p.ShiftButton, p.ShiftChord, p.PanicChord, p.PanicHoldMs, profileShortcuts: true);
    }

    /// <summary>A working copy whose active (top-level) profile is the FX pad's, for the mapping editor.</summary>
    public AppConfig FxPadEditCopy()
    {
        var copy = new AppConfig();
        copy.ControllerProfiles = new(ControllerProfiles, StringComparer.OrdinalIgnoreCase);
        copy.ControllerProfiles[ControllerProfileName] = CaptureProfile();
        copy.ControllerProfileName = FxPadProfileName;
        copy.LoadProfile(ProfileFor(FxPadProfileName));
        return copy;
    }

    /// <summary>Takes the FX pad's profile back from an edited <see cref="FxPadEditCopy"/>; controller 1's layers stay as they are.</summary>
    public void CommitFxPadEdit(AppConfig edited)
    {
        var mine = CaptureProfile();
        foreach (var (name, profile) in edited.ControllerProfiles)
            if (!string.Equals(name, ControllerProfileName, StringComparison.OrdinalIgnoreCase)) ControllerProfiles[name] = profile;
        ControllerProfiles[edited.ControllerProfileName] = edited.CaptureProfile();
        FxPadProfileName = edited.ControllerProfileName;
        LoadProfile(mine);
        ControllerProfiles[ControllerProfileName] = mine;
    }

    /// <summary>Next / previous profile for the FX pad (View + LB / RB tap on controller 2).</summary>
    public string CycleFxPadProfile(int step)
    {
        var names = ProfileNames.ToList();
        int i = Math.Max(0, names.FindIndex(n => string.Equals(n, FxPadProfileName, StringComparison.OrdinalIgnoreCase)));
        FxPadProfileName = names[((i + step) % names.Count + names.Count) % names.Count];
        return FxPadProfileName;
    }
    public Dictionary<string, ControllerProfile> ControllerProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore]
    public IEnumerable<string> ProfileNames => FactoryProfiles.Concat(ControllerProfiles.Keys
        .Where(name => !IsFactoryProfile(name)).Order(StringComparer.OrdinalIgnoreCase));
    [JsonIgnore]
    public bool CanModifyProfileName => !IsFactoryProfile(ControllerProfileName);

    private static bool IsFactoryProfile(string name) => FactoryProfiles.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static bool IsProfileName(string? name) => name is { Length: > 0 and <= 32 } && !name.Any(char.IsControl);

    private ControllerProfile CaptureProfile() => new(new(GamepadAxes), new(ShiftAxes), new(GamepadButtons), new(ShiftButtons),
        ShiftButton, ShiftChord, PanicChord, PanicHoldMs);

    private void LoadProfile(ControllerProfile profile)
    {
        GamepadAxes = new(profile.Axes);
        ShiftAxes = new(profile.ShiftAxes);
        GamepadButtons = new(profile.Buttons);
        ShiftButtons = new(profile.ShiftButtons);
        ShiftButton = profile.ShiftButton;
        ShiftChord = profile.ShiftChord;
        PanicChord = profile.PanicChord;
        PanicHoldMs = profile.PanicHoldMs;
    }

    private void NormalizeControllerProfiles()
    {
        var normalized = new Dictionary<string, ControllerProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, profile) in ControllerProfiles ?? [])
        {
            if (!IsProfileName(name) || profile is not { Axes: not null, ShiftAxes: not null, Buttons: not null,
                    ShiftButtons: not null, ShiftButton: not null, ShiftChord: not null, PanicChord: not null }) continue;
            normalized.TryAdd(name, profile);
        }
        ControllerProfiles = normalized;
        if (ConfigVersion < 8 || !ProfileNames.Contains(ControllerProfileName, StringComparer.OrdinalIgnoreCase))
        {
            var stock = new AppConfig();
            ControllerProfileName = "Imported";
            foreach (var name in FactoryProfiles)
            {
                stock.ApplyPreset(name);
                if (Same(GamepadAxes, stock.GamepadAxes) && Same(ShiftAxes, stock.ShiftAxes) &&
                    Same(GamepadButtons, stock.GamepadButtons) && Same(ShiftButtons, stock.ShiftButtons) &&
                    ShiftButton == stock.ShiftButton && ShiftChord == stock.ShiftChord &&
                    PanicChord == stock.PanicChord && PanicHoldMs == stock.PanicHoldMs)
                {
                    ControllerProfileName = name;
                    break;
                }
            }
        }
        ControllerProfiles[ControllerProfileName] = CaptureProfile();
    }

    public bool SwitchProfile(string name)
    {
        var canonical = ProfileNames.FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (canonical == null || canonical == ControllerProfileName) return false;
        ControllerProfiles[ControllerProfileName] = CaptureProfile();
        ControllerProfileName = canonical;
        if (ControllerProfiles.TryGetValue(canonical, out var profile)) LoadProfile(profile);
        else ApplyPreset(canonical);
        return true;
    }

    public bool AddProfile(string name, bool duplicate)
    {
        name = name.Trim();
        if (!IsProfileName(name) || ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        ControllerProfiles[ControllerProfileName] = CaptureProfile();
        ControllerProfileName = name;
        if (!duplicate) ApplyPreset("Default");
        ControllerProfiles[name] = CaptureProfile();
        return true;
    }

    public bool RenameProfile(string name)
    {
        name = name.Trim();
        if (!CanModifyProfileName || !IsProfileName(name) || ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        ControllerProfiles.Remove(ControllerProfileName);
        ControllerProfileName = name;
        ControllerProfiles[name] = CaptureProfile();
        return true;
    }

    public bool DeleteProfile()
    {
        if (!CanModifyProfileName) return false;
        string deleted = ControllerProfileName;
        SwitchProfile("Default");
        ControllerProfiles.Remove(deleted);
        return true;
    }

    public void ResetProfile() => ApplyPreset("Default");

    internal void CopyProfilesFrom(AppConfig source)
    {
        ControllerProfileName = source.ControllerProfileName;
        ControllerProfiles = new(source.ControllerProfiles, StringComparer.OrdinalIgnoreCase);
        LoadProfile(source.CaptureProfile());
    }

    public static string DirectoryPath { get; } = DataFolder.Resolve(Environment.SpecialFolder.ApplicationData);
    public static string FilePath => Path.Combine(DirectoryPath, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Fresh();
            return FromJson(File.ReadAllText(FilePath));
        }
        catch { return new AppConfig { ConfigVersion = CurrentVersion }; }
    }
    public static AppConfig FromJson(string json)
    {
        var c = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        if (c.ConfigVersion <= 5)
        {
            // a layer missing from an older file meant "that version's default", which was the 1.3 pad layout, not today's Live DJ one
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool Has(string n) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Object;
                if (!Has(nameof(GamepadAxes))) c.GamepadAxes = c.ConfigVersion == 5 ? ClassicAxes() : PadAxes();
                if (!Has(nameof(ShiftAxes))) c.ShiftAxes = c.ConfigVersion == 5 ? DefaultShiftAxes() : PadShiftAxes();
                if (!Has(nameof(GamepadButtons))) c.GamepadButtons = c.ConfigVersion == 5 ? V5Buttons() : PadButtons();
                if (!Has(nameof(ShiftButtons))) c.ShiftButtons = c.ConfigVersion == 5 ? V5ShiftButtons() : PadShiftButtons();
            }
            catch (JsonException) { }
        }
        c.Migrate();
        return c;
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, true);
    }
}
