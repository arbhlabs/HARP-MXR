namespace MixDock.Platform;

public sealed record OutputEndpoint(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Pure output-device policy (unit-tested): MixDock's output is always a real device, never a virtual cable
/// or the Steam Streaming sink, and never the endpoint apps are redirected to.</summary>
public static class OutputPolicy
{
    /// <summary>The user's selected real output (set by the UI); used when an app was already playing to a virtual device.</summary>
    public static volatile string? PreferredId;

    public static bool IsVirtual(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string n = name.ToLowerInvariant();
        return n.Contains("cable") || n.Contains("steam streaming") || n.Contains("voicemeeter") || n.Contains("virtual")
            || n.Contains("vb-audio") || n.Contains("fxsound");
    }

    /// <summary>Saved choice if it is still a real device that is not the redirect sink; else the Windows default
    /// if real; else the first real device; else null.</summary>
    public static OutputEndpoint? Pick(IReadOnlyList<OutputEndpoint> all, string? savedId, string? defaultId, params string?[] sinkIds)
    {
        var real = all.Where(d => !IsVirtual(d.Name) && !sinkIds.Contains(d.Id)).ToList();
        return real.FirstOrDefault(d => savedId != null && d.Id == savedId)
            ?? real.FirstOrDefault(d => defaultId != null && d.Id == defaultId)
            ?? real.FirstOrDefault();
    }

    public static bool IsAllowed(OutputEndpoint? e, params string?[] sinkIds) => e != null && !IsVirtual(e.Name) && !sinkIds.Contains(e.Id);
}

/// <summary>Apps MixDock must never capture or redirect (for example a voice chat or game you want left alone).
/// Configured with <c>ProtectedApps</c> in config.json; empty by default. Matching is a case-insensitive substring
/// of the process name.</summary>
public static class ProtectedApps
{
    static volatile string[] _names = Array.Empty<string>();

    public static void Set(IEnumerable<string>? names) =>
        _names = (names ?? Array.Empty<string>()).Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();

    public static bool IsProtected(string? appName) =>
        !string.IsNullOrEmpty(appName) && _names.Any(n => appName.Contains(n, StringComparison.OrdinalIgnoreCase));

    public static string Summary => _names.Length == 0 ? "none" : string.Join(", ", _names);
}
