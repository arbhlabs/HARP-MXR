using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;

namespace MixDock.Ui;

/// <summary>MIDI and pad settings: output / input port, channels, note base, clock, chord key and velocity feel. Dark, no error dialogs.</summary>
public sealed class MidiSettingsForm : Form
{
    private readonly AppConfig _c;
    private readonly ComboBox _out = new(), _in = new(), _key = new(), _curve = new();
    private readonly NumericUpDown _ch = new(), _chordCh = new(), _note = new(), _velMin = new(), _fxCc = new();
    private readonly CheckBox _enabled = new(), _clock = new(), _keyAuto = new(), _vel = new();

    public MidiSettingsForm(AppConfig config, string status)
    {
        _c = config;
        Text = "MIDI and pad settings"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        int y = 14;
        var st = new Label { AutoSize = false, Text = status, Left = 16, Top = y, Width = 488, Height = 22, ForeColor = status.Contains("no virtual port") ? Theme.Muted : Color.FromArgb(131, 181, 185), Font = Theme.Font(10, true) };
        Controls.Add(st); y += 30;
        var hint = new Label { AutoSize = false, Text = "MixDock creates its own port when the Tobias Erichsen teVirtualMIDI driver is installed, otherwise it uses a loopMIDI port.", Left = 16, Top = y, Width = 488, Height = 34, ForeColor = Theme.Muted, Font = Theme.Font(8.5f) };
        Controls.Add(hint); y += 40;
        void Row(string label, Control c, int w = 250)
        {
            if (label.Length > 0) Controls.Add(new Label { AutoSize = false, Text = label, Left = 16, Top = y + 3, Width = 190, Height = 22, ForeColor = Theme.Text });
            c.Left = label.Length > 0 ? 214 : 16; c.Top = y; c.Width = label.Length > 0 ? w : 488; Controls.Add(c); y += 32;
        }
        void Style(Control c) { c.BackColor = Theme.Raised; c.ForeColor = Theme.Text; }
        _enabled.Text = "MIDI on"; _enabled.Checked = _c.MidiEnabled; _enabled.ForeColor = Theme.Text; Row("", _enabled);
        _out.DropDownStyle = ComboBoxStyle.DropDownList; _out.Items.Add("(automatic)"); _out.Items.AddRange(MidiPorts.OutputNames().Cast<object>().ToArray());
        _out.SelectedIndex = Math.Max(0, _out.Items.IndexOf(_c.MidiOutPort)); Style(_out); Row("Output port", _out);
        _in.DropDownStyle = ComboBoxStyle.DropDownList; _in.Items.Add("(none)"); _in.Items.AddRange(MidiPorts.InputNames().Cast<object>().ToArray());
        _in.SelectedIndex = Math.Max(0, _in.Items.IndexOf(_c.MidiInPort)); Style(_in); Row("Input port (DAW to pads)", _in);
        _ch.Minimum = 1; _ch.Maximum = 16; _ch.Value = _c.MidiChannel; Style(_ch); Row("Pad channel", _ch, 70);
        _chordCh.Minimum = 1; _chordCh.Maximum = 16; _chordCh.Value = _c.MidiChordChannel; Style(_chordCh); Row("Chord channel", _chordCh, 70);
        _note.Minimum = 0; _note.Maximum = 111; _note.Value = _c.MidiNoteBase; Style(_note); Row("Lowest pad note", _note, 70);
        _fxCc.Minimum = 0; _fxCc.Maximum = 127; _fxCc.Value = _c.MidiFxCc; Style(_fxCc); Row("FX amount CC (input)", _fxCc, 70);
        _clock.Text = "Send MIDI clock (24 per beat) from the focus deck's beat"; _clock.Checked = _c.MidiClock; _clock.ForeColor = Theme.Text; _clock.Width = 380; Row("", _clock, 380);
        y += 6;
        _keyAuto.Text = "Chord key follows the focus deck's detected key"; _keyAuto.Checked = _c.KeyAuto; _keyAuto.ForeColor = Theme.Text; Row("", _keyAuto, 380);
        _key.DropDownStyle = ComboBoxStyle.DropDownList; foreach (var k in ChordTheory.AllKeys()) _key.Items.Add(ChordTheory.KeyName(k));
        _key.SelectedIndex = _c.ManualKeyPc + (_c.ManualKeyMinor ? 12 : 0); Style(_key); Row("Manual key", _key);
        _vel.Text = "Velocity from RT depth"; _vel.Checked = _c.VelocitySensitive; _vel.ForeColor = Theme.Text; Row("", _vel);
        _curve.DropDownStyle = ComboBoxStyle.DropDownList; _curve.Items.AddRange(new object[] { "soft", "normal", "hard" });
        _curve.SelectedItem = _c.VelocityCurve is "soft" or "hard" ? _c.VelocityCurve : "normal"; Style(_curve); Row("Velocity curve", _curve, 120);
        _velMin.Minimum = 1; _velMin.Maximum = 126; _velMin.Value = _c.VelocityMin; Style(_velMin); Row("Velocity with RT idle", _velMin, 70);

        ClientSize = new Size(520, y + 56);
        var ok = new StudioButton { Text = "Save", Left = 296, Top = y + 8, Width = 100, Height = 34, Accent = Theme.Cyan };
        var cancel = new StudioButton { Text = "Cancel", Left = 404, Top = y + 8, Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { Apply(); DialogResult = DialogResult.OK; Close(); };
        Controls.AddRange([ok, cancel]); AcceptButton = ok; CancelButton = cancel;
    }

    private void Apply()
    {
        _c.MidiEnabled = _enabled.Checked;
        _c.MidiOutPort = _out.SelectedIndex > 0 ? _out.SelectedItem?.ToString() ?? "" : "";
        _c.MidiInPort = _in.SelectedIndex > 0 ? _in.SelectedItem?.ToString() ?? "" : "";
        _c.MidiChannel = (int)_ch.Value; _c.MidiChordChannel = (int)_chordCh.Value; _c.MidiNoteBase = (int)_note.Value; _c.MidiFxCc = (int)_fxCc.Value;
        _c.MidiClock = _clock.Checked; _c.KeyAuto = _keyAuto.Checked;
        _c.ManualKeyPc = _key.SelectedIndex % 12; _c.ManualKeyMinor = _key.SelectedIndex >= 12;
        _c.VelocitySensitive = _vel.Checked; _c.VelocityCurve = _curve.SelectedItem?.ToString() ?? "normal"; _c.VelocityMin = (int)_velMin.Value;
    }
}

