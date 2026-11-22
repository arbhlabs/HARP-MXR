using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;
using MixDock.Ui;

namespace MixDock;

/// <summary>
/// Stick FX (R3), the big controller HUD (R5) and controller 2 as a dedicated FX pad (R7).
/// Controller 1 drives the rack through the set-pad axis actions (SetPadX/Y, SetSide) and the momentary FX buttons;
/// controller 2 has its own profile, mapper, set and target deck. Everything meets in one <see cref="FxRig"/>, which is
/// turned into each deck's <see cref="FxRack.Controls"/> once per poll (<see cref="PublishRacks"/>).
/// </summary>
public sealed partial class MainForm
{
    private FxRig _rig = null!;
    private PadMapper _fxMapper = null!;
    private AppConfig.ControllerProfile _fxProfile = null!;
    private bool _fxPadWasConnected, _fromFxPad;
    private PadFrame _fxFrame;
    private readonly FxOverlay _fxOverlay = new();
    private readonly double[] _fxThrow = new double[2], _fxBend = new double[2];
    private readonly bool[] _fxReverse = new bool[2];
    private double _fxRiser;
    private readonly Dictionary<PadButton, List<string>> _fxPadDispatched = new();
    private readonly HashSet<string> _stickSeen = new();
    private readonly (double X, double Y, bool On)[] _mouseSet = new (double, double, bool)[2];
    private readonly bool[] _rackIdle = { true, true };
    private readonly List<(int Deck, int Slot, StudioButton Button)> _setButtons = new();
    private long _fxMenuSince = -1; private bool _fxMenuArmed;
    private long _toastUntil;

    private void InitStickFx()
    {
        _rig = new FxRig(_config.FxSet, _config.FxPadSet) { Deadzone = _config.FxDeadzone };
        Gamepad.FxPadDevice = _config.FxPadDevice; Gamepad.MixerDevice = _config.MixerDevice;
        RecreateFxMapper(0);
        if (!_preview) ExtraButtons.Install();
    }

    private void RecreateFxMapper(PadButton held)
    {
        _fxProfile = _config.ProfileFor(_config.FxPadProfileName);
        _fxMapper = _config.CreateFxPadMapper();
        _fxMapper.Reset(held);
    }

    /// <summary>The overlay sits over the middle of the waveforms (design 600 x 176 of the 1366 x 768 console).</summary>
    private void InstallFxOverlay(DjLayout dj)
    {
        dj.Place(_fxOverlay, 383, 46, FxOverlay.DesignWidth, FxOverlay.DesignHeight);
        _fxOverlay.Visible = false;
    }

    // ------------------------------------------------------------------ controller 2: the FX pad

    /// <summary>Reads controller 2: its sticks feed the rig, its triggers the echo / riser / brake amounts that ProcessAxes
    /// merges, its buttons run through its own mapper. Returns true while it is connected.</summary>
    private bool PollFxPad(long nowMs)
    {
        Array.Clear(_fxThrow); Array.Clear(_fxBend); Array.Clear(_fxReverse); _fxRiser = 0;
        bool on = Gamepad.ReadFrame(PadRole.FxPad, out var frame, out _);
        if (on != _fxPadWasConnected)
        {
            _fxPadWasConnected = on;
            _fxMapper.Reset(on ? frame.Buttons : 0);
            _fxMenuSince = -1; _fxMenuArmed = false;
            if (!on) { ReleaseFxPad(); SetMessage("FX pad (controller 2) disconnected: its held effects were released. Controller 1 is unchanged."); }
            else
            {
                SetMessage($"FX pad connected: controller 2 ({Gamepad.IdentityOf(PadRole.FxPad)}), profile {_config.FxPadProfileName}. Target {FxRig.TargetName(_rig.Target)}.");
                FxRumble(.35, 90);
                Toast(PadRole.FxPad, "PAD", "FX PAD CONNECTED", _config.FxPadProfileName.ToUpperInvariant(), FxRig.Mask(_rig.Target, _focus));
            }
        }
        if (!on) return false;
        if (_bindingsOpen) { _fxMapper.Reset(frame.Buttons); return true; }
        if (FxMenuHold(frame, nowMs)) { _fxMapper.Reset(frame.Buttons); OpenMappings(PadRole.FxPad); return true; }
        _fxFrame = frame;
        int mask = FxRig.Mask(_rig.Target, _focus);
        bool shift = _fxMapper.Shift;
        foreach (var (axis, normal) in _fxProfile.Axes)
            FxPadAxis(axis, shift && _fxProfile.ShiftAxes.TryGetValue(axis, out var sa) ? sa : normal, frame, mask, nowMs);
        if (shift) foreach (var (axis, sa) in _fxProfile.ShiftAxes) if (!_fxProfile.Axes.ContainsKey(axis)) FxPadAxis(axis, sa, frame, mask, nowMs);
        foreach (var ev in _fxMapper.Update(frame.Buttons, nowMs))
        {
            if (ev.Kind == PadEventKind.Panic) { Panic(); continue; }
            if (ev.Kind is PadEventKind.PreviousProfile or PadEventKind.NextProfile)
            {
                string name = _config.CycleFxPadProfile(ev.Kind == PadEventKind.NextProfile ? 1 : -1);
                ReleaseFxPad(); RecreateFxMapper(frame.Buttons); SaveQuietly();
                Toast(PadRole.FxPad, "PRO", "FX PAD PROFILE", name.ToUpperInvariant(), mask);
                SetMessage($"FX pad (controller 2) profile: {name}.");
                break;
            }
            _fromFxPad = true;
            try { FxPadButton(ev, mask); }
            finally { _fromFxPad = false; }
            if (ev.Kind == PadEventKind.Press) { FxRumble(.18, 35); ActionToast(PadRole.FxPad, ev); }
        }
        return true;
    }

    private void FxPadAxis(string axis, string action, PadFrame frame, int mask, long nowMs)
    {
        var (def, deck) = ActionCatalog.Parse(action, true);
        if (def == null) return;
        int m = def.Scope == ActionScope.Global ? mask : deck == 0 ? mask : deck == 1 ? 1 : 2;
        double v = Gamepad.Axis(frame, axis);
        bool left = axis.StartsWith("Left");
        var (sx, sy) = left ? (frame.LX, frame.LY) : (frame.RX, frame.RY);
        switch (def.Id)
        {
            case "SetPadX" or "SetPadY":
                if (_stickSeen.Add("c2:" + (left ? "L" : "R"))) _rig.Stick("c2:" + (left ? "L" : "R"), m, _rig.PadSet, sx, sy, nowMs);
                break;
            case "SetPad2X" or "SetPad2Y":
                if (_stickSeen.Add("c2:" + (left ? "L" : "R"))) _rig.Stick("c2:" + (left ? "L" : "R"), m, _rig.PadSet + 1, sx, sy, nowMs);
                break;
            case "SetSide": _rig.Side("c2:" + axis, m, _rig.PadSet, v, nowMs); break;
            case "EchoThrow": ForDecks(m, d => _fxThrow[d] = Math.Max(_fxThrow[d], Math.Max(0, v))); break;
            case "TimeBend": ForDecks(m, d => _fxBend[d] = Math.Max(_fxBend[d], Math.Max(0, v))); break;
            case "Reverse": if (v > .3) ForDecks(m, d => _fxReverse[d] = true); break;
            case "Riser": _fxRiser = Math.Max(_fxRiser, Math.Max(0, v)); break;
        }
    }

    private static void ForDecks(int mask, Action<int> act) { for (int d = 0; d < 2; d++) if ((mask & (1 << d)) != 0) act(d); }

    /// <summary>One controller-2 button event. "Focus." resolves to the FX pad's target (focus deck, A, B or both).</summary>
    private void FxPadButton(PadEvent ev, int mask)
    {
        var (def, deck) = ActionCatalog.Parse(ev.Action, false);
        if (def == null) return;
        bool down = ev.Kind != PadEventKind.Release, repeat = ev.Kind == PadEventKind.Repeat;
        if (def.Scope == ActionScope.Global) { Dispatch(def.Id, down, repeat); return; }
        int m = deck == 0 ? mask : deck == 1 ? 1 : 2;
        string holdId = "c2:" + ev.Button;
        if (FxRig.TryHold(def.Id, out _, out _))
        {
            if (down && !repeat) _rig.Press(holdId, m, def.Id);
            else if (!down) _rig.Release(holdId);
            return;
        }
        if (down && !repeat)
        {
            var list = new List<string>();
            ForDecks(m, d => list.Add($"{DeckName(d)}.{def.Id}"));
            _fxPadDispatched[ev.Button] = list;
            foreach (var a in list) Dispatch(a, true);
        }
        else if (repeat && _fxPadDispatched.TryGetValue(ev.Button, out var held)) foreach (var a in held) Dispatch(a, true, true);
        else if (!down && _fxPadDispatched.Remove(ev.Button, out var rel)) foreach (var a in rel) Dispatch(a, false);
    }

    private void ReleaseFxPad()
    {
        _rig.ReleaseAll("c2:");
        foreach (var list in _fxPadDispatched.Values) foreach (var a in list) Dispatch(a, false);
        _fxPadDispatched.Clear();
        Array.Clear(_fxThrow); Array.Clear(_fxBend); Array.Clear(_fxReverse); _fxRiser = 0;
    }

    /// <summary>Hold Menu alone (sticks at rest) for 0.9 s on controller 2: its own mapping editor.</summary>
    private bool FxMenuHold(PadFrame f, long now)
    {
        if (f.Buttons == 0) _fxMenuArmed = true;
        bool still = f.LX == 0 && f.LY == 0 && f.RX == 0 && f.RY == 0 && f.LT == 0 && f.RT == 0;
        if (f.Buttons != PadButton.Start || !still) { _fxMenuSince = -1; if (f.Buttons != 0) _fxMenuArmed = false; return false; }
        if (!_fxMenuArmed) return false;
        if (_fxMenuSince < 0) _fxMenuSince = now;
        if (now - _fxMenuSince < 900) return false;
        _fxMenuArmed = false; _fxMenuSince = -1; return true;
    }

    private void FxRumble(double strength, int ms)
    {
        if (_preview || !_config.GamepadRumble) return;
        Gamepad.Rumble(PadRole.FxPad, strength, strength);
        int slot = Gamepad.SlotOf(PadRole.FxPad);
        Schedule(ms, () => { if (Gamepad.SlotOf(PadRole.FxPad) == slot) Gamepad.Rumble(PadRole.FxPad, 0, 0); });
    }

    // ------------------------------------------------------------------ controller 1 hooks

    /// <summary>Set-pad axes of controller 1 (called from ProcessAxes). Both axes of a stick map to one pad.</summary>
    private void MixerStickFx(string axis, string ctl, int deck, PadFrame frame, long nowMs)
    {
        bool left = axis.StartsWith("Left");
        double dz = _config.FxDeadzone;
        if (ctl == "SetSide")
        {
            double v = Gamepad.Axis(frame, axis);
            int o = GestureOwner(axis + "#side", deck, FxCurves.Amount(v, 0, dz) > 0);
            _rig.Side("c1:" + axis, 1 << o, _rig.Set, v, nowMs);
            return;
        }
        string id = "c1:" + (left ? "L" : "R");
        if (!_stickSeen.Add(id)) return;
        var (x, y) = left ? (frame.LX, frame.LY) : (frame.RX, frame.RY);
        int owner = GestureOwner(id + "#set", deck, FxCurves.Amount(x, y, dz) > 0);
        _rig.Stick(id, 1 << owner, ctl.StartsWith("SetPad2") ? _rig.Set + 1 : _rig.Set, x, y, nowMs);
    }

    /// <summary>New FX actions from either controller (Dispatch calls this first). True = handled.</summary>
    private bool StickFxDispatch(string action, int? deck, string ctl, int d, bool down, bool repeat)
    {
        if (deck is null)
        {
            switch (ctl)
            {
                case "SetNext" or "SetPrev":
                    if (down && !repeat) StepFxSet(ctl == "SetNext" ? 1 : -1, _fromFxPad);
                    return true;
                case "FxClear":
                    if (down && !repeat) { _rig.Clear(); ReleaseFxPad(); Toast(_fromFxPad ? PadRole.FxPad : PadRole.Mixer, "FX", "FX CLEAR", "DRY", 3, "every stick effect glides back to dry"); }
                    return true;
                case "FxTargetNext" or "FxTargetPrev":
                    if (down && !repeat)
                    {
                        ReleaseFxPad();
                        var t = _rig.StepTarget(ctl == "FxTargetNext" ? 1 : -1);
                        Toast(PadRole.FxPad, "◎", "FX PAD TARGET", FxRig.TargetName(t), FxRig.Mask(t, _focus));
                        SetMessage($"FX pad plays on: {FxRig.TargetName(t).ToLowerInvariant()}.");
                    }
                    return true;
                case "SwapControllers":
                    if (down && !repeat) SwapControllers();
                    return true;
            }
            return false;
        }
        if (!FxRig.TryHold(ctl, out _, out _)) return false;
        string holdId = "c1:" + action;
        if (down && !repeat) _rig.Press(holdId, 1 << d, ctl);
        else if (!down) _rig.Release(holdId);
        return true;
    }

    private void StepFxSet(int step, bool fxPad)
    {
        int set = fxPad ? _rig.StepPadSet(step) : _rig.StepSet(step);
        if (fxPad) _config.FxPadSet = set; else _config.FxSet = set;
        var s = FxCatalog.Sets[set];
        string next = fxPad ? $" · right stick: {FxCatalog.Sets[FxCatalog.WrapSet(set + 1)].Name}" : "";
        Toast(fxPad ? PadRole.FxPad : PadRole.Mixer, step > 0 ? "▶" : "◀", $"SET {s.Name}", $"{set + 1}/{FxCatalog.Sets.Count}",
            fxPad ? FxRig.Mask(_rig.Target, _focus) : 1 << _focus,
            $"↑ {FxCatalog.Name(s.Up)}  ↓ {FxCatalog.Name(s.Down)}  ← {FxCatalog.Name(s.Left)}  → {FxCatalog.Name(s.Right)}{next}");
        SetMessage($"{(fxPad ? "FX pad" : "Stick")} FX set {set + 1}: {s.Name} — up {FxCatalog.Name(s.Up)}, down {FxCatalog.Name(s.Down)}, left {FxCatalog.Name(s.Left)}, right {FxCatalog.Name(s.Right)}; left stick ← {FxCatalog.Name(s.SideLeft)}, → {FxCatalog.Name(s.SideRight)}.");
        if (fxPad) FxRumble(.3, 70); else RumblePulse(.3, 70);
        foreach (var (deck, slot, b) in _setButtons) RefreshSetButton(deck, slot, b);
    }

    private void SwapControllers()
    {
        ReleaseHeldGestures(); ReleaseFxPad();
        if (!Gamepad.SwapRoles()) { SetMessage("Swap needs two controllers connected."); return; }
        _config.FxPadDevice = Gamepad.FxPadDevice; _config.MixerDevice = Gamepad.MixerDevice; SaveQuietly();
        _padMapper.Reset(Gamepad.ReadFrame(PadRole.Mixer, out var a, out _) ? a.Buttons : 0);
        _fxMapper.Reset(Gamepad.ReadFrame(PadRole.FxPad, out var b, out _) ? b.Buttons : 0);
        _padWasConnected = true; _fxPadWasConnected = true;
        Toast(PadRole.Mixer, "⇄", "CONTROLLERS SWAPPED", "1 ⇄ 2", 2, $"mixer {Gamepad.IdentityOf(PadRole.Mixer)} · FX pad {Gamepad.IdentityOf(PadRole.FxPad)}");
        SetMessage($"Controllers swapped: mixer = {Gamepad.IdentityOf(PadRole.Mixer)}, FX pad = {Gamepad.IdentityOf(PadRole.FxPad)} (remembered).");
    }

    /// <summary>Mapping editor for one controller. Controller 2 edits its own profile copy; controller 1's layers stay put.</summary>
    private void OpenMappings(PadRole role)
    {
        if (role == PadRole.Mixer) { OpenMappings(); return; }
        if (_bindingsOpen) return;
        ReleaseFxPad();
        _bindingsOpen = true;
        try
        {
            var copy = _config.FxPadEditCopy();
            using var dlg = new BindingsForm(copy, PadRole.FxPad);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _config.CommitFxPadEdit(copy);
                RecreateFxMapper(Gamepad.ReadFrame(PadRole.FxPad, out var f, out _) ? f.Buttons : 0);
                SaveQuietly();
                SetMessage($"FX pad (controller 2) mappings saved: profile {_config.FxPadProfileName}.");
            }
        }
        catch (Exception ex) { Log.Write("FX pad mappings: " + ex); SetMessage("Could not open the FX pad mapping editor: " + ex.Message, true); }
        finally { _bindingsOpen = false; }
    }

    private void SaveQuietly()
    {
        if (_preview || ShotPath != null || UiCheckDirectory != null) return;
        try { _config.Save(); } catch (Exception ex) { Log.Write("save fx: " + ex.Message); }
    }

    // ------------------------------------------------------------------ rack publishing and HUD

    /// <summary>Once per poll: the mouse set pad joins the sticks, each deck's rack gets its targets, the HUD follows.</summary>
    private void PublishRacks(long nowMs)
    {
        for (int d = 0; d < 2; d++)
            if (_mouseSet[d].On) _rig.Stick("mouse" + d, 1 << d, _rig.Set, _mouseSet[d].X, _mouseSet[d].Y, nowMs);
        for (int d = 0; d < 2; d++)
        {
            bool any = _rig.Any(d);
            if (!any && _rackIdle[d]) continue;
            _rackIdle[d] = !any;
            var (bpm, ph) = Tempo(d);
            Fx(d).Rack.State = _rig.Build(d, bpm, ph, System.Diagnostics.Stopwatch.GetTimestamp(), _config.FxSmoothMs);
        }
        StickHud();
        _fxOverlay.Tick();
        foreach (var (deck, slot, b) in _setButtons)
        {
            var (drive, mix) = Fx(deck).Rack.Level(FxCatalog.Sets[_rig.Set].Slot(slot));
            bool lit = drive > .02f && mix > .1f;
            if (b.Active != lit) b.Active = lit;
        }
    }

    /// <summary>While a set stick is moving, the HUD shows it (the most recently engaged one wins).</summary>
    private void StickHud()
    {
        FxRig.StickState? best = null; string bestId = "";
        foreach (var (id, s) in _rig.Sticks)
            if (s.Active && (best == null || s.ActiveAt > best.ActiveAt)) { best = s; bestId = id; }
        if (best == null || Environment.TickCount64 < _toastUntil) return;
        var set = FxCatalog.Sets[FxCatalog.WrapSet(best.Set)];
        string[] dirs = best.Side ? ["", "", FxCatalog.Name(set.SideLeft), FxCatalog.Name(set.SideRight)]
                                  : [FxCatalog.Name(set.Up), FxCatalog.Name(set.Down), FxCatalog.Name(set.Left), FxCatalog.Name(set.Right)];
        var order = Enumerable.Range(0, 4).OrderByDescending(k => best.Shares[k]).ToArray();
        string title = dirs[order[0]];
        if (!best.Side && best.Shares[order[1]] > .25) title += " + " + dirs[order[1]];
        bool fxPad = bestId.StartsWith("c2:"), mouse = bestId.StartsWith("mouse");
        string source = mouse ? "MOUSE XY PAD" : $"{(bestId.Contains(":L") || bestId.Contains("LeftThumb") ? "LEFT" : "RIGHT")} STICK · {(fxPad ? "FX PAD" : "CONTROLLER 1")}";
        double sx = 0, sy = 0;
        if (mouse) { int d = bestId[^1] - '0'; sx = _mouseSet[d].X; sy = _mouseSet[d].Y; }
        else
        {
            var f = fxPad ? _fxFrame : _lastFrame;
            bool left = bestId.Contains(":L") || bestId.Contains("LeftThumb");
            (sx, sy) = left ? (f.LX, f.LY) : (f.RX, f.RY);
            if (best.Side) sy = 0;
        }
        _fxOverlay.Show(new HudView(true, title, $"{best.Amount * 100:0}%", best.Amount, source, mouse ? "XY" : best.Side ? "↔" : bestId.EndsWith("L") ? "L" : "R",
            DeckLabel(best.Mask), $"SET {FxCatalog.WrapSet(best.Set) + 1}/{FxCatalog.Sets.Count} · {set.Name}", dirs, (double[])best.Shares.Clone(), sx, sy, AccentOf(best.Mask)));
    }

    private static string DeckLabel(int mask) => mask == 3 ? "BOTH DECKS" : mask == 2 ? "DECK B" : "DECK A";
    private static int AccentOf(int mask) => mask == 3 ? 2 : mask == 2 ? 1 : 0;

    /// <summary>Big HUD toast for a button / move (held for 1.5 s; a moving set stick takes over again after 0.6 s).</summary>
    private void Toast(PadRole role, string glyph, string title, string value, int maskOrAccent, string detail = "")
    {
        int accent = maskOrAccent is 1 or 2 or 3 ? AccentOf(maskOrAccent) : 0;
        if (maskOrAccent == 3 && title == "FX CLEAR") accent = 3;
        string setName = role == PadRole.FxPad
            ? $"FX PAD · SETS {FxCatalog.Sets[_rig.PadSet].Name} / {FxCatalog.Sets[FxCatalog.WrapSet(_rig.PadSet + 1)].Name} · {FxRig.TargetName(_rig.Target)}"
            : $"SET {_rig.Set + 1}/{FxCatalog.Sets.Count} · {FxCatalog.Sets[_rig.Set].Name}";
        _fxOverlay.Show(new HudView(false, title, value, double.NaN, role == PadRole.FxPad ? "FX PAD" : "CONTROLLER 1", glyph,
            maskOrAccent is 1 or 2 or 3 ? DeckLabel(maskOrAccent) : DeckLabel(1 << _focus), setName, Accent: accent, Detail: detail));
        _toastUntil = Environment.TickCount64 + 600;
    }

    /// <summary>Every pressed button on either controller shows what it did.</summary>
    private void ActionToast(PadRole role, PadEvent ev)
    {
        var (def, deck) = ActionCatalog.Parse(ev.Action, false);
        if (def == null || def.Id is "SetNext" or "SetPrev" or "FxTargetNext" or "FxTargetPrev" or "FxClear" or "SwapControllers") return;
        int mask = def.Scope == ActionScope.Global ? 0 : deck == 1 ? 1 : deck == 2 ? 2 : role == PadRole.FxPad ? FxRig.Mask(_rig.Target, _focus) : 1 << _focus;
        string label = def.Label;
        int cut = label.IndexOf(" (", StringComparison.Ordinal); if (cut > 0) label = label[..cut];
        string value = def.Hold ? "HOLD" : def.Id switch
        {
            "FocusA" => "DECK A", "FocusB" => "DECK B", "FocusToggle" => $"DECK {DeckName(_focus)}",
            "Crossfader-" or "Crossfader+" or "CrossfaderReset" or "CrossfaderA" or "CrossfaderB" => $"{_cross.Value}%",
            "BassCut" => Panel(deck == 2 ? 1 : deck == 1 ? 0 : _focus).GetParameters().BassCut ? "−24 dB" : "ON",
            "MasterMute" => _engine.Mixer.Mute ? "MUTED" : "ON",
            "QuantCycle" => PadQuant.Label(_config.Quantize),
            _ => "✓"
        };
        _fxOverlay.Show(new HudView(false, label.ToUpperInvariant(), value, double.NaN, $"{(role == PadRole.FxPad ? "FX PAD" : "CONTROLLER 1")}{(ev.Shift ? " · SHIFT" : "")}",
            Glyph(ev.Button), mask == 0 ? "MASTER" : DeckLabel(mask),
            role == PadRole.FxPad ? $"FX PAD · {FxRig.TargetName(_rig.Target)}" : $"SET {_rig.Set + 1}/{FxCatalog.Sets.Count} · {FxCatalog.Sets[_rig.Set].Name}",
            Accent: mask == 0 ? 2 : AccentOf(mask)));
        _toastUntil = Environment.TickCount64 + 600;
    }

    internal static string Glyph(PadButton b) => b switch
    {
        PadButton.LeftShoulder => "LB", PadButton.RightShoulder => "RB", PadButton.LeftThumb => "L3", PadButton.RightThumb => "R3",
        PadButton.Back => "VIEW", PadButton.Start => "MENU", PadButton.Up => "↑", PadButton.Down => "↓", PadButton.Left => "←", PadButton.Right => "→",
        >= PadButton.Extra1 => "P" + (System.Numerics.BitOperations.TrailingZeroCount((uint)b) - 15),
        _ => b.ToString()
    };

    /// <summary>Feeds a deck's small L / R stick readout (caption, detail, active) when that stick is a set pad.</summary>
    private (string Caption, string Detail, bool Active)? StickFxReadout(int deck, bool right)
    {
        string[] ids = right ? ["c1:R", "mouse" + deck] : ["c1:L", "c1:LeftThumbX", "c1:LeftThumbY"];
        foreach (var id in ids)
        {
            if (!_rig.Sticks.TryGetValue(id, out var s) || (s.Mask & (1 << deck)) == 0) continue;
            if (!s.Active) continue;
            return (FxCatalog.Name(s.LeadFx), $"{s.Amount:P0}", true);
        }
        string axis = right ? "RightThumbX" : "LeftThumbX";
        string bound = _config.GamepadAxes.GetValueOrDefault(axis, "");
        if (!bound.Contains(".SetPad") && !bound.Contains(".SetSide")) return null;
        var set = FxCatalog.Sets[_rig.Set];
        return (right ? $"SET {set.Name}" : $"{FxCatalog.Name(set.SideLeft)} | {FxCatalog.Name(set.SideRight)}", "Rest", false);
    }

    // ------------------------------------------------------------------ mouse: set buttons + XY pad

    /// <summary>The deck's FX row: the set name (click = next set, right-click = previous) and the set's four effects
    /// (hold one to play it at 75 % on this deck). The XY pad below plays the set like the right stick.</summary>
    private void InstallSetButtons(int deck, int x, int y, Color accent, Func<string, int, int, int, int, Action, Color?, StudioButton> button)
    {
        var setButton = button("SET", x, y, 62, 24, () => StepFxSet(1, false), accent);
        setButton.AccessibleName = $"Deck {DeckName(deck)} FX set: click next, right-click previous";
        setButton.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) StepFxSet(-1, false); };
        _setButtons.Add((deck, -1, setButton));
        for (int k = 0; k < 4; k++)
        {
            int slot = k;
            var b = button("", x + 65 + k * 65, y, 62, 24, () => { }, accent);
            b.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { AuditionSlot(deck, slot, true); b.Capture = true; } };
            b.MouseUp += (_, _) => { AuditionSlot(deck, slot, false); b.Capture = false; };
            b.MouseCaptureChanged += (_, _) => { if (!b.Capture) AuditionSlot(deck, slot, false); };
            _setButtons.Add((deck, slot, b));
        }
        foreach (var (d, s, b) in _setButtons.Where(t => t.Deck == deck)) RefreshSetButton(d, s, b);
    }

    private readonly (bool On, int Slot)[] _audition = new (bool, int)[2];
    private void AuditionSlot(int deck, int slot, bool on)
    {
        _audition[deck] = (on, slot);
        double[] dx = [0, 0, -1, 1], dy = [1, -1, 0, 0];
        _mouseSet[deck] = on ? (dx[slot] * .82, dy[slot] * .82, true) : _mouseFreeze[deck] ? _mouseSet[deck] : (0, 0, false);
        if (on) Toast(PadRole.Mixer, "XY", FxCatalog.Name(FxCatalog.Sets[_rig.Set].Slot(slot)), "75%", 1 << deck, "mouse audition: hold the button");
    }

    private void RefreshSetButton(int deck, int slot, StudioButton b)
    {
        var set = FxCatalog.Sets[_rig.Set];
        string[] arrows = ["↑", "↓", "←", "→"];
        string text = slot < 0 ? set.Name : arrows[slot] + FxCatalog.Short(set.Slot(slot));
        if (b.Text != text) { b.Text = text; b.AccessibleName = slot < 0 ? $"Deck {DeckName(deck)} FX set {set.Name}: click next" : $"Deck {DeckName(deck)} hold to play {FxCatalog.Name(set.Slot(slot))}"; b.Invalidate(); }
    }

    /// <summary>Mouse XY pad = the right stick for that deck (x, y in 0..1 from the pad); FREEZE latches the position.</summary>
    private void MouseSetPad(int d, double x01, double y01, bool on)
    {
        if (_audition[d].On) return;
        if (on) _mouseSet[d] = (x01 * 2 - 1, y01 * 2 - 1, true);
        else if (!_mouseFreeze[d]) _mouseSet[d] = (0, 0, false);
        foreach (var (deck, slot, b) in _setButtons.Where(t => t.Deck == d && t.Slot < 0)) b.Active = _mouseFreeze[d];
    }
}
