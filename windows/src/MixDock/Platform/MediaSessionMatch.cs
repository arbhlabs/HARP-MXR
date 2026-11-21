namespace MixDock.Platform;

/// <summary>One Windows media session (System Media Transport Controls) as the matcher sees it.</summary>
public sealed record MediaSessionInfo(string AppId, bool Playing);

/// <summary>
/// Pure matching of a deck's captured process to a media session (unit-tested). Session app ids look like
/// "Spotify.exe", "chrome.exe", "msedge.exe" or a packaged id such as "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify".
/// </summary>
public static class MediaSessionMatch
{
    public static string Normalize(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s.EndsWith(".exe") ? s[..^4] : s;
    }

    /// <summary>3 = exact exe or app entry, 2 = a whole part of a packaged id, 1 = substring, 0 = no match.</summary>
    public static int Score(string? appId, string? processName)
    {
        string p = Normalize(processName), a = (appId ?? "").ToLowerInvariant();
        if (p.Length == 0 || a.Length == 0) return 0;
        foreach (var t in a.Split('!', '\\', '/'))
            if (Normalize(t) == p) return 3;
        foreach (var t in a.Split('!', '_', '.', '\\', '/', ' '))
            if (t == p) return 2;
        return p.Length >= 5 && a.Contains(p) ? 1 : 0;
    }

    /// <summary>Best session for the process, or -1. Among equal matches, prefer one whose state the command changes
    /// (a playing session to pause, a paused one to play), so the right browser tab is hit.</summary>
    public static int Pick(IReadOnlyList<MediaSessionInfo> sessions, string? processName, bool wantPause)
    {
        int best = -1, bestKey = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            int s = Score(sessions[i].AppId, processName);
            if (s == 0) continue;
            int key = s * 2 + (sessions[i].Playing == wantPause ? 1 : 0);
            if (key > bestKey) { bestKey = key; best = i; }
        }
        return best;
    }
}
