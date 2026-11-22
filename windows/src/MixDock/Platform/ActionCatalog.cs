namespace MixDock.Platform;

/// <summary>Global = one target; Deck = runs on the focused deck, or a fixed deck (A./B. prefix).</summary>
public enum ActionScope { Global, Deck }

/// <summary>One bindable action. Id is the config string without the deck prefix. Hold = has a press and a release (momentary).</summary>
public sealed record ActionDef(string Id, string Label, ActionScope Scope, bool Hold = false);

/// <summary>
/// Every action the controller can actually run, as handled by MainForm.Dispatch / ApplyAction / ProcessAxes.
/// The bindings editor offers only these, and startup warns about bindings that are not in here (so nothing is a silent no-op).
/// </summary>
public static class ActionCatalog
{
    public static readonly IReadOnlyList<ActionDef> Buttons = new ActionDef[]
    {
        // mixing moves that work on live apps (Spotify, browser) and on song files
        new("BassCut", "Bass cut (−24 dB)", ActionScope.Deck),
        new("LowKill", "Bass kill (on/off)", ActionScope.Deck),
        new("MidKill", "Mid kill (on/off)", ActionScope.Deck),
        new("HighKill", "High kill (on/off)", ActionScope.Deck),
        new("EqReset", "EQ + kills reset", ActionScope.Deck),
        new("FilterReset", "Filter reset to off", ActionScope.Deck),
        new("FilterFineReset", "Filter: centre the fine latch", ActionScope.Deck),
        new("Filter-", "Filter step to low-pass", ActionScope.Deck),
        new("Filter+", "Filter step to high-pass", ActionScope.Deck),
        new("Low+", "Bass EQ +", ActionScope.Deck), new("Low-", "Bass EQ −", ActionScope.Deck),
        new("Mid+", "Mid EQ +", ActionScope.Deck), new("Mid-", "Mid EQ −", ActionScope.Deck),
        new("High+", "High EQ +", ActionScope.Deck), new("High-", "High EQ −", ActionScope.Deck),
        new("Trim+", "Trim (gain) +", ActionScope.Deck), new("Trim-", "Trim (gain) −", ActionScope.Deck),
        new("Mute", "Deck mute (on/off)", ActionScope.Deck),
        new("Loop", "Slip loop (on/off)", ActionScope.Deck),
        new("LoopDouble", "Loop size ×2", ActionScope.Deck),
        new("LoopHalve", "Loop size ÷2", ActionScope.Deck),
        new("Roll:1/16", "Loop roll 1/16 (hold)", ActionScope.Deck, true),
        new("Roll:1/8", "Loop roll 1/8 (hold)", ActionScope.Deck, true),
        new("Roll:1/4", "Loop roll 1/4 (hold)", ActionScope.Deck, true),
        new("Roll:1/2", "Loop roll 1/2 (hold)", ActionScope.Deck, true),
        new("Roll:1", "Loop roll 1 beat (hold)", ActionScope.Deck, true),
        new("EchoThrow", "Echo throw (hold)", ActionScope.Deck, true),
        new("EchoFreeze", "Echo freeze (on/off)", ActionScope.Deck),
        new("Reverse", "Reverse (hold)", ActionScope.Deck, true),
        new("TimeBend", "Brake to stop (hold)", ActionScope.Deck, true),
        new("SpinBack", "Spin-back", ActionScope.Deck),
        new("Sync", "Sync to the other deck", ActionScope.Deck),
        new("TapBpm", "Tap BPM", ActionScope.Deck),
        new("Cue", "Headphone cue (on/off)", ActionScope.Deck),
        // song files only
        new("PlayPause", "Play / pause (files; apps via media keys or freeze)", ActionScope.Deck),
        new("BeatJumpBack", "Beat jump back (song files)", ActionScope.Deck),
        new("BeatJumpFwd", "Beat jump forward (song files)", ActionScope.Deck),
        new("BrowsePrev", "Source list: previous", ActionScope.Deck),
        new("BrowseNext", "Source list: next", ActionScope.Deck),
        new("Load", "Load the selected source", ActionScope.Deck),
        // pads
        new("Pad:1", "Pad 1 (empty pad captures the last beat)", ActionScope.Deck, true),
        new("Pad:2", "Pad 2 (empty pad captures the last beat)", ActionScope.Deck, true),
        new("Pad:3", "Pad 3 (empty pad captures the last beat)", ActionScope.Deck, true),
        new("Pad:4", "Pad 4 (empty pad captures the last beat)", ActionScope.Deck, true),
        new("PadCapture:1", "Pad 1 re-capture / alt chord", ActionScope.Deck, true),
        new("PadCapture:2", "Pad 2 re-capture / alt chord", ActionScope.Deck, true),
        new("PadCapture:3", "Pad 3 re-capture / alt chord", ActionScope.Deck, true),
        new("PadCapture:4", "Pad 4 re-capture / alt chord", ActionScope.Deck, true),
        // global
        new("FocusA", "Focus deck A", ActionScope.Global),
        new("FocusB", "Focus deck B", ActionScope.Global),
        new("FocusToggle", "Focus: swap A ↔ B", ActionScope.Global),
        new("Crossfader-", "Crossfader toward A", ActionScope.Global),
        new("Crossfader+", "Crossfader toward B", ActionScope.Global),
        new("CrossfaderReset", "Crossfader centre", ActionScope.Global),
        new("CrossfaderA", "Crossfader cut to A", ActionScope.Global),
        new("CrossfaderB", "Crossfader cut to B", ActionScope.Global),
        new("Master+", "Master volume +", ActionScope.Global),
        new("Master-", "Master volume −", ActionScope.Global),
        new("MasterMute", "Kill / restore all audio", ActionScope.Global),
        new("Riser", "Noise riser (hold)", ActionScope.Global, true),
        new("FxCycle", "Right-stick FX: next effect", ActionScope.Global),
        new("FxFreeze", "Freeze reverb tail (on/off)", ActionScope.Global),
        new("AutoMix", "Auto mix (on/off)", ActionScope.Global),
        new("AutoMix.Now", "Auto mix: mix now", ActionScope.Global),
        new("AutoMix.Skip", "Auto mix: next style", ActionScope.Global),
        new("PauseAll", "Pause / resume all decks", ActionScope.Global),
        new("BankNext", "Next pad bank", ActionScope.Global), new("BankPrev", "Previous pad bank", ActionScope.Global),
        new("Bank:1", "Pad bank 1", ActionScope.Global), new("Bank:2", "Pad bank 2", ActionScope.Global),
        new("Bank:3", "Pad bank 3", ActionScope.Global), new("Bank:4", "Pad bank 4", ActionScope.Global),
        new("BankMode", "Pad bank mode: next", ActionScope.Global),
        new("QuantCycle", "Pad quantize: next", ActionScope.Global),
        new("PadGrid", "Pad grid window", ActionScope.Global),
        new("PadOverlay", "Controller cheat sheet", ActionScope.Global),
        // stick FX sets (R3): sets of four effects on a stick, two more on the left stick
        new("SetNext", "FX set: next", ActionScope.Global),
        new("SetPrev", "FX set: previous", ActionScope.Global),
        new("FxClear", "FX: let go of every stick effect", ActionScope.Global),
        new("FxTargetNext", "FX pad target: next (focus · A · B · both)", ActionScope.Global),
        new("FxTargetPrev", "FX pad target: previous", ActionScope.Global),
        new("SwapControllers", "Swap controller 1 and 2 (mixer ↔ FX pad)", ActionScope.Global),
        new("Stutter:1/4", "Stutter 1/4 (hold)", ActionScope.Deck, true),
        new("Stutter:1/8", "Stutter 1/8 (hold)", ActionScope.Deck, true),
        new("Stutter:1/16", "Stutter 1/16 (hold)", ActionScope.Deck, true),
        new("Stutter:1/32", "Stutter 1/32 (hold)", ActionScope.Deck, true),
        new("Transformer:1/16", "Transformer gate 1/16 (hold)", ActionScope.Deck, true),
        new("Transformer:1/32", "Transformer gate 1/32 (hold)", ActionScope.Deck, true),
        new("Cut", "Cut: silence while held", ActionScope.Deck, true),
        new("GrainFreeze", "Freeze: hold the sound in the air (hold)", ActionScope.Deck, true),
        new("SubDrop", "Sub drop hit", ActionScope.Deck),
    };

    public static readonly IReadOnlyList<ActionDef> Axes = new ActionDef[]
    {
        new("Filter", "Filter sweep (springs back)", ActionScope.Deck),
        new("FilterFine", "Filter fine (stays where you leave it)", ActionScope.Deck),
        new("Crossfader", "Crossfader", ActionScope.Global),
        new("FxX", "FX pad X (amount + size/rate)", ActionScope.Deck),
        new("FxY", "FX pad Y (amount + reverb↔delay)", ActionScope.Deck),
        new("FxBlend", "FX blend: right = echo, left = reverb (depth = stick distance)", ActionScope.Deck),
        new("EchoThrow", "Echo throw (amount)", ActionScope.Deck),
        new("EchoSend", "Echo send ↑ / wash ↓", ActionScope.Deck),
        new("BeatRoll", "Beat roll 1/4 … 1/32 by depth", ActionScope.Deck),
        new("TimeBend", "Time bend: half = slow, full = stop", ActionScope.Deck),
        new("Reverse", "Reverse while pressed", ActionScope.Deck),
        new("SpinBack", "Spin-back when pressed", ActionScope.Deck),
        new("Riser", "Noise riser (amount)", ActionScope.Global),
        new("Master", "Master volume", ActionScope.Global),
        new("Low", "Bass EQ", ActionScope.Deck), new("Mid", "Mid EQ", ActionScope.Deck), new("High", "High EQ", ActionScope.Deck),
        new("Trim", "Trim (gain)", ActionScope.Deck),
        new("SetPadX", "FX set pad ↔ (4 effects of the set: direction picks, distance = amount)", ActionScope.Deck),
        new("SetPadY", "FX set pad ↕ (4 effects of the set)", ActionScope.Deck),
        new("SetPad2X", "Second FX set pad ↔ (the next set)", ActionScope.Deck),
        new("SetPad2Y", "Second FX set pad ↕ (the next set)", ActionScope.Deck),
        new("SetSide", "FX set ← → (the set's two left-stick effects)", ActionScope.Deck),
    };

    public static IReadOnlyList<ActionDef> For(bool axis) => axis ? Axes : Buttons;

    /// <summary>deck: 0 = focused deck, 1 = deck A, 2 = deck B.</summary>
    public static string Compose(ActionDef def, int deck)
        => def.Scope == ActionScope.Global ? def.Id : (deck switch { 1 => "A.", 2 => "B.", _ => "Focus." }) + def.Id;

    /// <summary>Config string -> definition + deck (0 focus, 1 A, 2 B). def is null when the action is unknown.</summary>
    public static (ActionDef? Def, int Deck) Parse(string? action, bool axis)
    {
        if (string.IsNullOrWhiteSpace(action)) return (null, 0);
        string id = action; int deck = 0;
        if (id.StartsWith("Focus.")) id = id[6..];
        else if (id.Length > 2 && id[1] == '.' && id[0] is 'A' or 'B') { deck = id[0] == 'A' ? 1 : 2; id = id[2..]; }
        foreach (var d in For(axis))
            if (d.Id == id)
                return d.Scope == ActionScope.Global && action != id ? (null, 0) : (d, deck);
        return (null, 0);
    }

    public static bool IsKnown(string? action, bool axis) => Parse(action, axis).Def != null;

    public static string Label(string? action, bool axis)
    {
        var (def, deck) = Parse(action, axis);
        if (def is null) return string.IsNullOrWhiteSpace(action) ? "—" : action!;
        return def.Scope == ActionScope.Global || deck == 0 ? def.Label : $"{(deck == 1 ? "A" : "B")}: {def.Label}";
    }

    /// <summary>Bindings that the app cannot run, as "layer input = action" lines.</summary>
    public static List<string> Unknown(IReadOnlyDictionary<string, string> buttons, IReadOnlyDictionary<string, string> shiftButtons,
        IReadOnlyDictionary<string, string> axes, IReadOnlyDictionary<string, string> shiftAxes)
    {
        var bad = new List<string>();
        void Check(IReadOnlyDictionary<string, string> map, string layer, bool axis)
        { foreach (var (k, v) in map) if (!string.IsNullOrWhiteSpace(v) && !IsKnown(v, axis)) bad.Add($"{layer}{k} = {v}"); }
        Check(buttons, "", false); Check(shiftButtons, "Shift+", false); Check(axes, "", true); Check(shiftAxes, "Shift+", true);
        return bad;
    }
}
