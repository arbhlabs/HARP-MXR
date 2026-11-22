using MixDock.Audio;
using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

/// <summary>Xbox controller performance layer: axes, mapped buttons (via PadMapper), beat quantization, rumble, overlay.</summary>
public sealed partial class MainForm
{
    private PadMapper _padMapper = null!;
    private PadOverlay _overlay = null!;
    private int _focus;
    private readonly FilterLatch[] _filters = { new(), new() };
    private readonly TapTempo[] _taps = { new(), new() };
    private readonly double[] _loopBeats = { 4, 4 };
    private readonly bool[] _loopOn = new bool[2];
    private readonly string?[] _rollAction = new string?[2];
    private readonly Dictionary<string, int> _axisOwner = new();
    private readonly long[] _throwAt = { -1, -1 };
    private readonly double[] _sendNow = new double[2];
    private bool _riserOn; private long _riserStartMs;
    private readonly List<(long Due, Action Act)> _scheduled = new();
    private double _beatPrevPhase = double.NaN;
    private PadFrame _lastFrame;
    private PadFx _fx = PadFx.Space;
    private bool _fxFreeze, _spinHeld;
    private readonly FxHud _hud = new();
    private readonly bool[] _axisRoll = new bool[2], _padLooping = new bool[2];
    private readonly double[] _rollBeatsNow = new double[2];
    private readonly int[] _padHeld = { -1, -1 };
    private readonly long[] _padDownAt = new long[2];
    private double _trigRumbleL, _trigRumbleR;
    /// <summary>Momentary button actions held right now (EchoThrow, Reverse, TimeBend, Riser): action -> deck it started on.</summary>
    private readonly Dictionary<string, int> _holds = new();
    private bool _bindingsOpen;
    private string _moveText = ""; private long _moveAt;

    private DeckPanel Panel(int d) => d == 0 ? _a : _b;
    private PerformanceFx Fx(int d) => _engine.Mixer.Fx(d);
    private PerformanceSampler Sampler(int d) => _engine.Mixer.Sampler(d);
    private DeckReader Reader(int d) => d == 0 ? _engine.ReaderA : _engine.ReaderB;
    private DeckSource Source(int d) => d == 0 ? _engine.DeckA : _engine.DeckB;
    private static string DeckName(int d) => d == 0 ? "A" : "B";

    private void InitPad()
    {
        _padMapper = _config.CreatePadMapper();
        InitStickFx();
        _filters[0].Load(_config.A.FilterLatch ?? _config.A.Filter);
        _filters[1].Load(_config.B.FilterLatch ?? _config.B.Filter);
        for (int d = 0; d < 2; d++)
        {
            int deck = d;
            Panel(d).Filter.SetPrecise(_filters[d].Effective);
            Panel(d).Filter.UserEdited += (_, _) => _filters[deck].SetFromKnob(Panel(deck).Filter.Precise);
        }
        for (int d = 0; d < 2; d++)
        {
            int dd = d;
            Panel(d).Pads.PadPressed += (pad, capture) =>
            {
                if (capture) { PadCapture(dd, pad); return; }
                if (ModeOf(_bank) == BankMode.Sample) { PadPlay(dd, PadBanks.Index(_bank, pad), true); PadPlay(dd, PadBanks.Index(_bank, pad), false); }
                else { BankPadDown(dd, pad, false); BankPadUp(pad); }
            };
        }
        SetFocus(0, quiet: true);
        _overlay = new PadOverlay(_config);
        Controls.Add(_overlay);
        InitBanks();
        Resize += (_, _) => PlaceOverlay();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F1 && e.Shift) { ToggleOverlay(); e.Handled = true; } };
    }

    private void PlaceOverlay()
    {
        if (_overlay is null) return;
        float s = _dj?.ScaleFactor ?? DeviceDpi / 96f;
        int w = Math.Min(ClientSize.Width - (int)(48 * s), (int)(PadOverlay.DesignSize.Width * s));
        int h = Math.Min(ClientSize.Height - (int)(160 * s), (int)(PadOverlay.DesignSize.Height * s));
        _overlay.SetBounds((ClientSize.Width - w) / 2, Math.Max(0, (ClientSize.Height - h) / 2), w, h);
    }
    private void ToggleOverlay()
    {
        PlaceOverlay();
        _overlay.Visible = !_overlay.Visible;
        if (_overlay.Visible) { _overlay.BringToFront(); _overlay.SetState(_lastFrame, _padMapper.Shift, _padWasConnected, _focus); }
        _padButton.Active = _overlay.Visible; _padButton.Invalidate();
    }

    private void SetFocus(int d, bool quiet = false)
    {
        _focus = d; _a.PadFocus = d == 0; _b.PadFocus = d == 1;
        ShowFocus();
        if (!quiet) { SetMessage($"Controller focus: Deck {DeckName(d)}."); AnnounceMove("FOCUS", d); RumblePulse(.15, 30); }
    }

    /// <summary>Heard tempo + beat phase of a deck; tap tempo fills in while auto-detect has no lock.</summary>
    private (double Bpm, double Phase) Tempo(int d)
    {
        double bpm = DeckSync.HeardBpm(_engine, d), ph = DeckSync.HeardPhase(_engine, d);
        if (bpm > 0 && !double.IsNaN(ph)) return (bpm, ph);
        long now = Environment.TickCount64;
        return _taps[d].Bpm > 0 ? (_taps[d].Bpm, _taps[d].Phase(now)) : (bpm, ph);
    }
    private double MsToGrid(int d, double gridBeats) { var (bpm, ph) = Tempo(d); return BeatQuant.MsToNext(ph, bpm, gridBeats); }

    /// <summary>Filter knob + DSP = latched fine value + spring offset.</summary>
    private void ApplyFilter(int d) => Panel(d).Filter.SetPrecise(_filters[d].Effective);
    private void SetFilterLatch(int d, double v) { _filters[d].Load(v); ApplyFilter(d); }

    private static int? DeckOf(string action, int focus, out string control)
    {
        if (action.StartsWith("Focus.")) { control = action[6..]; return focus; }
        if (action.Length > 2 && action[1] == '.' && action[0] is 'A' or 'B') { control = action[2..]; return action[0] == 'A' ? 0 : 1; }
        control = action; return null;
    }
    /// <summary>Gesture owner: the deck focused when the gesture started keeps it until it returns to rest.</summary>
    private int GestureOwner(string axis, int deck, bool active)
    {
        if (_axisOwner.TryGetValue(axis, out int o)) { if (!active) _axisOwner.Remove(axis); return o; }
        if (active) _axisOwner[axis] = deck;
        return deck;
    }

    private void RumblePulse(double strength, int ms, bool force = true)
    {
        if (_preview) return;
        if (!_config.GamepadRumble || (!force && _rumbleUntil != 0)) return;
        Gamepad.Rumble(strength, strength); _rumbleUntil = Environment.TickCount64 + ms;
    }

    private void PollPad()
    {
        if (_rumbleUntil != 0 && Environment.TickCount64 > _rumbleUntil) { Gamepad.Rumble(0, 0); _rumbleUntil = 0; _trigRumbleL = _trigRumbleR = 0; }
        if (_preview) return;
        long nowMs = Environment.TickCount64;
        RunScheduled(nowMs);
        ClockTick(nowMs);
        _rig.Begin(); _stickSeen.Clear();
        try { PollControllers(nowMs); }
        finally { PublishRacks(nowMs); }
    }

    /// <summary>Controller 2 (FX pad) first, so its trigger amounts are ready when controller 1's axes are processed.</summary>
    private void PollControllers(long nowMs)
    {
        if (!_padEnabled.Checked) { _padMapper.Reset(0); _fxMapper.Reset(0); _padStatus.Text = "●  Disabled"; return; }
        Gamepad.Poll();
        bool fxPad = PollFxPad(nowMs);
        bool connected = Gamepad.ReadFrame(PadRole.Mixer, out var frame, out var state);
        if (connected != _padWasConnected)
        {
            foreach (var drv in _axisDrivers.Values) drv.Reset();
            _padMapper.Reset(connected ? frame.Buttons : 0); // no phantom presses on (re)connect
            _padWasConnected = connected;
            if (!connected) { ReleaseHeldGestures(); SetMessage("Controller disconnected: held moves were released."); }
            if (connected)
            {
                RumblePulse(.35, 90);
                // a startup/recovery message wins over the hint when the pad was already plugged in at launch
                if (_padSeenOnce || string.IsNullOrWhiteSpace(_message.Text)) SetMessage(PadHints.Connected(Gamepad.Slot + 1));
                _padSeenOnce = true;
            }
            _mapMenuSince = -1;
            _mapMenuArmed = false;
        }
        if (!connected)
        {
            _padStatus.Text = fxPad ? $"●  Controller 1 not connected · FX pad {Gamepad.SlotOf(PadRole.FxPad) + 1} {FxRig.TargetName(_rig.Target)}" : "●  Not connected";
            _padStatus.ForeColor = Theme.Muted;
            if (_overlay.Visible) _overlay.SetState(default, false, false, _focus);
            // the FX pad still plays its triggers through the axis path when controller 1 is away
            if (fxPad && !_bindingsOpen) ProcessAxes(default, Math.Clamp(_padClock.Elapsed.TotalSeconds, 0.001, 0.1), nowMs);
            _padClock.Restart();
            return;
        }
        if (_bindingsOpen)
        {
            // mapping editor open: the controller only points at rows there; nothing fires here (and no phantom press on close)
            _padMapper.Reset(frame.Buttons);
            _padStatus.Text = "●  Controller: mapping editor open"; return;
        }
        _padStatus.ForeColor = _focus == 0 ? Theme.Cyan : Theme.Amber;
        if (MappingHold(frame, nowMs))
        {
            _padMapper.Reset(frame.Buttons);
            OpenMappings();
            _padMapper.Reset(Gamepad.ReadFrame(PadRole.Mixer, out var current, out _) ? current.Buttons : 0);
            return;
        }
        _lastFrame = frame;
        double dt = Math.Clamp(_padClock.Elapsed.TotalSeconds, 0.001, 0.1); _padClock.Restart();
        PadBankTick(frame, state.Gamepad.Buttons);
        ProcessAxes(frame, dt, nowMs);
        foreach (var ev in _padMapper.Update(frame.Buttons, nowMs))
        {
            if (ev.Kind == PadEventKind.Panic) { Panic(); continue; }
            if (ev.Kind is PadEventKind.PreviousProfile or PadEventKind.NextProfile)
            {
                CycleControllerProfile(ev.Kind == PadEventKind.NextProfile ? 1 : -1, frame.Buttons);
                break;
            }
            Dispatch(ev.Action, ev.Kind != PadEventKind.Release, ev.Kind == PadEventKind.Repeat);
            if (ev.Kind == PadEventKind.Press && !ev.Action.Contains("Pad")) RumblePulse(.18, 35);   // pad hits rumble by their velocity instead
            if (ev.Kind == PadEventKind.Press) ActionToast(PadRole.Mixer, ev);
        }
        BeatTick(frame);
        _padStatus.Text = $"●  Pad {Gamepad.Slot + 1}{(fxPad ? $" + FX pad {Gamepad.SlotOf(PadRole.FxPad) + 1}" : "")}  {(_padMapper.Shift ? "SHIFT  " : "")}Deck {DeckName(_focus)}  L {Arrow(frame.LX, frame.LY)}  R {Arrow(frame.RX, frame.RY)}";
        if (_overlay.Visible) _overlay.SetState(frame, _padMapper.Shift, true, _focus);
    }

    private bool _padSeenOnce;
    private long _mapMenuSince = -1;
    private bool _mapMenuArmed;
    private int _nudgeRepeats;

    // A solo long press leaves normal taps and performance chords intact. If Menu
    // is the configured Shift button, View is the editor shortcut instead.
    private bool MappingHold(PadFrame frame, long nowMs)
    {
        var shortcut = string.Equals(_config.ShiftButton, "Start", StringComparison.OrdinalIgnoreCase) ? PadButton.Back : PadButton.Start;
        if (frame.Buttons == 0) _mapMenuArmed = true;
        bool still = frame.LX == 0 && frame.LY == 0 && frame.RX == 0 && frame.RY == 0 && frame.LT == 0 && frame.RT == 0;
        if (frame.Buttons != shortcut || !still)
        {
            _mapMenuSince = -1;
            if (frame.Buttons != 0) _mapMenuArmed = false;
            return false;
        }
        if (!_mapMenuArmed) return false;
        if (_mapMenuSince < 0) _mapMenuSince = nowMs;
        if (nowMs - _mapMenuSince < 900) return false;
        _mapMenuArmed = false;
        _mapMenuSince = -1;
        return true;
    }

    private readonly double[] _appliedLeftReverb = new double[2];
    private readonly double[] _blendEcho = new double[2], _blendVerb = new double[2];

    private void ProcessAxes(PadFrame frame, double dt, long nowMs)
    {
        Span<double> send = stackalloc double[2], wash = stackalloc double[2], throwAmt = stackalloc double[2];
        Span<double> bend = stackalloc double[2], roll = stackalloc double[2], fxAmt = stackalloc double[2];
        Span<bool> reverse = stackalloc bool[2];
        double riser = 0; bool fxPad = false; int fxDeck = _focus;
        bool shift = _padMapper.Shift;
        Span<double> blendL = stackalloc double[2], blendR = stackalloc double[2];
        string ActionFor(string ax) => shift && _config.ShiftAxes.TryGetValue(ax, out var s1) ? s1 : _config.GamepadAxes.GetValueOrDefault(ax, "");
        foreach (var (axis, normalAction) in _config.GamepadAxes)
        {
            string action = shift && _config.ShiftAxes.TryGetValue(axis, out var sa) ? sa : normalAction;
            // a stick used as an FX-blend XY pad reads its other axis without the axis lock, so circling morphs smoothly
            string? partner = axis switch { "LeftThumbY" => "LeftThumbX", "RightThumbY" => "RightThumbX", _ => null };
            double v = partner != null && ActionFor(partner).EndsWith("FxBlend") ? Gamepad.Axis(frame, axis) : Gamepad.AxisLocked(frame, axis);
            int? deck = DeckOf(action, _focus, out string ctl);
            bool absolute = ctl == "Filter" && deck != null;
            if (!_axisDrivers.TryGetValue(axis, out var drv) || drv.Absolute != absolute)
                _axisDrivers[axis] = drv = new AxisDriver(absolute);
            if (absolute) { drv.SmoothMs = _config.FilterSmoothMs; v = Gamepad.FilterCurve(v, _config.FilterDeadzone, _config.FilterExpo); }
            switch (ctl)
            {
                case "Filter" when deck is int fd:
                {
                    // spring sweep: same driver as 1.0 (feel unchanged), now riding on top of the latched value
                    var result = drv.Update(v, dt);
                    int owner = GestureOwner(axis, fd, result != null);
                    double before = _filters[owner].Spring;
                    _filters[owner].SetSpring(result);
                    if (result != null || before != 0) ApplyFilter(owner);
                    break;
                }
                case "FilterFine" when deck is int d:
                    if (_filters[d].Creep(v, dt)) ApplyFilter(d);
                    break;
                case "FxBlend" when deck is int bd:
                {
                    var (sx, sy) = axis.StartsWith("Right") ? (frame.RX, frame.RY) : (frame.LX, frame.LY);
                    var (bl, br) = FxCurves.SideBlend(sx, sy, _config.FxDeadzone);
                    int o = GestureOwner(axis + "#blend", bd, bl + br > 0);
                    blendL[o] = Math.Max(blendL[o], bl); blendR[o] = Math.Max(blendR[o], br);
                    break;
                }
                case "FxX" or "FxY":
                    // XY pad: both stick axes are used together (no axis lock); the deck focused when the gesture started keeps it
                    fxPad = true; if (deck is int pd) fxDeck = pd;
                    break;
                case "SetPadX" or "SetPadY" or "SetPad2X" or "SetPad2Y" or "SetSide" when deck is int sd:
                    // stick FX sets: 4 effects on a stick (direction picks, distance = amount), 2 on one axis
                    MixerStickFx(axis, ctl, sd, frame, nowMs);
                    break;
                case "TimeBend" when deck is int d:
                {
                    int o = GestureOwner(axis + "#bend", d, v > 0);
                    bend[o] = Math.Max(bend[o], v); break;
                }
                case "BeatRoll" when deck is int d:
                {
                    int o = GestureOwner(axis + "#roll", d, v > 0.1);
                    roll[o] = Math.Max(roll[o], v); break;
                }
                case "Reverse" when deck is int d:
                {
                    int o = GestureOwner(axis + "#rev", d, v > 0.3);
                    if (v > 0.3) reverse[o] = true; break;
                }
                case "SpinBack" when deck is int d:
                {
                    bool on = v > 0.5;
                    if (on && !_spinHeld) { Sampler(d).SpinBack(); AnnounceMove("SPIN-BACK", d); RumblePulse(.35, 90); }
                    _spinHeld = on; break;
                }
                case "EchoSend" when deck is int d:
                {
                    int o = GestureOwner(axis, d, v != 0);
                    send[o] = Math.Max(send[o], Math.Abs(v)); if (v < 0) wash[o] = Math.Max(wash[o], -v);
                    break;
                }
                case "EchoThrow" when deck is int d:
                {
                    int o = GestureOwner(axis, d, v > 0);
                    throwAmt[o] = Math.Max(throwAmt[o], v);
                    break;
                }
                case "Riser": riser = Math.Max(riser, Math.Max(0, v)); break;
                default:
                {
                    var step = drv.Update(v, dt);
                    if (step != null) SetControl(deck is int sd ? $"{DeckName(sd)}.{ctl}" : action, (int)step.Value);
                    break;
                }
            }
        }

        // ---- momentary button holds ride on the same paths as their axis versions (full amount while held)
        foreach (var (held, hd) in _holds)
        {
            DeckOf(held, hd, out string hc);
            switch (hc)
            {
                case "EchoThrow": throwAmt[hd] = 1; break;
                case "Reverse": reverse[hd] = true; break;
                case "TimeBend": bend[hd] = 1; break;
                case "Riser": riser = 1; break;
            }
        }
        // ---- controller 2 (FX pad) triggers: echo throw, brake depth, reverse, riser build on its target deck(s)
        for (int d = 0; d < 2; d++)
        {
            throwAmt[d] = Math.Max(throwAmt[d], _fxThrow[d]); bend[d] = Math.Max(bend[d], _fxBend[d]);
            reverse[d] |= _fxReverse[d];
        }
        riser = Math.Max(riser, _fxRiser);

        // ---- Chord bank: the right stick shapes the chord (X inversion, Y voicing) instead of driving the FX pad; Shift+stick is still the FX pad
        if (ModeOf(_bank) == BankMode.Chord && !shift)
        {
            fxPad = false;
            _inv = _invSel.Update(frame.RX);
            _voicing = PadBanks.VoicingFromZone(_voiceSel.Update(frame.RY));
        }
        // a velocity pad held (One-shot / Chord / Drum): RT is the velocity, not the beat roll
        if (VelocityPadHeld()) { roll[0] = roll[1] = 0; }
        // ---- right-stick FX pad: radial deadzone 0.2 + soft curve; the DSP applies the 60 ms smoothing and lets tails ring out
        double amount = fxPad ? FxCurves.Amount(frame.RX, frame.RY, _config.FxDeadzone) : 0;
        amount = Math.Max(amount, _midiFx);          // optional DAW CC (default CC 1) adds FX amount
        double x01 = fxPad ? FxCurves.Param(frame.RX) : .5, y01 = fxPad ? FxCurves.Param(frame.RY) : .5;
        int fo = GestureOwner("FxPad", fxDeck, amount > 0);
        fxAmt[fo] = amount;
        // ---- left-stick FX blend: 60 ms smoothing, glides back to dry on release (tails ring out)
        double kb = 1 - Math.Exp(-dt / 0.06);
        for (int d = 0; d < 2; d++)
        {
            _blendEcho[d] += (blendR[d] - _blendEcho[d]) * kb; if (_blendEcho[d] < 1e-3) _blendEcho[d] = 0;
            _blendVerb[d] += (blendL[d] - _blendVerb[d]) * kb; if (_blendVerb[d] < 1e-3) _blendVerb[d] = 0;
            send[d] = Math.Max(send[d], _blendEcho[d]);
        }
        for (int d = 0; d < 2; d++)
        {
            var (bpmD, phD) = Tempo(d);
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double a = fxAmt[d];
            // the right-stick pad wins; when it rests, the left-stick blend drives the reverb
            bool verb = a <= 0 && _blendVerb[d] > 0;
            _appliedLeftReverb[d] = verb && !_mouseFxHeld[d] && !_mouseFreeze[d] ? _blendVerb[d] : 0;
            if(!_mouseFxHeld[d]&&!_mouseFreeze[d]) _engine.Mixer.Fx(d).State = verb
                ? new PerformanceFx.Controls(PadFx.Reverb, _blendVerb[d], .7, .5, bpmD, phD, _fxFreeze, _config.FxSmoothMs, stamp)
                : new PerformanceFx.Controls(_fx, a, x01, y01, bpmD, phD, _fxFreeze, _config.FxSmoothMs, stamp);
            var sampler = Sampler(d);
            sampler.SetTempo(bpmD, phD);
            sampler.Bend = (float)bend[d]; sampler.Reverse = reverse[d];
            // echo shares the pad: Echo = delay only, Space = the delay share of the Y morph (Y up = reverb, down = delay)
            double padEcho = 0;
            if (a > 0 && _fx == PadFx.Echo) padEcho = a;
            else if (a > 0 && _fx == PadFx.Space) padEcho = a * FxCurves.SpaceMix(y01).Echo;
            // echo throw (legacy axis action): starts on the next 1/4-beat grid line, send drops on release so the tail rings out
            if (throwAmt[d] > 0) { if (_throwAt[d] < 0) _throwAt[d] = nowMs + (long)MsToGrid(d, 0.25); }
            else _throwAt[d] = -1;
            double t = _throwAt[d] >= 0 && nowMs >= _throwAt[d] ? throwAmt[d] : 0;
            double amountE = Math.Max(Math.Max(t, send[d]), padEcho);
            var echo = _engine.Mixer.Echo(d);
            if (amountE > 0 && _sendNow[d] == 0)
                echo.SetBeats(padEcho > 0 ? FxCurves.EchoBeats(x01) : _config.EchoBeats, bpmD);
            echo.Feedback = padEcho > 0 ? (float)FxCurves.EchoFeedback(y01) : (float)(0.55 + 0.3 * wash[d]);
            echo.Send = (float)amountE;
            _sendNow[d] = amountE;
            RollAxis(d, roll[d]);
        }
        // riser (legacy axis action): build over RiserBeats beats of the focused deck, stop quantized to the next beat
        var riserFx = _engine.Mixer.Riser;
        var (rb, rp) = Tempo(_focus);
        if (riser > 0)
        {
            if (!_riserOn) { _riserOn = true; _riserStartMs = nowMs; riserFx.Sync(rb, rp); }
            double beatMs = BeatQuant.BeatMs(rb > 0 ? rb : 120);
            riserFx.Amount = (float)riser;
            riserFx.Build = (float)Math.Clamp((nowMs - _riserStartMs) / (beatMs * Math.Max(1, _config.RiserBeats)), 0, 1);
        }
        else if (_riserOn)
        {
            _riserOn = false;
            riserFx.StopAfter((int)(BeatQuant.MsToNext(rp, rb, 1) * 44.1));
        }

        // ---- on-screen readout, pad hold detection, trigger rumble that follows trigger depth
        _hud.SetFx(_fx, fxAmt[fo], x01, y01, _fxFreeze, fxPad ? fo : _focus);
        double bv = Math.Max(bend[0], bend[1]);
        if (bv > .02) AnnounceMove($"TIME BEND {bv * 100:0}%{(bv > .95 ? " · STOP" : "")}", bend[0] >= bend[1] ? 0 : 1);
        else if (reverse[0] || reverse[1]) AnnounceMove("REVERSE", reverse[0] ? 0 : 1);
        else if (Sampler(_focus).State == 4) AnnounceMove("SPIN UP · RE-LOCK", _focus);
        for (int d = 0; d < 2; d++)
            if (_padHeld[d] >= 0 && !_padLooping[d] && nowMs - _padDownAt[d] > 250)
            { _padLooping[d] = true; Sampler(d).SetLoop(true); AnnounceMove($"PAD {_padHeld[d] + 1} · REPEAT", d); }
        _hud.Tick();
        TriggerRumble(frame);
    }

    /// <summary>RT beat roll: depth 1/4..1/32 by pressure (slip loop on the heard stream); released on the next 1/16 grid line, back to the live beat.</summary>
    private void RollAxis(int d, double v)
    {
        if (v > 0.1)
        {
            double beats = FxCurves.RollBeats(v);
            if (!_axisRoll[d])
            {
                _axisRoll[d] = true; _rollBeatsNow[d] = beats;
                Reader(d).StartRoll((int)(MsToGrid(d, beats) * 44.1), RollFrames(d, beats));
                RumblePulse(.2, 30);
            }
            else if (beats != _rollBeatsNow[d]) { _rollBeatsNow[d] = beats; Reader(d).ResizeRoll(RollFrames(d, beats)); }
            AnnounceMove($"ROLL {FxCurves.BeatsLabel(beats)}", d);
        }
        else if (_axisRoll[d])
        {
            _axisRoll[d] = false;
            int deck = d;
            Schedule(MsToGrid(d, .25), () =>
            {
                if (_axisRoll[deck] || _rollAction[deck] != null) return;
                if (_loopOn[deck]) Reader(deck).StartRoll((int)(MsToGrid(deck, 1) * 44.1), RollFrames(deck, _loopBeats[deck]));
                else Reader(deck).StopRoll();
            });
        }
    }

    /// <summary>Continuous rumble: left motor follows LT depth, right follows RT depth (pulses take priority while they run).</summary>
    private void TriggerRumble(PadFrame frame)
    {
        if (!_config.GamepadRumble || _rumbleUntil != 0) return;
        double l = Math.Round(frame.LT * 0.5, 2), r = Math.Round(frame.RT * 0.5, 2);
        if (l == _trigRumbleL && r == _trigRumbleR) return;
        _trigRumbleL = l; _trigRumbleR = r;
        Gamepad.Rumble(l, r);
    }

    /// <summary>Names the performance move in progress on the readout.</summary>
    private void AnnounceMove(string text, int deck = -1)
    {
        string t = deck >= 0 ? $"{text} · DECK {DeckName(deck)}" : text;
        _hud.SetMove(t); _moveText = t; _moveAt = Environment.TickCount64;
        // the big HUD shows every move too (time bend %, roll size, reverse, spin-back, focus, fader, capture...)
        Toast(PadRole.Mixer, "•", text.ToUpperInvariant(), "", deck < 0 ? 3 : 1 << deck);
    }

    private static bool TryPad(string ctl, string prefix, out int pad)
    {
        pad = -1;
        if (!ctl.StartsWith(prefix) || !int.TryParse(ctl[prefix.Length..], out int n) || n < 1 || n > 4) return false;
        pad = n - 1; return true;
    }

    /// <summary>Sample-bank pad press (1.2 behaviour): retrigger on the quantize grid; held past 250 ms it gate-repeats until release. <paramref name="pad"/> is the pad key (bank * 4 + pad). Returns false for an empty pad.</summary>
    private bool PadPlay(int d, int pad, bool down)
    {
        var s = Sampler(d);
        if (down)
        {
            if (s.Length(pad) == 0)
            {
                // an empty pad is never a dead button: the first press grabs the last beat, the next press plays it
                if (pad / 4 == _bank) PadCapture(d, pad % 4);
                else SetMessage($"Pad {pad % 4 + 1} of bank {pad / 4 + 1} is empty.");
                return false;
            }
            s.Play(pad, FramesToLaunch(d), false);
            _padHeld[d] = pad; _padDownAt[d] = Environment.TickCount64; _padLooping[d] = false;
            AnnounceMove($"PAD {pad % 4 + 1}", d);
        }
        else if (_padHeld[d] == pad)
        {
            _padHeld[d] = -1;
            if (_padLooping[d]) { _padLooping[d] = false; s.StopPad(); }
        }
        return true;
    }

    /// <summary>Shift + pad in a Sample / One-shot bank: capture exactly the last beat, ending on the next beat line of the live deck.</summary>
    private void PadCapture(int d, int pad)
    {
        int key = PadBanks.Index(_bank, pad);
        var (bpm, ph) = Tempo(d);
        double useBpm = bpm > 0 ? bpm : 120;
        Sampler(d).Capture(key, bpm > 0 ? FxCurves.FramesToGrid(ph, bpm, 1) : 0, FxCurves.PadFrames(useBpm));
        Panel(d).Pads.FlashCapture(pad);
        AnnounceMove($"CAPTURE PAD {pad + 1} · BANK {_bank + 1}", d); RumblePulse(.3, 60);
        SetMessage($"Bank {_bank + 1} pad {pad + 1} on Deck {DeckName(d)}: last beat captured on the next beat{(bpm > 0 ? "" : " (no beat found yet: using 120 BPM, unquantized)")}.");
    }

    private void CycleFx()
    {
        _fx = FxCurves.Next(_fx);
        _hud.SetFx(_fx, 0, .5, .5, _fxFreeze, _focus);
        AnnounceMove($"FX → {FxCurves.Name(_fx)}");
        SetMessage($"Right-stick FX: {FxCurves.Name(_fx)}.");
        RumblePulse(.3, 70);
    }

    private void ToggleFxFreeze()
    {
        _fxFreeze = !_fxFreeze;
        AnnounceMove(_fxFreeze ? "REVERB TAIL FROZEN" : "TAIL RELEASED");
        SetMessage(_fxFreeze ? "Reverb tail frozen (Shift+R3 releases it)." : "Reverb tail released.");
        RumblePulse(.25, 60);
    }

    private void ReleaseHeldGestures()
    {
        for (int d = 0; d < 2; d++)
        {
            _filters[d].SetSpring(null); ApplyFilter(d);
            _appliedLeftReverb[d] = _blendEcho[d] = _blendVerb[d] = 0;
            _engine.Mixer.Echo(d).Send = 0; _sendNow[d] = 0; _throwAt[d] = -1;
            if (_rollAction[d] != null) { _rollAction[d] = null; if (!_loopOn[d]) Reader(d).StopRoll(); }
        }
        _axisOwner.Clear();
        if (_riserOn) { _riserOn = false; _engine.Mixer.Riser.StopAfter(0); }
        for (int d = 0; d < 2; d++)
        {
            var sm = Sampler(d); sm.Bend = 0; sm.Reverse = false;
            if (_padLooping[d]) sm.StopPad();
            _padHeld[d] = -1; _padLooping[d] = false;
            _holds.Clear();
            Fx(d).State = Fx(d).State with { Amount = 0 };
            if (_axisRoll[d]) { _axisRoll[d] = false; if (!_loopOn[d]) Reader(d).StopRoll(); }
        }
        _spinHeld = false;
        ReleaseLayers();
        for (int p = 0; p < 4; p++) BankPadUp(p);
        _rig?.ReleaseAll("c1:");
    }

    /// <summary>Soft rumble tick on each beat of the focused deck while a trigger is held or a loop/roll runs.</summary>
    private void BeatTick(PadFrame frame)
    {
        var (_, ph) = Tempo(_focus);
        bool beat = !double.IsNaN(ph) && !double.IsNaN(_beatPrevPhase) && ph < _beatPrevPhase - 0.5;
        _beatPrevPhase = ph;
        bool busy = frame.LT > 0 || frame.RT > 0 || Reader(0).Rolling || Reader(1).Rolling;
        if (beat && busy) RumblePulse(.10, 22, force: false);
    }

    private void Schedule(double inMs, Action act)
    {
        if (inMs <= 0) { act(); return; }
        _scheduled.Add((Environment.TickCount64 + (long)inMs, act));
    }
    private void RunScheduled(long now)
    {
        for (int i = _scheduled.Count - 1; i >= 0; i--)
            if (now >= _scheduled[i].Due) { var a = _scheduled[i].Act; _scheduled.RemoveAt(i); try { a(); } catch (Exception ex) { Log.Write("pad scheduled: " + ex.Message); } }
    }

    private void Panic()
    {
        ApplyAction("MasterMute");
        if (_engine.Mixer.Mute)
        {
            _engine.Mixer.KillFx(); _riserOn = false; _fxFreeze = false;
            for (int d = 0; d < 2; d++) { Reader(d).StopRoll(); _loopOn[d] = false; _rollAction[d] = null; _axisRoll[d] = false; _padHeld[d] = -1; _padLooping[d] = false; }
            _scheduled.Clear(); ResetPadBank(); _holds.Clear(); _rig.Clear(); ReleaseFxPad();
            SetMessage("PANIC: all audio killed. Hold View+Menu again to restore.", true);
        }
        else SetMessage("Audio restored.");
        RumblePulse(.6, 300);
    }

    private static double ParseBeats(string s)
    {
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0) return n / m;
        return double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 0.25;
    }

    private int RollFrames(int d, double beats)
    {
        var (bpm, _) = Tempo(d);
        return Math.Min(DeckReader.MaxLoopFrames, BeatQuant.Frames(beats, bpm));
    }

    /// <summary>Runs one mapped action. down=false is the release of a hold action (loop roll).</summary>
    private void Dispatch(string action, bool down, bool repeat = false)
    {
        int? deck = DeckOf(action, _focus, out string ctl);
        int d = deck ?? _focus;
        if (StickFxDispatch(action, deck, ctl, d, down, repeat)) return;
        if (ctl.StartsWith("Roll:"))
        {
            double beats = ParseBeats(ctl[5..]);
            if (down && !repeat)
            {
                _rollAction[d] = ctl;
                double ms = MsToGrid(d, beats);
                Reader(d).StartRoll((int)(ms * 44.1), RollFrames(d, beats));
                SetMessage($"Loop roll {BeatQuant.BeatsLabel(beats)} on Deck {DeckName(d)}{(Tempo(d).Bpm > 0 ? "" : " (no beat yet: unquantized, 120 BPM)")}.");
            }
            else if (!down && (_rollAction[d] == ctl || (deck == _focus && _rollAction[d = 1 - d] == ctl)))
            {
                _rollAction[d] = null;
                if (_loopOn[d]) Reader(d).StartRoll((int)(MsToGrid(d, 1) * 44.1), RollFrames(d, _loopBeats[d]));
                else Reader(d).StopRoll();
            }
            return;
        }
        if (ctl is "EchoThrow" or "Reverse" or "TimeBend" or "Riser")
        {
            // momentary: full amount while the button is held (ProcessAxes applies it), off on release
            if (down && !repeat) { _holds[action] = d; AnnounceMove(PadOverlay.Describe(ctl), ctl == "Riser" ? -1 : d); }
            else if (!down) _holds.Remove(action);
            return;
        }
        if (deck is null && ctl is "Crossfader-" or "Crossfader+")
        {
            // D-pad crossfader: a tap nudges, holding repeats and accelerates
            if (!down) return;
            _nudgeRepeats = repeat ? _nudgeRepeats + 1 : 0;
            int step = BeatQuant.NudgeStep(_nudgeRepeats);
            _cross.Value = Math.Clamp(_cross.Value + (ctl == "Crossfader+" ? step : -step), 0, 100);
            AnnounceMove($"FADER {_cross.Value}");
            return;
        }
        if (TryPad(ctl, "Pad:", out int padN)) { if (down) BankPadDown(d, padN, false); else BankPadUp(padN); return; }
        if (TryPad(ctl, "PadCapture:", out int capN)) { PadAlt(d, capN, down); return; }
        if (!down) return;
        if (deck is null) { ApplyAction(action); return; }
        if (ctl == "BeatRoll") return;   // analog only (a button roll is Roll:1/4 etc.)
        switch (ctl)
        {
            case "SpinBack":
                if (repeat) break;
                Sampler(d).SpinBack(); AnnounceMove("SPIN-BACK", d); RumblePulse(.35, 90);
                break;
            case "Cue":
            {
                _ = CueToggledAsync(d, !(d == 0 ? _engine.Mixer.CueA : _engine.Mixer.CueB));
                break;
            }
            case "Sync": SetMessage(DeckSync.Sync(_engine, d)); break;
            case "LoopDouble":
            case "LoopHalve":
            {
                _loopBeats[d] = BeatQuant.StepLoop(_loopBeats[d], ctl == "LoopDouble");
                if (_loopOn[d] && _rollAction[d] == null) Reader(d).ResizeRoll(RollFrames(d, _loopBeats[d]));
                SetMessage($"Loop size Deck {DeckName(d)}: {BeatQuant.BeatsLabel(_loopBeats[d])} beat{(_loopBeats[d] > 1 ? "s" : "")}.");
                break;
            }
            case "Loop":
            {
                _loopOn[d] = !_loopOn[d];
                if (_loopOn[d]) Reader(d).StartRoll((int)(MsToGrid(d, 1) * 44.1), RollFrames(d, _loopBeats[d]));
                else if (_rollAction[d] == null) Reader(d).StopRoll();
                SetMessage(_loopOn[d] ? $"Loop {BeatQuant.BeatsLabel(_loopBeats[d])} on Deck {DeckName(d)} (slip: the song keeps running underneath)." : $"Loop off, Deck {DeckName(d)}.");
                break;
            }
            case "BeatJumpBack":
            case "BeatJumpFwd":
            {
                // Chord bank: Shift+D-pad left/right is the manual chord key instead of a beatjump
                if (ModeOf(_bank) == BankMode.Chord) { StepKey(d, ctl == "BeatJumpFwd" ? 1 : -1); break; }
                var src = Source(d);
                if (!src.IsFile) { SetMessage("Beatjump needs a song file. Live apps can't seek; use a loop roll (X/Y) instead."); break; }
                var (bpm, _) = Tempo(d);
                if (bpm <= 0) { SetMessage("Beatjump: no beat found yet."); break; }
                double jump = _loopBeats[d] * 60.0 / bpm * (ctl == "BeatJumpFwd" ? 1 : -1);
                Schedule(MsToGrid(d, 1), () => { if (src.IsFile) src.SeekTo(src.Position + TimeSpan.FromSeconds(jump)); });
                SetMessage($"Beatjump {(jump > 0 ? "+" : "−")}{BeatQuant.BeatsLabel(_loopBeats[d])} beats on the next beat.");
                break;
            }
            case "FilterFineReset": _filters[d].ResetLatch(); ApplyFilter(d); break;
            case "FilterReset": SetFilterLatch(d, 50); break;
            case "EqReset": Panel(d).ResetEq(); break;
            case "TapBpm":
            {
                double bpm = _taps[d].Tap(Environment.TickCount64);
                SetMessage(bpm > 0 ? $"Tap tempo Deck {DeckName(d)}: {bpm:0.0} BPM (used for timing until HARP//MXR hears the beat)." : "Tap tempo: keep tapping on the beat.");
                break;
            }
            case "BassCut":
                Panel(d).ToggleBassCut();
                SetMessage($"Deck {DeckName(d)} bass {(Panel(d).GetParameters().BassCut ? "−24 dB" : "restored")}.");
                break;
            case "LowKill": Panel(d).ToggleKill(0); break;
            case "MidKill": Panel(d).ToggleKill(1); break;
            case "HighKill": Panel(d).ToggleKill(2); break;
            case "EchoFreeze":
            {
                var e = _engine.Mixer.Echo(d); e.Freeze = !e.Freeze;
                SetMessage(e.Freeze ? $"Echo frozen on Deck {DeckName(d)} (Shift+Y again to release)." : "Echo released.");
                break;
            }
            case "PlayPause":
                DeckPlayPause(d);
                break;
            case "BrowsePrev":
            case "BrowseNext":
            {
                var box = Panel(d).Sources;
                if (box.Items.Count == 0) break;
                int i = Math.Clamp((box.SelectedIndex < 0 ? 0 : box.SelectedIndex) + (ctl == "BrowseNext" ? 1 : -1), 0, box.Items.Count - 1);
                box.SelectedIndex = i;   // programmatic: does not arm; Shift+Menu loads it
                SetMessage($"Deck {DeckName(d)}: {box.Items[i]}  (Shift+Menu loads)");
                break;
            }
            case "Load":
            {
                var p = Panel(d);
                _ = SafeAsync(() => d == 0 ? ArmAsync(p, _engine.DeckA, _engine.DeckB, _takeoverA) : ArmAsync(p, _engine.DeckB, _engine.DeckA, _takeoverB));
                break;
            }
            default:
                ApplyAction($"{DeckName(d)}.{ctl}");
                break;
        }
    }
}
