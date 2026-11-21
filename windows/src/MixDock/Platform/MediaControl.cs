using Windows.Media.Control;

namespace MixDock.Platform;

public enum MediaResult { Done, NoSession, Refused }

/// <summary>Play / pause a source app through the Windows media session API (the same control as the media keys).</summary>
public static class MediaControl
{
    public static async Task<(MediaResult Result, string? AppId)> SetPlayingAsync(string? processName, bool play)
    {
        if (string.IsNullOrWhiteSpace(processName)) return (MediaResult.NoSession, null);
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            var infos = sessions.Select(s => new MediaSessionInfo(s.SourceAppUserModelId ?? "",
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)).ToList();
            int i = MediaSessionMatch.Pick(infos, processName, wantPause: !play);
            if (i < 0) return (MediaResult.NoSession, null);
            bool ok = play ? await sessions[i].TryPlayAsync() : await sessions[i].TryPauseAsync();
            return (ok ? MediaResult.Done : MediaResult.Refused, infos[i].AppId);
        }
        catch (Exception e)
        {
            Log.Write("MediaControl: " + e.Message);
            return (MediaResult.NoSession, null);
        }
    }
}
