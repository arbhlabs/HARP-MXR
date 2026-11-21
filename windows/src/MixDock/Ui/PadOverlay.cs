using System.Drawing.Drawing2D;
using MixDock.Platform;
using MixDock.Settings;

namespace MixDock.Ui;

/// <summary>
/// Controller cheatsheet: a drawn Xbox pad with every input labelled by its CURRENT mapping from config
/// (shift-layer labels while shift is held). Inputs light up live. Toggle with the PAD button or Shift+F1.
/// </summary>
public sealed class PadOverlay : Control
{
    private readonly AppConfig _config;
    private PadFrame _frame;
    private bool _shift, _connected;
    private int _focus;
    public PadOverlay(AppConfig config)
    {
        _config = config;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface; AccessibleName = "Controller mapping overlay"; Visible = false;
    }
    public static Size DesignSize => new(820, 440);

    public void SetState(PadFrame frame, bool shift, bool connected, int focus)
    {
        if (frame == _frame && shift == _shift && connected == _connected && focus == _focus) return;
        _frame = frame; _shift = shift; _connected = connected; _focus = focus; Invalidate();
    }

    /// <summary>Short human label for an action string.</summary>
    public static string Describe(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return "—";
        string deck = "";
        string a = action;
        if (a.StartsWith("Focus.")) a = a[6..];
        else if (a.Length > 2 && a[1] == '.' && (a[0] == 'A' || a[0] == 'B')) { deck = $"{a[0]} "; a = a[2..]; }
        string text = a switch
        {
            "Filter" => "FILTER sweep (spring)", "FilterFine" => "FILTER fine creep", "FilterFineReset" => "FILTER centre",
            "FilterReset" => "FILTER reset", "EqReset" => "EQ + kills reset", "EchoSend" => "ECHO send ↑ / wash ↓",
            "EchoThrow" => "ECHO throw", "Riser" => "NOISE riser", "Cue" => "CUE (headphones)", "Sync" => "SYNC",
            "LoopDouble" => "LOOP size ×2", "LoopHalve" => "LOOP size ÷2", "BeatJumpBack" => "BEATJUMP −", "BeatJumpFwd" => "BEATJUMP +",
            "Loop" => "LOOP on/off", "TapBpm" => "TAP BPM", "AutoMix" => "AUTO MIX on/off", "LowKill" => "LOW kill", "MidKill" => "MID kill",
            "HighKill" => "HIGH kill", "EchoFreeze" => "ECHO freeze", "PlayPause" => "PLAY / PAUSE (song)", "CrossfaderReset" => "X-FADER centre",
            "BrowsePrev" => "BROWSE ◀", "BrowseNext" => "BROWSE ▶", "Load" => "LOAD selected", "Mute" => "DECK mute", "MasterMute" => "KILL / RESTORE audio",
            "FocusA" => "FOCUS deck A", "FocusB" => "FOCUS deck B", "FocusToggle" => "FOCUS swap A↔B",
            "CrossfaderA" => "X-FADER cut to A", "CrossfaderB" => "X-FADER cut to B", "Crossfader+" => "X-FADER → B", "Crossfader-" => "X-FADER → A", "Crossfader" => "CROSSFADER", "Master" => "MASTER level",
            "Trim+" => "TRIM +", "Trim-" => "TRIM −", "Panic" => "KILL AUDIO",
            "FxX" => "FX PAD ↔ size / rate", "FxY" => "FX PAD ↕ reverb↔delay", "TimeBend" => "TIME BEND: ½ slow, full stop",
            "BeatRoll" => "BEAT ROLL 1/4 … 1/32", "SpinBack" => "SPIN-BACK", "Reverse" => "REVERSE (hold)",
            "FxCycle" => "FX: next effect", "FxFreeze" => "FREEZE reverb tail",
            "QuantCycle" => "QUANTIZE: off · 1/16 · 1/8 · 1/4 · bar", "BankMode" => "BANK MODE: next", "PadGrid" => "PAD GRID window",
            _ when a.StartsWith("Bank:") => $"BANK {a[5..]}",
            _ when a.StartsWith("PadCapture:") => $"ALT pad {a[11..]} · capture",
            _ when a.StartsWith("Pad:") => $"PAD {a[4..]}  (bank pad)",
            _ when a.StartsWith("Roll:") => $"LOOP ROLL {a[5..]}",
            _ => a.Replace("+", " +").Replace("-", " −").ToUpperInvariant()
        };
        return deck + text;
    }

    private string Axis(string name)
        => _shift && _config.ShiftAxes.TryGetValue(name, out var sv) ? Describe(sv) : _config.GamepadAxes.TryGetValue(name, out var v) ? Describe(v) : "—";
    private string Btn(string name) => Describe(_shift ? _config.ShiftButtons.GetValueOrDefault(name) : _config.GamepadButtons.GetValueOrDefault(name));
    private bool Down(PadButton b) => _connected && (_frame.Buttons & b) != 0;

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "PadOverlay.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(Width / 820f, Height / 440f);
        float ox = (Width - 820 * s) / 2, oy = (Height - 440 * s) / 2;
        PointF P(float x, float y) => new(ox + x * s, oy + y * s);
        RectangleF R(float x, float y, float w, float h) => new(ox + x * s, oy + y * s, w * s, h * s);
        Rectangle RI(float x, float y, float w, float h) => Rectangle.Round(R(x, y, w, h));
        Color lit = _focus == 0 ? Theme.Cyan : Theme.Amber;

        using (var border = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 16 * DeviceDpi / 96f))
        { using var b = new SolidBrush(Theme.Surface); g.FillPath(b, border); using var p = new Pen(Theme.Edge); g.DrawPath(p, border); }

        Theme.TextAt(g, _shift ? "CONTROLLER  ·  SHIFT LAYER" : "CONTROLLER", RI(24, 10, 360, 24), 11, _shift ? lit : Theme.Text, true);
        Theme.TextAt(g, $"Focus: DECK {(_focus == 0 ? "A" : "B")}{(_connected ? "" : "   ·   no controller connected")}", RI(460, 10, 336, 24), 9, _connected ? lit : Theme.Muted, true);

        // silhouette
        using (var fill = new SolidBrush(Theme.Raised))
        using (var edge = new Pen(Theme.Edge, 1.5f * s))
        {
            foreach (var grip in new[] { R(262, 170, 112, 190), R(446, 170, 112, 190) })
            { g.FillEllipse(fill, grip); g.DrawEllipse(edge, grip); }
            using var body = Theme.Round(R(270, 128, 280, 160), 64 * s);
            g.FillPath(fill, body); g.DrawPath(edge, body);
            g.FillEllipse(fill, R(266, 182, 104, 150)); g.FillEllipse(fill, R(450, 182, 104, 150));
        }
        void Shape(RectangleF r, bool on, bool round, double fillAmount = -1)
        {
            using var b = new SolidBrush(on ? lit : Theme.Surface);
            using var p = new Pen(on ? lit : Theme.Edge, 1.5f * s);
            if (round) { g.FillEllipse(b, r); g.DrawEllipse(p, r); return; }
            using var path = Theme.Round(r, 5 * s);
            if (fillAmount >= 0)
            {
                using var bg = new SolidBrush(Theme.Surface); g.FillPath(bg, path);
                if (fillAmount > 0) { var fr = new RectangleF(r.X, r.Y, r.Width * (float)fillAmount, r.Height); using var fp = Theme.Round(fr, 5 * s); using var lb = new SolidBrush(lit); g.FillPath(lb, fp); }
            }
            else g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        Shape(R(300, 76, 60, 18), _connected && _frame.LT > 0, false, _connected ? _frame.LT : 0);
        Shape(R(460, 76, 60, 18), _connected && _frame.RT > 0, false, _connected ? _frame.RT : 0);
        Shape(R(292, 106, 78, 16), Down(PadButton.LeftShoulder), false);
        Shape(R(450, 106, 78, 16), Down(PadButton.RightShoulder), false);
        void Stick(float cx, float cy, double x, double y, bool press)
        {
            Shape(R(cx - 22, cy - 22, 44, 44), press, true);
            bool moved = _connected && (x != 0 || y != 0);
            using var b = new SolidBrush(moved || press ? (press ? Theme.Surface : lit) : Theme.Muted);
            g.FillEllipse(b, R(cx - 7 + (float)x * 13, cy - 7 - (float)y * 13, 14, 14));
        }
        Stick(326, 178, _frame.LX, _frame.LY, Down(PadButton.LeftThumb));
        Stick(454, 244, _frame.RX, _frame.RY, Down(PadButton.RightThumb));
        Shape(R(362, 222, 16, 16), Down(PadButton.Up), false); Shape(R(362, 254, 16, 16), Down(PadButton.Down), false);
        Shape(R(346, 238, 16, 16), Down(PadButton.Left), false); Shape(R(378, 238, 16, 16), Down(PadButton.Right), false);
        Shape(R(384, 172, 16, 16), Down(PadButton.Back), true); Shape(R(420, 172, 16, 16), Down(PadButton.Start), true);
        foreach (var (b, x, y, t) in new[] { (PadButton.Y, 494f, 152f, "Y"), (PadButton.X, 472f, 174f, "X"), (PadButton.B, 516f, 174f, "B"), (PadButton.A, 494f, 196f, "A") })
        {
            Shape(R(x - 11, y - 11, 22, 22), Down(b), true);
            Theme.TextAt(g, t, RI(x - 11, y - 11, 22, 22), 7.5f, Down(b) ? Theme.Surface : Theme.Muted, true, true);
        }

        // labels
        var left = new (string In, string Act, PointF To, bool On)[]
        {
            ("LT", Axis("LeftTrigger"), P(300, 85), _connected && _frame.LT > 0),
            ("LB", Btn("LeftShoulder"), P(292, 114), Down(PadButton.LeftShoulder)),
            ("L-STICK ↕", Axis("LeftThumbY"), P(312, 172), _connected && _frame.LY != 0),
            ("L-STICK ↔", Axis("LeftThumbX"), P(306, 184), _connected && _frame.LX != 0),
            ("L3 (click)", Btn("LeftThumb"), P(316, 196), Down(PadButton.LeftThumb)),
            ("D-PAD ↑", Btn("Up"), P(362, 230), Down(PadButton.Up)),
            ("D-PAD ↓", Btn("Down"), P(362, 262), Down(PadButton.Down)),
            ("D-PAD ←", Btn("Left"), P(346, 246), Down(PadButton.Left)),
            ("D-PAD →", Btn("Right"), P(386, 238), Down(PadButton.Right)),
            ("VIEW (tap)", Btn("Back"), P(386, 180), Down(PadButton.Back)),
        };
        var right = new (string In, string Act, PointF To, bool On)[]
        {
            ("RT", Axis("RightTrigger"), P(520, 85), _connected && _frame.RT > 0),
            ("RB", Btn("RightShoulder"), P(528, 114), Down(PadButton.RightShoulder)),
            ("Y", Btn("Y"), P(505, 152), Down(PadButton.Y)),
            ("B", Btn("B"), P(527, 174), Down(PadButton.B)),
            ("X", Btn("X"), P(472, 163), Down(PadButton.X)),
            ("A", Btn("A"), P(505, 196), Down(PadButton.A)),
            ("R-STICK ↔ (FX pad)", Axis("RightThumbX"), P(476, 244), _connected && _frame.RX != 0),
            ("R-STICK ↕ (FX pad)", Axis("RightThumbY"), P(470, 260), _connected && _frame.RY != 0),
            ("R3 (click)", Btn("RightThumb"), P(454, 266), Down(PadButton.RightThumb)),
            ("MENU (tap)", Btn("Start"), P(434, 180), Down(PadButton.Start)),
        };
        using var line = new Pen(Theme.Edge, s);
        using var lineOn = new Pen(lit, 1.5f * s);
        for (int i = 0; i < left.Length; i++)
        {
            var (inp, act, to, on) = left[i]; float y = 46 + i * 35;
            Theme.TextAt(g, inp, RI(24, y, 210, 14), 7, on ? lit : Theme.Muted, true);
            Theme.TextAt(g, act, RI(24, y + 13, 210, 18), 8.5f, on ? lit : Theme.Text, on);
            g.DrawLine(on ? lineOn : line, P(236, y + 16), to);
        }
        for (int i = 0; i < right.Length; i++)
        {
            var (inp, act, to, on) = right[i]; float y = 46 + i * 35;
            Theme.TextAt(g, inp, RI(586, y, 210, 14), 7, on ? lit : Theme.Muted, true);
            Theme.TextAt(g, act, RI(586, y + 13, 210, 18), 8.5f, on ? lit : Theme.Text, on);
            g.DrawLine(on ? lineOn : line, P(582, y + 16), to);
        }
        string chord = $"SHIFT: hold {_config.ShiftButton} or {_config.ShiftChord.Replace("LeftShoulder", "LB").Replace("RightShoulder", "RB")}   ·   " +
                       $"KILL AUDIO: hold {_config.PanicChord.Replace("Back", "View").Replace("Start", "Menu")} {_config.PanicHoldMs} ms   ·   Shift+F1 hides";
        Theme.TextAt(g, chord.Replace("hold Back", "hold View"), RI(24, 398, 772, 20), 8, Theme.Muted, false, true);
        Theme.TextAt(g, "Rebind anything: MAP PAD in the top bar (press a button on the pad to find it)   ·   empty pads capture the last beat on first press", RI(24, 417, 772, 20), 8, Theme.Muted, false, true);
    }
}
