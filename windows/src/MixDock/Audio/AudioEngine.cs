using MixDock.Platform;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixDock.Audio;

public sealed class AudioEngine : IAsyncDisposable
{
    private readonly SampleRing _aBuffer = new(44100 * 4), _bBuffer = new(44100 * 4);
    public readonly DeckDsp A = new(), B = new();
    public readonly DeckReader ReaderA, ReaderB;
    public readonly MasterMixer Mixer;
    public readonly CueMixer Cue;
    public readonly DeckSource DeckA, DeckB;
    public readonly SyncController Sync;
    private WasapiPlayer? _player, _cuePlayer;
    private string? _outputId, _cueId;
    private MMDevice? _outputDevice, _cueDevice;
    public string? OutputId => _outputId;
    public string? CueId => _cueId;
    public string? CueName { get; private set; }
    /// <summary>Requested shared-mode buffer for both outputs (applies the next time an output is (re)opened).</summary>
    public LatencyProfile Profile { get; set; } = LatencyProfile.Standard;
    /// <summary>Bounded render-callback timing of the main mix.</summary>
    public TimingSnapshot Timing => Mixer.Timing.Snapshot();
    public AudioEngine()
    {
        ReaderA = new DeckReader(_aBuffer, A); ReaderB = new DeckReader(_bBuffer, B);
        Mixer = new MasterMixer(ReaderA, ReaderB);
        Cue = new CueMixer(Mixer);
        DeckA = new DeckSource(_aBuffer, A);
        DeckB = new DeckSource(_bBuffer, B);
        ReaderA.History = DeckA.History; ReaderB.History = DeckB.History;
        Sync = new SyncController(DeckA, ReaderA, DeckB, ReaderB);
        Mixer.Sync = Sync;
    }
    public async Task SetOutputAsync(string endpointId)
    {
        if (endpointId == _outputId && _player != null) return;
        if (!OutputSeparation.CueAllowed(endpointId, _cueId)) { Log.Write("output is the cue device: cue stopped"); await StopCueAsync(); }
        await StopOutputAsync();
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(endpointId);
        if (OutputPolicy.IsVirtual(device.FriendlyName)) { string n = device.FriendlyName; device.Dispose(); throw new InvalidOperationException($"Refusing virtual output '{n}'"); }
        Log.Write($"output player: {device.FriendlyName} mix format {device.AudioClient.MixFormat}, requested {Profile.RequestedMs} ms ({Profile.Name})");
        var player = BuildPlayer(device);
        try
        {
            player.Init(Mixer.ToWaveProvider());
            player.Play();
            _player = player;
            _outputDevice = device;
            _outputId = endpointId;
        }
        catch { await player.DisposeAsync(); device.Dispose(); throw; }
    }
    /// <summary>Shared-mode, event-driven player (never exclusive, so other apps keep using the endpoint). Disposes the device if the build fails.</summary>
    private WasapiPlayer BuildPlayer(MMDevice device)
    {
        try
        {
            return new WasapiPlayerBuilder().WithDevice(device)
                .WithSharedMode().WithEventSync().WithLatency(Profile.RequestedMs)
                .WithMmcssThreadPriority("Pro Audio").Build();
        }
        catch { device.Dispose(); throw; }
    }
    public async Task StopOutputAsync()
    {
        var old = _player; _player = null; _outputId = null;
        if (old != null) await old.DisposeAsync();
        _outputDevice?.Dispose(); _outputDevice = null;
    }

    /// <summary>Second output for headphone cue. Never the main device.</summary>
    public async Task SetCueOutputAsync(string? endpointId)
    {
        if (endpointId == _cueId && (_cuePlayer != null || endpointId == null)) return;
        await StopCueAsync();
        if (endpointId == null) return;
        if (!OutputSeparation.CueAllowed(_outputId, endpointId)) throw new InvalidOperationException("The headphone cue cannot use the main output device");
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(endpointId);
        Log.Write($"cue player: {device.FriendlyName} mix format {device.AudioClient.MixFormat}, requested {Profile.RequestedMs} ms ({Profile.Name})");
        var player = BuildPlayer(device);
        try
        {
            player.Init(Cue.ToWaveProvider());
            player.Play();
            _cuePlayer = player; _cueDevice = device; _cueId = endpointId; CueName = device.FriendlyName;
            Mixer.TapMaster = true;
        }
        catch { await player.DisposeAsync(); device.Dispose(); throw; }
    }
    public async Task StopCueAsync()
    {
        var old = _cuePlayer; _cuePlayer = null; _cueId = null; CueName = null;
        Mixer.TapMaster = false; Mixer.CueA = Mixer.CueB = false;
        if (old != null) await old.DisposeAsync();
        _cueDevice?.Dispose(); _cueDevice = null;
    }

    /// <summary>Best other output for headphones: not the main device, not the hidden sink, not virtual.</summary>
    public static string? ChooseCueDevice(string? mainId, string? sinkId)
    {
        using var en = new MMDeviceEnumerator();
        static int Rank(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("cable") || n.Contains("voicemeeter") || n.Contains("virtual") || n.Contains("steam streaming") || n.Contains("fxsound")) return 99;
            if (n.Contains("headphone") || n.Contains("headset") || n.Contains("earbud") || n.Contains("airpods") || n.Contains("buds") || n.Contains("bluetooth") || n.Contains("spark")) return 0;
            if (n.Contains("speakers") || n.Contains("realtek")) return 1;
            return 5;
        }
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => d.ID != mainId && d.ID != sinkId)
            .Select(d => (d, r: Rank(d.FriendlyName))).Where(x => x.r < 99).OrderBy(x => x.r).FirstOrDefault().d?.ID;
    }

    public async ValueTask DisposeAsync()
    {
        await DeckA.StopAsync();
        await DeckB.StopAsync();
        await StopCueAsync();
        await StopOutputAsync();
    }
}
