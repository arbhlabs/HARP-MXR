namespace MixDock.Ui;

/// <summary>One big AUTO MIX switch, a fade-length slider and a switch-every slider. Lives in the header.</summary>
public sealed class AutoMixBar : Panel
{
    private Font? _fitFont;
    private readonly StudioButton _toggle = new() { Text = "AUTO MIX  OFF", AccessibleName = "Auto mix: fade between the two decks by itself" };
    private readonly Fader _fade = new() { Value = 15, ResetValue = 15, Accent = Theme.Text, AccessibleName = "Fade length" };
    private readonly Fader _every = new() { Value = 29, ResetValue = 29, Accent = Theme.Text, AccessibleName = "Switch every" };
    private readonly Label _fadeL = new() { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Font(7.5f, true) };
    private readonly Label _everyL = new() { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Font(7.5f, true) };
    private readonly Label _status = new() { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    public new event Action<bool>? EnabledChanged;
    public event Action<double>? FadeSecondsChanged, EveryMinutesChanged;
    public double FadeSeconds => 4 + _fade.Value * 0.26;       // 4..30 s
    public double EveryMinutes => 1 + _every.Value * 0.07;      // 1..8 min

    public AutoMixBar()
    {
        BackColor = Theme.Background;
        Controls.AddRange([_toggle, _fadeL, _fade, _everyL, _every, _status]);
        _toggle.Accent = Theme.Cyan;
        _toggle.Click += (_, _) => Toggle();
        _fade.ValueChanged += (_, _) => { UpdateLabels(); FadeSecondsChanged?.Invoke(FadeSeconds); };
        _every.ValueChanged += (_, _) => { UpdateLabels(); EveryMinutesChanged?.Invoke(EveryMinutes); };
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        _fadeL.Text = $"FADE  {FadeSeconds:F0} s";
        _everyL.Text = $"SWITCH EVERY  {EveryMinutes:F0} min";
    }

    /// <summary>Same as clicking the AUTO MIX switch (controller Menu button).</summary>
    public bool Toggle()
    {
        _toggle.Active = !_toggle.Active; _toggle.Text = _toggle.Active ? "AUTO MIX  ON" : "AUTO MIX  OFF"; _toggle.Invalidate();
        EnabledChanged?.Invoke(_toggle.Active);
        return _toggle.Active;
    }
    public void SetStatus(string text) { if (_status.Text != text) _status.Text = text; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float s = UiMetrics.Scale(this); int D(float n) => (int)(n * s);
        float pixels=Math.Max(7,8*96/72*s);
        if(_fitFont==null||Math.Abs(_fitFont.Size-pixels)>.1){var old=_fitFont;_fitFont=new Font("Segoe UI",pixels,FontStyle.Regular,GraphicsUnit.Pixel);foreach(Control c in Controls)c.Font=_fitFont;old?.Dispose();}
        int h=Height;
        _toggle.SetBounds(0,D(3),D(130),h-D(6));
        _fadeL.SetBounds(D(138),0,D(150),D(14));_fade.SetBounds(D(138),D(12),D(145),h-D(12));
        _everyL.SetBounds(D(298),0,D(185),D(14));_every.SetBounds(D(298),D(12),D(180),h-D(12));
        _status.SetBounds(D(488),D(2),Math.Max(0,Width-D(488)),h-D(4));
    }
}
