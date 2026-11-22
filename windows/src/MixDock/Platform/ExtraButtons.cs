namespace MixDock.Platform;

/// <summary>
/// Extra controller buttons that XInput does not report (back paddles, extra top buttons), read through
/// Windows.Gaming.Input.RawGameController for the device behind an XInput slot (matched by vendor / product id).
/// Raw button i from <see cref="StandardCount"/> on is Extra(i - StandardCount + 1); a raw button that changes together
/// with an XInput button is a duplicate of a standard button (a firmware-mapped paddle, or a layout that lists the
/// face buttons later) and is ignored from then on, so nothing fires twice. UI thread only.
/// Whether a given pad exposes its paddles here depends on its firmware mode: see windows/docs/CONTROLLERS.md.
/// </summary>
public static class ExtraButtons
{
    public const int StandardCount = ExtraButtonLogic.StandardCount;
    private sealed class Device
    {
        public Windows.Gaming.Input.RawGameController Controller = null!;
        public bool[] Buttons = Array.Empty<bool>(), Previous = Array.Empty<bool>();
        public Windows.Gaming.Input.GameControllerSwitchPosition[] Switches = Array.Empty<Windows.Gaming.Input.GameControllerSwitchPosition>();
        public double[] Axes = Array.Empty<double>();
        public readonly HashSet<int> Aliases = new(), Confirmed = new();
        public PadButton LastXInput;
        public int PendingIndex = -1; public PadButton PendingXInput;
    }
    private static readonly Dictionary<string, Device> _byId = new(StringComparer.OrdinalIgnoreCase);
    private static bool _installed, _dirty = true;
    public static string LastError { get; private set; } = "";

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            Windows.Gaming.Input.RawGameController.RawGameControllerAdded += (_, _) => _dirty = true;
            Windows.Gaming.Input.RawGameController.RawGameControllerRemoved += (_, _) => _dirty = true;
            Gamepad.ExtraSource = Read;
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; Log.Write("extra buttons unavailable: " + LastError); }
    }

    private static void Refresh()
    {
        _dirty = false; _byId.Clear();
        foreach (var c in Windows.Gaming.Input.RawGameController.RawGameControllers)
        {
            string id = $"{c.HardwareVendorId:X4}:{c.HardwareProductId:X4}";
            if (_byId.ContainsKey(id) || c.ButtonCount <= StandardCount) continue;
            _byId[id] = new Device
            {
                Controller = c, Buttons = new bool[c.ButtonCount], Previous = new bool[c.ButtonCount],
                Switches = new Windows.Gaming.Input.GameControllerSwitchPosition[c.SwitchCount], Axes = new double[c.AxisCount]
            };
            Log.Write($"extra buttons: {c.DisplayName} {id} has {c.ButtonCount} raw buttons ({c.ButtonCount - StandardCount} beyond the standard {StandardCount})");
        }
    }

    /// <summary>Extra buttons held right now on the pad in XInput <paramref name="slot"/> with identity <paramref name="id"/>.</summary>
    public static PadButton Read(int slot, string id, PadButton xinput)
    {
        try
        {
            if (_dirty) Refresh();
            if (id.Length == 0 || !_byId.TryGetValue(id, out var dev)) return 0;
            dev.Controller.GetCurrentReading(dev.Buttons, dev.Switches, dev.Axes);
            return Classify(dev, xinput);
        }
        catch (Exception ex) { if (LastError.Length == 0) { LastError = ex.Message; Log.Write("extra buttons read: " + ex.Message); } return 0; }
    }

    private static PadButton Classify(Device dev, PadButton xinput)
        => ExtraButtonLogic.Classify(dev.Buttons, dev.Previous, dev.Aliases, dev.Confirmed, ref dev.PendingIndex, ref dev.LastXInput, xinput);



    /// <summary>Live raw state for the diagnostics view: "id: raw buttons pressed".</summary>
    public static string Describe()
    {
        try
        {
            if (_dirty) Refresh();
            if (_byId.Count == 0) return "No controller with more than the standard buttons is visible to Windows.Gaming.Input.";
            return string.Join("\n", _byId.Select(kv =>
            {
                kv.Value.Controller.GetCurrentReading(kv.Value.Buttons, kv.Value.Switches, kv.Value.Axes);
                var down = Enumerable.Range(0, kv.Value.Buttons.Length).Where(i => kv.Value.Buttons[i]).Select(i => i.ToString());
                return $"{kv.Key} {kv.Value.Controller.DisplayName}: {kv.Value.Buttons.Length} raw buttons; held now: [{string.Join(",", down)}]; ignored duplicates: [{string.Join(",", kv.Value.Aliases)}]";
            }));
        }
        catch (Exception ex) { return "Windows.Gaming.Input error: " + ex.Message; }
    }
}
