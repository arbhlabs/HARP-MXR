namespace MixDock.Audio;

/// <summary>
/// Variable-rate player on a <see cref="SampleHistory"/> (render thread only). The playhead is a fractional ABSOLUTE input
/// frame, so tempo/phase maths can use exact positions. Two engines:
///  - key-lock (default): WSOLA time stretch, 1024-frame windows / 512-frame hops, waveform-matched within +-0.9 ms so the
///    audible beat never strays more than that from the nominal playhead; pitch does not change;
///  - varispeed: linear interpolation (pitch follows speed), sample-exact timing.
/// A <see cref="Jump"/> (re-phase, replay/skip a bar) is crossfaded equal-power over 8 ms (configurable).
/// </summary>
public sealed class HistoryPlayer
{
    public const int Hs = 512;
    public int ToleranceFrames { get; set; } = 40;
    private readonly SampleHistory _h;
    private bool _keyLock = true;
    private double _nom;                        // nominal input position of the next hop to be produced (WSOLA) / varispeed playhead
    private long _c;                            // WSOLA: natural continuation of the previous segment
    private bool _seeded;
    private double _jumpDelta; private int _jumpXf; private bool _jumpPending;
    private readonly float[] _fifo = new float[Hs * 2 * 4];
    private int _fRead, _fCount;                // frames
    private readonly (double Pos, double Rate, int Left)[] _recs = new (double, double, int)[8];
    private int _rHead, _rCount;
    private readonly float[] _tailM = new float[Hs];
    private readonly float[] _fall = new float[Hs], _rise = new float[Hs];
    // varispeed crossfade
    private double _oldPos; private int _xfLeft, _xfLen;
    public int Underruns { get; private set; }
    public bool KeyLock { get => _keyLock; set { if (value != _keyLock) { double p = HeadPos; _keyLock = value; Seed(p); } } }

    public HistoryPlayer(SampleHistory h)
    {
        _h = h;
        for (int j = 0; j < Hs; j++)
        {
            double c = Math.Cos(Math.PI * j / (2.0 * Hs));
            _fall[j] = (float)(c * c); _rise[j] = (float)(1 - c * c);
        }
    }

    public bool Seeded => _seeded;
    /// <summary>Nominal input position (absolute frame) of the next output frame.</summary>
    public double HeadPos => _keyLock ? (_rCount > 0 ? _recs[_rHead].Pos : _nom) : _nom;
    /// <summary>Frames of audio between the playhead and the newest captured frame.</summary>
    public double Lead => _h.Written - HeadPos;

    /// <summary>Start playing at <paramref name="pos"/> (continues seamlessly from audio that was already playing at that spot).</summary>
    public void Seed(double pos)
    {
        _nom = pos; _c = (long)Math.Round(pos); _seeded = true; _fCount = 0; _fRead = 0; _rCount = 0; _rHead = 0;
        _jumpPending = false; _xfLeft = 0;
    }
    public void Unseed() { _seeded = false; _fCount = 0; _rCount = 0; }

    /// <summary>Move the playhead by <paramref name="deltaFrames"/> (negative = replay) with an equal-power crossfade.</summary>
    /// <summary>Live app decks: the playhead may never move back (no replay of audio that was already heard).</summary>
    public bool ForwardOnly { get; set; }
    /// <summary>Backward jumps that were refused because the deck is forward-only.</summary>
    public int RefusedBackJumps { get; private set; }

    public void Jump(double deltaFrames, int xfadeFrames = 352)
    {
        if (ForwardOnly && deltaFrames + _jumpDelta < 0)
        {
            RefusedBackJumps++;
            if (_jumpDelta <= 0) { _jumpDelta = 0; _jumpPending = false; return; }
            deltaFrames = -_jumpDelta;      // only cancels a forward jump that has not happened yet
        }
        _jumpDelta += deltaFrames; _jumpXf = Math.Clamp(xfadeFrames, 64, Hs); _jumpPending = true;
        if (!_keyLock) ApplyVariJump();
    }
    private void ApplyVariJump()
    {
        if (!_jumpPending) return;
        _oldPos = _nom; _nom += _jumpDelta; _xfLen = _xfLeft = _jumpXf; _jumpDelta = 0; _jumpPending = false;
    }

    /// <summary>Renders up to dst.Length/2 frames at the given speed; returns frames produced (less on underrun).</summary>
    public int Render(Span<float> dst, double rate)
    {
        int frames = dst.Length / 2;
        if (!_seeded) return 0;
        rate = Math.Clamp(double.IsFinite(rate) ? rate : 1, 0.5, 2);
        return _keyLock ? RenderWsola(dst, frames, rate) : RenderVari(dst, frames, rate);
    }

    private int RenderVari(Span<float> dst, int frames, double rate)
    {
        long written = _h.Written;
        int i = 0;
        for (; i < frames; i++)
        {
            long need = (long)_nom + 2;
            if (need >= written || !_h.Has((long)_nom, written)) break;
            _h.Interp(_nom, out float l, out float r);
            if (_xfLeft > 0)
            {
                _h.Interp(_oldPos, out float lo, out float ro);
                double t = 1.0 - (double)_xfLeft / _xfLen, a = Math.Sin(t * Math.PI / 2), b = Math.Cos(t * Math.PI / 2);
                l = (float)(l * a + lo * b); r = (float)(r * a + ro * b);
                _oldPos += rate; _xfLeft--;
            }
            dst[2 * i] = l; dst[2 * i + 1] = r;
            _nom += rate;
        }
        if (i < frames) Underruns++;
        return i;
    }

    private int RenderWsola(Span<float> dst, int frames, double rate)
    {
        while (_fCount < frames)
        {
            if (!ProduceHop(rate)) break;
        }
        int n = Math.Min(frames, _fCount);
        for (int i = 0; i < n; i++)
        {
            int k = ((_fRead + i) % (Hs * 4)) * 2;
            dst[2 * i] = _fifo[k]; dst[2 * i + 1] = _fifo[k + 1];
        }
        _fRead = (_fRead + n) % (Hs * 4); _fCount -= n;
        int left = n;
        while (left > 0 && _rCount > 0)
        {
            ref var rec = ref _recs[_rHead];
            int take = Math.Min(left, rec.Left);
            rec.Pos += take * rec.Rate; rec.Left -= take; left -= take;
            if (rec.Left == 0) { _rHead = (_rHead + 1) % _recs.Length; _rCount--; }
        }
        if (n < frames) Underruns++;
        return n;
    }

    private bool ProduceHop(double rate)
    {
        if (_fCount + Hs > Hs * 4 || _rCount >= _recs.Length) return false;
        long written = _h.Written;
        int tol = ToleranceFrames;
        bool jump = _jumpPending;
        if (jump) { _nom += _jumpDelta; }
        long nominal = (long)Math.Round(_nom);
        // data needed: this hop's head [s, s+Hs) with s <= nominal+tol, and the tail [_c, _c+Hs) which is already in history
        if (nominal + tol + Hs + 1 > written || !_h.Has(nominal - tol, written) || !_h.Has(_c, written) || _c + Hs + 1 > written)
        {
            if (jump) _nom -= _jumpDelta;       // retry next time
            return false;
        }
        long s = nominal; int xf = Hs;
        float[] tail = _tailM;
        for (int j = 0; j < Hs; j++) tail[j] = _h.Mono(_c + j);
        if (jump) { xf = _jumpXf; _jumpPending = false; _jumpDelta = 0; }
        else if (tol > 0)
        {
            double eT = 0; for (int j = 0; j < Hs; j += 2) eT += tail[j] * tail[j];
            double best = double.NegativeInfinity;
            for (int d = -tol; d <= tol; d++)
            {
                long cs = nominal + d; double dot = 0, eH = 0;
                for (int j = 0; j < Hs; j += 2) { float x = _h.Mono(cs + j); dot += x * tail[j]; eH += x * x; }
                double ncc = dot / Math.Sqrt(eH * eT + 1e-9);
                double score = ncc - 0.04 * Math.Abs(d) / tol;
                if (score > best) { best = score; s = cs; }
            }
        }
        int w = (_fRead + _fCount) % (Hs * 4);
        for (int j = 0; j < Hs; j++)
        {
            int k = ((w + j) % (Hs * 4)) * 2;
            _h.Interp(_c + j, out float tl, out float tr);
            _h.Interp(s + j, out float hl, out float hr);
            double fall, rise;
            if (jump) { double t = j < xf ? (double)j / xf : 1.0; fall = Math.Cos(t * Math.PI / 2); rise = Math.Sin(t * Math.PI / 2); }
            else { fall = _fall[j]; rise = _rise[j]; }
            _fifo[k] = (float)(tl * fall + hl * rise); _fifo[k + 1] = (float)(tr * fall + hr * rise);
        }
        _fCount += Hs;
        int idx = (_rHead + _rCount) % _recs.Length;
        _recs[idx] = (_nom, rate, Hs); _rCount++;
        _c = s + Hs;
        _nom += rate * Hs;
        return true;
    }
}
