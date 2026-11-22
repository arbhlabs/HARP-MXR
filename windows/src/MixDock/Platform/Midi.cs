using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MixDock.Platform;

/// <summary>One MIDI message (status byte includes the channel). Due = Stopwatch ticks when to send (0 = now), used for quantized launches.</summary>
public readonly record struct MidiEvent(byte Status, byte D1, byte D2, long Due = 0)
{
    public int Length => (Status & 0xF0) is 0xC0 or 0xD0 ? 2 : Status >= 0xF8 ? 1 : 3;
}

/// <summary>MIDI byte encoding (pure). Channels are 1-based in the API, 0-based on the wire.</summary>
public static class MidiMsg
{
    public const byte Clock = 0xF8;
    private static byte Ch(int channel) => (byte)(Math.Clamp(channel, 1, 16) - 1);
    private static byte D(int v) => (byte)Math.Clamp(v, 0, 127);
    public static MidiEvent NoteOn(int channel, int note, int velocity, long due = 0) => new((byte)(0x90 | Ch(channel)), D(note), D(Math.Max(1, velocity)), due);
    public static MidiEvent NoteOff(int channel, int note, long due = 0) => new((byte)(0x80 | Ch(channel)), D(note), 0, due);
    public static MidiEvent Cc(int channel, int controller, int value) => new((byte)(0xB0 | Ch(channel)), D(controller), D(value));
    public static MidiEvent Program(int channel, int program) => new((byte)(0xC0 | Ch(channel)), D(program), 0);
    public static MidiEvent ClockTick() => new(Clock, 0, 0);
    public static int Scale127(double v01) => (int)Math.Round(Math.Clamp(double.IsFinite(v01) ? v01 : 0, 0, 1) * 127);
    /// <summary>Stick/axis -1..1 to CC 0..127 (0 = full negative, 64 = centre).</summary>
    public static int AxisToCc(double v) => (int)Math.Round((Math.Clamp(double.IsFinite(v) ? v : 0, -1, 1) + 1) * 63.5);
    public static byte[] Bytes(MidiEvent e) => e.Length switch { 1 => new[] { e.Status }, 2 => new[] { e.Status, e.D1 }, _ => new[] { e.Status, e.D1, e.D2 } };

    /// <summary>Number of 24-per-quarter-note clock ticks between two beat positions (0 on a backwards jump; capped so a tempo jump cannot flood the port).</summary>
    public static int ClockTicksBetween(double beatFrom, double beatTo)
    {
        if (!double.IsFinite(beatFrom) || !double.IsFinite(beatTo) || beatTo <= beatFrom) return 0;
        long a = (long)Math.Floor(beatFrom * 24), b = (long)Math.Floor(beatTo * 24);
        return (int)Math.Clamp(b - a, 0, 48);
    }
}

/// <summary>An output MIDI port. Send must be safe to call from the MIDI thread only.</summary>
public interface IMidiPort : IDisposable
{
    string Name { get; }
    bool Send(MidiEvent e);
}

/// <summary>Beat clock snapshot for MIDI clock: the beat position at <see cref="StampTicks"/> (Stopwatch) and the tempo.</summary>
public sealed record MidiClockState(double BeatPos, double Bpm, long StampTicks);

/// <summary>
/// MIDI output service. The UI/audio side only enqueues events into a lock-free queue (<see cref="ConcurrentQueue{T}"/>, never blocking);
/// a dedicated high-priority thread sends them (honouring each event's due time to ~1 ms), emits the MIDI clock (24 ppqn) from the
/// beat clock when enabled, and polls an optional receiver. A port failure is swallowed: MIDI can never throw into the audio or UI thread.
/// </summary>
public sealed class MidiService : IDisposable
{
    private const int MaxQueue = 4096;
    private readonly ConcurrentQueue<MidiEvent> _out = new(), _in = new();
    private readonly List<MidiEvent> _later = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stop;
    private IMidiPort? _port;
    private volatile MidiClockState? _clock;
    private double _lastClockBeat = double.NaN;
    private int _queued;
    public volatile bool ClockEnabled;
    public IMidiPort? Port => _port;
    /// <summary>Human status such as "MIDI: MixDock (teVirtualMIDI)" or "MIDI: no virtual port".</summary>
    public string Status { get; private set; } = "MIDI: off";
    public long Sent, Dropped, ClockTicks;
    /// <summary>Messages received from the DAW (notes, CCs), drained by the UI thread.</summary>
    public bool TryReadIncoming(out MidiEvent e) => _in.TryDequeue(out e);
    /// <summary>Called by an input backend (any thread).</summary>
    public void PostIncoming(MidiEvent e) { if (_in.Count < MaxQueue) _in.Enqueue(e); }
    public Func<MidiEvent?>? Receiver;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    private bool _timerRaised;

    public MidiService(IMidiPort? port, string status)
    {
        _port = port; Status = status;
        // 1 ms timer resolution so due times and the clock are not rounded to Windows' 15.6 ms default tick
        if (port != null) try { _timerRaised = timeBeginPeriod(1) == 0; } catch { }
        _thread = new Thread(Run) { IsBackground = true, Name = "HARP-MXR MIDI", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Queue one event. Never blocks, never throws. Dropped silently when the port is missing or the queue is full.</summary>
    public void Post(MidiEvent e)
    {
        if (_port == null || _stop) return;
        if (Volatile.Read(ref _queued) >= MaxQueue) { Interlocked.Increment(ref Dropped); return; }
        Interlocked.Increment(ref _queued);
        _out.Enqueue(e);
        _wake.Set();
    }
    public void SetClock(double beatPos, double bpm) => _clock = bpm > 0 && double.IsFinite(beatPos) ? new MidiClockState(beatPos, bpm, Stopwatch.GetTimestamp()) : null;

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                Pump(Stopwatch.GetTimestamp());
                var rx = Receiver;
                if (rx != null) { var e = rx(); while (e is MidiEvent m) { PostIncoming(m); e = rx(); } }
            }
            catch { /* a broken port must never take MixDock down */ }
            _wake.Wait(1); _wake.Reset();
        }
    }

    /// <summary>One MIDI-thread step (public so tests can drive it deterministically).</summary>
    public void Pump(long nowTicks)
    {
        while (_out.TryDequeue(out var e)) { Interlocked.Decrement(ref _queued); if (e.Due > nowTicks) _later.Add(e); else Emit(e); }
        for (int i = 0; i < _later.Count;)
            if (_later[i].Due <= nowTicks) { Emit(_later[i]); _later.RemoveAt(i); } else i++;
        if (ClockEnabled && _clock is { } c)
        {
            double beat = c.BeatPos + (nowTicks - c.StampTicks) / (double)Stopwatch.Frequency * c.Bpm / 60.0;
            if (!double.IsNaN(_lastClockBeat))
                for (int k = MidiMsg.ClockTicksBetween(_lastClockBeat, beat); k > 0; k--) { Emit(MidiMsg.ClockTick()); ClockTicks++; }
            _lastClockBeat = beat;
        }
        else _lastClockBeat = double.NaN;
    }
    private void Emit(MidiEvent e)
    {
        try { if (_port?.Send(e) == true) Interlocked.Increment(ref Sent); } catch { }
    }

    public void Dispose()
    {
        _stop = true; ClockEnabled = false; _wake.Set();
        try { _thread.Join(500); } catch { }
        try { Pump(long.MaxValue); } catch { }      // flush what is still queued (e.g. all-notes-off)
        try { _port?.Dispose(); } catch { }
        _port = null;
        if (_timerRaised) { _timerRaised = false; try { timeEndPeriod(1); } catch { } }
    }
}

/// <summary>Opens the best available MIDI output. Order: teVirtualMIDI (Tobias Erichsen driver DLL) creates a port named "MixDock"; else an existing loopMIDI / "MixDock" port via winmm; else none.</summary>
public static class MidiPorts
{
    public const string PortName = "HARP MXR";

    public static MidiService Open(string? preferredOut, Func<IMidiPort?>? virtualFactory = null, Func<string?, IMidiPort?>? winmmFactory = null)
    {
        IMidiPort? p = null; string status;
        try { p = (virtualFactory ?? TeVirtualMidi.TryCreate)(); } catch { p = null; }
        if (p != null) status = $"MIDI: {p.Name} (teVirtualMIDI)";
        else
        {
            try { p = (winmmFactory ?? WinmmOut.TryOpenVirtual)(preferredOut); } catch { p = null; }
            status = p != null ? $"MIDI: {p.Name} (winmm)" : "MIDI: no virtual port";
        }
        return new MidiService(p, status);
    }

    public static string[] OutputNames() { try { return WinmmOut.Names(); } catch { return Array.Empty<string>(); } }
    public static string[] InputNames() { try { return WinmmIn.Names(); } catch { return Array.Empty<string>(); } }
}

/// <summary>Virtual MIDI port from the Tobias Erichsen teVirtualMIDI SDK DLL. Only used when the DLL (installed with loopMIDI / rtpMIDI) is present.</summary>
public sealed class TeVirtualMidi : IMidiPort
{
    private const uint ParseRx = 1, InstantiateBoth = 12;
    [DllImport("teVirtualMIDI64.dll", EntryPoint = "virtualMIDICreatePortEx2", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr Create(string name, IntPtr callback, IntPtr instance, uint maxSysex, uint flags);
    [DllImport("teVirtualMIDI64.dll", EntryPoint = "virtualMIDIClosePort")]
    private static extern void Close(IntPtr port);
    [DllImport("teVirtualMIDI64.dll", EntryPoint = "virtualMIDISendData")]
    private static extern bool SendData(IntPtr port, byte[] data, uint length);
    [DllImport("teVirtualMIDI64.dll", EntryPoint = "virtualMIDIGetData")]
    private static extern bool GetData(IntPtr port, byte[] data, ref uint length);

    private IntPtr _h;
    private readonly byte[] _rx = new byte[16];
    public string Name => MidiPorts.PortName;
    private TeVirtualMidi(IntPtr h) { _h = h; }

    public static IMidiPort? TryCreate()
    {
        try
        {
            if (!Environment.Is64BitProcess) return null;
            var h = Create(MidiPorts.PortName, IntPtr.Zero, IntPtr.Zero, 65535, ParseRx | InstantiateBoth);
            return h == IntPtr.Zero ? null : new TeVirtualMidi(h);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
        catch { return null; }
    }
    public bool Send(MidiEvent e) => _h != IntPtr.Zero && SendData(_h, MidiMsg.Bytes(e), (uint)e.Length);
    /// <summary>One pending message from the DAW, or null.</summary>
    public MidiEvent? Receive()
    {
        if (_h == IntPtr.Zero) return null;
        uint len = (uint)_rx.Length;
        if (!GetData(_h, _rx, ref len) || len == 0) return null;
        return new MidiEvent(_rx[0], len > 1 ? _rx[1] : (byte)0, len > 2 ? _rx[2] : (byte)0);
    }
    public void Dispose() { var h = _h; _h = IntPtr.Zero; if (h != IntPtr.Zero) try { Close(h); } catch { } }
}

/// <summary>Plain Windows MIDI output (winmm). Only ever opens a virtual cable port (loopMIDI or one named MixDock), never a hardware synth.</summary>
public sealed class WinmmOut : IMidiPort
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Caps { public ushort Mid, Pid; public uint Ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public ushort Tech, Voices, Notes, ChannelMask; public uint Support; }
    [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetDevCaps(UIntPtr id, ref Caps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutOpen(out IntPtr h, uint id, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(IntPtr h, uint msg);
    [DllImport("winmm.dll")] private static extern uint midiOutClose(IntPtr h);

    private IntPtr _h;
    public string Name { get; }
    private WinmmOut(IntPtr h, string name) { _h = h; Name = name; }

    public static string[] Names()
    {
        var l = new List<string>(); uint n = midiOutGetNumDevs();
        for (uint i = 0; i < n; i++) { var c = new Caps(); if (midiOutGetDevCaps((UIntPtr)i, ref c, (uint)Marshal.SizeOf<Caps>()) == 0) l.Add(c.Name); }
        return l.ToArray();
    }
    /// <summary>True for ports that are safe to feed: loopMIDI cables and anything named MixDock.</summary>
    public static bool IsVirtualCable(string name)
        => name.Contains("loopMIDI", StringComparison.OrdinalIgnoreCase) || name.Contains("MixDock", StringComparison.OrdinalIgnoreCase) || name.Contains("HARP", StringComparison.OrdinalIgnoreCase);

    public static IMidiPort? TryOpenVirtual(string? preferred)
    {
        var names = Names();
        int pick = -1;
        if (!string.IsNullOrWhiteSpace(preferred)) pick = Array.FindIndex(names, n => n.Equals(preferred, StringComparison.OrdinalIgnoreCase) && IsVirtualCable(n));
        if (pick < 0) pick = Array.FindIndex(names, IsVirtualCable);
        if (pick < 0) return null;
        return midiOutOpen(out var h, (uint)pick, IntPtr.Zero, IntPtr.Zero, 0) == 0 ? new WinmmOut(h, names[pick]) : null;
    }
    public bool Send(MidiEvent e)
    {
        if (_h == IntPtr.Zero) return false;
        uint msg = (uint)(e.Status | (e.D1 << 8) | (e.D2 << 16));
        return midiOutShortMsg(_h, msg) == 0;
    }
    public void Dispose() { var h = _h; _h = IntPtr.Zero; if (h != IntPtr.Zero) try { midiOutClose(h); } catch { } }
}

/// <summary>Optional MIDI input (winmm callback into the service's lock-free incoming queue). Lights pads from a DAW, like a Launchpad, and carries CC for the FX amount.</summary>
public sealed class WinmmIn : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Caps { public ushort Mid, Pid; public uint Ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Support; }
    private delegate void Proc(IntPtr h, uint msg, IntPtr inst, IntPtr p1, IntPtr p2);
    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCaps(UIntPtr id, ref Caps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out IntPtr h, uint id, Proc cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInStop(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInClose(IntPtr h);

    private IntPtr _h; private readonly Proc _proc; // the delegate field keeps the native callback alive
    private readonly MidiService _svc;
    public string Name { get; }
    private WinmmIn(MidiService svc, IntPtr h, Proc proc, string name) { _svc = svc; _h = h; _proc = proc; Name = name; }

    public static string[] Names()
    {
        var l = new List<string>(); uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++) { var c = new Caps(); if (midiInGetDevCaps((UIntPtr)i, ref c, (uint)Marshal.SizeOf<Caps>()) == 0) l.Add(c.Name); }
        return l.ToArray();
    }
    public static WinmmIn? TryOpen(MidiService svc, string? name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var names = Names(); int idx = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return null;
            Proc proc = (h, msg, inst, p1, p2) =>
            {
                if (msg != 0x3C3) return;                       // MIM_DATA
                uint w = (uint)p1.ToInt64();
                svc.PostIncoming(new MidiEvent((byte)(w & 0xFF), (byte)((w >> 8) & 0x7F), (byte)((w >> 16) & 0x7F)));
            };
            if (midiInOpen(out var hh, (uint)idx, proc, IntPtr.Zero, 0x30000) != 0) return null;   // CALLBACK_FUNCTION
            midiInStart(hh);
            return new WinmmIn(svc, hh, proc, names[idx]);
        }
        catch { return null; }
    }
    public void Dispose() { var h = _h; _h = IntPtr.Zero; if (h != IntPtr.Zero) try { midiInStop(h); midiInClose(h); } catch { } }
}
