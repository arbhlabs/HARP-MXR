using MixDock.Platform;
using MixDock.Ui;

namespace MixDock;

public sealed partial class MainForm
{
    private readonly ComboBox _controllerProfile = new()
    {
        AccessibleName = "Active controller profile",
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text
    };

    private void InstallControllerProfileBox(DjLayout layout, int x, int y, int w, int h)
    {
        _controllerProfile.BackColor = Theme.Idle;
        _controllerProfile.DrawMode = DrawMode.OwnerDrawFixed;
        _controllerProfile.DrawItem += (_, e) =>
        {
            using var bg = new SolidBrush((e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0 ? Theme.Raised : Theme.Idle);
            e.Graphics.FillRectangle(bg, e.Bounds);
            string text = e.Index >= 0 ? _controllerProfile.Items[e.Index]?.ToString() ?? "" : "";
            TextRenderer.DrawText(e.Graphics, text, _controllerProfile.Font, e.Bounds, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        layout.Place(_controllerProfile, x, y, w, h, 8);
        _controllerProfile.SelectionChangeCommitted += (_, _) =>
        {
            if (_controllerProfile.SelectedItem is not string name) return;
            var buttons = !_preview && Gamepad.Read(out var state) ? state.Gamepad.Buttons : 0;
            SwitchControllerProfile(name, buttons);
        };
        RefreshControllerProfileBox();
    }

    private void RefreshControllerProfileBox()
    {
        _controllerProfile.Items.Clear();
        _controllerProfile.Items.AddRange(_config.ProfileNames.ToArray());
        _controllerProfile.SelectedItem = _config.ControllerProfileName;
    }

    private void CycleControllerProfile(int direction, PadButton buttons)
    {
        var names = _config.ProfileNames.ToArray();
        int index = Array.IndexOf(names, _config.ControllerProfileName);
        SwitchControllerProfile(names[(index + direction + names.Length) % names.Length], buttons);
    }

    private void SwitchControllerProfile(string name, PadButton buttons)
    {
        if (!_config.SwitchProfile(name)) return;
        ApplyBindings(buttons);
        ShowControllerProfile();
        RumblePulse(.18, 40);
    }

    private void ShowControllerProfile()
    {
        RefreshControllerProfileBox();
        AnnounceMove($"Controller profile · {_config.ControllerProfileName}");
    }
}
