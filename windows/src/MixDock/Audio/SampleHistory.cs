namespace MixDock.Audio;

/// <summary>
/// Last ~23 s of a deck's raw captured stereo audio, addressed by ABSOLUTE frame index (frames since the stream started),
/// the same clock <see cref="BpmDetector"/> grids use. One writer (capture / file thread), one reader (render thread).
/// A reader may jump back in time (replay a bar) or sit ahead of the writer's lead (live decks cannot read the future).
/// </summary>
public sealed class SampleHistory
{
    public const int CapacityFrames = 1 << 20;     // 23.8 s
    private const int Mask = CapacityFrames - 1;
    private readonly float[] _buf = new float[CapacityFrames * 2];
    private long _written;
    private int _epoch;

    /// <summary>Frames written so far (the newest frame is Written - 1).</summary>
    public long Written => Volatile.Read(ref _written);
    /// <summary>Bumps on every Reset (stream restarted / file seek): readers must re-seed.</summary>
    public int Epoch => Volatile.Read(ref _epoch);
    private long _continuousFrom;
    /// <summary>First frame that belongs to the current continuous stretch of audio (a file seek starts a new one).</summary>
    public long ContinuousFrom => Volatile.Read(ref _continuousFrom);
    private long _playhead = -1;
    /// <summary>Reader's playhead (absolute frame), published every block so file feeders can throttle; -1 = no history reader.</summary>
    public long Playhead { get => Volatile.Read(ref _playhead); set => Volatile.Write(ref _playhead, value); }
    /// <summary>Held while the capture/file thread appends to this history AND the deck's ring, so a reader can take a consistent snapshot of both.</summary>
    public readonly object Gate = new();

    /// <summary>The audio jumped (file seek) but the frame clock keeps running: readers must re-seed at the new stretch.</summary>
    public void Discontinuity()
    {
        Volatile.Write(ref _continuousFrom, Written);
        Interlocked.Increment(ref _epoch);
    }

    public void Reset()
    {
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _continuousFrom, 0);
        Volatile.Write(ref _playhead, -1);
        Interlocked.Increment(ref _epoch);
    }

    public void Write(ReadOnlySpan<float> stereo)
    {
        long w = _written; int n = stereo.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int k = (int)((w + i) & Mask) * 2;
            _buf[k] = stereo[2 * i]; _buf[k + 1] = stereo[2 * i + 1];
        }
        Volatile.Write(ref _written, w + n);
    }

    /// <summary>True when frame <paramref name="idx"/> is still held (not overwritten) and already written.</summary>
    public bool Has(long idx, long written) => idx >= 0 && idx < written && idx >= written - CapacityFrames + 4096;

    public float L(long idx) => _buf[(int)(idx & Mask) * 2];
    public float R(long idx) => _buf[(int)(idx & Mask) * 2 + 1];

    /// <summary>Linear interpolation at a fractional frame.</summary>
    public void Interp(double pos, out float l, out float r)
    {
        long i0 = (long)Math.Floor(pos); float f = (float)(pos - i0);
        int a = (int)(i0 & Mask) * 2, b = (int)((i0 + 1) & Mask) * 2;
        l = _buf[a] * (1 - f) + _buf[b] * f; r = _buf[a + 1] * (1 - f) + _buf[b + 1] * f;
    }

    /// <summary>Mono sum at an integer frame (used by the stretch matcher).</summary>
    public float Mono(long idx) { int k = (int)(idx & Mask) * 2; return _buf[k] + _buf[k + 1]; }
}
