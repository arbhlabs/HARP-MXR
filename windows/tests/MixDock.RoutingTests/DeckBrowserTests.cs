using MixDock.Platform;

delegate void CheckFn(bool ok, string name, string detail = "");

static class DeckBrowserTests
{
    sealed class FakeProbe : IBrowserProbe
    {
        public Dictionary<string, string> Registry = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Vars = new() { ["ProgramFiles"] = @"C:\PF", ["ProgramFiles(x86)"] = @"C:\PF86", ["LocalAppData"] = @"C:\LA" };
        public string? AppPath(string exe) => Registry.GetValueOrDefault(exe);
        public bool FileExists(string p) => Files.Contains(p);
        public string? Env(string n) => Vars.GetValueOrDefault(n);
    }
    sealed class FakeLauncher : IBrowserLauncher
    {
        public List<BrowserLaunch> Launched = new();
        public int Launch(BrowserLaunch l) { Launched.Add(l); return 4242; }
    }

    public static void Run(CheckFn check)
    {
        Console.WriteLine("--- deck browser: detection");
        var probe = new FakeProbe();
        probe.Registry["chrome.exe"] = @"C:\Reg\Chrome\chrome.exe"; probe.Files.Add(@"C:\Reg\Chrome\chrome.exe");
        probe.Files.Add(@"C:\LA\Vivaldi\Application\vivaldi.exe");
        probe.Files.Add(@"C:\PF86\Microsoft\Edge\Application\msedge.exe");
        probe.Registry["brave.exe"] = @"C:\Gone\brave.exe"; // registered but the file is missing
        var found = BrowserDetector.Detect(probe);
        check(found.Select(b => b.Key).SequenceEqual(new[] { "chrome", "vivaldi", "msedge" }), "finds Chrome, Vivaldi and Edge in a fixed order; skips a stale registry entry", string.Join(",", found.Select(b => b.Key)));
        check(found[0].ExePath == @"C:\Reg\Chrome\chrome.exe", "registry App Paths wins over guessing", found[0].ExePath);
        check(found[1].ExePath == @"C:\LA\Vivaldi\Application\vivaldi.exe", "known install path is used when the registry has nothing");
        check(BrowserDetector.Detect(new FakeProbe()).Count == 0, "no browsers installed gives an empty list");
        check(BrowserDetector.Choose(found, "vivaldi")?.Key == "vivaldi", "the deck's current Chromium source is preferred");
        check(BrowserDetector.Choose(found, "spotify")?.Key == "chrome", "a non-browser source falls back to the first one found");
        check(BrowserDetector.Choose(found, null)?.Key == "chrome", "no source falls back to the first one found");
        check(BrowserDetector.Choose(new List<BrowserInfo>(), "chrome") is null, "nothing installed chooses nothing");
        check(BrowserDetector.IsChromium("msedge") && BrowserDetector.IsChromium("Vivaldi") && !BrowserDetector.IsChromium("firefox"), "Chromium process names are recognised");

        Console.WriteLine("--- deck browser: command line");
        var chrome = found[0];
        var a = DeckBrowserCommand.Build(chrome, 0, @"C:\LA", null);
        var b = DeckBrowserCommand.Build(chrome, 1, @"C:\LA", null);
        check(a.Args[0] == @"--user-data-dir=C:\LA\HARP-MXR\deck-browser\chrome-deck-A", "deck A gets its own profile folder", a.Args[0]);
        check(b.Args[0] == @"--user-data-dir=C:\LA\HARP-MXR\deck-browser\chrome-deck-B", "deck B gets a different profile folder", b.Args[0]);
        check(a.Args.Contains("--no-first-run") && a.Args.Contains("--no-default-browser-check") && a.Args.Count == 3, "first-run and default-browser prompts are off, nothing else added");
        check(a.ExePath == chrome.ExePath && a.ProfileDir == @"C:\LA\HARP-MXR\deck-browser\chrome-deck-A", "launch targets the chosen browser and reports the profile folder");
        check(DeckBrowserCommand.ProfileDir(@"C:\LA", found[1], 1).EndsWith(@"vivaldi-deck-B"), "profile folders are per browser too");
        check(DeckBrowserCommand.Build(chrome, 0, @"C:\LA", "https://soundcloud.com").Args[^1] == "https://soundcloud.com/", "an https start URL is appended");
        check(DeckBrowserCommand.Build(chrome, 0, @"C:\LA", "javascript:alert(1)").Args.Count == 3, "a non-web start URL is ignored");
        check(DeckBrowserCommand.Build(chrome, 0, @"C:\LA", "not a url").Args.Count == 3, "a malformed start URL is ignored");

        Console.WriteLine("--- deck browser: fake launcher");
        var fake = new FakeLauncher();
        var svc = new DeckBrowserService(probe, fake, @"C:\LA");
        var (l, pid) = svc.Launch(found[2], 1, null);
        check(pid == 4242 && fake.Launched.Count == 1 && ReferenceEquals(fake.Launched[0], l), "the service hands the built command to the launcher and returns its pid");
        check(fake.Launched[0].Args[0].EndsWith(@"msedge-deck-B") && fake.Launched[0].ExePath == found[2].ExePath, "Edge on deck B launches with its own folder");
        check(svc.Installed().Count == 3, "the service lists installed browsers through the probe");

        Console.WriteLine("--- deck browser: recognising a deck browser's process");
        string exe = "\"C:\\Reg\\Chrome\\chrome.exe\"";
        var root = DeckBrowserCommand.ParseRoot($"{exe} --user-data-dir={a.ProfileDir} --no-first-run --no-default-browser-check");
        check(root == ("chrome", 0), "main process of a deck A browser is recognised", root?.ToString() ?? "null");
        var quoted = DeckBrowserCommand.ParseRoot($"{exe} \"--user-data-dir={b.ProfileDir}\" --no-first-run");
        check(quoted == ("chrome", 1), "a quoted profile argument is recognised", quoted?.ToString() ?? "null");
        check(DeckBrowserCommand.ParseRoot($"{exe} --type=renderer --user-data-dir={a.ProfileDir}") is null, "child processes (--type=) are not roots");
        check(DeckBrowserCommand.ParseRoot($"{exe} --profile-directory=Default") is null, "a normal browser is not a deck browser");
        check(DeckBrowserCommand.ParseRoot($"{exe} --user-data-dir=C:\\Other\\Profile") is null, "a foreign profile folder is not a deck browser");
        check(DeckBrowserCommand.ParseRoot(null) is null, "no command line is not a deck browser");
        check(DeckBrowserCommand.GroupName("chrome", 1) == "chrome@deck-b", "deck browser audio gets its own picker group");
        check(DeckBrowserCommand.GroupLabel("chrome@deck-b", n => "Chrome") == "Chrome · Deck B browser", "picker label names the deck");
        check(DeckBrowserCommand.GroupLabel("chrome", n => n) is null, "a normal app name has no deck label");
    }
}
