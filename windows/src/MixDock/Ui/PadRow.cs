using System.Drawing.Drawing2D;

namespace MixDock.Ui;

/// <summary>Four sample pads of one deck (A/B/X/Y) with a stored-beat dot and a level LED strip. Click = play, Shift+click = capture the last beat.</summary>
public sealed class PadRow : Control
{
    private static readonly string[] Labels = { "A", "B", "X", "Y" };
    private readonly bool[] _has = new bool[4];
    private readonly float[] _level = new float[4];
    private int _playing = -1;
    private Color _accent = Theme.Cyan;
    private long _captureFlashAt; private int _capturePad = -1;
    public event Action<int, bool>? PadPressed;

    public PadRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface; Cursor = Cursors.Hand; AccessibleName = "Sample pads";
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Accent { get => _accent; set { _accent = value; Invalidate(); } }

    public void Set(ReadOnlySpan<int> lengths, float[] levels, int playing)
    {
        bool changed = playing != _playing;
        for (int i = 0; i < 4; i++)
        {
            bool has = lengths[i] > 0;
            float lv = float.IsFinite(levels[i]) ? Math.Clamp(levels[i], 0, 1) : 0;
            if (has != _has[i] || Math.Abs(lv - _level[i]) > .02f || (lv == 0) != (_level[i] == 0)) changed = true;
            _has[i] = has; _level[i] = lv;
        }
        _playing = playing;
        if (changed || Environment.TickCount64 - _captureFlashAt < 500) Invalidate();
    }
    public void FlashCapture(int pad) { _capturePad = pad; _captureFlashAt = Environment.TickCount64; Invalidate(); }

    private RectangleF Cell(int i)
    {
        float gap = 6 * DeviceDpi / 96f, w = (Width - gap * 3) / 4f;
        return new RectangleF(i * (w + gap), 0, w, Height - 1);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            for (int i = 0; i < 4; i++) if (Cell(i).Contains(e.Location)) { PadPressed?.Invoke(i, (ModifierKeys & Keys.Shift) != 0); break; }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "PadRow", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        if (Width < 80 || Height < 16) return;
        for (int i = 0; i < 4; i++)
        {
            var r = Cell(i); if (!Theme.Ok(r)) continue;
            bool on = _playing == i, flash = _capturePad == i && Environment.TickCount64 - _captureFlashAt < 450;
            using var path = Theme.Round(r, 6 * s);
            using (var b = new SolidBrush(on ? Color.FromArgb(70, _accent) : Color.FromArgb(26, 30, 40))) g.FillPath(b, path);
            using (var p = new Pen(on || flash ? _accent : _has[i] ? Color.FromArgb(120, _accent) : Theme.Edge, on || flash ? 2 : 1)) g.DrawPath(p, path);
            Theme.TextAt(g, Labels[i], new Rectangle((int)r.X + (int)(7 * s), (int)r.Y, (int)(20 * s), (int)r.Height), 11, on ? Theme.Text : _has[i] ? _accent : Theme.Muted, true);
            // level LEDs (5 segments) on the right, stored-beat dot above them
            int segs = 5, lit = (int)Math.Ceiling(_level[i] * segs - .01f);
            float lx = r.Right - 12 * s, segH = (r.Height - 10 * s) / segs;
            for (int k = 0; k < segs; k++)
            {
                var seg = new RectangleF(lx, r.Bottom - 5 * s - (k + 1) * segH + 1.5f * s, 6 * s, Math.Max(1, segH - 3 * s));
                if (!Theme.Ok(seg)) continue;
                Color c = k >= segs - 1 ? Theme.Red : k >= segs - 2 ? Color.FromArgb(242, 191, 100) : _accent;
                using var b = new SolidBrush(k < lit && _playing == i ? c : Color.FromArgb(39, 45, 57));
                g.FillRectangle(b, seg);
            }
            if (_has[i]) { using var b = new SolidBrush(_accent); Theme.Ellipse(g, b, new RectangleF(r.X + r.Width * .5f - 3 * s, r.Y + 6 * s, 6 * s, 6 * s)); }
        }
    }
}
