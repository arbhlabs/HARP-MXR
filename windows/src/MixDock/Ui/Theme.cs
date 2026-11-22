using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace MixDock.Ui;

internal static class Theme
{
    // Near-black OLED base; surfaces step up in small, even increments so hierarchy comes from contrast, not gradients.
    public static readonly Color Background = Color.FromArgb(11,13,15), Surface = Color.FromArgb(21,24,27),
        Raised = Color.FromArgb(32,36,40), Edge = Color.FromArgb(48,54,60), Text = Color.FromArgb(236,240,248),
        Muted = Color.FromArgb(150,162,180), Cyan = Color.FromArgb(70,216,227), Amber = Color.FromArgb(245,166,35),
        Red = Color.FromArgb(248,104,116), Green = Color.FromArgb(88,206,132), Idle = Color.FromArgb(19,22,25);
    public static readonly Color Pink = Amber; // compatibility alias
    /// <summary>GDI+ throws OutOfMemoryException for zero/negative/NaN rects, pens and sweeps; these helpers skip such draws.</summary>
    public static bool Ok(RectangleF r) => float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Width) && float.IsFinite(r.Height) && r.Width >= 1 && r.Height >= 1;
    public static void Arc(Graphics g, Pen p, RectangleF r, float start, float sweep)
    {
        if (!Ok(r) || !float.IsFinite(start) || !float.IsFinite(sweep)) return;
        sweep = Math.Clamp(sweep, -360f, 360f); if (Math.Abs(sweep) < .01f) return;
        g.DrawArc(p, r, start, sweep);
    }
    public static void Ellipse(Graphics g, Pen p, RectangleF r) { if (Ok(r)) g.DrawEllipse(p, r); }
    public static void Ellipse(Graphics g, Brush b, RectangleF r) { if (Ok(r)) g.FillEllipse(b, r); }
    public static LinearGradientBrush? Gradient(RectangleF r, Color a, Color b, float angle) => Ok(r) ? new LinearGradientBrush(r, a, b, angle) : null;
    /// <summary>A paint glitch must never show an error dialog: log once per call site and skip the frame.</summary>
    [ThreadStatic] private static float _paintScale;
    public static void Guard(Action paint, string where, Control? control = null)
    {
        float before=_paintScale;
        _paintScale=control==null?1:UiMetrics.Scale(control);
        try { paint(); }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException or OverflowException)
        { if (_lastGuard != where) { _lastGuard = where; Log.Write($"paint skipped in {where}: {ex.GetType().Name} {ex.Message}"); } }
        finally { _paintScale=before; }
    }
    private static string? _lastGuard;
    // Shared type scale in logical points. Console fonts use explicit pixels to avoid double DPI scaling.
    public const string TextFamily = "Segoe UI", NumericFamily = "Consolas";
    private static float TypeSize(float size) => size switch
    {
        <= 7 => 7, <= 8 => 8, <= 9 => 9, <= 11 => 11, <= 14 => 14, _ => size
    };
    public static Font Font(float size, bool bold = false) =>
        new(TextFamily, TypeSize(size), bold ? FontStyle.Bold : FontStyle.Regular);
    public static Font PixelFont(float points, float scale, bool bold = false, bool numeric = false) =>
        new(numeric ? NumericFamily : TextFamily, Math.Max(7, TypeSize(points) * 96 / 72 * scale),
            bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = Math.Min(radius*2, Math.Min(r.Width,r.Height));
        p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
    }
    public static void TextAt(Graphics g,string text,Rectangle r,float size,Color color,bool bold=false,bool center=false,bool numeric=false)
    {
        using var f = PixelFont(size,_paintScale>0?_paintScale:1,bold,numeric);
        TextRenderer.DrawText(g,text,f,r,color,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|
            (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
    }
}
public class SurfacePanel : Panel
{
    public SurfacePanel() { DoubleBuffered=true; BackColor=Theme.Surface; ResizeRedraw=true; }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "Theme.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        if(Width<2 || Height<2) return;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var p=Theme.Round(new RectangleF(.5f,.5f,Width-1,Height-1),2*UiMetrics.Scale(this));
        using var b=new SolidBrush(Theme.Surface);
        e.Graphics.FillPath(b,p); using var pen=new Pen(Theme.Edge); e.Graphics.DrawPath(pen,p); base.OnPaint(e);
    }
}
public sealed class StudioButton : Button
{
    private bool _hover;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Accent {get;set;}=Theme.Muted;
    private bool _active;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; Invalidate(); }
    }
    public StudioButton()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; BackColor=Theme.Surface; ForeColor=Theme.Text;
        Cursor=Cursors.Hand; Font=Theme.Font(9,true); AccessibleRole=AccessibleRole.PushButton;
    }
    protected override void OnMouseEnter(EventArgs e) {_hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e) {_hover=false;Invalidate();base.OnMouseLeave(e);}
    /// <summary>Optional status lamp along the bottom edge (e.g. pad has a sample or a hot cue).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Lamp
    {
        get => _lamp;
        set { if (_lamp == value) return; _lamp = value; Invalidate(); }
    }
    private Color? _lamp;
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "Theme.2", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g=e.Graphics;
        // the Button base never repaints the background under UserPaint: without this, old text and the
        // anti-aliased edge pixels of earlier frames ghost through the translucent fill (garbled labels, stray marks)
        g.Clear(Parent?.BackColor ?? Theme.Background);
        if(Width<4||Height<4)return;
        float s=UiMetrics.Scale(this);
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using var p=Theme.Round(new RectangleF(1,1,Width-3,Height-3),2.5f*s);
        // Three unambiguous states: idle (dark, hairline), hover (raised, accent edge), active (solid accent, dark text).
        Color fill=Active?Accent:_hover&&Enabled?Theme.Raised:Theme.Idle;
        Color edge=Active?Accent:Focused||_hover?Accent:Theme.Edge;
        using var b=new SolidBrush(fill);
        using var pen=new Pen(edge,Focused?2:1);
        g.FillPath(b,p);g.DrawPath(pen,p);
        if(_lamp is Color lamp&&!Active)
        {
            using var lb=new SolidBrush(lamp);
            g.FillRectangle(lb,4*s,Height-4*s,Width-8*s,Math.Max(2,2*s));
        }
        Color text=!Enabled?Theme.Edge:Active?Theme.Background:_hover?Theme.Text:ForeColor;
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,text,
            TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
    }
}
