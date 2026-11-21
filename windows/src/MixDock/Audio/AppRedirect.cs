using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace MixDock.Audio;

/// <summary>
/// Moves an app's audio streams to an unheard "sink" endpoint using Windows' per-app output setting
/// (same store as Settings > Volume mixer). Process-loopback capture keeps receiving the stream,
/// the raw app sound goes to the sink, and MixDock plays the processed copy to the real device.
/// The setting is persisted per exe, so every redirect is journaled and cleared on exit/startup.
/// </summary>
public sealed class AppRedirect
{
    private static readonly string JournalPath = Path.Combine(Log.Dir, "redirected.json");
    private readonly RedirectCore _core = Core();
    private readonly object _gate = new();

    private static RedirectCore Core() => new(new WinEndpointPolicy(), new RedirectJournal(JournalPath), PolicyId, IsAlive);
    private static bool IsAlive(int pid) { try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; } catch { return false; } }

    /// <summary>Real per-app endpoint store (the undocumented Windows AudioPolicyConfig factory).</summary>
    private sealed class WinEndpointPolicy : IEndpointPolicy
    {
        public int Get(uint pid, int role, out string id)
        {
            id = "";
            try
            {
                int hr = Factory().GetPersistedDefaultAudioEndpoint(pid, 0, role, out var h);
                if (hr == 0 && h != IntPtr.Zero) { var b = WindowsGetStringRawBuffer(h, out var len); id = Marshal.PtrToStringUni(b, (int)len) ?? ""; WindowsDeleteString(h); }
                return hr;
            }
            catch (Exception ex) { Log.Write("endpoint get: " + ex.Message); return unchecked((int)0x80004005); }
        }
        public int Set(uint pid, int role, string? id)
        {
            try { return SetEndpointRaw(pid, role, id ?? ""); }
            catch (Exception ex) { Log.Write("endpoint set: " + ex.Message); return unchecked((int)0x80004005); }
        }
    }

    public string? SinkId { get; private set; }
    public string? RealDeviceId { get; private set; }
    public bool Active => SinkId != null;

    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactory
    {
        int i0(); int i1(); int i2();
        int s0(); int s1(); int s2(); int s3(); int s4(); int s5(); int s6(); int s7(); int s8(); int s9();
        int s10(); int s11(); int s12(); int s13(); int s14(); int s15(); int s16(); int s17(); int s18();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr hstringDeviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr hstringDeviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int len, out IntPtr h);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr h);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);

    private static IAudioPolicyConfigFactory Factory()
    {
        WindowsCreateString("Windows.Media.Internal.AudioPolicyConfig", "Windows.Media.Internal.AudioPolicyConfig".Length, out var cls);
        try
        {
            var iid = typeof(IAudioPolicyConfigFactory).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(cls, ref iid, out var p));
            return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(p);
        }
        finally { WindowsDeleteString(cls); }
    }

    private static string PolicyId(string mmId) => $@"\\?\SWD#MMDEVAPI#{mmId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";

    internal static int SetEndpointRaw(uint pid, int role, string raw)
    {
        var f = Factory(); IntPtr h = IntPtr.Zero;
        if (raw.Length > 0) WindowsCreateString(raw, raw.Length, out h);
        try { return f.SetPersistedDefaultAudioEndpoint(pid, 0, role, h); } finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }

    /// <summary>Clear any per-app output setting left on MixDock itself by an earlier test/crash.</summary>
    public static void ClearSelf() { try { foreach (int r in new[] { 0, 1, 2 }) SetEndpointRaw((uint)Environment.ProcessId, r, ""); } catch { } }

    /// <summary>Probe on our own pid: reads the persisted endpoint; throws if the vtable is wrong.</summary>
    public static bool IsSupported(out string detail)
    {
        try
        {
            var f = Factory();
            int hr = f.GetPersistedDefaultAudioEndpoint((uint)Environment.ProcessId, 0, 1, out var h);
            string cur = "";
            if (h != IntPtr.Zero) { var b = WindowsGetStringRawBuffer(h, out var len); cur = Marshal.PtrToStringUni(b, (int)len) ?? ""; WindowsDeleteString(h); }
            detail = $"GetPersisted hr=0x{hr:X8} current='{cur}'";
            return hr == 0;
        }
        catch (Exception ex) { detail = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>Pick an Active render endpoint nobody listens to. Never the real device or any <paramref name="protectedIds"/> (cue device, chosen output).</summary>
    public static string? ChooseSink(string realDeviceId, string? preferredId = null, params string?[] protectedIds)
    {
        using var en = new MMDeviceEnumerator();
        var devs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToList();
        return SinkPolicy.Choose(devs, preferredId, protectedIds.Append(realDeviceId).Append(Platform.OutputPolicy.PreferredId).ToArray());
    }

    /// <summary>Redirect every process in rootPid's tree to sinkId. Safe to call repeatedly (picks up new processes).
    /// Returns (redirected, failed, owned by the other deck).</summary>
    public (int ok, int failed, int foreign) Take(int rootPid, string sinkId, string realDeviceId)
    {
        lock (_gate)
        {
            SinkId = sinkId; RealDeviceId = realDeviceId;
            var tree = SessionTakeover.ProcessTree(rootPid).Where(p => p != Environment.ProcessId);
            var r = _core.Take(tree, sinkId);
            if (r.ok > 0 || r.failed > 0) Log.Write($"AppRedirect root={rootPid}: {r.ok} redirected, {r.failed} failed, {r.foreign} owned by the other deck");
            return r;
        }
    }

    /// <summary>Puts every redirected process back exactly as it was. False if some could not be verified restored (kept in the journal for retry).</summary>
    public bool Restore()
    {
        lock (_gate)
        {
            bool ok;
            try { ok = _core.Restore(); } catch (Exception ex) { Log.Write("AppRedirect restore failed: " + ex.Message); ok = false; }
            if (ok) SinkId = null; else Log.Write("AppRedirect: some apps could not be restored; they stay journaled and are retried at the next start");
            return ok;
        }
    }

    /// <summary>Crash recovery from the shared journal: put back the exact endpoint each process had. Entries that fail stay for the next run.</summary>
    public static void RecoverFromJournal()
    {
        try { int left = Core().Recover(); Log.Write($"AppRedirect recovery done, {left} entries left"); }
        catch (Exception ex) { Log.Write("AppRedirect recover failed: " + ex.Message); }
    }
}
