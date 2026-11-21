using System.Diagnostics;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;
using MixDock.Ui;

namespace MixDock;

public sealed partial class MainForm : Form
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }
    private readonly AudioEngine _engine = new();
    private readonly TakeoverController _takeoverA, _takeoverB;
    private readonly MonitorBar _monitor = new();
    private readonly AutoMixBar _autoBar = new();
    private AutoMix? _autoMix;
    private EngineMixHost? _mixHost;
    private readonly AppConfig _config;
    private readonly bool _preview;
    private readonly DeckPanel _a, _b;
    private readonly ComboBox _outputs = new();
    private readonly Crossfader _cross;
    private readonly Fader _master;
    private readonly Label _message, _padStatus, _masterValue;
    private readonly CheckBox _padEnabled;
    private readonly StudioButton _panic, _padButton, _midiButton;
    private readonly LevelMeter _masterMeter = new() { Available = false, Accent = Color.FromArgb(125, 226, 190) };
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 33 };
    private readonly SemaphoreSlim _audioChanges = new(1, 1);
    private GlobalHotkeys? _hotkeys;
    private bool _closing;
    private readonly Dictionary<string, AxisDriver> _axisDrivers = new();
    private readonly System.Diagnostics.Stopwatch _padClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _padTimer = new() { Interval = 16 };
    private bool _padWasConnected;
    private long _rumbleUntil;
    private static string Arrow(double x, double y) => (x == 0 && y == 0) ? "·" : $"{Math.Sqrt(x * x + y * y):0.00}";
    private int _tickCount, _tickBusy, _lastDrops;

    public MainForm(bool preview = false)
    {
        SuspendLayout();
        if (ShotPath != null) PrepareShot();
        _takeoverA = new TakeoverController(_engine, _engine.DeckA);
        _takeoverB = new TakeoverController(_engine, _engine.DeckB);
        if(!preview) AppDomain.CurrentDomain.ProcessExit += (_, _) => TakeoverController.ReleaseAll();
        _preview = preview;
        _config = preview || ShotPath != null ? new AppConfig() : AppConfig.Load();
        if (preview) _config.GamepadEnabled = false;
        ProtectedApps.Set(_config.ProtectedApps);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "MixDock"; try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        ClientSize = new Size(1184, 920);
        MinimumSize = new Size(1120, 916);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font(10);
        DoubleBuffered = true;
        var header = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = BackColor, Padding = new Padding(24, 16, 24, 8) };
        var brand = MakeLabel("MIXDOCK", 24, 12, 24, Theme.Text, true);
        var subtitle = MakeLabel("YOUR APPS.  YOUR MIX.", 27, 53, 8, Theme.Muted);
        var gear = new StudioButton { Text = "⚙", AccessibleName = "Advanced settings", Size = new Size(40, 40), Font = Theme.Font(17), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.AddRange([brand, subtitle, gear, _autoBar]);
        header.Layout += (_, _) => { int dpi = DeviceDpi; _autoBar.SetBounds(dpi * 330 / 96, dpi * 8 / 96, Math.Max(100, header.Width - dpi * 430 / 96), dpi * 64 / 96); };
        header.Layout += (_, _) => gear.Location = new Point(header.Width - (int)(64 * DeviceDpi / 96f), (int)(24 * DeviceDpi / 96f));
        gear.Click += (_, _) => ShowAdvanced(gear);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 8, 24, 8), ColumnCount = 5, RowCount = 1, BackColor = BackColor };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _a = new DeckPanel("A", Theme.Cyan, _config.A) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _b = new DeckPanel("B", Theme.Amber, _config.B) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        body.Controls.Add(_a, 0, 0); body.Controls.Add(_b, 4, 0);
        _gridCenter = new PadGridView(compact: true);
        var center = new SurfacePanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        body.Controls.Add(center, 2, 0);
        var masterTitle = MakeLabel("MASTER", 0, 0, 11, Theme.Text, true);
        var masterHint = MakeLabel("OUTPUT LEVEL", 0, 0, 8, Theme.Muted);
        var meterHint = MakeLabel("Meter awaiting engine", 0, 0, 8, Theme.Muted);
        _master = new Fader { Vertical = true, Value = _config.Master, ResetValue = 88, Accent = Color.FromArgb(125, 226, 190), AccessibleName = "Master volume" };
        _masterValue = MakeLabel($"{_master.Value}%", 0, 0, 14, Theme.Text, true);
        _cross = new Crossfader { Value = _config.Crossfader };
        var blendTitle = MakeLabel("CROSSFADER", 0, 0, 8.5f, Theme.Muted, true);
        var aLabel = MakeLabel("A", 0, 0, 11, Theme.Cyan, true);
        var bLabel = MakeLabel("B", 0, 0, 11, Theme.Amber, true);
        _panic = new StudioButton { Text = "KILL AUDIO", Accent = Theme.Red, ForeColor = Theme.Red, AccessibleName = "Kill or restore all audio" };
        center.Controls.AddRange([masterTitle, masterHint, _hud, _gridCenter, _masterMeter, _master, _masterValue, meterHint, blendTitle, _cross, aLabel, bLabel, _panic]);
        center.Layout += (_, _) =>
        {
            float s = DeviceDpi / 96f; int D(float n) => (int)(n * s); float w = center.Width / s, h = center.Height / s;
            void Place(Control c, float x, float y, float cw, float ch) { c.AutoSize = false; c.SetBounds(D(x), D(y), D(cw), D(ch)); }
            Place(_hud, 16, 14, w - 32, 104); masterHint.Visible = false;
            Place(_gridCenter, 16, 124, w - 32, 168);
            Place(masterTitle, 24, 300, w / 2, 24); Place(_masterValue, w - 88, 296, 64, 30);
            Place(_masterMeter, w / 2 - 72, 330, 64, Math.Max(40, h - 534)); Place(_master, w / 2 + 12, 330, 64, Math.Max(40, h - 534));
            Place(meterHint, 24, h - 196, w - 48, 24);
            Place(blendTitle, 24, h - 152, w - 48, 20); Place(aLabel, 24, h - 128, 24, 24); Place(bLabel, w - 40, h - 128, 24, 24);
            Place(_cross, 16, h - 112, w - 32, 56); Place(_panic, 24, h - 56, w - 48, 40);
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = BackColor };
        _message = MakeLabel(preview ? "UI preview — audio capture is off." : "Choose an app on either deck to start mixing.", 24, 18, 9, Theme.Muted);
        _message.AutoSize = false; _message.AutoEllipsis = true; _message.Height = 32;
        _padEnabled = new CheckBox { Text = "Controller", Checked = _config.GamepadEnabled, AutoSize = false, Size = new Size(88, 28), ForeColor = Theme.Muted, AccessibleName = "Enable Xbox controller" };
        _padStatus = MakeLabel("●  Not connected", 0, 0, 8.5f, Theme.Muted);
        var maps = new StudioButton { Text = "Mappings", AccessibleName = "Keyboard and controller mappings" };
        maps.Click += (_, _) => OpenMappings();
        _padButton = new StudioButton { Text = "PAD", AccessibleName = "Show the controller mapping (Shift+F1)" };
        _padButton.Click += (_, _) => ToggleOverlay();
        _midiButton = new StudioButton { Text = "MIDI", AccessibleName = "MIDI and pad settings" };
        _midiButton.Click += (_, _) => OpenMidiSettings();
        footer.Controls.AddRange([_message, _padEnabled, _padStatus, maps, _padButton, _midiButton]);
        footer.Layout += (_, _) =>
        {
            int D(int n) => (int)(n * DeviceDpi / 96f);
            maps.SetBounds(footer.Width - D(136), D(16), D(112), D(32));
            _padButton.SetBounds(footer.Width - D(208), D(16), D(64), D(32));
            _midiButton.SetBounds(footer.Width - D(280), D(16), D(64), D(32));
            _padStatus.SetBounds(footer.Width - D(544), D(19), D(256), D(26)); _padStatus.AutoSize = false;
            _padEnabled.SetBounds(footer.Width - D(624), D(18), D(72), D(28));
            _message.SetBounds(D(24), D(17), footer.Width - D(664), D(32));
        };
        Controls.Add(body); Controls.Add(_monitor); Controls.Add(footer); Controls.Add(_waves); Controls.Add(header);
        InitPad();
        InitWaveforms();
        _a.ParametersChanged += (_, _) => _engine.A.Parameters = _a.GetParameters();
        _b.ParametersChanged += (_, _) => _engine.B.Parameters = _b.GetParameters();
        _engine.A.Parameters = _a.GetParameters(); _engine.B.Parameters = _b.GetParameters();
        _a.ArmRequested += async (_, _) => await SafeAsync(() => ArmAsync(_a, _engine.DeckA, _engine.DeckB, _takeoverA));
        _b.ArmRequested += async (_, _) => await SafeAsync(() => ArmAsync(_b, _engine.DeckB, _engine.DeckA, _takeoverB));
        _cross.ValueChanged += (_, _) => _engine.Mixer.Crossfader = _cross.Value / 100.0;
        _master.ValueChanged += (_, _) => { _engine.Mixer.Master = _master.Value / 100f; _masterValue.Text = $"{_master.Value}%"; };
        _engine.Mixer.Crossfader = _cross.Value / 100.0; _engine.Mixer.Master = _master.Value / 100f;
        _panic.Click += (_, _) => ToggleMasterMute(_panic);
        _mixHost = new EngineMixHost(_engine);
        _autoMix = new AutoMix(_mixHost) { FadeSeconds = _autoBar.FadeSeconds, EveryMinutes = _autoBar.EveryMinutes };
        _mixHost.CrossfaderMoved += x => _cross.Value = (int)Math.Round(x * 100);
        _autoBar.EnabledChanged += on => { if (_preview) return; _autoMix!.Enabled = on; if (!on) { _autoMix.Cancel(); _autoBar.SetStatus(""); } };
        _autoBar.FadeSecondsChanged += v => _autoMix!.FadeSeconds = v;
        _autoBar.EveryMinutesChanged += v => _autoMix!.EveryMinutes = v;
        _monitor.PlayPauseClicked += SongPlayPause;
        _monitor.SeekRequested += (d, f) => { var ds = d == 0 ? _engine.DeckA : _engine.DeckB; if (ds.IsFile) ds.SeekTo(TimeSpan.FromSeconds(ds.Duration.TotalSeconds * f)); };
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                bool left = PointToClient(new Point(e.X, e.Y)).X < ClientSize.Width / 2;
                await SafeAsync(() => left ? LoadSongAsync(_a, _engine.DeckA, _takeoverA, 0, files[0]) : LoadSongAsync(_b, _engine.DeckB, _takeoverB, 1, files[0]));
            }
        };
        _monitor.CueToggled += async (d, on) => await CueToggledAsync(d, on);
        _monitor.SyncClicked += d => { if (!_preview) SetMessage(DeckSync.Sync(_engine, d)); };
        _monitor.NudgeChanged += (d, p) => DeckSync.SetNudge(_engine, d, p);
        _monitor.CueVolumeChanged += v => _engine.Cue.Volume = v / 100f;
        _monitor.CueBlendChanged += v => _engine.Cue.Blend = v / 100f;
        Shown += async (_, _) =>
        {
            if(UiCheckDirectory!=null){await RunUiCheckAsync();return;}
            try
            {
                if (!_preview) { SessionTakeover.RecoverFromJournal(); AppRedirect.RecoverFromJournal(); }
                RefreshDevices();
                if (!_preview) { try { var cid = AudioEngine.ChooseCueDevice(AudioDiscovery.DefaultOutputId(), null); if (cid != null) { using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator(); _monitor.SetCueDevice(en.GetDevice(cid).FriendlyName); } } catch { } }
                if (!_preview) _hotkeys = new GlobalHotkeys(Handle, _config.Hotkeys);
                if (!_preview) WarnUnknownBindings();
            }
            catch (Exception ex) { SetMessage($"Startup: {ex.Message}", true); }
            _meterTimer.Tick += (_, _) => TickUi(); _meterTimer.Start(); _padTimer.Tick += (_, _) => PollPad(); _padTimer.Start();
            if (ShotPath != null) _ = RunShotAsync();
        };
        InstallDjShell();
        _engine.Profile=LatencyProfile.All.FirstOrDefault(p=>p.RequestedMs==_config.LatencyMs)??LatencyProfile.Standard;
        if(!_preview)_hotCues.Load();
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true);
        if(UiCheckDirectory!=null){StartPosition=FormStartPosition.Manual;Location=new Point(-20000,-20000);ShowInTaskbar=false;Opacity=0;}
    }

    /// <summary>UI-thread hook for Luna's actual post-mix peak. No estimated/fake signal.</summary>
    public void SetMasterPeak(float peak)
    {
        _masterMeter.SetPeak(peak);
        var hint = _masterMeter.Parent?.Controls.OfType<Label>().FirstOrDefault(l => l.Text == "Meter awaiting engine");
        if (hint != null) hint.Text = "Post-mix signal";
    }

    private void ShowAdvanced(Control anchor)
    {
        var menu = new ContextMenuStrip { BackColor = Theme.Raised, ForeColor = Theme.Text, ShowImageMargin = false };
        menu.Items.Add("Refresh app list", null, (_, _) => { try { RefreshDevices(); } catch (Exception ex) { SetMessage(ex.Message, true); } });
        menu.Items.Add("Double-click a knob to reset").Enabled = false;
        // disposing inside Closed crashed the app (ObjectDisposedException in the ToolStrip modal filter): dispose after the close finishes
        menu.Closed += (_, _) => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(menu.Dispose)); } catch { } };
        menu.Show(anchor, new Point(0, anchor.Height));
    }
    private async Task SetOutputAsync()
    {
        var endpoint = _outputs.SelectedItem as OutputEndpoint;
        if (endpoint is null) { SetMessage("No audio output available. Check your speakers in Settings.", true); return; }
        if (!OutputPolicy.IsAllowed(endpoint, _takeoverA.SinkId, _takeoverB.SinkId))
        {
            SetMessage("MixDock never plays to a virtual cable or the redirect sink. Pick your headphones or speakers.", true);
            RefreshDevices();
            return;
        }
        OutputPolicy.PreferredId = endpoint.Id;
        await _audioChanges.WaitAsync();
        try { if (!_closing) await _engine.SetOutputAsync(endpoint.Id); }
        finally { _audioChanges.Release(); }
        SetMessage("Ready. Choose an app on either deck to start mixing.");
    }
    private void RefreshDevices()
    {
        string? outputId = (_outputs.SelectedItem as OutputEndpoint)?.Id ?? _config.OutputId;
        string? sinkA = _takeoverA.SinkId, sinkB = _takeoverB.SinkId, defaultId = null;
        // virtual devices and the redirect sink are never offered, so they can never be selected or saved
        var endpoints = AudioDiscovery.Outputs().Where(d => OutputPolicy.IsAllowed(d, sinkA, sinkB)).ToList();
        _outputs.Items.Clear(); _outputs.Items.AddRange(endpoints.Cast<object>().ToArray());
        try { defaultId = AudioDiscovery.DefaultOutputId(); } catch { /* no default output */ }
        var pick = OutputPolicy.Pick(endpoints, outputId, defaultId, sinkA, sinkB);
        _outputs.SelectedItem = pick; OutputPolicy.PreferredId = pick?.Id;
        if (pick != null && pick.Id != outputId && !_preview && _engine.OutputId != null && _engine.OutputId != pick.Id)
            _ = SafeAsync(SetOutputAsync);
        var apps = AudioDiscovery.ActiveAudioProcesses();
        _a.SetApps(apps); _b.SetApps(apps);
        if (!_preview) SetMessage(apps.Count == 0 ? "Start audio in an app, then refresh the app list in Settings." : "Choose an app on either deck to start mixing.");
    }
    private int DropTotal() => _engine.ReaderA.Underruns + _engine.ReaderB.Underruns + _engine.ReaderA.Overruns + _engine.ReaderB.Overruns + _engine.DeckA.Discontinuities + _engine.DeckB.Discontinuities;
    private void LogStats()
    {
        int total = DropTotal();
        Log.Write($"audio stats: underruns A={_engine.ReaderA.Underruns} B={_engine.ReaderB.Underruns}; overruns A={_engine.ReaderA.Overruns} B={_engine.ReaderB.Overruns}; capture discontinuities A={_engine.DeckA.Discontinuities} B={_engine.DeckB.Discontinuities}; buffered A={_engine.ReaderA.BufferedMs} ms B={_engine.ReaderB.BufferedMs} ms");
        if (total > _lastDrops) SetMessage($"Audio hiccups so far: {total} (details in the log)", true);
        _lastDrops = total;
    }
    private static string Mmss(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    private async Task LoadSongAsync(DeckPanel panel, DeckSource deck, TakeoverController takeover, int di, string? path)
    {
        if (_preview) return;
        await _audioChanges.WaitAsync();
        try
        {
            if (path == null)
            {
                using var dlg = new OpenFileDialog { Title = "Load a song", Filter = "Music|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.aiff|All files|*.*" };
                if (dlg.ShowDialog(this) != DialogResult.OK) { panel.Sources.SelectedIndex = -1; return; }
                path = dlg.FileName;
            }
            panel.SetStatus("Loading…");
            takeover.Release();                         // give the app's own sound back
            DeckSync.Reset(_engine, di); _monitor.ResetNudge(di); _engine.Mixer.Key(di).Reset();
            await deck.LoadFileAsync(path);
            if (_engine.OutputId == null) await _engine.SetOutputAsync((_outputs.SelectedItem as OutputEndpoint)?.Id ?? OutputPolicy.Pick(AudioDiscovery.Outputs(), null, AudioDiscovery.DefaultOutputId(), _takeoverA.SinkId, _takeoverB.SinkId)?.Id ?? throw new InvalidOperationException("No real audio output available"));
            panel.SetStatus($"LIVE · {deck.FileName}", true);
            ClearLivePause(di); ApplyLookAhead(di);
            SetMessage($"Playing {deck.FileName} on Deck {(di == 0 ? "A" : "B")}.");
        }
        catch (Exception ex) { panel.SetStatus("Could not open that file"); SetMessage("Could not open that file: " + ex.Message, true); Log.Write("LoadSong: " + ex); }
        finally { _audioChanges.Release(); }
    }
    private void SongPlayPause(int d)
    {
        var ds = d == 0 ? _engine.DeckA : _engine.DeckB;
        if (!ds.IsFile) return;
        if (ds.Ended) { ds.SeekTo(TimeSpan.Zero); ds.Paused = false; } else ds.Paused = !ds.Paused;
    }
    private async Task CueToggledAsync(int deck, bool on)
    {
        if (_preview) return;
        if (!on) { if (deck == 0) _engine.Mixer.CueA = false; else _engine.Mixer.CueB = false; return; }
        await SafeAsync(async () =>
        {
            if (_engine.CueId == null)
            {
                var id = AudioEngine.ChooseCueDevice(_engine.OutputId, _takeoverA.SinkId ?? _takeoverB.SinkId);
                if (id == null) { SetMessage("No headphones found. Plug in or connect headphones, then press CUE again.", true); return; }
                await _engine.SetCueOutputAsync(id);
            }
            _monitor.SetCueDevice(_engine.CueName, true);
            if (deck == 0) _engine.Mixer.CueA = true; else _engine.Mixer.CueB = true;
            SetMessage($"Deck {(deck == 0 ? "A" : "B")} is in your headphones ({_engine.CueName}).");
        });
        _monitor.SetCueButtons(_engine.Mixer.CueA, _engine.Mixer.CueB);
    }
    private async Task RefreshAppsAsync()
    {
        if (_closing || _a.Sources.DroppedDown || _b.Sources.DroppedDown) return;
        try
        {
            var apps = await Task.Run(AudioDiscovery.ActiveAudioProcesses);
            if (_closing || _a.Sources.DroppedDown || _b.Sources.DroppedDown) return;
            _a.SetApps(apps); _b.SetApps(apps);
        }
        catch (Exception ex) { Log.Write("RefreshApps: " + ex.Message); }
    }
    private async Task ArmAsync(DeckPanel panel, DeckSource deck, DeckSource other, TakeoverController takeover)
    {
        var requested = panel.SelectedApp; if (requested is null) return;
        if (requested.Pid == -2) { await LoadSongAsync(panel, deck, takeover, ReferenceEquals(panel, _a) ? 0 : 1, null); return; }
        if (_preview) { panel.SetStatus("Selected · audio off in UI preview"); return; }
        await _audioChanges.WaitAsync();
        try
        {
            if (_closing || panel.SelectedPid != requested.Pid) return;
            var otherPanel = ReferenceEquals(panel, _a) ? _b : _a;
            if (other.ActivePid == requested.Pid || (other.ActivePid != null && otherPanel.SelectedApp is AudioApp selected &&
                AppPicker.Key(selected.Name) == AppPicker.Key(requested.Name)))
            {
                await deck.StopAsync();
                panel.SetStatus("Already assigned to the other deck");
                SetMessage("That app is on the other deck. Choose a different app.", true); return;
            }
            panel.SetStatus("Taking over…");
            { int di = ReferenceEquals(panel, _a) ? 0 : 1; DeckSync.Reset(_engine, di); _monitor.ResetNudge(di); _engine.Mixer.Key(di).Reset(); }
            string status = await takeover.EngageAsync(requested.Pid, AppPicker.DisplayName(requested.Name));
            { int di = ReferenceEquals(panel, _a) ? 0 : 1; ClearLivePause(di); ApplyLookAhead(di); }
            if (panel.SelectedPid == requested.Pid)
            {
                panel.SetStatus(status, true);
                SetMessage(takeover.RawSilenced
                    ? $"{AppPicker.DisplayName(requested.Name)} now plays through MixDock. Turn the knobs."
                    : status);
            }
        }
        catch { panel.SetStatus("Could not start this app"); throw; }
        finally { _audioChanges.Release(); }
    }
    private void TickUi()
    {
        RefreshDj();
        _engine.A.DecayPeak(); _engine.B.DecayPeak(); _engine.Mixer.DecayPeak();
        SetMasterPeak(_engine.Mixer.Peak);
        _engine.Cue.DecayPeak();
        if (!_preview && _tickCount > 0 && _tickCount % 300 == 0) LogStats();
        for (int d = 0; d < 2; d++)
        {
            CheckPauseSource(d);
            var ds = d == 0 ? _engine.DeckA : _engine.DeckB;
            double dur = ds.Duration.TotalSeconds;
            _monitor.SetFile(d, ds.IsFile, dur > 0 ? Math.Clamp(ds.Position.TotalSeconds / dur, 0, 1) : 0, ds.IsFile ? $"{Mmss(ds.Position)} / {Mmss(ds.Duration)}" : "", ds.Paused);
        }
        if (!_preview && _autoMix != null) { _autoMix.Tick(_meterTimer.Interval); _autoBar.SetStatus(_autoMix.Status); }
        for (int d = 0; d < 2; d++) _monitor.SetDeck(d, DeckSync.HeardBpm(_engine, d), DeckSync.HeardPhase(_engine, d));
        _a.SetPeak(_engine.A.Peak); _b.SetPeak(_engine.B.Peak);
        UpdatePadRows();
        UpdateGrid();
        if (!_preview && ++_tickCount % 15 == 0 && Interlocked.CompareExchange(ref _tickBusy, 1, 0) == 0) _ = Task.Run(() => { try { _takeoverA.Tick(); _takeoverB.Tick(); } finally { Volatile.Write(ref _tickBusy, 0); } });
        if (!_preview && _tickCount % 90 == 0) _ = RefreshAppsAsync();
        _panic.Active = _engine.Mixer.Mute;
        _panic.Text = _engine.Mixer.Mute ? "RESTORE AUDIO" : "KILL AUDIO";
        _panic.Invalidate();
    }
    private void SetControl(string action, int step)
    {
        switch (action)
        {
            case "A.Low": _a.Low.Value += step; break;
            case "A.Mid": _a.Mid.Value += step; break;
            case "A.High": _a.High.Value += step; break;
            case "A.Filter": SetFilterLatch(0, _filters[0].Latched + step); break;
            case "A.Trim": _a.Trim.Value = Math.Clamp(_a.Trim.Value + step, 0, 100); break;
            case "B.Low": _b.Low.Value += step; break;
            case "B.Mid": _b.Mid.Value += step; break;
            case "B.High": _b.High.Value += step; break;
            case "B.Filter": SetFilterLatch(1, _filters[1].Latched + step); break;
            case "B.Trim": _b.Trim.Value = Math.Clamp(_b.Trim.Value + step, 0, 100); break;
            case "Crossfader": _cross.Value += step; break;
            case "Master": _master.Value = Math.Clamp(_master.Value + step, 0, 100); break;
        }
    }
    private void ApplyAction(string action)
    {
        if (action.EndsWith('+') || action.EndsWith('-'))
        {
            int step = action.EndsWith('+') ? 3 : -3;
            SetControl(action[..^1], step);
            return;
        }
        switch (action)
        {
            case "A.FilterReset": SetFilterLatch(0, 50); break;
            case "B.FilterReset": SetFilterLatch(1, 50); break;
            case "A.EqReset": _a.ResetEq(); break;
            case "B.EqReset": _b.ResetEq(); break;
            case "FocusA": SetFocus(0); break;
            case "FocusB": SetFocus(1); break;
            case "FocusToggle": SetFocus(1 - _focus); break;
            case "CrossfaderA": _cross.Value = 0; AnnounceMove("CUT TO A"); break;
            case "CrossfaderB": _cross.Value = 100; AnnounceMove("CUT TO B"); break;
            case "AutoMix": if (!_preview) SetMessage(_autoBar.Toggle() ? "AUTO MIX on." : "AUTO MIX off."); break;
            case "Panic": Panic(); break;
            case "PadOverlay": ToggleOverlay(); break;
            case "A.Mute": _a.Muted = !_a.Muted; _engine.A.Parameters = _a.GetParameters(); break;
            case "B.Mute": _b.Muted = !_b.Muted; _engine.B.Parameters = _b.GetParameters(); break;
            case "MasterMute": _engine.Mixer.Mute = !_engine.Mixer.Mute; break;
            case "CrossfaderReset": _cross.Value = 50; break;
            case "FxCycle": CycleFx(); break;
            case "FxFreeze": ToggleFxFreeze(); break;
            case "QuantCycle": CycleQuantize(); break;
            case "BankMode": CycleBankMode(); break;
            case "PadGrid": ToggleGridFloat(); break;
            case "PauseAll": PauseAll(); break;
            case "BankNext": SetBank((_bank + 1) % 4); break;
            case "BankPrev": SetBank((_bank + 3) % 4); break;
            default:
                if (action.StartsWith("Bank:") && int.TryParse(action[5..], out int bn)) SetBank(bn - 1);
                break;
        }
    }
    private void ToggleMasterMute(Button b)
    {
        ApplyAction("MasterMute");
        b.Text = _engine.Mixer.Mute ? "RESTORE AUDIO" : "KILL AUDIO";
    }
    /// <summary>In-app controller mapping editor; changes apply immediately (no restart, no notepad).</summary>
    private void OpenMappings()
    {
        if (_bindingsOpen) return;
        ReleaseHeldGestures();
        _bindingsOpen = true;
        try
        {
            using var dlg = new BindingsForm(_config);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                ApplyBindings();
                SetMessage("Controller mappings saved and active.");
            }
        }
        catch (Exception ex) { Log.Write("Mappings: " + ex); SetMessage("Could not open the mapping editor: " + ex.Message, true); }
        finally { _bindingsOpen = false; }
    }
    private void ApplyBindings()
    {
        ReleaseHeldGestures();
        _padMapper = _config.CreatePadMapper();
        _padMapper.Reset(Gamepad.Read(out var st) ? st.Gamepad.Buttons : 0);
        _axisDrivers.Clear(); _axisOwner.Clear();
        if (!_preview && ShotPath == null) { try { _config.Save(); } catch (Exception ex) { Log.Write("save bindings: " + ex.Message); } }
        if (_overlay.Visible) _overlay.Invalidate();
        WarnUnknownBindings();
    }
    /// <summary>A binding the app cannot run must never be a silent dead button.</summary>
    private void WarnUnknownBindings()
    {
        var bad = ActionCatalog.Unknown(_config.GamepadButtons, _config.ShiftButtons, _config.GamepadAxes, _config.ShiftAxes);
        if (bad.Count == 0) return;
        Log.Write("unknown controller bindings: " + string.Join("; ", bad));
        SetMessage($"{bad.Count} controller binding{(bad.Count > 1 ? "s do" : " does")} nothing ({bad[0]}). Open MAP PAD to fix.", true);
    }
    private void SaveSettings()
    {
        if (_preview || ShotPath != null) return;
        _config.A = _a.GetState(); _config.B = _b.GetState();
        _config.A.FilterLatch = _filters[0].Latched; _config.B.FilterLatch = _filters[1].Latched;
        _config.OutputId = (_outputs.SelectedItem as OutputEndpoint)?.Id;
        _config.Crossfader = _cross.Value; _config.Master = _master.Value;
        _config.GamepadEnabled = _padEnabled.Checked;
        SaveSlices();
        _config.Save();
    }
    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY && _hotkeys != null &&
            _hotkeys.TryResolve(m.WParam.ToInt32(), out string action))
        { Dispatch(action, true); if (action.Contains("Pad:")) Dispatch(action, false); return; }   // a hotkey is a tap: pads get their release at once
        base.WndProc(ref m);
    }
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closing) { base.OnFormClosing(e); return; }
        _padTimer.Stop(); if(!_preview) Gamepad.Rumble(0, 0);
        e.Cancel = true;
        _closing = true;
        _meterTimer.Stop();
        try { SaveSettings(); } catch { /* don't prevent exit */ }
        _hotkeys?.Dispose();
        StopMidi();
        await _audioChanges.WaitAsync();
        try { _takeoverA.Release(); _takeoverB.Release(); await _engine.DisposeAsync(); } catch { /* app is closing */ }
        finally { _audioChanges.Release(); }
        _meterTimer.Dispose();
        BeginInvoke(new Action(Close));
    }
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { SetMessage($"Audio error: {ex.Message}", true); }
    }
    private void SetMessage(string text, bool error = false)
    {
        _message.Text = text;
        _message.ForeColor = error ? Color.FromArgb(246, 125, 133) : Color.FromArgb(131, 181, 185);
    }
    private static Label MakeLabel(string text, int x, int y, float font, Color color, bool bold = false)
        => new() { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = color,
            Font = Theme.Font(font, bold) };
}

