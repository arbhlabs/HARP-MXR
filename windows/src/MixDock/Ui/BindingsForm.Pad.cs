using MixDock.Platform;

namespace MixDock.Ui;

public sealed partial class BindingsForm
{
    private PadButton _repeatDirection;
    private long _repeatAt;
    private PadButton _learnModifier;

    private void SetField(ComboBox field, bool focus = true)
    {
        _field = field;
        if (focus) field.Focus();
        WarnConflict();
        UpdateHints();
        Invalidate();
    }

    private void UpdateHints()
    {
        _hints.Text = _acceptText != null ? "D-pad   Choose letter     [A]   Insert     [X]   Erase     [Menu]   Apply     [B]   Back"
            : _finding ? "Press a button or move a stick or trigger    ·    Hold Shift to find its second layer"
            : _editing == _preset ? "D-pad   Choose profile      [A]   Load into editor      [B]   Back"
            : _editing == _profileCommand ? "D-pad   Choose profile command      [A]   Apply      [B]   Back"
            : _editing != null ? "D-pad   Change value      [A]   Accept      [B]   Undo      [X]   Search actions"
            : "D-pad  Row / field    [A]  Edit    [B]  Cancel    [X]  Find input    [Y]  Profiles    [View]  Tools    [LB/RB]  Layer    [Menu]  Save";
    }

    private void BeginFind()
    {
        _finding = true;
        _editing = null;
        _learnModifier = 0;
        _status.Text = "Listening for controller input…";
        UpdateHints();
        Invalidate();
    }

    private void BeginEdit(ComboBox field)
    {
        _editing = field;
        _originalIndex = field.SelectedIndex;
        SetField(field);
        ShowEditChoices(field);
    }

    private void EndEdit(bool accept)
    {
        var edited = _editing!;
        if (!accept) edited.SelectedIndex = _originalIndex;
        _editing = null;
        _editChoices.Visible = false;
        if (edited == _preset && accept) LoadProfile();
        if (edited == _profileCommand && accept) RunProfileCommand();
        if (edited == _preset || edited == _profileCommand) SetField(_normal!.Action, _acceptText == null);
        else SetField(edited);
    }

    private void Find(PadFrame frame, PadFrame previous, PadButton pressed)
    {
        var shift = PadMapper.ParseAny(_shiftButton.SelectedItem as string);
        var chord = PadMapper.Parse(_draftConfig.ShiftChord) ?? 0;
        bool shifted = (shift != 0 && (frame.Buttons & shift) != 0) || (chord != 0 && frame.Buttons.HasFlag(chord));
        string? key = null;
        foreach (PadButton button in Enum.GetValues<PadButton>())
        {
            if ((pressed & button) == 0) continue;
            if ((button & (shift | chord)) != 0) { _learnModifier = button; continue; }
            key = button.ToString();
            break;
        }
        Span<double> axes = [frame.LY, frame.LX, frame.RX, frame.RY, frame.LT, frame.RT];
        Span<double> before = [previous.LY, previous.LX, previous.RX, previous.RY, previous.LT, previous.RT];
        double strongest = .6;
        for (int i = 0; key == null && i < axes.Length; i++)
        {
            if (Math.Abs(axes[i]) <= strongest || Math.Abs(before[i]) > .6) continue;
            int other = i switch { 0 => 1, 1 => 0, 2 => 3, 3 => 2, _ => -1 };
            if (other >= 0 && Math.Abs(axes[other]) > Math.Abs(axes[i])) continue;
            key = Inputs[i].Key;
        }
        if (key == null && _learnModifier != 0 && (frame.Buttons & _learnModifier) == 0)
        {
            key = _learnModifier.ToString();
            shifted = false;
        }
        if (key == null) return;
        _finding = false;
        _inputs.SelectedIndex = Array.FindIndex(Inputs, i => i.Key == key);
        SetField(shifted ? _shift!.Action : _normal!.Action);
        _status.Text = $"Found {Inputs[_displayed].Name} · {(shifted ? "Shift" : "Normal")}.";
    }

    internal void ProcessFrame(PadFrame frame)
    {
        var previous = _previous;
        _previous = frame;
        if (previous is null) return;
        var pressed = frame.Buttons & ~previous.Value.Buttons;
        if (_finding)
        {
            Find(frame, previous.Value, pressed);
            return;
        }
        var direction = frame.Buttons & (PadButton.Up | PadButton.Down | PadButton.Left | PadButton.Right);
        long now = Environment.TickCount64;
        if (_repeatDirection != direction)
        {
            _repeatDirection = direction;
            _repeatAt = now + 400;
        }
        else if (direction != 0 && now >= _repeatAt)
        {
            pressed |= direction;
            _repeatAt = now + 100;
        }
        if (pressed == 0) return;
        int horizontal = (pressed.HasFlag(PadButton.Right) ? 1 : 0) - (pressed.HasFlag(PadButton.Left) ? 1 : 0);
        int vertical = (pressed.HasFlag(PadButton.Down) ? 1 : 0) - (pressed.HasFlag(PadButton.Up) ? 1 : 0);
        if (_acceptText != null) { EnterText(pressed, horizontal, vertical); return; }
        if (_editing != null)
        {
            if (pressed.HasFlag(PadButton.B)) EndEdit(false);
            else if (pressed.HasFlag(PadButton.A)) EndEdit(true);
            else if (pressed.HasFlag(PadButton.X) && (_editing == _normal!.Action || _editing == _shift!.Action))
            {
                EndEdit(false);
                BeginText("Find action", _search.Text, value => _search.Text = value);
            }
            else
            {
                _editing.SelectedIndex = Math.Clamp(_editing.SelectedIndex + (vertical != 0 ? vertical : horizontal), 0, _editing.Items.Count - 1);
                _editChoices.SelectedIndex = _editing.SelectedIndex;
            }
            return;
        }
        if (pressed.HasFlag(PadButton.B)) { _cancel.PerformClick(); return; }
        if (pressed.HasFlag(PadButton.Start)) { _save.PerformClick(); return; }
        if (pressed.HasFlag(PadButton.X)) { BeginFind(); return; }
        if (pressed.HasFlag(PadButton.Y)) { BeginEdit(_preset); return; }
        if (pressed.HasFlag(PadButton.Back)) { BeginEdit(_profileCommand); return; }
        if (pressed.HasFlag(PadButton.LeftShoulder)) SetField(_normal!.Action);
        else if (pressed.HasFlag(PadButton.RightShoulder)) SetField(_shift!.Action);
        else if (pressed.HasFlag(PadButton.A)) BeginEdit(_field ?? _normal!.Action);
        else if (vertical != 0)
        {
            _inputs.SelectedIndex = Math.Clamp(_inputs.SelectedIndex + vertical, 0, Inputs.Length - 1);
            SetField(_field!);
        }
        else if (horizontal != 0)
        {
            var fields = Fields().Append(_shiftButton).Where(c => c.Enabled).ToArray();
            int pos = Array.IndexOf(fields, _field);
            SetField(fields[Math.Clamp(pos + horizontal, 0, fields.Length - 1)]);
        }
    }
}
