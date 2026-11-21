using MixDock.Platform;
using MixDock.Settings;
int n = 0;
void Assert(bool ok, string t) { if (!ok) throw new Exception("FAIL: " + t); Console.WriteLine("PASS " + t); n++; }
const double dt = 1 / 60.0;
// 1. Typical worn-stick resting noise (up to ~14% diagonal) reads exactly zero.
Assert(Gamepad.Stick(3200, 3200) == (0, 0), "radial deadzone swallows diagonal rest noise");
Assert(Gamepad.Stick(-4000, 1500) == (0, 0), "radial deadzone swallows off-axis rest noise");
// 2. Rescale: just past deadzone is near zero (no jump), full tilt = 1.
var (_, y1) = Gamepad.Stick(0, (short)(32767 * 0.16)); Assert(y1 > 0 && y1 < 0.03, "no jump at deadzone edge");
var (_, y2) = Gamepad.Stick(0, 32767); Assert(Math.Abs(y2 - 1) < 1e-9, "full tilt = 1");
var (_, y3) = Gamepad.Stick(0, -32768); Assert(Math.Abs(y3 + 1) < 1e-9, "-32768 clamps to -1");
// 3. THE DRIFT BUG: filter (absolute) push, hold, release -> returns to 50 and then stops writing.
var f = new AxisDriver(absolute: true); double? last = null;
for (int i = 0; i < 30; i++) last = f.Update(0.8, dt) ?? last;
Assert(last > 85 && last < 92, $"holding stick at 0.8 holds filter steady (~90), got {last:0.0}");
double held = last!.Value; for (int i = 0; i < 60; i++) last = f.Update(0.8, dt) ?? last;
Assert(Math.Abs(last!.Value - held) < 0.5, "held stick does NOT keep moving the filter");
for (int i = 0; i < 30; i++) last = f.Update(0, dt) ?? last;
Assert(Math.Abs(last!.Value - 50) < 0.6, "release recenters filter to 50");
int writes = 0; for (int i = 0; i < 600; i++) if (f.Update(0, dt) != null) writes++;
Assert(writes == 0, "10 s at rest: zero writes (no self-movement, mouse edits untouched)");
// 4. Rate mode stops dead at rest, no leftover remainder bleed.
var r = new AxisDriver(absolute: false); int sum = 0;
for (int i = 0; i < 60; i++) sum += (int)(r.Update(1, dt) ?? 0);
Assert(sum >= 55 && sum <= 61, $"rate full tilt ~60 units/s, got {sum}");
r.Update(0.2, dt); Assert(r.Update(0, dt) == null, "rate mode zero at rest");
int bleed = 0; for (int i = 0; i < 5; i++) bleed += (int)(r.Update(0.16, dt) ?? 0);
Assert(bleed == 0, "tiny tilt after release produces no leftover jump");
// 5. Axis lock: pushing up with slight sideways does not move crossfader.
var fr = Gamepad.Process(new XInputGamepad { LeftThumbX = 9000, LeftThumbY = 30000 });
Assert(Gamepad.AxisLocked(fr, "LeftThumbX") == 0 && Gamepad.AxisLocked(fr, "LeftThumbY") > 0.6, "axis lock blocks crossfader crosstalk");
// 6. Triggers deadzone.
Assert(Gamepad.Trigger(15) == 0 && Gamepad.Trigger(255) == 1, "trigger deadzone + full range");
// ---------- 1.1: fine filter (left stick X) ----------
var fl = new FilterLatch();
fl.Creep(1, dt); Assert(Math.Abs(fl.Latched - 50.08) < 1e-9, $"full tilt creeps 0.08 units per 60 Hz frame, got {fl.Latched - 50:0.#####}");
fl.Load(50); fl.Creep(0.25, dt); Assert(Math.Abs(fl.Latched - (50 + 0.0625 * 0.08)) < 1e-9, "quadratic curve: 1/4 tilt = 1/16 speed (0.005/frame)");
Assert(fl.Latched != Math.Round(fl.Latched, 2), "resolution finer than 0.01 (double precision, no int steps)");
fl.Load(50); fl.Creep(-1, dt); Assert(fl.Latched < 50, "left creeps down");
fl.Load(50); for (int i = 0; i < 60; i++) fl.Creep(1, dt); double after1s = fl.Latched;
for (int i = 0; i < 60; i++) fl.Creep(0, dt); Assert(fl.Latched == after1s && Math.Abs(after1s - 54.8) < 1e-6, "latches: stays at 54.8 after release (no return)");
fl.Load(99.99); for (int i = 0; i < 600; i++) fl.Creep(1, dt); Assert(fl.Latched == 100, "creep clamps at 100");
fl.Load(0.01); for (int i = 0; i < 600; i++) fl.Creep(-1, dt); Assert(fl.Latched == 0, "creep clamps at 0");
// spring + latch: spring rides on top of the latch and returns to it, not to 50
fl.Load(62.5); var spring = new AxisDriver(absolute: true); double? sv = null;
for (int i = 0; i < 30; i++) { sv = spring.Update(0.8, dt); fl.SetSpring(sv); }
Assert(Math.Abs(fl.Effective - Math.Min(100, 62.5 + (sv!.Value - 50))) < 1e-9 && fl.Effective > 95, $"effective = latched + spring offset, got {fl.Effective:0.0}");
Assert(fl.Effective <= 100, "effective clamps to 100");
for (int i = 0; i < 40; i++) fl.SetSpring(spring.Update(0, dt));
Assert(Math.Abs(fl.Effective - 62.5) < 0.6 && fl.Spring is > -0.6 and < 0.6, $"release returns to the LATCH (62.5), got {fl.Effective:0.00}");
fl.SetSpring(spring.Update(0, dt)); Assert(fl.Spring == 0 && fl.Effective == 62.5, "at rest spring offset exactly 0");
fl.Load(10); fl.SetSpring(10); Assert(fl.Effective == 0, "effective clamps to 0 (latch 10, spring -40)");
fl.SetFromKnob(33.3); Assert(Math.Abs(fl.Latched - 73.3) < 1e-9, "mouse edit rewrites latch (minus active spring)");
fl.SetSpring(null); fl.SetFromKnob(40.25); Assert(fl.Latched == 40.25 && fl.Effective == 40.25, "mouse edit at rest: latch = knob value");
fl.ResetLatch(); Assert(fl.Latched == 50, "L3 reset -> latch 50");

// ---------- quantization ----------
Assert(BeatQuant.MsToNext(0.5, 120, 1) == 250, "half a beat at 120 BPM -> 250 ms to next beat");
Assert(Math.Abs(BeatQuant.MsToNext(0.45, 120, 0.25) - 25) < 1e-9 && BeatQuant.MsToNext(0.3, 120, 0.25) == 0, "1/4 grid: phase .45 -> 25 ms to next sixteenth; .3 (25 ms late) fires now");
Assert(BeatQuant.MsToNext(0.02, 120, 1) == 0, "10 ms late hit fires immediately (forgiveness)");
Assert(Math.Abs(BeatQuant.MsToNext(0.1, 120, 1) - 450) < 1e-6, "50 ms late -> waits for next beat");
Assert(BeatQuant.MsToNext(double.NaN, 120, 1) == 0 && BeatQuant.MsToNext(0.5, 0, 1) == 0, "unknown tempo/phase -> unquantized");
Assert(BeatQuant.MsToNext(0.5, 120, 4) == 250, "grids > 1 beat snap to the next beat");
Assert(BeatQuant.Frames(0.25, 120) == 5512 && BeatQuant.Frames(1, 0) == 22050, "roll length frames (1/4 @120 = 125 ms), 120 BPM fallback");
Assert(BeatQuant.StepLoop(4, true) == 8 && BeatQuant.StepLoop(16, true) == 16 && BeatQuant.StepLoop(1.0 / 32, false) == 1.0 / 32, "loop size ladder clamps 1/32..16");
Assert(BeatQuant.BeatsLabel(0.25) == "1/4" && BeatQuant.BeatsLabel(4) == "4", "beat labels");
var tt = new TapTempo(); tt.Tap(0); tt.Tap(500); tt.Tap(1000); tt.Tap(1500);
Assert(Math.Abs(tt.Bpm - 120) < 1e-9 && Math.Abs(tt.Phase(1750) - 0.5) < 1e-9, "tap tempo 120 BPM, phase anchored on last tap");
tt.Tap(5000); Assert(Math.Abs(tt.Bpm - 120) < 1e-9, "a tap after a 2 s pause starts over but keeps last tempo");
tt.Tap(5600); Assert(Math.Abs(tt.Bpm - 100) < 1e-9, "new tap pair -> 100 BPM");

// ---------- mapper: every mapping of the "Pad performer" preset (the 1.3 layout; mapper mechanics) ----------
var cfg = new AppConfig { ConfigVersion = AppConfig.CurrentVersion }; cfg.ApplyPreset("Pad performer");
long t = 1000;
List<PadEvent> Tap(PadMapper m, PadButton b, PadButton hold = 0)
{
    var ev = new List<PadEvent>();
    ev.AddRange(m.Update(hold | b, t += 20)); ev.AddRange(m.Update(hold | b, t += 50)); ev.AddRange(m.Update(hold, t += 20));
    return ev;
}
string Fired(List<PadEvent> ev) => string.Join(",", ev.Where(e => e.Kind == PadEventKind.Press).Select(e => e.Action));
var normal = new (PadButton, string)[] {
    (PadButton.A, "Focus.Pad:1"), (PadButton.B, "Focus.Pad:2"), (PadButton.X, "Focus.Pad:3"), (PadButton.Y, "Focus.Pad:4"),
    (PadButton.LeftShoulder, "FocusA"), (PadButton.RightShoulder, "FocusB"),
    (PadButton.Up, "Bank:1"), (PadButton.Down, "Bank:3"), (PadButton.Left, "Bank:4"), (PadButton.Right, "Bank:2"),
    (PadButton.LeftThumb, "Focus.FilterFineReset"), (PadButton.RightThumb, "FxCycle"), (PadButton.Back, "Focus.TapBpm"), (PadButton.Start, "AutoMix") };
foreach (var (b, want) in normal) { var m = cfg.CreatePadMapper(); Assert(Fired(Tap(m, b)) == want, $"default {b} -> {want}"); }
var shifted = new (PadButton, string)[] {
    (PadButton.A, "Focus.PadCapture:1"), (PadButton.B, "Focus.PadCapture:2"), (PadButton.X, "Focus.PadCapture:3"), (PadButton.Y, "Focus.PadCapture:4"),
    (PadButton.LeftShoulder, "QuantCycle"), (PadButton.RightShoulder, "Focus.Sync"),
    (PadButton.Up, "Focus.Loop"), (PadButton.Down, "Focus.Cue"), (PadButton.Left, "Focus.BeatJumpBack"), (PadButton.Right, "Focus.BeatJumpFwd"),
    (PadButton.LeftThumb, "Focus.EqReset"), (PadButton.RightThumb, "FxFreeze"), (PadButton.Start, "PadGrid") };
foreach (var (b, want) in shifted)
{
    var m = cfg.CreatePadMapper(); m.Update(PadButton.Back, t += 20); m.Update(PadButton.Back, t += 100);
    var ev = Tap(m, b, PadButton.Back); ev.AddRange(m.Update(0, t += 20));
    Assert(Fired(ev) == want && ev.Where(e => e.Kind == PadEventKind.Press).All(e => e.Shift), $"shift(View) + {b} -> {want}, View tap suppressed");
}
{   // LB+RB chord = shift; neither focus fires
    var m = cfg.CreatePadMapper(); var lr = PadButton.LeftShoulder | PadButton.RightShoulder;
    var ev = m.Update(PadButton.LeftShoulder, t += 20); ev.AddRange(m.Update(lr, t += 30)); Assert(m.Shift, "LB+RB held = shift");
    ev.AddRange(Tap(m, PadButton.A, lr)); ev.AddRange(Tap(m, PadButton.Back, lr)); ev.AddRange(m.Update(PadButton.RightShoulder, t += 20)); ev.AddRange(m.Update(0, t += 20));
    Assert(Fired(ev) == "Focus.PadCapture:1,BankMode", $"LB+RB chord: A -> pad capture, View -> bank mode; no FocusA/FocusB (got {Fired(ev)})");
}
{   // View held alone then released = nothing; long View hold is not a tap
    var m = cfg.CreatePadMapper(); var ev = m.Update(PadButton.Back, t += 20); ev.AddRange(m.Update(PadButton.Back, t += 600)); ev.AddRange(m.Update(0, t += 20));
    Assert(ev.Count == 0 && !m.Shift, "View held 600 ms alone: shift only, no tap BPM");
}
{   // panic
    var m = cfg.CreatePadMapper(); var bs = PadButton.Back | PadButton.Start; var ev = new List<PadEvent>();
    ev.AddRange(m.Update(PadButton.Back, t += 20)); long t0 = t += 20; ev.AddRange(m.Update(bs, t0));
    ev.AddRange(m.Update(bs, t0 + 480)); Assert(!ev.Any(e => e.Kind == PadEventKind.Panic), "panic not before 500 ms");
    ev.AddRange(m.Update(bs, t0 + 500)); ev.AddRange(m.Update(bs, t0 + 900)); ev.AddRange(m.Update(0, t0 + 920));
    Assert(ev.Count(e => e.Kind == PadEventKind.Panic) == 1, "Back+Start 500 ms -> exactly one panic");
    Assert(!ev.Any(e => e.Kind == PadEventKind.Press), "panic chord fires neither TapBpm, AutoMix nor shift-Load");
    t = t0 + 1000; m.Update(PadButton.Back, t += 20); var ev2 = Tap(m, PadButton.Start, PadButton.Back);
    Assert(Fired(ev2) == "PadGrid", "quick Menu tap while View held = shift pad grid (not panic)");
}
{   // hold actions get a release (loop roll)
    var m = cfg.CreatePadMapper(); var ev = Tap(m, PadButton.X);
    Assert(ev.Select(e => e.Kind).SequenceEqual(new[] { PadEventKind.Press, PadEventKind.Release }) && ev.All(e => e.Action == "Focus.Pad:3"), "sample pad: press + release events (hold = gate-repeat)");
    // shift released before the button: release still pairs with the shift action
    m.Update(PadButton.Back, t += 20); var e1 = m.Update(PadButton.Back | PadButton.Y, t += 20); var e2 = m.Update(PadButton.Y, t += 20); var e3 = m.Update(0, t += 20);
    Assert(e1.Single().Action == "Focus.PadCapture:4" && e3.Single().Action == "Focus.PadCapture:4" && e2.Count == 0, "release pairs with the action pressed (shift latched per press)");
}
{   // debounce + held repeat
    var m = cfg.CreatePadMapper(); var ev = m.Update(PadButton.A, t += 20); ev.AddRange(m.Update(0, t += 10)); ev.AddRange(m.Update(PadButton.A, t += 10));
    Assert(ev.Count(e => e.Kind == PadEventKind.Press) == 1, "contact bounce within 40 ms ignored");
    m.Update(0, t += 100);
    m.Update(PadButton.Back, t += 20); var rep = m.Update(PadButton.Back | PadButton.Up, t += 20); long p0 = t;
    for (int i = 1; i <= 50; i++) rep.AddRange(m.Update(PadButton.Back | PadButton.Up, p0 + i * 16));
    int reps = rep.Count(e => e.Kind == PadEventKind.Repeat);
    Assert(rep[0].Action == "Focus.Loop" && reps == 0, "Shift+Up (loop) does not repeat");
    { var tm = new PadMapper(new Dictionary<string, string>(), new Dictionary<string, string> { ["Up"] = "Focus.Trim+" }); tm.Update(PadButton.Back, p0 + 3000); var tr = tm.Update(PadButton.Back | PadButton.Up, p0 + 3020); for (int i = 1; i <= 50; i++) tr.AddRange(tm.Update(PadButton.Back | PadButton.Up, p0 + 3020 + i * 16));
      reps = tr.Count(e => e.Kind == PadEventKind.Repeat); Assert(tr[0].Action == "Focus.Trim+" && reps >= 6 && reps <= 8, $"held Trim+ still repeats after 350 ms every 60 ms when mapped (got {reps} in 800 ms)"); }
    t = p0 + 2000;
    var x2 = cfg.CreatePadMapper(); var xr = x2.Update(PadButton.Up, t); for (int i = 1; i < 60; i++) xr.AddRange(x2.Update(PadButton.Up, t + i * 16));
    Assert(!xr.Any(e => e.Kind == PadEventKind.Repeat), "Bank select does not auto-repeat");
    t += 2000;
}
{   // reconnect: held buttons don't fire
    var m = cfg.CreatePadMapper(); m.Reset(PadButton.A); Assert(m.Update(PadButton.A, t += 20).Count == 0, "reset: already-held button does not fire");
}
// ---------- config: remap + migration ----------
var custom = new AppConfig { GamepadButtons = new() { ["A"] = "MasterMute" }, ShiftButtons = new(), ShiftButton = "RightThumb" };
var cm = custom.CreatePadMapper();
Assert(Fired(Tap(cm, PadButton.A)) == "MasterMute" && Fired(Tap(cm, PadButton.B)) == "", "remapped buttons dispatch from config");
var oldJson = """{"GamepadAxes":{"LeftThumbY":"A.Filter","RightThumbY":"B.Filter","LeftThumbX":"Crossfader"},"GamepadButtons":{"A":"A.FilterReset","B":"B.FilterReset","X":"A.Mute","Y":"B.Mute","LeftShoulder":"Crossfader-","RightShoulder":"Crossfader+","Start":"MasterMute"},"Master":70}""";
var mig = AppConfig.FromJson(oldJson);
Assert(mig.GamepadAxes["LeftThumbX"] == "Focus.FxBlend" && mig.GamepadAxes["RightThumbX"] == "Focus.FxX" && mig.GamepadButtons["X"] == "Focus.Roll:1/4"
    && mig.ShiftButtons.Count == 14 && mig.Master == 70 && mig.ConfigVersion == AppConfig.CurrentVersion, "1.0 default config migrates to the Live DJ defaults, keeps other settings");
var mig2 = AppConfig.FromJson("""{"GamepadAxes":{"LeftThumbY":"B.Filter","LeftThumbX":"Crossfader","RightTrigger":"Master"},"GamepadButtons":{"A":"B.Mute"}}""");
Assert(mig2.GamepadAxes["LeftThumbX"] == "B.FilterFine" && mig2.GamepadAxes["RightThumbX"] == "Focus.FxX" && mig2.GamepadAxes["RightTrigger"] == "Master"
    && mig2.GamepadButtons.Count == 1, "custom 1.0 map: only LeftThumbX=Crossfader moves, the rest is kept");
var mig3 = AppConfig.FromJson("""{"ConfigVersion":2,"GamepadAxes":{"LeftThumbX":"Crossfader"}}""");
Assert(mig3.GamepadAxes["LeftThumbX"] == "Crossfader", "a 1.1 user who deliberately maps LeftThumbX=Crossfader is left alone");
Assert(AppConfig.FromJson("{}").GamepadAxes["LeftThumbX"] == "Focus.FxBlend", "empty config gets the Live DJ defaults (left stick X = FX blend)");
{   // ConfigVersion 3 migration (1.1 -> 1.2)
    var v2 = AppConfig.FromJson("""{"ConfigVersion":2,"GamepadAxes":{"LeftThumbY":"Focus.Filter","LeftThumbX":"Focus.FilterFine","RightThumbX":"Crossfader","RightThumbY":"Focus.EchoSend","LeftTrigger":"Focus.EchoThrow","RightTrigger":"Riser"},"Master":61}""");
    Assert(v2.ConfigVersion == AppConfig.CurrentVersion && v2.GamepadAxes["RightThumbX"] == "Focus.FxX" && v2.GamepadAxes["RightThumbY"] == "Focus.FxY" && v2.GamepadAxes["LeftTrigger"] == "Focus.EchoThrow"
        && v2.GamepadAxes["RightTrigger"] == "Focus.BeatRoll" && v2.ShiftAxes["RightTrigger"] == "Focus.Reverse" && v2.ShiftAxes["LeftTrigger"] == "Focus.TimeBend" && v2.Master == 61, "1.1 default axes migrate through 1.3 to the Live DJ mapping");
    Assert(v2.GamepadAxes["RightThumbX"] != "Crossfader" && v2.GamepadAxes["LeftThumbX"] == "Focus.FxBlend" && v2.GamepadButtons["Left"] == "Crossfader-", "crossfader moved from the right stick to the D-pad (left stick X = FX blend)");
    var v2c = AppConfig.FromJson("""{"ConfigVersion":2,"GamepadAxes":{"RightThumbX":"Crossfader","RightThumbY":"B.Trim","RightTrigger":"Master"},"GamepadButtons":{"RightThumb":"Focus.Loop","A":"MasterMute"}}""");
    Assert(v2c.GamepadAxes["RightThumbX"] == "Focus.FxX" && v2c.GamepadAxes["RightThumbY"] == "B.Trim" && v2c.GamepadAxes["RightTrigger"] == "Master" && v2c.GamepadButtons["RightThumb"] == "FxCycle" && v2c.GamepadButtons["A"] == "MasterMute", "customised 1.1 map keeps user bindings, only crossfader-on-RS and R3 loop move");
    var v3 = AppConfig.FromJson("""{"ConfigVersion":3,"GamepadAxes":{"RightThumbX":"Crossfader"}}""");
    Assert(v3.GamepadAxes["RightThumbX"] == "Crossfader", "a 1.2 config is never migrated again");
    Assert(new AppConfig().FxDeadzone == 0.2 && new AppConfig().FxSmoothMs == 60, "FX pad defaults: deadzone 0.2, 60 ms smoothing");
}
Assert(PadMapper.Parse("LeftShoulder+RightShoulder") == (PadButton.LeftShoulder | PadButton.RightShoulder) && PadMapper.Parse("Nope") is null, "chord parsing");
// v1.1 filter feel: deadzone 0.22, expo 0.6, 60 ms smoothing
{
    var (_, ry) = Gamepad.Stick(0, (short)(32767 * 0.20));
    Assert(Gamepad.FilterCurve(ry, 0.22, 0.6) == 0, "filter deadzone 0.22 ignores a 20% nudge");
    var (_, fy) = Gamepad.Stick(0, 32767);
    Assert(Math.Abs(Gamepad.FilterCurve(fy, 0.22, 0.6) - 1) < 1e-9 && Math.Abs(Gamepad.FilterCurve(-fy, 0.22, 0.6) + 1) < 1e-9, "filter curve full tilt = +/-1");
    var (_, hy) = Gamepad.Stick(0, (short)(32767 * 0.61));
    double half = Gamepad.FilterCurve(hy, 0.22, 0.6); // x=0.5 -> 0.6*0.125+0.4*0.5 = 0.275
    Assert(Math.Abs(half - 0.275) < 0.01, $"expo: half tilt gives 0.275, got {half:0.000}");
    Assert(Gamepad.FilterCurve(hy, 0.22, 0) > half, "expo 0 is linear (more sensitive)");
    double prev = 0; bool mono = true;
    for (int k = 23; k <= 100; k++) { var (_, sy) = Gamepad.Stick(0, (short)(32767 * k / 100.0)); double c = Gamepad.FilterCurve(sy, 0.22, 0.6); if (c < prev) mono = false; prev = c; }
    Assert(mono, "filter curve monotonic (no jumps)");
    var slow = new AxisDriver(true, 60); var fast = new AxisDriver(true, 35);
    double s1 = slow.Update(1, 1 / 60.0)!.Value, f1 = fast.Update(1, 1 / 60.0)!.Value;
    Assert(s1 < f1 && s1 > 50, "60 ms smoothing is gentler than 35 ms");
    double? lv = null; for (int i = 0; i < 60; i++) lv = slow.Update(0, 1 / 60.0) ?? lv;
    Assert(Math.Abs(lv!.Value - 50) < 0.6, "60 ms smoothing still settles and stops at rest");
    int w = 0; for (int i = 0; i < 300; i++) if (slow.Update(0, 1 / 60.0) != null) w++;
    Assert(w == 0, "no writes at rest with 60 ms smoothing");
}
// controller hints are built from the live bindings
{
    var c = new AppConfig();
    string sh = PadHints.For(true, c.ShiftButtons), nh = PadHints.For(false, c.GamepadButtons);
    Assert(sh.StartsWith("SHIFT: ") && sh.Contains("Menu Play / pause") && sh.Contains("D← Mid kill") && sh.Contains("D↑ Next pad bank"), "shift hint lists pause, kills and banks");
    Assert(nh.Contains("D← fader→A") && nh.Contains("D→ fader→B") && !nh.StartsWith("SHIFT"), "normal hint shows the D-pad crossfader");
    c.ShiftButtons["Start"] = "PauseAll";
    Assert(PadHints.For(true, c.ShiftButtons).Contains("Menu Pause / resume all decks"), "a remapped button shows its new action");
    Assert(PadHints.Connected(2).Contains("Controller 2") && PadHints.Connected(2).Contains("Shift+F1"), "connect hint points at the full map");
}
// 1.5 remap: D-pad crossfader with acceleration, left stick FX blend, migration of an untouched 1.4 layout only
{
    var c = new AppConfig();
    Assert(c.GamepadButtons["Left"] == "Crossfader-" && c.GamepadButtons["Right"] == "Crossfader+", "D-pad left/right = crossfader");
    Assert(c.GamepadAxes["LeftThumbY"] == "Focus.Filter" && c.GamepadAxes["LeftThumbX"] == "Focus.FxBlend", "left stick: Y filter, X FX blend");
    Assert(c.GamepadAxes["RightThumbX"] == "Focus.FxX" && c.GamepadAxes["RightThumbY"] == "Focus.FxY", "right stick keeps the FX pad");
    Assert(c.ShiftButtons["Left"] == "Focus.MidKill" && c.ShiftButtons["Right"] == "Focus.HighKill" && c.ShiftButtons["Up"] == "BankNext" && c.ShiftButtons["Down"] == "BankPrev", "mid/high kill and bank select move to Shift+D-pad");
    Assert(c.ShiftButtons["Start"] == "Focus.PlayPause", "Shift+Menu pauses the focus deck");
    Assert(BeatQuant.NudgeStep(0) == 2 && BeatQuant.NudgeStep(3) == 3 && BeatQuant.NudgeStep(100) == 6, "crossfader nudge: tap 2, accelerates to 6");
    var m = c.CreatePadMapper(); var evs = new List<PadEvent>(); long tm = 0;
    for (int i = 0; i < 40; i++) evs.AddRange(m.Update(PadButton.Right, tm += 20));
    Assert(evs.Count(e => e.Action == "Crossfader+" && e.Kind == PadEventKind.Press) == 1 && evs.Count(e => e.Action == "Crossfader+" && e.Kind == PadEventKind.Repeat) >= 5, "holding D-pad right repeats the crossfader nudge");
    var v5 = new AppConfig { ConfigVersion = 5, GamepadButtons = AppConfig.V5Buttons(), ShiftButtons = AppConfig.V5ShiftButtons() };
    v5.GamepadAxes["LeftThumbX"] = "Crossfader";
    var up = AppConfig.FromJson(System.Text.Json.JsonSerializer.Serialize(v5));
    Assert(up.GamepadButtons["Left"] == "Crossfader-" && up.GamepadAxes["LeftThumbX"] == "Focus.FxBlend" && up.ShiftButtons["Left"] == "Focus.MidKill" && up.ConfigVersion == AppConfig.CurrentVersion, "untouched 1.4 layout migrates to the 1.5 remap");
    v5.GamepadButtons["A"] = "MasterMute";
    var kept = AppConfig.FromJson(System.Text.Json.JsonSerializer.Serialize(v5));
    Assert(kept.GamepadButtons["Left"] == "Focus.MidKill" && kept.GamepadAxes["LeftThumbX"] == "Crossfader" && kept.GamepadButtons["A"] == "MasterMute", "an edited 1.4 layout is left alone");
    Assert(kept.Hotkeys["Ctrl+Alt+Shift+Space"] == "PauseAll", "pause-all hotkey added on migration");
}
// look-ahead config: default 10 s, clamped 0..30, survives a save/load round trip
{
    var c = new AppConfig();
    Assert(c.LookAhead(0) == 10 && c.LookAhead(1) == 10, "look-ahead defaults to 10 s per deck");
    c.SetLookAhead(0, 0); c.SetLookAhead(1, 99);
    Assert(c.LookAhead(0) == 0 && c.LookAhead(1) == 30, "look-ahead clamps to 0..30 and allows 0 per deck");
    var back = AppConfig.FromJson(System.Text.Json.JsonSerializer.Serialize(c));
    Assert(back.LookAhead(0) == 0 && back.LookAhead(1) == 30, "look-ahead persists through config.json");
    Assert(AppConfig.FromJson("""{"ConfigVersion":6,"LookAheadSec":[-5]}""").LookAhead(1) == 10 && AppConfig.FromJson("""{"ConfigVersion":6,"LookAheadSec":[-5]}""").LookAhead(0) == 0, "broken look-ahead values are repaired");
}
// media session matching: the deck's captured process finds its Windows media session
{
    var s = new List<MediaSessionInfo> { new("chrome.exe", false), new("Spotify.exe", true), new("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", false), new("msedge.exe", true), new("308046B0AF4A39CB", true) };
    Assert(MediaSessionMatch.Pick(s, "Spotify", wantPause: true) == 1, "Spotify desktop: exact exe, playing session chosen to pause");
    Assert(MediaSessionMatch.Pick(s, "Spotify", wantPause: false) == 2, "resume prefers the paused Spotify session");
    Assert(MediaSessionMatch.Pick(s, "chrome", true) == 0 && MediaSessionMatch.Pick(s, "msedge.exe", true) == 3, "browsers match by exe name");
    Assert(MediaSessionMatch.Pick(s, "firefox", true) == -1 && MediaSessionMatch.Pick(s, "", true) == -1, "no session: -1 (deck freezes instead)");
    Assert(MediaSessionMatch.Score("Vivaldi.ABC123", "vivaldi") == 2 && MediaSessionMatch.Score("Spotify.exe", "spot") == 0, "packaged ids match a whole part; short substrings never match");
    var two = new List<MediaSessionInfo> { new("chrome.exe", false), new("chrome.exe", true) };
    Assert(MediaSessionMatch.Pick(two, "chrome", true) == 1, "two browser sessions: pause hits the one that is playing");
}
Console.WriteLine($"{n} gamepad tests passed");
