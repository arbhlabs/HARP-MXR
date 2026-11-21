namespace MixDock.Audio;

/// <summary>Bounded interleaved stereo float FIFO; overwrites oldest frames on overrun.</summary>
public sealed class SampleRing
{
    private readonly object _gate = new();
    private readonly float[] _buffer;
    private int _head, _count;
    public int Capacity => _buffer.Length;
    public int Count { get { lock (_gate) return _count; } }
    public SampleRing(int stereoFrames = 44100) => _buffer = new float[stereoFrames * 2];
    /// <summary>Drop the oldest samples so at most keep remain (bounds latency).</summary>
    public void Trim(int keep)
    {
        keep &= ~1;
        lock (_gate)
        {
            int drop = _count - keep;
            if (drop <= 0) return;
            _head = (_head + drop) % Capacity; _count -= drop;
        }
    }
    public void Clear() { lock (_gate) { _head = 0; _count = 0; } }
    /// <summary>Copies the next samples without consuming them (used to crossfade out of audio that is about to be dropped).</summary>
    public int Peek(Span<float> destination)
    {
        lock (_gate)
        {
            int n = Math.Min(destination.Length, _count) & ~1;
            for (int i = 0; i < n; i++) destination[i] = _buffer[(_head + i) % Capacity];
            return n;
        }
    }
    public void Write(ReadOnlySpan<float> input)
    {
        if (input.Length % 2 != 0) throw new ArgumentException("Stereo frames only");
        lock (_gate)
        {
            if (input.Length > Capacity) input = input[^Capacity..];
            var overflow = Math.Max(0, _count + input.Length - Capacity);
            _head = (_head + overflow) % Capacity;
            _count -= overflow;
            var tail = (_head + _count) % Capacity;
            for (int i = 0; i < input.Length; i++) _buffer[(tail + i) % Capacity] = input[i];
            _count += input.Length;
        }
    }
    public int Read(Span<float> destination)
    {
        destination.Clear();
        lock (_gate)
        {
            int n = Math.Min(destination.Length, _count) & ~1;
            for (int i = 0; i < n; i++) destination[i] = _buffer[(_head + i) % Capacity];
            _head = (_head + n) % Capacity; _count -= n;
            return n;
        }
    }
}
