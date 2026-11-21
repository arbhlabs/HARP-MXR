using System.Drawing.Drawing2D;
using MixDock.Platform;

namespace MixDock.Ui;

/// <summary>Everything the pad grid draws. Filled by the main form; indexes are pad keys (bank * 4 + pad).</summary>
public sealed class PadGridState
{
    public int Bank, Focus;
    public BankMode[] Modes = { BankMode.Sample, BankMode.OneShot, BankMode.Chord, BankMode.Drum };
    public string[] Labels = new string[16];
    public bool[] Filled = new bool[16], Held = new bool[16], Latched = new bool[16], Playing = new bool[16], Pending = new bool[16], DawLit = new bool[16];
    public long[] HitAt = new long[16];
    public int[] Velocity = new int[16];
    public string KeyText = "", ChordText = "", QuantText = "", MidiText = "";
    public bool Layer, Shift, Connected;
    public PadGridState() { Array.Fill(Labels, ""); }
}

/// <summary>
/// 4x4 pad grid: banks are rows (D-pad Up/Right/Down/Left = 1-4), A/B/X/Y are the columns. Colours are fixed by mode (Sample cyan, One-shot
/// amber, Chord green, Drum white/blue); the current bank row is outlined. A pad flashes on hit (brightness = velocity), stays lit while
/// held or latched and blinks while its launch waits for the grid. Used in the centre panel (compact) and as the floating overlay.
/// </summary>
public sealed class PadGridView : Control
{
    public static readonly Color SampleColor = Theme.Cyan, OneShotColor = Color.FromArgb(242, 191, 100), ChordColor = Color.FromArgb(125, 226, 190), DrumColor = Color.FromArgb(200, 222, 255);
    public static Color ModeColor(BankMode m) => m switch { BankMode.Sample => SampleColor, BankMode.OneShot => OneShotColor, BankMode.Chord => ChordColor, _ => DrumColor };
    private static readonly string[] Columns = { "A", "B", "X", "Y" };
    private PadGridState _s = new();
    private readonly bool _compact;
    private string _signature = "";
    public static Size FloatDesignSize => new(560, 392);

    public PadGridView(bool compact)
    {
        _compact = compact;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface; AccessibleName = compact ? "Pad grid" : "Pad grid overlay"; if (!compact) Visible = false;
    }

    /// <summary>Takes the new state; repaints when anything visible changed or an animation (flash, blink) is running.</summary>
    public void SetState(PadGridState s)
    {
        _s = s;
        long now = Environment.TickCount64;
        bool animating = false;
        for (int i = 0; i < 16; i++)
            if (s.Pending[i] || now - s.HitAt[i] < 420) { animating = true; break; }
        string sig = $"{s.Bank}|{s.Focus}|{s.Layer}|{s.Shift}|{s.Connected}|{s.KeyText}|{s.ChordText}|{s.QuantText}|{s.MidiText}|{string.Join(",", s.Modes)}|{string.Join(",", s.Labels)}|" +
            string.Concat(Enumerable.Range(0, 16).Select(i => (s.Filled[i] ? 'f' : '-') + "" + (s.Held[i] ? 'h' : '-') + (s.Latched[i] ? 'l' : '-') + (s.Playing[i] ? 'p' : '-') + (s.DawLit[i] ? 'd' : '-')));
        if (animating || sig != _signature) { _signature = sig; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "PadGridView", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = UiMetrics.Scale(this); int D(float n) => (int)Math.Round(n * k);
        if (Width < D(120) || Height < D(60)) return;
        var s = _s; long now = Environment.TickCount64;
        Color accent = s.Focus == 0 ? Theme.Cyan : Theme.Amber;
        if (!_compact)
        {
            using var border = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 16 * k);
            using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, border);
            using var p = new Pen(Theme.Edge); g.DrawPath(p, border);
        }
        int pad = D(_compact ? 6 : 16);
        float head = _compact ? D(15) : D(26), foot = _compact ? D(44) : D(56);
        var top = new Rectangle(pad, pad - (_compact ? D(2) : 0), Width - pad * 2, (int)head);
        float tf = _compact ? 7f : 10f;
        Theme.TextAt(g, _compact ? "PAD GRID" : "PAD GRID  ·  Shift+Menu hides", new Rectangle(top.X, top.Y, top.Width / 2, top.Height), tf, Theme.Muted, true);
        string right = $"{s.QuantText}   {(s.Layer ? "LAYER" : "")}".Trim();
        using (var f = Theme.Font(tf, s.Layer))
            TextRenderer.DrawText(g, right, f, new Rectangle(top.X + top.Width / 2, top.Y, top.Width / 2, top.Height), s.Layer ? Theme.Text : Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        float labelW = _compact ? D(22) : D(86);
        var gridArea = new RectangleF(pad + labelW, top.Bottom + D(2), Width - pad * 2 - labelW, Height - top.Bottom - foot - pad - D(2));
        if (!Theme.Ok(gridArea)) return;
        float gap = D(_compact ? 3 : 8), cw = (gridArea.Width - gap * 3) / 4f, ch = (gridArea.Height - gap * 3) / 4f;
        if (cw < 4 || ch < 4) return;
        // column headers live inside the first row's label strip in compact mode; a dedicated strip in the floating view
        for (int b = 0; b < 4; b++)
        {
            var mode = s.Modes[b]; Color mc = ModeColor(mode);
            float y = gridArea.Y + b * (ch + gap);
            bool cur = b == s.Bank;
            var lab = new Rectangle(pad, (int)y, (int)labelW - D(4), (int)ch);
            Theme.TextAt(g, _compact ? $"{b + 1}" : $"BANK {b + 1}", _compact ? new Rectangle(lab.X, lab.Y, lab.Width, lab.Height) : new Rectangle(lab.X, lab.Y, lab.Width, lab.Height / 2 + D(3)), _compact ? 8 : 10, cur ? mc : Theme.Muted, true);
            if (!_compact) Theme.TextAt(g, PadBanks.ModeName(mode), new Rectangle(lab.X, lab.Y + lab.Height / 2 - D(1), lab.Width, lab.Height / 2), 7.5f, cur ? mc : Theme.Muted, cur);
            for (int p = 0; p < 4; p++)
            {
                int key = b * 4 + p;
                var r = new RectangleF(gridArea.X + p * (cw + gap), y, cw, ch);
                if (!Theme.Ok(r)) continue;
                long since = now - s.HitAt[key];
                double flash = since is >= 0 and < 420 ? (1 - since / 420.0) * (.35 + .65 * Math.Clamp(s.Velocity[key] / 127.0, 0, 1)) : 0;
                bool on = s.Held[key] || s.Latched[key] || s.Playing[key];
                bool blink = s.Pending[key] && (now / 110) % 2 == 0;
                int alpha = on ? 120 : 28;
                alpha = (int)Math.Min(255, alpha + flash * 200 + (blink ? 110 : 0));
                using var path = Theme.Round(r, (_compact ? 4 : 9) * k);
                using (var fill = new SolidBrush(Color.FromArgb(alpha, mc))) g.FillPath(fill, path);
                using (var edge = new Pen(cur ? mc : s.Filled[key] || mode is BankMode.Chord or BankMode.Drum ? Color.FromArgb(110, mc) : Theme.Edge, cur ? 1.8f * k : 1f)) g.DrawPath(edge, path);
                if (s.Latched[key]) using (var ring = new Pen(Theme.Text, 1.6f * k)) { var rr = RectangleF.Inflate(r, -2.5f * k, -2.5f * k); if (Theme.Ok(rr)) { using var rp = Theme.Round(rr, 3 * k); g.DrawPath(ring, rp); } }
                if (s.DawLit[key]) { using var dot = new SolidBrush(Theme.Text); Theme.Ellipse(g, dot, new RectangleF(r.Right - 7 * k, r.Y + 3 * k, 4 * k, 4 * k)); }
                string label = s.Labels[key];
                if (label.Length == 0) label = $"· {Columns[p]} ·";
                bool bright = alpha > 140;
                Theme.TextAt(g, label, Rectangle.Round(r), _compact ? 7.5f : (label.Length > 4 ? 10 : 13), bright ? Theme.Background : (cur ? Theme.Text : Theme.Muted), true, true);
            }
        }
        // column letters under the grid keep the controller mapping obvious
        float fy = gridArea.Bottom + D(1);
        for (int p = 0; p < 4; p++)
            Theme.TextAt(g, Columns[p], new Rectangle((int)(gridArea.X + p * (cw + gap)), (int)fy, (int)cw, D(_compact ? 12 : 16)), _compact ? 6.5f : 8, Theme.Muted, true, true);

        // footer: key, chord, MIDI
        var foot1 = new Rectangle(pad, (int)(fy + D(_compact ? 12 : 16)), Width - pad * 2, D(_compact ? 13 : 20));
        Theme.TextAt(g, string.IsNullOrEmpty(s.ChordText) ? s.KeyText : $"{s.KeyText}   ·   {s.ChordText}", foot1, _compact ? 7f : 10f, s.Modes[s.Bank] == BankMode.Chord ? ChordColor : Theme.Muted, s.Modes[s.Bank] == BankMode.Chord);
        if (!_compact)
            Theme.TextAt(g, s.MidiText, new Rectangle(pad, foot1.Bottom, Width - pad * 2, D(18)), 8.5f, s.MidiText.Contains("no virtual port") ? Theme.Muted : Color.FromArgb(131, 181, 185));
        else if (Height > D(150))
            Theme.TextAt(g, s.MidiText, new Rectangle(pad, foot1.Bottom - D(1), Width - pad * 2, D(12)), 6.5f, Theme.Muted);
    }
}
