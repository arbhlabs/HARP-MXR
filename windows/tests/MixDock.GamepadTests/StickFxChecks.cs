using System.Text.Json;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;

/// <summary>R3 / R6 / R7 controller checks: the new profiles, migration of a real edited layout, two-controller roles and
/// routing, extra (paddle) buttons, the FX pad mapper and the stick FX rig.</summary>
internal static class StickFxChecks
{
    public static void Run(Action<bool, string> check)
    {
        Profiles(check);
        Migration(check);
        Roles(check);
        Hub(check);
        Extras(check);
        FxPadMapper(check);
        Rig(check);
    }

    private static void Profiles(Action<bool, string> check)
    {
        var c = AppConfig.FromJson("{}");
        var names = c.ProfileNames.ToList();
        check(names.Take(3).SequenceEqual(["Default", "Classic", "Live DJ"]) && names.Contains("Stick FX") && names.Contains("FX Pad") && names.Contains("GameSir G7 Pro"),
            "factory profiles: Stick FX, FX Pad and GameSir G7 Pro join the existing four (order of the first ones unchanged)");
        check(c.ControllerProfileName == "Default" && c.GamepadAxes["LeftThumbY"] == "Focus.Filter", "an existing (empty) config file keeps Default (filter on the left stick)");
        var fresh = AppConfig.Fresh();
        check(fresh.ControllerProfileName == "Stick FX" && fresh.GamepadAxes["LeftThumbY"] == "Crossfader" && fresh.GamepadAxes["RightThumbX"] == "Focus.SetPadX"
            && fresh.ConfigVersion == AppConfig.CurrentVersion, "a first run (no config file) starts on Stick FX");
        var freshRound = AppConfig.FromJson(JsonSerializer.Serialize(fresh));
        check(freshRound.ControllerProfileName == "Stick FX" && freshRound.GamepadAxes["LeftThumbY"] == "Crossfader", "the first-run Stick FX default survives save and load");
        check(c.SwitchProfile("Stick FX"), "Stick FX can be selected");
        check(c.GamepadAxes["LeftThumbY"] == "Crossfader" && c.GamepadAxes["LeftThumbX"] == "Focus.SetSide", "Stick FX: left stick ↕ = crossfader (rate: stays where you leave it), ↔ = the set's two side effects");
        check(c.GamepadAxes["RightThumbX"] == "Focus.SetPadX" && c.GamepadAxes["RightThumbY"] == "Focus.SetPadY", "Stick FX: right stick = the 4-direction set pad");
        check(c.GamepadButtons["LeftThumb"] == "SetPrev" && c.GamepadButtons["RightThumb"] == "SetNext", "Stick FX: L3 / R3 step the set back / forward");
        check(c.ShiftAxes["LeftThumbY"] == "Focus.Filter" && c.GamepadButtons["A"] == "Focus.BassCut" && c.GamepadButtons["Left"] == "Crossfader-",
            "Stick FX keeps the filter (Shift + left stick ↕), the −24 dB bass cut on A and the D-pad crossfader nudges");
        check(c.SwitchProfile("GameSir G7 Pro") && c.ShiftButton == "Back,Extra1" && c.GamepadButtons["Extra2"] == "SetNext" &&
              c.GamepadButtons["Extra3"] == "Focus.PlayPause" && c.GamepadButtons["Extra4"] == "Focus.Sync" && c.GamepadAxes["RightThumbX"] == "Focus.SetPadX",
            "GameSir G7 Pro: Stick FX + paddle L4 = Shift (View still works), R4 = next set, L5 = pause focus deck, R5 = sync");
        var p = c.ProfileFor("FX Pad");
        check(p.Axes["LeftThumbX"] == "Focus.SetPadX" && p.Axes["RightThumbX"] == "Focus.SetPad2X" && p.Axes["LeftTrigger"] == "Focus.EchoThrow" && p.Axes["RightTrigger"] == "Riser",
            "FX Pad: both sticks are set pads (this set and the next), LT echo throw, RT riser build");
        check(p.Buttons["A"] == "Focus.Stutter:1/8" && p.Buttons["LeftShoulder"] == "Focus.Cut" && p.Buttons["RightShoulder"] == "Focus.SubDrop" && p.Buttons["Up"] == "SetNext" && p.Buttons["Right"] == "FxTargetNext",
            "FX Pad: clicky buttons are momentary hits, the D-pad picks set and target");
        check(p.ShiftAxes["LeftTrigger"] == "Focus.TimeBend" && p.ShiftChord == "", "FX Pad: Shift + LT = brake depth; no LB+RB shift chord (both bumpers are hits)");
        foreach (var name in AppConfig.Presets)
        {
            var s = new AppConfig(); s.ApplyPreset(name);
            check(ActionCatalog.Unknown(s.GamepadButtons, s.ShiftButtons, s.GamepadAxes, s.ShiftAxes).Count == 0, $"preset {name}: every binding is a real action");
        }
        var cfg = AppConfig.FromJson("{}");
        check(cfg.FxPadProfileName == "FX Pad" && cfg.FxPadDevice == "045E:0B13", "controller 2 defaults to the FX Pad profile; the Bluetooth Xbox pad (045E:0B13) is the FX pad");
        var fxMapper = cfg.CreateFxPadMapper();
        check(fxMapper.ActionFor(PadButton.A, false) == "Focus.Stutter:1/8" && cfg.GamepadButtons["A"] == "Focus.LowKill", "the FX pad mapper uses its own profile; controller 1 keeps its own");
        var copy = cfg.FxPadEditCopy();
        copy.GamepadButtons["A"] = "Focus.Stutter:1/4";
        cfg.CommitFxPadEdit(copy);
        check(cfg.ProfileFor("FX Pad").Buttons["A"] == "Focus.Stutter:1/4" && cfg.GamepadButtons["A"] == "Focus.LowKill" && cfg.ControllerProfileName == "Default",
            "editing controller 2 changes only the FX pad profile, never controller 1's bindings");
        var round = AppConfig.FromJson(JsonSerializer.Serialize(cfg));
        check(round.ProfileFor(round.FxPadProfileName).Buttons["A"] == "Focus.Stutter:1/4" && round.GamepadButtons["A"] == "Focus.LowKill", "the FX pad's edited profile survives JSON");
        check(cfg.CycleFxPadProfile(1) != "FX Pad" && cfg.ControllerProfileName == "Default", "View + RB on controller 2 cycles only controller 2's profile");
    }

    /// <summary>A legacy config without a profile name, using a custom L3 binding.</summary>
    private static void Migration(Action<bool, string> check)
    {
        const string json = """
        {"ConfigVersion":7,
         "GamepadAxes":{"LeftThumbY":"Focus.Filter","LeftThumbX":"Focus.FxBlend","RightThumbX":"Focus.FxX","RightThumbY":"Focus.FxY","LeftTrigger":"Focus.EchoThrow","RightTrigger":"Focus.BeatRoll"},
         "GamepadButtons":{"A":"Focus.BassCut","B":"Focus.Loop","X":"Focus.Roll:1/4","Y":"Focus.Roll:1/8","LeftShoulder":"FocusA","RightShoulder":"FocusB","Up":"Focus.LoopDouble","Down":"Focus.LoopHalve","Left":"Crossfader-","Right":"Crossfader+","LeftThumb":"Focus.TimeBend","RightThumb":"FxCycle","Back":"Focus.TapBpm","Start":"Focus.Sync"},
         "ShiftButtons":{"A":"Focus.Pad:1","B":"Focus.Pad:2","X":"Focus.Pad:3","Y":"Focus.Pad:4","LeftShoulder":"QuantCycle","RightShoulder":"CrossfaderReset","Up":"BankNext","Right":"Focus.HighKill","Down":"BankPrev","Left":"Focus.MidKill","LeftThumb":"Focus.EqReset","RightThumb":"FxFreeze","Back":"BankMode","Start":"Focus.PlayPause"},
         "ShiftAxes":{"LeftThumbX":"Focus.FilterFine","LeftTrigger":"Focus.TimeBend","RightTrigger":"Focus.Reverse"}}
        """;
        using var doc = JsonDocument.Parse(json);
        var c = AppConfig.FromJson(json);
        bool Same(Dictionary<string, string> got, string section)
        {
            var want = doc.RootElement.GetProperty(section).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            return got.Count == want.Count && want.All(kv => got.TryGetValue(kv.Key, out var v) && v == kv.Value);
        }
        check(Same(c.GamepadAxes, "GamepadAxes") && Same(c.GamepadButtons, "GamepadButtons") && Same(c.ShiftButtons, "ShiftButtons") && Same(c.ShiftAxes, "ShiftAxes"),
            "an edited 1.4 layout (L3 = brake) migrates with every binding preserved");
        check(c.ControllerProfileName == "Imported" && c.ShiftButton == "Back" && c.ShiftChord == "LeftShoulder+RightShoulder", "the imported profile stays active, modifiers unchanged");
        check(c.ProfileNames.Contains("Stick FX") && c.FxPadProfileName == "FX Pad", "the new Stick FX and FX Pad profiles are offered next to it, not forced");
        var again = AppConfig.FromJson(JsonSerializer.Serialize(c));
        check(again.GamepadButtons["LeftThumb"] == "Focus.TimeBend" && again.ControllerProfileName == "Imported", "saving and loading again changes nothing");
    }

    private static void Roles(Action<bool, string> check)
    {
        const string g7 = "3537:109B", xbox = "045E:0B13";
        int[] none = [-1, -1, -1, -1];
        int[] R(bool[] on, string?[] ids, int[] prev, bool arrived = true, string fx = xbox, string mix = "") => ControllerRoles.Assign(on, ids, prev, fx, mix, arrived);
        var one = R([true, false, false, false], [g7, null, null, null], none);
        check(one[0] == (int)PadRole.Mixer && one.Count(r => r >= 0) == 1, "one pad: it is controller 1 (mixer)");
        var both = R([true, true, false, false], [g7, xbox, null, null], none);
        check(both[0] == (int)PadRole.Mixer && both[1] == (int)PadRole.FxPad, "G7 Pro + Bluetooth Xbox: G7 mixes, Xbox is the FX pad");
        var swapped = R([true, true, false, false], [xbox, g7, null, null], none);
        check(swapped[0] == (int)PadRole.FxPad && swapped[1] == (int)PadRole.Mixer, "by identity, not by slot: Xbox in slot 1 is still the FX pad");
        var xboxAlone = R([false, true, false, false], [null, xbox, null, null], none);
        check(xboxAlone[1] == (int)PadRole.Mixer, "the Xbox pad alone drives the mixer");
        var g7Arrives = R([true, true, false, false], [g7, xbox, null, null], xboxAlone);
        check(g7Arrives[0] == (int)PadRole.Mixer && g7Arrives[1] == (int)PadRole.FxPad, "when the G7 Pro arrives, identities decide: G7 mixer, Xbox FX pad");
        var unplugMixer = R([false, true, false, false], [g7, xbox, null, null], both, arrived: false);
        check(unplugMixer[1] == (int)PadRole.FxPad && unplugMixer[0] == -1, "unplugging the mixer never re-routes the FX pad");
        var unplugFx = R([true, false, false, false], [g7, xbox, null, null], both, arrived: false);
        check(unplugFx[0] == (int)PadRole.Mixer && unplugFx[1] == -1, "unplugging the FX pad leaves the mixer alone");
        var twins = R([true, true, false, false], ["3537:100A", "3537:100A", null, null], none);
        check(twins[0] == (int)PadRole.Mixer && twins[1] == (int)PadRole.FxPad, "two identical pads: by slot (lower slot mixes)");
        var pref = R([true, true, false, false], ["3537:100A", "1234:0001", null, null], none, fx: "", mix: "1234:0001");
        check(pref[1] == (int)PadRole.Mixer && pref[0] == (int)PadRole.FxPad, "a remembered mixer identity wins");
        var three = R([true, true, true, false], [g7, xbox, "054C:0CE6", null], both);
        check(three[0] == (int)PadRole.Mixer && three[1] == (int)PadRole.FxPad && three[2] == -1, "a third pad gets no role and changes nothing");
    }

    private static void Hub(Action<bool, string> check)
    {
        var connected = new[] { true, true, false, false };
        var states = new XInputState[4];
        states[0].Gamepad.Buttons = PadButton.A; states[1].Gamepad.Buttons = PadButton.B;
        states[1].Gamepad.LeftThumbX = 32767;
        Gamepad.ResetForTests();
        Gamepad.StateSource = slot => (connected[slot], states[slot]);
        Gamepad.IdentitySource = slot => slot == 0 ? "3537:109B" : "045E:0B13";
        Gamepad.ExtraSource = (slot, id, xi) => slot == 0 ? PadButton.Extra2 : 0;
        Gamepad.FxPadDevice = "045E:0B13"; Gamepad.MixerDevice = "";
        Gamepad.Poll(force: true);
        bool m = Gamepad.ReadFrame(PadRole.Mixer, out var mf, out _), f = Gamepad.ReadFrame(PadRole.FxPad, out var ff, out _);
        check(m && f && mf.Buttons == (PadButton.A | PadButton.Extra2) && ff.Buttons == PadButton.B && ff.LX > .99,
            "two controllers read at once: each role gets its own pad's buttons and sticks (extra buttons merged in)");
        check(Gamepad.Read(out var legacy) && legacy.Gamepad.Buttons == PadButton.A, "Gamepad.Read() is still controller 1");
        connected[0] = false; Gamepad.Poll(force: true);
        check(!Gamepad.Read(PadRole.Mixer, out _) && Gamepad.ReadFrame(PadRole.FxPad, out var still, out _) && still.Buttons == PadButton.B,
            "controller 1 unplugged: the FX pad keeps working as the FX pad");
        connected[0] = true; Gamepad.Poll(force: true);
        check(Gamepad.SlotOf(PadRole.Mixer) == 0 && Gamepad.SlotOf(PadRole.FxPad) == 1, "plugged back in: roles as before");
        check(Gamepad.SwapRoles() && Gamepad.SlotOf(PadRole.Mixer) == 1 && Gamepad.FxPadDevice == "3537:109B" && Gamepad.MixerDevice == "045E:0B13",
            "swap: roles exchange and the identities are remembered");
        Gamepad.ResetForTests();
        Gamepad.FxPadDevice = "045E:0B13"; Gamepad.MixerDevice = ""; Gamepad.ExtraSource = null;
        Gamepad.StateSource = _ => (false, default);
    }

    private static void Extras(Action<bool, string> check)
    {
        var raw = new bool[14]; var prev = new bool[14]; var aliases = new HashSet<int>(); var confirmed = new HashSet<int>();
        int pending = -1; PadButton lastXi = 0;
        PadButton Step(PadButton xi) => ExtraButtonLogic.Classify(raw, prev, aliases, confirmed, ref pending, ref lastXi, xi);
        raw[10] = true;
        var first = Step(0); var second = Step(0);
        check(first == 0 && second == PadButton.Extra1, "a raw button beyond the standard ten becomes Extra1 (held back one poll on its very first press)");
        raw[10] = false; Step(0); raw[10] = true;
        check(Step(0) == PadButton.Extra1, "after that it fires at once");
        raw[10] = false; Step(0);
        raw[11] = true;
        var dup = Step(PadButton.A);
        check(dup == 0 && aliases.Contains(11) && Step(PadButton.A) == 0, "a raw button that moves together with an XInput button (a firmware-mapped paddle) never fires twice");
        raw[11] = false; Step(0); raw[12] = true; Step(0);
        check(Step(PadButton.B) == 0 && aliases.Contains(12), "…also when XInput reports the press one poll later");
        var map = new PadMapper(new Dictionary<string, string> { ["Extra2"] = "SetNext", ["A"] = "Focus.LowKill" },
            new Dictionary<string, string> { ["A"] = "Focus.Pad:1" }, "Back,Extra1", "LeftShoulder+RightShoulder");
        var ev = map.Update(PadButton.Extra2, 100);
        check(ev.Count == 1 && ev[0].Action == "SetNext", "a paddle bound as Extra2 fires its action");
        map.Update(0, 200);
        map.Update(PadButton.Extra1, 300); var shifted = map.Update(PadButton.Extra1 | PadButton.A, 400);
        check(shifted.Count == 1 && shifted[0].Action == "Focus.Pad:1" && shifted[0].Shift, "a paddle as the Shift button (\"Back,Extra1\") gives the shift layer");
        map.Update(0, 500); map.Update(PadButton.Back, 600); var viaView = map.Update(PadButton.Back | PadButton.A, 700);
        check(viaView.Count == 1 && viaView[0].Action == "Focus.Pad:1", "…and View still does too");
        check(PadMapper.Parse("Extra8") == PadButton.Extra8 && PadMapper.ParseAny("Back,Extra1") == (PadButton.Back | PadButton.Extra1), "extra buttons parse by name");
        var st = new XInputGamepad { Buttons = PadButton.Y | PadButton.Extra3, LeftTrigger = 200, RightThumbY = -32767 };
        check(st.RawButtons == 0x8000 && Gamepad.Process(st).RT == 0 && Gamepad.Process(st).LT > .7 && Gamepad.Process(st).RY < -.99,
            "the XInput struct keeps its 2-byte button word: triggers and sticks still land in the right fields");
    }

    private static void FxPadMapper(Action<bool, string> check)
    {
        var c = AppConfig.FromJson("{}");
        var m = c.CreateFxPadMapper();
        var a = m.Update(PadButton.A, 100); var r = m.Update(0, 300);
        check(a.Single().Action == "Focus.Stutter:1/8" && r.Single().Kind == PadEventKind.Release, "FX pad A: stutter 1/8 while held, released on let-go");
        var lb = m.Update(PadButton.LeftShoulder, 400);
        check(lb.Count == 1 && lb[0].Action == "Focus.Cut", "FX pad LB cuts instantly (no shift-chord wait)");
        m.Update(PadButton.LeftShoulder | PadButton.RightShoulder, 420);
        check(!m.Shift, "LB + RB together is no shift on the FX pad");
        m.Update(0, 500);
        m.Update(PadButton.Back, 600); var sh = m.Update(PadButton.Back | PadButton.Y, 650);
        check(sh.Single().Action == "Focus.SpinBack" && sh[0].Shift, "FX pad Shift (View) + Y = spin-back");
        m.Update(0, 700);
        m.Update(PadButton.Back | PadButton.Start, 800); var p = m.Update(PadButton.Back | PadButton.Start, 1400);
        check(p.Any(e => e.Kind == PadEventKind.Panic), "View + Menu held on the FX pad is panic, as on controller 1");
    }

    private static void Rig(Action<bool, string> check)
    {
        var rig = new FxRig(0, 1);
        var set0 = FxCatalog.Sets[0];
        rig.Begin(); rig.Stick("c1:R", 1, rig.Set, 0, 1);
        var c = rig.Build(0, 120, 0, 0, 60);
        check(c.Drive[(int)set0.Up] == 1 && c.Mix[(int)set0.Up] == 1 && c.Drive[(int)set0.Down] == 1 && c.Mix[(int)set0.Down] == 0,
            $"stick straight up at full = {FxCatalog.Name(set0.Up)} at 100 %");
        check(rig.Build(1, 120, 0, 0, 60).Drive.All(v => v == 0), "deck B is untouched");
        rig.Begin(); rig.Stick("c1:R", 1, rig.Set, .7071, .7071);
        c = rig.Build(0, 120, 0, 0, 60);
        check(Math.Abs(c.Mix[(int)set0.Up] - .5) < .01 && Math.Abs(c.Mix[(int)set0.Right] - .5) < .01, $"diagonal up-right morphs {FxCatalog.Name(set0.Up)} and {FxCatalog.Name(set0.Right)} half and half");
        rig.Begin(); var s = rig.Stick("c1:R", 1, rig.Set, .05, .05);
        c = rig.Build(0, 120, 0, 0, 60);
        check(c.Drive.All(v => v == 0) && Math.Abs(s.Shares[0] - .5) < .01, "back at the centre: amount 0, the last direction is kept (no flip on release)");
        rig.StepSet(1);
        rig.Begin(); rig.Stick("c1:R", 1, rig.Set, 0, -1);
        c = rig.Build(0, 120, 0, 0, 60);
        check(c.Drive[(int)FxCatalog.Sets[1].Down] == 1 && c.Mix[(int)set0.Up] == 0, $"R3 to set 2: down is now {FxCatalog.Name(FxCatalog.Sets[1].Down)}");
        rig.Begin(); rig.Side("c1:LeftThumbX", 2, rig.Set, -1);
        c = rig.Build(1, 120, 0, 0, 60);
        check(c.Drive[(int)FxCatalog.Sets[1].SideLeft] == 1 && c.Mix[(int)FxCatalog.Sets[1].SideLeft] == 1, "left stick ← plays the set's left side effect");
        check(FxRig.TryHold("Stutter:1/8", out var fx, out var param) && fx == StickFx.BeatRepeat && Math.Abs(param - .5) < 1e-6, "Stutter 1/8 = beat repeat of half a beat");
        check(FxRig.TryHold("Transformer:1/32", out fx, out param) && fx == StickFx.Transformer && Math.Abs(param - .125) < 1e-6, "Transformer 1/32 = gate period of 1/8 beat");
        rig.Begin(); rig.SetTarget(FxTarget.Both);
        rig.Press("c2:A", FxRig.Mask(rig.Target, 0), "Stutter:1/8");
        check(rig.Build(0, 120, 0, 0, 60).Drive[(int)StickFx.BeatRepeat] == 1 && rig.Build(1, 120, 0, 0, 60).Param[(int)StickFx.BeatRepeat] == .5f, "FX pad target BOTH: a stutter hits both decks");
        rig.Press("c2:RB", 1, "SubDrop"); rig.Release("c2:RB"); rig.Press("c2:RB", 1, "SubDrop");
        check(rig.Build(0, 120, 0, 0, 60).Param[(int)StickFx.SubDrop] == 2 && rig.Build(1, 120, 0, 0, 60).Param[(int)StickFx.SubDrop] == 0, "each sub-drop press fires once (counter), on its target only");
        rig.ReleaseAll("c2:");
        check(rig.HoldCount == 0 && rig.Build(0, 120, 0, 0, 60).Drive[(int)StickFx.BeatRepeat] == 0, "an unplugged FX pad releases its holds");
        check(FxRig.Mask(FxTarget.Focus, 1) == 2 && FxRig.Mask(FxTarget.A, 1) == 1 && rig.StepTarget(1) == FxTarget.Focus, "target cycles focus · A · B · both");
        rig.Press("c1:x", 1, "Cut"); rig.Clear();
        check(rig.HoldCount == 0 && rig.Sticks.Count == 0, "FX clear lets go of everything");
        check(FxRig.Mask(FxTarget.B, 0) == 2 && new FxRig(7, -1).Set == 1 && new FxRig(7, -1).PadSet == FxCatalog.Sets.Count - 1, "saved set numbers wrap safely");
    }
}
