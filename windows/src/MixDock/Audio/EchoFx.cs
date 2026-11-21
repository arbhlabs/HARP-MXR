namespace MixDock.Audio;

/// <summary>
/// Tempo-synced stereo echo used for echo-out transitions. Input is sent pre-fade, so when AUTO MIX pulls a deck
/// to zero the echo tail keeps ringing and dies away on the beat. Feedback is damped (one-pole low-pass) like a
/// tape echo so repeats get darker. Allocation-free on the audio thread; idle cost is a single branch.
/// </summary>
public sealed class EchoFx
{
    private readonly float[] _buf;               // interleaved stereo, up to 2 s
    private int _w;
    private volatile float _send, _feedback = 0.55f;
    private volatile bool _freeze;
    private volatile int _delayFrames = 22050;
    private float _sendSmooth, _lpL, _lpR, _tailEnergy;

    public EchoFx(int sampleRate = 44100) { _buf = new float[sampleRate * 2 * 2]; }

    /// <summary>0..1 amount of the deck signal fed into the echo.</summary>
    public float Send { get => _send; set => _send = Math.Clamp(value, 0, 1); }
    public float Feedback { get => _feedback; set => _feedback = Math.Clamp(value, 0, 0.85f); }
    public int DelayFrames { get => _delayFrames; set => _delayFrames = Math.Clamp(value, 64, _buf.Length / 2 - 1); }

    /// <summary>Delay = beats * one beat at bpm (falls back to 375 ms when the tempo is unknown).</summary>
    public void SetBeats(double beats, double bpm, int sampleRate = 44100)
        => DelayFrames = (int)(bpm > 0 ? beats * 60.0 / bpm * sampleRate : 0.375 * sampleRate);

    /// <summary>Freeze: the current echo buffer loops forever (no new input, no decay) until released.</summary>
    public bool Freeze { get => _freeze; set => _freeze = value; }

    public bool Active => _freeze || _send > 0 || _sendSmooth > 1e-4f || _tailEnergy > 1e-7f;

    public void Clear() { Array.Clear(_buf); _tailEnergy = 0; _sendSmooth = 0; _lpL = _lpR = 0; }

    /// <summary>dst += echo(src * inputGain * send). MixDock feeds the post-crossfader deck signal (inputGain = the
    /// deck's fader gain), so cutting the deck stops new input while the tail rings out.</summary>
    public void Process(ReadOnlySpan<float> src, Span<float> dst, float inputGain = 1)
    {
        if (!Active) return;
        int len = _buf.Length, d = _delayFrames * 2;
        float send = _send, fb = _feedback, energy = 0;
        if (_freeze)
        {
            for (int i = 0; i + 1 < src.Length && i + 1 < dst.Length; i += 2)
            {
                int r = _w - d; if (r < 0) r += len;
                float el = _buf[r], er = _buf[r + 1];
                _buf[_w] = el; _buf[_w + 1] = er;
                dst[i] += el; dst[i + 1] += er;
                _w += 2; if (_w >= len) _w = 0;
            }
            _sendSmooth = 0; _tailEnergy = 1e-3f;
            return;
        }
        for (int i = 0; i + 1 < src.Length; i += 2)
        {
            _sendSmooth += (send - _sendSmooth) * 0.002f;
            int r = _w - d; if (r < 0) r += len;
            float el = _buf[r], er = _buf[r + 1];
            _lpL += (el - _lpL) * 0.45f; _lpR += (er - _lpR) * 0.45f;
            float g = _sendSmooth * inputGain;
            _buf[_w] = src[i] * g + _lpL * fb;
            _buf[_w + 1] = src[i + 1] * g + _lpR * fb;
            dst[i] += el; dst[i + 1] += er;
            energy += el * el + er * er;
            _w += 2; if (_w >= len) _w = 0;
        }
        _tailEnergy = energy / Math.Max(1, src.Length);
    }
}
