using NAudio.Wave;

namespace MixDock.Audio;

/// <summary>A decoded song as float samples (interleaved, native channels and rate) that can seek.</summary>
public interface ITrackReader : ISampleProvider, IDisposable
{
    TimeSpan CurrentTime { get; set; }
    TimeSpan TotalTime { get; }
}

public enum TrackProblem { Missing, Unsupported, Damaged }

/// <summary>A file that cannot be played; <see cref="Exception.Message"/> is already short and plain.</summary>
public sealed class TrackOpenException : Exception
{
    public TrackProblem Problem { get; }
    public TrackOpenException(TrackProblem problem, string message, Exception? inner = null) : base(message, inner) { Problem = problem; }
}

public enum OggCodec { None, Vorbis, Opus, Other }

public static class TrackReaderFactory
{
    public const string OpenDialogFilter = "Music|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.aiff;*.aif";
    public const string LibraryDialogFilter = OpenDialogFilter;

    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".webm", ".mp4", ".wv" };

    /// <summary>Which Ogg codec a file holds, by its first page (not its extension).</summary>
    public static OggCodec Sniff(string path)
    {
        Span<byte> head = stackalloc byte[64];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            n = fs.Read(head);
        return Sniff(head[..n]);
    }

    public static OggCodec Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4 || head[0] != 'O' || head[1] != 'g' || head[2] != 'g' || head[3] != 'S') return OggCodec.None;
        // first page: 27-byte header, a segment table, then the codec's identification packet
        if (head.Length < 28) return OggCodec.Other;
        int body = 27 + head[26];
        var pkt = head.Length > body ? head[body..] : ReadOnlySpan<byte>.Empty;
        if (pkt.StartsWith("\u0001vorbis"u8)) return OggCodec.Vorbis;
        if (pkt.StartsWith("OpusHead"u8)) return OggCodec.Opus;
        return OggCodec.Other;
    }

    /// <summary>Open a song. Throws only <see cref="TrackOpenException"/>.</summary>
    public static ITrackReader Open(string path)
    {
        string name = Path.GetFileName(path);
        if (!File.Exists(path)) throw new TrackOpenException(TrackProblem.Missing, $"Can't play {name}: the file wasn't found");
        try
        {
            switch (Sniff(path))
            {
                case OggCodec.Vorbis: return new VorbisTrackReader(path);
                case OggCodec.Opus: return new OpusTrackReader(path);
                case OggCodec.Other: throw new TrackOpenException(TrackProblem.Unsupported, $"Can't play {name}: this file type isn't supported");
            }
            return new NAudioTrackReader(path);
        }
        catch (TrackOpenException) { throw; }
        catch (Exception ex)
        {
            bool known = Known.Contains(Path.GetExtension(path));
            throw new TrackOpenException(known ? TrackProblem.Damaged : TrackProblem.Unsupported,
                known ? $"Can't play {name}: the file is damaged" : $"Can't play {name}: this file type isn't supported", ex);
        }
    }

    public static string Describe(string path, Exception ex) =>
        ex is TrackOpenException ? ex.Message : $"Can't play {Path.GetFileName(path)}: {ex.Message}";
}

sealed class NAudioTrackReader : ITrackReader
{
    readonly AudioFileReader _r;
    public NAudioTrackReader(string path) { _r = new AudioFileReader(path); }
    public WaveFormat WaveFormat => _r.WaveFormat;
    public int Read(Span<float> buffer) => _r.Read(buffer);
    public TimeSpan CurrentTime { get => _r.CurrentTime; set => _r.CurrentTime = value; }
    public TimeSpan TotalTime => _r.TotalTime;
    public void Dispose() => _r.Dispose();
}

sealed class VorbisTrackReader : ITrackReader
{
    NVorbis.VorbisReader _r;
    readonly string _path;
    public WaveFormat WaveFormat { get; }
    public VorbisTrackReader(string path)
    {
        _path = path;
        _r = new NVorbis.VorbisReader(path);
        if (_r.Channels < 1 || _r.SampleRate < 8000) { _r.Dispose(); throw new InvalidDataException("bad Vorbis header"); }
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_r.SampleRate, _r.Channels);
    }
    public int Read(Span<float> buffer)
    {
        try { return _r.ReadSamples(buffer); }
        catch (Exception) { return 0; }   // damaged page mid-song: the deck ends instead of crashing the reader thread
    }
    public TimeSpan CurrentTime { get => _r.TimePosition; set => Seek(value); }
    void Seek(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        try { _r.TimePosition = t; return; }
        catch (Exception) { /* some encoders write page positions NVorbis rejects: fall back to decoding forward from the start */ }
        _r.Dispose();
        _r = new NVorbis.VorbisReader(_path);
        var skip = new float[4096 * _r.Channels];
        long left = (long)(t.TotalSeconds * _r.SampleRate) * _r.Channels;
        while (left > 0)
        {
            int n;
            try { n = _r.ReadSamples(skip.AsSpan(0, (int)Math.Min(skip.Length, left))); } catch (Exception) { break; }
            if (n <= 0) break;
            left -= n;
        }
    }
    public TimeSpan TotalTime => _r.TotalTime;
    public void Dispose() => _r.Dispose();
}

sealed class OpusTrackReader : ITrackReader
{
    const int Rate = 48000;
    readonly string _path;
    FileStream _fs = null!;
    Concentus.Oggfile.OpusOggReadStream _s = null!;
    short[] _pending = Array.Empty<short>();
    int _pendingPos;
    long _framesRead;
    long _skipFrames;   // decoder warm-up after a seek: decoded but not played
    readonly int _ch;
    public WaveFormat WaveFormat { get; }
    public TimeSpan TotalTime { get; }

    public OpusTrackReader(string path)
    {
        _path = path;
        try
        {
            // OpusHead is the first packet: version at +8, channel count at +9
            var head = new byte[64]; int n;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = probe.Read(head, 0, head.Length);
            int body = 27 + head[26];
            if (n < body + 19) throw new InvalidDataException("short Opus header");
            _ch = head[body + 9];
            if (_ch is < 1 or > 2) throw new InvalidDataException("unsupported Opus channel layout");
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, _ch);
            Reopen();
            TotalTime = _s.CanSeek ? _s.TotalTime : TimeSpan.Zero;
            if (TotalTime <= TimeSpan.Zero || !_s.HasNextPacket) throw new InvalidDataException("no Opus audio");
        }
        catch { _fs?.Dispose(); throw; }
    }

    // The demuxer cannot rewind after the end of the stream, so every seek starts a fresh one.
    void Reopen()
    {
        _fs?.Dispose();
        _fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _s = new Concentus.Oggfile.OpusOggReadStream(Concentus.OpusCodecFactory.CreateDecoder(Rate, _ch), _fs);
        _pending = Array.Empty<short>(); _pendingPos = 0;
    }

    public int Read(Span<float> buffer)
    {
        int count = buffer.Length, done = 0;
        try
        {
            while (done < count)
            {
                if (_pendingPos >= _pending.Length)
                {
                    if (!_s.HasNextPacket) break;
                    var p = _s.DecodeNextPacket();
                    if (p == null) break;   // end of stream or a damaged packet
                    _pending = p; _pendingPos = 0;
                    if (_skipFrames > 0) { int drop = (int)Math.Min(_skipFrames * _ch, _pending.Length); _pendingPos = drop; _skipFrames -= drop / _ch; }
                    continue;
                }
                int take = Math.Min(count - done, _pending.Length - _pendingPos);
                for (int i = 0; i < take; i++) buffer[done + i] = _pending[_pendingPos + i] / 32768f;
                _pendingPos += take; done += take;
            }
        }
        catch (Exception) { /* damaged packet: end of song */ }
        _framesRead += done / _ch;
        return done;
    }

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds(_framesRead / (double)Rate);
        set
        {
            var t = value < TimeSpan.Zero ? TimeSpan.Zero : value > TotalTime ? TotalTime : value;
            Reopen();
            _skipFrames = 0;
            if (t > TimeSpan.Zero)
            {
                var warm = TimeSpan.FromMilliseconds(Math.Min(90, t.TotalMilliseconds));
                _s.SeekTo(t - warm);
                _skipFrames = (long)(warm.TotalSeconds * Rate);
                _s.ReadNextRawPacket();   // the demuxer leaves the packet queued before the seek in front: drop it undecoded
            }
            _framesRead = (long)(t.TotalSeconds * Rate);
        }
    }
    public void Dispose() => _fs?.Dispose();
}