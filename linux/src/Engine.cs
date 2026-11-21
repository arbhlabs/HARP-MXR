using System.Diagnostics;
using System.Runtime.InteropServices;
using MixDock.Audio;

namespace MixDock.Linux;

/// <summary>One file deck: decoded PCM in RAM (interleaved float stereo, 44.1 kHz) plus the shared DeckDsp.</summary>
public sealed class Deck
{
    public readonly string Name;
    public readonly DeckDsp Dsp = new();
    private float[] _pcm = Array.Empty<float>();
    private long _pos;            // frame index
    public bool Playing;
    public string Title = "(empty)";
    public string Status = "Load a file: mixdock <trackA> [trackB]";

    public Deck(string name) => Name = name;

    public long Frames { get { lock (this) return _pcm.Length / 2; } }
    public long Position { get { lock (this) return _pos; } }
    public bool Loaded { get { lock (this) return _pcm.Length > 0; } }

    public void Load(string title, float[] pcm)
    {
        lock (this) { _pcm = pcm; _pos = 0; Playing = false; Title = title; Status = "Ready"; }
        Dsp.ResetAutoGain();
    }

    public void Seek(double seconds)
    {
        lock (this) _pos = Math.Clamp(_pos + (long)(seconds * DeckDsp.SampleRate), 0, Math.Max(0, _pcm.Length / 2 - 1));
    }

    public void Cue() { lock (this) { _pos = 0; Playing = false; } }

    public void TogglePlay() { lock (this) Playing = _pcm.Length > 0 && !Playing; }

    public void Update(Func<DeckParameters, DeckParameters> change) => Dsp.Parameters = change(Dsp.Parameters);

    /// <summary>Render thread: copy the next block (silence when paused or past the end) and run the deck DSP.</summary>
    public void Render(Span<float> scratch, Span<float> output)
    {
        scratch.Clear();
        lock (this)
        {
            if (Playing && _pcm.Length > 0)
            {
                long start = _pos * 2;
                int n = (int)Math.Min(scratch.Length, _pcm.Length - start);
                if (n > 0) _pcm.AsSpan((int)start, n).CopyTo(scratch);
                _pos += n / 2;
                if (_pos * 2 >= _pcm.Length) { Playing = false; Status = "End of track"; }
            }
        }
        Dsp.Process(scratch, output);
    }
}

/// <summary>Two decks, equal-power crossfader, master volume and the shared look-ahead peak limiter.</summary>
public sealed class Mixer
{
    public readonly Deck A = new("A"), B = new("B");
    private readonly PeakLimiter _limiter = new(DeckDsp.SampleRate);
    private float[] _scratch = Array.Empty<float>(), _a = Array.Empty<float>(), _b = Array.Empty<float>();
    private double _xfade = 0.5, _master = 0.9;

    /// <summary>0 = only A, 1 = only B.</summary>
    public double Crossfader { get => Volatile.Read(ref _xfade); set => Volatile.Write(ref _xfade, Math.Clamp(value, 0, 1)); }
    public double Master { get => Volatile.Read(ref _master); set => Volatile.Write(ref _master, Math.Clamp(value, 0, 1)); }
    public float PeakOut { get; private set; }

    public static (double a, double b) EqualPower(double x) => (Math.Cos(x * Math.PI / 2), Math.Sin(x * Math.PI / 2));

    public void Render(Span<float> output)
    {
        if (_scratch.Length < output.Length) { _scratch = new float[output.Length]; _a = new float[output.Length]; _b = new float[output.Length]; }
        var a = _a.AsSpan(0, output.Length); var b = _b.AsSpan(0, output.Length); var s = _scratch.AsSpan(0, output.Length);
        A.Render(s, a);
        B.Render(s, b);
        var (ga, gb) = EqualPower(Crossfader);
        float fa = (float)(ga * Master), fb = (float)(gb * Master), peak = 0;
        for (int i = 0; i < output.Length; i++) output[i] = a[i] * fa + b[i] * fb;
        _limiter.Process(output);
        for (int i = 0; i < output.Length; i++) { float v = Math.Abs(output[i]); if (v > peak) peak = v; }
        PeakOut = peak;
    }
}

/// <summary>Decodes any format ffmpeg understands to 44.1 kHz float stereo.</summary>
public static class TrackDecoder
{
    public static float[] Decode(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-i", path, "-f", "f32le", "-ac", "2", "-ar", "44100", "-" }) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not start");
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0 || ms.Length == 0) throw new InvalidOperationException("ffmpeg: " + err.Result.Trim());
        var bytes = ms.GetBuffer().AsSpan(0, (int)(ms.Length / 8 * 8));
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }
}

/// <summary>Streams the mix to the first available player: pw-cat (PipeWire), pacat (PulseAudio) or aplay (ALSA).
/// The blocking pipe paces the render loop.</summary>
public sealed class AudioOut : IDisposable
{
    public const int BlockFrames = 512;
    private readonly Mixer _mixer;
    private Process? _player;
    private Thread? _thread;
    private volatile bool _run;
    public string Backend { get; private set; } = "none";
    public string? Error { get; private set; }

    public AudioOut(Mixer mixer) => _mixer = mixer;

    public static (string exe, string[] args)[] Candidates =>
    [
        // pacat first: it always reads raw PCM from stdin and also runs on PipeWire through pipewire-pulse
        ("pacat", ["--playback", "--format", "float32le", "--rate", "44100", "--channels", "2", "--latency-msec", "40"]),
        ("pw-cat", ["--playback", "--raw", "--format", "f32", "--rate", "44100", "--channels", "2", "--latency", "40ms", "-"]),
        ("aplay", ["-q", "-f", "FLOAT_LE", "-r", "44100", "-c", "2", "-B", "60000"]),
    ];

    public static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

    public bool Start()
    {
        foreach (var (exe, args) in Candidates)
        {
            if (FindOnPath(exe) is not { } full) continue;
            var psi = new ProcessStartInfo(full) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try { _player = Process.Start(psi); } catch { continue; }
            if (_player == null) continue;
            _player.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Error = e.Data; };
            _player.BeginErrorReadLine();
            if (_player.WaitForExit(500))   // rejected our options or found no sound server: try the next player
            {
                _player.Dispose(); _player = null; continue;
            }
            Error = null;
            Backend = exe;
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "mixdock-audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return true;
        }
        Error = "No audio player found. Install pulseaudio-utils (pacat, also works on PipeWire), pipewire (pw-cat) or alsa-utils (aplay).";
        return false;
    }

    private void Loop()
    {
        var buf = new float[BlockFrames * 2];
        try
        {
            var stdin = _player!.StandardInput.BaseStream;
            while (_run)
            {
                _mixer.Render(buf);
                stdin.Write(MemoryMarshal.AsBytes(buf.AsSpan()));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (_run) Error = $"{Backend} stopped: {e.Message}";
        }
    }

    public void Dispose()
    {
        _run = false;
        try { _player?.StandardInput.Close(); } catch { }
        try { if (_player is { HasExited: false }) _player.Kill(); } catch { }
        _thread?.Join(500);
        _player?.Dispose();
    }
}
