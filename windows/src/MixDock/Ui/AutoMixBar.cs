using System.Drawing.Drawing2D;
using MixDock.Audio;

namespace MixDock.Ui;

/// <summary>
/// The Auto Mix transition card: what is happening, the style, bars left, the next deck (tempo and key) and which lanes
/// the automation holds versus the owner's hand. Everything is sized from the card height so it reads from a metre or
/// more, and text shrinks to fit before it would ever clip.
/// </summary>
public sealed class AutoMixBar : Panel
{
    /// <summary>Logical (1366 x 768 canvas) height the card is designed for.</summary>
    public const int DesignHeight = 84;
    private readonly StudioButton _toggle = new() { Text = "AUTO", AccessibleName = "Auto mix: beat-aligned transitions between the decks" };
    private readonly StudioButton _now = new() { Text = "MIX NOW", AccessibleName = "Auto mix: start the next transition on the coming phrase" };
    private readonly StudioButton _skip = new() { Text = "SKIP", AccessibleName = "Auto mix: choose a different transition style" };
    private readonly StudioButton _mode = new() { Text = "AUTO ▾", AccessibleName = "Auto mix genre, energy, length and effects" };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tip = new();
    private AutoMixCard _card = AutoMixCard.Off;
    private MixPreferences _prefs = new();

    public new event Action<bool>? EnabledChanged;
    public event Action<MixPreferences>? PreferencesChanged;
    public event Action? SkipRequested, MixNowRequested, PickNextRequested;

    public bool On => _toggle.Active;
    public AutoMixCard Card => _card;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MixPreferences Preferences
    {
        get => _prefs;
        set { _prefs = value; _mode.Text = GenreShort(value.Genre) + " ▾"; PerformLayout(); }
    }

    public AutoMixBar()
    {
        DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Background;
        Controls.AddRange([_toggle, _now, _skip, _mode]);
        _toggle.Accent = Theme.Cyan; _now.Accent = Theme.Amber; _skip.Accent = Theme.Text; _mode.Accent = Theme.Text;
        _toggle.Click += (_, _) => Toggle();
        _now.Click += (_, _) => MixNowRequested?.Invoke();
        _skip.Click += (_, _) => SkipRequested?.Invoke();
        _mode.Click += (_, _) => { BuildMenu(); _menu.Show(_mode, new Point(0, _mode.Height)); };
        _now.Enabled = _skip.Enabled = false;
    }

    /// <summary>Same as clicking AUTO (controller Menu button).</summary>
    public bool Toggle()
    {
        _toggle.Active = !_toggle.Active;
        _now.Enabled = _skip.Enabled = _toggle.Active;
        EnabledChanged?.Invoke(_toggle.Active);
        Invalidate();
        return _toggle.Active;
    }

    public void ShowCard(AutoMixCard card)
    {
        if (card == _card) return;
        bool text = card.Why != _card.Why;
        _card = card;
        _skip.Enabled = On && !card.Running && card.Style.Length > 0;
        _now.Enabled = On && !card.Running;
        if (text) _tip.SetToolTip(this, card.Why);
        Invalidate();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Pick next track from library", null, (_, _) => PickNextRequested?.Invoke());
        ToolStripMenuItem Group<T>(string title, IEnumerable<T> values, T current, Func<T, string> name, Func<T, MixPreferences> apply)
        {
            var group = new ToolStripMenuItem(title);
            foreach (var v in values)
            {
                var item = new ToolStripMenuItem(name(v)) { Checked = EqualityComparer<T>.Default.Equals(v, current) };
                item.Click += (_, _) => { Preferences = apply(v); PreferencesChanged?.Invoke(_prefs); };
                group.DropDownItems.Add(item);
            }
            return group;
        }
        _menu.Items.Add(Group("Genre", Enum.GetValues<DjGenre>(), _prefs.Genre,
            g => g == DjGenre.Auto ? "Auto (guess from tempo)" : TransitionPlanner.Profile(g).Name, g => _prefs with { Genre = g }));
        _menu.Items.Add(Group("Energy", Enum.GetValues<EnergyArc>(), _prefs.Arc, a => a switch
        {
            EnergyArc.WarmUp => "Warm up", EnergyArc.Peak => "Peak time", EnergyArc.CoolDown => "Cool down", _ => "Follow the room"
        }, a => _prefs with { Arc = a }));
        _menu.Items.Add(Group("Length", Enum.GetValues<LengthBias>(), _prefs.Length, l => l.ToString(), l => _prefs with { Length = l }));
        _menu.Items.Add(Group("Effects", Enum.GetValues<FxIntensity>(), _prefs.Fx, f => f.ToString(), f => _prefs with { Fx = f }));
        var dramatic = new ToolStripMenuItem("Dramatic moments (power down, double drop)") { Checked = _prefs.Dramatic };
        dramatic.Click += (_, _) => { Preferences = _prefs with { Dramatic = !_prefs.Dramatic }; PreferencesChanged?.Invoke(_prefs); };
        _menu.Items.Add(dramatic);
    }

    private static string GenreShort(DjGenre g) => g switch
    {
        DjGenre.Auto => "AUTO", DjGenre.DrumAndBass => "DNB", DjGenre.HipHop => "HIP-HOP", DjGenre.AfroLatin => "AFRO",
        DjGenre.OpenFormat => "OPEN", _ => g.ToString().ToUpperInvariant()
    };

    private float S => Height > 0 ? Height / (float)DesignHeight : UiMetrics.Scale(this);
    private int D(float n) => (int)Math.Round(n * S);

    // Logical regions inside the card (x from the left edge, the button column on the right).
    private Rectangle Info => new(D(78), 0, Math.Max(1, Width - D(78) - D(128)), Height);
    private Rectangle StateRect => new(Info.X + D(6), D(5), Info.Width - D(12), D(30));
    private Rectangle WhyRect => new(Info.X + D(6), D(37), Info.Width - D(12), D(18));
    private Rectangle LaneRect => new(Info.X + D(6), D(64), Info.Width - D(12), D(17));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Width < 10 || Height < 10) return;
        int right = Width - D(124);
        _toggle.SetBounds(0, 0, D(72), Height);
        _now.SetBounds(right, 0, D(124), D(40));
        _skip.SetBounds(right, D(44), D(50), Height - D(44));
        _mode.SetBounds(right + D(54), D(44), Width - right - D(54), Height - D(44));
        foreach (var b in new[] { _toggle, _now, _skip, _mode }) Fit(b, 12, b == _toggle ? 16 : 12);
    }

    /// <summary>Largest bold font (up to <paramref name="maxPx"/> logical px) at which the caption fits its button.</summary>
    private void Fit(StudioButton b, float minPx, float maxPx)
    {
        float px = maxPx * S;
        Font f;
        while (true)
        {
            f = new Font("Segoe UI", Math.Max(7, px), FontStyle.Bold, GraphicsUnit.Pixel);
            var size = TextRenderer.MeasureText(b.Text, f, Size.Empty, TextFormatFlags.SingleLine);
            if (size.Width <= b.Width - 2 && size.Height <= b.Height || px <= Math.Max(7, minPx * S * .6f)) break;
            f.Dispose(); px -= .5f;
        }
        f.Dispose();
        _fontPx[b] = px;
        b.Font = ButtonFont(px);
    }
    // Shared, never disposed: a caption font must outlive every control that may still paint with it.
    private static readonly Dictionary<int, Font> ButtonFonts = new();
    private static Font ButtonFont(float px)
    {
        int key = (int)Math.Round(Math.Max(7, px) * 2);
        if (!ButtonFonts.TryGetValue(key, out var f) || !Usable(f))
            ButtonFonts[key] = f = new Font("Segoe UI", key / 2f, FontStyle.Bold, GraphicsUnit.Pixel);
        return f;
    }
    private static bool Usable(Font f) { try { return f.GetHeight() > 0; } catch (ArgumentException) { return false; } }
    private readonly Dictionary<StudioButton, float> _fontPx = new();

    private static string StateText(AutoMixCard c) => c.Style.Length > 0 ? c.Style.ToUpperInvariant() : c.State;
    private static readonly (MixLane Lane, string Name)[] LaneNames =
    {
        (MixLane.Fader, "FADER"), (MixLane.EqA | MixLane.EqB, "EQ"), (MixLane.FilterA | MixLane.FilterB, "FILTER"),
        (MixLane.FxA | MixLane.FxB, "FX"), (MixLane.RollA | MixLane.RollB, "LOOP"), (MixLane.TempoA | MixLane.TempoB, "TEMPO")
    };

    /// <summary>Text pieces of a card that would not fit even at the smallest allowed font (used by the offscreen UI check).</summary>
    public IReadOnlyList<string> Overflow(AutoMixCard card)
    {
        var bad = new List<string>();
        int chip = card.Style.Length > 0 && card.State.Length > 0 ? Measure(card.State, 10, true) + D(18) : 0;
        if (chip + Measure(StateText(card), 11, true) > StateRect.Width - CountWidth(card)) bad.Add(StateText(card));
        if (Measure(card.Why, 9.5f, false) > WhyRect.Width) bad.Add(card.Why);
        int left = card.Running ? LaneNames.Where(l => ((card.Automated | card.Manual) & l.Lane) != 0).Sum(l => Measure(l.Name, 9.5f, true) + D(12))
            : card.Genre.Length > 0 ? Math.Min(LaneRect.Width / 3, Measure(card.Genre.ToUpperInvariant(), 9.5f, true)) + D(10) : 0;
        if (card.Next.Length > 0 && left + D(4) + Measure(card.Next, 9.5f, true) > LaneRect.Width) bad.Add(card.Next);
        foreach (var b in new[] { _toggle, _now, _skip, _mode })
        {
            using var f = new Font("Segoe UI", Math.Max(7, _fontPx.GetValueOrDefault(b, 12 * S)), FontStyle.Bold, GraphicsUnit.Pixel);
            if (TextRenderer.MeasureText(b.Text, f, Size.Empty, TextFormatFlags.SingleLine).Width > b.Width) bad.Add(b.Text);
        }
        return bad;
    }

    private int Measure(string text, float logicalPx, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var f = new Font("Segoe UI", Math.Max(7, logicalPx * S), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        return TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
    }
    private int CountWidth(AutoMixCard c) => c.Count.Length == 0 ? 0 : Measure(c.Count, 13, true) + D(8);
    private int LanesWidth(float px) => LaneNames.Sum(l => Measure(l.Name, px, true) + D(12)) + D(8);

    /// <summary>Draws <paramref name="text"/> at the largest size between min and max (logical px) that fits the box.</summary>
    private int DrawFit(Graphics g, string text, Rectangle r, float maxPx, float minPx, bool bold, Color color, bool right = false)
    {
        if (string.IsNullOrEmpty(text) || r.Width <= 0) return 0;
        float px = maxPx;
        Font f;
        Size size;
        while (true)
        {
            f = new Font("Segoe UI", Math.Max(7, px * S), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            size = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (size.Width <= r.Width && size.Height <= r.Height + 2 || px <= minPx) break;
            f.Dispose(); px -= .5f;
        }
        using (f)
            TextRenderer.DrawText(g, text, f, r, color, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
        return Math.Min(size.Width, r.Width);
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "AutoMixBar.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics;
        foreach (var b in new[] { _toggle, _now, _skip, _mode })
            if (!Usable(b.Font)) b.Font = ButtonFont(_fontPx.GetValueOrDefault(b, 12 * S));
        g.Clear(BackColor);
        if (Width < 40 || Height < 20) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = _card;
        var info = Info;
        Color accent = c.Running ? Theme.Cyan : c.State is "NEXT" or "MIX NOW" or "STARTING" ? Theme.Amber : On ? Theme.Text : Theme.Muted;
        using (var path = Theme.Round(new RectangleF(info.X + .5f, .5f, info.Width - 1, Height - 1), 3 * S))
        using (var fill = new SolidBrush(Theme.Surface))
        using (var edge = new Pen(c.Running ? Theme.Cyan : Theme.Edge, c.Running ? Math.Max(1, 1.5f * S) : 1))
        { g.FillPath(fill, path); g.DrawPath(edge, path); }

        // Row 1: state chip, style (biggest text), count on the right.
        var row = StateRect;
        int countW = CountWidth(c);
        if (countW > 0) DrawFit(g, c.Count, new Rectangle(row.Right - countW, row.Y, countW, row.Height), 22, 13, true, accent, right: true);
        int x = row.X;
        if (c.Style.Length > 0 && c.State.Length > 0)
        {
            int chipW = Measure(c.State, 10, true) + D(10);
            var chip = new Rectangle(x, row.Y + D(6), chipW, row.Height - D(12));
            using (var b = new SolidBrush(accent)) using (var p = Theme.Round(chip, 2 * S)) g.FillPath(b, p);
            DrawFit(g, c.State, new Rectangle(chip.X + D(5), chip.Y, chip.Width - D(8), chip.Height), 10, 9.5f, true, Theme.Background);
            x = chip.Right + D(8);
        }
        DrawFit(g, StateText(c), new Rectangle(x, row.Y, Math.Max(1, row.Right - countW - x), row.Height), 20, 11, true,
            c.Style.Length > 0 ? Theme.Text : accent);

        // Row 2: why.
        DrawFit(g, c.Why, WhyRect, 13, 9.5f, false, Theme.Muted);

        // Progress through the transition.
        var bar = new RectangleF(info.X + D(6), D(57), info.Width - D(12), Math.Max(2, 4 * S));
        using (var track = new SolidBrush(Theme.Idle)) g.FillRectangle(track, bar);
        if (c.Running && c.Progress > 0)
            using (var b = new SolidBrush(Theme.Cyan)) g.FillRectangle(b, bar with { Width = bar.Width * (float)Math.Clamp(c.Progress, 0, 1) });

        // Row 3: lanes (cyan = automated now, amber = your hand) and the next deck.
        var lanes = LaneRect;
        int lx = lanes.X;
        if (c.Running)
            foreach (var (lane, name) in LaneNames)
            {
                bool manual = (c.Manual & lane) != 0, auto = (c.Automated & lane) != 0 && !manual;
                if (!manual && !auto) continue;
                int w = Measure(name, 9.5f, true) + D(8);
                var chip = new Rectangle(lx, lanes.Y, w, lanes.Height);
                Color col = manual ? Theme.Amber : Theme.Cyan;
                using (var p = Theme.Round(chip, 2 * S)) using (var b = new SolidBrush(col)) g.FillPath(b, p);
                DrawFit(g, name, new Rectangle(chip.X + D(4), chip.Y, chip.Width - D(6), chip.Height), 9.5f, 9.5f, true, Theme.Background);
                lx = chip.Right + D(4);
            }
        else if (c.Genre.Length > 0)
            lx += DrawFit(g, c.Genre.ToUpperInvariant(), new Rectangle(lx, lanes.Y, Math.Max(1, lanes.Width / 3), lanes.Height), 11, 9.5f, true, Theme.Muted) + D(10);
        if (lx < lanes.Right - D(20))
            DrawFit(g, c.Next, new Rectangle(lx + D(4), lanes.Y, lanes.Right - lx - D(4), lanes.Height), 12, 9.5f, true, Theme.Text, right: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _menu.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
