using System.Drawing.Drawing2D;

namespace MixDock.Ui;

/// <summary>Compact bottom strip: per-deck CUE / tempo / SYNC, and headphone cue volume + blend.</summary>
public sealed class MonitorBar : Panel
{
    private sealed class DeckStrip : SurfacePanel
    {
        public readonly StudioButton Cue = new() { Text = "CUE", AccessibleName = "Send this deck to the headphones" };
        public readonly StudioButton Sync = new() { Text = "SYNC", AccessibleName = "Match tempo and beat to the other deck" };
        public readonly StudioButton Down = new() { Text = "−", AccessibleName = "Slower" };
        public readonly StudioButton Up = new() { Text = "+", AccessibleName = "Faster" };
        public readonly StudioButton Play = new() { Text = "II", AccessibleName = "Play or pause the song", Visible = false };
        public double HeardBpm, Phase = double.NaN, Nudge, Frac; public bool Beating, IsFile; public string TimeText = "";
        public event Action<double>? SeekRequested;
        private readonly Color _accent;
        public DeckStrip(Color accent)
        {
            _accent = accent; Cue.Accent = Sync.Accent = accent;
            Controls.AddRange([Cue, Sync, Down, Up, Play]);
            DoubleBuffered = true;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            float s = DeviceDpi / 96f; int D(float n) => (int)(n * s);
            int y = D(8), h = D(34);
            Cue.SetBounds(D(10), y, D(56), h);
            Up.SetBounds(Width - D(10) - D(32), y, D(32), h);
            Down.SetBounds(Up.Left - D(4) - D(32), y, D(32), h);
            Sync.SetBounds(Down.Left - D(6) - D(62), y, D(62), h);
            Play.SetBounds(Sync.Left - D(6) - D(40), y, D(40), h);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!IsFile || e.Y < Height - (int)(22 * DeviceDpi / 96f)) return;
            int bx = (int)(12 * DeviceDpi / 96f), bw = Width - 2 * bx;
            SeekRequested?.Invoke(Math.Clamp((e.X - bx) / (double)bw, 0, 1));
        }
        protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "MonitorBar.1", this);
        private void PaintCore(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f; int D(float n) => (int)(n * s);
            int left = Cue.Right + D(10);
            string bpm = HeardBpm > 0 ? $"{HeardBpm:F1}" : "--";
            Theme.TextAt(g, bpm, new Rectangle(left, D(4), D(84), D(26)), 15, Theme.Text, true);
            Theme.TextAt(g, IsFile ? TimeText : (Nudge == 0 ? "BPM" : $"BPM {Nudge:+0.0;-0.0}%"), new Rectangle(left, D(28), D(84), D(14)), 7, Theme.Muted);
            int cx = left + D(86), cy = D(24);
            using (var b = new SolidBrush(Beating ? _accent : Color.FromArgb(48, 56, 72))) Theme.Ellipse(g, b, new RectangleF(cx, cy - D(7), D(14), D(14)));
            // beat phase bar along the bottom: the marker travels from one beat to the next
            int bx = D(12), bw = Width - D(24), by = Height - D(11);
            using (var track = new Pen(Color.FromArgb(48, 56, 72), D(4))) { track.StartCap = track.EndCap = LineCap.Round; g.DrawLine(track, bx, by, bx + bw, by); }
            if (IsFile)
            {
                using var pr = new Pen(_accent, D(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(pr, bx, by, bx + (float)(bw * Frac), by);
                using var mk = new SolidBrush(Color.White); Theme.Ellipse(g, mk, new RectangleF(bx + (float)(bw * Frac) - D(5), by - D(5), D(10), D(10)));
            }
            else if (!double.IsNaN(Phase))
            {
                float px = bx + (float)(bw * Phase);
                using var m = new SolidBrush(_accent); Theme.Ellipse(g, m, new RectangleF(px - D(5), by - D(5), D(10), D(10)));
            }
        }
    }

    private readonly DeckStrip _a = new(Theme.Cyan), _b = new(Theme.Amber);
    private readonly Label _device = new() { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Font(8), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
    private readonly Fader _vol = new() { Value = 60, ResetValue = 60, Accent = Theme.Text, AccessibleName = "Headphone volume" };
    private readonly Fader _blend = new() { Value = 0, ResetValue = 0, Accent = Theme.Text, AccessibleName = "Headphone mix: cue to master" };
    private readonly SurfacePanel _center = new();

    public event Action<int, bool>? CueToggled;
    public event Action<int>? SyncClicked, PlayPauseClicked;
    public event Action<int, double>? SeekRequested;
    public event Action<int, double>? NudgeChanged;
    public event Action<int>? CueVolumeChanged, CueBlendChanged;

    public MonitorBar()
    {
        Dock = DockStyle.Bottom; Height = 72; BackColor = Theme.Background; Padding = new Padding(24, 0, 24, 6);
        Controls.AddRange([_a, _center, _b]);
        _center.Controls.AddRange([_device, _vol, _blend]);
        foreach (var (strip, idx) in new[] { (_a, 0), (_b, 1) })
        {
            int d = idx;
            strip.Cue.Click += (_, _) => { strip.Cue.Active = !strip.Cue.Active; strip.Cue.Invalidate(); CueToggled?.Invoke(d, strip.Cue.Active); };
            strip.Sync.Click += (_, _) => SyncClicked?.Invoke(d);
            strip.Play.Click += (_, _) => PlayPauseClicked?.Invoke(d);
            strip.SeekRequested += f => SeekRequested?.Invoke(d, f);
            strip.Down.Click += (_, _) => { strip.Nudge = Math.Max(-8, Math.Round(strip.Nudge - 0.5, 1)); NudgeChanged?.Invoke(d, strip.Nudge); strip.Invalidate(); };
            strip.Up.Click += (_, _) => { strip.Nudge = Math.Min(8, Math.Round(strip.Nudge + 0.5, 1)); NudgeChanged?.Invoke(d, strip.Nudge); strip.Invalidate(); };
        }
        _vol.ValueChanged += (_, _) => CueVolumeChanged?.Invoke(_vol.Value);
        _blend.ValueChanged += (_, _) => CueBlendChanged?.Invoke(_blend.Value);
        SetCueDevice(null);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float s = DeviceDpi / 96f; int D(float n) => (int)(n * s);
        int w = Width - Padding.Horizontal, x0 = Padding.Left, gap = D(16);
        int side = (int)((w - 2 * gap) * 0.36), mid = w - 2 * gap - 2 * side, h = Height - Padding.Bottom - D(2);
        _a.SetBounds(x0, D(2), side, h); _center.SetBounds(x0 + side + gap, D(2), mid, h); _b.SetBounds(x0 + side + gap + mid + gap, D(2), side, h);
        _device.SetBounds(D(6), D(2), _center.Width - D(12), D(18));
        _vol.SetBounds(D(4), D(18), _center.Width / 2 - D(4), h - D(20)); _blend.SetBounds(_center.Width / 2, D(18), _center.Width / 2 - D(4), h - D(20));
    }

    public void SetCueDevice(string? name, bool active = false)
    {
        _device.Text = name == null ? "No headphones found — connect some, then press CUE"
            : (active ? $"Headphones: {name}" : $"Headphones ready: {name}");
        _device.ForeColor = name == null ? Theme.Muted : Theme.Text;
    }

    public void SetCueButtons(bool a, bool b) { _a.Cue.Active = a; _b.Cue.Active = b; _a.Cue.Invalidate(); _b.Cue.Invalidate(); }

    public void SetDeck(int deck, double heardBpm, double phase)
    {
        var s = deck == 0 ? _a : _b;
        bool beating = !double.IsNaN(phase) && (phase < 0.12 || phase > 0.96);
        if (Math.Abs(s.HeardBpm - heardBpm) > 0.05 || beating != s.Beating || double.IsNaN(phase) != double.IsNaN(s.Phase) || (!double.IsNaN(phase) && Math.Abs(phase - s.Phase) > 0.01))
        { s.HeardBpm = heardBpm; s.Phase = phase; s.Beating = beating; s.Invalidate(); }
    }
    public void SetFile(int deck, bool isFile, double frac, string time, bool paused)
    {
        var s = deck == 0 ? _a : _b;
        if (s.IsFile == isFile && Math.Abs(s.Frac - frac) < 0.002 && s.TimeText == time && s.Play.Text == (paused ? "▶" : "II")) return;
        s.IsFile = isFile; s.Frac = frac; s.TimeText = time; s.Play.Visible = isFile; s.Play.Text = paused ? "▶" : "II"; s.Invalidate();
    }
    public Fader VolumeControl => _vol;
    public Fader BlendControl => _blend;
    public void StepNudge(int deck,double step) { var strip=deck==0?_a:_b;strip.Nudge=Math.Clamp(strip.Nudge+step,-8,8);NudgeChanged?.Invoke(deck,strip.Nudge);strip.Invalidate(); }
    public void ResetNudge(int deck) { var s = deck == 0 ? _a : _b; s.Nudge = 0; s.Invalidate(); }
}
