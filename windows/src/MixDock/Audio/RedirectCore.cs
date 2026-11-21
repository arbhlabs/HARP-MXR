using System.Text.Json;

namespace MixDock.Audio;

/// <summary>Per-app output endpoint store (Windows' persisted per-app default endpoint). Faked in tests.</summary>
public interface IEndpointPolicy
{
    /// <summary>Reads the persisted endpoint for <paramref name="pid"/>; "" means no per-app setting. Returns an HRESULT.</summary>
    int Get(uint pid, int role, out string id);
    /// <summary>Writes it; null/"" clears the per-app setting. Returns an HRESULT.</summary>
    int Set(uint pid, int role, string? id);
}

/// <summary>One redirected process: the settings it had BEFORE MixDock touched it, per role in <see cref="RedirectCore.Roles"/> order.</summary>
public sealed record RedirectEntry(int Pid, string Owner, string Sink, string[] Original, int Attempts = 0);

/// <summary>Journal shared by every controller (both decks): one file, entries merged by pid, never overwritten wholesale.</summary>
public sealed class RedirectJournal
{
    private static readonly object Gate = new();
    private readonly string _path;
    public RedirectJournal(string path) { _path = path; }

    public List<RedirectEntry> Load()
    {
        lock (Gate) return LoadUnlocked();
    }

    private List<RedirectEntry> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            string json = File.ReadAllText(_path);
            try { return JsonSerializer.Deserialize<List<RedirectEntry>>(json) ?? new(); }
            catch (JsonException)
            {   // legacy format: a bare list of pids -> clear their per-app setting on recovery
                var pids = JsonSerializer.Deserialize<List<int>>(json) ?? new();
                return pids.Select(p => new RedirectEntry(p, "legacy", "", Enumerable.Repeat("", RedirectCore.Roles.Length).ToArray())).ToList();
            }
        }
        catch (Exception ex) { Log.Write("redirect journal unreadable: " + ex.Message); return new(); }
    }

    /// <summary>Atomic read-modify-write; the file is deleted only when no entry remains.</summary>
    public void Update(Func<List<RedirectEntry>, List<RedirectEntry>> change)
    {
        lock (Gate)
        {
            var next = change(LoadUnlocked());
            try
            {
                if (next.Count == 0) { File.Delete(_path); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(next));
                File.Move(tmp, _path, true);
            }
            catch (Exception ex) { Log.Write("redirect journal write failed: " + ex.Message); }
        }
    }
}

/// <summary>
/// Transactional redirect/restore logic with no Win32 dependency. Every change is journaled BEFORE it is made and verified
/// after; a failed change is rolled back and never recorded as redirected; restore puts back exactly the endpoint each
/// role had before (not "default"), and an entry stays in the journal until its restore is verified.
/// </summary>
public sealed class RedirectCore
{
    public static readonly int[] Roles = { 1, 0 };     // communications, console (same order the redirect always used)
    public const int MaxRecoverAttempts = 3;
    private readonly IEndpointPolicy _p;
    private readonly RedirectJournal _j;
    private readonly Func<string, string> _policyId;
    private readonly Func<int, bool> _alive;
    private readonly object _gate = new();
    private readonly Dictionary<int, RedirectEntry> _mine = new();
    public string Owner { get; } = Guid.NewGuid().ToString("N");

    public RedirectCore(IEndpointPolicy policy, RedirectJournal journal, Func<string, string> policyId, Func<int, bool>? alive = null)
    { _p = policy; _j = journal; _policyId = policyId; _alive = alive ?? (_ => true); }

    public int RedirectedCount { get { lock (_gate) return _mine.Count; } }

    /// <summary>Redirect the given processes to <paramref name="sinkMmId"/>. Returns (redirected, failed, skipped because the other deck owns it).</summary>
    public (int ok, int failed, int foreign) Take(IEnumerable<int> pids, string sinkMmId)
    {
        string sink = _policyId(sinkMmId); int ok = 0, failed = 0, foreign = 0;
        lock (_gate)
        {
            var others = _j.Load().Where(e => e.Owner != Owner).Select(e => e.Pid).ToHashSet();
            foreach (int pid in pids)
            {
                if (others.Contains(pid)) { foreign++; continue; }
                if (_mine.TryGetValue(pid, out var have))
                {
                    if (have.Sink == sink) continue;
                    if (SetAll(pid, sink)) { var upd = have with { Sink = sink }; _mine[pid] = upd; Replace(upd); ok++; } else failed++;
                    continue;
                }
                var orig = new string[Roles.Length]; bool readable = true;
                for (int i = 0; i < Roles.Length; i++)
                {
                    int hr = _p.Get((uint)pid, Roles[i], out var cur);
                    if (hr != 0) { readable = false; cur = ""; }
                    orig[i] = cur ?? "";
                }
                if (!readable) Log.Write($"AppRedirect pid={pid}: could not read the current endpoint; restore will clear it");
                var entry = new RedirectEntry(pid, Owner, sink, orig);
                Replace(entry);                                       // journal first: a crash mid-change is still recoverable
                if (SetAll(pid, sink)) { _mine[pid] = entry; ok++; }
                else
                {
                    for (int i = 0; i < Roles.Length; i++) _p.Set((uint)pid, Roles[i], orig[i].Length == 0 ? null : orig[i]);
                    Drop(pid); failed++;
                    Log.Write($"AppRedirect pid={pid}: redirect failed, rolled back");
                }
            }
        }
        return (ok, failed, foreign);
    }

    /// <summary>Put every endpoint this owner changed back exactly as found. True only if all were verified restored.</summary>
    public bool Restore()
    {
        lock (_gate)
        {
            foreach (var e in _mine.Values.ToList())
                if (RestoreEntry(e, _p)) { _mine.Remove(e.Pid); Drop(e.Pid); }
            return _mine.Count == 0;
        }
    }

    /// <summary>Startup/crash recovery of every entry in the journal. Failed entries stay (a few attempts for dead pids). Returns entries left.</summary>
    public int Recover()
    {
        int left = 0;
        _j.Update(list =>
        {
            var keep = new List<RedirectEntry>();
            foreach (var e in list)
            {
                if (RestoreEntry(e, _p)) continue;
                bool dead = !_alive(e.Pid);
                if (dead && e.Attempts + 1 >= MaxRecoverAttempts) { Log.Write($"AppRedirect recover: pid={e.Pid} is gone and cannot be restored, giving up"); continue; }
                keep.Add(e with { Attempts = e.Attempts + 1 });
            }
            left = keep.Count; return keep;
        });
        return left;
    }

    private bool SetAll(int pid, string sink)
    {
        bool ok = true;
        for (int i = 0; i < Roles.Length; i++)
        {
            int hr = _p.Set((uint)pid, Roles[i], sink);
            Log.Write($"  Set pid={pid} role={Roles[i]} hr=0x{hr:X8}");
            if (hr != 0 || !Reads(pid, Roles[i], sink)) ok = false;
        }
        return ok;
    }

    private bool Reads(int pid, int role, string expected) =>
        _p.Get((uint)pid, role, out var cur) == 0 && string.Equals(cur, expected, StringComparison.OrdinalIgnoreCase);

    private static bool RestoreEntry(RedirectEntry e, IEndpointPolicy p)
    {
        bool ok = true;
        for (int i = 0; i < Roles.Length; i++)
        {
            string want = i < e.Original.Length ? e.Original[i] : "";
            int hr = p.Set((uint)e.Pid, Roles[i], want.Length == 0 ? null : want);
            bool back = hr == 0 && p.Get((uint)e.Pid, Roles[i], out var cur) == 0 && string.Equals(cur ?? "", want, StringComparison.OrdinalIgnoreCase);
            Log.Write($"AppRedirect restore pid={e.Pid} role={Roles[i]} hr=0x{hr:X8} verified={back}");
            if (!back) ok = false;
        }
        return ok;
    }

    private void Replace(RedirectEntry e) => _j.Update(l => { l.RemoveAll(x => x.Pid == e.Pid && x.Owner == e.Owner); l.Add(e); return l; });
    private void Drop(int pid) => _j.Update(l => { l.RemoveAll(x => x.Pid == pid && x.Owner == Owner); return l; });
}

/// <summary>Pure sink choice: an unheard endpoint that is never MixDock's own output, the headphone cue device or the user's chosen output.</summary>
public static class SinkPolicy
{
    public static string? Choose(IEnumerable<(string Id, string Name)> candidates, string? preferredId, params string?[] protectedIds)
    {
        var devs = candidates.Where(d => !protectedIds.Contains(d.Id)).ToList();
        if (preferredId != null && devs.Any(d => d.Id == preferredId)) return preferredId;
        static int Rank(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("steam streaming")) return 0;
            if (n.Contains("cable") || n.Contains("voicemeeter") || n.Contains("virtual")) return 1;
            if (n.Contains("hdmi") || n.Contains("high definition audio") || n.Contains("display")) return 9;
            return 9;
        }
        return devs.Select(d => (d, r: Rank(d.Name))).Where(x => x.r < 9).OrderBy(x => x.r).Select(x => x.d.Id).FirstOrDefault();
    }
}
