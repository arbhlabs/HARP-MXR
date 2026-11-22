using System.Drawing;
using System.Windows.Forms;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;
using MixDock.Ui;

// Usage: MixDock.ShotTests <output folder>. Draws the real 1.3 controls with sample data and saves PNGs. Never starts HARP-MXR.exe or audio.
var outDir = args.Length > 0 ? args[0] : "out/v130shots";
Directory.CreateDirectory(outDir);
ApplicationConfiguration.Initialize();
long now = Environment.TickCount64;
var key = new MusicalKey(9, true);

PadGridState Scene(int bank, int variant)
{
    var s = new PadGridState { Bank = bank, Focus = 0, Connected = true, Layer = variant == 1, QuantText = "QUANT 1/16", MidiText = "MIDI: MixDock (teVirtualMIDI)",
        KeyText = $"KEY {ChordTheory.KeyName(key)} (auto)" };
    var cfg = new AppConfig();
    for (int b = 0; b < 4; b++) s.Modes[b] = cfg.Banks[b].Mode;
    for (int k = 0; k < 16; k++)
    {
        int b = k / 4, p = k % 4; var mode = s.Modes[b];
        s.Filled[k] = mode is BankMode.Chord or BankMode.Drum || (k % 3 != 1);
        s.Labels[k] = mode switch
        {
            BankMode.Chord => ChordTheory.ChordName(key, cfg.Banks[b].Degrees[p]),
            BankMode.Drum => DrumKit.Name(DrumKit.ForPad(p, false)),
            BankMode.Sample => s.Filled[k] ? "SMP" : "", _ => s.Filled[k] ? "ONE" : ""
        };
        s.Velocity[k] = 90; s.HitAt[k] = 0;
    }
    if (variant == 0)
    {   // a hit just landed on bank 3 pad B (hard), pad A is held, bank 1 pad X waits for the grid, DAW lights one pad
        s.Held[8] = true; s.Playing[8] = true; s.HitAt[9] = now - 60; s.Velocity[9] = 118; s.Playing[9] = true;
        s.Pending[2] = true; s.HitAt[2] = now - 10; s.Velocity[2] = 70;
        s.DawLit[13] = true; s.ChordText = "Am / 1st inv / open";
    }
    else
    {   // layer mode: three pads latched across banks, quantize 1/8
        s.QuantText = "QUANT 1/8"; s.Latched[0] = s.Playing[0] = true; s.Latched[5] = s.Playing[5] = true; s.Latched[10] = s.Playing[10] = true;
        s.Held[11] = true; s.Playing[11] = true; s.Pending[11] = true; s.ChordText = "F / root / close";
    }
    return s;
}

void Snap(Control c, string name, Size size)
{
    using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, BackColor = Theme.Background, ClientSize = size };
    f.Controls.Add(c); c.Dock = DockStyle.Fill; c.Visible = true; f.Show(); Application.DoEvents(); c.Invalidate(); Application.DoEvents();
    using var bmp = new Bitmap(size.Width, size.Height);
    c.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
    bmp.Save(Path.Combine(outDir, name), System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine("saved " + name);
    f.Controls.Remove(c); f.Close();
}
float k = 1.0f;
using (var g = new Form()) { k = g.DeviceDpi / 96f; }
Size Sz(int w, int h) => new((int)(w * k), (int)(h * k));

{ var v = new PadGridView(compact: false); v.SetState(Scene(2, 0)); Snap(v, "padgrid-floating-chord-bank.png", Sz(560, 392)); }
{ var v = new PadGridView(compact: false); v.SetState(Scene(0, 1)); Snap(v, "padgrid-floating-layer-latched.png", Sz(560, 392)); }
{   // the centre panel look: grid inside the same SurfacePanel the app uses (about 300 px wide)
    var panel = new SurfacePanel { Padding = new Padding((int)(16 * k)) };
    var v = new PadGridView(compact: true) { Dock = DockStyle.Top, Height = (int)(178 * k) }; v.SetState(Scene(2, 0)); panel.Controls.Add(v);
    Snap(panel, "padgrid-centre-panel.png", Sz(300, 210));
}
{   // drum bank selected, no MIDI port (status text as on this PC)
    var s = Scene(3, 0); s.MidiText = "MIDI: no virtual port"; s.ChordText = ""; s.Held[12] = s.Playing[12] = true; s.HitAt[12] = now - 30; s.Velocity[12] = 100; s.Held[8] = false; s.Playing[8] = false; s.HitAt[9] = 0; s.Playing[9] = false;
    var v = new PadGridView(compact: false); v.SetState(s); Snap(v, "padgrid-floating-drum-bank-no-midi.png", Sz(560, 392));
}
{
    var cfg = new AppConfig();
    var o = new PadOverlay(cfg); o.SetState(new PadFrame(0, 0, 0.6, 0.3, 0.2, 0.8, PadButton.A | PadButton.LeftShoulder), false, true, 0); Snap(o, "pad-cheatsheet-normal.png", Sz(820, 440));
    var o2 = new PadOverlay(cfg); o2.SetState(new PadFrame(0, 0, 0, 0, 0, 0, PadButton.Back), true, true, 0); Snap(o2, "pad-cheatsheet-shift.png", Sz(820, 440));
}
{
    var cfg = new AppConfig();
    using var dlg = new MidiSettingsForm(cfg, "MIDI: no virtual port");
    dlg.StartPosition = FormStartPosition.Manual; dlg.Location = new Point(-20000, -20000); dlg.ShowInTaskbar = false; dlg.Show(); Application.DoEvents();
    using var bmp = new Bitmap(dlg.Width, dlg.Height); dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    bmp.Save(Path.Combine(outDir, "midi-settings.png"), System.Drawing.Imaging.ImageFormat.Png); Console.WriteLine("saved midi-settings.png"); dlg.Close();
}

// ---- R5 controller HUD: three states at the sizes the console gives it (1280x720 .. 3840x2160 windows, 100-200 % DPI)
int hudFailures = 0;
void HudCheck(bool ok, string t) { Console.WriteLine((ok ? "PASS " : "FAIL ") + t); if (!ok) hudFailures++; }
{
    var space = FxCatalog.Sets[0];
    string[] dirs = { FxCatalog.Name(space.Up), FxCatalog.Name(space.Down), FxCatalog.Name(space.Left), FxCatalog.Name(space.Right) };
    var states = new (string Name, HudView View)[]
    {
        ("stick", new HudView(true, $"{dirs[0]} + {dirs[3]}", "72%", .72, "RIGHT STICK · CONTROLLER 1", "R", "DECK A", $"SET 1/{FxCatalog.Sets.Count} · {space.Name}",
            dirs, new[] { .55, 0, 0, .45 }, .55, .62, 0)),
        ("button", new HudView(false, "STUTTER 1/8", "HOLD", double.NaN, "FX PAD", "A", "BOTH DECKS", "FX PAD · SETS SWEEP / MOTION · BOTH DECKS", Accent: 2)),
        ("set", new HudView(false, $"SET {FxCatalog.Sets[5].Name}", $"6/{FxCatalog.Sets.Count}", double.NaN, "CONTROLLER 1", "▶", "DECK B", $"SET 6/{FxCatalog.Sets.Count} · {FxCatalog.Sets[5].Name}", Accent: 1,
            Detail: $"↑ {FxCatalog.Name(FxCatalog.Sets[5].Up)}  ↓ {FxCatalog.Name(FxCatalog.Sets[5].Down)}  ← {FxCatalog.Name(FxCatalog.Sets[5].Left)}  → {FxCatalog.Name(FxCatalog.Sets[5].Right)}")),
    };
    // console scale = min(W / 1366, H / 768) for 1280x720, 1366x768, 1920x1080, 2560x1440, 3840x2160; plus 125/150/200 % on a 1366 console
    var scales = new (string Tag, float S)[] { ("720p", 1280 / 1366f), ("768p", 1f), ("125pct", 1.25f), ("150pct", 1.5f), ("1080p", 1920 / 1366f), ("200pct", 2f), ("1440p", 2560 / 1366f), ("4k", 3840 / 1366f) };
    foreach (var (name, view) in states)
        foreach (var (tag, s) in scales)
        {
            var hud = new FxOverlay { AnimationsEnabled = () => true };
            var size = new Size((int)Math.Round(FxOverlay.DesignWidth * s), (int)Math.Round(FxOverlay.DesignHeight * s));
            using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, BackColor = Theme.Background, ClientSize = size };
            f.Controls.Add(hud); hud.Bounds = new Rectangle(Point.Empty, size); f.Show(); hud.Show(view); Application.DoEvents();
            var over = hud.Overflows();
            HudCheck(over.Count == 0, $"HUD {name} at {tag} ({size.Width}x{size.Height}): every text fits{(over.Count > 0 ? ": " + string.Join("; ", over) : "")}");
            HudCheck(hud.Visible && hud.Width == size.Width, $"HUD {name} at {tag}: visible, no clipping of the box");
            if (tag is "720p" or "1080p" or "4k")
            {
                using var bmp = new Bitmap(size.Width, size.Height); hud.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
                bmp.Save(Path.Combine(outDir, $"hud-{name}-{tag}.png"), System.Drawing.Imaging.ImageFormat.Png); Console.WriteLine($"saved hud-{name}-{tag}.png");
            }
            f.Close();
        }
    // fade: 1.5 s hold, then a short fade; with Windows animations off it just disappears, no fade
    long t = 0;
    var anim = new FxOverlay { Clock = () => t, AnimationsEnabled = () => true };
    using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
    {
        f.Controls.Add(anim); f.Show(); anim.Show(states[1].View);
        t = 1400; anim.Tick(); bool held = anim.Visible && anim.Opacity() == 1;
        t = 1700; anim.Tick(); bool fading = anim.Visible && anim.Opacity() is > 0 and < 1;
        t = 1900; anim.Tick();
        HudCheck(held && fading && !anim.Visible, "HUD holds 1.5 s, fades, then hides");
        var still = new FxOverlay { Clock = () => t, AnimationsEnabled = () => false };
        f.Controls.Add(still); t = 0; still.Show(states[0].View);
        t = 1600; still.Tick(); bool noFade = still.Visible ? still.Opacity() == 1 : true;
        t = 1501; still.Tick();
        HudCheck(noFade && !still.Visible, "reduced motion: no fade animation, the HUD simply disappears after 1.5 s");
        anim.Show(states[0].View); t = 3000; anim.Show(states[0].View);
        HudCheck(anim.Visible, "every controller action shows the HUD again");
        f.Close();
    }
}
return hudFailures == 0 ? 0 : 1;
