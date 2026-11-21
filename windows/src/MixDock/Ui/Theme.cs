using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace MixDock.Ui;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(16,18,20), Surface = Color.FromArgb(25,28,30),
        Raised = Color.FromArgb(34,37,40), Edge = Color.FromArgb(48,53,57), Text = Color.FromArgb(236,240,248),
        Muted = Color.FromArgb(153,165,185), Cyan = Color.FromArgb(70,216,227), Amber = Color.FromArgb(245,166,35),
        Red = Color.FromArgb(248,104,116);
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
    public static Font Font(float size, bool bold = false) => new("Segoe UI Variable", size, bold ? FontStyle.Bold : FontStyle.Regular);
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = Math.Min(radius*2, Math.Min(r.Width,r.Height));
        p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
    }
    public static void TextAt(Graphics g,string text,Rectangle r,float size,Color color,bool bold=false,bool center=false)
    {
        using var f = new Font("Segoe UI",size*96/72*(_paintScale>0?_paintScale:1),bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
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
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Active {get;set;}
    public StudioButton()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; BackColor=Theme.Surface; ForeColor=Theme.Text;
        Cursor=Cursors.Hand; Font=Theme.Font(9,true); AccessibleRole=AccessibleRole.PushButton;
    }
    protected override void OnMouseEnter(EventArgs e) {_hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e) {_hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "Theme.2", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g=e.Graphics;
        // the Button base never repaints the background under UserPaint: without this, old text and the
        // anti-aliased edge pixels of earlier frames ghost through the translucent fill (garbled labels, stray marks)
        g.Clear(Parent?.BackColor ?? Theme.Background);
        if(Width<4||Height<4)return;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using var p=Theme.Round(new RectangleF(1,1,Width-3,Height-3),2*UiMetrics.Scale(this));
        using var b=new SolidBrush(Active?Color.FromArgb(65,Accent):_hover?Theme.Raised:Color.FromArgb(26,30,40));
        using var pen=new Pen(Focused||_hover||Active?Accent:Theme.Edge,Focused?2:1);
        g.FillPath(b,p);g.DrawPath(pen,p);
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,Active||_hover?Theme.Text:ForeColor,
            TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
    }
}

