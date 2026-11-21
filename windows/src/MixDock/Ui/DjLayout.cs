using System.Drawing.Drawing2D;

namespace MixDock.Ui;

// Fit the approved 1366 x 768 console to the client area, independently of desktop DPI.
// Controls use the same scale for painting, hit testing and typography.
public sealed class DjLayout : Panel, IConsoleScale
{
    private readonly List<(Control Control, RectangleF Bounds, float Font)> _items = new();
    private readonly Dictionary<Control,Font> _ownedFonts=new();
    public float ScaleFactor => Math.Min(Width / 1366f, Height / 768f);
    public DjLayout() { DoubleBuffered = true; BackColor = Theme.Background; Dock = DockStyle.Fill; }
    public void Place(Control c, float x, float y, float w, float h, float font = 8)
    {
        c.Dock = DockStyle.None; c.Anchor = AnchorStyles.Top | AnchorStyles.Left; c.Margin = Padding.Empty;
        c.AutoSize = false;
        Controls.Add(c); _items.Add((c, new(x, y, w, h), font));
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (_items == null) return;
        float s = Math.Max(.1f, ScaleFactor), ox = (Width - 1366 * s) / 2, oy = (Height - 768 * s) / 2;
        foreach (var (c, r, f) in _items)
        {
            c.SetBounds((int)Math.Round(ox + r.X * s), (int)Math.Round(oy + r.Y * s),
                (int)Math.Round(r.Width * s), (int)Math.Round(r.Height * s));
            if(c is WaveformStrip){c.Padding=new Padding((int)(8*s),(int)(4*s),(int)(8*s),(int)(4*s));c.PerformLayout();}
            if(c is AppPicker picker)picker.ItemHeight=(int)(26*s);
            // Pixel fonts avoid a second DPI multiplier after fitting the console.
            float pixels = Math.Max(7, f * 96 / 72 * s);
            if (!_ownedFonts.TryGetValue(c,out var font)||Math.Abs(font.Size-pixels)>.1)
            {
                var next=new Font("Segoe UI",pixels,c is StudioButton?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
                c.Font=next;_ownedFonts[c]=next;font?.Dispose();
            }

        }
    }
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)foreach(var font in _ownedFonts.Values)font.Dispose();}
}

public sealed class JogView : Control
{
    public Func<(double Bpm, double Phase)>? Read;
    public Color Accent = Theme.Cyan;
    public JogView() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface; AccessibleName = "Heard tempo and beat phase"; }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "JogView", this);
    private void PaintCore(PaintEventArgs e)
    {
        var (bpm, phase) = Read?.Invoke() ?? (0, double.NaN);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float side = Math.Min(Width, Height) - 8, x = (Width - side) / 2, y = (Height - side) / 2;
        if (side < 16) return;   // collapsed/hidden: a zero-size font or ellipse throws in GDI+
        using var fill = new SolidBrush(Color.FromArgb(29,33,36)); g.FillEllipse(fill, x, y, side, side);
        using var pen = new Pen(Accent, Math.Max(1, side / 70)); g.DrawEllipse(pen, x, y, side, side);
        if (double.IsFinite(phase))
        {
            double a = phase * Math.PI * 2 - Math.PI / 2;
            using var marker = new Pen(Theme.Text, 3);
            g.DrawLine(marker, Width / 2f + (float)Math.Cos(a) * side * .4f, Height / 2f + (float)Math.Sin(a) * side * .4f,
                Width / 2f + (float)Math.Cos(a) * side * .47f, Height / 2f + (float)Math.Sin(a) * side * .47f);
        }
        using var font = new Font("Segoe UI", side * .21f, FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, bpm > 0 ? bpm.ToString("0.0") : "—", font, new Rectangle(0, Height / 2 - (int)(side * .2), Width, (int)(side * .35)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var small = new Font("Segoe UI", side * .075f, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, "HEARD BPM", small, new Rectangle(0, Height / 2 + (int)(side * .13), Width, (int)(side * .17)), Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}

public sealed class XyPad : Control
{
    public event Action<double,double,bool>? Changed;
    private double _x = .5, _y = .5; private bool _held;
    public XyPad() { DoubleBuffered = true; BackColor = Theme.Raised; Cursor = Cursors.Cross; AccessibleName = "Effect XY: drag to apply, release for dry"; TabStop = true; }
    private void MoveTo(MouseEventArgs e) { _x = Math.Clamp(e.X / (double)Math.Max(1, Width),0,1); _y = 1-Math.Clamp(e.Y/(double)Math.Max(1,Height),0,1); Changed?.Invoke(_x,_y,_held); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if(e.Button!=MouseButtons.Left)return; Focus(); Capture = _held = true; MoveTo(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if(_held)MoveTo(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _held=false; Capture=false; MoveTo(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { if(!Capture && _held){_held=false;Changed?.Invoke(_x,_y,false);} base.OnMouseCaptureChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode==Keys.Escape){_held=false;Capture=false;Changed?.Invoke(_x,_y,false);Invalidate();} base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "XyPad", this);
    private void PaintCore(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        using var pen = new Pen(_held?Theme.Cyan:Theme.Edge); e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
        if(_held){e.Graphics.DrawLine(pen,(int)(_x*Width),0,(int)(_x*Width),Height);e.Graphics.DrawLine(pen,0,(int)((1-_y)*Height),Width,(int)((1-_y)*Height));}
        TextRenderer.DrawText(e.Graphics,_held?"RELEASE → DRY":"XY / DRAG TO APPLY",Font,ClientRectangle,Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
}

public sealed class SignalBar : Control
{
    public Func<float>? Read;
    public SignalBar(){DoubleBuffered=true;BackColor=Theme.Surface;AccessibleName="Master signal meter";}
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "SignalBar", this);
    private void PaintCore(PaintEventArgs e)
    {
        if (Width < 30 || Height < 3) return;
        float peak=Read?.Invoke()??0; if (!float.IsFinite(peak)) peak = 0;float value=peak<=.001?0:Math.Clamp((20*MathF.Log10(peak)+60)/60,0,1);
        for(int i=0;i<30;i++){float f=(i+1)/30f;using var b=new SolidBrush(f<=value?(f>.95?Theme.Red:f>.8?Theme.Amber:Color.FromArgb(78,199,126)):Theme.Raised);e.Graphics.FillRectangle(b,i*Width/30f,Height*.3f,Math.Max(1,Width/30f-1),Height*.4f);}
    }
}
