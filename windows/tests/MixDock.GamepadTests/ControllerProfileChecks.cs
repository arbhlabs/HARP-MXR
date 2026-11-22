using System.Text.Json;
using MixDock.Platform;
using MixDock.Settings;

internal static class ControllerProfileChecks
{
    public static void Run(Action<bool, string> check)
    {
        var config = AppConfig.FromJson("{}");
        check(config.ProfileNames.Take(3).SequenceEqual(["Default", "Classic", "Live DJ"]), "three requested profiles ship");
        check(config.SwitchProfile("Classic") && config.GamepadAxes["LeftThumbX"] == "Crossfader"
            && config.GamepadButtons["Left"] == "Focus.MidKill", "Classic restores the 1.4.0 layout");
        check(config.SwitchProfile("Live DJ") && config.GamepadButtons["A"] == "Focus.BassCut", "Live DJ restores BassCut");
        check(config.AddProfile("Set 1", duplicate: true) && config.GamepadButtons["A"] == "Focus.BassCut", "duplicate copies active bindings");
        config.GamepadButtons["A"] = "Focus.Cue";
        check(config.RenameProfile("Club") && !config.AddProfile("club", duplicate: false), "rename and collision checks");
        check(config.SwitchProfile("Live DJ") && config.GamepadButtons["A"] == "Focus.BassCut", "edited duplicate does not change original");
        check(config.SwitchProfile("Club") && config.GamepadButtons["A"] == "Focus.Cue", "switch restores edited bindings");
        var restored = AppConfig.FromJson(JsonSerializer.Serialize(config));
        check(restored.ControllerProfileName == "Club" && restored.GamepadButtons["A"] == "Focus.Cue", "active profile and latest binding survive JSON");
        check(restored.AddProfile("New set", duplicate: false) && restored.GamepadButtons["A"] == "Focus.LowKill", "new profile starts at Default");
        check(restored.DeleteProfile() && !restored.ProfileNames.Contains("New set") && restored.ControllerProfileName == "Default", "deleting active profile selects Default");
        check(!restored.DeleteProfile() && !restored.RenameProfile("Other"), "factory names stay available");
        check(restored.SwitchProfile("Club"), "custom profile remains available");
        restored.ResetProfile();
        check(restored.GamepadButtons["A"] == "Focus.LowKill", "reset restores Default mappings");
        var old = AppConfig.FromJson("""{"ConfigVersion":7,"GamepadButtons":{"A":"Focus.Cue","FutureInput":"future"}}""");
        check(old.ControllerProfileName == "Imported" && old.GamepadButtons["FutureInput"] == "future", "legacy custom mappings are imported intact");
        check(old.SwitchProfile("Default") && old.SwitchProfile("Imported") && old.GamepadButtons["A"] == "Focus.Cue", "imported profile survives switching");

        var m = new AppConfig().CreatePadMapper();
        var view = PadButton.Back; var lb = PadButton.LeftShoulder; var rb = PadButton.RightShoulder;
        m.Update(view, 100);
        check(m.Update(view | rb, 150).Count == 0, "View+RB waits to distinguish tap and hold");
        var tap = m.Update(view, 230);
        check(tap.Count == 1 && tap[0].Kind == PadEventKind.NextProfile, "View+RB tap advances once");
        check(m.Update(0, 250).Count == 0, "profile switch suppresses View BPM tap");
        m.Update(view | lb, 400);
        var held = m.Update(view | lb, 800);
        check(held.Count == 1 && held[0].Action == "QuantCycle" && held[0].Shift, "held View+LB preserves shifted quantize");
        check(m.Update(0, 820).All(e => e.Kind == PadEventKind.Release), "held modifier does not switch");
        m.Reset(view | lb);
        check(m.Update(0, 900).Count == 0, "reconnect never switches while releasing a held chord");
        m.Update(view | lb, 980);
        check(m.Update(view, 1050).Single().Kind == PadEventKind.PreviousProfile, "View+LB tap cycles back");
        m.Reset(0);
        m.Update(view | rb, 1200);
        check(m.Update(view | rb | PadButton.Start, 1230).All(e => e.Kind != PadEventKind.NextProfile) &&
            m.Update(view | rb | PadButton.Start, 1760).Any(e => e.Kind == PadEventKind.Panic), "panic cancels an overlapping profile chord");
    }
}
