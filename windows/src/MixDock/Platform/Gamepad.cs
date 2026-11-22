using System.Runtime.InteropServices;

namespace MixDock.Platform;

/// <summary>Controller buttons. The low 16 bits are XInput's own; Extra1..Extra8 are inputs XInput does not report
/// (back paddles, extra top buttons) read through Windows.Gaming.Input (see <see cref="ExtraButtons"/>).</summary>
[Flags]
public enum PadButton : uint
{
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
    Extra1 = 0x10000, Extra2 = 0x20000, Extra3 = 0x40000, Extra4 = 0x80000,
    Extra5 = 0x100000, Extra6 = 0x200000, Extra7 = 0x400000, Extra8 = 0x800000,
}
[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    /// <summary>XInput's 16-bit button word. Must stay 2 bytes: the triggers and sticks follow it in the native struct.</summary>
    public ushort RawButtons;
    public byte LeftTrigger, RightTrigger;
    public short LeftThumbX, LeftThumbY, RightThumbX, RightThumbY;
    public PadButton Buttons { readonly get => (PadButton)RawButtons; set => RawButtons = (ushort)((uint)value & 0xFFFF); }
}
[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}
[StructLayout(LayoutKind.Sequential)]
public struct XInputVibration { public ushort Left, Right; }

public enum PadRole { Mixer = 0, FxPad = 1 }

/// <summary>Raw (Windows.Gaming.Input) buttons -> Extra1..Extra8. Pure; unit-tested.</summary>
public static class ExtraButtonLogic
{
    /// <summary>Raw buttons 0-9 of an Xbox-layout controller are A B X Y LB RB View Menu L3 R3; raw 10+ map to Extra1+.</summary>
    public const int StandardCount = 10, MaxExtra = 8;
    /// <summary>The first press of a raw button is held back one poll: if an XInput button went down with it (now or on the
    /// next poll) it is a duplicate of a standard button (a paddle the firmware maps to A, say) and ignored from then on;
    /// otherwise it is confirmed as an extra input.</summary>
    public static PadButton Classify(bool[] buttons, bool[] previous, HashSet<int> aliases, HashSet<int> confirmed, ref int pending, ref PadButton lastXInput, PadButton xinput)
    {
        var gained = xinput & ~lastXInput & (PadButton)0xFFFF;
        if (pending >= 0) { if (gained != 0) aliases.Add(pending); else confirmed.Add(pending); pending = -1; }
        PadButton extra = 0;
        for (int i = StandardCount; i < buttons.Length && i - StandardCount < MaxExtra; i++)
        {
            bool down = buttons[i], was = previous[i];
            if (down && !was && !aliases.Contains(i) && !confirmed.Contains(i))
            {
                if (gained != 0) aliases.Add(i); else pending = i;
            }
            if (down && confirmed.Contains(i)) extra |= (PadButton)((uint)PadButton.Extra1 << (i - StandardCount));
            previous[i] = down;
        }
        lastXInput = xinput;
        return extra;
    }
}

/// <summary>Which XInput slot is controller 1 (mixer) and which is controller 2 (FX pad). Pure, unit-tested.</summary>
public static class ControllerRoles
{
    /// <summary>
    /// - A pad that disconnects frees its role; the other pad keeps its role (nothing is re-routed).
    /// - Roles are only (re)decided when a pad arrives. One pad connected: it is the mixer.
    /// - Two or more: the pad matching <paramref name="fxPadDevice"/> is the FX pad, one matching <paramref name="mixerDevice"/>
    ///   the mixer; otherwise existing roles stay and a newcomer takes the free role (mixer first). Ties go to the lower slot.
    /// </summary>
    public static int[] Assign(bool[] connected, string?[] ids, int[] previous, string fxPadDevice, string mixerDevice, bool arrived)
    {
        var roles = new int[connected.Length];
        for (int i = 0; i < roles.Length; i++) roles[i] = connected[i] && i < previous.Length ? previous[i] : -1;
        if (!arrived) return roles;
        var on = Enumerable.Range(0, connected.Length).Where(i => connected[i]).ToList();
        if (on.Count == 0) return roles;
        bool Is(int i, string want) => want.Length > 0 && string.Equals(ids[i], want, StringComparison.OrdinalIgnoreCase);
        if (on.Count == 1) { if (roles[on[0]] < 0) roles[on[0]] = (int)PadRole.Mixer; return roles; }
        int Holder(PadRole r) => on.FirstOrDefault(i => roles[i] == (int)r, -1);
        var fxMatch = on.Where(i => Is(i, fxPadDevice)).ToList();
        var mixMatch = on.Where(i => Is(i, mixerDevice)).ToList();
        int fx = fxMatch.Count == 1 ? fxMatch[0] : -1;
        int mix = mixMatch.Count == 1 && mixMatch[0] != fx ? mixMatch[0] : -1;
        if (mix < 0) { int h = Holder(PadRole.Mixer); if (h >= 0 && h != fx) mix = h; }
        if (fx < 0) { int h = Holder(PadRole.FxPad); if (h >= 0 && h != mix) fx = h; }
        if (mix < 0) mix = on.FirstOrDefault(i => i != fx, -1);
        if (fx < 0) fx = on.FirstOrDefault(i => i != mix, -1);
        for (int i = 0; i < roles.Length; i++) roles[i] = i == mix ? (int)PadRole.Mixer : i == fx ? (int)PadRole.FxPad : -1;
        return roles;
    }
}

/// <summary>Processed, drift-free controller frame. Sticks use a radial deadzone with rescale.</summary>
public readonly record struct PadFrame(double LX, double LY, double RX, double RY, double LT, double RT, PadButton Buttons);

public static class Gamepad
{
    public const double StickDeadzone = 0.15;   // radial, per stick
    public const double TriggerDeadzone = 0.08;
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    private static extern uint GetStateNative(uint index, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState", ExactSpelling = true)]
    private static extern uint SetStateNative(uint index, ref XInputVibration vib);

    // ---- two controllers: every connected XInput slot is read; each role (mixer, FX pad) owns at most one slot and keeps it
    // until that pad disconnects, so unplugging one never re-routes the other. Assignment is by device identity (VID:PID).
    private static readonly XInputState[] _states = new XInputState[4];
    private static readonly bool[] _connected = new bool[4];
    private static readonly string[] _ids = new string[4];
    private static int[] _roles = { -1, -1, -1, -1 };
    private static long _nextScanTick, _lastPoll = -1;
    /// <summary>Identity preferences (from config): this device is the FX pad / the mixer whenever two pads are connected.</summary>
    public static string FxPadDevice = "045E:0B13", MixerDevice = "";
    /// <summary>Reads controller identity for a slot ("VID:PID"); replaceable in tests.</summary>
    public static Func<int, string> IdentitySource = SlotIdentity;

    [StructLayout(LayoutKind.Sequential)]
    private struct CapabilitiesEx
    {
        public byte Type, SubType; public ushort Flags;
        public XInputGamepad Gamepad; public XInputVibration Vibration;
        public ushort VendorId, ProductId, VersionNumber, Unknown1; public uint Unknown2;
    }
    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint GetCapabilitiesEx(uint unknown, uint index, uint flags, out CapabilitiesEx caps);
    /// <summary>"VID:PID" of an XInput slot (undocumented XInputGetCapabilitiesEx, as SDL uses it); "" when unknown.</summary>
    public static string SlotIdentity(int slot)
    {
        try { return GetCapabilitiesEx(1, (uint)slot, 0, out var c) == 0 ? $"{c.VendorId:X4}:{c.ProductId:X4}" : ""; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }
    /// <summary>Extra (non-XInput) buttons of a slot; set by the app (Windows.Gaming.Input), null = none.</summary>
    public static Func<int, string, PadButton, PadButton>? ExtraSource;
    /// <summary>XInput reader; replaceable in tests (slot, out state) → connected.</summary>
    public static Func<int, (bool Ok, XInputState State)> StateSource = slot =>
    {
        try { return (GetStateNative((uint)slot, out var s) == 0, s); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return (false, default); }
    };

    public static int Slot => SlotOf(PadRole.Mixer);
    public static int SlotOf(PadRole role) => Array.IndexOf(_roles, (int)role);
    public static string IdentityOf(PadRole role) { int s = SlotOf(role); return s >= 0 ? _ids[s] ?? "" : ""; }
    public static int ConnectedCount => _connected.Count(c => c);

    /// <summary>Re-reads every assigned or connected slot (at most once per millisecond tick); empty slots are probed once a second
    /// because XInputGetState on an empty slot is slow.</summary>
    public static void Poll(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now == _lastPoll) return;
        _lastPoll = now;
        bool scan = force || now >= _nextScanTick, arrived = false;
        if (scan) _nextScanTick = now + 1000;
        for (int i = 0; i < 4; i++)
        {
            if (!_connected[i] && !scan) continue;
            var (ok, st) = StateSource(i);
            if (ok && !_connected[i]) { _ids[i] = IdentitySource(i); arrived = true; }
            _connected[i] = ok; _states[i] = ok ? st : default;
        }
        _roles = ControllerRoles.Assign(_connected, _ids, _roles, FxPadDevice, MixerDevice, arrived);
    }

    /// <summary>Controller 1 (the mixer).</summary>
    public static bool Read(out XInputState state) => Read(PadRole.Mixer, out state);
    public static bool Read(PadRole role, out XInputState state)
    {
        Poll();
        int s = SlotOf(role);
        state = s >= 0 ? _states[s] : default;
        return s >= 0;
    }
    /// <summary>Processed frame of a role, including extra buttons (paddles) when the app provides them.</summary>
    public static bool ReadFrame(PadRole role, out PadFrame frame, out XInputState state)
    {
        frame = default;
        if (!Read(role, out state)) return false;
        frame = Process(state.Gamepad);
        int s = SlotOf(role);
        if (ExtraSource != null && s >= 0)
        {
            try { frame = frame with { Buttons = frame.Buttons | (ExtraSource(s, _ids[s] ?? "", frame.Buttons) & ExtraMask) }; } catch { }
        }
        return true;
    }
    public const PadButton ExtraMask = (PadButton)0xFF0000;

    /// <summary>Swap mixer and FX pad (both connected): remembers the identities so the choice sticks.</summary>
    public static bool SwapRoles()
    {
        int m = SlotOf(PadRole.Mixer), f = SlotOf(PadRole.FxPad);
        if (m < 0 || f < 0) return false;
        _roles[m] = (int)PadRole.FxPad; _roles[f] = (int)PadRole.Mixer;
        if (!string.Equals(_ids[m], _ids[f], StringComparison.OrdinalIgnoreCase)) { FxPadDevice = _ids[m] ?? ""; MixerDevice = _ids[f] ?? ""; }
        return true;
    }

    /// <summary>Test hook: forget all slots.</summary>
    public static void ResetForTests() { Array.Clear(_connected); Array.Clear(_ids); _roles = new[] { -1, -1, -1, -1 }; _nextScanTick = 0; _lastPoll = -1; }

    public static void Rumble(double left, double right) => Rumble(PadRole.Mixer, left, right);
    public static void Rumble(PadRole role, double left, double right)
    {
        int slot = SlotOf(role);
        if (slot < 0) return;
        try
        {
            var v = new XInputVibration { Left = (ushort)(Math.Clamp(left, 0, 1) * 65535), Right = (ushort)(Math.Clamp(right, 0, 1) * 65535) };
            SetStateNative((uint)slot, ref v);
        }
        catch { }
    }

    /// <summary>Radial deadzone + rescale: inside the circle = exactly 0; outside maps smoothly 0..1 with no jump.</summary>
    public static (double X, double Y) Stick(short rawX, short rawY, double deadzone = StickDeadzone)
    {
        double x = Math.Max(rawX, (short)-32767) / 32767.0, y = Math.Max(rawY, (short)-32767) / 32767.0;
        double mag = Math.Sqrt(x * x + y * y);
        if (mag <= deadzone) return (0, 0);
        double scaled = Math.Min(1, (mag - deadzone) / (1 - deadzone));
        return (x / mag * scaled, y / mag * scaled);
    }

    public static double Trigger(byte raw, double deadzone = TriggerDeadzone)
    {
        double t = raw / 255.0;
        return t <= deadzone ? 0 : Math.Min(1, (t - deadzone) / (1 - deadzone));
    }

    public static PadFrame Process(XInputGamepad pad)
    {
        var (lx, ly) = Stick(pad.LeftThumbX, pad.LeftThumbY);
        var (rx, ry) = Stick(pad.RightThumbX, pad.RightThumbY);
        return new PadFrame(lx, ly, rx, ry, Trigger(pad.LeftTrigger), Trigger(pad.RightTrigger), pad.Buttons);
    }

    public static double Axis(PadFrame f, string axis) => axis switch
    {
        "LeftThumbX" => f.LX, "LeftThumbY" => f.LY,
        "RightThumbX" => f.RX, "RightThumbY" => f.RY,
        "LeftTrigger" => f.LT, "RightTrigger" => f.RT,
        _ => 0
    };

    /// <summary>Axis lock: on a stick, only the dominant direction drives a control, so pushing
    /// the left stick up for the filter no longer nudges the crossfader sideways.</summary>
    public static double AxisLocked(PadFrame f, string axis)
    {
        double v = Axis(f, axis);
        string? other = axis switch
        {
            "LeftThumbX" => "LeftThumbY", "LeftThumbY" => "LeftThumbX",
            "RightThumbX" => "RightThumbY", "RightThumbY" => "RightThumbX", _ => null
        };
        if (other is null) return v;
        return Math.Abs(v) >= Math.Abs(Axis(f, other)) * 1.25 ? v : 0;
    }

    /// <summary>Filter response: re-applies a larger radial deadzone on top of the 0.15 stick deadzone, then an
    /// expo curve out = expo*x^3 + (1-expo)*x so small moves change the filter gently. Sign preserved, 1 -> 1.</summary>
    public static double FilterCurve(double processed, double deadzone, double expo)
    {
        if (processed == 0) return 0;
        double raw = StickDeadzone + Math.Abs(processed) * (1 - StickDeadzone);
        if (raw <= deadzone) return 0;
        double x = Math.Min(1, (raw - deadzone) / (1 - deadzone));
        double e = Math.Clamp(expo, 0, 1);
        return Math.Sign(processed) * (e * x * x * x + (1 - e) * x);
    }

    /// <summary>Back-compat: raw pad axis with the new radial deadzone.</summary>
    public static double Axis(XInputGamepad pad, string axis) => Axis(Process(pad), axis);
}

/// <summary>Per-axis controller logic, testable without hardware.
/// Absolute mode (filter): stick deflection maps to an offset from center, smoothed; on release the
/// value glides back to center and then STOPS (no residual integration). Rate mode (crossfader/EQ):
/// speed proportional to deflection^2, exact zero at rest, remainder cleared on release.</summary>
public sealed class AxisDriver
{
    public bool Absolute { get; }
    private double _smoothed;     // absolute mode: smoothed deflection -1..1
    private double _remainder;    // rate mode: fractional step carry
    private bool _active;
    public double SmoothMs { get; set; } = 35;
    public AxisDriver(bool absolute, double smoothMs = 35) { Absolute = absolute; SmoothMs = smoothMs; }

    /// <summary>Returns target value (absolute, 0..100) or step delta (rate). null = do nothing this frame.</summary>
    public double? Update(double input, double dtSeconds)
    {
        if (Absolute)
        {
            double alpha = 1 - Math.Exp(-dtSeconds / Math.Max(0.001, SmoothMs / 1000.0));
            _smoothed += (input - _smoothed) * alpha;
            if (input == 0 && Math.Abs(_smoothed) < 0.01) _smoothed = 0;
            bool moving = input != 0 || _smoothed != 0;
            if (!moving && !_active) return null; // at rest: never write, mouse edits stay put
            _active = moving;
            return Math.Clamp(50 + _smoothed * 50, 0, 100);
        }
        if (input == 0) { _remainder = 0; return null; }
        double speed = Math.Sign(input) * input * input * 60.0; // units/sec at full tilt (≈1.7 s full travel)
        _remainder += speed * dtSeconds;
        int whole = (int)Math.Truncate(_remainder);
        _remainder -= whole;
        return whole == 0 ? null : whole;
    }
    public void Reset() { _smoothed = 0; _remainder = 0; _active = false; }
}
