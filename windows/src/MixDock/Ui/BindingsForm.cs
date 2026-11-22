using MixDock.Platform;
using MixDock.Settings;

namespace MixDock.Ui;

/// <summary>
/// Controller mapping editor: every Xbox input with its normal and Shift action, picked from the action catalog.
/// Find input identifies buttons and dominant stick/trigger motion; the D-pad navigates the editor.
/// Edits a copy; OK writes the four layers + Shift button back into the config. Nothing runs while it is open.
/// </summary>
public sealed partial class BindingsForm : Form
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
        new("Back", "View", false), new("Start", "Menu", false),
        // inputs XInput does not report (back paddles, extra top buttons), read through Windows.Gaming.Input; press to bind
        new("Extra1", "Extra 1 (paddle / extra)", false), new("Extra2", "Extra 2 (paddle / extra)", false),
        new("Extra3", "Extra 3 (paddle / extra)", false), new("Extra4", "Extra 4 (paddle / extra)", false),
        new("Extra5", "Extra 5", false), new("Extra6", "Extra 6", false), new("Extra7", "Extra 7", false), new("Extra8", "Extra 8", false),
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
    private static void StyleCombo(ComboBox c)
    {
        c.DropDownStyle = ComboBoxStyle.DropDownList; c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Theme.Raised; c.ForeColor = Theme.Text; c.MaxDropDownItems = 24;
        c.DrawMode = DrawMode.OwnerDrawFixed;
        c.DrawItem += (_, e) =>
        {
            bool selected = e.State.HasFlag(DrawItemState.Selected) && !e.State.HasFlag(DrawItemState.ComboBoxEdit);
            using var fill = new SolidBrush(selected ? Theme.Cyan : Theme.Raised);
            e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index < 0) return;
            string label = c.GetItemText(c.Items[e.Index]) ?? "";
            var text = Rectangle.Inflate(e.Bounds, -3, 0);
            TextRenderer.DrawText(e.Graphics, label, e.Font, text, selected ? Theme.Background : c.Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        };
    }

    private Cell MakeCell(Input input, bool shift)
    {
        var cell = new Cell { Action = new ComboBox(), Deck = new ComboBox() };
        StyleCombo(cell.Action); StyleCombo(cell.Deck);

        cell.Action.Margin = new Padding(3, 3, 3, 3); cell.Deck.Margin = new Padding(3, 3, 12, 3);
        cell.Action.Items.Add(new Choice { Text = input.Axis && shift ? "(same as without Shift)" : "— nothing —" });
        foreach (var d in ActionCatalog.For(input.Axis).Where(MatchesSearch))
            cell.Action.Items.Add(new Choice { Def = d, Text = d.Label });
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
            if (cell.Action.SelectedIndex < 0)
            {
                var current = new Choice { Def = def, Text = def.Label };
                cell.Action.Items.Insert(1, current);
                cell.Action.SelectedItem = current;
            }
            cell.Deck.SelectedIndex = deck;
        }
        else if (!string.IsNullOrWhiteSpace(action))
        {
            var raw = new Choice { Raw = action, Text = $"⚠ {action} (not a HARP//MXR action: does nothing)" };
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


    private readonly AppConfig _config;
    private readonly AppConfig _draftConfig = new();
    private readonly ComboBox _shiftButton = new(), _preset = new();
    private readonly ListBox _inputs = new();
    private readonly Label _selection = new(), _status = new(), _hints = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 25 };
    private readonly List<(Control Control, Rectangle Design, float Size, bool Bold)> _positions = new();
    private readonly Dictionary<(float Size, bool Bold), Font> _fonts = new();
    private readonly Dictionary<(string Key, bool Shift), string?> _draft = new();
    private Cell? _normal, _shift;
    private int _displayed = -1;
    private ComboBox? _field, _editing;
    private int _originalIndex;
    private bool _finding;
    private PadFrame? _previous;
    private float _scale;
    private readonly StudioButton _save = new() { Text = "Save and use", Accent = Theme.Cyan, DialogResult = DialogResult.OK };
    private readonly StudioButton _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

    private readonly PadRole _role;

    /// <param name="role">Which controller this editor maps: Find input listens to that pad only.</param>
    public BindingsForm(AppConfig config, PadRole role = PadRole.Mixer)
    {
        _config = config; _role = role;
        _draftConfig.CopyProfilesFrom(config);
        Text = role == PadRole.FxPad ? "HARP//MXR · Controller 2 (FX pad) mappings" : "HARP//MXR · Controller mappings";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(620, 395);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        DoubleBuffered = true;

        AddLabel(role == PadRole.FxPad ? "Controller 2 · FX pad" : "Controller mappings", 20, 12, 410, 46, 24, true).ForeColor = role == PadRole.FxPad ? Theme.Amber : Theme.Text;
        AddLabel("PROFILE", 477, 18, 78, 34, 12, true);
        StyleCombo(_preset);
        _preset.AccessibleName = "Mapping profile";
        _preset.Enter += (_, _) => SetField(_preset, false);
        Place(_preset, 555, 21, 215, 34);
        var load = new StudioButton { Text = "Load profile", Accent = Theme.Amber };
        load.Click += (_, _) => LoadProfile();
        Place(load, 786, 14, 192, 42);

        AddLabel("CONTROLLER INPUT", 20, 83, 240, 24, 13, true);
        _inputs.AccessibleName = "Controller inputs";
        _inputs.BackColor = Theme.Surface;
        _inputs.ForeColor = Theme.Text;
        _inputs.BorderStyle = BorderStyle.None;
        _inputs.DrawMode = DrawMode.OwnerDrawFixed;
        _inputs.IntegralHeight = false;
        _inputs.DrawItem += DrawInput;
        foreach (var input in Inputs) _inputs.Items.Add(input.Name);
        _inputs.SelectedIndexChanged += (_, _) => { if (_displayed != _inputs.SelectedIndex) ShowInput(); };
        Place(_inputs, 20, 119, 234, 379);

        Place(_selection, 280, 76, 692, 44, 22, true);
        AddLabel("Find action", 280, 116, 112, 27, 13);
        _search.AccessibleName = "Search controller actions by name";
        _search.BackColor = Theme.Surface;
        _search.ForeColor = Theme.Text;
        _search.TextChanged += (_, _) => ShowInput();
        Place(_search, 395, 116, 363, 27);
        AddLabel("NORMAL", 280, 144, 230, 24, 13, true).ForeColor = Theme.Cyan;
        AddLabel("WITH SHIFT HELD", 280, 245, 230, 24, 13, true).ForeColor = Theme.Amber;
        AddLabel("Shift button", 280, 355, 188, 32);
        StyleCombo(_shiftButton);
        _shiftButton.AccessibleName = "Shift button";
        _shiftButton.Items.Add("(chord only)");
        foreach (var input in Inputs.Where(i => !i.Axis)) _shiftButton.Items.Add(input.Key);
        _shiftButton.Enter += (_, _) => SetField(_shiftButton, false);
        Place(_shiftButton, 475, 355, 242, 34);
        _modifierLine = AddLabel("", 280, 388, 699, 29, 14);
        var find = new StudioButton { Text = "Find input", Accent = Theme.Cyan };
        find.Click += (_, _) => BeginFind();
        Place(find, 280, 421, 124, 41);
        Place(_status, 420, 412, 557, 63);
        _status.ForeColor = Theme.Muted;
        InstallProfileCommands();

        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        _save.Click += (_, _) => Commit();
        AcceptButton = _save; CancelButton = _cancel;
        Place(_cancel, 689, 516, 128, 42);
        Place(_save, 829, 516, 149, 42, 16, true);
        Place(_hints, 20, 577, 958, 52, 15, true);
        _hints.BackColor = Theme.Surface;
        _hints.ForeColor = Theme.Text;
        _hints.TextAlign = ContentAlignment.MiddleCenter;

        RefreshProfiles();
        ReadDraft(_draftConfig);
        _inputs.SelectedIndex = 0;
        _status.Text = "Choose an input to edit both layers.";
        _poll.Tick += (_, _) =>
        {
            if (Gamepad.ReadFrame(_role, out var frame, out _)) ProcessFrame(frame);
            else _previous = null;
        };
        Shown += (_, _) => { _normal!.Action.Focus(); if (!_verification) _poll.Start(); };
        Resize += (_, _) => LayoutEditor();
        LayoutEditor();
    }

    private void Place(Control c, int x, int y, int w, int h, float size = 16, bool bold = false)
    {
        Controls.Add(c);
        _positions.Add((c, new Rectangle(x, y, w, h), size, bold));
    }

    private Label AddLabel(string text, int x, int y, int w, int h, float size = 16, bool bold = false)
    {
        var label = new Label { Text = text, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted };
        Place(label, x, y, w, h, size, bold);
        return label;
    }

    private void LayoutEditor()
    {
        float scale = Math.Max(.2f, Math.Min(ClientSize.Width / 1000f, ClientSize.Height / 640f));
        var oldFonts = scale != _scale ? _fonts.Values.ToArray() : [];
        if (oldFonts.Length > 0) _fonts.Clear();
        _scale = scale;
        int x = (int)((ClientSize.Width - 1000 * scale) / 2), y = (int)((ClientSize.Height - 640 * scale) / 2);
        foreach (var (c, r, size, bold) in _positions)
        {
            var key = (size, bold);
            if (!_fonts.TryGetValue(key, out var font))
                _fonts[key] = font = new Font("Segoe UI", Math.Max(8, size * scale), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            if (c.Font != font) c.Font = font;
            if (c is ComboBox combo) combo.ItemHeight = font.Height + Math.Max(2, (int)(3 * scale));
            c.SetBounds(x + (int)(r.X * scale), y + (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale));
        }
        int rowHeight = Math.Max(15, (int)(32 * scale));
        if (_inputs.ItemHeight != rowHeight) _inputs.ItemHeight = rowHeight;
        foreach (var font in oldFonts) font.Dispose();
        Invalidate();
    }

    protected override void OnLoad(EventArgs e)
    {
        if (!_verification && Owner != null)
        {
            var area = Screen.FromControl(Owner).WorkingArea;
            float fit = Math.Min(DeviceDpi / 96f, Math.Min((area.Width - 32) / 1000f, (area.Height - 64) / 640f));
            ClientSize = new Size((int)(1000 * fit), (int)(640 * fit));
            CenterToParent();
        }
        base.OnLoad(e);
    }

    private void DrawInput(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = e.Index == _inputs.SelectedIndex;
        using var fill = new SolidBrush(selected ? Theme.Raised : Theme.Surface);
        e.Graphics.FillRectangle(fill, e.Bounds);
        if (selected)
        {
            using var rail = new SolidBrush(Theme.Cyan);
            e.Graphics.FillRectangle(rail, e.Bounds.X, e.Bounds.Y, Math.Max(2, (int)(3 * _scale)), e.Bounds.Height);
        }
        var text = Rectangle.Inflate(e.Bounds, -(int)(11 * _scale), 0);
        TextRenderer.DrawText(e.Graphics, Inputs[e.Index].Name, _inputs.Font, text, selected ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_field is null || !_field.Enabled) return;
        using var pen = new Pen(_editing != null ? Theme.Amber : Theme.Cyan, Math.Max(2, 2 * _scale));
        e.Graphics.DrawRectangle(pen, Rectangle.Inflate(_field.Bounds, 3, 3));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _poll.Dispose();
        base.Dispose(disposing);
        if (disposing) foreach (var font in _fonts.Values) font.Dispose();
    }

    private void ReadDraft(AppConfig source)
    {
        foreach (var input in Inputs)
        {
            _draft[(input.Key, false)] = (input.Axis ? source.GamepadAxes : source.GamepadButtons).GetValueOrDefault(input.Key);
            _draft[(input.Key, true)] = (input.Axis ? source.ShiftAxes : source.ShiftButtons).GetValueOrDefault(input.Key);
        }
        int shiftIndex = _shiftButton.Items.IndexOf(source.ShiftButton);
        if (shiftIndex < 0 && !string.IsNullOrWhiteSpace(source.ShiftButton)) shiftIndex = _shiftButton.Items.Add(source.ShiftButton);   // e.g. "Back,Extra1": either button
        _shiftButton.SelectedIndex = Math.Max(0, shiftIndex);
        _modifierLine.Text = $"Shift chord  {Chord(source.ShiftChord)}     ·     Kill audio  {Chord(source.PanicChord)}  ({source.PanicHoldMs} ms)";
    }

    private void StoreRow()
    {
        if (_displayed < 0 || _normal == null || _shift == null) return;
        _draft[(Inputs[_displayed].Key, false)] = ReadCell(_normal);
        _draft[(Inputs[_displayed].Key, true)] = ReadCell(_shift);
    }

    private void ShowInput(bool saveCurrent = true)
    {
        if (_inputs.SelectedIndex < 0) return;
        if (saveCurrent) StoreRow();
        int field = Math.Max(0, Array.IndexOf(Fields(), _field));
        foreach (var old in new[] { _normal, _shift }.OfType<Cell>())
        foreach (var c in new[] { old.Action, old.Deck })
        {
            _positions.RemoveAll(p => p.Control == c);
            c.Dispose();
        }
        _editing = null;
        _editChoices.Visible = false;
        var input = Inputs[_displayed = _inputs.SelectedIndex];
        _selection.Text = input.Name;
        _selection.TextAlign = ContentAlignment.MiddleLeft;
        _normal = BuildLayer(false, 178);
        _shift = BuildLayer(true, 279);
        LayoutEditor();
        SetField(Fields()[field].Enabled ? Fields()[field] : _normal.Action, false);

        Cell BuildLayer(bool shifted, int y)
        {
            var cell = MakeCell(input, shifted);
            Place(cell.Action, 280, y, 477, 38);
            Place(cell.Deck, 773, y, 205, 38);
            ShowCell(cell, _draft[(input.Key, shifted)], input.Axis);
            cell.Action.SelectedIndexChanged += (_, _) => WarnConflict();
            cell.Deck.SelectedIndexChanged += (_, _) => WarnConflict();
            cell.Action.Enter += (_, _) => SetField(cell.Action, false);
            cell.Deck.Enter += (_, _) => SetField(cell.Deck, false);
            return cell;
        }
    }

    private ComboBox[] Fields() => _normal == null || _shift == null ? [] : [_normal.Action, _normal.Deck, _shift.Action, _shift.Deck];

    private void LoadProfile()
    {
        if (_preset.SelectedItem is not string name) return;
        WriteDraft();
        _draftConfig.SwitchProfile(name);
        ReadDraft(_draftConfig);
        RefreshProfiles();
        ShowInput(false);
        _status.ForeColor = Theme.Muted;
        _status.Text = $"{name} selected. Save to apply.";
    }

    private void Commit()
    {
        WriteDraft();
        _config.CopyProfilesFrom(_draftConfig);
    }

    private void WriteDraft()
    {
        StoreRow();
        foreach (var input in Inputs)
        foreach (bool shifted in new[] { false, true })
        {
            var bindings = input.Axis ? (shifted ? _draftConfig.ShiftAxes : _draftConfig.GamepadAxes)
                                      : (shifted ? _draftConfig.ShiftButtons : _draftConfig.GamepadButtons);
            if (_draft[(input.Key, shifted)] is string action) bindings[input.Key] = action;
            else bindings.Remove(input.Key);
        }
        _draftConfig.ShiftButton = _shiftButton.SelectedIndex <= 0 ? "" : (string)_shiftButton.SelectedItem!;
        if (string.IsNullOrWhiteSpace(_draftConfig.PanicChord)) _draftConfig.PanicChord = "Back+Start";
    }
}
