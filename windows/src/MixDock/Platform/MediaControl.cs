using MixDock.Audio;
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
    /// <summary>Seconds left in the track a source app plays, from its media session timeline (null when the app
    /// publishes none). Auto Mix uses it to plan a live deck's exit before the song ends.</summary>
    public static async Task<double?> GetRemainingAsync(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            var infos = sessions.Select(s => new MediaSessionInfo(s.SourceAppUserModelId ?? "",
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)).ToList();
            int i = MediaSessionMatch.Pick(infos, processName, wantPause: true);
            if (i < 0) return null;
            var t = sessions[i].GetTimelineProperties();
            if (t == null || t.EndTime <= t.StartTime || t.EndTime.TotalSeconds < 20) return null;
            var position = t.Position;
            if (infos[i].Playing) position += DateTimeOffset.Now - t.LastUpdatedTime;
            double left = (t.EndTime - position).TotalSeconds;
            return left is > 0 and < 6 * 3600 ? left : null;
        }
        catch (Exception e)
        {
            Log.Write("MediaControl timeline: " + e.Message);
            return null;
        }
    }
    /// <summary>Title, artist, album, length and position of what a source app plays, from its media session.</summary>
    public static async Task<NowPlaying?> GetNowPlayingAsync(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            var infos = sessions.Select(s => new MediaSessionInfo(s.SourceAppUserModelId ?? "",
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)).ToList();
            int i = MediaSessionMatch.Pick(infos, processName, wantPause: true);
            if (i < 0) return null;
            var props = await sessions[i].TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
            var t = sessions[i].GetTimelineProperties();
            double duration = 0; double? position = null;
            if (t != null && t.EndTime > t.StartTime)
            {
                duration = (t.EndTime - t.StartTime).TotalSeconds;
                var at = t.Position - t.StartTime;
                if (infos[i].Playing) at += DateTimeOffset.Now - t.LastUpdatedTime;
                position = at.TotalSeconds;
            }
            return new NowPlaying(new TrackMetadata(props.Title, props.Artist ?? "", props.AlbumTitle ?? "", duration), position);
        }
        catch (Exception e)
        {
            Log.Write("MediaControl now playing: " + e.Message);
            return null;
        }
    }
}