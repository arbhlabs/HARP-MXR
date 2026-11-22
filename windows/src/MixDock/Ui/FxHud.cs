using System.Drawing.Drawing2D;
using MixDock.Audio;

namespace MixDock.Ui;

/// <summary>Large on-screen readout of the right-stick effect: name, wet level, X/Y values and the current performance move.</summary>
public sealed class FxHud : Control
{
    private PadFx _fx = PadFx.Space;
    private double _amount, _x = .5, _y = .5;
    private bool _frozen;
    private Color _accent = Theme.Cyan;
    private string _move = "";
    private long _moveAt;
    private long _flashAt;
    private int _deck;

    public FxHud()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface; AccessibleName = "Effect readout"; Height = 104;
    }

    public void SetFx(PadFx fx, double amount, double x, double y, bool frozen, int deck)
    {
        amount = double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;
        if (fx == _fx && Math.Abs(amount - _amount) < .004 && Math.Abs(x - _x) < .004 && Math.Abs(y - _y) < .004 && frozen == _frozen && deck == _deck) return;
        if (fx != _fx) _flashAt = Environment.TickCount64;
        _fx = fx; _amount = amount; _x = x; _y = y; _frozen = frozen; _deck = deck;
        _accent = deck == 0 ? Theme.Cyan : Theme.Amber;
        Invalidate();
    }

    /// <summary>Names the performance move in progress (empty = none). Stays visible while it is refreshed, fades about 1.4 s after.</summary>
    public void SetMove(string text)
    {
        long now = Environment.TickCount64;
        if (text == _move && (text.Length == 0 || now - _moveAt < 200)) return;
        if (text.Length > 0 || now - _moveAt > 1400) { _move = text.Length > 0 ? text : _move; }
        if (text.Length > 0) _moveAt = now;
        Invalidate();
    }
    public void Tick()
    {
        if (_move.Length > 0 && Environment.TickCount64 - _moveAt > 1600) { _move = ""; Invalidate(); }
        else if (Environment.TickCount64 - _flashAt < 400) Invalidate();
    }

    private static string Detail(PadFx fx) => fx switch
    {
        PadFx.Space => ("SIZE", "REVERB ↔ DELAY") is var t ? t.Item1 + " · " + t.Item2 : "",
        PadFx.Reverb => "SIZE · BRIGHTNESS",
        PadFx.Flanger => "RATE · DEPTH",
        PadFx.Phaser => "RATE · DEPTH",
        PadFx.Crush => "RATE · BITS",
        PadFx.Gate => "1/8 ↔ 1/16 · LENGTH",
        PadFx.Echo => "TIME · FEEDBACK",
        _ => ""
    };

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "FxHud", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = UiMetrics.Scale(this); int D(float n) => (int)Math.Round(n * s);
        if (Width < D(60) || Height < D(60)) return;
        var box = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        using (var path = Theme.Round(box, 12 * s))
        {
            using var fill = new SolidBrush(Theme.Raised); g.FillPath(fill, path);
            long since = Environment.TickCount64 - _flashAt;
            using var edge = new Pen(since < 400 ? _accent : Theme.Edge, since < 400 ? 2 * s : 1); g.DrawPath(edge, path);
        }
        Theme.TextAt(g, $"RIGHT STICK FX · DECK {(_deck == 0 ? "A" : "B")}", new Rectangle(D(12), D(5), Width - D(110), D(14)), 7.5f, Theme.Muted, true);
        Theme.TextAt(g, $"X {_x * 100:0}  Y {_y * 100:0}", new Rectangle(Width - D(98), D(5), D(88), D(14)), 7.5f, Theme.Muted, false, false);
        Theme.TextAt(g, FxCurves.Name(_fx), new Rectangle(D(12), D(18), Width - D(88), D(36)), 22, _accent, true);
        Theme.TextAt(g, $"{_amount * 100:0}%", new Rectangle(Width - D(80), D(20), D(68), D(32)), 18, _amount > 0 ? Theme.Text : Theme.Muted, true);
        var bar = new RectangleF(D(12), D(58), Width - D(24), D(7));
        if (Theme.Ok(bar))
        {
            using (var path = Theme.Round(bar, 3.5f * s)) { using var b = new SolidBrush(Color.FromArgb(39, 45, 57)); g.FillPath(b, path); }
            var lit = new RectangleF(bar.X, bar.Y, (float)(bar.Width * _amount), bar.Height);
            if (Theme.Ok(lit)) { using var path = Theme.Round(lit, 3.5f * s); using var b = new SolidBrush(_accent); g.FillPath(b, path); }
        }
        if (_move.Length > 0)
        {
            int age = (int)Math.Clamp(Environment.TickCount64 - _moveAt, 0, 1600);
            int alpha = age < 1000 ? 255 : (int)(255 * (1600 - age) / 600.0);
            Theme.TextAt(g, _move, new Rectangle(D(12), D(70), Width - D(24), D(24)), 11, Color.FromArgb(Math.Clamp(alpha, 0, 255), Theme.Text), true);
        }
        else Theme.TextAt(g, (_frozen ? "FROZEN · " : "") + Detail(_fx), new Rectangle(D(12), D(70), Width - D(24), D(24)), 8, _frozen ? Theme.Text : Theme.Muted, _frozen);
    }
}

/// <summary>What the controller HUD shows. Stick view: the set's four directions (or the two side slots), the stick puck,
/// the amount ring and the leading effect. Action view: a button glyph, what it did and its value.</summary>
public sealed record HudView(
    bool Stick, string Title, string Value, double Amount, string Source, string Glyph, string Deck, string SetName,
    string[]? Directions = null, double[]? Shares = null, double StickX = 0, double StickY = 0, int Accent = 0, string Detail = "")
{
    /// <summary>Accent: 0 = deck A (cyan), 1 = deck B (amber), 2 = both decks (green), 3 = warning (red).</summary>
    public Color AccentColor => Accent switch { 1 => Theme.Amber, 2 => Theme.Green, 3 => Theme.Red, _ => Theme.Cyan };
}

/// <summary>
/// Big controller feedback (R5), readable from 1–1.5 m: large effect / action name, amount ring, which stick or button,
/// the current set and the four directions' effects. Shown on every controller action, it fades out about 1.5 s after
/// the last update; with Windows animations off it simply disappears (no fade). Scales with its own height (design
/// 600 × 176), so it fits at any DPI and window size.
/// </summary>
public sealed class FxOverlay : Control
{
    public const int DesignWidth = 600, DesignHeight = 176;
    public const int HoldMs = 1500, FadeMs = 350;
    private HudView? _view;
    private long _shownAt;
    /// <summary>Windows "Show animations" (SPI_GETCLIENTAREAANIMATION); replaceable for offscreen shots.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool> AnimationsEnabled { get; set; } = SystemAnimations;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;
    public HudView? View => _view;

    public FxOverlay()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background; AccessibleName = "Controller feedback"; AccessibleRole = AccessibleRole.Alert; Visible = false;
    }

    /// <summary>Shows (or refreshes) the HUD. Repeated identical views only extend the hold time.</summary>
    public void Show(HudView view)
    {
        bool same = _view != null && _view == view;
        _view = view; _shownAt = Clock();
        AccessibleDescription = $"{view.Title} {view.Value} {view.Source} {view.Deck}";
        if (!Visible) { Visible = true; BringToFront(); }
        if (!same) Invalidate();
    }

    /// <summary>Call from the UI timer: fades and hides after the hold time.</summary>
    public void Tick()
    {
        if (!Visible || _view == null) return;
        long age = Clock() - _shownAt;
        if (age > HoldMs + (AnimationsEnabled() ? FadeMs : 0)) { Visible = false; return; }
        if (age > HoldMs && AnimationsEnabled()) Invalidate();
    }

    /// <summary>0..1 visibility (1 while held, then the fade; reduced motion: 1 until it hides).</summary>
    public float Opacity()
    {
        long age = Clock() - _shownAt;
        if (age <= HoldMs || !AnimationsEnabled()) return 1;
        return Math.Clamp(1 - (age - HoldMs) / (float)FadeMs, 0, 1);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
    private static long _animAt; private static bool _anim = true;
    private static bool SystemAnimations()
    {
        long now = Environment.TickCount64;
        if (now >= _animAt) { _anim = !SystemParametersInfo(0x1042, 0, out int v, 0) || v != 0; _animAt = now + 1000; }
        return _anim;
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e.Graphics), "FxOverlay", this);

    private static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>The largest pixel font (≤ <paramref name="px"/>) whose text fits <paramref name="width"/>; never below 60 %.</summary>
    internal static Font FitFont(Graphics g, string text, float px, int width, bool bold, bool numeric = false)
    {
        for (float p = px; ; p *= .92f)
        {
            var f = new Font(numeric ? Theme.NumericFamily : Theme.TextFamily, Math.Max(6, p), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            if (p < px * .6f || TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width <= width) return f;
            f.Dispose();
        }
    }

    /// <summary>Widest text run that would be drawn ellipsised at the current size (offscreen fit check); empty = all fit.</summary>
    public List<string> Overflows()
    {
        var bad = new List<string>();
        if (_view == null || Width < 10 || Height < 10) return bad;
        using var bmp = new Bitmap(Width, Height); using var g = Graphics.FromImage(bmp);
        Layout(g, (text, rect, px, bold, numeric) =>
        {
            using var f = FitFont(g, text, px, rect.Width, bold, numeric);
            if (TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width > rect.Width + 1 ||
                f.Height > rect.Height + 2) bad.Add($"'{text}' needs more than {rect.Width}×{rect.Height}");
        });
        return bad;
    }

    private delegate void TextSink(string text, Rectangle rect, float px, bool bold, bool numeric);

    private void PaintCore(Graphics g)
    {
        var v = _view; if (v == null || Width < 40 || Height < 30) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float o = Opacity(), s = Math.Min(Width / (float)DesignWidth, Height / (float)DesignHeight);
        Color accent = v.AccentColor, bg = Theme.Background;
        Color C(Color c) => Mix(bg, c, o);
        var box = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = Theme.Round(box, 14 * s))
        {
            using var fill = new SolidBrush(C(Theme.Raised)); g.FillPath(fill, path);
            using var edge = new Pen(C(accent), Math.Max(1.5f, 2.5f * s)); g.DrawPath(edge, path);
        }
        float cx = 92 * s, cy = Height / 2f, r = 62 * s;
        if (v.Stick)
        {
            // compass: four direction ticks, the share of each lit, the amount as a ring, the stick puck
            var ring = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (var track = new Pen(C(Theme.Edge), 7 * s)) Theme.Ellipse(g, track, ring);
            if (double.IsFinite(v.Amount) && v.Amount > 0)
                using (var amt = new Pen(C(accent), 7 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Theme.Arc(g, amt, ring, -90, (float)(360 * Math.Clamp(v.Amount, 0, 1)));
            for (int k = 0; k < 4; k++)
            {
                double share = v.Shares is { Length: 4 } ? v.Shares[k] : 0;
                var (dx, dy) = k switch { 0 => (0f, -1f), 1 => (0f, 1f), 2 => (-1f, 0f), _ => (1f, 0f) };
                using var b = new SolidBrush(C(share > .02 ? Mix(Theme.Edge, accent, (float)Math.Clamp(share, 0, 1)) : Theme.Edge));
                float tr = (7 + 5 * (float)share) * s;
                Theme.Ellipse(g, b, new RectangleF(cx + dx * (r - 22 * s) - tr, cy + dy * (r - 22 * s) - tr, 2 * tr, 2 * tr));
            }
            float px = cx + (float)Math.Clamp(v.StickX, -1, 1) * (r - 12 * s), py = cy - (float)Math.Clamp(v.StickY, -1, 1) * (r - 12 * s);
            using (var puck = new SolidBrush(C(Theme.Text))) Theme.Ellipse(g, puck, new RectangleF(px - 9 * s, py - 9 * s, 18 * s, 18 * s));
            using (var glyphFont = new Font(Theme.TextFamily, 15 * s, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, v.Glyph, glyphFont, new Rectangle((int)(cx - r), (int)(cy - 12 * s), (int)(2 * r), (int)(24 * s)),
                    C(Theme.Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else
        {
            // button glyph badge
            var badge = new RectangleF(cx - r * .8f, cy - r * .8f, r * 1.6f, r * 1.6f);
            using (var b = new SolidBrush(C(Mix(Theme.Raised, accent, .22f)))) Theme.Ellipse(g, b, badge);
            using (var p = new Pen(C(accent), 3 * s)) Theme.Ellipse(g, p, badge);
            using var f = FitFont(g, v.Glyph, 40 * s, (int)(r * 1.4f), true);
            TextRenderer.DrawText(g, v.Glyph, f, Rectangle.Round(badge), C(Theme.Text), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        Layout(g, (text, rect, px, bold, numeric) =>
        {
            if (string.IsNullOrEmpty(text)) return;
            using var f = FitFont(g, text, px, rect.Width, bold, numeric);
            Color col = px >= 24 * s ? (numeric ? Theme.Text : accent) : bold ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, text, f, rect, C(col), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                (numeric ? TextFormatFlags.Right : TextFormatFlags.Left));
        });
        if (v.Stick && v.Directions is { Length: 4 } && v.Shares is { Length: 4 })
        {
            // light the active direction rows
            for (int k = 0; k < 4; k++)
            {
                if (v.Shares[k] < .02) continue;
                var cell = DirectionCell(k, s);
                using var bar = new SolidBrush(C(Mix(Theme.Raised, accent, .25f + .45f * (float)v.Shares[k])));
                g.FillRectangle(bar, cell.X - 4 * s, cell.Bottom - 3 * s, cell.Width * (float)Math.Clamp(v.Shares[k], 0, 1), 3 * s);
            }
        }
    }

    private Rectangle DirectionCell(int k, float s)
    {
        int x0 = (int)(176 * s), w = (int)((DesignWidth - 176 - 16) * s / 2), y0 = (int)(116 * s), h = (int)(22 * s);
        return new Rectangle(x0 + (k / 2) * w, y0 + (k % 2) * h, w - (int)(8 * s), h);
    }

    /// <summary>Every text run, its box and its target size (shared by painting and the fit check).</summary>
    private void Layout(Graphics g, TextSink text)
    {
        var v = _view!; float s = Math.Min(Width / (float)DesignWidth, Height / (float)DesignHeight);
        int x0 = (int)(176 * s), right = Width - (int)(16 * s), valueW = (int)(118 * s);
        text($"{v.Source} · {v.Deck}", new Rectangle(x0, (int)(12 * s), right - x0, (int)(20 * s)), 13 * s, true, false);
        text(v.Title, new Rectangle(x0, (int)(34 * s), right - x0 - valueW - (int)(6 * s), (int)(46 * s)), 34 * s, true, false);
        text(v.Value, new Rectangle(right - valueW, (int)(34 * s), valueW, (int)(46 * s)), 30 * s, true, true);
        text(v.Detail.Length > 0 ? $"{v.SetName} · {v.Detail}" : v.SetName, new Rectangle(x0, (int)(84 * s), right - x0, (int)(24 * s)), 15 * s, false, false);
        if (v.Stick && v.Directions is { Length: 4 } dirs)
        {
            string[] arrows = { "↑", "↓", "←", "→" };
            for (int k = 0; k < 4; k++)
                if (dirs[k].Length > 0) text($"{arrows[k]} {dirs[k]}", DirectionCell(k, s), 15 * s, v.Shares is { Length: 4 } sh && sh[k] >= .5, false);
        }
    }
}
