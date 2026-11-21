namespace MixDock.Audio;

/// <summary>
/// Pulls one deck's raw captured audio at render time: bounded delay (latency target), optional
/// variable-rate resampling (FILE decks only), a slow delay "slew" used for phase alignment, then the
/// deck's EQ/filter/trim DSP. DSP runs here (not at capture) so knob moves are heard immediately.
/// Underruns never produce a hard zero: the last sample is held and faded out, and playback resumes
/// as soon as ~40 ms is buffered again.
/// </summary>
public sealed class DeckReader
{
    private const int Rate44 = 44100;
    public const int DefaultTargetMs = 120;
    private readonly SampleRing _ring;
    private readonly DeckDsp _dsp;
    private float[] _src = new float[8192];
    private int _srcFrames;
    private double _pos;
    private bool _started;
    private volatile bool _rebuffer, _trimNow, _slew;
    private bool _fullRestart;
    private double _rate = 1.0;
    private int _targetFrames = Rate44 * DefaultTargetMs / 1000;
    private float _lastL, _lastR;
    private int _underrun, _overrun;

    /// <summary>Times the output ran dry (audible as a dropout).</summary>
    public int Underruns => Volatile.Read(ref _underrun);
    /// <summary>Times the buffer overfilled and old audio had to be dropped.</summary>
    public int Overruns => Volatile.Read(ref _overrun);

    public DeckReader(SampleRing ring, DeckDsp dsp) { _ring = ring; _dsp = dsp; }

    // ---- history mode (continuous beat sync): the deck plays from its SampleHistory through a HistoryPlayer, so the
    // playhead is an exact absolute frame, speed changes keep pitch (WSOLA) and the phase can be re-aligned by a jump.
    private SampleHistory? _hist; private HistoryPlayer? _player; private int _seenEpoch;
    private double _histRate = 1;
    private bool _histKeyLock = true;
    /// <summary>The deck's history (set by the engine / tests).</summary>
    public SampleHistory? History { get => _hist; set { _hist = value; _player = value == null ? null : new HistoryPlayer(value); } }
    public HistoryPlayer? Player => _player;
    /// <summary>True while the deck plays from its history.</summary>
    public bool HistoryMode => _player is { Seeded: true };
    /// <summary>Speed the history player runs at (render thread writes it through the sync controller).</summary>
    public double HistoryRate { get => Volatile.Read(ref _histRate); set => Volatile.Write(ref _histRate, Math.Clamp(value, 0.5, 2.0)); }

    /// <summary>Absolute input frame of the next frame to be heard (works in both modes).</summary>
    public double PlayheadFrame
    {
        get
        {
            if (_hist == null) return double.NaN;
            if (HistoryMode) return _player!.HeadPos;
            lock (_hist.Gate) return _hist.Written - (_ring.Count / 2.0 + _srcFrames - _pos);
        }
    }
    /// <summary>Speed the deck is really running at (history rate or the legacy resample rate).</summary>
    public double EffectiveRate => HistoryMode ? HistoryRate : Rate;

    /// <summary>Switch to history mode, continuing seamlessly from the audio that is playing right now.</summary>
    public bool EnterHistory(bool keyLock)
    {
        if (_hist == null || _player == null) return false;
        if (HistoryMode) { if (_player.KeyLock != keyLock) _player.KeyLock = keyLock; return true; }
        double pos;
        lock (_hist.Gate) pos = _hist.Written - (_ring.Count / 2.0 + _srcFrames - _pos);
        if (pos < _hist.ContinuousFrom) pos = _hist.ContinuousFrom;
        _histKeyLock = keyLock; _player.KeyLock = keyLock;
        _player.Seed(pos); _seenEpoch = _hist.Epoch; HistoryRate = 1;
        _ring.Clear(); _srcFrames = 0; _pos = 0;
        return true;
    }
    /// <summary>Leave history mode (source change): the legacy ring path takes over from an empty buffer.</summary>
    public void LeaveHistory() { _player?.Unseed(); _started = false; _srcFrames = 0; _pos = 0; if (_hist != null) _hist.Playhead = -1; }

    private void ReadHistory(Span<float> dst)
    {
        var pl = _player!; var h = _hist!;
        _ring.Clear();
        if (h.Epoch != _seenEpoch) { _seenEpoch = h.Epoch; pl.Seed(Math.Max(h.ContinuousFrom, 0)); }
        int frames = dst.Length / 2;
        int n = pl.Render(dst, HistoryRate);
        if (n < frames)
        {
            Interlocked.Increment(ref _underrun);
            PadHold(dst, n * 2);
        }
        h.Playhead = (long)pl.HeadPos;
        _lastL = dst[dst.Length - 2]; _lastR = dst[dst.Length - 1];
        ApplyRoll(dst);
        _dsp.Process(dst, dst);
    }

    /// <summary>Playback speed 1.0 = real time (0.85..1.15). Only FILE decks may leave 1.0 for long.</summary>
    public double Rate { get => Volatile.Read(ref _rate); set => Volatile.Write(ref _rate, Math.Clamp(value, 0.85, 1.15)); }

    public int TargetMs
    {
        get => Volatile.Read(ref _targetFrames) * 1000 / Rate44;
        set
        {
            int t = Rate44 * Math.Clamp(value, 20, 3000) / 1000;
            if (t > _targetFrames + Rate44 / 50) _rebuffer = true;          // deeper delay: re-buffer (short gap)
            else if (t < _targetFrames - Rate44 / 50) _trimNow = true;      // shallower: drop the excess
            Volatile.Write(ref _targetFrames, t);
        }
    }

    /// <summary>Move to a new delay WITHOUT a gap: the buffer is walked there at +-3 % speed.</summary>
    public void SlewDelayTo(int ms)
    {
        Volatile.Write(ref _targetFrames, Rate44 * Math.Clamp(ms, 20, 2500) / 1000);
        _slew = true;
    }

    /// <summary>How much audio is buffered ahead of playback, in ms.</summary>
    public int BufferedMs => HistoryMode ? (int)(_player!.Lead * 1000 / Rate44) : (_ring.Count / 2 + _srcFrames) * 1000 / Rate44;

    public void ResetState() { _srcFrames = 0; _pos = 0; _started = false; }

    private void PadHold(Span<float> dst, int fromSample)
    {
        // hold the last frame and fade it out over ~3 ms instead of dropping to zero (no click)
        int frames = (dst.Length - fromSample) / 2;
        float l = fromSample >= 2 ? dst[fromSample - 2] : _lastL, r = fromSample >= 2 ? dst[fromSample - 1] : _lastR;
        for (int i = 0; i < frames; i++)
        {
            float g = Math.Max(0f, 1f - i / 128f);
            dst[fromSample + 2 * i] = l * g; dst[fromSample + 2 * i + 1] = r * g;
        }
    }

    public void Read(Span<float> dst)
    {
        if (HistoryMode) { ReadHistory(dst); return; }
        int targetS = _targetFrames * 2;
        if (_rebuffer) { _rebuffer = false; _started = false; _fullRestart = true; }
        if (_trimNow) { _trimNow = false; _srcFrames = 0; _pos = 0; _ring.Trim(targetS); }
        if (_ring.Count > targetS * 2 + 2 * Rate44 / 10 && !_slew) { _ring.Trim(targetS); Interlocked.Increment(ref _overrun); }
        if (!_started)
        {
            int restart = _fullRestart ? targetS : Math.Min(targetS, 2 * Rate44 * 40 / 1000);
            if (_ring.Count + _srcFrames * 2 >= restart && _ring.Count > 0) { _started = true; _fullRestart = false; }
            else { PadHold(dst, 0); _lastL = _lastR = 0; _dsp.Process(dst, dst); return; }
        }
        double rate = Rate;
        if (_slew)
        {
            int err = BufferedMs - TargetMs;
            if (Math.Abs(err) <= 6) _slew = false;
            else rate = err < 0 ? 0.97 : 1.03;
        }
        int frames = dst.Length / 2;
        if (Math.Abs(rate - 1.0) < 1e-4 && _srcFrames == 0)
        {
            int got = _ring.Read(dst);
            if (got < dst.Length)
            {
                Interlocked.Increment(ref _underrun);
                PadHold(dst, got);
                if (got == 0) _started = false;
            }
        }
        else
        {
            int need = (int)Math.Ceiling(_pos + frames * rate) + 2;
            if (_src.Length < need * 2) Array.Resize(ref _src, need * 2 + 4096);
            if (_srcFrames < need)
            {
                int want = Math.Min(need - _srcFrames, _ring.Count / 2);
                if (want > 0)
                {
                    _ring.Read(_src.AsSpan(_srcFrames * 2, want * 2));
                    _srcFrames += want;
                }
            }
            int i = 0;
            for (; i < frames; i++)
            {
                int idx = (int)_pos;
                if (idx + 1 >= _srcFrames) break;
                float f = (float)(_pos - idx);
                dst[2 * i] = _src[2 * idx] * (1 - f) + _src[2 * idx + 2] * f;
                dst[2 * i + 1] = _src[2 * idx + 1] * (1 - f) + _src[2 * idx + 3] * f;
                _pos += rate;
            }
            if (i < frames)
            {
                Interlocked.Increment(ref _underrun);
                PadHold(dst, i * 2);
                if (i == 0) _started = false;
            }
            int drop = Math.Min((int)_pos, _srcFrames);
            if (drop > 0)
            {
                Array.Copy(_src, drop * 2, _src, 0, (_srcFrames - drop) * 2);
                _srcFrames -= drop; _pos -= drop;
            }
            if (Math.Abs(rate - 1.0) < 1e-4 && _srcFrames == 0) _pos = 0;
        }
        _lastL = dst[dst.Length - 2]; _lastR = dst[dst.Length - 1];
        ApplyRoll(dst);
        _dsp.Process(dst, dst);
    }

    // ---- slip loop / loop roll, render side: works on LIVE app decks (no seeking needed) and file decks ----
    // The heard stream keeps running underneath. During the first pass the loop is simply the live audio, which is
    // recorded into _loop; later passes replay it. Leaving the roll crossfades back to where the song is NOW (slip).
    public const int MaxLoopFrames = Rate44 * 8;
    private const int Xf = 128;                                   // ~3 ms seam / exit crossfade
    private readonly float[] _loop = new float[MaxLoopFrames * 2];
    private readonly float[] _lead = new float[Xf * 2];          // audio just before the loop start (seam crossfade)
    private readonly float[] _recent = new float[Xf * 2]; private int _recentW;
    private sealed record RollRequest(int DelayFrames, int LengthFrames, bool Stop, bool Resize);
    private RollRequest? _rollReq;
    private long _framesOut, _rollPendingAt = -1, _rollStart = -1;
    private int _rollLen, _rollPendingLen, _captured;
    private bool _rollOn;
    private float _rollMix;
    private volatile bool _rollingPublic;

    /// <summary>True while a roll/loop is engaged (or pending its quantized start).</summary>
    public bool Rolling => _rollingPublic;
    /// <summary>Start a roll after delayFrames (beat quantization), looping lengthFrames.</summary>
    public void StartRoll(int delayFrames, int lengthFrames)
        { Volatile.Write(ref _rollReq, new RollRequest(Math.Max(0, delayFrames), Math.Clamp(lengthFrames, 64, MaxLoopFrames), false, false)); _rollingPublic = true; }
    /// <summary>Change the length of the active loop (shorter always works; longer only within what was recorded).</summary>
    public void ResizeRoll(int lengthFrames) => Volatile.Write(ref _rollReq, new RollRequest(0, Math.Clamp(lengthFrames, 64, MaxLoopFrames), false, true));
    public void StopRoll() { Volatile.Write(ref _rollReq, new RollRequest(0, 0, true, false)); _rollingPublic = false; }

    private void ApplyRoll(Span<float> dst)
    {
        var req = Interlocked.Exchange(ref _rollReq, null);
        if (req != null)
        {
            if (req.Stop) { _rollOn = false; _rollPendingAt = -1; }
            else if (req.Resize) { if (_rollOn && req.LengthFrames <= _captured) _rollLen = req.LengthFrames; }
            else { _rollPendingAt = _framesOut + req.DelayFrames; _rollPendingLen = req.LengthFrames; }
        }
        if (!_rollOn && _rollMix <= 0 && _rollPendingAt < 0)
        {
            // idle: only keep the short lead-in history
            for (int i = 0; i + 1 < dst.Length; i += 2) { _recent[_recentW] = dst[i]; _recent[_recentW + 1] = dst[i + 1]; _recentW = (_recentW + 2) % _recent.Length; }
            _framesOut += dst.Length / 2;
            return;
        }
        for (int i = 0; i + 1 < dst.Length; i += 2, _framesOut++)
        {
            float l = dst[i], r = dst[i + 1];
            if (_rollPendingAt >= 0 && _framesOut >= _rollPendingAt)
            {
                _rollPendingAt = -1; _rollOn = true; _rollStart = _framesOut; _rollLen = _rollPendingLen; _captured = 0; _rollMix = 1;
                for (int k = 0; k < Xf * 2; k++) _lead[k] = _recent[(_recentW + k) % _recent.Length];   // oldest -> newest
            }
            _recent[_recentW] = l; _recent[_recentW + 1] = r; _recentW = (_recentW + 2) % _recent.Length;
            if (_rollStart < 0) continue;
            long k2 = _framesOut - _rollStart;
            if (_rollOn && k2 < MaxLoopFrames && _captured == k2) { _loop[2 * k2] = l; _loop[2 * k2 + 1] = r; _captured++; }
            int j = (int)(k2 % _rollLen);
            float rl, rr;
            if (k2 < _rollLen) { rl = l; rr = r; }                       // first pass = the live audio itself
            else { rl = _loop[2 * j]; rr = _loop[2 * j + 1]; }
            int fromEnd = _rollLen - j;
            if (fromEnd <= Xf)                                           // seam: fade the loop end into the lead-in that precedes its start
            {
                float t = 1f - fromEnd / (float)Xf; int li = Xf - fromEnd;
                rl = rl * (1 - t) + _lead[2 * li] * t; rr = rr * (1 - t) + _lead[2 * li + 1] * t;
            }
            if (!_rollOn) { _rollMix -= 1f / Xf; if (_rollMix <= 0) { _rollMix = 0; _rollStart = -1; continue; } }
            dst[i] = l + (rl - l) * _rollMix; dst[i + 1] = r + (rr - r) * _rollMix;
        }
    }
}
