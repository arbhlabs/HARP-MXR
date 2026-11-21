using MixDock.Platform;
using MixDock.Settings;

namespace MixDock.Ui;

/// <summary>
/// Controller mapping editor: every Xbox input with its normal and Shift action, picked from the action catalog.
/// Press a button or move a stick/trigger on the controller and its row lights up and takes focus (learn).
/// Edits a copy; OK writes the four layers + Shift button back into the config. Nothing runs while it is open.
/// </summary>
public sealed class BindingsForm : Form
{
    private sealed record Input(string Key, string Name, bool Axis);
    private static readonly Input[] Inputs =
    {
        new("LeftThumbY", "Left stick ↕", true), new("LeftThumbX", "Left stick ↔", true),
        new("RightThumbX", "Right stick ↔", true), new("RightThumbY", "Right stick ↕", true),
        new("LeftTrigger", "LT (left trigger)", true), new("RightTrigger", "RT (right trigger)", true),
        new("A", "A", false), new("B", "B", false), new("X", "X", false), new("Y", "Y", false),
        new("LeftShoulder", "LB", false), new("RightShoulder", "RB", false),
        new("Up", "D-pad ↑", false), new("Down", "D-pad ↓", false), new("Left", "D-pad ←", false), new("Right", "D-pad →", false),
        new("LeftThumb", "L3 (press left stick)", false), new("RightThumb", "R3 (press right stick)", false),
        new("Back", "View ⧉ (tap; hold = Shift)", false), new("Start", "Menu ≡ (tap)", false),
    };

    /// <summary>One combo entry: a catalog action, nothing, or a raw string the catalog does not know (kept, flagged).</summary>
    private sealed class Choice
    {
        public ActionDef? Def; public string? Raw; public string Text = "";
        public override string ToString() => Text;
    }
    private sealed class Cell
    {
        public ComboBox Action = null!, Deck = null!;
    }
    private sealed class Row
    {
        public Input Input = null!; public Label Label = null!; public Cell Normal = null!, Shift = null!; public long LitUntil;
    }

    private readonly AppConfig _config;
    private readonly List<Row> _rows = new();
    private readonly ComboBox _shiftButton = new(), _preset = new();
    private readonly Label _learn = new();
    private readonly TableLayoutPanel _table = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 25 };
    private PadButton _prevButtons; private readonly double[] _prevAxis = new double[6];
    private bool _primed;

    public BindingsForm(AppConfig config)
    {
        _config = config;
        Text = "MixDock · Controller mappings";
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1000, 720); MinimumSize = new Size(860, 520);
        StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
        KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(16, 10, 16, 6), BackColor = Theme.Background };
        var title = new Label { Text = "CONTROLLER MAPPINGS", Font = Theme.Font(13, true), AutoSize = true, Location = new Point(16, 8), ForeColor = Theme.Text };
        _learn.Text = "Press any button or move a stick on the controller: its row lights up so you can pick what it does.";
        _learn.AutoSize = false; _learn.SetBounds(16, 38, 620, 22); _learn.ForeColor = Theme.Cyan;
        var hint = new Label { Text = "\"Focused deck\" follows LB / RB.  Hold View (or LB+RB) for the Shift column.  Hold View+Menu ½ s = kill all audio.", AutoSize = false, ForeColor = Theme.Muted };
        hint.SetBounds(16, 62, 760, 22);
        StyleCombo(_preset); _preset.Items.AddRange(AppConfig.Presets); _preset.SelectedIndex = 0; _preset.SetBounds(650, 34, 160, 26);
        var load = new StudioButton { Text = "LOAD PRESET", Accent = Theme.Amber }; load.SetBounds(818, 33, 160, 28);
        load.Click += (_, _) => LoadPreset((string)_preset.SelectedItem!);
        top.Controls.AddRange([title, _learn, hint, _preset, load]);
        top.Resize += (_, _) => { int r = top.ClientSize.Width - 16; load.Left = r - load.Width; _preset.Left = load.Left - 8 - _preset.Width; };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Theme.Background };
        var shiftLabel = new Label { Text = "Shift button (hold):", AutoSize = true, Location = new Point(16, 18), ForeColor = Theme.Muted };
        StyleCombo(_shiftButton); _shiftButton.SetBounds(150, 14, 150, 26);
        _shiftButton.Items.Add("(none: LB+RB only)");
        foreach (var i in Inputs) if (!i.Axis) _shiftButton.Items.Add(i.Key);
        int si = string.IsNullOrWhiteSpace(_config.ShiftButton) ? 0 : _shiftButton.Items.IndexOf(_config.ShiftButton);
        _shiftButton.SelectedIndex = Math.Max(0, si);
        var ok = new StudioButton { Text = "SAVE + USE NOW", Accent = Theme.Cyan, DialogResult = DialogResult.OK };
        var cancel = new StudioButton { Text = "CANCEL", DialogResult = DialogResult.Cancel };
        ok.Size = new Size(170, 34); cancel.Size = new Size(110, 34);
        bottom.Controls.AddRange([shiftLabel, _shiftButton, ok, cancel]);
        bottom.Resize += (_, _) => { int r = bottom.ClientSize.Width - 16; ok.Location = new Point(r - ok.Width, 11); cancel.Location = new Point(ok.Left - 8 - cancel.Width, 11); };
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) => Commit();

        _table.Dock = DockStyle.Fill; _table.AutoScroll = true; _table.BackColor = Theme.Surface; _table.Padding = new Padding(12, 6, 24, 6);
        _table.ColumnCount = 5;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        void Head(string t, int col) => _table.Controls.Add(new Label { Text = t, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, true), AutoSize = true, Margin = new Padding(3, 8, 3, 4) }, col, 0);
        _table.RowCount = Inputs.Length + 1;
        Head("INPUT", 0); Head("ACTION", 1); Head("DECK", 2); Head("WITH SHIFT HELD", 3); Head("DECK", 4);
        int rowIndex = 1;
        foreach (var input in Inputs)
        {
            var row = new Row { Input = input };
            row.Label = new Label { Text = input.Name, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Padding = new Padding(6, 0, 0, 0), Margin = new Padding(0, 2, 6, 2) };
            row.Normal = MakeCell(input, shift: false);
            row.Shift = MakeCell(input, shift: true);
            _table.Controls.Add(row.Label, 0, rowIndex);
            _table.Controls.Add(row.Normal.Action, 1, rowIndex); _table.Controls.Add(row.Normal.Deck, 2, rowIndex);
            _table.Controls.Add(row.Shift.Action, 3, rowIndex); _table.Controls.Add(row.Shift.Deck, 4, rowIndex);
            _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            _rows.Add(row); rowIndex++;
        }
        Controls.Add(_table); Controls.Add(bottom); Controls.Add(top);
        LoadFrom(_config.GamepadButtons, _config.ShiftButtons, _config.GamepadAxes, _config.ShiftAxes);
        _poll.Tick += (_, _) => PollLearn();
        Shown += (_, _) => _poll.Start();
        FormClosed += (_, _) => { _poll.Stop(); _poll.Dispose(); };
    }

    private static void StyleCombo(ComboBox c)
    {
        c.DropDownStyle = ComboBoxStyle.DropDownList; c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Theme.Raised; c.ForeColor = Theme.Text; c.MaxDropDownItems = 24;
    }

    private Cell MakeCell(Input input, bool shift)
    {
        var cell = new Cell { Action = new ComboBox(), Deck = new ComboBox() };
        StyleCombo(cell.Action); StyleCombo(cell.Deck);
        cell.Action.Dock = DockStyle.Fill; cell.Deck.Dock = DockStyle.Fill;
        cell.Action.Margin = new Padding(3, 3, 3, 3); cell.Deck.Margin = new Padding(3, 3, 12, 3);
        cell.Action.Items.Add(new Choice { Text = input.Axis && shift ? "(same as without Shift)" : "— nothing —" });
        foreach (var d in ActionCatalog.For(input.Axis)) cell.Action.Items.Add(new Choice { Def = d, Text = d.Label });
        cell.Deck.Items.AddRange(["Focused deck", "Deck A", "Deck B"]);
        cell.Deck.SelectedIndex = 0;
        cell.Action.SelectedIndexChanged += (_, _) => cell.Deck.Enabled = (cell.Action.SelectedItem as Choice)?.Def?.Scope == ActionScope.Deck;
        cell.Action.AccessibleName = $"{input.Name}{(shift ? " with Shift" : "")}: action";
        cell.Deck.AccessibleName = $"{input.Name}{(shift ? " with Shift" : "")}: deck";
        return cell;
    }

    private static void ShowCell(Cell cell, string? action, bool axis)
    {
        var (def, deck) = ActionCatalog.Parse(action, axis);
        if (def != null)
        {
            foreach (var item in cell.Action.Items)
                if (item is Choice c && c.Def == def) { cell.Action.SelectedItem = c; break; }
            cell.Deck.SelectedIndex = deck;
        }
        else if (!string.IsNullOrWhiteSpace(action))
        {
            var raw = new Choice { Raw = action, Text = $"⚠ {action} (not a MixDock action: does nothing)" };
            cell.Action.Items.Add(raw); cell.Action.SelectedItem = raw; cell.Deck.SelectedIndex = 0;
        }
        else { cell.Action.SelectedIndex = 0; cell.Deck.SelectedIndex = 0; }
        cell.Deck.Enabled = def?.Scope == ActionScope.Deck;
    }

    private static string? ReadCell(Cell cell)
    {
        if (cell.Action.SelectedItem is not Choice c) return null;
        if (c.Raw != null) return c.Raw;
        return c.Def is null ? null : ActionCatalog.Compose(c.Def, Math.Max(0, cell.Deck.SelectedIndex));
    }

    private void LoadFrom(IReadOnlyDictionary<string, string> buttons, IReadOnlyDictionary<string, string> shiftButtons,
        IReadOnlyDictionary<string, string> axes, IReadOnlyDictionary<string, string> shiftAxes)
    {
        foreach (var r in _rows)
        {
            var normal = r.Input.Axis ? axes : buttons; var shift = r.Input.Axis ? shiftAxes : shiftButtons;
            ShowCell(r.Normal, normal.GetValueOrDefault(r.Input.Key), r.Input.Axis);
            ShowCell(r.Shift, shift.GetValueOrDefault(r.Input.Key), r.Input.Axis);
        }
    }

    private void LoadPreset(string name)
    {
        var tmp = new AppConfig(); tmp.ApplyPreset(name);
        LoadFrom(tmp.GamepadButtons, tmp.ShiftButtons, tmp.GamepadAxes, tmp.ShiftAxes);
        _shiftButton.SelectedIndex = Math.Max(0, _shiftButton.Items.IndexOf(tmp.ShiftButton));
        _learn.Text = $"Loaded the {name} preset. SAVE + USE NOW applies it.";
    }

    private void Commit()
    {
        Dictionary<string, string> b = new(), sb = new(), a = new(), sa = new();
        foreach (var r in _rows)
        {
            string? n = ReadCell(r.Normal), s = ReadCell(r.Shift);
            if (n != null) (r.Input.Axis ? a : b)[r.Input.Key] = n;
            if (s != null) (r.Input.Axis ? sa : sb)[r.Input.Key] = s;
        }
        _config.GamepadButtons = b; _config.ShiftButtons = sb; _config.GamepadAxes = a; _config.ShiftAxes = sa;
        _config.ShiftButton = _shiftButton.SelectedIndex <= 0 ? "" : (string)_shiftButton.SelectedItem!;
        // the panic chord must stay reachable: View+Menu unless the user chose another shift button
        if (string.IsNullOrWhiteSpace(_config.PanicChord)) _config.PanicChord = "Back+Start";
    }

    // ---------------------------------------------------------------- learn: the controller picks the row
    private void PollLearn()
    {
        if (!Gamepad.Read(out var state)) { _learn.Text = "No controller connected. Plug it in (or turn it on) to use press-to-find."; _primed = false; return; }
        var frame = Gamepad.Process(state.Gamepad);
        var buttons = state.Gamepad.Buttons;
        double[] axis = { frame.LY, frame.LX, frame.RX, frame.RY, frame.LT, frame.RT };
        if (!_primed) { _prevButtons = buttons; Array.Copy(axis, _prevAxis, 6); _primed = true; _learn.Text = "Controller ready: press a button or move a stick to find it."; }
        var pressed = buttons & ~_prevButtons;
        var shiftBtn = PadMapper.Parse(_shiftButton.SelectedIndex <= 0 ? null : (string)_shiftButton.SelectedItem!) ?? 0;
        bool shiftHeld = (shiftBtn != 0 && (buttons & shiftBtn) == shiftBtn) || (buttons & (PadButton.LeftShoulder | PadButton.RightShoulder)) == (PadButton.LeftShoulder | PadButton.RightShoulder);
        foreach (PadButton b in Enum.GetValues<PadButton>())
        {
            if ((pressed & b) == 0) continue;
            bool asShift = shiftHeld && b != shiftBtn && b is not (PadButton.LeftShoulder or PadButton.RightShoulder);
            Light(b.ToString(), asShift);
        }
        for (int i = 0; i < 6; i++)
        {
            double v = Math.Abs(axis[i]);
            if (v > 0.6 && Math.Abs(_prevAxis[i]) <= 0.6)
            {
                // sticks: only the dominant direction counts, so a diagonal push finds the intended row
                int other = i switch { 0 => 1, 1 => 0, 2 => 3, 3 => 2, _ => -1 };
                if (other < 0 || v >= Math.Abs(axis[other])) Light(Inputs[i].Key, shiftHeld);
            }
        }
        _prevButtons = buttons; Array.Copy(axis, _prevAxis, 6);
        long now = Environment.TickCount64;
        foreach (var r in _rows)
        {
            bool lit = now < r.LitUntil;
            var back = lit ? Color.FromArgb(40, 92, 98) : Theme.Surface;
            if (r.Label.BackColor != back) r.Label.BackColor = back;
        }
    }

    private void Light(string key, bool shift)
    {
        var row = _rows.FirstOrDefault(r => r.Input.Key == key);
        if (row is null) return;
        row.LitUntil = Environment.TickCount64 + 1500;
        var cell = shift ? row.Shift : row.Normal;
        _table.ScrollControlIntoView(row.Label);
        if (!cell.Action.DroppedDown) cell.Action.Focus();
        _learn.Text = $"{row.Input.Name}{(shift ? " + SHIFT" : "")}: now {ActionCatalog.Label(ReadCell(cell), row.Input.Axis)}. Pick a new action with the mouse or arrow keys.";
    }
}
