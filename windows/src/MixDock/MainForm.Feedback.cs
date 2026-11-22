using System.Drawing.Drawing2D;
using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

public sealed partial class MainForm
{
    private readonly StickReadout[,] _stickReadouts = new StickReadout[2,2];
    private readonly PadGlyphBar[] _buttonHints = [
        new(("A", PadButton.A), ("B", PadButton.B), ("X", PadButton.X), ("Y", PadButton.Y)),
        new(("LB", PadButton.LeftShoulder), ("RB", PadButton.RightShoulder), ("Menu", PadButton.Start))
    ];
    private PadOverlay? _shiftOverlay;
    private float _focusOrigin, _focusPosition;
    private int _focusTarget;
    private long _focusStarted;

    private void InstallStickReadouts(DjLayout dj, int deck, int x, int y, Color accent)
    {
        for (int i = 0; i < 2; i++)
        {
            var stick = _stickReadouts[deck,i] = new StickReadout(i == 0 ? "L" : "R", accent) { BackColor = Theme.Background };
            dj.Place(stick, x + i * 144, y, 140, 32);
        }
    }

    private void InstallControllerFeedback(DjLayout dj)
    {
        _shiftOverlay = new PadOverlay(_config) {AccessibleName = "Held SHIFT mappings"};
        Controls.Add(_shiftOverlay);
        dj.Paint += (_, e) => PaintFocusRing(e.Graphics, dj);
        RefreshControllerFeedback(default, false, false);
    }

    private void MoveFocusRing(int deck)
    {
        if (_focusTarget == deck) return;
        _focusOrigin = _focusPosition;
        _focusTarget = deck;
        _focusStarted = Environment.TickCount64;
        AdvanceFocusRing();
    }

    private void AdvanceFocusRing()
    {
        float t = ConsoleMotion.Enabled ? ConsoleMotion.Ease(Environment.TickCount64 - _focusStarted) : 1;
        float next = _focusOrigin + (_focusTarget - _focusOrigin) * t;
        if (next == _focusPosition) return;
        _focusPosition = next;
        _dj?.Invalidate(false);
    }

    private void PaintFocusRing(Graphics g, DjLayout dj)
    {
        float s = dj.ScaleFactor;
        if (s <= 0) return;
        // the ring sits in the 4-unit gutter around the focused deck column, so no control covers it
        float x = (dj.Width - 1366*s)/2 + (DeckX(0) - 4 + (DeckX(1) - DeckX(0))*_focusPosition)*s;
        float y = (dj.Height - 768*s)/2 + (DeckTop - 4)*s;
        using var ring = Theme.Round(new RectangleF(x, y, (DeckWidth + 8)*s, (DeckBottom - DeckTop + 4)*s), 4*s);
        using var pen = new Pen(_focusTarget == 0 ? Theme.Cyan : Theme.Amber, Math.Max(1, 1.6f*s));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawPath(pen, ring);
    }

    private void RefreshControllerFeedback(PadFrame frame, bool connected, bool shift)
    {
        AdvanceFocusRing();
        if (!connected) { frame = default; shift = false; }
        foreach (var bar in _buttonHints)
            bar.UpdateState(_padMapper, frame, shift, _focus == 0 ? Theme.Cyan : Theme.Amber,
                string.Equals(_config.ShiftButton, "Start", StringComparison.OrdinalIgnoreCase));

        if (_shiftOverlay != null)
        {
            bool show = shift && !_overlay.Visible && !_bindingsOpen;
            if (show)
            {
                PlaceOverlay();
                _shiftOverlay.Bounds = _overlay.Bounds;
                _shiftOverlay.SetState(frame, true, connected, _focus);
                if (!_shiftOverlay.Visible) _shiftOverlay.BringToFront();
            }
            _shiftOverlay.Visible = show;
        }

        string Binding(string axis) => shift && _config.ShiftAxes.TryGetValue(axis, out var action)
            ? action : _config.GamepadAxes.GetValueOrDefault(axis, "");
        string leftX = Binding("LeftThumbX");
        string leftY = Binding("LeftThumbY");
        string rightX = Binding("RightThumbX"), rightY = Binding("RightThumbY");
        bool sideBlend = leftX.EndsWith("FxBlend", StringComparison.Ordinal);
        bool chordStick = ModeOf(_bank) == BankMode.Chord && !shift;
        int leftDeck = _axisOwner.GetValueOrDefault("LeftThumbX#blend",
            _axisOwner.GetValueOrDefault("LeftThumbY", DeckOf(leftY, _focus, out _) ?? _focus));
        int rightDeck = _axisOwner.GetValueOrDefault("FxPad", DeckOf(rightX, _focus, out _) ?? _focus);
        for (int d = 0; d < 2; d++)
        {
            var fx = Fx(d).State;
            bool mouse = _mouseFxHeld[d];
            bool left = d == leftDeck && (frame.LX != 0 || frame.LY != 0);
            bool right = d == rightDeck && (frame.RX != 0 || frame.RY != 0);
            double filter = (Panel(d).Filter.Precise - 50) * 2;
            string caption = "FILTER", detail = Math.Abs(filter) < .5 ? "Open" : $"{(filter < 0 ? "LP" : "HP")} {Math.Abs(filter):0}%";
            if (left && sideBlend && Math.Abs(frame.LX) > _config.FxDeadzone)
            {
                bool echo = frame.LX > 0;
                double depth = echo ? _blendEcho[d] : _appliedLeftReverb[d];
                caption = echo ? "ECHO" : "REVERB";
                detail = Math.Abs(filter) < .5 ? $"{depth:P0}" : $"{depth:P0} · {(filter < 0 ? "LP" : "HP")} {Math.Abs(filter):0}%";
            }
            else if (left && Math.Abs(frame.LX) > Math.Abs(frame.LY) && leftX == "Crossfader")
            {
                caption = "CROSSFADE";
                detail = $"B  {_cross.Value}%";
            }
            else if (left && !leftY.EndsWith("Filter", StringComparison.Ordinal))
            {
                caption = "AXIS";
                detail = PadOverlay.Describe(Math.Abs(frame.LX) > Math.Abs(frame.LY) ? leftX : leftY);
            }
            if (mouse)
            {
                caption = "MOUSE XY";
                detail = $"{fx.X*100:0} / {fx.Y*100:0}";
            }
            if (StickFxReadout(d, false) is { } ls) { caption = ls.Caption; detail = ls.Detail; left = ls.Active; }
            _stickReadouts[d,0].UpdateState(frame.LX, frame.LY, left, caption, detail);
            string effect = fx.Effect.ToString().ToUpperInvariant();
            string amount = fx.Freeze ? $"Hold  {fx.Amount:P0}" : $"{fx.Amount:P0}";
            if (right && chordStick && !mouse) { effect = "CHORD"; amount = _voicing.ToString(); }
            else if (right && !mouse && !rightX.EndsWith("FxX", StringComparison.Ordinal)
                     && !rightY.EndsWith("FxY", StringComparison.Ordinal))
            {
                bool horizontal = Math.Abs(frame.RX) > Math.Abs(frame.RY);
                effect = horizontal ? "X AXIS" : "Y AXIS";
                amount = PadOverlay.Describe(horizontal ? rightX : rightY);
            }
            else if (right && !fx.Freeze)
            {
                amount += $"   {fx.X*100:0}/{fx.Y*100:0}";
            }
            if (StickFxReadout(d, true) is { } rs) { effect = rs.Caption; amount = rs.Detail; right = rs.Active; mouse = false; }
            _stickReadouts[d,1].UpdateState(mouse ? fx.X*2-1 : _mouseSet[d].On ? _mouseSet[d].X : frame.RX, mouse ? fx.Y*2-1 : _mouseSet[d].On ? _mouseSet[d].Y : frame.RY,
                mouse || right, effect, amount);
        }
    }
}
