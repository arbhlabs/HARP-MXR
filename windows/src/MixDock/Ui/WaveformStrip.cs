using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MixDock.Audio;

namespace MixDock.Ui;

/// <summary>What one deck shows this frame. File decks: <see cref="Track"/> + head in track seconds.
/// Live decks: <see cref="Live"/> + head in analyzer seconds (column index / 100).</summary>
public readonly record struct WaveInput(TrackWaveform? Track, WaveformAnalyzer? Live, double HeadSec, bool Playing, double Rate,
    BeatGrid Grid, double? CueSec, double? LoopStartSec, double? LoopEndSec, bool Analyzing)
{
    public bool Empty => Track == null && Live == null;
}

/// <summary>
/// Two stacked deck lanes (Rekordbox/Serato layout): a scrolling 3-band waveform around a fixed centre playhead,
/// and under it a full-track overview with playhead, cue, loop region and beat-grid ticks.
/// Pixels are written straight into a reused 32-bit buffer from precomputed 10 ms peak columns (no per-frame
/// allocation), then blitted; the control is double-buffered and redrawn by its own ~60 fps timer.
/// </summary>
public sealed class WaveformStrip : Control
{
    public const double WindowSec = 8;                       // seconds visible in the scrolling lane
    // band colours: low = amber, mid = warm white, high = pale cyan (played audio is drawn dimmer)
    private static readonly int[] Band = { Argb(232, 146, 48), Argb(78, 199, 126), Argb(156, 222, 236) };
    private static readonly int[] BandPast = { Argb(150, 96, 36), Argb(43, 119, 73), Argb(98, 140, 150) };
    private static readonly int Bg = Argb(17, 19, 26), Center = Argb(40, 45, 58);
    private static readonly Color CueColor = Color.FromArgb(255, 150, 30), LoopColor = Color.FromArgb(96, 204, 132);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Lane[] _lanes = { new("A", Theme.Cyan), new("B", Theme.Amber) };

    /// <summary>Polled once per frame on the UI thread for each deck (0 = A, 1 = B).</summary>
    public Func<int, WaveInput>? Input;
    /// <summary>Click on an overview: deck, fraction of the track.</summary>
    public event Action<int, double>? SeekRequested;

    private sealed class Lane(string name, Color accent)
    {
        public readonly string Name = name; public readonly Color Accent = accent;
        public WaveInput In; public double Head;
        public Bitmap? Scroll, Overview; public int[] Px = [], OvPx = [];
        public TrackWaveform? OverviewOf; public int OverviewW;
        public Rectangle ScrollRect, OverviewRect;
    }

    public WaveformStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top; Height = 140; BackColor = Theme.Background; Padding = new Padding(24, 4, 24, 4);
        AccessibleName = "Deck waveforms"; AccessibleRole = AccessibleRole.Graphic;
        _timer.Tick += (_, _) => Frame();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _timer.Start(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); foreach (var l in _lanes) { l.Scroll?.Dispose(); l.Overview?.Dispose(); } }
        base.Dispose(disposing);
    }

    /// <summary>Pull new input and advance the smoothed playheads; repaint only when something moves.</summary>
    public void Frame()
    {
        double dt = Math.Clamp(_clock.Elapsed.TotalSeconds, 0, 0.25); _clock.Restart();
        bool dirty = false;
        for (int d = 0; d < 2; d++)
        {
            var l = _lanes[d];
            var inp = Input?.Invoke(d) ?? default;
            // source positions move in 10-20 ms steps; glide between them so the scroll stays smooth
            if (inp.Empty || !inp.Playing || !ReferenceEquals(inp.Track, l.In.Track) || !ReferenceEquals(inp.Live, l.In.Live)) l.Head = inp.HeadSec;
            else
            {
                double pred = l.Head + dt * (inp.Rate > 0 ? inp.Rate : 1), err = inp.HeadSec - pred;
                l.Head = Math.Abs(err) > 0.25 ? inp.HeadSec : pred + err * 0.12;
            }
            dirty |= inp.Playing || inp != l.In;     // paused or idle decks stop repainting
            l.In = inp;
        }
        if (dirty) Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float s = UiMetrics.Scale(this); int D(float n) => (int)Math.Round(n * s);
        int x = Padding.Left + D(36), w = Math.Max(10, Width - 2*x), y = Padding.Top;
        int laneH = (Height - Padding.Vertical) / 2, ovH = D(18), gap = D(3);
        for (int d = 0; d < 2; d++)
        {
            var l = _lanes[d]; int top = y + d * laneH;
            l.ScrollRect = new Rectangle(x, top, w, Math.Max(8, laneH - ovH - gap * 2));
            l.OverviewRect = new Rectangle(x, l.ScrollRect.Bottom + gap, w, ovH);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        for (int d = 0; d < 2; d++)
        {
            var r = _lanes[d].OverviewRect;
            if (r.Contains(e.Location) && _lanes[d].In.Track != null) SeekRequested?.Invoke(d, Math.Clamp((e.X - r.X) / (double)r.Width, 0, 1));
        }
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "WaveformStrip.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        float s = UiMetrics.Scale(this); int D(float n) => (int)Math.Round(n * s);
        foreach (var l in _lanes)
        {
            using (var b = new SolidBrush(l.Accent)) g.FillRectangle(b, Padding.Left, l.ScrollRect.Y, D(3), l.OverviewRect.Bottom - l.ScrollRect.Y);
            Theme.TextAt(g, l.Name, new Rectangle(Padding.Left + D(8), l.ScrollRect.Y, D(24), l.ScrollRect.Height), 13, l.Accent, true);
            DrawScroll(g, l, s);
            DrawOverview(g, l, s);
        }
    }

    // ---------- scrolling lane ----------
    private void DrawScroll(Graphics g, Lane l, float s)
    {
        var r = l.ScrollRect; if (r.Width < 4 || r.Height < 4) return;
        var bmp = Ensure(ref l.Scroll, r.Size);
        int w = r.Width, h = r.Height, cy = h / 2;
        if (l.Px.Length < w * h) l.Px = new int[w * h];
        var px = l.Px; Array.Fill(px, Bg, 0, w * h);
        for (int x = 0; x < w; x++) px[cy * w + x] = Center;
        var inp = l.In;
        double secPerPx = WindowSec / w, head = l.Head;
        int centerX = w / 2;
        if (!inp.Empty)
        {
            float gain = inp.Track?.Gain ?? 1.6f, half = cy - 1;
            for (int x = 0; x < w; x++)
            {
                double t0 = head + (x - centerX) * secPerPx;
                long c0 = (long)Math.Floor(t0 * WaveformAnalyzer.ColumnsPerSecond), c1 = (long)Math.Floor((t0 + secPerPx) * WaveformAnalyzer.ColumnsPerSecond);
                if (c1 <= c0) c1 = c0 + 1;
                if (!Max(inp, c0, c1, out var lo, out var mi, out var hi)) continue;
                ColumnPixels(px, w, h, x, cy, lo * gain * half, mi * gain * half, hi * gain * 1.4f * half, x < centerX ? BandPast : Band);
            }
        }
        Blit(bmp, px, w, h);
        g.DrawImageUnscaled(bmp, r.Location);
        if (inp.Empty)
        {
            Theme.TextAt(g, "Load a song or choose an app to see its waveform", r, 8.5f, Theme.Muted, false, true);
            return;
        }
        if (inp.Analyzing) Theme.TextAt(g, "Analysing track…", new Rectangle(r.X + 8, r.Y, r.Width / 3, r.Height / 3), 8, Theme.Muted);
        double tL = head - centerX * secPerPx, tR = head + (w - centerX) * secPerPx;
        float X(double t) => r.X + centerX + (float)((t - head) / secPerPx);
        // loop region
        if (inp.LoopStartSec is double ls && inp.LoopEndSec is double le && le > ls && le > tL && ls < tR)
        {
            float a = Math.Max(r.X, X(ls)), b = Math.Min(r.Right, X(le));
            using var fill = new SolidBrush(Color.FromArgb(46, LoopColor)); g.FillRectangle(fill, a, r.Y, b - a, r.Height);
            using var edge = new Pen(LoopColor, Math.Max(1, s)); g.DrawLine(edge, X(ls), r.Y, X(ls), r.Bottom); g.DrawLine(edge, X(le), r.Y, X(le), r.Bottom);
        }
        // beat grid: bar lines brighter than beat lines
        if (inp.Grid.Valid)
        {
            using var beat = new Pen(Color.FromArgb(48, 255, 255, 255), 1); using var bar = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1, s));
            using var barTick = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            foreach (var (t, k) in inp.Grid.Beats(tL, tR))
            {
                float x = X(t); bool isBar = inp.Track != null && ((k % 4) + 4) % 4 == 0;
                g.DrawLine(isBar ? bar : beat, x, r.Y, x, r.Bottom);
                if (isBar) { g.FillRectangle(barTick, x - s, r.Y, 2 * s, 4 * s); g.FillRectangle(barTick, x - s, r.Bottom - 4 * s, 2 * s, 4 * s); }
            }
        }
        // cue marker
        if (inp.CueSec is double cue && cue >= tL && cue <= tR)
        {
            float x = X(cue);
            using var p = new Pen(CueColor, Math.Max(1, s)); g.DrawLine(p, x, r.Y, x, r.Bottom);
            using var b = new SolidBrush(CueColor); g.FillPolygon(b, [new PointF(x - 5 * s, r.Y), new PointF(x + 5 * s, r.Y), new PointF(x, r.Y + 7 * s)]);
        }
        // fixed playhead
        using (var ph = new Pen(Color.White, 2 * s)) g.DrawLine(ph, r.X + centerX, r.Y, r.X + centerX, r.Bottom);
        using (var e = new Pen(Theme.Edge)) g.DrawRectangle(e, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    private static bool Max(in WaveInput inp, long c0, long c1, out float lo, out float mi, out float hi)
    {
        lo = mi = hi = 0; bool any = false;
        if (inp.Track is { } tr)
        {
            var cols = tr.Columns;
            long a = Math.Max(0, c0), b = Math.Min(cols.Length, c1);
            for (long i = a; i < b; i++) { var c = cols[i]; any = true; if (c.Low > lo) lo = c.Low; if (c.Mid > mi) mi = c.Mid; if (c.High > hi) hi = c.High; }
        }
        else if (inp.Live is { } lv)
        {
            for (long i = c0; i < c1; i++)
                if (lv.TryGet(i, out var c)) { any = true; if (c.Low > lo) lo = c.Low; if (c.Mid > mi) mi = c.Mid; if (c.High > hi) hi = c.High; }
        }
        return any;
    }

    /// <summary>One symmetric bar per pixel column: each band fills out to its own height; where bands overlap the
    /// shorter one is drawn on top, so a bass-heavy kick reads amber with a white/cyan core.</summary>
    internal static void ColumnPixels(int[] px, int w, int h, int x, int cy, float lo, float mi, float hi, int[] colors)
    {
        int max = (int)Math.Min(cy - 1, Math.Max(lo, Math.Max(mi, hi)));
        for (int dy = 0; dy <= max; dy++)
        {
            int c = -1; float best = float.MaxValue;
            if (lo > 0 && lo >= dy && lo < best) { best = lo; c = colors[0]; }
            if (mi > 0 && mi >= dy && mi < best) { best = mi; c = colors[1]; }
            if (hi > 0 && hi >= dy && hi < best) { c = colors[2]; }
            if (c == -1) continue;
            px[(cy - dy) * w + x] = c;
            if (cy + dy < h) px[(cy + dy) * w + x] = c;
        }
    }

    // ---------- overview ----------
    private void DrawOverview(Graphics g, Lane l, float s)
    {
        var r = l.OverviewRect; if (r.Width < 4 || r.Height < 4) return;
        var inp = l.In;
        using (var bg = new SolidBrush(Color.FromArgb(17, 19, 26))) g.FillRectangle(bg, r);
        if (inp.Track is { } tr && tr.Columns.Length > 0)
        {
            if (!ReferenceEquals(l.OverviewOf, tr) || l.OverviewW != r.Width || l.Overview?.Height != r.Height)
            {   // static image, rebuilt only when the track or the size changes
                var bmp = Ensure(ref l.Overview, r.Size); int w = r.Width, h = r.Height, cy = h / 2;
                var px = new int[w * h]; Array.Fill(px, Bg);
                double per = tr.Columns.Length / (double)w;
                for (int x = 0; x < w; x++)
                {
                    float lo = 0, mi = 0, hi = 0;
                    for (int i = (int)(x * per), e = Math.Max((int)((x + 1) * per), (int)(x * per) + 1); i < e && i < tr.Columns.Length; i++)
                    { var c = tr.Columns[i]; lo = Math.Max(lo, c.Low); mi = Math.Max(mi, c.Mid); hi = Math.Max(hi, c.High); }
                    ColumnPixels(px, w, h, x, cy, lo * tr.Gain * cy, mi * tr.Gain * cy, hi * tr.Gain * 1.4f * cy, Band);
                }
                Blit(bmp, px, w, h); l.OverviewOf = tr; l.OverviewW = w;
            }
            g.DrawImageUnscaled(l.Overview!, r.Location);
            double dur = Math.Max(0.001, tr.DurationSec);
            float X(double t) => r.X + (float)(Math.Clamp(t / dur, 0, 1) * (r.Width - 1));
            using (var played = new SolidBrush(Color.FromArgb(120, 14, 16, 22))) g.FillRectangle(played, r.X, r.Y, X(l.Head) - r.X, r.Height);
            if (inp.Grid.Valid)
            {   // one tick per 4 bars (16 beats): the phrase grid a DJ counts in
                using var tick = new Pen(Color.FromArgb(110, 255, 255, 255), 1);
                foreach (var (t, k) in inp.Grid.Beats(0, dur)) if (((k % 16) + 16) % 16 == 0) g.DrawLine(tick, X(t), r.Bottom - 4 * s, X(t), r.Bottom);
            }
            if (inp.LoopStartSec is double ls && inp.LoopEndSec is double le && le > ls)
            { using var fill = new SolidBrush(Color.FromArgb(90, LoopColor)); g.FillRectangle(fill, X(ls), r.Y, Math.Max(2, X(le) - X(ls)), r.Height); }
            if (inp.CueSec is double cue) { using var b = new SolidBrush(CueColor); float x = X(cue); g.FillPolygon(b, [new PointF(x - 4 * s, r.Y), new PointF(x + 4 * s, r.Y), new PointF(x, r.Y + 6 * s)]); }
            using var ph = new Pen(Color.White, 2 * s); g.DrawLine(ph, X(l.Head), r.Y, X(l.Head), r.Bottom);
        }
        else if (inp.Live is { } lv)
        {   // live app: the last minute of captured audio, newest (heard) at the right edge
            int w = r.Width, h = r.Height, cy = h / 2; var bmp = Ensure(ref l.Overview, r.Size); l.OverviewOf = null;
            if (l.OvPx.Length < w * h) l.OvPx = new int[w * h];
            var px = l.OvPx; Array.Fill(px, Bg, 0, w * h);
            const double Span = 60; double per = Span * WaveformAnalyzer.ColumnsPerSecond / w;
            long headCol = (long)(l.Head * WaveformAnalyzer.ColumnsPerSecond);
            for (int x = 0; x < w; x++)
            {
                long c0 = headCol - (long)((w - x) * per), c1 = Math.Max(c0 + 1, headCol - (long)((w - x - 1) * per));
                if (Max(inp, c0, c1, out var lo, out var mi, out var hi)) ColumnPixels(px, w, h, x, cy, lo * 1.6f * cy, mi * 1.6f * cy, hi * 2.2f * cy, BandPast);
            }
            Blit(bmp, px, w, h); g.DrawImageUnscaled(bmp, r.Location);
            Theme.TextAt(g, "LIVE · LAST 60 s", new Rectangle(r.X + 6, r.Y, r.Width / 2, r.Height), 7, Theme.Muted);
            using var ph = new Pen(Color.White, 2 * s); g.DrawLine(ph, r.Right - 1, r.Y, r.Right - 1, r.Bottom);
        }
        using var edge = new Pen(Theme.Edge); g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    private static Bitmap Ensure(ref Bitmap? bmp, Size size)
    {
        if (bmp == null || bmp.Size != size) { bmp?.Dispose(); bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb); }
        return bmp;
    }
    private static void Blit(Bitmap bmp, int[] px, int w, int h)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, data.Scan0 + y * data.Stride, w); }
        finally { bmp.UnlockBits(data); }
    }
    private static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
}
