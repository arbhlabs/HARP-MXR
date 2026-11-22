using MixDock.Platform;
using MixDock.Settings;

namespace MixDock.Ui;

public sealed partial class BindingsForm
{
    private bool _verification;
    protected override bool ShowWithoutActivation => _verification;

    internal static void VerifyOffscreen(Form owner, string directory, Action<bool, string> check)
    {
        var config = new AppConfig();
        config.GamepadButtons["X"] = "custom-legacy-value";
        config.GamepadButtons["FutureInput"] = "future-action";
        using var dialog = new BindingsForm(config)
        {
            _verification = true,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            ShowInTaskbar = false,
            Opacity = 0
        };
        dialog.Show(owner);
        dialog._poll.Stop();
        dialog.ProcessFrame(default);
        void Tap(PadButton b)
        {
            dialog.ProcessFrame(default(PadFrame) with { Buttons = b });
            dialog.ProcessFrame(default);
        }
        Tap(PadButton.Down);
        check(dialog._displayed == 1, "Controller D-pad selects the next mapping row");
        Tap(PadButton.RightShoulder);
        check(dialog._field == dialog._shift!.Action, "RB focuses the Shift action");
        var action = dialog._field!;
        int initial = action.SelectedIndex;
        Tap(PadButton.A); Tap(PadButton.Down); Tap(PadButton.B);
        check(action.SelectedIndex == initial, "Controller B undoes a pending action choice");
        Tap(PadButton.X);
        dialog.ProcessFrame(default(PadFrame) with { LX = .9 });
        dialog.ProcessFrame(default);
        check(dialog._displayed == 1 && !dialog._finding, "Find input selects the moved axis");
        Tap(PadButton.X);
        dialog.ProcessFrame(default(PadFrame) with { Buttons = PadButton.Back });
        dialog.ProcessFrame(default(PadFrame) with { Buttons = PadButton.Back | PadButton.A });
        dialog.ProcessFrame(default);
        check(Inputs[dialog._displayed].Key == "A" && dialog._field == dialog._shift.Action, "Find input identifies A on the held shift layer");
        Tap(PadButton.LeftShoulder);
        var normal = dialog._normal!;
        Tap(PadButton.A); Tap(PadButton.Down); Tap(PadButton.A);
        string? expected = ReadCell(normal);
        check(expected != null && expected != config.GamepadButtons["A"], "Controller edit remains in the draft before save");
        Tap(PadButton.Start);
        check(config.GamepadButtons["A"] == expected && config.GamepadButtons["X"] == "custom-legacy-value" &&
            config.GamepadButtons["FutureInput"] == "future-action", "Menu saves edited binding and preserves unknown stored values");

        Tap(PadButton.Y); Tap(PadButton.Down); Tap(PadButton.A);
        var preset = new AppConfig(); preset.ApplyPreset((string)dialog._preset.SelectedItem!);
        check(ReadCell(dialog._normal!) == preset.GamepadButtons["A"], "Controller loads the second profile into the draft");
        check(config.GamepadButtons["A"] == expected, "Profile preview preserves active configuration");
        Tap(PadButton.RightShoulder);
        for (int i = 0; i < 4; i++) Tap(PadButton.Right);
        check(dialog._field == dialog._shiftButton, "Controller reaches the Shift button selector");
        Tap(PadButton.LeftShoulder);
        Tap(PadButton.A); Tap(PadButton.X); Tap(PadButton.Right); Tap(PadButton.Right); Tap(PadButton.A); Tap(PadButton.Start);
        check(dialog._search.Text == "C" && dialog._normal!.Action.Items.Cast<Choice>().Any(c => c.Def?.Id == "Cue") &&
            !dialog._normal.Action.Items.Cast<Choice>().Any(c => c.Def?.Id == "HighKill"), "Controller types a query and filters the action list");
        dialog._search.Clear();
        Tap(PadButton.Back); Tap(PadButton.Down); Tap(PadButton.Down); Tap(PadButton.A);
        Tap(PadButton.A); Tap(PadButton.Start);
        check(dialog._draftConfig.ControllerProfileName == "A" && dialog._preset.Text == "A" &&
            config.ControllerProfileName == "Default", "Controller duplicates and names a profile without modifying the active setup");
        Tap(PadButton.Back); Tap(PadButton.Down); Tap(PadButton.A);
        check(!dialog._draftConfig.ProfileNames.Contains("A") && dialog._preset.Text == "Default",
            "Controller can delete a custom profile and return to Default");
        Tap(PadButton.Back); Tap(PadButton.Down); Tap(PadButton.A);
        check(ReadCell(dialog._normal!) == AppConfig.DefaultButtons()["A"], "Controller reset restores Default bindings");
        using (var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height))
        {
            // Capture client controls, excluding the system-owned window border.
            using var full = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(full, new Rectangle(Point.Empty, full.Size));
            var origin = dialog.PointToScreen(Point.Empty) - (Size)dialog.Location;
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImageUnscaled(full, -origin.X, -origin.Y);
            bitmap.Save(Path.Combine(directory, "mappings.png"));
        }
        Tap(PadButton.B);
        check(dialog.DialogResult == DialogResult.Cancel && config.GamepadButtons["A"] == expected, "Controller can cancel a draft with B");
        dialog.Close();
    }
}
