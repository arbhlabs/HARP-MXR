using MixDock.Audio;
using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

/// <summary>
/// "Deck browser": opens a Chromium browser with its own profile for one deck. A separate profile
/// means a separate process tree, so per-process capture can take that deck's tabs apart from the other deck's.
/// </summary>
public sealed partial class MainForm
{
    public const string DeckBrowserHint = "Opens a separate browser window just for this deck, so each deck can play its own tab.";
    private readonly CancellationTokenSource?[] _browserWait = new CancellationTokenSource?[2];
    private readonly ToolTip _deckBrowserTip = new() { InitialDelay = 300, AutoPopDelay = 8000 };
    private DeckBrowserService? _deckBrowser;

    private StudioButton DeckBrowserButton(StudioButton b, int d)
    {
        b.AccessibleName = $"Deck {DeckName(d)} browser";
        b.AccessibleDescription = DeckBrowserHint;
        _deckBrowserTip.SetToolTip(b, DeckBrowserHint);
        return b;
    }

    private void OnDeckBrowserClick(int d, Control anchor)
    {
        if (_preview) { SetMessage("Deck browser is off in UI preview."); return; }
        _deckBrowser ??= new DeckBrowserService(new SystemBrowserProbe(), new ProcessBrowserLauncher(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var installed = _deckBrowser.Installed();
        if (installed.Count == 0) { SetMessage("No Chrome, Vivaldi, Edge or Brave found. Install one to use a deck browser.", true); return; }
        string? current = Panel(d).SelectedApp?.Name.Split('@')[0];
        var pick = BrowserDetector.Choose(installed, current);
        bool currentIsBrowser = pick != null && pick.Key.Equals(current, StringComparison.OrdinalIgnoreCase);
        if (installed.Count == 1 || currentIsBrowser) { _ = SafeAsync(() => LaunchDeckBrowserAsync(d, pick!)); return; }
        var menu = new ContextMenuStrip();
        foreach (var b in installed)
        {
            var info = b;
            menu.Items.Add(info.Name, null, (_, _) => _ = SafeAsync(() => LaunchDeckBrowserAsync(d, info)));
        }
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private async Task LaunchDeckBrowserAsync(int d, BrowserInfo browser)
    {
        var panel = Panel(d); string deckName = DeckName(d);
        _browserWait[d]?.Cancel();
        var cts = _browserWait[d] = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            panel.SetStatus("Opening browser…");
            var service = _deckBrowser!;
            await Task.Run(() => service.Launch(browser, d, null), ct);
            SetMessage($"{browser.Name} opened for deck {deckName}. Play something in it and the deck connects on its own.");
            panel.SetStatus("Waiting for browser audio…");
            string group = DeckBrowserCommand.GroupName(browser.Key, d);
            var until = DateTime.UtcNow.AddMinutes(5);
            while (!ct.IsCancellationRequested && !_closing && DateTime.UtcNow < until)
            {
                await Task.Delay(1000, ct);
                if (panel.Sources.DroppedDown) continue;
                var apps = await Task.Run(AudioDiscovery.ActiveAudioProcesses, ct);
                var hit = apps.FirstOrDefault(a => a.Name.Equals(group, StringComparison.OrdinalIgnoreCase));
                if (hit is null) continue;
                _a.SetApps(apps); _b.SetApps(apps);
                var item = panel.Sources.Items.OfType<AudioApp>().FirstOrDefault(a => a.Name.Equals(group, StringComparison.OrdinalIgnoreCase));
                if (item is null) continue;
                panel.Sources.SelectedItem = item;
                bool deckA = d == 0;
                await ArmAsync(panel, deckA ? _engine.DeckA : _engine.DeckB, deckA ? _engine.DeckB : _engine.DeckA, deckA ? _takeoverA : _takeoverB);
                return;
            }
            if (!ct.IsCancellationRequested && !_closing)
            {
                panel.SetStatus("No browser audio yet");
                SetMessage($"Deck {deckName} is still waiting. Play something in its browser window, then pick it in the list.", true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("DeckBrowser: " + ex.Message);
            panel.SetStatus("Could not open browser");
            SetMessage($"Could not open {browser.Name} for deck {deckName}.", true);
        }
    }
}
