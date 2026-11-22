using System.Diagnostics;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;

// v1.3.0 "MIDI Pad" tests: banks and mapping, chord theory, key detection, velocity, quantize, layers and voice stealing,
// MIDI encoding and fallback, and the pad-to-audio latency benchmark against the 1.2 sampler path.
int n = 0;
void Assert(bool ok, string t) { if (!ok) throw new Exception("FAIL: " + t); Console.WriteLine("PASS " + t); n++; }
const int Sr = 44100;

// ======================================================================= 1. banks and mapping
{
    Assert(PadBanks.BankFromDpad(PadButton.Up) == 1 && PadBanks.BankFromDpad(PadButton.Right) == 2 && PadBanks.BankFromDpad(PadButton.Down) == 3 && PadBanks.BankFromDpad(PadButton.Left) == 4, "D-pad Up=1 Right=2 Down=3 Left=4");
    Assert(PadBanks.BankFromDpad(PadButton.A) == 0, "non-D-pad button is no bank");
    var keys = new HashSet<int>(); for (int b = 0; b < 4; b++) for (int p = 0; p < 4; p++) keys.Add(PadBanks.Index(b, p));
    Assert(keys.Count == 16 && keys.Min() == 0 && keys.Max() == 15, "4 banks x 4 pads = 16 distinct pad keys");
    Assert(PadBanks.Index(2, 3) == 11 && PadBanks.Index(9, 9) == 15 && PadBanks.Index(-1, -1) == 0, "pad key = bank*4 + pad, clamped");

    var cfg = new AppConfig { ConfigVersion = AppConfig.CurrentVersion }; cfg.ApplyPreset("Pad performer");
    long t = 1000;
    List<PadEvent> Tap(PadMapper m, PadButton b, PadButton hold = 0)
    {
        var ev = new List<PadEvent>();
        ev.AddRange(m.Update(hold | b, t += 20)); ev.AddRange(m.Update(hold | b, t += 50)); ev.AddRange(m.Update(hold, t += 20));
        return ev;
    }
    string Fired(List<PadEvent> ev) => string.Join(",", ev.Where(e => e.Kind == PadEventKind.Press).Select(e => e.Action));
    foreach (var (b, want) in new[] { (PadButton.Up, "Bank:1"), (PadButton.Right, "Bank:2"), (PadButton.Down, "Bank:3"), (PadButton.Left, "Bank:4") })
        Assert(Fired(Tap(cfg.CreatePadMapper(), b)) == want, $"D-pad {b} selects {want}");
    foreach (var (b, want) in new[] { (PadButton.A, "Focus.Pad:1"), (PadButton.B, "Focus.Pad:2"), (PadButton.X, "Focus.Pad:3"), (PadButton.Y, "Focus.Pad:4") })
        Assert(Fired(Tap(cfg.CreatePadMapper(), b)) == want, $"{b} is pad {want[^1]} of the current bank");
    foreach (var (b, want) in new[] { (PadButton.Up, "Focus.Loop"), (PadButton.Down, "Focus.Cue"), (PadButton.Left, "Focus.BeatJumpBack"), (PadButton.Right, "Focus.BeatJumpFwd"),
                 (PadButton.LeftShoulder, "QuantCycle"), (PadButton.RightShoulder, "Focus.Sync"), (PadButton.Start, "PadGrid") })
    {
        var m = cfg.CreatePadMapper(); m.Update(PadButton.Back, t += 20); m.Update(PadButton.Back, t += 100);
        // View + LB/RB is also the profile chord (a quick tap switches profile), so the shifted shoulder action is a HOLD
        // past PadMapper.TapMaxMs; every other button fires its shift action on a normal tap.
        bool shoulder = b is PadButton.LeftShoulder or PadButton.RightShoulder;
        var ev = new List<PadEvent>();
        ev.AddRange(m.Update(PadButton.Back | b, t += 20));
        ev.AddRange(m.Update(PadButton.Back | b, t += shoulder ? PadMapper.TapMaxMs + 50 : 50));
        ev.AddRange(m.Update(PadButton.Back, t += 20)); ev.AddRange(m.Update(0, t += 20));
        Assert(Fired(ev) == want, $"Shift layer: {b} -> {want}{(shoulder ? " (held: a tap is the profile chord)" : "")}");
        if (shoulder)
        {
            var q = cfg.CreatePadMapper(); q.Update(PadButton.Back, t += 20);
            var tap = q.Update(PadButton.Back | b, t += 20); tap.AddRange(q.Update(PadButton.Back, t += 60)); tap.AddRange(q.Update(0, t += 20));
            Assert(Fired(tap) == "" && tap.Any(e => e.Kind is PadEventKind.PreviousProfile or PadEventKind.NextProfile), $"View + quick {b} tap switches the controller profile instead");
        }
    }
    {   // View is the shift button itself, so Shift+View is reached through the LB+RB chord
        var m = cfg.CreatePadMapper(); var lr = PadButton.LeftShoulder | PadButton.RightShoulder;
        var ev = m.Update(PadButton.LeftShoulder, t += 20); ev.AddRange(m.Update(lr, t += 30)); ev.AddRange(Tap(m, PadButton.Back, lr));
        Assert(Fired(ev) == "BankMode", "Shift+View (via the LB+RB chord) cycles the bank mode");
    }
    {   // held pad keeps its pairing while the bank changes: press A, switch bank, release A
        var m = cfg.CreatePadMapper(); var all = new List<PadEvent>();
        all.AddRange(m.Update(PadButton.A, t += 20)); all.AddRange(m.Update(PadButton.A | PadButton.Down, t += 60)); all.AddRange(m.Update(PadButton.A, t += 60)); all.AddRange(m.Update(0, t += 60));
        var seq = all.Select(e => $"{e.Kind}:{e.Action}").ToArray();
        Assert(seq.SequenceEqual(new[] { "Press:Focus.Pad:1", "Press:Bank:3", "Release:Bank:3", "Release:Focus.Pad:1" }), $"bank switch while a pad is held: the pad still gets its own release ({string.Join(" ", seq)})");
    }
    {   // LB held while pads are hit: LB never fires FocusA (it is a layer key then)
        var m = cfg.CreatePadMapper(); var ev = m.Update(PadButton.LeftShoulder, t += 20); ev.AddRange(m.Update(PadButton.LeftShoulder | PadButton.A, t += 60));
        ev.AddRange(m.Update(PadButton.LeftShoulder, t += 60)); ev.AddRange(m.Update(0, t += 60));
        Assert(Fired(ev) == "Focus.Pad:1" && !ev.Any(e => e.Action == "FocusA"), "LB + pad: layer gesture, no deck focus change on LB release");
        var m2 = cfg.CreatePadMapper(); var ev2 = m2.Update(PadButton.LeftShoulder, t += 20); ev2.AddRange(m2.Update(0, t += 60));
        Assert(Fired(ev2) == "FocusA", "a plain LB tap still focuses deck A");
    }

    // modes
    Assert(PadBanks.NextMode(BankMode.Sample) == BankMode.OneShot && PadBanks.NextMode(BankMode.OneShot) == BankMode.Chord && PadBanks.NextMode(BankMode.Chord) == BankMode.Drum && PadBanks.NextMode(BankMode.Drum) == BankMode.Sample, "mode cycle Sample > One-shot > Chord > Drum > Sample");
    Assert(PadBanks.NextMode(BankMode.Chord, drumEnabled: false) == BankMode.Sample, "mode cycle skips Drum when it is disabled");
    Assert(new[] { 0, 1, 2, 3 }.Select(PadBanks.DefaultMode).SequenceEqual(new[] { BankMode.Sample, BankMode.OneShot, BankMode.Chord, BankMode.Drum }), "default modes: Sample, One-shot, Chord, Drum");

    // MIDI note layout
    Assert(PadBanks.Note(36, 0, 0) == 36 && PadBanks.Note(36, 3, 3) == 51 && PadBanks.Note(36, 2, 1) == 45, "pad notes start at 36 (Push / Launchpad drum layout), bank*4+pad");
    Assert(PadBanks.PadFromNote(36, 36) == 0 && PadBanks.PadFromNote(36, 51) == 15 && PadBanks.PadFromNote(36, 52) == -1 && PadBanks.PadFromNote(36, 35) == -1, "incoming note to pad key");

    // config v3 -> v4
    var v3Json = """{"ConfigVersion":3,"Master":61,"GamepadButtons":{"A":"Focus.Pad:1","B":"Focus.Pad:2","X":"Focus.Pad:3","Y":"Focus.Pad:4","LeftShoulder":"FocusA","RightShoulder":"FocusB","Up":"Focus.LoopDouble","Down":"Focus.LoopHalve","Left":"Focus.BeatJumpBack","Right":"Focus.BeatJumpFwd","LeftThumb":"Focus.FilterFineReset","RightThumb":"FxCycle","Back":"Focus.TapBpm","Start":"AutoMix"},"ShiftButtons":{"A":"Focus.PadCapture:1","B":"Focus.PadCapture:2","X":"Focus.PadCapture:3","Y":"Focus.PadCapture:4","LeftShoulder":"Focus.PlayPause","RightShoulder":"CrossfaderReset","Up":"Focus.Loop","Down":"Focus.Cue","Left":"Focus.BrowsePrev","Right":"Focus.BrowseNext","LeftThumb":"Focus.EqReset","RightThumb":"FxFreeze","Back":"Focus.Sync","Start":"Focus.Load"}}""";
    var m3 = AppConfig.FromJson(v3Json);
    Assert(m3.ConfigVersion == AppConfig.CurrentVersion && m3.Master == 61, "v3 config migrates to the current version and keeps other settings");
    { var live = new AppConfig(); Assert(m3.GamepadButtons.Count == live.GamepadButtons.Count && m3.GamepadButtons.All(kv => live.GamepadButtons[kv.Key] == kv.Value) && m3.ShiftButtons.All(kv => live.ShiftButtons[kv.Key] == kv.Value), "untouched v3 layout ends on the Live DJ preset"); }
    {   // a real user file: written back as ConfigVersion 2 by an old build but holding the untouched 1.3 layout
        var p = new AppConfig(); p.ApplyPreset("Pad performer"); p.ConfigVersion = 2;
        var back = AppConfig.FromJson(System.Text.Json.JsonSerializer.Serialize(p));
        Assert(back.GamepadButtons["A"] == "Focus.LowKill" && back.GamepadAxes["LeftThumbX"] == "Focus.FxBlend" && back.ConfigVersion == AppConfig.CurrentVersion, "untouched 1.3 layout saved as v2 becomes Live DJ");
        var edited = new AppConfig(); edited.ApplyPreset("Pad performer"); edited.ConfigVersion = 4; edited.GamepadButtons["A"] = "MasterMute";
        var kept = AppConfig.FromJson(System.Text.Json.JsonSerializer.Serialize(edited));
        Assert(kept.GamepadButtons["A"] == "MasterMute" && kept.GamepadButtons["Up"] == "Bank:1", "an edited 1.3 layout is left exactly as it is");
    }
    {   // every binding in both presets is a real action; nothing is a silent no-op
        foreach (var name in AppConfig.Presets)
        {
            var p = new AppConfig(); p.ApplyPreset(name);
            var unknown = ActionCatalog.Unknown(p.GamepadButtons, p.ShiftButtons, p.GamepadAxes, p.ShiftAxes);
            Assert(unknown.Count == 0, $"preset {name}: every binding is in the action catalog ({string.Join("; ", unknown)})");
        }
        Assert(ActionCatalog.Unknown(new Dictionary<string, string> { ["A"] = "Focus.Nope" }, new Dictionary<string, string>(), new Dictionary<string, string> { ["LeftThumbX"] = "Focus.LowKill" }, new Dictionary<string, string>()).Count == 2, "unknown button action and a button action on an axis are flagged");
        foreach (var axis in new[] { false, true })
            foreach (var d in ActionCatalog.For(axis))
                for (int deck = 0; deck < (d.Scope == ActionScope.Deck ? 3 : 1); deck++)
                {
                    var (rt2, bd) = ActionCatalog.Parse(ActionCatalog.Compose(d, deck), axis);
                    Assert(rt2 == d && bd == deck, $"catalog round trip {d.Id} deck {deck}");
                }
        Assert(ActionCatalog.Parse("A.FocusA", false).Def is null, "a global action never takes a deck prefix");
    }
    Assert(m3.Banks.Count == 4 && m3.Banks.Select(b => b.Mode).SequenceEqual(new[] { BankMode.Sample, BankMode.OneShot, BankMode.Chord, BankMode.Drum }) && m3.Quantize == QuantGrid.Sixteenth && m3.ActiveBank == 0, "v3 config gets default banks and 1/16 quantize");
    Assert(m3.Banks[2].Degrees.SequenceEqual(new[] { 0, 3, 4, 5 }) && m3.Banks[2].AltDegrees.SequenceEqual(new[] { 1, 2, 6, 7 }), "chord pads default to I IV V vi, Shift set ii iii vii I(octave)");
    var custom = AppConfig.FromJson("""{"ConfigVersion":3,"GamepadButtons":{"Up":"Focus.LoopDouble","Down":"MasterMute","A":"Focus.Pad:1"},"ShiftButtons":{"Left":"Focus.BrowsePrev","Start":"MyAction"}}""");
    Assert(custom.GamepadButtons["Up"] == "Bank:1" && custom.GamepadButtons["Down"] == "MasterMute" && custom.ShiftButtons["Start"] == "MyAction" && custom.ShiftButtons["Left"] == "Focus.BeatJumpBack", "migration moves only untouched 1.2 defaults, never a binding the user changed");
    var v4 = AppConfig.FromJson("""{"ConfigVersion":4,"GamepadButtons":{"Up":"Focus.LoopDouble"}}""");
    Assert(v4.GamepadButtons["Up"] == "Focus.LoopDouble", "a 1.3 config is never migrated again");
    // banks persist
    var save = new AppConfig { ConfigVersion = 4, ActiveBank = 2, Quantize = QuantGrid.Eighth, VelocityCurve = "hard", ManualKeyPc = 9, ManualKeyMinor = true, KeyAuto = false };
    save.Banks[0].Mode = BankMode.Drum; save.Banks[3].Mode = BankMode.Chord; save.Banks[3].Degrees = new[] { 1, 2, 4, 5 };
    var rt = AppConfig.FromJson(System.Text.Json.JsonSerializer.Serialize(save));
    Assert(rt.ActiveBank == 2 && rt.Quantize == QuantGrid.Eighth && rt.Banks[0].Mode == BankMode.Drum && rt.Banks[3].Mode == BankMode.Chord && rt.Banks[3].Degrees.SequenceEqual(new[] { 1, 2, 4, 5 }), "banks, modes, chord degrees, active bank and quantize persist in config");
    Assert(rt.VelocityCurve == "hard" && !rt.KeyAuto && rt.ManualKeyPc == 9 && rt.ManualKeyMinor, "velocity curve and manual key persist");
    var bad = AppConfig.FromJson("""{"ConfigVersion":4,"ActiveBank":9,"Banks":[{"Mode":"Chord","Degrees":[1]}],"MidiChannel":40,"VelocityMin":0,"MidiNoteBase":500}""");
    Assert(bad.Banks.Count == 4 && bad.ActiveBank == 3 && bad.Banks[0].Degrees.Length == 4 && bad.MidiChannel == 16 && bad.VelocityMin == 1 && bad.MidiNoteBase == 111, "damaged bank / MIDI settings are repaired, never thrown");
    var dflt = new AppConfig();
    Assert(dflt.MidiChannel == 1 && dflt.MidiChordChannel == 2 && dflt.MidiNoteBase == 36 && dflt.MidiClock && dflt.VelocityMin == 30 && dflt.Quantize == QuantGrid.Sixteenth, "defaults: pads ch1, chords ch2, note base 36, clock on, min velocity 30, quantize 1/16");
}

// ======================================================================= 2. chord theory
{
    string[] majorNames = { "C", "Dm", "Em", "F", "G", "Am", "Bdim" };
    var cMaj = new MusicalKey(0, false);
    Assert(Enumerable.Range(0, 7).Select(d => ChordTheory.ChordName(cMaj, d)).SequenceEqual(majorNames), "C major scale degrees: C Dm Em F G Am Bdim");
    Assert(Enumerable.Range(0, 7).Select(d => ChordTheory.ChordName(new MusicalKey(9, true), d)).SequenceEqual(new[] { "Am", "Bdim", "C", "Dm", "Em", "F", "G" }), "A minor scale degrees: Am Bdim C Dm Em F G");
    Assert(new[] { 0, 3, 4, 5 }.Select(d => ChordTheory.Numeral(cMaj, d)).SequenceEqual(new[] { "I", "IV", "V", "vi" }) && ChordTheory.Numeral(cMaj, 6) == "vii°" && ChordTheory.Numeral(cMaj, 7) == "I'", "roman numerals I IV V vi vii° I'");
    int[] majorQ = { 0, 1, 1, 0, 0, 1, 2 }, minorQ = { 1, 2, 0, 1, 1, 0, 0 };   // 0 major, 1 minor, 2 diminished
    var keys = ChordTheory.AllKeys().ToList();
    Assert(keys.Count == 24 && keys.Distinct().Count() == 24, "24 distinct keys");
    bool allScale = true, allQuality = true, allRange = true, allAscending = true, allRoot = true, allNames = true;
    int combos = 0;
    var names = new[] { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
    foreach (var key in keys)
    {
        var steps = key.Minor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 };
        var scalePcs = steps.Select(s => (key.PitchClass + s) % 12).ToHashSet();
        for (int d = 0; d < 7; d++)
        {
            var tri = ChordTheory.Build(key, d, 0, Voicing.Close);
            if (!tri.All(x => scalePcs.Contains(x % 12))) allScale = false;
            int q = (key.Minor ? minorQ : majorQ)[d];
            string suffix = q == 0 ? "" : q == 1 ? "m" : "dim";
            if (ChordTheory.Quality(key, d) != suffix) allQuality = false;
            if (ChordTheory.ChordName(key, d) != names[(key.PitchClass + steps[d]) % 12] + suffix) allNames = false;
            for (int inv = 0; inv < 3; inv++)
                foreach (var v in Enum.GetValues<Voicing>())
                {
                    var notes = ChordTheory.Build(key, d, inv, v); combos++;
                    if (notes.Min() < 24 || notes.Max() > 108) allRange = false;
                    if (!notes.SequenceEqual(notes.OrderBy(x => x))) allAscending = false;
                    if (!notes.Any(x => x % 12 == (key.PitchClass + steps[d]) % 12)) allRoot = false;
                }
        }
    }
    Assert(allScale, "all 24 keys: every degree's triad uses only notes of the key's scale");
    Assert(allQuality, "all 24 keys: triad qualities follow the scale (major: M m m M M m dim; minor: m dim M m m M M)");
    Assert(allNames, "all 24 keys: chord names match root + quality");
    Assert(allRange && allAscending && allRoot && combos == 24 * 7 * 3 * 4, $"all {combos} key/degree/inversion/voicing combinations: notes ascending, in MIDI range, root present");
    Assert(ChordTheory.Build(cMaj, 0, 0, Voicing.Close).SequenceEqual(new[] { 48, 52, 55 }), "C major I root position = C3 E3 G3 (48 52 55)");
    Assert(ChordTheory.Build(cMaj, 0, 1, Voicing.Close).SequenceEqual(new[] { 52, 55, 60 }), "C major I first inversion = E3 G3 C4");
    Assert(ChordTheory.Build(cMaj, 0, 2, Voicing.Close).SequenceEqual(new[] { 55, 60, 64 }), "C major I second inversion = G3 C4 E4");
    Assert(ChordTheory.Build(cMaj, 0, 0, Voicing.Open).SequenceEqual(new[] { 36, 48, 55, 64 }), "open voicing: bass octave down, root, fifth, third up an octave");
    Assert(ChordTheory.Build(cMaj, 0, 0, Voicing.Add9).SequenceEqual(new[] { 48, 52, 55, 62 }), "add9 voicing adds the 9th above the root (D4)");
    Assert(ChordTheory.Build(cMaj, 0, 0, Voicing.OctaveStack).SequenceEqual(new[] { 48, 52, 55, 60, 64, 67 }), "octave stack doubles the triad an octave up");
    Assert(ChordTheory.Build(new MusicalKey(9, true), 0, 0, Voicing.Close).SequenceEqual(new[] { 57, 60, 64 }), "A minor i = A3 C4 E4");
    Assert(ChordTheory.Build(new MusicalKey(9, true), 0, 1, Voicing.Open).SequenceEqual(new[] { 48, 60, 69, 76 }), "Am first inversion (C E A), open: C2.. bass C3, C4, A4, E5");
    Assert(ChordTheory.Build(cMaj, 5, 0, Voicing.Close).SequenceEqual(new[] { 57, 60, 64 }) && ChordTheory.Build(cMaj, 7, 0, Voicing.Close).SequenceEqual(new[] { 60, 64, 67 }), "vi = Am and degree 7 = I an octave up");
    Assert(ChordTheory.Describe(new MusicalKey(9, true), 0, 1, Voicing.Open) == "Am / 1st inv / open", "chord readout reads 'Am / 1st inv / open'");
    Assert(ChordTheory.KeyName(new MusicalKey(9, true)) == "A minor" && ChordTheory.KeyName(new MusicalKey(6, false)) == "F# major", "key names");

    // right-stick zones with hysteresis
    var invSel = ZoneSelector.ForInversion();
    Assert(invSel.Update(0) == 0 && invSel.Update(-1) == 0 && invSel.Update(.5) == 0, "inversion: rest, left and a nudge stay root position");
    Assert(invSel.Update(.6) == 1 && invSel.Update(.5) == 1 && invSel.Update(.42) == 1, "inversion: crossing 0.45 selects 1st; hysteresis holds it until 0.37");
    Assert(invSel.Update(.3) == 0, "inversion: back to root below the hysteresis band");
    Assert(invSel.Update(1) == 2 && invSel.Update(.75) == 2 && invSel.Update(.6) == 1, "inversion: far right = 2nd, steps back down through 1st");
    var voi = ZoneSelector.ForVoicing();
    Assert(PadBanks.VoicingFromZone(voi.Update(0)) == Voicing.Close && PadBanks.VoicingFromZone(voi.Update(.5)) == Voicing.Open && PadBanks.VoicingFromZone(voi.Update(.9)) == Voicing.Add9 && PadBanks.VoicingFromZone(voi.Update(-.9)) == Voicing.OctaveStack, "voicing: rest close, up open, far up add9, down octave stack");
    var jitter = ZoneSelector.ForVoicing(); jitter.Update(.4); int flips = 0, last = jitter.Zone;
    var rnd = new Random(7); for (int i = 0; i < 2000; i++) { int z = jitter.Update(.35 + (rnd.NextDouble() - .5) * .12); if (z != last) { flips++; last = z; } }
    Assert(flips == 0, "a stick jittering +-0.06 around a zone boundary never flips the voicing");
    Assert(ZoneSelector.ForInversion().Update(double.NaN) == 0, "non-finite stick value is ignored");
}

// ======================================================================= 3. key detector
{
    double Midi(int m) => 440 * Math.Pow(2, (m - 69) / 12.0);
    float[] Song(int[][] chords, double secondsPerChord, int repeats)
    {
        int per = (int)(secondsPerChord * Sr); var o = new float[per * chords.Length * repeats * 2];
        for (int r = 0; r < repeats; r++)
            for (int c = 0; c < chords.Length; c++)
                for (int i = 0; i < per; i++)
                {
                    double t = i / (double)Sr, env = Math.Min(1, t / .02) * Math.Exp(-t / 1.6), s = 0;
                    foreach (int note in chords[c])
                        for (int h = 1; h <= 3; h++) s += Math.Sin(2 * Math.PI * Midi(note) * h * (r * per * chords.Length + c * per + i) / Sr) / (h * h);
                    int idx = ((r * chords.Length + c) * per + i) * 2;
                    o[idx] = o[idx + 1] = (float)(s * env * .08);
                }
        return o;
    }
    MusicalKey? Detect(float[] song, out double conf)
    {
        var kd = new KeyDetector { Background = false };
        for (int i = 0; i + 4096 <= song.Length; i += 4096) kd.Feed(song.AsSpan(i, 4096));
        conf = kd.Confidence; return kd.Key;
    }
    // C major: I vi IV V I (bass + triad)
    var cmaj = Song(new[] { new[] { 36, 48, 52, 55 }, new[] { 45, 57, 60, 64 }, new[] { 41, 53, 57, 60 }, new[] { 43, 55, 59, 62 }, new[] { 36, 48, 52, 55 } }, 1.0, 3);
    var kC = Detect(cmaj, out double cC);
    Assert(kC == new MusicalKey(0, false), $"synthetic C major detected as C major (got {kC}, confidence {cC:0.00})");
    // A minor: i iv v i (natural minor)
    var amin = Song(new[] { new[] { 33, 45, 48, 52 }, new[] { 38, 50, 53, 57 }, new[] { 40, 52, 55, 59 }, new[] { 33, 45, 48, 52 }, new[] { 33, 45, 48, 52 } }, 1.0, 3);
    var kA = Detect(amin, out double cA);
    Assert(kA == new MusicalKey(9, true), $"synthetic A minor detected as A minor (got {kA}, confidence {cA:0.00})");
    // F# major: I IV V I
    var fsmaj = Song(new[] { new[] { 42, 54, 58, 61 }, new[] { 47, 59, 63, 66 }, new[] { 49, 61, 65, 68 }, new[] { 42, 54, 58, 61 }, new[] { 42, 54, 58, 61 } }, 1.0, 3);
    var kF = Detect(fsmaj, out double cF);
    Assert(kF == new MusicalKey(6, false), $"synthetic F# major detected as F# major (got {kF}, confidence {cF:0.00})");
    Assert(cC >= 0.2 && cA >= 0.2 && cF >= 0.2, "confidence on clear tonal material is above the app's 0.2 threshold");
    var silent = new KeyDetector { Background = false }; silent.Feed(new float[8192 * 8]);
    Assert(silent.Key == null, "silence gives no key");
    var noise = new KeyDetector { Background = false }; var nr = new Random(3); var nz = new float[16384 * 4]; for (int i = 0; i < nz.Length; i++) nz[i] = (float)(nr.NextDouble() - .5) * .1f;
    noise.Feed(nz); Assert(noise.Confidence < cC, "broadband noise is less confident than clear tonal music");
    // manual key fallback is what the app uses without a key
    var cfg = new AppConfig { KeyAuto = true };
    Assert(new MusicalKey(cfg.ManualKeyPc, cfg.ManualKeyMinor) == new MusicalKey(0, false), "manual key fallback defaults to C major");
}

// ======================================================================= 4. velocity
{
    Assert(PadBanks.Velocity(0) == 30 && PadBanks.Velocity(0, min: 50) == 50, "RT idle -> minimum velocity 30 (configurable)");
    Assert(PadBanks.Velocity(1) == 127 && PadBanks.Velocity(2) == 127, "RT fully pressed -> 127");
    Assert(PadBanks.Velocity(.5) is 78 or 79, $"RT half, linear -> ~78 (got {PadBanks.Velocity(.5)})");
    Assert(PadBanks.Velocity(.5, PadBanks.CurveGamma("soft")) > PadBanks.Velocity(.5) && PadBanks.Velocity(.5, PadBanks.CurveGamma("hard")) < PadBanks.Velocity(.5), "soft curve is louder than linear, hard is quieter, at the same depth");
    bool mono = true, inRange = true; int prev = 0;
    foreach (string curve in new[] { "soft", "normal", "hard" })
    {
        prev = 0; for (int i = 0; i <= 100; i++) { int v = PadBanks.Velocity(i / 100.0, PadBanks.CurveGamma(curve)); if (v < prev) mono = false; if (v is < 30 or > 127) inRange = false; prev = v; }
    }
    Assert(mono && inRange, "all three curves are monotone and stay within 30..127");
    Assert(PadBanks.Velocity(double.NaN) == 30 && PadBanks.Velocity(-1) == 30 && PadBanks.Velocity(.5, double.NaN) is 78 or 79, "non-finite input falls back safely");
    Assert(PadBanks.CurveGamma("SOFT") < 1 && PadBanks.CurveGamma("hard") > 1 && PadBanks.CurveGamma("nonsense") == 1 && PadBanks.CurveGamma(null) == 1, "curve names");
    Assert(PadBanks.Gain(127) == 1f && Math.Abs(PadBanks.Gain(64) - 64 / 127f) < 1e-6 && PadBanks.Gain(0) == 0, "velocity -> amplitude is linear");
}

// ======================================================================= 5. quantize scheduler
{
    Assert(PadQuant.MsToNext(.6, 120, QuantGrid.Off) == 0, "quantize Off fires immediately");
    Assert(PadQuant.MsToNext(.5, 120, QuantGrid.Sixteenth) == 0, "on a 1/16 line: fire now");
    Assert(Math.Abs(PadQuant.MsToNext(.6, 120, QuantGrid.Sixteenth) - 75) < 1e-6, "1/16 @120 BPM, 50 ms after a line -> wait 75 ms");
    Assert(Math.Abs(PadQuant.MsToNext(.6, 120, QuantGrid.Eighth) - 200) < 1e-6, "1/8 @120 BPM, 50 ms after a line -> wait 200 ms");
    Assert(Math.Abs(PadQuant.MsToNext(.6, 120, QuantGrid.Quarter) - 200) < 1e-6, "1/4 @120 BPM at beat .6 -> wait 200 ms");
    Assert(Math.Abs(PadQuant.MsToNext(5.6, 120, QuantGrid.Bar) - 1200) < 1e-6, "1 bar @120 BPM at beat 5.6 -> wait 1200 ms for beat 8");
    Assert(PadQuant.MsToNext(.05, 120, QuantGrid.Quarter) == 0 && PadQuant.MsToNext(4.04, 120, QuantGrid.Bar) == 0, "a hit up to 30 ms late still fires now");
    Assert(PadQuant.MsToNext(.5, 0, QuantGrid.Quarter) == 0 && PadQuant.MsToNext(double.NaN, 120, QuantGrid.Quarter) == 0 && PadQuant.MsToNext(.5, double.NaN, QuantGrid.Quarter) == 0, "unknown tempo or position: no waiting");
    Assert(PadQuant.FramesToNext(.6, 120, QuantGrid.Eighth) == 8820 && PadQuant.FramesToNext(.6, 120, QuantGrid.Off) == 0, "frames = ms * 44.1");
    Assert(PadQuant.Next(QuantGrid.Off) == QuantGrid.Sixteenth && PadQuant.Next(QuantGrid.Sixteenth) == QuantGrid.Eighth && PadQuant.Next(QuantGrid.Eighth) == QuantGrid.Quarter && PadQuant.Next(QuantGrid.Quarter) == QuantGrid.Bar && PadQuant.Next(QuantGrid.Bar) == QuantGrid.Off, "Shift+LB cycles Off, 1/16, 1/8, 1/4, 1 bar");
    Assert(new[] { QuantGrid.Off, QuantGrid.Sixteenth, QuantGrid.Eighth, QuantGrid.Quarter, QuantGrid.Bar }.Select(PadQuant.Label).SequenceEqual(new[] { "OFF", "1/16", "1/8", "1/4", "1 BAR" }), "quantize labels");

    // beat clock continuity across phase wraps
    var bc = new BeatClock();
    bc.Update(120, .9, 0); bc.Update(120, .95, 25); bc.Update(120, .02, 50); bc.Update(120, .1, 75);
    Assert(bc.Valid && Math.Abs(bc.Position - 1.1) < 1e-9, $"beat clock counts the wrap: .9 -> .02 gives position 1.02 then 1.1 (got {bc.Position:0.000})");
    bc.Update(120, .09, 100); Assert(Math.Abs(bc.Position - 1.09) < 1e-9, "a small backwards jitter does not add a beat");
    Assert(Math.Abs(bc.At(100 + 500) - (1.09 + 1)) < 1e-9, "position extrapolates at the tempo (500 ms = 1 beat at 120 BPM)");
    bc.Update(0, double.NaN, 200); Assert(!bc.Valid && double.IsNaN(bc.At(300)), "unknown tempo makes the clock invalid (pads then fire immediately)");

    // simulated 120 BPM clock: every hit lands exactly on the grid
    var r2 = new Random(11); bool allOnGrid = true; int checkedHits = 0;
    foreach (var g in new[] { QuantGrid.Sixteenth, QuantGrid.Eighth, QuantGrid.Quarter, QuantGrid.Bar })
        for (int i = 0; i < 400; i++)
        {
            double bpm = 90 + r2.NextDouble() * 80, pos = r2.NextDouble() * 64, ms = PadQuant.MsToNext(pos, bpm, g);
            double landing = pos + ms * bpm / 60000.0, grid = PadQuant.Beats(g), rem = landing % grid; if (rem > grid / 2) rem = grid - rem;
            double sinceBefore = (pos % grid) * 60000.0 / bpm;
            double remMs = rem * 60000.0 / bpm;
            bool onGrid = (ms > 0 && remMs < 0.001) || (ms == 0 && (sinceBefore <= PadQuant.LateToleranceMs + 1e-9 || remMs < 0.5));
            if (!onGrid) allOnGrid = false; checkedHits++;
        }
    Assert(allOnGrid, $"{checkedHits} random hits at random tempos land on the grid line (or are within the 30 ms late tolerance) for every grid");

    // engine: a launch with the quantize delay starts exactly that many frames later
    var sampler = new PerformanceSampler();
    foreach (var g in new[] { QuantGrid.Sixteenth, QuantGrid.Eighth, QuantGrid.Quarter, QuantGrid.Bar })
    {
        var eng = new PadEngine(sampler); int delay = PadQuant.FramesToNext(.6, 120, g);
        eng.Trigger(new PadVoiceSpec(0, VoiceKind.Drum, -1, 127, false, false, Drum: DrumSound.Kick), delay);
        int first = FirstNonZero(eng, delay + 2048);
        Assert(delay > 0 && Math.Abs(first - delay) <= 1, $"engine launch on {PadQuant.Label(g)}: first sound at frame {first} for a {delay}-frame quantize wait");
    }
    { var eng = new PadEngine(sampler); eng.Trigger(new PadVoiceSpec(0, VoiceKind.Drum, -1, 127, false, false, Drum: DrumSound.Kick), PadQuant.FramesToNext(.6, 120, QuantGrid.Off));
      Assert(FirstNonZero(eng, 2048) <= 1, "quantize Off: the pad sounds on the very next sample"); }
}

// ======================================================================= 6. layers, latching, voice stealing
{
    var sampler = new PerformanceSampler();
    var sine = new float[Sr * 2]; for (int i = 0; i < Sr; i++) sine[i * 2] = sine[i * 2 + 1] = (float)(.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));
    for (int k = 0; k < 16; k++) sampler.LoadSlice(k, sine, Sr);
    Assert(sampler.Length(15) == Sr && sampler.Length(0) == Sr, "sampler holds 16 pads (4 banks x 4)");

    // 8 voices, the oldest stolen with a 5 ms fade
    var eng = new PadEngine(sampler); var all = new List<float>(); var buf = new float[512];
    void Run(int frames) { for (int done = 0; done < frames; done += 256) { Array.Clear(buf); eng.Process(buf); all.AddRange(buf); } }
    int maxActive = 0;
    for (int k = 0; k < 12; k++) { eng.Trigger(new PadVoiceSpec(k, VoiceKind.Slice, -1, 64, true, false, SlicePad: k)); Run(256); maxActive = Math.Max(maxActive, eng.ActiveVoices); }
    Run(2048);
    Assert(maxActive == 8 && eng.ActiveVoices == 8, $"12 layered pads: never more than 8 voices sounding (max {maxActive})");
    int mask = eng.PlayingMask;
    Assert((mask & 0xF) == 0 && (mask & 0xFF0) == 0xFF0, $"the 4 oldest voices (pads 0-3) were stolen, pads 4-11 still sound (mask {Convert.ToString(mask, 2)})");
    Assert(eng.AliveVoices == 8, "stolen voices are gone after their 5 ms fade");
    // click-free: max sample step through all steals vs the same material without stealing
    double maxStep = 0; for (int i = 2; i < all.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(all[i] - all[i - 2]));
    var all8 = new List<float>(); var eng8 = new PadEngine(sampler);
    for (int k = 0; k < 8; k++) { eng8.Trigger(new PadVoiceSpec(k, VoiceKind.Slice, -1, 64, true, false, SlicePad: k)); for (int b = 0; b < 1; b++) { Array.Clear(buf); eng8.Process(buf); all8.AddRange(buf); } }
    double baseStep = 0; for (int i = 2; i < all8.Count; i++) baseStep = Math.Max(baseStep, Math.Abs(all8[i] - all8[i - 2]));
    Assert(all.All(float.IsFinite), "voice stealing output is finite");
    Assert(maxStep < 0.45, $"voice stealing is click-free: max sample step {maxStep:0.0000} under 0.45 (8 stacked 440 Hz voices, no-steal run {baseStep:0.0000})");
    // single-voice steal, strict threshold
    var e1 = new PadEngine(sampler); var o1 = new List<float>();
    for (int k = 0; k < 9; k++) { e1.Trigger(new PadVoiceSpec(k, VoiceKind.Slice, -1, 127, true, false, SlicePad: k)); }
    // 9 identical-phase voices would be loud; use a quiet single steal: 8 voices at velocity 8, then one more
    var e2 = new PadEngine(sampler); var o2 = new List<float>();
    for (int k = 0; k < 8; k++) { e2.Trigger(new PadVoiceSpec(k, VoiceKind.Slice, -1, 16, true, false, SlicePad: k)); Array.Clear(buf); e2.Process(buf); o2.AddRange(buf); }
    for (int b = 0; b < 4; b++) { Array.Clear(buf); e2.Process(buf); o2.AddRange(buf); }
    int stealAt = o2.Count; e2.Trigger(new PadVoiceSpec(8, VoiceKind.Slice, -1, 16, true, false, SlicePad: 8));
    for (int b = 0; b < 8; b++) { Array.Clear(buf); e2.Process(buf); o2.AddRange(buf); }
    double stealStep = 0, steadyStep = 0;
    for (int i = stealAt; i < o2.Count - 2; i++) stealStep = Math.Max(stealStep, Math.Abs(o2[i] - o2[i - 2]));
    for (int i = 1024; i < stealAt - 2; i += 1) steadyStep = Math.Max(steadyStep, Math.Abs(o2[i] - o2[i - 2]));
    Assert(stealStep < steadyStep * 1.5 + .002, $"a steal fades over 5 ms: max step {stealStep:0.00000} vs steady {steadyStep:0.00000}");

    // latch and release: LB release lets go of every latched pad (5 ms fade), pads keep sounding until then
    var el = new PadEngine(sampler); var ol = new List<float>();
    for (int k = 0; k < 3; k++) el.Trigger(new PadVoiceSpec(k, VoiceKind.Chord, -1, 100, true, true, Notes: new[] { 48 + k, 55 + k, 60 + k }));
    for (int b = 0; b < 40; b++) { Array.Clear(buf); el.Process(buf); ol.AddRange(buf); }
    el.NoteOff(0); el.NoteOff(1); el.NoteOff(2);       // the pads are released but latched
    for (int b = 0; b < 80; b++) { Array.Clear(buf); el.Process(buf); ol.AddRange(buf); }
    Assert(el.ActiveVoices == 3 && Rms(ol.Skip(ol.Count - 512)) > .01, "latched chords keep sounding after the pads are released");
    el.UnlatchAll();
    for (int b = 0; b < 60; b++) { Array.Clear(buf); el.Process(buf); ol.AddRange(buf); }
    Assert(el.AliveVoices == 0 && Rms(ol.Skip(ol.Count - 512)) == 0, "releasing LB ends every latched chord (silent, voices freed)");
    // hitting a latched pad again unlatches just that one
    var eu = new PadEngine(sampler);
    eu.Trigger(new PadVoiceSpec(5, VoiceKind.Chord, -1, 100, true, true, Notes: new[] { 52, 55, 60 })); eu.Trigger(new PadVoiceSpec(6, VoiceKind.Chord, -1, 100, true, true, Notes: new[] { 53, 57, 60 }));
    for (int b = 0; b < 20; b++) { Array.Clear(buf); eu.Process(buf); }
    eu.Unlatch(5); eu.NoteOff(5);
    for (int b = 0; b < 40; b++) { Array.Clear(buf); eu.Process(buf); }
    Assert(eu.AliveVoices == 1 && (eu.PlayingMask & (1 << 6)) != 0 && (eu.PlayingMask & (1 << 5)) == 0, "hitting a latched pad again releases only that pad");
    // layers across banks: pads of different banks (keys 0 and 8) keep sounding together
    var eb = new PadEngine(sampler);
    eb.Trigger(new PadVoiceSpec(0, VoiceKind.Drum, -1, 100, true, false, Drum: DrumSound.Kick)); eb.Trigger(new PadVoiceSpec(8, VoiceKind.Chord, -1, 100, true, true, Notes: new[] { 48, 52, 55 }));
    for (int b = 0; b < 100; b++) { Array.Clear(buf); eb.Process(buf); }
    Assert((eb.PlayingMask & 0x101) == 0x101, "layers survive across banks (bank 1 pad and bank 3 pad both sound)");
    // choke group (one-shot bank): a new hit fades the previous one of the same group, a latched layer is exempt
    var ec = new PadEngine(sampler);
    ec.Trigger(new PadVoiceSpec(4, VoiceKind.Slice, 1, 100, false, false, SlicePad: 4)); for (int b = 0; b < 8; b++) { Array.Clear(buf); ec.Process(buf); }
    ec.Trigger(new PadVoiceSpec(5, VoiceKind.Slice, 1, 100, false, false, SlicePad: 5)); for (int b = 0; b < 8; b++) { Array.Clear(buf); ec.Process(buf); }
    Assert(ec.AliveVoices == 1 && (ec.PlayingMask & (1 << 5)) != 0, "one-shot choke group: the second hit of a bank replaces the first");
    ec.Trigger(new PadVoiceSpec(5, VoiceKind.Slice, 1, 100, false, false, SlicePad: 5)); for (int b = 0; b < 8; b++) { Array.Clear(buf); ec.Process(buf); }
    Assert(ec.AliveVoices == 1, "retriggering a pad replaces its own voice through a 5 ms fade (no stacking)");
    // one-shot plays to the end then stops by itself
    var eo = new PadEngine(sampler); eo.Trigger(new PadVoiceSpec(4, VoiceKind.Slice, 1, 100, false, false, SlicePad: 4));
    for (int b = 0; b < 400; b++) { Array.Clear(buf); eo.Process(buf); }
    Assert(eo.AliveVoices == 0, "a one-shot plays its whole slice once and ends");
    // a quick chord tap is still a stab
    var et = new PadEngine(sampler); var ot = new List<float>();
    et.Trigger(new PadVoiceSpec(2, VoiceKind.Chord, -1, 100, false, true, Notes: new[] { 48, 52, 55 })); et.NoteOff(2);
    for (int b = 0; b < 8; b++) { Array.Clear(buf); et.Process(buf); ot.AddRange(buf); }
    Assert(Rms(ot.Skip(ot.Count - 1024)) > .005, "a tap shorter than the quantize wait still plays a minimum 100 ms stab");
    for (int b = 0; b < 60; b++) { Array.Clear(buf); et.Process(buf); }
    Assert(et.AliveVoices == 0, "the tapped chord releases and frees its voice");
    // KillAll
    var ek = new PadEngine(sampler); for (int k = 0; k < 4; k++) ek.Trigger(new PadVoiceSpec(k, VoiceKind.Chord, -1, 100, true, true, Notes: new[] { 48, 52, 55 }));
    for (int b = 0; b < 10; b++) { Array.Clear(buf); ek.Process(buf); }
    ek.KillAll(); for (int b = 0; b < 4; b++) { Array.Clear(buf); ek.Process(buf); }
    Assert(ek.AliveVoices == 0, "panic KillAll silences every voice within a few ms");
    // empty slice pad plays nothing
    var ee = new PadEngine(new PerformanceSampler()); ee.Trigger(new PadVoiceSpec(3, VoiceKind.Slice, 0, 100, false, false, SlicePad: 3)); Array.Clear(buf); ee.Process(buf);
    Assert(ee.AliveVoices == 0 && buf.All(x => x == 0), "an empty slice pad does nothing");
}

// ======================================================================= 7. synth and drum quality, velocity scaling
{
    var sampler = new PerformanceSampler(); var buf = new float[512];
    float[] Render(PadVoiceSpec spec, int blocks, Action<PadEngine>? mid = null, int midAt = -1)
    {
        var e = new PadEngine(sampler); var o = new List<float>(); e.Trigger(spec);
        for (int b = 0; b < blocks; b++) { if (b == midAt) mid?.Invoke(e); Array.Clear(buf); e.Process(buf); o.AddRange(buf); }
        return o.ToArray();
    }
    foreach (DrumSound d in Enum.GetValues<DrumSound>())
    {
        var k = DrumKit.Get(d);
        Assert(k.Length > 500 && k.All(float.IsFinite) && k.Max(Math.Abs) is > .05f and <= 1f && Math.Abs(k[^1]) < .01f, $"drum {DrumKit.Name(d)}: finite, audible, within +-1 and ends at zero ({k.Length} samples)");
    }
    Assert(DrumKit.ForPad(0, false) == DrumSound.Kick && DrumKit.ForPad(1, false) == DrumSound.Snare && DrumKit.ForPad(2, false) == DrumSound.Hat && DrumKit.ForPad(3, false) == DrumSound.Clap && DrumKit.ForPad(0, true) == DrumSound.Tom, "drum pads: kick, snare, hat, clap; Shift set tom, rim, open hat, crash");
    float Peak(float[] x) => x.Max(Math.Abs);
    var kick127 = Render(new(0, VoiceKind.Drum, -1, 127, false, false, Drum: DrumSound.Kick), 40);
    var kick64 = Render(new(0, VoiceKind.Drum, -1, 64, false, false, Drum: DrumSound.Kick), 40);
    Assert(Math.Abs(Peak(kick127) / Peak(kick64) - 127.0 / 64) < .06, $"velocity scales volume linearly (ratio {Peak(kick127) / Peak(kick64):0.00} vs {127.0 / 64:0.00})");
    var notes = new[] { 48, 52, 55, 60 };
    var chordHard = Render(new(1, VoiceKind.Chord, -1, 127, false, true, Notes: notes), 40);
    var chordSoft = Render(new(1, VoiceKind.Chord, -1, 30, false, true, Notes: notes), 40);
    double Bright(float[] x) { double hi = 0, all = 0; for (int i = 4; i < x.Length; i += 2) { double d = x[i] - 2 * x[i - 2] + x[i - 4]; hi += d * d; all += x[i] * x[i]; } return hi / Math.Max(1e-12, all); }
    Assert(Bright(chordHard) > Bright(chordSoft) * 1.2, $"velocity opens the synth filter: brightness hard {Bright(chordHard):0.0000} > soft {Bright(chordSoft):0.0000}");
    Assert(chordHard.All(float.IsFinite) && Peak(chordHard) < 1.2f && Peak(chordHard) > .05f, $"supersaw chord: finite and bounded (peak {Peak(chordHard):0.00})");
    double dc = chordHard.Skip(2000).Where((_, i) => i % 2 == 0).Average(); Assert(Math.Abs(dc) < .05, $"chord synth has no DC offset ({dc:0.0000})");
    var six = Render(new(1, VoiceKind.Chord, -1, 127, true, true, Notes: new[] { 36, 48, 52, 55, 60, 64, 67, 72 }), 60);
    Assert(six.All(float.IsFinite) && Peak(six) < 1.3f, "an 8-note stack stays bounded");
    // chord release is a ramp: no step bigger than the steady-state material
    var rel = Render(new(1, VoiceKind.Chord, -1, 127, false, true, Notes: notes), 120, e => e.NoteOff(1), 50);
    double steady = 0, relStep = 0; for (int i = 4000; i < 40 * 512; i += 2) steady = Math.Max(steady, Math.Abs(rel[i] - rel[i - 2]));
    for (int i = 50 * 512; i < rel.Length - 2; i += 2) relStep = Math.Max(relStep, Math.Abs(rel[i] - rel[i - 2]));
    Assert(relStep <= steady * 1.1 && rel[^2] == 0, $"chord release is click-free: max step {relStep:0.0000} vs steady {steady:0.0000}, ends silent");
    // attack: the first samples ramp in over ~5 ms
    int firstBig = Array.FindIndex(chordHard, x => Math.Abs(x) > Peak(chordHard) * .5);
    Assert(Math.Abs(chordHard[0]) < Peak(chordHard) * .1 && firstBig >= 20, "chord attack ramps in (declick), first sample is not a full-scale jump");
    var pluck = Math.Abs(ChordVoiceDsp.Cutoff(1, 0) - ChordVoiceDsp.Cutoff(1, 1)); Assert(pluck > 1000 && ChordVoiceDsp.Cutoff(1, 1) > ChordVoiceDsp.Cutoff(.1, 1), "pluck sweep and velocity brightness in the cutoff curve");
    // drum latch loops, and release fades
    var drum = new PadEngine(sampler); drum.SetTempo(120); drum.Trigger(new PadVoiceSpec(0, VoiceKind.Drum, -1, 100, true, false, Drum: DrumSound.Kick));
    for (int b = 0; b < 200; b++) { Array.Clear(buf); drum.Process(buf); }
    Assert(drum.AliveVoices == 1, "a latched drum loops on the beat instead of ending");
    drum.UnlatchAll(); for (int b = 0; b < 4; b++) { Array.Clear(buf); drum.Process(buf); }
    Assert(drum.AliveVoices == 0, "unlatching fades the looping drum out");
    // sampler 16-pad capture
    var sp = new PerformanceSampler(); sp.SetTempo(120, 0);
    var feed = new float[2048]; for (int i = 0; i < feed.Length; i += 2) feed[i] = feed[i + 1] = .3f;
    for (int b = 0; b < 60; b++) sp.Process(feed.AsSpan().ToArray());
    sp.Capture(13, 0, 4000); for (int b = 0; b < 4; b++) sp.Process(feed.AsSpan().ToArray());
    Assert(sp.Length(13) == 4000 && sp.Length(12) == 0 && sp.CopySlice(13) is { Length: 8000 } && sp.CopySlice(12) == null, "pad 13 (bank 4, pad 2) captures independently; empty pads copy as null");
    // slice persistence
    var tmp = Path.Combine(Path.GetTempPath(), "mixdock-slices-" + Guid.NewGuid().ToString("N")); var data = new float[2000]; for (int i = 0; i < data.Length; i++) data[i] = (float)Math.Sin(i * .01);
    Assert(SliceStore.Save(1, 7, data, tmp) && SliceStore.Load(1, 7, tmp) is { Frames: 1000 } ld && ld.Data.SequenceEqual(data) && SliceStore.Load(0, 0, tmp) == null, "captured slices round-trip through the slice store");
    File.WriteAllBytes(Path.Combine(tmp, "d0p01.f32"), new byte[] { 1, 2, 3 }); Assert(SliceStore.Load(0, 1, tmp) == null, "a damaged slice file is ignored");
    try { Directory.Delete(tmp, true); } catch { }
}

// ======================================================================= 8. MIDI
{
    Assert(MidiMsg.Bytes(MidiMsg.NoteOn(1, 36, 100)).SequenceEqual(new byte[] { 0x90, 36, 100 }), "note on ch1: 90 24 64");
    Assert(MidiMsg.Bytes(MidiMsg.NoteOff(1, 36)).SequenceEqual(new byte[] { 0x80, 36, 0 }), "note off ch1: 80 24 00");
    Assert(MidiMsg.Bytes(MidiMsg.NoteOn(2, 60, 127)).SequenceEqual(new byte[] { 0x91, 60, 127 }), "chord note on ch2: 91 3C 7F");
    Assert(MidiMsg.Bytes(MidiMsg.Cc(1, 20, 64)).SequenceEqual(new byte[] { 0xB0, 20, 64 }), "CC 20 (left stick X): B0 14 40");
    Assert(MidiMsg.Bytes(MidiMsg.Program(1, 2)).SequenceEqual(new byte[] { 0xC0, 2 }) && MidiMsg.Bytes(MidiMsg.ClockTick()).SequenceEqual(new byte[] { 0xF8 }), "program change is 2 bytes, clock tick is the single byte F8");
    Assert(MidiMsg.Bytes(MidiMsg.NoteOn(1, 200, 0)).SequenceEqual(new byte[] { 0x90, 127, 1 }) && MidiMsg.Bytes(MidiMsg.Cc(99, 5, 500)).SequenceEqual(new byte[] { 0xBF, 5, 127 }), "values and channels are clamped; note on never has velocity 0");
    Assert(MidiMsg.AxisToCc(-1) == 0 && MidiMsg.AxisToCc(0) == 64 && MidiMsg.AxisToCc(1) == 127 && MidiMsg.AxisToCc(double.NaN) == 64 && MidiMsg.Scale127(0) == 0 && MidiMsg.Scale127(1) == 127, "stick -1..1 and trigger 0..1 map to CC 0..127");
    Assert(MidiMsg.ClockTicksBetween(0, 1) == 24 && MidiMsg.ClockTicksBetween(0, .5) == 12 && MidiMsg.ClockTicksBetween(.99, 1.01) == 1 && MidiMsg.ClockTicksBetween(1, 0) == 0 && MidiMsg.ClockTicksBetween(0, 1000) == 48 && MidiMsg.ClockTicksBetween(double.NaN, 1) == 0, "clock ticks: 24 per beat, none backwards, capped on a jump");
    // CC numbers of the spec: LX/LY/RX/RY = 20-23, LT/RT = 24/25, bank = 26 are used by the app as 20 + index
    Assert(Enumerable.Range(0, 6).Select(i => 20 + i).SequenceEqual(new[] { 20, 21, 22, 23, 24, 25 }), "axis CC map: LX LY RX RY LT RT = CC 20..25, bank = CC 26");

    var fake = new FakePort();
    using (var svc = new MidiService(fake, "MIDI: fake"))
    {
        svc.Post(MidiMsg.NoteOn(1, 36, 99)); svc.Post(MidiMsg.Cc(1, 22, 80)); svc.Post(MidiMsg.NoteOff(1, 36));
        Wait(() => fake.Sent.Count >= 3);
        Assert(fake.Sent.Count == 3 && fake.Sent[0].SequenceEqual(new byte[] { 0x90, 36, 99 }) && fake.Sent[1].SequenceEqual(new byte[] { 0xB0, 22, 80 }) && fake.Sent[2].SequenceEqual(new byte[] { 0x80, 36, 0 }), "the MIDI thread sends queued events to the port in order");
        var sw = Stopwatch.StartNew(); long due = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
        svc.Post(MidiMsg.NoteOn(1, 40, 100, due)); svc.Post(MidiMsg.NoteOn(1, 41, 100));
        Wait(() => fake.Sent.Count >= 4);
        Assert(fake.Sent.Count == 4 && fake.Sent[3][1] == 41, "an event with a due time waits while later immediate events go first");
        Wait(() => fake.Sent.Count >= 5);
        Assert(fake.Sent.Count == 5 && fake.Sent[4][1] == 40 && sw.Elapsed.TotalMilliseconds >= 90 && sw.Elapsed.TotalMilliseconds < 400, $"the delayed (quantized) note arrives at its due time ({sw.Elapsed.TotalMilliseconds:0} ms for a 100 ms wait)");
        int before = fake.Sent.Count; svc.SetClock(0, 120); svc.ClockEnabled = true; var cw = Stopwatch.StartNew();
        Thread.Sleep(1000); svc.ClockEnabled = false; Thread.Sleep(30);
        int ticks = fake.Sent.Skip(before).Count(b => b.Length == 1 && b[0] == 0xF8);
        Assert(ticks is >= 42 and <= 54, $"MIDI clock at 120 BPM sends ~48 ticks per second ({ticks} in {cw.Elapsed.TotalSeconds:0.00} s)");
        var gaps = new List<double>(); for (int i = before + 1; i < fake.Sent.Count && i < fake.At.Count; i++) if (fake.Sent[i][0] == 0xF8 && fake.Sent[i - 1][0] == 0xF8) gaps.Add((fake.At[i] - fake.At[i - 1]) * 1000.0 / Stopwatch.Frequency);
        gaps.Sort(); double p95 = gaps.Count > 10 ? gaps[(int)(gaps.Count * .95)] : double.NaN;
        Console.WriteLine($"INFO MIDI clock tick spacing: median {gaps[gaps.Count / 2]:0.0} ms, p95 {p95:0.0} ms (ideal 20.8 ms)");
        Assert(p95 < 35, $"MIDI clock ticks are evenly spaced (p95 gap {p95:0.0} ms for 20.8 ms ticks): the MIDI thread runs on a 1 ms timer");
        int after = fake.Sent.Count; Thread.Sleep(200);
        Assert(fake.Sent.Count == after, "clock stops when disabled");
        svc.Receiver = new Queue<MidiEvent>(new[] { new MidiEvent(0x90, 36, 100), new MidiEvent(0xB0, 1, 64) }).TryDequeueOrNull;
        Wait(() => { return false; }, 150);
        var got = new List<MidiEvent>(); while (svc.TryReadIncoming(out var ie)) got.Add(ie);
        Assert(got.Count == 2 && got[0].D1 == 36 && got[1].D1 == 1, "incoming DAW notes and CCs reach the UI through the lock-free queue");
    }
    Assert(fake.Disposed, "disposing the service closes the port");
    // Disposal flushes pending all-notes-off
    var fake2 = new FakePort(); var s2 = new MidiService(fake2, "x"); s2.Post(MidiMsg.Cc(1, 123, 0)); s2.Dispose();
    Assert(fake2.Sent.Count == 1 && fake2.Sent[0][1] == 123, "disposing flushes the last queued message (all notes off)");
    // no-port fallback never throws
    MidiService none = null!;
    try { none = MidiPorts.Open(null, () => null, _ => null); Assert(true, "no ports: Open does not throw"); } catch (Exception ex) { Assert(false, "no ports: Open threw " + ex.Message); }
    Assert(none.Status == "MIDI: no virtual port" && none.Port == null, "no-port status reads 'MIDI: no virtual port'");
    try { none.Post(MidiMsg.NoteOn(1, 36, 100)); none.Post(MidiMsg.ClockTick()); none.SetClock(0, 120); none.ClockEnabled = true; Thread.Sleep(30); none.Dispose(); none.Dispose(); Assert(true, "no-port service: Post / clock / double Dispose never throw"); }
    catch (Exception ex) { Assert(false, "no-port service threw " + ex.Message); }
    try { var thr = MidiPorts.Open(null, () => throw new DllNotFoundException("teVirtualMIDI64"), _ => throw new InvalidOperationException("boom")); Assert(thr.Status == "MIDI: no virtual port", "factories that throw degrade to 'no virtual port'"); thr.Dispose(); }
    catch (Exception ex) { Assert(false, "throwing factories escaped: " + ex.Message); }
    var badPort = new FakePort { Throw = true }; using (var sb = new MidiService(badPort, "bad")) { sb.Post(MidiMsg.NoteOn(1, 1, 1)); Thread.Sleep(50); Assert(true, "a port that throws on Send never takes the MIDI thread down"); sb.Post(MidiMsg.NoteOn(1, 2, 1)); Thread.Sleep(30); }
    var real = MidiPorts.Open(null); Console.WriteLine("INFO real port status on this PC: " + real.Status); var outs = MidiPorts.OutputNames(); var ins = MidiPorts.InputNames();
    Console.WriteLine($"INFO winmm output ports: [{string.Join(" | ", outs)}]; input ports: [{string.Join(" | ", ins)}]");
    Assert(real.Status.StartsWith("MIDI:"), "the real open path returns a status without throwing on this PC"); real.Dispose();
    Assert(!WinmmOut.IsVirtualCable("Microsoft GS Wavetable Synth") && WinmmOut.IsVirtualCable("loopMIDI Port") && WinmmOut.IsVirtualCable("MixDock"), "winmm fallback only ever opens loopMIDI / MixDock ports, never a hardware or GS synth");
    Assert(new MidiEvent(0xC0, 1, 0).Length == 2 && new MidiEvent(0xF8, 0, 0).Length == 1 && new MidiEvent(0x90, 1, 1).Length == 3, "message lengths");
}

// ======================================================================= 9. latency benchmark vs the 1.2 sampler path
{
    // The 1.2 pad path: UI enqueues a Play command on the sampler, the render thread starts it on the next block.
    // The 1.3 paths enqueue a voice on the PadEngine the same way. Latency = frames from the enqueue to the first non-zero sample
    // plus the wall time the enqueue-to-processed-block took. Budget: at most 1 audio buffer (here 256 frames) plus the quantize wait.
    const int block = 256; var dc = new float[Sr]; for (int i = 0; i < Sr; i++) dc[i * 2 % dc.Length] = .5f;
    var slice = new float[Sr * 2]; Array.Fill(slice, .5f);
    var sampler12 = new PerformanceSampler(); sampler12.LoadSlice(0, slice, Sr);
    int frames12 = -1;
    { var buf = new float[block * 2]; sampler12.Process(buf); sampler12.Play(0, 0, false); sampler12.Process(buf); frames12 = Array.FindIndex(buf, x => x != 0) / 2; }
    var samplerN = new PerformanceSampler(); samplerN.LoadSlice(0, slice, Sr);
    int Frames(Action<PadEngine> start)
    {
        var e = new PadEngine(samplerN); var buf = new float[block * 2]; e.Process(buf); start(e); Array.Clear(buf); e.Process(buf);
        int idx = Array.FindIndex(buf, x => x != 0); return idx < 0 ? int.MaxValue : idx / 2;
    }
    int fSlice = Frames(e => e.Trigger(new PadVoiceSpec(0, VoiceKind.Slice, -1, 100, false, false, SlicePad: 0)));
    int fChord = Frames(e => e.Trigger(new PadVoiceSpec(1, VoiceKind.Chord, -1, 100, false, true, Notes: new[] { 48, 52, 55 })));
    int fDrum = Frames(e => e.Trigger(new PadVoiceSpec(2, VoiceKind.Drum, -1, 100, false, false, Drum: DrumSound.Kick)));
    Console.WriteLine($"INFO first non-zero output frame after the pad event: v1.2 sampler {frames12}, one-shot {fSlice}, chord {fChord}, drum {fDrum} (buffer {block} frames)");
    Assert(frames12 >= 0 && fSlice <= frames12 && fChord <= frames12 && fDrum <= frames12, $"pad event to first non-zero sample: one-shot {fSlice}, chord {fChord}, drum {fDrum} frames <= v1.2 sampler {frames12}");
    Assert(Math.Max(fSlice, Math.Max(fChord, fDrum)) < block, "every new pad path sounds within the first audio buffer (no added buffering)");
    // wall-clock: enqueue -> block rendered, median of many trials, both paths
    double Median(Func<double> f, int count) { var l = new List<double>(); for (int i = 0; i < count; i++) l.Add(f()); l.Sort(); return l[l.Count / 2]; }
    var bufW = new float[block * 2];
    double wall12 = Median(() => { var s = new PerformanceSampler(); s.LoadSlice(0, slice, Sr); s.Process(bufW); var sw = Stopwatch.StartNew(); s.Play(0, 0, false); s.Process(bufW); return sw.Elapsed.TotalMilliseconds; }, 200);
    double wallChord = Median(() => { var e = new PadEngine(samplerN); e.Process(bufW); var sw = Stopwatch.StartNew(); e.Trigger(new PadVoiceSpec(1, VoiceKind.Chord, -1, 100, false, true, Notes: new[] { 48, 52, 55, 60 })); e.Process(bufW); return sw.Elapsed.TotalMilliseconds; }, 200);
    double wallDrum = Median(() => { var e = new PadEngine(samplerN); e.Process(bufW); var sw = Stopwatch.StartNew(); e.Trigger(new PadVoiceSpec(2, VoiceKind.Drum, -1, 100, false, false, Drum: DrumSound.Kick)); e.Process(bufW); return sw.Elapsed.TotalMilliseconds; }, 200);
    Console.WriteLine($"INFO enqueue -> block rendered, median of 200: v1.2 sampler {wall12:0.000} ms, chord {wallChord:0.000} ms, drum {wallDrum:0.000} ms (one {block}-frame buffer is {block * 1000.0 / Sr:0.00} ms)");
    Assert(wallChord < block * 1000.0 / Sr && wallDrum < block * 1000.0 / Sr, "rendering the first block with a new voice takes less than one buffer of time (real-time safe)");
    // cost of 8 loud chord voices per 256-frame block stays far below real time
    var heavy = new PadEngine(samplerN); for (int k = 0; k < 8; k++) heavy.Trigger(new PadVoiceSpec(k, VoiceKind.Chord, -1, 127, true, true, Notes: new[] { 36, 48, 52, 55, 60, 64 }));
    var hb = new float[block * 2]; heavy.Process(hb); var hs = Stopwatch.StartNew(); for (int i = 0; i < 400; i++) { Array.Clear(hb); heavy.Process(hb); }
    double perBlock = hs.Elapsed.TotalMilliseconds / 400;
    Console.WriteLine($"INFO 8 chord voices x 6 notes x 5 saws: {perBlock:0.000} ms per {block}-frame block ({perBlock / (block * 1000.0 / Sr) * 100:0.0}% of real time)");
    Assert(perBlock < block * 1000.0 / Sr * .5, "8 chord voices of 6 notes use under half of the real-time budget");
}

Console.WriteLine($"ALL {n} PAD TESTS PASSED");

static double Rms(IEnumerable<float> x) { double s = 0; int c = 0; foreach (var v in x) { s += v * v; c++; } return c == 0 ? 0 : Math.Sqrt(s / c); }
static int FirstNonZero(PadEngine e, int frames)
{
    var buf = new float[512]; int at = 0;
    while (at < frames)
    {
        Array.Clear(buf); e.Process(buf);
        for (int i = 0; i < buf.Length; i += 2) if (buf[i] != 0 || buf[i + 1] != 0) return at + i / 2;
        at += buf.Length / 2;
    }
    return -1;
}
static void Wait(Func<bool> cond, int ms = 1500) { var sw = Stopwatch.StartNew(); while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(2); }

sealed class FakePort : IMidiPort
{
    public readonly List<byte[]> Sent = new(); public readonly List<long> At = new(); public bool Disposed, Throw;
    private readonly object _l = new();
    public string Name => "fake";
    public bool Send(MidiEvent e) { if (Throw) throw new InvalidOperationException("port gone"); lock (_l) { Sent.Add(MidiMsg.Bytes(e)); At.Add(Stopwatch.GetTimestamp()); } return true; }
    public void Dispose() => Disposed = true;
}
static class QueueExt { public static MidiEvent? TryDequeueOrNull(this Queue<MidiEvent> q) => q.Count > 0 ? q.Dequeue() : null; }
