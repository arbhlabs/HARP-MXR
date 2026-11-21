using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MixDock.Platform;

public sealed class GlobalHotkeys : IDisposable
{
    private readonly IntPtr _window;
    private readonly Dictionary<int, string> _action = new();
    public int BoundCount => _action.Count;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public GlobalHotkeys(IntPtr hwnd, Dictionary<string, string> mappings)
    {
        _window = hwnd;
        int id = 0x5A00;
        foreach (var (combo, action) in mappings)
        {
            if (!TryParse(combo, out var modifiers, out var key)) continue;
            // MOD_NOREPEAT avoids uncontrolled repeat; collisions quietly skip.
            if (RegisterHotKey(hwnd, id, modifiers | 0x4000, (uint)key))
                _action[id] = action;
            id++;
        }
    }
    public bool TryResolve(int id, out string action) => _action.TryGetValue(id, out action!);
    private static bool TryParse(string text, out uint mods, out Keys key)
    {
        mods = 0; key = Keys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= 0x2; break;
                case "ALT": mods |= 0x1; break;
                case "SHIFT": mods |= 0x4; break;
                case "WIN": mods |= 0x8; break;
                default:
                    if (!Enum.TryParse(part, true, out key) || key == Keys.None) return false;
                    break;
            }
        }
        return key != Keys.None && mods != 0;
    }
    public void Dispose()
    {
        foreach (var id in _action.Keys) UnregisterHotKey(_window, id);
        _action.Clear();
    }
}
