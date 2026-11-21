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
