using MixDock.Audio;
using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

/// <summary>Deck pause from inside MixDock. Song files pause their decoder. Live app decks send play/pause to the app's
/// Windows media session (Spotify, browser tabs); when no session matches, the deck is frozen in HARP//MXR instead (the app
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
        string? name = Panel(d).SelectedApp?.Name;
        string app = AppPicker.DisplayName(name ?? "app");
        if (pause)
        {
            // Buffered pause: the deck stops instantly but keeps recording the app, so the waveform fills in ahead of the
            // playhead and nothing is lost. TickBufferedPause holds the app itself once the buffer is full.
            rd.Frozen = true; src.Frozen = false; _appHeld[d] = false; _pauseLabel[d] = "PAUSED";
            SetMessage($"Deck {DeckName(d)} paused. Still recording {app}: up to {DeckReader.MaxShiftMs / 1000} s is kept, so you resume where you stopped.");
            Log.Write($"pause deck {d}: buffered"); RefreshDj(); return;
        }
        bool held = _appHeld[d]; _appHeld[d] = false;
        src.Frozen = false; rd.Frozen = false;      // continues from the paused frame with a deeper delay
        if (held && !_preview) src.MeasureResume();
        var (result, appId) = _preview || !held ? (MediaResult.NoSession, null) : await MediaControl.SetPlayingAsync(name, play: true);
        if (result != MediaResult.Done) src.CancelResumeMeasurement();
        _pauseLabel[d] = null;
        int behind = BehindLiveMs(d) / 1000;
        SetMessage($"Deck {DeckName(d)} playing{(result == MediaResult.Done ? $" ({app} resumed)" : "")}{(behind > 0 ? $", {behind} s behind live. Click the deck time to jump to live." : ".")}");
        Log.Write($"resume deck {d}: {result} {appId}, {behind} s behind live");
        RefreshDj();
    }

    private readonly bool[] _appHeld = new bool[2]; private readonly bool[] _holding = new bool[2];

    /// <summary>Called on every UI refresh. When a paused deck has filled its time-shift buffer, the app itself is paused
    /// through its Windows media session, so the stream waits instead of running past the buffer. Apps without a media
    /// session keep playing unheard from then on (shown as FROZEN).</summary>
    private void TickBufferedPause(int d)
    {
        var src = Source(d); var rd = Reader(d);
        if (!_livePaused[d] || _appHeld[d] || _holding[d] || src.Frozen || rd.BufferedMs < DeckReader.MaxShiftMs) return;
        src.Frozen = true; _holding[d] = true;      // stop recording at once; the queue is full
        _ = HoldAppAsync(d);
    }

    private async Task HoldAppAsync(int d)
    {
        try
        {
            string? name = Panel(d).SelectedApp?.Name; string app = AppPicker.DisplayName(name ?? "app");
            var (result, appId) = _preview ? (MediaResult.NoSession, null) : await MediaControl.SetPlayingAsync(name, play: false);
            if (!_livePaused[d]) { if (result == MediaResult.Done) await MediaControl.SetPlayingAsync(name, play: true); return; }
            _appHeld[d] = result == MediaResult.Done;
            _pauseLabel[d] = _appHeld[d] ? "PAUSED" : "FROZEN";
            SetMessage(_appHeld[d] ? $"Deck {DeckName(d)}: buffer full, {app} is paused too and waits for you."
                                   : $"Deck {DeckName(d)}: buffer full and {app} has no media session to pause, so it plays on unheard.", !_appHeld[d]);
            Log.Write($"pause deck {d}: buffer full -> {result} {appId}");
        }
        finally { _holding[d] = false; }
    }

    /// <summary>How far behind the app the deck plays after a buffered pause, beyond its configured look-ahead.</summary>
    private int BehindLiveMs(int d) => Reader(d).ShiftedMs((int)(_config.LookAhead(d) * 1000));

    /// <summary>Drop the time-shift and play what the app is playing right now (crossfaded skip forward).</summary>
    private void DeckGoLive(int d)
    {
        var src = Source(d);
        if (src.IsFile || src.ActivePid is not > 0 || _livePaused[d]) return;
        if (BehindLiveMs(d) < 500) return;
        ApplyLookAhead(d); SetMessage($"Deck {DeckName(d)} is back at live.");
    }

    /// <summary>A new source on a paused live deck (or a released deck) clears the freeze.</summary>
    private void CheckPauseSource(int d)
    {
        if (_livePaused[d] && Source(d).ActivePid != _pausedPid[d]) ClearLivePause(d);
    }

    private void ClearLivePause(int d)
    {
        _livePaused[d] = false; _pausedPid[d] = null; _pauseLabel[d] = null; _appHeld[d] = false;
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
