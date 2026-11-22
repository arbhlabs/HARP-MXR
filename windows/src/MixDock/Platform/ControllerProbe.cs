using System.Runtime.InteropServices;
using System.Text;

namespace MixDock.Platform;

/// <summary>
/// Read-only controller inventory: XInput slots (with vendor/product from XInputGetCapabilitiesEx), Windows.Gaming.Input
/// raw game controllers (button / axis / switch counts) and Raw Input HID game devices (HID button and value caps).
/// Nothing is written to any device. Used for the "extra buttons" path (back paddles, extra top buttons) and for
/// stable controller identity. Safe to call from the UI thread; WinRT enumeration is asynchronous, so callers that
/// want the raw controllers wait briefly (see <see cref="Snapshot"/>).
/// </summary>
public static class ControllerProbe
{
    public sealed record XInputSlot(int Slot, bool Connected, ushort Vendor, ushort Product, ushort Version, byte SubType, ushort Buttons);
    public sealed record RawController(string Name, ushort Vendor, ushort Product, int Buttons, int Axes, int Switches, bool IsWireless, string Id);
    public sealed record HidDevice(string Path, ushort Vendor, ushort Product, ushort UsagePage, ushort Usage, int Buttons, int Values, string Product_);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilitiesEx
    {
        public byte Type, SubType; public ushort Flags;
        public XInputGamepad Gamepad; public XInputVibration Vibration;
        public ushort VendorId, ProductId, VersionNumber, Unknown1; public uint Unknown2;
    }
    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint XInputGetCapabilitiesEx(uint unknown, uint index, uint flags, out XInputCapabilitiesEx caps);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    private static extern uint XInputGetState(uint index, out XInputState state);

    /// <summary>Vendor/product of an XInput slot, or null (no pad / API missing). Cheap; no device access beyond XInput.</summary>
    public static (ushort Vendor, ushort Product)? SlotIdentity(int slot)
    {
        try
        {
            if (XInputGetCapabilitiesEx(1, (uint)slot, 0, out var caps) != 0) return null;
            return (caps.VendorId, caps.ProductId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public static List<XInputSlot> XInputSlots()
    {
        var list = new List<XInputSlot>();
        for (int i = 0; i < 4; i++)
        {
            try
            {
                bool on = XInputGetState((uint)i, out var st) == 0;
                ushort v = 0, p = 0, ver = 0; byte sub = 0;
                if (on && XInputGetCapabilitiesEx(1, (uint)i, 0, out var caps) == 0) { v = caps.VendorId; p = caps.ProductId; ver = caps.VersionNumber; sub = caps.SubType; }
                list.Add(new XInputSlot(i, on, v, p, ver, sub, on ? (ushort)st.Gamepad.Buttons : (ushort)0));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { list.Add(new XInputSlot(i, false, 0, 0, 0, 0, 0)); }
        }
        return list;
    }

    /// <summary>Windows.Gaming.Input raw controllers. The first call after process start can be empty for a moment
    /// (WinRT enumerates asynchronously), so this waits up to <paramref name="waitMs"/> for the list to fill.</summary>
    public static List<RawController> RawControllers(int waitMs = 600)
    {
        var list = new List<RawController>();
        try
        {
            var all = Windows.Gaming.Input.RawGameController.RawGameControllers;
            long until = Environment.TickCount64 + waitMs;
            while (all.Count == 0 && Environment.TickCount64 < until) { Thread.Sleep(50); all = Windows.Gaming.Input.RawGameController.RawGameControllers; }
            foreach (var c in all)
                list.Add(new RawController(c.DisplayName ?? "", c.HardwareVendorId, c.HardwareProductId, c.ButtonCount, c.AxisCount, c.SwitchCount, c.IsWireless, c.NonRoamableId ?? ""));
        }
        catch (Exception ex) { list.Add(new RawController("error: " + ex.GetType().Name + " " + ex.Message, 0, 0, 0, 0, 0, false, "")); }
        return list;
    }

    // ---- Raw Input HID inventory
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDeviceList { public IntPtr Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RidDeviceInfoHid { public uint Size, Type, VendorId, ProductId, VersionNumber; public ushort UsagePage, Usage; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HidpButtonCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort UsageMin;   // Range.UsageMin / NotRange.Usage
        [FieldOffset(58)] public ushort UsageMax;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, ref HidpCaps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int reportType, [Out] HidpButtonCaps[] caps, ref ushort length, IntPtr preparsed);
    private const uint RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b, RIDI_PREPARSEDDATA = 0x20000005;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    /// <summary>HID game devices (usage page 1, usage 4 joystick / 5 gamepad / 8 multi-axis) seen by Raw Input.</summary>
    public static List<HidDevice> HidGameDevices()
    {
        var list = new List<HidDevice>();
        try
        {
            uint count = 0, sz = (uint)Marshal.SizeOf<RawInputDeviceList>();
            if (GetRawInputDeviceList(null, ref count, sz) != 0 || count == 0) return list;
            var devs = new RawInputDeviceList[count];
            if (GetRawInputDeviceList(devs, ref count, sz) == uint.MaxValue) return list;
            foreach (var d in devs)
            {
                if (d.Type != 2) continue;   // RIM_TYPEHID
                uint isz = (uint)Marshal.SizeOf<RidDeviceInfoHid>() + 8;
                IntPtr info = Marshal.AllocHGlobal((int)isz);
                try
                {
                    Marshal.WriteInt32(info, (int)isz);
                    if (GetRawInputDeviceInfoW(d.Device, RIDI_DEVICEINFO, info, ref isz) == uint.MaxValue) continue;
                    var hid = Marshal.PtrToStructure<RidDeviceInfoHid>(info);
                    if (hid.UsagePage != 1 || hid.Usage is not (4 or 5 or 8)) continue;
                    string name = DeviceName(d.Device);
                    var (buttons, values) = HidCounts(d.Device);
                    list.Add(new HidDevice(name, (ushort)hid.VendorId, (ushort)hid.ProductId, hid.UsagePage, hid.Usage, buttons, values, ""));
                }
                finally { Marshal.FreeHGlobal(info); }
            }
        }
        catch (Exception ex) { list.Add(new HidDevice("error: " + ex.Message, 0, 0, 0, 0, 0, 0, "")); }
        return list;
    }

    private static string DeviceName(IntPtr device)
    {
        uint n = 0; GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, IntPtr.Zero, ref n);
        if (n == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)n * 2);
        try { GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, buf, ref n); return Marshal.PtrToStringUni(buf) ?? ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static (int Buttons, int Values) HidCounts(IntPtr device)
    {
        uint n = 0; GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref n);
        if (n == 0) return (-1, -1);
        IntPtr pre = Marshal.AllocHGlobal((int)n);
        try
        {
            if (GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, pre, ref n) == uint.MaxValue) return (-1, -1);
            var caps = new HidpCaps { Reserved = new ushort[17] };
            if (HidP_GetCaps(pre, ref caps) != HIDP_STATUS_SUCCESS) return (-1, -1);
            int buttons = 0;
            if (caps.NumberInputButtonCaps > 0)
            {
                ushort len = caps.NumberInputButtonCaps; var bc = new HidpButtonCaps[len];
                if (HidP_GetButtonCaps(0, bc, ref len, pre) == HIDP_STATUS_SUCCESS)
                    for (int i = 0; i < len; i++) buttons += bc[i].IsRange != 0 ? bc[i].UsageMax - bc[i].UsageMin + 1 : 1;
            }
            return (buttons, caps.NumberInputValueCaps);
        }
        finally { Marshal.FreeHGlobal(pre); }
    }

    /// <summary>Human-readable report of every interface (for the diagnostics log and docs).</summary>
    public static string Snapshot(int rawWaitMs = 600)
    {
        var sb = new StringBuilder();
        sb.AppendLine("XInput slots:");
        foreach (var s in XInputSlots())
            sb.AppendLine(s.Connected ? $"  slot {s.Slot}: VID {s.Vendor:X4} PID {s.Product:X4} ver {s.Version:X4} subtype {s.SubType}" : $"  slot {s.Slot}: empty");
        sb.AppendLine("Windows.Gaming.Input RawGameController:");
        var raw = RawControllers(rawWaitMs);
        if (raw.Count == 0) sb.AppendLine("  none");
        foreach (var r in raw)
            sb.AppendLine($"  {r.Name}  VID {r.Vendor:X4} PID {r.Product:X4}  buttons {r.Buttons}  axes {r.Axes}  switches {r.Switches}  wireless {r.IsWireless}");
        sb.AppendLine("Raw Input HID game devices:");
        var hid = HidGameDevices();
        if (hid.Count == 0) sb.AppendLine("  none");
        foreach (var h in hid)
            sb.AppendLine($"  VID {h.Vendor:X4} PID {h.Product:X4}  usage {h.UsagePage:X2}/{h.Usage:X2}  HID buttons {h.Buttons}  value caps {h.Values}  {Shorten(h.Path)}");
        return sb.ToString();
    }

    private static string Shorten(string path) => path.Length > 90 ? path[..90] + "…" : path;
}
