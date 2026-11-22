using NAudio.Wave;

namespace MixDock.Audio;

/// <summary>Realtime pull mixer: two DeckReaders (delay + resample + EQ) -> crossfader -> master. Taps cue and master feeds.</summary>
public sealed class MasterMixer : ISampleProvider
{
    private readonly DeckReader _a, _b;
    private float[] _scratch = Array.Empty<float>(), _fx = Array.Empty<float>();
    public TransitionAutomation Automation { get; } = new();
    private TransitionAutomation.Job? _autoJob;
    private readonly EchoFx _autoEcho = new();
    private readonly RiserFx _autoRiser = new();
    private DeckParameters? _manualA, _manualB;
    private bool _cueSeeded;
    private double _autoRoll;
    private double _normalPower, _expectedPower, _mixedPower, _levelCorrection = 1, _inPower, _inPeak;
    private double Target(double progress)
    {
        double s = Math.Clamp((progress - .4) / .6, 0, 1), inP = Math.Max(_inPeak, 1e-8);
        return Math.Exp(Math.Log(_expectedPower) * (1 - s) + Math.Log(inP) * s);
    }
    private double _terminalGain = 1;
    /// <summary>Per-deck echo (post-fader send, tail rings out after the deck is cut) and the master riser.</summary>
    public readonly EchoFx EchoA = new(), EchoB = new();
    public readonly RiserFx Riser = new();
    public EchoFx Echo(int deck) => deck == 0 ? EchoA : EchoB;
    /// <summary>Right-stick effects (reverb send + inserts) and the slip engine (pads, time bend, reverse), per deck.</summary>
    public readonly PerformanceFx FxA = new(), FxB = new();
    public readonly PerformanceSampler SamplerA = new(), SamplerB = new();
    public PerformanceFx Fx(int deck) => deck == 0 ? FxA : FxB;
    public PerformanceSampler Sampler(int deck) => deck == 0 ? SamplerA : SamplerB;
    /// <summary>Pad player per deck (one-shots, drums, chord synth, layers). Mixed into the deck before its FX, fader and crossfader.</summary>
    public readonly PadEngine PadsA, PadsB;
    public PadEngine Pads(int deck) => deck == 0 ? PadsA : PadsB;
    /// <summary>Rolling key estimate of what each deck plays (feeds the chord pads).</summary>
    public readonly KeyDetector KeyA = new(), KeyB = new();
    public KeyDetector Key(int deck) => deck == 0 ? KeyA : KeyB;
    /// <summary>Panic: silence every effect tail immediately.</summary>
    public void KillFx() { Automation.Cancel(); EchoA.Send = EchoB.Send = 0; EchoA.Freeze = EchoB.Freeze = false; EchoA.Clear(); EchoB.Clear(); Riser.Kill(); FxA.Clear(); FxB.Clear(); SamplerA.Reset(); SamplerB.Reset(); PadsA.KillAll(); PadsB.KillAll(); }
    private double _xfade = 0.5;
    private float _master = 0.88f;
    private bool _mute;
    private volatile float _peak;
    public readonly SampleRing CueRingA = new(44100), CueRingB = new(44100), MasterTap = new(44100);
    public volatile bool CueA, CueB, TapMaster;
    private volatile float _fgA = 1, _fgB = 1;
    public void SetFadeGain(int deck, double g) { if (deck == 0) _fgA = (float)g; else _fgB = (float)g; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public double Crossfader
    {
        get => Volatile.Read(ref _xfade);
        set { Automation.TakeOver(MixLane.Fader); Volatile.Write(ref _xfade, Math.Clamp(value, 0, 1)); }
    }
    public float Master { get => Volatile.Read(ref _master); set => Volatile.Write(ref _master, Math.Clamp(value, 0, 1)); }
    public float Peak => _peak;
    public void DecayPeak() => _peak *= 0.8f;
    public bool Mute { get => Volatile.Read(ref _mute); set => Volatile.Write(ref _mute, value); }
    public MasterMixer(DeckReader a, DeckReader b) { _a = a; _b = b; PadsA = new PadEngine(SamplerA); PadsB = new PadEngine(SamplerB); }

    /// <summary>Continuous beat sync; steers both decks' speed/phase once per block.</summary>
    public SyncController? Sync
    {
        get => _sync;
        set { _sync = value; if (value != null) value.Audible = Audible; }
    }
    private SyncController? _sync;
    private volatile float _audA = 1, _audB = 1;
    /// <summary>True while the deck reaches the master at an audible level (fader, crossfader and transition lanes).</summary>
    public bool Audible(int deck) => (deck == 0 ? _audA : _audB) > 0.01f;

    /// <summary>How long each block took to mix (diagnostics; bounded ring).</summary>
    public readonly CallbackStats Timing = new();

    public int Read(Span<float> buffer)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_scratch.Length < buffer.Length) { _scratch = new float[buffer.Length]; _fx = new float[buffer.Length]; }
        Sync?.Process(buffer.Length / 2);
        ObserveManualEdits();
        var current = Automation.Pending;
        float peak = 0;
        MixLane lanes = MixLane.None;
        for (int index = 0; index < buffer.Length;)
        {
            int frames = Automation.Slice(current, (buffer.Length - index) / 2);
            if (frames <= 0) { buffer[index..].Clear(); break; }
            long at = Automation.OutputFrame;
            var running = current != null && !Automation.IsComplete(current) && at >= current.Start && at < current.End ? current : null;
            lanes = SetAutomation(running, at);
            var span = buffer.Slice(index, frames * 2);
            peak = Math.Max(peak, ReadSlice(span, running == null || Automation.IsComplete(running) ? null : running, at, lanes));
            index += span.Length;
            Automation.Advance(frames);
        }
        Automation.Publish(current, lanes);
        _peak = Math.Max(peak, _peak * 0.9f);
        if (TapMaster) MasterTap.Write(buffer); else if (MasterTap.Count > 0) MasterTap.Clear();
        Timing.Record(buffer.Length / 2, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        return buffer.Length;
    }

    private DeckReader Reader(int deck) => deck == 0 ? _a : _b;
    private static MixLane ForDeck(MixLane first, int deck) => (MixLane)((int)first << deck);

    private float _sendA, _sendB;
    private void ObserveManualEdits()
    {
        Observe(0, _a.Dsp.Parameters, ref _manualA);
        Observe(1, _b.Dsp.Parameters, ref _manualB);
        // A hand on an echo send takes that deck's FX lane.
        if (EchoA.Send != _sendA) { Automation.TakeOver(MixLane.FxA); _sendA = EchoA.Send; }
        if (EchoB.Send != _sendB) { Automation.TakeOver(MixLane.FxB); _sendB = EchoB.Send; }

        void Observe(int deck, DeckParameters now, ref DeckParameters? previous)
        {
            if (previous is { } old && old != now)
            {
                if (old.Filter != now.Filter) Automation.TakeOver(ForDeck(MixLane.FilterA, deck));
                if (old.LowDb != now.LowDb || old.MidDb != now.MidDb || old.HighDb != now.HighDb ||
                    old.LowKill != now.LowKill || old.MidKill != now.MidKill || old.HighKill != now.HighKill ||
                    old.BassCut != now.BassCut || old.Trim != now.Trim || old.Mute != now.Mute)
                    Automation.TakeOver(ForDeck(MixLane.EqA, deck));
            }
            previous = now;
        }
    }

    private MixLane SetAutomation(TransitionAutomation.Job? job, long frame)
    {
        if (job != _autoJob)
        {
            if (_autoJob is { } old)
            {
                ResetLane(0); ResetLane(1);
                if (_autoRoll > 0 && Automation.Owns(ForDeck(MixLane.RollA, old.Outgoing), frame))
                    Reader(old.Outgoing).StopRoll();
                _autoEcho.Send = 0;
                _autoRiser.Amount = 0;
            }
            _autoJob = job;
            _autoRoll = 0;
            _cueSeeded = false;
            if (job != null)
            {
                _autoEcho.SetBeats(.5, 60 / job.Plan.BeatSec);
                _autoEcho.Feedback = .30f;
                _autoEcho.Send = job.Plan.Style == TransitionStyle.EchoOut ? 1 : 0;
                _autoRiser.Sync(60 / job.Plan.BeatSec, 0);
                _expectedPower = Math.Max(_normalPower, 1e-8);
                _mixedPower = _expectedPower; _inPower = _inPeak = 0;
            }
        }
        if (job == null) return MixLane.None;
        int outgoing = job.Outgoing, incoming = 1 - outgoing;
        var plan = job.Plan;
        var o = Reader(outgoing);
        var n = Reader(incoming);
        if (!_cueSeeded && frame >= job.Landing)
        {
            _cueSeeded = true;
            if (job.CueFrame is double cue && !n.SeedAutomation(cue, job.CueEpoch))
            {
                Automation.MissCue(job);
                return SetAutomation(null, frame);
            }
        }
        var value = TransitionAutomation.Evaluate(plan, (frame - job.Start) / 44100d);
        MixLane active = Automation.Owns(MixLane.Fader, frame) ? MixLane.Fader : MixLane.None;
        SetDeck(outgoing, value.OutLow, value.OutMid, value.OutHigh, value.OutFilter);
        SetDeck(incoming, value.InLow, value.InMid, value.InHigh, value.InFilter);
        var fx = ForDeck(MixLane.FxA, outgoing);
        bool ownEffects = Automation.Owns(fx, frame);
        if (ownEffects && (value.Echo > 0 || value.Reverb > 0 || value.Riser > 0 || value.Brake > 0)) active |= fx;
        Sampler(outgoing).AutoBend = ownEffects ? (float)value.Brake : 0;
        _autoRiser.Amount = ownEffects ? (float)value.Riser : 0;
        _autoRiser.Build = (float)value.Riser;
        var roll = ForDeck(MixLane.RollA, outgoing);
        if (Automation.Owns(roll, frame))
        {
            if (value.RollBeats > 0) active |= roll;
            if (value.RollBeats != _autoRoll)
            {
                int length = (int)Math.Round(value.RollBeats * plan.BeatSec * 44100);
                if (length == 0) o.StopRoll();
                else if (_autoRoll > 0) o.ResizeRoll(length);
                else o.StartRoll(0, length);
            }
            _autoRoll = value.RollBeats;
        }
        else _autoRoll = 0;

        if (plan.Blends && _cueSeeded && (Sync == null || !Sync.Active))
        {
            var tempo = ForDeck(MixLane.TempoA, incoming);
            if (Automation.Owns(tempo, frame) && (n.HistoryMode || n.EnterHistory(true)))
            {
                n.HistoryRate = value.Rate;
                active |= tempo;
            }
            if (plan.Style == TransitionStyle.TempoRamp)
            {
                tempo = ForDeck(MixLane.TempoA, outgoing);
                if (Automation.Owns(tempo, frame) && (o.HistoryMode || o.EnterHistory(true)))
                {
                    o.HistoryRate = value.Rate / plan.TempoRatio;
                    active |= tempo;
                }
            }
        }
        return active;

        void SetDeck(int deck, double low, double mid, double high, double filter)
        {
            var dsp = Reader(deck).Dsp;
            var eqLane = ForDeck(MixLane.EqA, deck);
            var filterLane = ForDeck(MixLane.FilterA, deck);
            bool eq = Automation.Owns(eqLane, frame), sweep = Automation.Owns(filterLane, frame);
            dsp.AutoLowDb = eq ? low : 0;
            dsp.AutoMidDb = eq ? mid : 0;
            dsp.AutoHighDb = eq ? high : 0;
            dsp.AutoFilter = sweep ? filter : 0;
            if (eq && (low != 0 || mid != 0 || high != 0)) active |= eqLane;
            if (sweep && filter != 0) active |= filterLane;
        }

        void ResetLane(int deck)
        {
            var reader = Reader(deck);
            reader.Dsp.AutoLowDb = reader.Dsp.AutoMidDb = reader.Dsp.AutoHighDb = reader.Dsp.AutoFilter = 0;
            Sampler(deck).AutoBend = 0;
            if (Sync is not { Active: true } && Automation.Owns(ForDeck(MixLane.TempoA, deck), frame) && reader.HistoryMode)
                reader.HistoryRate = 1;
        }
    }

    private float ReadSlice(Span<float> buffer, TransitionAutomation.Job? job, long at, MixLane lanes)
    {
        var bs = _scratch.AsSpan(0, buffer.Length);
        _a.Read(buffer);
        _b.Read(bs);
        // cue feed: pre-fader, post-EQ
        if (CueA) CueRingA.Write(buffer); else if (CueRingA.Count > 0) CueRingA.Clear();
        if (CueB) CueRingB.Write(bs); else if (CueRingB.Count > 0) CueRingB.Clear();
        // Constant-power crossfade: equal-energy uncorrelated decks keep their level through the blend.
        var plan = job?.Plan;
        bool fader = (lanes & MixLane.Fader) != 0 && job != null;
        bool outA = job?.Outgoing == 0;
        double secs = job == null ? 0 : (at - job.Start) / 44100d;
        var values = plan == null ? default : TransitionAutomation.Evaluate(plan, secs);
        double x = fader ? outA ? values.Blend : 1 - values.Blend : Crossfader;
        double x2 = x;
        if (fader)
        {
            double last = TransitionAutomation.Evaluate(plan!, secs + buffer.Length / (2d * 44100)).Blend;
            x2 = outA ? last : 1 - last;
        }
        double gainA = Math.Cos(x * Math.PI / 2) * _fgA, gainB = Math.Sin(x * Math.PI / 2) * _fgB;
        _audA = (float)(Math.Max(Math.Cos(x * Math.PI / 2), Math.Cos(x2 * Math.PI / 2)) * _fgA);
        _audB = (float)(Math.Max(Math.Sin(x * Math.PI / 2), Math.Sin(x2 * Math.PI / 2)) * _fgB);
        float master = Mute ? 0 : Master;
        var fx = _fx.AsSpan(0, buffer.Length); fx.Clear();
        KeyA.Feed(buffer); KeyB.Feed(bs);     // what the song plays, before pads are added
        SamplerA.Process(buffer); SamplerB.Process(bs);
        PadsA.Process(buffer); PadsB.Process(bs);
        double reverb = (lanes & (MixLane.FxA | MixLane.FxB)) != 0 ? values.Reverb : 0;
        FxA.Process(buffer, fx, (float)gainA, outA ? reverb : 0); FxB.Process(bs, fx, (float)gainB, outA ? 0 : reverb);
        EchoA.Process(buffer, fx, (float)gainA); EchoB.Process(bs, fx, (float)gainB);
        _autoEcho.Process(outA ? buffer : bs, fx, (float)((lanes & (MixLane.FxA | MixLane.FxB)) != 0 ? values.Echo : 0));
        Riser.Process(fx);
        _autoRiser.Process(fx);
        float pk = 0;
        double power = 0, inPower = 0;
        double progress = plan == null ? 0 : Math.Clamp(secs / plan.DurationSec, 0, 1);
        // The sum is held at a level that glides from the outgoing deck's power to the incoming deck's own peak power, so beat-aligned
        // decks that add coherently, and EQ/filter dips, neither bump nor sag the level against the before-after line.
        double normalize = fader && _mixedPower > 1e-7 ? Math.Clamp(Math.Sqrt(Target(progress) / _mixedPower), .60, 1.45) : 1;
        double trimOut = plan == null ? 1 : 1 + (plan.OutGain - 1) * Math.Clamp(progress * 7, 0, 1);
        double trimIn = plan?.InGain ?? 1;
        int frames = buffer.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            if (fader)
            {
                double angle = (x + (x2 - x) * i / frames) * Math.PI / 2;
                gainA = Math.Cos(angle) * _fgA * (outA ? trimOut : trimIn);
                gainB = Math.Sin(angle) * _fgB * (outA ? trimIn : trimOut);
            }
            _levelCorrection += (normalize - _levelCorrection) * .001;
            _terminalGain += ((fader ? trimIn : 1) - _terminalGain) * .00007;
            double correction = fader ? _levelCorrection : _levelCorrection * _terminalGain;
            for (int ch = 0; ch < 2; ch++)
            {
                int j = i * 2 + ch;
                double sum = buffer[j] * gainA + bs[j] * gainB + fx[j];
                power += sum * sum;
                double ip = (outA ? bs[j] * _fgB : buffer[j] * _fgA) * trimIn; inPower += ip * ip;
                float v = (float)(0.944 * Math.Tanh(sum * master * correction / 0.944));
                buffer[j] = v;
                pk = Math.Max(pk, Math.Abs(v));
            }
        }
        double response = 1 - Math.Exp(-frames / (44100 * .32));
        power /= Math.Max(1, buffer.Length);
        if (fader) { double pin = inPower / Math.Max(1, buffer.Length); _inPower += (pin - _inPower) * response; _inPeak = Math.Max(_inPeak, _inPower); }
        if (fader)
        {
            _mixedPower += (power - _mixedPower) * response;
            Volatile.Write(ref _xfade, x2);
        }
        else _normalPower += (power - _normalPower) * response;
        return pk;
    }
}

/// <summary>Headphone feed: selected deck(s) pre-fader, blended with the master mix. Blend 0 = cue only, 1 = master only.</summary>
public sealed class CueMixer : ISampleProvider
{
    private readonly MasterMixer _m;
    private float[] _s1 = Array.Empty<float>(), _s2 = Array.Empty<float>(), _s3 = Array.Empty<float>();
    private volatile float _vol = 0.6f, _blend = 0.0f;
    private volatile float _peak;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public float Volume { get => _vol; set => _vol = Math.Clamp(value, 0, 1); }
    public float Blend { get => _blend; set => _blend = Math.Clamp(value, 0, 1); }
    public float Peak => _peak;
    public void DecayPeak() => _peak *= 0.8f;
    public CueMixer(MasterMixer m) { _m = m; }

    public int Read(Span<float> buffer)
    {
        int n = buffer.Length;
        if (_s1.Length < n) { _s1 = new float[n]; _s2 = new float[n]; _s3 = new float[n]; }
        const int target = 44100 * 2 * 70 / 1000;
        if (_m.CueRingA.Count > target * 2) _m.CueRingA.Trim(target);
        if (_m.CueRingB.Count > target * 2) _m.CueRingB.Trim(target);
        if (_m.MasterTap.Count > target * 2) _m.MasterTap.Trim(target);
        _m.CueRingA.Read(_s1.AsSpan(0, n)); _m.CueRingB.Read(_s2.AsSpan(0, n)); _m.MasterTap.Read(_s3.AsSpan(0, n));
        float vol = _vol, blend = _blend, pk = 0;
        for (int i = 0; i < n; i++)
        {
            double cue = _s1[i] + _s2[i];
            double v = vol * ((1 - blend) * cue + blend * _s3[i]);
            float o = (float)(0.944 * Math.Tanh(v / 0.944));
            buffer[i] = o; float a = Math.Abs(o); if (a > pk) pk = a;
        }
        _peak = Math.Max(pk, _peak * 0.9f);
        return n;
    }
}
