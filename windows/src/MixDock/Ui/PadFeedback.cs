using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MixDock.Platform;

namespace MixDock.Ui;

internal static class ConsoleMotion
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);

    private static long _refreshAt;
    private static bool _enabled;
    internal static bool Enabled
    {
        get
        {
            long now = Environment.TickCount64;
            if (now >= _refreshAt)
            {
                _enabled = SystemParametersInfo(0x1042, 0, out int value, 0) && value != 0;
                _refreshAt = now + 1000;
            }
            return _enabled;
        }
    }

    internal static float Ease(long elapsed)
    {
        float remaining = 1 - Math.Clamp(elapsed / 160f, 0, 1);
        return 1 - remaining * remaining * remaining;
    }
}

internal sealed class StickReadout : Control
{
    private readonly string _side;
    private readonly Color _accent;
    private PointF _puck;
    private string _caption = "", _detail = "";
    private float _visibility;
    private long _lastMoved;

    internal StickReadout(string side, Color accent)
    {
        _side = side;
        _accent = accent;
        DoubleBuffered = ResizeRedraw = true;
        BackColor = Theme.Surface;
        AccessibleRole = AccessibleRole.Indicator;
    }

    internal void UpdateState(double x, double y, bool active, string caption, string detail)
    {
        long now = Environment.TickCount64;
        PointF puck = active ? new((float)Math.Clamp(x, -1, 1), (float)Math.Clamp(y, -1, 1)) : _puck;
        if (active) _lastMoved = now;
        float visibility = active ? 1 : ConsoleMotion.Enabled ? 1 - ConsoleMotion.Ease(now - _lastMoved) : 0;
        // Retain the final position and legend while they fade together.
        if (!active && visibility > 0) { caption = _caption; detail = _detail; }
        if (visibility == _visibility && puck == _puck && _caption == caption && _detail == detail) return;
        _visibility = visibility;
        _puck = puck;
        _caption = caption;
        _detail = detail;
        AccessibleName = $"{_side} stick, {caption}, {detail}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() =>
    {
        var g = e.Graphics;
        float s = UiMetrics.Scale(this);
        float side = Math.Min(Height - 4 * s, 30 * s);
        if (side < 4) return;
        var plot = new RectangleF(2*s, (Height-side)/2, side, side);
        using var edge = new Pen(Theme.Edge, Math.Max(1,s));
        g.DrawRectangle(edge, plot.X, plot.Y, side, side);
        float cx = plot.X + side/2, cy = plot.Y + side/2;
        g.DrawLine(edge, cx, plot.Top, cx, plot.Bottom);
        g.DrawLine(edge, plot.Left, cy, plot.Right, cy);
        if (_visibility > 0)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = 3.3f*s, travel = side/2-r;
            using var b = new SolidBrush(Color.FromArgb((int)(255*_visibility), _accent));
            g.FillEllipse(b, cx + _puck.X*travel-r, cy - _puck.Y*travel-r, 2*r, 2*r);
        }
        int textX = (int)(plot.Right + 5*s), textWidth = Width-textX;
        if (textWidth < 1) return;
        Theme.TextAt(g, _side + "  " + _caption, new(textX, 0, textWidth, Height/2), 8, Theme.Muted, true);
        Theme.TextAt(g, _detail, new(textX, Height/2, textWidth, Height-Height/2), 9, _visibility > .15f ? _accent : Theme.Text, true);
    }, nameof(StickReadout), this);
}

internal sealed class PadGlyphBar : Control
{
    private readonly (string Glyph, PadButton Button)[] _inputs;
    private bool _useViewShortcut;
    private readonly string[] _captions;
    private readonly string?[] _actions;
    private PadButton _held;
    private Color _accent = Theme.Cyan;

    internal PadGlyphBar(params (string, PadButton)[] inputs)
    {
        _inputs = inputs;
        _captions = new string[inputs.Length];
        _actions = new string?[inputs.Length];
        Array.Fill(_captions, "—");
        DoubleBuffered = ResizeRedraw = true;
        BackColor = Theme.Surface;
    }

    internal void UpdateState(PadMapper mapper, PadFrame frame, bool shift, Color accent, bool useViewShortcut)
    {
        bool dirty = frame.Buttons != _held || accent != _accent || useViewShortcut != _useViewShortcut;
        _useViewShortcut = useViewShortcut;
        _held = frame.Buttons;
        _accent = accent;
        for (int i = 0; i < _inputs.Length; i++)
        {
            string action = _inputs[i].Button == PadButton.Start ? "Hold to map" : mapper.ActionFor(_inputs[i].Button, shift) ?? "";
            if (_actions[i] == action) continue;
            _actions[i] = action;
            _captions[i] = _inputs[i].Button == PadButton.Start ? action : Compact(action);
            dirty = true;
        }
        if (!dirty) return;
        AccessibleName = string.Join(", ", _inputs.Select((p,i) => (_useViewShortcut && p.Button == PadButton.Start ? "View" : p.Glyph) + " " + _captions[i]));
        Invalidate();
    }

    private static string Compact(string action)
    {
        int dot = action.IndexOf('.');
        string scope = dot == 1 ? action[..1] + " " : "";
        string id = dot < 0 ? action : action[(dot+1)..];
        string? compact = id switch
        {
            "QuantCycle" => "Quantize", "BankMode" => "Bank mode", "FxCycle" => "Next FX",
            "FxFreeze" => "Freeze FX", "CrossfaderReset" => "Center fader", "FocusToggle" => "Swap deck",
            "FilterFineReset" => "Center filter", "BeatJumpBack" => "Jump back", "BeatJumpFwd" => "Jump ahead",
            "TimeBend" => "Brake", "BeatRoll" => "Beat roll", "PadGrid" => "Pad grid",
            _ when id.StartsWith("PadCapture:", StringComparison.Ordinal) => "Alt pad " + id[11..],
            _ when id.StartsWith("Pad:", StringComparison.Ordinal) => "Pad " + id[4..],
            _ => null
        };
        if (compact != null) return scope + compact;
        string caption = PadOverlay.Describe(action);
        int extra = caption.IndexOf(" (", StringComparison.Ordinal);
        return extra >= 0 ? caption[..extra] : caption;
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() =>
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = UiMetrics.Scale(this);
        for (int i = 0; i < _inputs.Length; i++)
        {
            var (glyph, button) = _inputs[i];
            if (_useViewShortcut && button == PadButton.Start) (glyph,button) = ("View",PadButton.Back);
            int left = i*Width/_inputs.Length;
            int right = (i+1)*Width/_inputs.Length;
            int width = (int)((glyph.Length > 2 ? 35 : glyph.Length == 2 ? 25 : 19)*s);
            var r = new Rectangle(left+1, (int)(2*s), width, Height-(int)(4*s));
            if (r.Height < 2) continue;
            bool down = (_held & button) != 0;
            using var shape = Theme.Round(r, glyph.Length == 1 ? r.Height / 2f : 3*s);
            using var fill = new SolidBrush(down ? _accent : Theme.Raised);
            using var stroke = new Pen(down ? _accent : Theme.Muted, Math.Max(1,s));
            g.FillPath(fill, shape); g.DrawPath(stroke, shape);
            Theme.TextAt(g, glyph, r, 8, down ? Theme.Background : Theme.Text, true, true);
            int x = r.Right+(int)(5*s);
            Theme.TextAt(g, _captions[i], new(x,0,Math.Max(0,right-x),Height), 8, Theme.Text);
        }
    }, nameof(PadGlyphBar), this);
}
