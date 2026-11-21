namespace MixDock.Audio;

/// <summary>
/// White-noise riser for the RT throw: noise through a high-pass whose cutoff climbs with the build (0..1, driven
/// by held beats), gated by a sixteenth-note pulse that gets deeper as the build rises. Release is quantized: the
/// UI passes the frames left to the next beat and the riser stops there with a short fade. Allocation-free.
/// </summary>
public sealed class RiserFx
{
    private const int Sr = 44100;
    private readonly Biquad _hpL = new(), _hpR = new();
    private volatile float _amount, _build;
    private volatile int _periodFrames = Sr / 2;          // one beat
    private int _stopIn = -1;                              // render-thread countdown to the quantized stop
    private volatile int _stopReq = -1;
    private double _beatPos;                               // frames into the current beat
    private float _gain, _gate = 1;
    private uint _rng = 0x9E3779B9;
    private float _builtBuild = -1;

    /// <summary>0..1 throw amount (trigger depth).</summary>
    public float Amount { get => _amount; set { _amount = Math.Clamp(value, 0, 1); if (value > 0) _stopReq = -1; } }
    /// <summary>0..1 build progress (sweep + pulse depth).</summary>
    public float Build { get => _build; set => _build = Math.Clamp(value, 0, 1); }
    public bool Active => _amount > 0 || _gain > 1e-4f;

    /// <summary>Lock the sixteenth pulse to the beat: phase 0..1 now, tempo in BPM.</summary>
    public void Sync(double bpm, double phase)
    {
        if (bpm > 0) _periodFrames = (int)(60.0 / bpm * Sr);
        _beatPos = double.IsNaN(phase) ? 0 : phase * _periodFrames;
    }
    /// <summary>Stop on the next beat: frames from now until it.</summary>
    public void StopAfter(int frames) => _stopReq = Math.Max(0, frames);
    public void Kill() { _amount = 0; _gain = 0; _stopReq = -1; _stopIn = -1; }

    /// <summary>dst += riser noise.</summary>
    public void Process(Span<float> dst)
    {
        int req = _stopReq;
        if (req >= 0) { _stopReq = -1; _stopIn = req; }
        if (!Active && _stopIn < 0) return;
        float build = _build;
        if (Math.Abs(build - _builtBuild) > 0.002f)
        {
            double f = 250 * Math.Pow(8000.0 / 250, build);      // 250 Hz -> 8 kHz
            _hpL.Pass(Sr, f, true); _hpR.Pass(Sr, f, true);
            _builtBuild = build;
        }
        int period = Math.Max(64, _periodFrames), sixteenth = Math.Max(16, period / 4);
        float depth = 0.15f + 0.75f * build;
        for (int i = 0; i + 1 < dst.Length; i += 2)
        {
            float target = _amount;
            if (_stopIn >= 0) { if (_stopIn == 0) target = 0; else { _stopIn--; target = _gain; } }
            if (_stopIn == 0 && _gain < 1e-4f) { _stopIn = -1; _amount = 0; }
            _gain += (target - _gain) * (target > _gain ? 0.0008f : 0.003f);
            double pos = _beatPos % sixteenth;
            float wantGate = pos < sixteenth * 0.55 ? 1f : 1f - depth;
            _gate += (wantGate - _gate) * 0.01f;
            _beatPos += 1; if (_beatPos >= period) _beatPos -= period;
            float g = _gain * _gate * (0.10f + 0.16f * build);
            dst[i] += _hpL.Process(Noise() * g);
            dst[i + 1] += _hpR.Process(Noise() * g);
        }
    }

    private float Noise()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng / (float)uint.MaxValue) * 2f - 1f;
    }
}
