namespace MixDock.Audio;

/// <summary>Effects the right-stick pad cycles through (R3). Space = reverb/delay morph on the stick's Y axis.</summary>
public enum PadFx { Space, Reverb, Flanger, Phaser, Crush, Gate, Echo }

/// <summary>Pure parameter curves and timing for the performance effects (unit-tested, no audio state).</summary>
public static class FxCurves
{
    public const int SampleRate = 44100;
    public const double DefaultDeadzone = 0.2;

    public static string Name(PadFx fx) => fx switch
    {
        PadFx.Space => "SPACE", PadFx.Reverb => "REVERB", PadFx.Flanger => "FLANGER", PadFx.Phaser => "PHASER",
        PadFx.Crush => "CRUSH", PadFx.Gate => "GATE", PadFx.Echo => "ECHO", _ => "FX"
    };
    public static PadFx Next(PadFx fx) => (PadFx)(((int)fx + 1) % Enum.GetValues<PadFx>().Length);

    /// <summary>Wet amount from stick distance: radial deadzone (0.2), rescaled to 0..1 with no jump, then a smoothstep soft curve.</summary>
    public static double Amount(double x, double y, double deadzone = DefaultDeadzone)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        double r = Math.Sqrt(x * x + y * y);
        deadzone = Math.Clamp(deadzone, 0, .9);
        if (r <= deadzone) return 0;
        double v = Math.Min(1, (r - deadzone) / (1 - deadzone));
        return v * v * (3 - 2 * v);
    }
    /// <summary>Stick axis -1..1 to a parameter 0..1 (centre = 0.5).</summary>
    public static double Param(double axis) => double.IsFinite(axis) ? Math.Clamp(.5 + .5 * axis, 0, 1) : .5;

    /// <summary>One-pole smoothing coefficient for a time constant at the given sample rate.</summary>
    public static double Coef(double ms, int sr = SampleRate) => 1 - Math.Exp(-1.0 / (Math.Max(.1, ms) * .001 * sr));

    /// <summary>LFO length in beats: x=0 -> 8 beats (slow sweep), x=1 -> 1 beat. Exponential.</summary>
    public static double LfoBeats(double x) => 8 * Math.Pow(2, -3 * Math.Clamp(x, 0, 1));
    /// <summary>Flanger delay centre/range in ms for depth y: 0.6..3 ms centre, up to 4 ms sweep.</summary>
    public static (double CentreMs, double SweepMs) FlangerSweep(double depth)
    {
        depth = Math.Clamp(depth, 0, 1);
        return (.8 + 2.4 * depth, .3 + 3.7 * depth);
    }
    public static double FlangerFeedback(double depth) => .15 + .65 * Math.Clamp(depth, 0, 1);
    /// <summary>Phaser notch sweep: exponential 250 Hz .. 250*2^(2+3*depth) Hz.</summary>
    public static double PhaserHz(double lfo01, double depth) => 250 * Math.Pow(2, lfo01 * (2 + 3 * Math.Clamp(depth, 0, 1)));
    /// <summary>Bitcrusher: sample-rate divisor 1..32 (x) and bit depth 16..3 (y).</summary>
    public static double CrushDivisor(double x) => Math.Pow(2, 5 * Math.Clamp(x, 0, 1));
    public static double CrushBits(double y) => 16 - 13 * Math.Clamp(y, 0, 1);

    /// <summary>Gate division in beats: x &lt; .5 = 1/8 note (0.5 beat), else 1/16 note (0.25 beat).</summary>
    public static double GateDivision(double x) => x < .5 ? .5 : .25;
    /// <summary>Gate open fraction of each step: 25..75 % (y).</summary>
    public static double GateDuty(double y) => .25 + .5 * Math.Clamp(y, 0, 1);
    /// <summary>Position inside the current gate step, 0..1, from the beat phase (0..1 within a beat, plus whole beats).</summary>
    public static double GatePosition(double beatPos, double division)
    {
        double p = beatPos / division; return p - Math.Floor(p);
    }
    /// <summary>1 while the gate step is in its open part, 0 in its closed part (smoothed by the DSP with 5 ms ramps).</summary>
    public static double GateOpen(double beatPos, double division, double duty) => GatePosition(beatPos, division) < duty ? 1 : 0;
    /// <summary>Frames per gate step at a tempo.</summary>
    public static double GateStepFrames(double bpm, double division, int sr = SampleRate) => 60.0 / Math.Clamp(bpm, 40, 300) * division * sr;

    /// <summary>Reverb: room feedback (0.70..0.97) and damping from x (space size / brightness).</summary>
    public static double ReverbFeedback(double x) => .70 + .27 * Math.Clamp(x, 0, 1);
    public static double ReverbDamp(double x) => .55 - .4 * Math.Clamp(x, 0, 1);

    /// <summary>Space pad: Y up = reverb, Y down = delay. Returns the (reverb, echo) share, equal-power, of the wet amount.</summary>
    public static (double Reverb, double Echo) SpaceMix(double y01)
    {
        double a = Math.Clamp(y01, 0, 1) * Math.PI / 2;
        return (Math.Sin(a), Math.Cos(a));
    }
    /// <summary>Echo time from x: 1/4 (x=0.. ) down to 1/16 beat, stepped musically; feedback from y.</summary>
    public static double EchoBeats(double x) => x < .25 ? 1 : x < .5 ? .75 : x < .75 ? .5 : .25;
    public static double EchoFeedback(double y) => .35 + .5 * Math.Clamp(y, 0, 1);

    /// <summary>LT time bend: half pressure = half speed, full press = stopped. Playback speed 1..0.</summary>
    public static double BendSpeed(double pressure) => double.IsFinite(pressure) ? 1 - Math.Clamp(pressure, 0, 1) : 1;
    /// <summary>Tape-stop slew: seconds to go from full speed to a stop (shorter for a harder press).</summary>
    public static double BrakeSeconds(double pressure) => .55 - .3 * Math.Clamp(pressure, 0, 1);
    public static double SpinUpSeconds(double fromSpeed) => .08 + .25 * (1 - Math.Clamp(fromSpeed, 0, 1));
    /// <summary>Level while the tape slows: fades only in the last 12 % of speed so a stop never leaves DC or a click.</summary>
    public static double BendGain(double speed)
    {
        double t = Math.Clamp(speed / .12, 0, 1); return t * t * (3 - 2 * t);
    }

    /// <summary>RT beat roll: depth by trigger pressure, 1/4 -> 1/8 -> 1/16 -> 1/32 note (1, .5, .25, .125 beat).</summary>
    public static double RollBeats(double pressure)
    {
        double p = Math.Clamp(double.IsFinite(pressure) ? pressure : 0, 0, 1);
        double[] beats = { 1, .5, .25, .125 };
        return beats[Math.Min(beats.Length - 1, (int)(p * beats.Length))];
    }
    public static string BeatsLabel(double beats) => beats >= 1 ? "1/4" : beats >= .5 ? "1/8" : beats >= .25 ? "1/16" : "1/32";

    /// <summary>Pad capture length: exactly one beat at the tempo, within the sampler's 4 s memory.</summary>
    public static int PadFrames(double bpm, int sr = SampleRate) => (int)Math.Clamp(60.0 / Math.Clamp(bpm, 40, 300) * sr, 64, sr * 4);
    /// <summary>Frames until the next 1/16-note grid line given the beat phase 0..1.</summary>
    public static int FramesToGrid(double phase, double bpm, double gridBeats = .25, int sr = SampleRate)
    {
        if (!double.IsFinite(phase) || !double.IsFinite(bpm) || bpm <= 0) return 0;
        double pos = phase / gridBeats; double next = Math.Ceiling(pos - 1e-9) - pos;
        return (int)Math.Round(next * gridBeats * 60.0 / bpm * sr);
    }
}
