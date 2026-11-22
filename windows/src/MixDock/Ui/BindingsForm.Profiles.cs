using MixDock.Platform;

namespace MixDock.Ui;

public sealed partial class BindingsForm
{
    private readonly TextBox _search = new();
    private readonly ComboBox _profileCommand = new();
    private readonly ListBox _editChoices = new()
    {
        Visible = false, BackColor = Theme.Raised, ForeColor = Theme.Text,
        IntegralHeight = false, AccessibleName = "Controller action choices"
    };
    private Label _modifierLine = null!;
    private bool _warning;
    private readonly Panel _textPanel = new() { Visible = false, BackColor = Theme.Raised, Padding = new Padding(12) };
    private readonly Label _textTitle = new() { Dock = DockStyle.Top, Height = 27 };
    private readonly TextBox _textValue = new() { Dock = DockStyle.Top, MaxLength = 32 };
    private readonly Label _alphabet = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private Action<string>? _acceptText;
    private int _letter;
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -";
    private static readonly string[] Commands = ["New profile", "Rename profile", "Duplicate profile", "Delete profile", "Reset to Default"];

    private static string Chord(string s) => s.Replace("LeftShoulder", "LB").Replace("RightShoulder", "RB")
        .Replace("Back", "View").Replace("Start", "Menu");

    private bool MatchesSearch(ActionDef action) => _search.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .All(word => action.Label.Contains(word, StringComparison.OrdinalIgnoreCase) || action.Id.Contains(word, StringComparison.OrdinalIgnoreCase));

    private void InstallProfileCommands()
    {
        StyleCombo(_profileCommand);
        _profileCommand.AccessibleName = "Controller profile command";
        _profileCommand.Items.AddRange(Commands);
        _profileCommand.SelectedIndex = 0;
        Place(_profileCommand, 280, 476, 286, 32);
        _profileCommand.Enter += (_, _) => SetField(_profileCommand, false);
        var run = new StudioButton { Text = "Apply command", Accent = Theme.Amber };
        run.Click += (_, _) => RunProfileCommand();
        Place(run, 581, 476, 170, 33);
        _editChoices.SelectedIndexChanged += (_, _) =>
        {
            if (_editing != null && _editChoices.Visible && _editChoices.SelectedIndex >= 0)
                _editing.SelectedIndex = _editChoices.SelectedIndex;
        };
        _editChoices.DoubleClick += (_, _) => { if (_editing != null) EndEdit(true); };
        Place(_editChoices, 280, 217, 697, 288);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 39, FlowDirection = FlowDirection.RightToLeft };
        var apply = new StudioButton { Text = "Apply", Accent = Theme.Cyan, Width = 100, Height = 32 };
        var cancel = new StudioButton { Text = "Back", Width = 100, Height = 32 };
        apply.Click += (_, _) => EndText(true);
        cancel.Click += (_, _) => EndText(false);
        buttons.Controls.AddRange([apply, cancel]);
        _textPanel.Controls.AddRange([_alphabet, buttons, _textValue, _textTitle]);
        _textValue.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            e.SuppressKeyPress = true;
            EndText(e.KeyCode == Keys.Enter);
        };
        Place(_textPanel, 275, 147, 710, 323, 18);
    }

    private void ShowEditChoices(ComboBox field)
    {
        _editChoices.Items.Clear();
        _editChoices.Items.AddRange(field.Items.Cast<object>().ToArray());
        _editChoices.SelectedIndex = field.SelectedIndex;
        _editChoices.Visible = true;
        _editChoices.BringToFront();
    }

    private void RefreshProfiles()
    {
        _preset.Items.Clear();
        _preset.Items.AddRange(_draftConfig.ProfileNames.ToArray());
        _preset.SelectedItem = _draftConfig.ControllerProfileName;
    }

    private void RunProfileCommand()
    {
        WriteDraft();
        int choice = _profileCommand.SelectedIndex;
        if (choice < 0) return;
        if (choice is 1 or 3 && !_draftConfig.CanModifyProfileName)
        {
            SetEditorStatus("Factory profiles stay available. Duplicate this profile to name your own copy.", warning: true);
            return;
        }
        if (choice < 3)
        {
            string initial = choice == 1 ? _draftConfig.ControllerProfileName : "";
            BeginText(Commands[choice], initial, name =>
            {
                bool changed = choice == 1 ? _draftConfig.RenameProfile(name) : _draftConfig.AddProfile(name, choice == 2);
                if (changed) ReloadProfile($"{_draftConfig.ControllerProfileName} selected. Save to apply.");
                else SetEditorStatus("Enter a unique name of 1–32 characters.", warning: true);
            });
        }
        else if (choice == 3)
        {
            _draftConfig.DeleteProfile();
            ReloadProfile("Profile deleted from the draft. Save to apply.");
        }
        else
        {
            _draftConfig.ResetProfile();
            ReloadProfile("Default bindings restored in this draft. Save to apply.");
        }
    }

    private void ReloadProfile(string message)
    {
        ReadDraft(_draftConfig);
        RefreshProfiles();
        ShowInput(false);
        SetEditorStatus(message);
    }

    private void SetEditorStatus(string message, bool warning = false)
    {
        _status.Text = message;
        _status.ForeColor = warning ? Theme.Amber : Theme.Muted;
    }

    private void WarnConflict()
    {
        if (_displayed < 0 || _normal == null || _shift == null) return;
        bool shift = _field == _shift.Action || _field == _shift.Deck;
        var selected = Inputs[_displayed];
        string? action = ReadCell(shift ? _shift : _normal);
        var duplicate = action == null ? null : Inputs.FirstOrDefault(input => input != selected &&
            (string.Equals(_draft[(input.Key, shift)], action, StringComparison.Ordinal) ||
            shift && input.Axis && _draft[(input.Key, true)] == null && _draft[(input.Key, false)] == action));
        if (duplicate != null)
        {
            _warning = true;
            SetEditorStatus($"Also mapped to {duplicate.Name} in this layer.", warning: true);
        }
        else if (_warning)
        {
            _warning = false;
            SetEditorStatus("");
        }
    }

    private void BeginText(string title, string initial, Action<string> accept)
    {
        _acceptText = accept;
        _textTitle.Text = title;
        _textValue.AccessibleName = title;
        _textValue.Text = initial;
        _letter = 0;
        UpdateAlphabet();
        _textPanel.Visible = true;
        _textPanel.BringToFront();
        _textValue.Focus();
        _textValue.SelectAll();
        UpdateHints();
    }

    private void EndText(bool accept)
    {
        var action = _acceptText;
        _acceptText = null;
        _textPanel.Visible = false;
        if (accept) action?.Invoke(_textValue.Text);
        if (_field is { IsDisposed: false }) _field.Focus();
        UpdateHints();
    }

    private void EnterText(PadButton pressed, int horizontal, int vertical)
    {
        if (pressed.HasFlag(PadButton.B)) { EndText(false); return; }
        if (pressed.HasFlag(PadButton.Start)) { EndText(true); return; }
        if (pressed.HasFlag(PadButton.A)) _textValue.SelectedText = Letters[_letter].ToString();
        else if (pressed.HasFlag(PadButton.X))
        {
            if (_textValue.SelectionLength == 0 && _textValue.SelectionStart > 0) _textValue.Select(_textValue.SelectionStart - 1, 1);
            _textValue.SelectedText = "";
        }
        else
        {
            _letter = (_letter + horizontal + vertical * 8 + Letters.Length) % Letters.Length;
            UpdateAlphabet();
        }
    }

    private void UpdateAlphabet() => _alphabet.Text = string.Join(Environment.NewLine,
        Enumerable.Range(0, Letters.Length).Chunk(8).Select(row => string.Join("  ", row.Select(i =>
            i == _letter ? $"[{(Letters[i] == ' ' ? '␣' : Letters[i])}]" : $" {(Letters[i] == ' ' ? '␣' : Letters[i])} "))));
}
