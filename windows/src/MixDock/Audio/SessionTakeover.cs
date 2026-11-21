using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace MixDock.Audio;

/// <summary>
/// Mutes the selected app's own audio sessions so only MixDock's processed copy is heard.
/// Process loopback capture (DeckSource) keeps reading the app's stream. No driver involved.
/// Mute state lives in Windows, so every mute is journaled to disk and undone on exit/startup.
/// </summary>
public sealed class SessionTakeover
{
    private sealed record Undo(string EndpointId, string SessionInstanceId, int Pid, string Exe, bool WasMuted);
    private readonly List<Undo> _undo = new();
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDock", "muted.json");

    /// <summary>Endpoint where the app is actually playing (auto output device).</summary>
    public string? DeviceId { get; private set; }

    /// <summary>Mute every session owned by rootPid's process tree. Safe to call repeatedly.</summary>
    public void Take(int rootPid)
    {
        var tree = ProcessTree(rootPid);
        bool changed = false;
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var s = sessions[i];
                    int pid = checked((int)s.GetProcessID);
                    if (!tree.Contains(pid) || s.IsSystemSoundsSession) continue;
                    string id = s.GetSessionInstanceIdentifier;
                    DeviceId ??= dev.ID;
                    if (_undo.Any(u => u.SessionInstanceId == id)) continue;
                    var volume = s.SimpleAudioVolume;
                    _undo.Add(new Undo(dev.ID, id, pid, ExeOf(pid), volume.Mute));
                    volume.Mute = true;
                    changed = true;
                }
            }
            catch { /* endpoint unplugged while enumerating */ }
        }
        if (changed) WriteJournal();
    }

    /// <summary>Unmute everything this takeover muted, then clear the journal.</summary>
    public void Restore()
    {
        if (_undo.Count == 0) { DeviceId = null; DeleteJournal(); return; }
        RestoreEntries(_undo);
        _undo.Clear();
        DeviceId = null;
        DeleteJournal();
    }

    /// <summary>Startup/crash recovery: undo mutes left behind by a previous MixDock run.</summary>
    public static void RecoverFromJournal()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(JournalPath)) ?? new();
            RestoreEntries(entries.Select(e => new Undo(e.EndpointId, e.SessionInstanceId, e.Pid, e.Exe, e.WasMuted)).ToList());
        }
        catch { /* corrupt journal: nothing reliable to restore */ }
        finally { DeleteJournal(); }
    }

    private static void RestoreEntries(List<Undo> entries)
    {
        using var en = new MMDeviceEnumerator();
        foreach (var grp in entries.Where(u => !u.WasMuted).GroupBy(u => u.EndpointId))
        {
            try
            {
                var sessions = en.GetDevice(grp.Key).AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var s = sessions[i];
                    var u = grp.FirstOrDefault(x => x.SessionInstanceId == s.GetSessionInstanceIdentifier);
                    if (u != null) s.SimpleAudioVolume.Mute = false;
                }
            }
            catch { /* device gone; Windows already dropped the session */ }
        }
    }

    private sealed record Entry(string EndpointId, string SessionInstanceId, int Pid, string Exe, bool WasMuted);

    private void WriteJournal()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            var entries = _undo.Select(u => new Entry(u.EndpointId, u.SessionInstanceId, u.Pid, u.Exe, u.WasMuted));
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(entries));
        }
        catch { /* journal is best effort; in-memory undo still runs on normal exit */ }
    }

    private static void DeleteJournal()
    {
        try { File.Delete(JournalPath); } catch { }
    }

    private static string ExeOf(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return ""; }
    }

    // Toolhelp32 snapshot: pid -> parent pid, then BFS from root (root included).
    internal static HashSet<int> ProcessTree(int root)
    {
        var children = new Dictionary<int, List<int>>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return new() { root };
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    int parent = (int)entry.th32ParentProcessID;
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = new();
                    list.Add((int)entry.th32ProcessID);
                }
                while (Process32NextW(snap, ref entry));
            }
        }
        finally { CloseHandle(snap); }

        var result = new HashSet<int> { root };
        var queue = new Queue<int>(); queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (!children.TryGetValue(p, out var kids)) continue;
            foreach (int k in kids) if (result.Add(k)) queue.Enqueue(k);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}
