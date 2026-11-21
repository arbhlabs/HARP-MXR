using System.Drawing;
using System.Windows.Forms;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

/// <summary>`MixDock.exe --shot out.png [--app vivaldi]`: runs the real window off-screen, selects an app, saves a screenshot, exits.</summary>
public sealed partial class MainForm
{
    public static string? ShotPath;
    public static string ShotApp = "vivaldi";
    public static bool ShotAutoMix;
    public static int ShotSoak;
    public static string? ShotSong;

    // shot mode never takes focus from whatever the user is doing
    protected override bool ShowWithoutActivation => ShotPath != null || UiCheckDirectory != null;

    private void PrepareShot()
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        ShowInTaskbar = false;
    }

    private async Task RunShotAsync()
    {
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("MIXDOCK_SHOT_H"), out int sh) && sh > 400) { MaximumSize = new Size(6000, 6000); MinimumSize = Size.Empty; ClientSize = new Size((int)(1184 * DeviceDpi / 96f), (int)(sh * DeviceDpi / 96f)); PerformLayout(); }
            await Task.Delay(1500);
            var apps = await Task.Run(AudioDiscovery.ActiveAudioProcesses);
            _a.SetApps(apps);
            var pick = _a.Sources.Items.Cast<AudioApp>().FirstOrDefault(x => AppPicker.Key(x.Name) == ShotApp);
            Log.Write($"shot: apps=[{string.Join(", ", apps.Select(x => x.Name))}] pick={pick?.Name}");
            if (pick != null)
            {
                _a.Sources.SelectedItem = pick;
                await SafeAsync(() => ArmAsync(_a, _engine.DeckA, _engine.DeckB, _takeoverA));
                await Task.Delay(12000);   // let the beat detector listen
                Log.Write($"shot: BPM A={_engine.DeckA.Bpm.Bpm:F1} conf={_engine.DeckA.Bpm.Confidence:F2} phase={_engine.DeckA.Bpm.Phase:F2}");
                await CueToggledAsync(0, true);
                _monitor.SetCueButtons(_engine.Mixer.CueA, _engine.Mixer.CueB);
                await Task.Delay(2500);
                Log.Write($"shot: cue device={_engine.CueName} cuePeak={_engine.Cue.Peak:F3} masterTapActive={_engine.Mixer.TapMaster}");
            }
            if (ShotSoak > 0 && _autoMix != null)
            {
                var spot = apps.FirstOrDefault(x => AppPicker.Key(x.Name) == "spotify");
                if (spot != null)
                {
                    _b.SetApps(apps); _b.Sources.SelectedItem = _b.Sources.Items.Cast<AudioApp>().FirstOrDefault(x => x.Pid == spot.Pid);
                    await SafeAsync(() => ArmAsync(_b, _engine.DeckB, _engine.DeckA, _takeoverB));
                }
                _autoMix.Enabled = true; _autoMix.FadeSeconds = 6; _autoMix.EveryMinutes = 0.6;
                int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
                Log.Write($"soak: start {ShotSoak}s, spotify={(spot == null ? "none" : spot.Name + " playing=" + spot.Playing)}, A={_engine.DeckA.ActivePid} B={_engine.DeckB.ActivePid}");
                for (int sec = 0; sec < ShotSoak; sec += 5)
                {
                    await Task.Delay(5000);
                    if (sec == 20 || sec == 100 || sec == 200) { Log.Write("soak: SYNC B->A: " + DeckSync.Sync(_engine, 1)); Log.Write("soak: SYNC A->B: " + DeckSync.Sync(_engine, 0)); }
                    if (sec % 30 == 0) Log.Write($"soak t={sec}s underruns A={_engine.ReaderA.Underruns} B={_engine.ReaderB.Underruns} overruns {_engine.ReaderA.Overruns}/{_engine.ReaderB.Overruns} discont {_engine.DeckA.Discontinuities}/{_engine.DeckB.Discontinuities} bufA={_engine.ReaderA.BufferedMs} bufB={_engine.ReaderB.BufferedMs} rateA={_engine.ReaderA.Rate:F3} rateB={_engine.ReaderB.Rate:F3} cross={_engine.Mixer.Crossfader:F2} autoMix='{_autoMix.Status}' peakA={_engine.A.Peak:F2} peakB={_engine.B.Peak:F2}");
                }
                Log.Write($"soak: FINAL underruns A={_engine.ReaderA.Underruns} B={_engine.ReaderB.Underruns} overruns A={_engine.ReaderA.Overruns} B={_engine.ReaderB.Overruns} discontinuities A={_engine.DeckA.Discontinuities} B={_engine.DeckB.Discontinuities} silentPackets A={_engine.DeckA.SilentPackets} B={_engine.DeckB.SilentPackets}; GC gen0 +{GC.CollectionCount(0) - gc0}, gen2 +{GC.CollectionCount(2) - gc2}");
            }
            if (ShotAutoMix && _autoMix != null)
            {
                var spot = apps.FirstOrDefault(x => AppPicker.Key(x.Name) == "spotify");
                Log.Write($"shot: spotify entry={(spot == null ? "none" : spot.Name + " playing=" + spot.Playing + " peak=" + spot.Peak.ToString("F3"))}");
                if (spot != null)
                {
                    _b.SetApps(apps); _b.Sources.SelectedItem = _b.Sources.Items.Cast<AudioApp>().FirstOrDefault(x => x.Pid == spot.Pid);
                    await SafeAsync(() => ArmAsync(_b, _engine.DeckB, _engine.DeckA, _takeoverB));
                    await Task.Delay(9000);
                    Log.Write($"shot: BPM A={_engine.DeckA.Bpm.Bpm:F1} B={_engine.DeckB.Bpm.Bpm:F1}");
                }
                _autoMix.Enabled = true; _autoMix.FadeSeconds = 6;
                _autoMix.ForceFade();
                bool shotTaken = false, started = false;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(500);
                    Log.Write($"shot: automix t={i * 0.5:F1}s fading={_autoMix.Fading} p={_autoMix.FadeProgress:F2} cross={_engine.Mixer.Crossfader:F2} peakA={_engine.A.Peak:F3} peakB={_engine.B.Peak:F3} master={_engine.Mixer.Peak:F3} lowA={_engine.A.AutoLowDb:F0} lowB={_engine.B.AutoLowDb:F0} :: {_autoMix.Status}");
                    if (_autoMix.Fading && _autoMix.FadeProgress > 0.45 && !shotTaken)
                    {
                        shotTaken = true; using var mid = new Bitmap(Width, Height); DrawToBitmap(mid, new Rectangle(0, 0, Width, Height));
                        mid.Save(Path.ChangeExtension(ShotPath!, ".automix.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    if (_autoMix.Fading) started = true;
                    if (started && !_autoMix.Fading) break;
                }
            }
            if (ShotSong != null)
            {
                _engine.Mixer.Master = 0;   // screenshot only: the song plays silently
                await LoadSongAsync(_a, _engine.DeckA, _takeoverA, 0, ShotSong);
                await LoadSongAsync(_b, _engine.DeckB, _takeoverB, 1, ShotSong);
                _engine.DeckB.SeekTo(TimeSpan.FromSeconds(Math.Min(16, _engine.DeckB.Duration.TotalSeconds / 2)));
                await Task.Delay(4000);
                // the engine has no looping yet: a display-only 4-bar loop on deck B proves the loop overlay renders
                if (_tracks[1] is { Grid.Valid: true } tr)
                {
                    double p = tr.Grid.PeriodSec, start = tr.Grid.FirstBeatSec + Math.Floor((_engine.DeckB.Position.TotalSeconds - 2 - tr.Grid.FirstBeatSec) / p) * p;
                    _loop[1] = (start, start + 16 * p);
                }
                Log.Write($"shot: waveforms A={_tracks[0]?.Columns.Length} cols grid {_tracks[0]?.Grid.Bpm:F2} B={_tracks[1]?.Columns.Length} cols, cue A={_cueSec[0]:F3}s loop B={_loop[1]}");
                await Task.Delay(600);
                // leak soak: keep both decks playing (silently, off-screen) while the UI repaints at full rate
                if (int.TryParse(Environment.GetEnvironmentVariable("MIXDOCK_SHOT_HOLD"), out int hold) && hold > 0)
                {
                    var rnd = new Random(1);
                    for (int s = 0; s < hold; s++)
                    {
                        // move every knob / the crossfader like a controller would, so the paint paths that crashed get exercised
                        foreach (var p in new[] { _a, _b }) { p.Filter.SetPrecise(rnd.Next(0, 101)); p.Low.Value = rnd.Next(0, 101); p.High.Value = rnd.Next(0, 101); }
                        _cross.Value = rnd.Next(0, 101); SetFocus(s % 2, true);
                        AnnounceMove($"SOAK {s}");
                        await Task.Delay(1000);
                    }
                }
            }
            Log.Write($"shot: A peak={_engine.A.Peak:F3} status after engage; saving {ShotPath}");
            if (Environment.GetEnvironmentVariable("MIXDOCK_SHOT_DECK") == "1")
            {
                // layout check at the design height (568 logical px of body), independent of how tall the screen is
                float k = DeviceDpi / 96f;
                _a.Parent?.Controls.Remove(_a); _a.Dock = DockStyle.None; _a.Size = new Size((int)(410 * k), (int)(568 * k)); _a.PerformLayout();
                _a.Pads.Set(new[] { 22050, 0, 22050, 0 }, new[] { .6f, 0f, 0f, 0f }, 0);
                using var db = new Bitmap(_a.Width, _a.Height); _a.DrawToBitmap(db, new Rectangle(0, 0, _a.Width, _a.Height));
                db.Save(Path.ChangeExtension(ShotPath!, ".deck.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            if (Environment.GetEnvironmentVariable("MIXDOCK_SHOT_OVERLAY") == "1")
            {
                ToggleOverlay(); _overlay.SetState(new PadFrame(0, 0, .6, .5, .5, .8, 0), false, true, 0);
                using var ob = new Bitmap(_overlay.Width, _overlay.Height); _overlay.DrawToBitmap(ob, new Rectangle(0, 0, _overlay.Width, _overlay.Height));
                ob.Save(Path.ChangeExtension(ShotPath!, ".overlay.png"), System.Drawing.Imaging.ImageFormat.Png);
                _overlay.SetState(new PadFrame(0, 0, 0, 0, 0, 0, 0), true, true, 1);
                using var ob2 = new Bitmap(_overlay.Width, _overlay.Height); _overlay.DrawToBitmap(ob2, new Rectangle(0, 0, _overlay.Width, _overlay.Height));
                ob2.Save(Path.ChangeExtension(ShotPath!, ".overlay-shift.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ShotPath!))!);
            bmp.Save(ShotPath!, System.Drawing.Imaging.ImageFormat.Png);
            Log.Write("shot saved");
        }
        catch (Exception ex) { Log.Write("shot failed: " + ex); }
        finally { Close(); }
    }
}
