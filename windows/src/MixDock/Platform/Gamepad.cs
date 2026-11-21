using System.Runtime.InteropServices;

namespace MixDock.Platform;

[Flags]
public enum PadButton : ushort
{
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
}
[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public PadButton Buttons;
    public byte LeftTrigger, RightTrigger;
    public short LeftThumbX, LeftThumbY, RightThumbX, RightThumbY;
}
[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}
[StructLayout(LayoutKind.Sequential)]
public struct XInputVibration { public ushort Left, Right; }

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

    private static int _slot = -1;            // hot-plug: first connected slot 0..3
    private static long _nextScanTick;

    public static int Slot => _slot;

    /// <summary>Reads the active controller; rescans all 4 XInput slots (throttled) after a disconnect.</summary>
    public static bool Read(out XInputState state)
    {
        state = default;
        try
        {
            if (_slot >= 0 && GetStateNative((uint)_slot, out state) == 0) return true;
            _slot = -1;
            long now = Environment.TickCount64;
            if (now < _nextScanTick) return false;
            _nextScanTick = now + 1000; // XInputGetState on empty slots is slow; scan at most 1/s
            for (uint i = 0; i < 4; i++)
                if (GetStateNative(i, out state) == 0) { _slot = (int)i; return true; }
            return false;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static void Rumble(double left, double right)
    {
        if (_slot < 0) return;
        try
        {
            var v = new XInputVibration { Left = (ushort)(Math.Clamp(left, 0, 1) * 65535), Right = (ushort)(Math.Clamp(right, 0, 1) * 65535) };
            SetStateNative((uint)_slot, ref v);
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
