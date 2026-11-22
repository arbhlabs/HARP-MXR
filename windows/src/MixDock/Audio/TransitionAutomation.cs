namespace MixDock.Audio;

[Flags]
public enum MixLane
{
    None = 0, Fader = 1, EqA = 2, EqB = 4, FilterA = 8, FilterB = 16, FxA = 32, FxB = 64,
    RollA = 128, RollB = 256, TempoA = 512, TempoB = 1024, All = 2047
}

public readonly record struct TransitionLaneValues(double Blend, double OutLow, double InLow, double OutMid,
    double InMid, double OutHigh, double InHigh, double OutFilter, double InFilter, double Echo,
    double Reverb, double RollBeats, double Riser, double Brake, double Rate);

public sealed record TransitionRenderState(long Frame, TransitionPlan? Plan, double Progress,
    TransitionLaneValues Lane, bool Active, int Outgoing, MixLane Automated, bool MissedCue);

/// <summary>Render-thread automation. Every time coordinate is an absolute output frame (44.1 kHz).</summary>
public sealed class TransitionAutomation
{
    public const int SampleRate = 44100;
    internal sealed record Job(TransitionPlan Plan, int Outgoing, long Start, long Landing, long End,
        double? CueFrame, int CueEpoch);
    private Job? _requested;
    private Job? _finished;
    private long _outputFrame;
    private readonly long[] _manualUntil = new long[11];
    private TransitionRenderState _snapshot = new(0, null, 0, default, false, 0, MixLane.None, false);
    private int _missedCue;

    public long OutputFrame => Volatile.Read(ref _outputFrame);
    public TransitionRenderState Snapshot => Volatile.Read(ref _snapshot);
    internal Job? Pending => Volatile.Read(ref _requested);
    internal bool IsComplete(Job job) => ReferenceEquals(_finished, job);

    public void Schedule(TransitionPlan plan, int outgoingDeck, long startFrame, double? cueFrame = null, int cueEpoch = 0)
    {
        if (outgoingDeck is < 0 or > 1 || !double.IsFinite(plan.DurationSec) || plan.DurationSec <= 0 ||
            !double.IsFinite(plan.BeatSec) || plan.BeatSec <= 0) throw new ArgumentOutOfRangeException(nameof(plan));
        long start = Math.Max(startFrame, OutputFrame);
        long landing = start + (long)Math.Round((plan.LandingSec - plan.StartSec) * SampleRate);
        long end = start + Math.Max(1, (long)Math.Round(plan.DurationSec * SampleRate));
        Volatile.Write(ref _missedCue, 0);
        Volatile.Write(ref _requested, new Job(plan, outgoingDeck, start, Math.Clamp(landing, start, end), end, cueFrame, cueEpoch));
    }

    public void Cancel() => Volatile.Write(ref _requested, null);

    public void TakeOver(MixLane lanes)
    {
        var job = Pending;
        if (job == null) return;
        long until = OutputFrame + (long)Math.Ceiling(job.Plan.BeatSec * 16 * SampleRate);
        for (int lane = 0; lane < _manualUntil.Length; lane++)
            if (((int)lanes & (1 << lane)) != 0) Interlocked.Exchange(ref _manualUntil[lane], until);
    }

    /// <summary>Lanes currently held by the owner's hand.</summary>
    public MixLane Manual
    {
        get
        {
            long frame = OutputFrame; int lanes = 0;
            for (int lane = 0; lane < _manualUntil.Length; lane++)
                if (Volatile.Read(ref _manualUntil[lane]) > frame) lanes |= 1 << lane;
            return (MixLane)lanes;
        }
    }

    internal bool Owns(MixLane lane, long frame) =>
        frame >= Volatile.Read(ref _manualUntil[System.Numerics.BitOperations.TrailingZeroCount((uint)lane)]);

    internal int Slice(Job? job, int available)
    {
        if (job == null || IsComplete(job)) return available;
        long frame = OutputFrame;
        if (frame < job.Start) return (int)Math.Min(available, job.Start - frame);
        if (frame >= job.End) return available;
        long next = frame < job.Landing ? job.Landing : job.End;
        return (int)Math.Min(Math.Min(available, 256), next - frame);
    }

    internal void MissCue(Job job)
    {
        Volatile.Write(ref _missedCue, 1);
        _finished = job;
    }

    internal void Advance(int frames) => Volatile.Write(ref _outputFrame, _outputFrame + frames);

    internal void Publish(Job? job, MixLane lanes)
    {
        long frame = OutputFrame;
        if (job == null)
        {
            if (_snapshot.Plan != null) Volatile.Write(ref _snapshot, new(frame, null, 0, default, false, 0, MixLane.None, false));
            return;
        }
        bool missed = Volatile.Read(ref _missedCue) != 0;
        bool active = frame >= job.Start && frame < job.End && !missed;
        if (frame >= job.End) _finished = job;
        double progress = Math.Clamp((frame - job.Start) / (double)(job.End - job.Start), 0, 1);
        Volatile.Write(ref _snapshot, new(frame, job.Plan, progress, Evaluate(job.Plan, (frame - job.Start) / (double)SampleRate),
            active, job.Outgoing, active ? lanes : MixLane.None, missed));
    }

    public static TransitionLaneValues Evaluate(TransitionPlan plan, double elapsed)
    {
        double p = Math.Clamp(elapsed / plan.DurationSec, 0, 1);
        double beat = plan.BeatSec, landing = plan.LandingSec - plan.StartSec;
        double handoff = plan.Blends ? plan.DurationSec * .5 : landing;
        double blend = plan.Blends ? Smooth(p) : Smooth((elapsed - landing) / .008);
        // The outgoing bass is closed before the incoming bass opens. The short gap is masked by the mids.
        double outLow = -42 * Smooth((elapsed - handoff + beat * .25) / (beat * .24));
        double inLow = -42 * (1 - Smooth((elapsed - handoff) / (beat * .22)));
        double outMid = -6 * Smooth((p - .64) / .36), inMid = -6 * (1 - Smooth(p / .38));
        double outHigh = -4 * Smooth((p - .56) / .44), inHigh = -4 * (1 - Smooth(p / .38));
        double filterOut = 0, filterIn = 0, echo = 0, reverb = 0, roll = 0, riser = 0, brake = 0;
        double rate = plan.Blends ? plan.TempoRatio : 1;
        // The outgoing stream keeps drifting on its own (long DJ mixes); the incoming follows that drift.
        if (plan.Blends && plan.Style != TransitionStyle.TempoRamp && plan.DriftBpmPerSec != 0)
            rate *= 1 + plan.DriftBpmPerSec * elapsed * plan.BeatSec / 60;
        if (!plan.Blends)
        {
            outMid = inMid = outHigh = inHigh = 0;
            inLow = 0;
        }
        switch (plan.Style)
        {
            case TransitionStyle.FilterBlend:
                filterOut = .46 * Smooth(p);
                filterIn = -.46 * (1 - Smooth(p * 1.7));
                reverb = .15 * plan.FxDepth * Math.Sin(p * Math.PI);
                break;
            case TransitionStyle.EchoOut:
                echo = plan.ProtectVocals || elapsed >= landing ? 0 : .40 * plan.FxDepth * Smooth(p * 5);
                break;
            case TransitionStyle.DropSwap:
                riser = elapsed < landing ? .36 * plan.FxDepth * Smooth(elapsed / Math.Max(.02, landing)) : 0;
                break;
            case TransitionStyle.DoubleDrop:
                blend = .47 * Smooth(p / .06) + .53 * Smooth((p - .24) / .76);
                reverb = .09 * plan.FxDepth * Math.Sin(p * Math.PI);
                break;
            case TransitionStyle.LoopRoll:
                roll = p < .32 ? 0 : p < .58 ? 1 : p < .81 ? .5 : .25;
                filterOut = .32 * Smooth(p);
                break;
            case TransitionStyle.TempoRamp:
                rate = plan.TempoRatio + (1 - plan.TempoRatio) * Smooth(p);
                break;
            case TransitionStyle.PowerDown:
                brake = elapsed < landing ? .95 * Smooth(elapsed / Math.Max(.02, landing)) : 0;
                break;
            case TransitionStyle.BreakdownEntry:
                reverb = .26 * plan.FxDepth * Math.Sin(p * Math.PI);
                inLow *= .82;
                break;
        }
        return new(blend, outLow, inLow, outMid, inMid, outHigh, inHigh, filterOut, filterIn, echo,
            reverb, roll, riser, brake, rate);
    }

    private static double Smooth(double value)
    {
        double x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
