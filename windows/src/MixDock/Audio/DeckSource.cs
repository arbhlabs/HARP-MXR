using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace MixDock.Audio;

public sealed class DeckSource : IAsyncDisposable, ISyncDeck
{
    /// <summary>Everything captured, by absolute frame (beat grids, continuous sync and slip all read from here).</summary>
    public readonly SampleHistory History = new();
    BpmDetector ISyncDeck.Bpm => Bpm;
    bool ISyncDeck.Active => ActivePid != null;
    private WasapiRecorder? _recorder;
    private readonly SampleRing _ring;
    private readonly DeckDsp _dsp;
    public int? ActivePid { get; private set; }
    private long _bytes;
    private int _rawPeakBits;
    /// <summary>Total bytes captured since StartAsync.</summary>
    public long TotalBytes => Interlocked.Read(ref _bytes);
    /// <summary>Largest raw (pre-DSP) sample since last call; resets.</summary>
    public float TakeRawPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _rawPeakBits, 0));
    public DeckDsp Dsp => _dsp;
    public readonly BpmDetector Bpm = new();
    /// <summary>Rolling 10 ms band-peak columns of the captured app audio (live decks; drawn by WaveformStrip).</summary>
    public readonly WaveformAnalyzer Wave = new(120);
    private int _discont, _silent;
    /// <summary>Capture packets flagged DATA_DISCONTINUITY (the app stream skipped).</summary>
    public int Discontinuities => Volatile.Read(ref _discont);
    public int SilentPackets => Volatile.Read(ref _silent);
    public DeckSource(SampleRing ring, DeckDsp dsp) { _ring = ring; _dsp = dsp; }
    private volatile bool _frozen;
    /// <summary>Live deck paused in MixDock: captured audio is dropped until resume (pairs with DeckReader.Frozen).</summary>
    public bool Frozen { get => _frozen; set => _frozen = value; }
    public async Task StartAsync(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) throw new ArgumentException("Invalid target process");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new NotSupportedException("Application loopback requires Windows build 20348+; Windows 11 recommended.");
        await StopAsync();
        Log.Write($"DeckSource.StartAsync pid={pid}");
        _bytes = 0;
        var recorder = await Task.Run(async () => await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
            .WithBufferLength(50)
            .BuildAsync());
        try
        {
            recorder.DataAvailable += OnAudio;
            recorder.StartRecording();
            _recorder = recorder;
            ActivePid = pid;
            Log.Write($"DeckSource started OK pid={pid}");
        }
        catch (Exception ex) { Log.Write($"DeckSource start FAILED pid={pid}: {ex}"); await recorder.DisposeAsync(); throw; }
    }
    private void OnAudio(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        // NAudio zero-copy buffer is only valid in this callback. Output is copied into ring.
        if (bytes.Length < 8 || bytes.Length % 8 != 0) return;
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(bytes);
        Interlocked.Add(ref _bytes, bytes.Length);
        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Interlocked.Increment(ref _discont);
        if ((flags & AudioClientBufferFlags.Silent) != 0) Interlocked.Increment(ref _silent);
        float rp = 0; foreach (var f in samples) { float a = Math.Abs(f); if (a > rp) rp = a; }
        int cur;
        do { cur = Volatile.Read(ref _rawPeakBits); if (BitConverter.Int32BitsToSingle(cur) >= rp) break; }
        while (Interlocked.CompareExchange(ref _rawPeakBits, BitConverter.SingleToInt32Bits(rp), cur) != cur);
        // Raw audio goes into the ring; EQ/trim run at render time (DeckReader) so knobs act instantly.
        if (_frozen) return;   // deck paused in MixDock: the delay line, waveform and BPM all hold still
        Bpm.Feed(samples);
        Wave.Feed(samples);
        lock (History.Gate) { History.Write(samples); _ring.Write(samples); }
    }

    // ---- local file playback (same ring / DSP / BPM path as app capture) ----
    private NAudio.Wave.AudioFileReader? _fr;
    private NAudio.Wave.ISampleProvider? _chain;
    private CancellationTokenSource? _fileCts;
    private Task? _fileTask;
    private readonly object _fileGate = new();
    private volatile bool _paused;
    public bool IsFile => _fr != null;
    public string? FileName { get; private set; }
    public string? FilePath { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool Ended { get; private set; }
    public bool Paused { get => _paused; set => _paused = value; }
    public TimeSpan Position { get { try { return _fr?.CurrentTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } } }

    private void BuildChain()
    {
        NAudio.Wave.ISampleProvider p = _fr!;
        if (p.WaveFormat.Channels == 1) p = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(p);
        if (p.WaveFormat.SampleRate != 44100) p = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(p, 44100);
        _chain = p;
    }

    public async Task LoadFileAsync(string path)
    {
        await StopAsync();
        var fr = new NAudio.Wave.AudioFileReader(path);
        _fr = fr; FileName = Path.GetFileName(path); FilePath = path; Duration = fr.TotalTime; Ended = false; _paused = false;
        BuildChain();
        ActivePid = -1;
        Log.Write($"DeckSource file loaded: {path} {fr.WaveFormat} {Duration}");
        _fileCts = new CancellationTokenSource(); var ct = _fileCts.Token;
        _fileTask = Task.Run(() =>
        {
            var buf = new float[882 * 2];
            while (!ct.IsCancellationRequested)
            {
                long ph = History.Playhead;      // history mode: keep ~350 ms ahead of the playhead (file decks may run faster than real time)
                if (_paused || (ph >= 0 ? History.Written - ph > 44100 * 350 / 1000 : _ring.Count > 44100 * 2 * 180 / 1000)) { Thread.Sleep(8); continue; }
                int n;
                lock (_fileGate) n = _chain!.Read(buf.AsSpan());
                if (n <= 0) { Ended = true; _paused = true; continue; }
                var span = buf.AsSpan(0, n & ~1);
                float rp = 0; foreach (var f in span) { float a = Math.Abs(f); if (a > rp) rp = a; }
                int cur;
                do { cur = Volatile.Read(ref _rawPeakBits); if (BitConverter.Int32BitsToSingle(cur) >= rp) break; }
                while (Interlocked.CompareExchange(ref _rawPeakBits, BitConverter.SingleToInt32Bits(rp), cur) != cur);
                Interlocked.Add(ref _bytes, span.Length * 4);
                Bpm.Feed(span);
                lock (History.Gate) { History.Write(span); _ring.Write(span); }
            }
        }, ct);
    }

    /// <param name="keepGrid">true for beat-quantised cue jumps: the file's beat lattice is unchanged, so the detected grid stays valid.</param>
    public void SeekTo(TimeSpan t, bool keepGrid = true)
    {
        if (_fr == null) return;
        lock (_fileGate)
        {
            _fr.CurrentTime = t < TimeSpan.Zero ? TimeSpan.Zero : (t > Duration ? Duration : t);
            BuildChain(); Ended = false;
        }
        _ring.Clear();
        History.Discontinuity();
        if (!keepGrid) Bpm.Reset();
    }

    private async Task StopFileAsync()
    {
        var cts = _fileCts; var task = _fileTask; _fileCts = null; _fileTask = null;
        cts?.Cancel();
        if (task != null) { try { await task; } catch { } }
        cts?.Dispose();
        lock (_fileGate) { _fr?.Dispose(); _fr = null; _chain = null; }
        FileName = null; FilePath = null; Ended = false;
    }
    public async Task StopAsync()
    {
        await StopFileAsync();
        var old = _recorder;
        _recorder = null;
        ActivePid = null;
        if (old != null)
        {
            old.DataAvailable -= OnAudio;
            await old.DisposeAsync();
        }
        _ring.Clear();
        Bpm.Reset();
        Wave.Reset();
        History.Reset();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
