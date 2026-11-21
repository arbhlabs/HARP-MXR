namespace MixDock.Audio;

/// <summary>Real mixer behind AutoMix.</summary>
public sealed class EngineMixHost : IMixHost
{
    private readonly AudioEngine _e;
    /// <summary>Raised on the audio-control side when AutoMix moves the crossfader (UI should follow).</summary>
    public event Action<double>? CrossfaderMoved;
    public EngineMixHost(AudioEngine e) { _e = e; }
    private DeckSource Src(int d) => d == 0 ? _e.DeckA : _e.DeckB;
    public bool HasSource(int deck) => Src(deck).ActivePid != null;
    public float Level(int deck) => deck == 0 ? _e.A.Peak : _e.B.Peak;
    public double Bpm(int deck) => DeckSync.HeardBpm(_e, deck);
    public double Phase(int deck) => DeckSync.HeardPhase(_e, deck);
    public double Crossfader
    {
        get => _e.Mixer.Crossfader;
        set { _e.Mixer.Crossfader = value; CrossfaderMoved?.Invoke(value); }
    }
    public void SetFadeGain(int deck, double gain) => _e.Mixer.SetFadeGain(deck, gain);
    public void SetAutoLowDb(int deck, double db) => (deck == 0 ? _e.A : _e.B).AutoLowDb = db;
    public void SyncTempo(int slave) { var msg = DeckSync.Sync(_e, slave); Log.Write("AutoMix sync: " + msg); }
    public void GlideTempo(int deck, double toward1) => DeckSync.Glide(_e, deck, toward1);
}
