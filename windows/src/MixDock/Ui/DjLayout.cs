using System.Drawing.Drawing2D;

namespace MixDock.Ui;

// Fit the approved 1366 x 768 console to the client area, independently of desktop DPI.
// Controls use the same scale for painting, hit testing and typography.
public sealed class DjLayout : Panel, IConsoleScale
{
    private readonly List<(Control Control, RectangleF Bounds, float Font)> _items = new();
    private float _fontScale = -1;
    private readonly Dictionary<Control,Font> _ownedFonts=new();
    public float ScaleFactor => Math.Min(Width / 1366f, Height / 768f);
    public DjLayout() { DoubleBuffered = true; BackColor = Theme.Background; Dock = DockStyle.Fill; }
    public void Place(Control c, float x, float y, float w, float h, float font = 8)
    {
        c.Dock = DockStyle.None; c.Anchor = AnchorStyles.Top | AnchorStyles.Left; c.Margin = Padding.Empty;
        c.AutoSize = false;
        Controls.Add(c); _items.Add((c, new(x, y, w, h), font));
    }
    /// <summary>Logical (1366 x 768) bounds a control was placed at.</summary>
    public RectangleF BoundsOf(Control c) { foreach (var item in _items) if (item.Control == c) return item.Bounds; return RectangleF.Empty; }
    /// <summary>Controls whose logical bounds lie inside <paramref name="area"/> (used to hide what an expanded panel covers).</summary>
    public IEnumerable<Control> ControlsWithin(RectangleF area, Control except)
    {
        foreach (var item in _items) if (item.Control != except && area.Contains(item.Bounds)) yield return item.Control;
    }
    /// <summary>Re-place a control on the canvas (workspace modes); fonts keep their size.</summary>
    public void Move(Control c, float x, float y, float w, float h)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Control == c) { _items[i] = (c, new(x, y, w, h), _items[i].Font); break; }
        PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (_items == null) return;
        float s = Math.Max(.1f, ScaleFactor), ox = (Width - 1366 * s) / 2, oy = (Height - 768 * s) / 2;
        foreach (var (c, r, f) in _items)
        {
            // Round both edges, not the size: controls that touch on the canvas never overlap by a pixel after scaling.
            int left = (int)Math.Round(ox + r.X * s), top = (int)Math.Round(oy + r.Y * s);
            int right = (int)Math.Round(ox + r.Right * s), bottom = (int)Math.Round(oy + r.Bottom * s);
            // An owner-drawn combo box sizes itself from ItemHeight; derive it from the slot so it cannot spill below.
            if (c is ComboBox { DrawMode: DrawMode.OwnerDrawFixed } combo) combo.ItemHeight = Math.Max(8, bottom - top - 6);
            c.SetBounds(left, top, right - left, bottom - top);
            if(c is WaveformStrip){c.Padding=new Padding((int)(8*s),(int)(4*s),(int)(8*s),(int)(4*s));c.PerformLayout();}
            // Pixel fonts avoid a second DPI multiplier after fitting the console.
            if (!_ownedFonts.TryGetValue(c,out var font)||Math.Abs(_fontScale-s)>.001)
            {
                var next=Theme.PixelFont(f,s,c is StudioButton,c.Tag is "numeric");
                c.Font=next;_ownedFonts[c]=next;font?.Dispose();
            }

        }
        _fontScale = s;
    }
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)foreach(var font in _ownedFonts.Values)font.Dispose();}
}

public sealed class JogView : Control
{
    public Func<(double Bpm, double Phase)>? Read;
    /// <summary>Detected key, or null when detection is not confident (never invented).</summary>
    public Func<string?>? KeyText;
    public Color Accent = Theme.Cyan;
    public JogView() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface; AccessibleName = "Heard tempo and beat phase"; }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "JogView", this);
    private void PaintCore(PaintEventArgs e)
    {
        var (bpm, phase) = Read?.Invoke() ?? (0, double.NaN);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float side = Math.Min(Width, Height) - 8, x = (Width - side) / 2, y = (Height - side) / 2;
        if (side < 16) return;   // collapsed/hidden: a zero-size font or ellipse throws in GDI+
        using var fill = new SolidBrush(Theme.Idle); g.FillEllipse(fill, x, y, side, side);
        using var track = new Pen(Theme.Edge, Math.Max(1, side / 40)); g.DrawEllipse(track, x, y, side, side);
        using var pen = new Pen(Accent, Math.Max(1, side / 40));
        // the ring fills through the bar: a beat-phase clock that reads at a glance (only when tempo is measured)
        if (double.IsFinite(phase) && bpm > 0) Theme.Arc(g, pen, new RectangleF(x, y, side, side), -90, (float)(phase * 360));
        else g.DrawEllipse(pen, x, y, side, side);
        using var font = new Font(Theme.NumericFamily, side * .21f, FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, bpm > 0 ? bpm.ToString("0.0") : "—", font, new Rectangle(0, Height / 2 - (int)(side * .2), Width, (int)(side * .35)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var small = new Font("Segoe UI", Math.Max(9f * UiMetrics.Scale(this), side * .085f), FontStyle.Bold, GraphicsUnit.Pixel);
        string key = KeyText?.Invoke() ?? "";
        TextRenderer.DrawText(g, key.Length > 0 ? $"BPM · {key}" : "BPM", small, new Rectangle(0, Height / 2 + (int)(side * .13), Width, (int)(side * .2)), Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}

public sealed class XyPad : Control
{
    public event Action<double,double,bool>? Changed;
    public Color Accent = Theme.Cyan;
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
        var g = e.Graphics; g.Clear(Theme.Idle);
        using var grid = new Pen(Theme.Edge); g.DrawLine(grid, Width/2, 0, Width/2, Height); g.DrawLine(grid, 0, Height/2, Width, Height/2);
        using var pen = new Pen(_held?Accent:Theme.Edge); g.DrawRectangle(pen,0,0,Width-1,Height-1);
        if(_held)
        {
            float px=(float)(_x*Width), py=(float)((1-_y)*Height), r=Math.Max(3,Height*.18f);
            g.DrawLine(pen,px,0,px,Height);g.DrawLine(pen,0,py,Width,py);
            g.SmoothingMode=SmoothingMode.AntiAlias; using var puck=new SolidBrush(Accent); Theme.Ellipse(g,puck,new RectangleF(px-r,py-r,2*r,2*r));
        }
        TextRenderer.DrawText(g,_held?"RELEASE = DRY":"XY · DRAG TO APPLY",Font,ClientRectangle,_held?Theme.Text:Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
}

public sealed class SignalBar : Control
{
    public Func<float>? Read;
    private readonly MeterEnvelope _meter = new();
    public SignalBar(){DoubleBuffered=true;BackColor=Theme.Surface;AccessibleName="Master signal meter";}
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "SignalBar", this);
    private void PaintCore(PaintEventArgs e)
    {
        if (Width < 30 || Height < 3) return;
        _meter.Update(Read?.Invoke() ?? 0, Environment.TickCount64);
        float value = _meter.Level;
        for(int i=0;i<30;i++){float f=(i+1)/30f;using var b=new SolidBrush(f<=value?(f>.95?Theme.Red:f>.8?Theme.Amber:Color.FromArgb(78,199,126)):_meter.IsHeldSegment(i,30)?Theme.Text:Theme.Raised);e.Graphics.FillRectangle(b,i*Width/30f,Height*.3f,Math.Max(1,Width/30f-1),Height*.4f);}
    }
}

/// <summary>Header status lamps: one word plus a coloured dot per system, read from real state every UI tick.</summary>
public sealed class StatusChips : Control
{
    public readonly record struct Chip(string Text, Color Color);
    public Func<Chip[]>? Read;
    private Chip[] _last = [];
    public StatusChips() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Background; AccessibleRole = AccessibleRole.Indicator; AccessibleName = "Audio, controller and MIDI status"; }
    public void Tick()
    {
        var next = Read?.Invoke() ?? [];
        if (next.SequenceEqual(_last)) return;
        _last = next; AccessibleDescription = string.Join(", ", next.Select(c => c.Text)); Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "StatusChips", this);
    private void PaintCore(PaintEventArgs e)
    {
        if (_last.Length == 0 || Width < 20) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = UiMetrics.Scale(this), gap = 4 * s;
        // widths follow the words, and spare room is shared evenly, so a longer state never clips
        using var font = Theme.PixelFont(8, s, true);
        var need = _last.Select(c => TextRenderer.MeasureText(c.Text, font, Size.Empty, TextFormatFlags.NoPadding).Width + 24 * s).ToArray();
        float spare = Math.Max(0, (Width - gap * (_last.Length - 1) - need.Sum()) / _last.Length), x = 0;
        for (int i = 0; i < _last.Length; i++)
        {
            float w = need[i] + spare; var r = new RectangleF(x, 1, w, Height - 2); x += w + gap;
            using var path = Theme.Round(r, 2.5f * s);
            using var bg = new SolidBrush(Theme.Idle); g.FillPath(bg, path);
            using var edge = new Pen(Color.FromArgb(110, _last[i].Color)); g.DrawPath(edge, path);
            float d = 7 * s; using var dot = new SolidBrush(_last[i].Color);
            Theme.Ellipse(g, dot, new RectangleF(r.X + 7 * s, r.Y + (r.Height - d) / 2, d, d));
            Theme.TextAt(g, _last[i].Text, Rectangle.Round(new RectangleF(r.X + 18 * s, r.Y, r.Width - 21 * s, r.Height)), 8, Theme.Text, true);
        }
    }
}
