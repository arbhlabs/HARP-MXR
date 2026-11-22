using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MixDock.Platform;

public sealed record AudioApp(int Pid, string Name, float Peak = 0, bool Playing = false)
{
    public override string ToString() => Name;
}

public static class AudioDiscovery
{
    public static List<OutputEndpoint> Outputs()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new OutputEndpoint(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name).ToList();
    }
    public static string? DefaultOutputId()
    {
        using var e = new MMDeviceEnumerator();
        using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return d.ID;
    }
    /// <summary>Every session owned by a pid in the set, across all Active render endpoints.</summary>
    public static List<(string endpointId, string endpointName, int pid, float peak)> FindPlayback(ICollection<int> pids)
    {
        var list = new List<(string, string, int, float)>();
        using var e = new MMDeviceEnumerator();
        foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var ss = device.AudioSessionManager.Sessions;
                for (int i = 0; i < ss.Count; i++)
                {
                    using var s = ss[i];
                    int pid = checked((int)s.GetProcessID);
                    if (!pids.Contains(pid) || s.IsSystemSoundsSession) continue;
                    list.Add((device.ID, device.FriendlyName, pid, s.AudioMeterInformation.MasterPeakValue));
                }
            }
            catch { }
        }
        return list;
    }
    /// <summary>
    /// One entry per app (grouped by exe), with the root pid of its process tree as the target,
    /// ranked by live peak. Browsers play through child audio-service processes, so grouping by
    /// tree is what makes "Vivaldi" selectable instead of an arbitrary child PID.
    /// </summary>
    public static List<AudioApp> ActiveAudioProcesses()
    {
        var byExe = new Dictionary<string, (float peak, bool playing, HashSet<int> pids)>(StringComparer.OrdinalIgnoreCase);
        using var e = new MMDeviceEnumerator();
        foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    int pid = checked((int)session.GetProcessID);
                    if (pid <= 0 || pid == Environment.ProcessId || session.IsSystemSoundsSession) continue;
                    string exe;
                    try { using var p = Process.GetProcessById(pid); exe = p.ProcessName; }
                    catch { continue; }
                    if (exe.Equals("audiodg", StringComparison.OrdinalIgnoreCase) || ProtectedApps.IsProtected(exe)) continue;
                    float peak = 0; try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }
                    bool active = session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;
                    byExe.TryGetValue(exe, out var cur);
                    cur.pids ??= new HashSet<int>();
                    cur.pids.Add(pid);
                    byExe[exe] = (Math.Max(cur.peak, peak), cur.playing || active, cur.pids);
                }
            }
            catch { /* endpoint unplugged while enumerating */ }
        }
        var result = new List<AudioApp>();
        Dictionary<int, (int parent, string? cmd)>? snap = null;
        foreach (var (exe, v) in byExe.ToList())
        {
            var skip = new HashSet<int>();
            if (BrowserDetector.IsChromium(exe))
            {
                // Deck browsers (own --user-data-dir) are separate process trees: list them apart from the normal browser.
                try
                {
                    snap ??= ProcSnapshot();
                    foreach (var p in Process.GetProcessesByName(exe))
                    {
                        using (p)
                        {
                            if (!snap.TryGetValue(p.Id, out var info) || DeckBrowserCommand.ParseRoot(info.cmd) is not var (key, deck) || !key.Equals(exe, StringComparison.OrdinalIgnoreCase)) continue;
                            var tree = Audio.SessionTakeover.ProcessTree(p.Id);
                            skip.UnionWith(tree);
                            if (!v.pids.Overlaps(tree)) continue;
                            result.Add(new AudioApp(p.Id, DeckBrowserCommand.GroupName(exe.ToLowerInvariant(), deck), v.peak, v.playing));
                        }
                    }
                }
                catch (Exception ex) { Log.Write("DeckBrowser discovery: " + ex.Message); }
            }
            var rest = v.pids.Where(p => !skip.Contains(p)).ToHashSet();
            if (rest.Count == 0) continue;
            result.Add(new AudioApp(RootPid(exe, rest, skip), exe, v.peak, v.playing));
        }
        static int Pri(string n) { n = n.ToLowerInvariant(); return n.Contains("spotify") ? 0 : n.Contains("vivaldi") ? 1 : 2; }
        return result.OrderByDescending(a => a.Playing).ThenBy(a => Pri(a.Name)).ThenByDescending(a => a.Peak).ThenBy(a => a.Name).ToList();
    }

    // Root of the app's process tree: a process of this exe whose parent is not the same exe.
    private static int RootPid(string exe, HashSet<int> sessionPids, HashSet<int> skip)
    {
        try
        {
            var procs = Process.GetProcessesByName(exe);
            var ids = procs.Select(p => p.Id).Where(id => !skip.Contains(id)).ToHashSet();
            var parents = ParentMap();
            var roots = ids.Where(id => !ids.Contains(parents.GetValueOrDefault(id))).ToList();
            foreach (var p in procs) p.Dispose();
            // prefer the root whose tree actually owns a session
            foreach (int r in roots)
                if (Audio.SessionTakeover.ProcessTree(r).Overlaps(sessionPids)) return r;
            if (roots.Count > 0) return roots[0];
        }
        catch { }
        return sessionPids.First();
    }

    private static Dictionary<int, (int parent, string? cmd)> ProcSnapshot()
    {
        var map = new Dictionary<int, (int, string?)>();
        using var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process");
        foreach (System.Management.ManagementObject o in q.Get())
            map[Convert.ToInt32(o["ProcessId"])] = (Convert.ToInt32(o["ParentProcessId"]), o["CommandLine"] as string);
        return map;
    }

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        using var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
        foreach (System.Management.ManagementObject o in q.Get())
            map[Convert.ToInt32(o["ProcessId"])] = Convert.ToInt32(o["ParentProcessId"]);
        return map;
    }
}
