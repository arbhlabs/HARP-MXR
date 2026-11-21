using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

/// <summary>Deck pause from inside MixDock. Song files pause their decoder. Live app decks send play/pause to the app's
/// Windows media session (Spotify, browser tabs); when no session matches, the deck is frozen in MixDock instead (the app
/// keeps playing, its audio is dropped). Either way the deck's look-ahead delay line holds still and resumes seamlessly.</summary>
public sealed partial class MainForm
{
    private readonly bool[] _livePaused = new bool[2];
    private readonly int?[] _pausedPid = new int?[2];
    private readonly string?[] _pauseLabel = new string?[2];

    private bool DeckIsPaused(int d) => Source(d).IsFile ? Source(d).Paused : _livePaused[d];

    private void DeckPlayPause(int d)
    {
        var src = Source(d);
        if (src.IsFile) { SongPlayPause(d); SetMessage(src.Paused ? $"Deck {DeckName(d)} paused." : $"Deck {DeckName(d)} playing."); return; }
        if (src.ActivePid is not > 0) { SetMessage($"Deck {DeckName(d)} has no source to pause."); return; }
        _ = SetLivePausedAsync(d, !_livePaused[d]);
    }

    /// <summary>Pause both decks if anything plays, otherwise resume both.</summary>
    private void PauseAll()
    {
        bool anyPlaying = Enumerable.Range(0, 2).Any(d => (Source(d).IsFile || Source(d).ActivePid is > 0) && !DeckIsPaused(d));
        for (int d = 0; d < 2; d++)
        {
            var src = Source(d);
            if (src.IsFile) { if (src.Paused != anyPlaying) SongPlayPause(d); }
            else if (src.ActivePid is > 0 && _livePaused[d] != anyPlaying) _ = SetLivePausedAsync(d, anyPlaying);
        }
        SetMessage(anyPlaying ? "All decks paused. Pause all again (Ctrl+Alt+Shift+Space) resumes them." : "All decks playing.");
    }

    private async Task SetLivePausedAsync(int d, bool pause)
    {
        var src = Source(d); var rd = Reader(d);
        _livePaused[d] = pause; _pausedPid[d] = pause ? src.ActivePid : null;
        // freeze first so the pause is instant; the app follows through its media session
        src.Frozen = pause; rd.Frozen = pause;
        string? name = Panel(d).SelectedApp?.Name;
        var (result, appId) = _preview ? (MediaResult.NoSession, null) : await MediaControl.SetPlayingAsync(name, play: !pause);
        string app = AppPicker.DisplayName(name ?? "app");
        if (!pause) { _pauseLabel[d] = null; SetMessage($"Deck {DeckName(d)} playing{(result == MediaResult.Done ? $" ({app} resumed)" : "")}."); }
        else if (result == MediaResult.Done) { _pauseLabel[d] = "PAUSED"; SetMessage($"Deck {DeckName(d)} paused: {app} paused through Windows media controls."); }
        else { _pauseLabel[d] = "FROZEN"; SetMessage($"Deck {DeckName(d)} frozen in MixDock: {app} has no media session to pause, so it keeps playing unheard.", result == MediaResult.Refused); }
        Log.Write($"pause deck {d} -> {pause}: {result} {appId}");
        RefreshDj();
    }

    /// <summary>A new source on a paused live deck (or a released deck) clears the freeze.</summary>
    private void CheckPauseSource(int d)
    {
        if (_livePaused[d] && Source(d).ActivePid != _pausedPid[d]) ClearLivePause(d);
    }

    private void ClearLivePause(int d)
    {
        _livePaused[d] = false; _pausedPid[d] = null; _pauseLabel[d] = null;
        Source(d).Frozen = false; Reader(d).Frozen = false;
    }

    /// <summary>Live decks get the configured look-ahead; song files need none (their whole waveform is known).</summary>
    private void ApplyLookAhead(int d)
    {
        var src = Source(d);
        bool live = !src.IsFile && src.ActivePid is > 0;
        Reader(d).ForwardLive = live;      // live app audio: forward-only playhead, drift correction, crossfaded overflow
        Reader(d).LookAheadMs = live ? (int)(_config.LookAhead(d) * 1000) : 0;
    }
}
