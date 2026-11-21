using System.Diagnostics;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;
using MixDock.Ui;

namespace MixDock;

/// <summary>1.3 "MIDI Pad": 16 pads in 4 banks, per-bank modes, scale-locked chords, trigger velocity, quantized launches, LB layers, MIDI in/out and the pad grid.</summary>
public sealed partial class MainForm
{
    private const double KeyConfidenceMin = 0.2;
    private int _bank;
    private readonly BeatClock[] _clocks = { new(), new() };
    private readonly PadGridState _grid = new();
    private PadGridView _gridCenter = null!, _gridFloat = null!;
    private readonly bool[] _latched = new bool[16];
    private readonly int[] _latchDeck = new int[16];
    private readonly int[]?[] _latchNotes = new int[16][];
    private readonly int[] _heldKey = { -1, -1, -1, -1 }, _heldDeck = new int[4];
    private readonly BankMode[] _heldMode = new BankMode[4];
    private readonly bool[] _heldLegacy = new bool[4];
    private readonly int[]?[] _heldNotes = new int[4][];
    private readonly long[] _heldOnTicks = new long[4];
    private readonly long[] _hitAt = new long[16], _pendingDue = new long[16];
    private readonly int[] _hitVel = new int[16];
    private readonly bool[] _dawLit = new bool[16];
    private readonly ZoneSelector _invSel = ZoneSelector.ForInversion(), _voiceSel = ZoneSelector.ForVoicing();
    private int _inv; private Voicing _voicing = Voicing.Close;
    private string _lastChordText = "";
    private bool _lbWas;
    private MidiService? _midi; private WinmmIn? _midiIn;
    private double _midiFx;
    private readonly int[] _lastCc = { -1, -1, -1, -1, -1, -1 };
    private readonly float[] _padLv = new float[4];

    private BankMode ModeOf(int bank) => _config.Banks[Math.Clamp(bank, 0, 3)].Mode;

    private void InitBanks()
    {
        _bank = Math.Clamp(_config.ActiveBank, 0, 3);
        _gridFloat = new PadGridView(compact: false);
        Controls.Add(_gridFloat);
        Resize += (_, _) => PlaceGridFloat();
        if (_preview || ShotPath != null) return;
        // restore captured slices
        for (int d = 0; d < 2; d++)
            for (int k = 0; k < 16; k++)
                if (SliceStore.Load(d, k) is { } sl) Sampler(d).LoadSlice(k, sl.Data, sl.Frames);
        StartMidi();
    }

    // ------------------------------------------------------------------ key
    private MusicalKey CurrentKey(int deck, out string source)
    {
        var det = _engine.Mixer.Key(deck);
        if (_config.KeyAuto && det.Key is MusicalKey k && det.Confidence >= KeyConfidenceMin) { source = "auto"; return k; }
        source = _config.KeyAuto ? "manual, no key heard yet" : "manual";
        return new MusicalKey(_config.ManualKeyPc, _config.ManualKeyMinor);
    }
    private void StepKey(int d, int semitones)
    {
        var cur = CurrentKey(d, out _);
        _config.KeyAuto = false;
        _config.ManualKeyPc = (cur.PitchClass + semitones + 120) % 12; _config.ManualKeyMinor = cur.Minor;
        SetMessage($"Chord key: {ChordTheory.KeyName(new MusicalKey(_config.ManualKeyPc, _config.ManualKeyMinor))} (manual; Shift+D-pad left/right steps it, the MIDI window sets Auto).");
        RumblePulse(.2, 30);
    }

    // ------------------------------------------------------------------ bank / mode / quantize actions
    private void SetBank(int bank)
    {
        bank = Math.Clamp(bank, 0, 3);
        bool changed = bank != _bank;
        _bank = bank; _config.ActiveBank = bank;
        AnnounceMove($"BANK {bank + 1} · {PadBanks.ModeName(ModeOf(bank))}");
        SetMessage($"Bank {bank + 1}: {PadBanks.ModeName(ModeOf(bank))} (held pads keep sounding until released).");
        if (changed) { MidiPost(MidiMsg.Cc(_config.MidiChannel, 26, bank + 1)); MidiPost(MidiMsg.Program(_config.MidiChannel, bank)); }
        RumblePulse(.12, 25);
    }
    private void CycleBankMode()
    {
        var b = _config.Banks[_bank];
        b.Mode = PadBanks.NextMode(b.Mode);
        AnnounceMove($"BANK {_bank + 1} → {PadBanks.ModeName(b.Mode)}");
        SetMessage($"Bank {_bank + 1} is now {PadBanks.ModeName(b.Mode)}.");
        RumblePulse(.25, 50);
    }
    private void CycleQuantize()
    {
        _config.Quantize = PadQuant.Next(_config.Quantize);
        AnnounceMove($"QUANTIZE {PadQuant.Label(_config.Quantize)}");
        SetMessage($"Pad quantize: {PadQuant.Label(_config.Quantize)}{(_config.Quantize == QuantGrid.Off ? " (pads fire immediately)" : " (pads wait for the next grid line)")}.");
        RumblePulse(.2, 40);
    }

    private double MsToLaunch(int d)
    {
        var (bpm, _) = Tempo(d);
        return PadQuant.MsToNext(_clocks[d].At(Environment.TickCount64), bpm, _config.Quantize);
    }
    private int FramesToLaunch(int d) => (int)Math.Round(MsToLaunch(d) * 44.1);

    // ------------------------------------------------------------------ MIDI
    private void MidiPost(MidiEvent e) { try { _midi?.Post(e); } catch { } }
    private static long DueTicks(double ms) => ms <= 0 ? 0 : Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);

    private void StartMidi()
    {
        try
        {
            _midiIn?.Dispose(); _midiIn = null; _midi?.Dispose(); _midi = null;
            if (!_config.MidiEnabled) { _midi = new MidiService(null, "MIDI: off"); return; }
            _midi = MidiPorts.Open(_config.MidiOutPort);
            _midi.ClockEnabled = _config.MidiClock;
            if (_midi.Port is TeVirtualMidi te) _midi.Receiver = () => te.Receive();
            _midiIn = WinmmIn.TryOpen(_midi, _config.MidiInPort);
            Log.Write(_midi.Status + (_midiIn != null ? $"; input {_midiIn.Name}" : ""));
        }
        catch (Exception ex) { Log.Write("MIDI start: " + ex.Message); _midi = new MidiService(null, "MIDI: no virtual port"); }
    }

    private void MidiAxes(PadFrame f)
    {
        if (_midi?.Port == null) return;
        int[] v = { MidiMsg.AxisToCc(f.LX), MidiMsg.AxisToCc(f.LY), MidiMsg.AxisToCc(f.RX), MidiMsg.AxisToCc(f.RY), MidiMsg.Scale127(f.LT), MidiMsg.Scale127(f.RT) };
        for (int i = 0; i < 6; i++)
            if (v[i] != _lastCc[i]) { _lastCc[i] = v[i]; MidiPost(MidiMsg.Cc(_config.MidiChannel, 20 + i, v[i])); }
    }

    private void DrainMidiIn()
    {
        if (_midi == null) return;
        int guard = 0;
        while (guard++ < 64 && _midi.TryReadIncoming(out var e))
        {
            int type = e.Status & 0xF0;
            if (type == 0x90 && e.D2 > 0 || type == 0x80 || type == 0x90)
            {
                int idx = PadBanks.PadFromNote(_config.MidiNoteBase, e.D1);
                if (idx >= 0) _dawLit[idx] = type == 0x90 && e.D2 > 0;
            }
            else if (type == 0xB0 && e.D1 == _config.MidiFxCc) _midiFx = Math.Clamp(e.D2 / 127.0, 0, 1);
        }
    }

    // ------------------------------------------------------------------ pads
    private int VelocityNow(bool full)
    {
        if (!_config.VelocitySensitive) return 100;
        int v = PadBanks.Velocity(_lastFrame.RT, PadBanks.CurveGamma(_config.VelocityCurve), _config.VelocityMin);
        return full ? Math.Max(v, 100) : v;
    }
    /// <summary>True while a held pad is in a velocity bank (One-shot / Chord / Drum): RT then means velocity, not beat roll.</summary>
    private bool VelocityPadHeld()
    {
        for (int p = 0; p < 4; p++) if (_heldKey[p] >= 0 && !_heldLegacy[p]) return true;
        return false;
    }

    private string PadText(int bank, int pad, bool alt)
    {
        var mode = ModeOf(bank);
        switch (mode)
        {
            case BankMode.Chord:
            {
                var key = CurrentKey(_focus, out _); var b = _config.Banks[bank];
                return ChordTheory.ChordName(key, (alt ? b.AltDegrees : b.Degrees)[pad]);
            }
            case BankMode.Drum: return DrumKit.Name(DrumKit.ForPad(pad, alt));
            default: return $"{pad + 1}";
        }
    }

    private void BankPadDown(int d, int pad, bool alt)
    {
        try { BankPadDownCore(d, pad, alt); }
        catch (Exception ex) { Log.Write("pad down: " + ex); }
    }
    private void BankPadDownCore(int d, int pad, bool alt)
    {
        int bank = _bank, key = PadBanks.Index(bank, pad); var mode = ModeOf(bank);
        long now = Environment.TickCount64;
        if (_latched[key]) { UnlatchPad(key); AnnounceMove($"UNLATCH {PadText(bank, pad, alt)}", d); return; }
        if (_heldKey[pad] >= 0) BankPadUp(pad);   // a release we never saw
        bool layer = (_padMapper.Held & PadButton.LeftShoulder) != 0;
        var (bpm, _) = Tempo(d);
        var sm = Sampler(d); var eng = _engine.Mixer.Pads(d); eng.SetTempo(bpm);
        double ms = MsToLaunch(d); int delay = (int)Math.Round(ms * 44.1);
        bool legacy = mode == BankMode.Sample && !layer;
        int vel = VelocityNow(full: legacy);      // Sample pads keep the 1.2 full level; MIDI still reports the trigger depth
        int midiVel = _config.VelocitySensitive ? PadBanks.Velocity(_lastFrame.RT, PadBanks.CurveGamma(_config.VelocityCurve), _config.VelocityMin) : 100;
        int[]? notes = null; string what = "";
        if (legacy)
        {
            if (!PadPlay(d, key, true)) return;
            _heldLegacy[pad] = true; what = $"SAMPLE {pad + 1}";
        }
        else
        {
            switch (mode)
            {
                case BankMode.Chord:
                {
                    var k = CurrentKey(d, out _); var b = _config.Banks[bank];
                    int degree = (alt ? b.AltDegrees : b.Degrees)[pad];
                    notes = ChordTheory.Build(k, degree, _inv, _voicing);
                    eng.Trigger(new PadVoiceSpec(key, VoiceKind.Chord, -1, vel, layer, true, notes), delay);
                    what = ChordTheory.Describe(k, degree, _inv, _voicing); _lastChordText = what;
                    break;
                }
                case BankMode.Drum:
                {
                    var snd = DrumKit.ForPad(pad, alt);
                    eng.Trigger(new PadVoiceSpec(key, VoiceKind.Drum, -1, vel, layer, false, Drum: snd), delay);
                    what = DrumKit.Name(snd);
                    break;
                }
                default:   // One-shot, or a Sample pad layered with LB (additive, looped while latched)
                {
                    if (sm.Length(key) == 0) { PadCapture(d, pad); return; }   // empty: grab the last beat; the next press plays it
                    eng.Trigger(new PadVoiceSpec(key, VoiceKind.Slice, layer ? -1 : bank, vel, layer, false, SlicePad: key), delay);
                    what = mode == BankMode.Sample ? $"SAMPLE {pad + 1} LAYER" : $"ONE-SHOT {pad + 1}";
                    break;
                }
            }
            if (layer) { _latched[key] = true; _latchDeck[key] = d; _latchNotes[key] = notes; }
        }
        _heldKey[pad] = key; _heldDeck[pad] = d; _heldMode[pad] = mode; _heldNotes[pad] = notes; _heldOnTicks[pad] = DueTicks(ms);
        _hitAt[key] = now; _hitVel[key] = midiVel; _pendingDue[key] = ms > 0 ? now + (long)ms : 0;
        // MIDI: pad note on channel 1, chord notes on channel 2, both at the moment the sound starts
        long due = DueTicks(ms);
        MidiPost(MidiMsg.NoteOn(_config.MidiChannel, PadBanks.Note(_config.MidiNoteBase, bank, pad), midiVel, due));
        if (notes != null) foreach (int n in notes) MidiPost(MidiMsg.NoteOn(_config.MidiChordChannel, n, midiVel, due));
        AnnounceMove($"{what} · V{midiVel}{(ms > 0 ? $" · {PadQuant.Label(_config.Quantize)}" : "")}", d);
        RumblePulse(.08 + .5 * midiVel / 127.0, 30);
    }

    private void BankPadUp(int pad)
    {
        try
        {
            int key = _heldKey[pad]; if (key < 0) return;
            _heldKey[pad] = -1;
            int d = _heldDeck[pad];
            if (_heldLegacy[pad]) { _heldLegacy[pad] = false; PadPlay(d, key, false); MidiPadOff(pad, key, null); return; }
            if (_latched[key]) return;               // layered: keeps sounding until LB is released or the pad is hit again
            _engine.Mixer.Pads(d).NoteOff(key);
            MidiPadOff(pad, key, _heldNotes[pad]);
        }
        catch (Exception ex) { Log.Write("pad up: " + ex); }
    }
    private void MidiPadOff(int pad, int key, int[]? notes)
    {
        long on = _heldOnTicks[pad], now = Stopwatch.GetTimestamp();
        long due = on > now ? on + Stopwatch.Frequency / 50 : 0;     // a tap shorter than the quantize wait still gets a note of 20 ms
        MidiPost(MidiMsg.NoteOff(_config.MidiChannel, PadBanks.Note(_config.MidiNoteBase, key / 4, key % 4), due));
        if (notes != null) foreach (int n in notes) MidiPost(MidiMsg.NoteOff(_config.MidiChordChannel, n, due));
    }

    private void UnlatchPad(int key)
    {
        if (!_latched[key]) return;
        _latched[key] = false;
        _engine.Mixer.Pads(_latchDeck[key]).Unlatch(key);
        MidiPost(MidiMsg.NoteOff(_config.MidiChannel, PadBanks.Note(_config.MidiNoteBase, key / 4, key % 4)));
        if (_latchNotes[key] is { } ns) foreach (int n in ns) MidiPost(MidiMsg.NoteOff(_config.MidiChordChannel, n));
        _latchNotes[key] = null;
    }
    private void ReleaseLayers()
    {
        bool any = false;
        for (int k = 0; k < 16; k++) if (_latched[k]) { any = true; UnlatchPad(k); }
        if (any) { _engine.Mixer.PadsA.UnlatchAll(); _engine.Mixer.PadsB.UnlatchAll(); AnnounceMove("LAYER RELEASED"); }
    }

    /// <summary>Shift + pad: capture (Sample / One-shot banks) or the alternative chord / drum set (Chord / Drum banks).</summary>
    private void PadAlt(int d, int pad, bool down)
    {
        var mode = ModeOf(_bank);
        if (mode is BankMode.Chord or BankMode.Drum) { if (down) BankPadDown(d, pad, true); else BankPadUp(pad); return; }
        if (down) PadCapture(d, pad);
    }

    private void ResetPadBank()
    {
        for (int p = 0; p < 4; p++) { _heldKey[p] = -1; _heldLegacy[p] = false; }
        Array.Clear(_latched); Array.Clear(_pendingDue);
        MidiPost(MidiMsg.Cc(_config.MidiChannel, 123, 0)); MidiPost(MidiMsg.Cc(_config.MidiChordChannel, 123, 0));
    }

    // ------------------------------------------------------------------ per-tick work
    /// <summary>Every poll, controller or not: beat clocks (quantize, MIDI clock), incoming MIDI, pending-launch bookkeeping.</summary>
    private void ClockTick(long nowMs)
    {
        for (int d = 0; d < 2; d++) { var (b, p) = Tempo(d); _clocks[d].Update(b, p, nowMs); }
        if (_midi != null)
        {
            _midi.ClockEnabled = _config.MidiClock && _config.MidiEnabled;
            var c = _clocks[_focus]; _midi.SetClock(c.At(nowMs), c.Bpm);
        }
        DrainMidiIn();
        for (int k = 0; k < 16; k++) if (_pendingDue[k] != 0 && nowMs >= _pendingDue[k]) _pendingDue[k] = 0;
    }
    /// <summary>Every poll with a controller connected: LB layer release and the stick/trigger CCs.</summary>
    private void PadBankTick(PadFrame frame, PadButton buttons)
    {
        bool lb = (buttons & PadButton.LeftShoulder) != 0;
        if (_lbWas && !lb) ReleaseLayers();
        _lbWas = lb;
        MidiAxes(frame);
    }

    private void UpdateGrid()
    {
        if (_gridCenter is null) return;
        var g = _grid; long now = Environment.TickCount64; bool shift = _padMapper.Shift;
        g.Bank = _bank; g.Focus = _focus; g.Shift = shift; g.Connected = _padWasConnected;
        g.Layer = (_padMapper.Held & PadButton.LeftShoulder) != 0;
        for (int b = 0; b < 4; b++) g.Modes[b] = ModeOf(b);
        var key = CurrentKey(_focus, out string src);
        g.KeyText = $"KEY {ChordTheory.KeyName(key)} ({src})";
        g.ChordText = ModeOf(_bank) == BankMode.Chord
            ? (_lastChordText.Length > 0 ? _lastChordText : $"{ChordTheory.InversionNames[Math.Clamp(_inv, 0, 2)]} / {ChordTheory.VoicingNames[(int)_voicing]}")
            : "";
        g.QuantText = "QUANT " + PadQuant.Label(_config.Quantize);
        g.MidiText = _midi?.Status ?? "MIDI: off";
        var sm = Sampler(_focus); int mask = _engine.Mixer.Pads(_focus).PlayingMask | _engine.Mixer.Pads(1 - _focus).PlayingMask;
        for (int k = 0; k < 16; k++)
        {
            int b = k / 4, p = k % 4; var mode = g.Modes[b];
            g.Filled[k] = mode is BankMode.Chord or BankMode.Drum || sm.Length(k) > 0;
            g.Labels[k] = mode switch
            {
                BankMode.Chord or BankMode.Drum => PadText(b, p, shift),
                _ => g.Filled[k] ? (mode == BankMode.Sample ? "SMP" : "ONE") : ""
            };
            bool held = false; for (int q = 0; q < 4; q++) if (_heldKey[q] == k) held = true;
            g.Held[k] = held; g.Latched[k] = _latched[k];
            g.Playing[k] = (mask & (1 << k)) != 0 || sm.PlayingPad == k;
            g.Pending[k] = _pendingDue[k] != 0; g.DawLit[k] = _dawLit[k];
            g.HitAt[k] = _hitAt[k]; g.Velocity[k] = _hitVel[k];
        }
        _gridCenter.SetState(g);
        if (_gridFloat.Visible) _gridFloat.SetState(g);
    }

    private void UpdatePadRows()
    {
        Span<int> lens = stackalloc int[4];
        for (int d = 0; d < 2; d++)
        {
            var sm = _engine.Mixer.Sampler(d); int off = _bank * 4;
            for (int k = 0; k < 4; k++) { lens[k] = sm.Length(off + k); _padLv[k] = sm.Levels[off + k]; }
            int playing = sm.PlayingPad - off; if (playing is < 0 or > 3) playing = -1;
            Panel(d).Pads.Set(lens, _padLv, playing);
        }
    }

    // ------------------------------------------------------------------ grid placement / MIDI window
    private void PlaceGridFloat()
    {
        if (_gridFloat is null) return;
        float s = DeviceDpi / 96f;
        int w = Math.Min(ClientSize.Width - (int)(48 * s), (int)(PadGridView.FloatDesignSize.Width * s));
        int h = Math.Min(ClientSize.Height - (int)(160 * s), (int)(PadGridView.FloatDesignSize.Height * s));
        _gridFloat.SetBounds((ClientSize.Width - w) / 2, (int)(120 * s), w, h);
    }
    private void ToggleGridFloat()
    {
        PlaceGridFloat();
        _gridFloat.Visible = !_gridFloat.Visible;
        if (_gridFloat.Visible) { _gridFloat.BringToFront(); UpdateGrid(); }
    }

    private void OpenMidiSettings()
    {
        try
        {
            using var dlg = new MidiSettingsForm(_config, _midi?.Status ?? "MIDI: off");
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                if (!_preview) StartMidi();
                SetMessage(_midi?.Status ?? "MIDI settings saved.");
            }
        }
        catch (Exception ex) { Log.Write("MIDI settings: " + ex.Message); SetMessage("Could not open the MIDI settings.", true); }
    }

    private void SaveSlices()
    {
        if (_preview || ShotPath != null) return;
        for (int d = 0; d < 2; d++)
            for (int k = 0; k < 16; k++)
                if (Sampler(d).CopySlice(k) is { } data) SliceStore.Save(d, k, data);
    }
    private void StopMidi()
    {
        try
        {
            ResetPadBank();
            _midiIn?.Dispose(); _midiIn = null; _midi?.Dispose(); _midi = null;
        }
        catch { }
    }
}
