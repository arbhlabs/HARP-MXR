using System.Diagnostics;
using Microsoft.Win32;

namespace MixDock.Platform;

/// <summary>A Chromium browser that can be launched with its own profile.</summary>
public sealed record BrowserInfo(string Key, string Name, string ExePath);

/// <summary>What the detector needs from the machine; tests supply a fake.</summary>
public interface IBrowserProbe
{
    /// <summary>Default value of HKCU/HKLM "...\App Paths\&lt;exe&gt;", or null.</summary>
    string? AppPath(string exeName);
    bool FileExists(string path);
    string? Env(string name);
}

public sealed class SystemBrowserProbe : IBrowserProbe
{
    public string? AppPath(string exeName)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (k?.GetValue(null) is string s && s.Length > 0) return s.Trim('"');
            }
            catch { /* unreadable key: treat as absent */ }
        }
        return null;
    }
    public bool FileExists(string path) => File.Exists(path);
    public string? Env(string name) => Environment.GetEnvironmentVariable(name);
}

public static class BrowserDetector
{
    // key (also the process name without .exe), display name, relative install paths
    static readonly (string key, string name, string[] rel)[] Known =
    {
        ("chrome", "Chrome", new[] { @"Google\Chrome\Application\chrome.exe" }),
        ("vivaldi", "Vivaldi", new[] { @"Vivaldi\Application\vivaldi.exe" }),
        ("msedge", "Microsoft Edge", new[] { @"Microsoft\Edge\Application\msedge.exe" }),
        ("brave", "Brave", new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" }),
    };

    public static bool IsChromium(string processName) =>
        Known.Any(k => k.key.Equals(processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Installed Chromium browsers in a fixed order: Chrome, Vivaldi, Edge, Brave.</summary>
    public static List<BrowserInfo> Detect(IBrowserProbe probe)
    {
        var roots = new[] { probe.Env("ProgramFiles"), probe.Env("ProgramFiles(x86)"), probe.Env("LocalAppData") }
            .Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).ToList();
        var found = new List<BrowserInfo>();
        foreach (var (key, name, rel) in Known)
        {
            string? exe = probe.AppPath(key + ".exe");
            if (exe == null || !probe.FileExists(exe))
                exe = roots.SelectMany(r => rel.Select(p => Path.Combine(r, p))).FirstOrDefault(probe.FileExists);
            if (exe != null) found.Add(new BrowserInfo(key, name, exe));
        }
        return found;
    }

    /// <summary>The deck's current source if it is Chromium and installed, else the first one found.</summary>
    public static BrowserInfo? Choose(IReadOnlyList<BrowserInfo> installed, string? currentSourceProcess)
    {
        var match = installed.FirstOrDefault(b => b.Key.Equals(currentSourceProcess, StringComparison.OrdinalIgnoreCase));
        return match ?? installed.FirstOrDefault();
    }
}

public sealed record BrowserLaunch(string ExePath, IReadOnlyList<string> Args, string ProfileDir);

public static class DeckBrowserCommand
{
    public const string ProfileFolder = "deck-browser";
    public const string UserDataFlag = "--user-data-dir=";
    public static char DeckLetter(int deck) => deck == 0 ? 'A' : 'B';

    public static string ProfileDir(string localAppData, BrowserInfo browser, int deck) =>
        Path.Combine(localAppData, "HARP-MXR", ProfileFolder, $"{browser.Key}-deck-{DeckLetter(deck)}");

    public static BrowserLaunch Build(BrowserInfo browser, int deck, string localAppData, string? startUrl = null)
    {
        string profile = ProfileDir(localAppData, browser, deck);
        var args = new List<string> { UserDataFlag + profile, "--no-first-run", "--no-default-browser-check" };
        if (Uri.TryCreate(startUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            args.Add(u.AbsoluteUri);
        return new BrowserLaunch(browser.ExePath, args, profile);
    }

    /// <summary>
    /// Recognises the main process of a deck browser from its command line (child processes carry
    /// --type= and are ignored). Returns the browser key and deck index.
    /// </summary>
    public static (string browser, int deck)? ParseRoot(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine) || commandLine.Contains(" --type=", StringComparison.Ordinal)) return null;
        int i = commandLine.IndexOf(UserDataFlag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = commandLine[(i + UserDataFlag.Length)..];
        if (rest.StartsWith('"')) rest = rest[1..];
        int m = rest.IndexOf(@"\HARP-MXR\" + ProfileFolder + @"\", StringComparison.OrdinalIgnoreCase);
        if (m < 0) return null;
        string leaf = rest[(m + (@"\HARP-MXR\" + ProfileFolder + @"\").Length)..];
        int end = leaf.IndexOfAny(new[] { '"', ' ', '\\' });
        if (end >= 0) leaf = leaf[..end];
        int d = leaf.LastIndexOf("-deck-", StringComparison.Ordinal);
        if (d <= 0 || leaf.Length != d + 7) return null;
        char c = char.ToUpperInvariant(leaf[^1]);
        if (c != 'A' && c != 'B') return null;
        return (leaf[..d].ToLowerInvariant(), c == 'A' ? 0 : 1);
    }

    /// <summary>Picker entry name for a deck browser's audio group ("chrome@deck-a").</summary>
    public static string GroupName(string browserKey, int deck) => $"{browserKey}@deck-{char.ToLowerInvariant(DeckLetter(deck))}";

    /// <summary>"Chrome · Deck A browser" for a group name, or null when it is not one.</summary>
    public static string? GroupLabel(string name, Func<string, string> displayName)
    {
        int at = name.IndexOf("@deck-", StringComparison.Ordinal);
        if (at <= 0 || name.Length != at + 7) return null;
        return $"{displayName(name[..at])} · Deck {char.ToUpperInvariant(name[^1])} browser";
    }
}

public interface IBrowserLauncher
{
    /// <summary>Starts the browser and returns its root process id.</summary>
    int Launch(BrowserLaunch launch);
}

public sealed class ProcessBrowserLauncher : IBrowserLauncher
{
    public int Launch(BrowserLaunch launch)
    {
        Directory.CreateDirectory(launch.ProfileDir);
        var psi = new ProcessStartInfo(launch.ExePath) { UseShellExecute = false };
        foreach (var a in launch.Args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The browser did not start.");
        return p.Id;
    }
}

/// <summary>Launch flow with every machine dependency injected, so tests never open a browser.</summary>
public sealed class DeckBrowserService
{
    readonly IBrowserProbe _probe; readonly IBrowserLauncher _launcher; readonly string _localAppData;
    public DeckBrowserService(IBrowserProbe probe, IBrowserLauncher launcher, string localAppData)
    { _probe = probe; _launcher = launcher; _localAppData = localAppData; }

    public List<BrowserInfo> Installed() => BrowserDetector.Detect(_probe);

    /// <summary>Launches <paramref name="browser"/> for the deck; returns the launch used and its root pid.</summary>
    public (BrowserLaunch launch, int pid) Launch(BrowserInfo browser, int deck, string? startUrl)
    {
        var launch = DeckBrowserCommand.Build(browser, deck, _localAppData, startUrl);
        return (launch, _launcher.Launch(launch));
    }
}
