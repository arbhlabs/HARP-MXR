using System.Collections.Concurrent;

namespace MixDock.Audio;

public enum VoiceKind { Slice, Chord, Drum }

/// <summary>What a pad hit asks the engine to play.</summary>
/// <param name="Key">pad key (bank * 4 + pad); a new hit on the same key replaces the old voice through a 5 ms fade</param>
/// <param name="Group">choke group (-1 = none): starting a voice fades out every other un-latched voice of the group</param>
/// <param name="Velocity">MIDI velocity 1..127, scales amplitude (and the chord synth's brightness)</param>
/// <param name="Latched">layer mode: keeps sounding (loops / sustains) until unlatched</param>
/// <param name="SustainWhileHeld">chord pads: sound until NoteOff; false = plays out (one-shot)</param>
public readonly record struct PadVoiceSpec(int Key, VoiceKind Kind, int Group, int Velocity, bool Latched, bool SustainWhileHeld,
    int[]? Notes = null, int SlicePad = 0, DrumSound Drum = DrumSound.Kick);

/// <summary>
/// Render-thread polyphonic pad player for one deck: slices captured by the <see cref="PerformanceSampler"/> (one-shot / layer),
/// built-in drum one-shots and the chord synth. Up to <see cref="MaxVoices"/> voices sound at once; the oldest is stolen
/// through a 5 ms fade. Every start, stop, steal and release is a 5 ms (chord release 70 ms) amplitude ramp, so nothing clicks.
/// A pad hit costs one queue write on the UI thread; the voice starts on the very next audio sample (plus the quantize delay).
/// The output is added into the deck buffer, so the deck's FX chain, fader and crossfader apply to it.
/// </summary>
public sealed class PadEngine
{
    public const int MaxVoices = 8, Ramp = 220, ChordReleaseFrames = 3087, MinChordHoldFrames = 4410, SlotCount = 16;
    private readonly PerformanceSampler? _sampler;

    private sealed class Voice
    {
        public bool Alive, Releasing, Latched, Held, Loop, Sustain;
        public VoiceKind Kind; public int Key, Group; public long Seq;
        public float Gain, Amp, RelStep;
        public int At, MinHold;
        public int SlicePad; public float[]? Drum; public int DrumPos, LoopLen;
        public readonly ChordVoiceDsp Chord = new();
    }
    private sealed record Cmd(int Type, PadVoiceSpec Spec, int Delay, int Key = 0);   // 0 start, 1 note-off, 2 unlatch key, 3 unlatch all, 4 stop key, 5 kill all
    private sealed class Pending { public PadVoiceSpec Spec; public int Wait; public bool ReleasedEarly; public bool Used; }

    private readonly ConcurrentQueue<Cmd> _cmds = new();
    private readonly Voice[] _v = Enumerable.Range(0, SlotCount).Select(_ => new Voice()).ToArray();
    private readonly Pending[] _pending = Enumerable.Range(0, 32).Select(_ => new Pending()).ToArray();
    private long _seq;
    private volatile int _playingMask, _activeVoices, _aliveVoices;
    private double _bpm = 120;
    /// <summary>Bit k set = pad key k is sounding right now.</summary>
    public int PlayingMask => _playingMask;
    /// <summary>Voices that are not fading out (never above <see cref="MaxVoices"/>).</summary>
    public int ActiveVoices => _activeVoices;
    /// <summary>All voices still producing sound, including ones fading out.</summary>
    public int AliveVoices => _aliveVoices;
    public readonly float[] Levels = new float[16];
    public void SetTempo(double bpm) { if (double.IsFinite(bpm) && bpm > 0) _bpm = Math.Clamp(bpm, 40, 300); }

    public PadEngine(PerformanceSampler? sampler = null) { _sampler = sampler; }

    // ---- UI-thread API (queue writes only)
    public void Trigger(PadVoiceSpec spec, int delayFrames = 0) => _cmds.Enqueue(new Cmd(0, spec, Math.Max(0, delayFrames)));
    /// <summary>Pad released: ends chord voices that are only held (and cancels a not-yet-started launch).</summary>
    public void NoteOff(int key) => _cmds.Enqueue(new Cmd(1, default, 0, key));
    public void Unlatch(int key) => _cmds.Enqueue(new Cmd(2, default, 0, key));
    public void UnlatchAll() => _cmds.Enqueue(new Cmd(3, default, 0));
    public void Stop(int key) => _cmds.Enqueue(new Cmd(4, default, 0, key));
    public void KillAll() => _cmds.Enqueue(new Cmd(5, default, 0));

    private void BeginRelease(Voice v, int frames)
    {
        if (!v.Alive || v.Releasing) return;
        v.Releasing = true; v.RelStep = Math.Max(v.Amp, .0001f) / Math.Max(1, frames);
    }

    private void Start(in PadVoiceSpec s, bool releasedEarly)
    {
        // retrigger and choke: fade the old voices of this key / group
        foreach (var o in _v)
            if (o.Alive && !o.Releasing && (o.Key == s.Key || (s.Group >= 0 && o.Group == s.Group && !o.Latched))) BeginRelease(o, Ramp);
        // steal the oldest while the voice limit is reached
        while (CountActive() >= MaxVoices)
        {
            Voice? oldest = null;
            foreach (var o in _v) if (o.Alive && !o.Releasing && (oldest == null || o.Seq < oldest.Seq)) oldest = o;
            if (oldest == null) break;
            BeginRelease(oldest, Ramp);
        }
        Voice? slot = null;
        foreach (var o in _v) if (!o.Alive) { slot = o; break; }
        if (slot == null) foreach (var o in _v) if (slot == null || o.Amp < slot.Amp) slot = o;   // pathological: all 16 busy; take the quietest fading one
        slot ??= _v[0];
        slot.Alive = true; slot.Releasing = false; slot.Kind = s.Kind; slot.Key = s.Key; slot.Group = s.Group; slot.Seq = ++_seq;
        slot.Latched = s.Latched; slot.Sustain = s.SustainWhileHeld; slot.Held = !releasedEarly; slot.Loop = s.Latched;
        slot.Gain = PadGain(s.Velocity); slot.Amp = 0; slot.At = 0; slot.MinHold = s.Kind == VoiceKind.Chord ? MinChordHoldFrames : 0;
        slot.SlicePad = s.SlicePad; slot.DrumPos = 0;
        if (s.Kind == VoiceKind.Drum) { slot.Drum = DrumKit.Get(s.Drum); slot.LoopLen = Math.Max(slot.Drum.Length, (int)(60.0 / _bpm * FxCurves.SampleRate)); }
        else if (s.Kind == VoiceKind.Chord) slot.Chord.Start(s.Notes ?? Array.Empty<int>(), s.Velocity / 127.0);
        else if (_sampler == null || _sampler.Length(s.SlicePad) <= 0) { slot.Alive = false; return; }
    }
    private static float PadGain(int velocity) => Math.Clamp(velocity, 1, 127) / 127f;
    private int CountActive() { int n = 0; foreach (var o in _v) if (o.Alive && !o.Releasing) n++; return n; }

    public void Process(Span<float> audio)
    {
        while (_cmds.TryDequeue(out var c))
        {
            switch (c.Type)
            {
                case 0:
                {
                    Pending? p = null;
                    foreach (var x in _pending) if (!x.Used) { p = x; break; }
                    if (c.Delay == 0 || p == null) Start(c.Spec, false);
                    else { p.Used = true; p.Spec = c.Spec; p.Wait = c.Delay; p.ReleasedEarly = false; }
                    break;
                }
                case 1:
                    foreach (var p in _pending) if (p.Used && p.Spec.Key == c.Key) p.ReleasedEarly = true;
                    foreach (var o in _v) if (o.Alive && o.Key == c.Key) { o.Held = false; }
                    break;
                case 2:
                    foreach (var p in _pending) if (p.Used && p.Spec.Key == c.Key) p.Spec = p.Spec with { Latched = false };
                    foreach (var o in _v) if (o.Alive && o.Key == c.Key && o.Latched) { o.Latched = false; o.Loop = false; if (!o.Held || o.Kind != VoiceKind.Chord) BeginRelease(o, o.Kind == VoiceKind.Chord ? ChordReleaseFrames : Ramp); }
                    break;
                case 3:
                    foreach (var p in _pending) if (p.Used && p.Spec.Latched) p.Used = false;
                    foreach (var o in _v) if (o.Alive && o.Latched) { o.Latched = false; o.Loop = false; BeginRelease(o, o.Kind == VoiceKind.Chord ? ChordReleaseFrames : Ramp); }
                    break;
                case 4:
                    foreach (var p in _pending) if (p.Used && p.Spec.Key == c.Key) p.Used = false;
                    foreach (var o in _v) if (o.Alive && o.Key == c.Key) BeginRelease(o, Ramp);
                    break;
                case 5:
                    foreach (var p in _pending) p.Used = false;
                    foreach (var o in _v) if (o.Alive) { o.Latched = false; BeginRelease(o, Ramp); }
                    break;
            }
        }
        bool any = false;
        foreach (var o in _v) if (o.Alive) { any = true; break; }
        foreach (var p in _pending) if (p.Used) { any = true; break; }
        if (!any) { _playingMask = 0; return; }

        for (int i = 0; i + 1 < audio.Length; i += 2)
        {
            foreach (var p in _pending)
                if (p.Used && --p.Wait <= 0) { p.Used = false; Start(p.Spec, p.ReleasedEarly); }
            float sl = 0, sr = 0;
            foreach (var v in _v)
            {
                if (!v.Alive) continue;
                float l = 0, r = 0, edge = 1;
                switch (v.Kind)
                {
                    case VoiceKind.Slice:
                    {
                        int n = _sampler!.Length(v.SlicePad);
                        if (n <= 0) { v.Alive = false; continue; }
                        if (v.At >= n) { if (v.Loop) v.At = 0; else { v.Alive = false; continue; } }
                        var d = _sampler.PadData(v.SlicePad);
                        l = d[v.At * 2]; r = d[v.At * 2 + 1];
                        edge = Math.Min(1f, Math.Min(v.At, n - 1 - v.At) / (float)Ramp);
                        v.At++;
                        break;
                    }
                    case VoiceKind.Drum:
                    {
                        var d = v.Drum!;
                        if (v.DrumPos >= (v.Loop ? v.LoopLen : d.Length)) { if (v.Loop) v.DrumPos = 0; else { v.Alive = false; continue; } }
                        l = r = v.DrumPos < d.Length ? d[v.DrumPos] : 0;
                        v.DrumPos++;
                        break;
                    }
                    default:
                        v.Chord.Next(out l, out r);
                        break;
                }
                // amplitude envelope: 5 ms attack from the very first sample; release by ramp; held chords end on note-off after a minimum stab length
                if (v.Releasing)
                {
                    v.Amp -= v.RelStep;
                    if (v.Amp <= 0) { v.Alive = false; v.Amp = 0; continue; }
                }
                else
                {
                    if (v.Amp < 1) v.Amp = Math.Min(1f, v.Amp + 1f / Ramp);
                    if (v.Kind == VoiceKind.Chord && v.Sustain)
                    {
                        v.At++;   // frames since the chord started: a quick tap still gets a minimum stab length
                        if (!v.Held && !v.Latched && v.At >= v.MinHold) BeginRelease(v, ChordReleaseFrames);
                    }
                }
                float a = v.Amp * v.Gain * edge;
                float ol = l * a, or = r * a;
                if (float.IsFinite(ol)) sl += ol;
                if (float.IsFinite(or)) sr += or;
                int k = v.Key & 15; float lv = Math.Max(Math.Abs(ol), Math.Abs(or)); if (lv > Levels[k]) Levels[k] = lv;
            }
            audio[i] += sl; audio[i + 1] += sr;
        }
        int mask = 0, active = 0, alive = 0;
        foreach (var v in _v) if (v.Alive) { alive++; if (!v.Releasing) { active++; mask |= 1 << (v.Key & 15); } }
        _playingMask = mask; _activeVoices = active; _aliveVoices = alive;
        for (int k = 0; k < 16; k++) Levels[k] *= .9f;
    }
}
