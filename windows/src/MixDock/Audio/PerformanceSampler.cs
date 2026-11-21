using System.Collections.Concurrent;

namespace MixDock.Audio;

/// <summary>
/// Per-deck "slip" engine on the heard stream (render thread; commands come from the UI thread through a queue):
/// four beat-sample pads, LT time bend (tape slow/stop, spins back up and re-locks on the next beat), reverse
/// (censor-style: the playhead keeps running underneath) and spin-back. Every transition uses 5 ms ramps or
/// speed slews, never a hard switch, so nothing clicks.
/// </summary>
public sealed class PerformanceSampler
{
    private const int Sr = FxCurves.SampleRate;
    public const int Capacity = Sr * 4;
    private const int MaxLag = Capacity - 4096;
    private const int Ramp = 220;                      // 5 ms
    private readonly float[] _history = new float[Capacity * 2];
    /// <summary>16 pads = 4 banks x 4. Buffers are allocated on first capture (UI thread) so unused pads cost nothing.</summary>
    public const int PadCount = 16;
    private readonly float[]?[] _pads = new float[]?[PadCount];
    private readonly int[] _length = new int[PadCount];
    private abstract record Cmd;
    private sealed record CaptureCmd(int Pad, int Delay, int Length) : Cmd;
    private sealed record PlayCmd(int Pad, int Delay, bool Loop) : Cmd;
    private sealed record StopPadCmd : Cmd;
    private sealed record LoopCmd(bool On) : Cmd;
    private sealed record SpinCmd : Cmd;
    private sealed record ResetCmd : Cmd;
    private readonly ConcurrentQueue<Cmd> _cmds = new();
    private CaptureCmd? _pendingCapture; private PlayCmd? _pendingPlay; private int _capWait, _playWait;

    // UI-thread controls
    private volatile float _bend;
    private volatile bool _reverse;
    private double _bpm, _phaseIn = double.NaN; private readonly object _tempoLock = new();
    public float Bend { get => _bend; set => _bend = Math.Clamp(float.IsFinite(value) ? value : 0, 0, 1); }
    public bool Reverse { get => _reverse; set => _reverse = value; }
    public void SetTempo(double bpm, double phase) { lock (_tempoLock) { _bpm = bpm; _phaseIn = phase; _tempoStamp = System.Diagnostics.Stopwatch.GetTimestamp(); _tempoNew = true; } }
    private long _tempoStamp; private bool _tempoNew;

    public float[] Levels { get; } = new float[PadCount];
    public int Length(int pad) => (uint)pad < PadCount ? Volatile.Read(ref _length[pad]) : 0;
    /// <summary>Captured audio of a pad (interleaved stereo, first Length(pad) frames valid). Render-thread readers only.</summary>
    public float[] PadData(int pad) => _pads[pad] ?? Array.Empty<float>();
    /// <summary>Restore a saved slice (UI thread, before it is played).</summary>
    public void LoadSlice(int pad, float[] stereo, int frames)
    {
        if ((uint)pad >= PadCount) return;
        frames = Math.Clamp(frames, 0, Math.Min(Capacity, stereo.Length / 2));
        var buf = _pads[pad] ??= new float[Capacity * 2];
        Array.Copy(stereo, buf, frames * 2);
        Volatile.Write(ref _length[pad], frames);
    }
    /// <summary>Copy of a captured slice for saving, or null when empty.</summary>
    public float[]? CopySlice(int pad)
    {
        int n = Length(pad); var buf = (uint)pad < PadCount ? _pads[pad] : null;
        if (n <= 0 || buf == null) return null;
        var copy = new float[n * 2]; Array.Copy(buf, copy, n * 2); return copy;
    }
    private volatile int _playingPad = -1;
    public int PlayingPad => _playingPad;
    private volatile int _stateCode;
    /// <summary>0 = live, 1 = tape bend / stop, 2 = reverse, 3 = spin-back, 4 = re-locking.</summary>
    public int State => _stateCode;

    public void Capture(int pad, int delayFrames, int length)
    {
        pad = Math.Clamp(pad, 0, PadCount - 1);
        _pads[pad] ??= new float[Capacity * 2];
        _cmds.Enqueue(new CaptureCmd(pad, Math.Max(0, delayFrames), Math.Clamp(length, 64, Capacity)));
    }
    public void Play(int pad, int delayFrames, bool loop) => _cmds.Enqueue(new PlayCmd(Math.Clamp(pad, 0, PadCount - 1), Math.Max(0, delayFrames), loop));
    public void StopPad() => _cmds.Enqueue(new StopPadCmd());
    /// <summary>Switch the playing pad to gate-repeat (hold) or back to one-shot.</summary>
    public void SetLoop(bool on) => _cmds.Enqueue(new LoopCmd(on));
    public void SpinBack() => _cmds.Enqueue(new SpinCmd());
    /// <summary>Panic: back to the live stream immediately (still through ramps).</summary>
    public void Reset() { _bend = 0; _reverse = false; _cmds.Enqueue(new ResetCmd()); }

    // render-thread state
    private int _w, _filled;
    private double _lag, _speed = 1, _beatPos;
    private bool _tape, _xfade;
    private float _xf;
    private double _spinT = -1;
    private int _active = -1, _at; private bool _loop; private float _padMix;

    /// <summary>Spin-back speed profile: whips back, decays to a stop, then the normal spin-up and re-lock take over.</summary>
    public static double SpinSpeed(double t) => t < 0 ? 1 : t < .55 ? -2.2 * Math.Exp(-t / .17) : 1;
    public const double SpinSeconds = .55;

    public void Process(Span<float> audio)
    {
        double bpm; double phaseIn; long stamp; bool fresh;
        lock (_tempoLock) { bpm = _bpm; phaseIn = _phaseIn; stamp = _tempoStamp; fresh = _tempoNew; _tempoNew = false; }
        double beatInc = bpm > 0 ? bpm / 60.0 / Sr : 0;
        if (fresh && beatInc > 0 && double.IsFinite(phaseIn))
        {
            // age the measured phase by the time since the UI took it; re-lock only when it really drifted (> ~5 ms)
            double ph = phaseIn + (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) / (double)System.Diagnostics.Stopwatch.Frequency * bpm / 60;
            double d = ph - (_beatPos - Math.Floor(_beatPos)); d -= Math.Round(d);
            if (Math.Abs(d) > .01) _beatPos += d;
        }
        while (_cmds.TryDequeue(out var cmd))
        {
            switch (cmd)
            {
                case CaptureCmd c: _pendingCapture = c; _capWait = c.Delay; break;
                case PlayCmd p: _pendingPlay = p; _playWait = p.Delay; break;
                case StopPadCmd: _pendingPlay = null; _loop = false; _stopPad = true; break;
                case LoopCmd l: _loop = l.On; if (_pendingPlay != null) _pendingPlay = _pendingPlay with { Loop = l.On }; break;
                case SpinCmd: _spinT = 0; break;
                case ResetCmd: _spinT = -1; _pendingPlay = null; _stopPad = true; _loop = false; break;
            }
        }
        for (int i = 0; i + 1 < audio.Length; i += 2)
        {
            float l = audio[i], r = audio[i + 1];
            double prevBeat = _beatPos; _beatPos += beatInc;
            bool edge = beatInc > 0 && Math.Floor(_beatPos) > Math.Floor(prevBeat);

            if (_pendingCapture != null && _capWait-- <= 0)
            {
                var c = _pendingCapture; _pendingCapture = null;
                int len = Math.Min(c.Length, _filled);
                for (int j = 0; j < len; j++)
                {
                    int h = (_w - len + j + Capacity * 2) % Capacity;
                    var pb = _pads[c.Pad]!; pb[j * 2] = _history[h * 2]; pb[j * 2 + 1] = _history[h * 2 + 1];
                }
                Volatile.Write(ref _length[c.Pad], len);
            }
            if (_pendingPlay != null && _playWait-- <= 0)
            {
                var p = _pendingPlay; _pendingPlay = null;
                if (_length[p.Pad] > 0) { _active = p.Pad; _at = 0; _loop = p.Loop; _stopPad = false; _playingPad = p.Pad; }
            }
            _history[_w * 2] = l; _history[_w * 2 + 1] = r;
            if (_filled < Capacity) _filled++;

            // ---- tape engine
            double ts;
            if (_spinT >= 0) { ts = SpinSpeed(_spinT); _spinT += 1.0 / Sr; if (_spinT >= SpinSeconds) _spinT = -1; }
            else if (_reverse) ts = -1;
            else ts = FxCurves.BendSpeed(_bend);
            float ol = l, or = r;
            if (!_tape && ts < .999 && !_xfade) { _tape = true; _lag = 0; _speed = 1; }
            if (_tape)
            {
                double limit;
                if (ts < _speed) limit = (_reverse || _spinT >= 0) ? 1.0 / (Sr * .006) * 2 : 1.0 / (FxCurves.BrakeSeconds(_bend) * Sr);
                else limit = 1.0 / (FxCurves.SpinUpSeconds(Math.Max(0, _speed)) * Sr);
                _speed += Math.Clamp(ts - _speed, -limit, limit);
                _lag = Math.Clamp(_lag + 1 - _speed, 0, MaxLag);
                double rp = _w - _lag; if (rp < 0) rp += Capacity;
                int a = (int)rp % Capacity, b = (a + 1) % Capacity; float f = (float)(rp - Math.Floor(rp));
                float tl = _history[a * 2] * (1 - f) + _history[b * 2] * f, tr = _history[a * 2 + 1] * (1 - f) + _history[b * 2 + 1] * f;
                float g = (float)FxCurves.BendGain(Math.Abs(_speed));
                tl *= g; tr *= g;
                bool settled = ts >= .999 && _speed >= .999;
                if (settled && !_xfade && (_lag < 1 || bpm <= 0 || edge || _lag >= MaxLag - 1)) { _xfade = true; _xf = 0; }
                if (_xfade)
                {
                    _xf += 1f / Ramp;
                    float t = Math.Min(1f, _xf);
                    ol = tl * (1 - t) + l * t; or = tr * (1 - t) + r * t;
                    if (_xf >= 1f) { _tape = false; _xfade = false; _lag = 0; _speed = 1; }
                }
                else { ol = tl; or = tr; }
                _stateCode = _xfade || (settled && _lag > 0) ? 4 : _spinT >= 0 ? 3 : _reverse ? 2 : 1;
            }
            else _stateCode = 0;

            // ---- pads: replace the deck with the captured beat; edges blend through 5 ms so seams never click
            float goal = _active >= 0 && !_stopPad ? 1 : 0;
            _padMix += Math.Clamp(goal - _padMix, -1f / Ramp, 1f / Ramp);
            if (_active >= 0)
            {
                int n = _length[_active];
                float env = Math.Min(1f, Math.Min(_at, n - 1 - _at) / (float)Ramp) * _padMix;
                var pd = _pads[_active]!; float pl = pd[_at * 2], pr = pd[_at * 2 + 1];
                ol += (pl - ol) * env; or += (pr - or) * env;
                float lv = Math.Max(Math.Abs(pl), Math.Abs(pr)) * env;
                if (lv > Levels[_active]) Levels[_active] = lv;
                if (++_at >= n) { _at = 0; if (!_loop) _stopPad = true; }
                if (_stopPad && _padMix <= 0) { _active = -1; _playingPad = -1; _stopPad = false; }
            }
            audio[i] = float.IsFinite(ol) ? ol : 0; audio[i + 1] = float.IsFinite(or) ? or : 0;
            _w = (_w + 1) % Capacity;
        }
        for (int p = 0; p < PadCount; p++) Levels[p] *= .9f;
    }
    private bool _stopPad;
}
