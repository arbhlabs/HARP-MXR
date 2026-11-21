using NAudio.Wave;

namespace MixDock.Audio;

/// <summary>Realtime pull mixer: two DeckReaders (delay + resample + EQ) -> crossfader -> master. Taps cue and master feeds.</summary>
public sealed class MasterMixer : ISampleProvider
{
    private readonly DeckReader _a, _b;
    private float[] _scratch = Array.Empty<float>(), _fx = Array.Empty<float>();
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
    public void KillFx() { EchoA.Send = EchoB.Send = 0; EchoA.Freeze = EchoB.Freeze = false; EchoA.Clear(); EchoB.Clear(); Riser.Kill(); FxA.Clear(); FxB.Clear(); SamplerA.Reset(); SamplerB.Reset(); PadsA.KillAll(); PadsB.KillAll(); }
    private double _xfade = 0.5;
    private float _master = 0.88f;
    private bool _mute;
    private volatile float _peak;
    public readonly SampleRing CueRingA = new(44100), CueRingB = new(44100), MasterTap = new(44100);
    public volatile bool CueA, CueB, TapMaster;
    private volatile float _fgA = 1, _fgB = 1;
    public void SetFadeGain(int deck, double g) { if (deck == 0) _fgA = (float)g; else _fgB = (float)g; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public double Crossfader { get => Volatile.Read(ref _xfade); set => Volatile.Write(ref _xfade, Math.Clamp(value, 0, 1)); }
    public float Master { get => Volatile.Read(ref _master); set => Volatile.Write(ref _master, Math.Clamp(value, 0, 1)); }
    public float Peak => _peak;
    public void DecayPeak() => _peak *= 0.8f;
    public bool Mute { get => Volatile.Read(ref _mute); set => Volatile.Write(ref _mute, value); }
    public MasterMixer(DeckReader a, DeckReader b) { _a = a; _b = b; PadsA = new PadEngine(SamplerA); PadsB = new PadEngine(SamplerB); }

    /// <summary>Continuous beat sync; steers both decks' speed/phase once per block.</summary>
    public SyncController? Sync;

    /// <summary>How long each block took to mix (diagnostics; bounded ring).</summary>
    public readonly CallbackStats Timing = new();

    public int Read(Span<float> buffer)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Sync?.Process(buffer.Length / 2);
        if (_scratch.Length < buffer.Length) { _scratch = new float[buffer.Length]; _fx = new float[buffer.Length]; }
        var bs = _scratch.AsSpan(0, buffer.Length);
        _a.Read(buffer);
        _b.Read(bs);
        // cue feed: pre-fader, post-EQ
        if (CueA) CueRingA.Write(buffer); else if (CueRingA.Count > 0) CueRingA.Clear();
        if (CueB) CueRingB.Write(bs); else if (CueRingB.Count > 0) CueRingB.Clear();
        // Constant-power crossfade: equal-energy uncorrelated decks keep their level through the blend.
        double x = Crossfader;
        double gainA = Math.Cos(x * Math.PI / 2) * _fgA, gainB = Math.Sin(x * Math.PI / 2) * _fgB;
        float master = Mute ? 0 : Master;
        var fx = _fx.AsSpan(0, buffer.Length); fx.Clear();
        KeyA.Feed(buffer); KeyB.Feed(bs);     // what the song plays, before pads are added
        SamplerA.Process(buffer); SamplerB.Process(bs);
        PadsA.Process(buffer); PadsB.Process(bs);
        FxA.Process(buffer, fx, (float)gainA); FxB.Process(bs, fx, (float)gainB);
        EchoA.Process(buffer, fx, (float)gainA); EchoB.Process(bs, fx, (float)gainB);
        Riser.Process(fx);
        float pk = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            double sum = (buffer[i] * gainA + bs[i] * gainB + fx[i]) * master;
            float v = (float)(0.944 * Math.Tanh(sum / 0.944));
            buffer[i] = v;
            float av = Math.Abs(v); if (av > pk) pk = av;
        }
        _peak = Math.Max(pk, _peak * 0.9f);
        if (TapMaster) MasterTap.Write(buffer); else if (MasterTap.Count > 0) MasterTap.Clear();
        Timing.Record(buffer.Length / 2, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        return buffer.Length; // continuous provider: never signals end of stream
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
