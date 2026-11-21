namespace MixDock.Audio;

/// <summary>
/// Stereo look-ahead brickwall limiter for the master bus. The gain applied to an output frame is never above
/// what that frame needs to stay under the ceiling (no overshoot), the gain starts falling up to the look-ahead
/// before a peak (no hard edge) and recovers with a smooth release. Allocation-free on the audio thread.
/// </summary>
public sealed class PeakLimiter
{
    private readonly int _look;                 // look-ahead in frames
    private readonly float[] _delay;            // interleaved stereo delay line
    private readonly double[] _need;            // per-frame required gain, same ring as _delay
    private readonly int[] _dq;                 // monotonic deque of ring indices (min of _need over the window)
    private int _dqHead, _dqCount, _pos;
    private double _gain = 1, _attack, _release;
    private volatile float _reductionDb;

    public float Ceiling { get; set; } = 0.891f;   // -1 dBFS
    /// <summary>Current gain reduction in dB (0 = not limiting).</summary>
    public float ReductionDb => _reductionDb;
    public int LatencyFrames => _look;

    public PeakLimiter(int sampleRate = 44100, double lookAheadMs = 2.0, double releaseMs = 120)
    {
        _look = Math.Max(1, (int)(sampleRate * lookAheadMs / 1000));
        _delay = new float[(_look + 1) * 2];
        _need = new double[_look + 1];
        _dq = new int[_look + 1];
        _attack = Math.Exp(-1.0 / Math.Max(1, _look / 3.0));
        _release = Math.Exp(-1.0 / (sampleRate * releaseMs / 1000));
        Array.Fill(_need, 1.0);
    }

    public void Reset()
    {
        Array.Clear(_delay); Array.Fill(_need, 1.0); _dqHead = _dqCount = _pos = 0; _gain = 1; _reductionDb = 0;
    }

    public void Process(Span<float> stereo)
    {
        int n = _need.Length;
        double ceil = Ceiling, minGain = 1;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float l = stereo[i], r = stereo[i + 1];
            if (!float.IsFinite(l)) l = 0; if (!float.IsFinite(r)) r = 0;
            double peak = Math.Max(Math.Abs(l), Math.Abs(r));
            double need = peak > ceil ? ceil / peak : 1.0;
            // push the newest frame; evict deque entries that are no longer the window minimum
            int slot = _pos;
            _delay[2 * slot] = l; _delay[2 * slot + 1] = r; _need[slot] = need;
            while (_dqCount > 0 && _need[_dq[(_dqHead + _dqCount - 1) % n]] >= need) _dqCount--;
            _dq[(_dqHead + _dqCount) % n] = slot; _dqCount++;
            // the frame leaving the delay line is the oldest one (look-ahead frames behind the newest)
            int outSlot = (_pos + 1) % n;
            double target = _need[_dq[_dqHead]];          // minimum over the out frame and the look-ahead
            // smooth toward the window minimum, but never above what the outgoing frame itself needs
            _gain = target < _gain ? target + (_gain - target) * _attack : target + (_gain - target) * _release;
            double g = Math.Min(_gain, _need[outSlot]);
            if (g < minGain) minGain = g;
            stereo[i] = (float)(_delay[2 * outSlot] * g);
            stereo[i + 1] = (float)(_delay[2 * outSlot + 1] * g);
            // the out slot is about to be overwritten: drop it from the deque front
            if (_dqCount > 0 && _dq[_dqHead] == outSlot) { _dqHead = (_dqHead + 1) % n; _dqCount--; }
            _pos = outSlot;
        }
        float red = (float)(-20 * Math.Log10(Math.Max(1e-6, minGain)));
        _reductionDb = Math.Max(red, _reductionDb * 0.85f);
    }
}
