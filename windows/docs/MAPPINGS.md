# Stick FX (1.5)

**Profile "Stick FX"** (choose it under PROFILE or with View + RB). A first install starts on it; an existing config keeps the profile and edited layers it had. It is the Live DJ layout with these changes:

| Input | Stick FX |
|---|---|
| Left stick ↕ | Crossfader (rate: it stays where you leave it) |
| Left stick ↔ | The current set's two side effects (← / →), distance = amount |
| Shift + left stick ↕ | Filter (spring), as before |
| Right stick | Set pad: ↑ ↓ ← → = the set's four effects, distance = amount, diagonals morph |
| L3 / R3 | Previous / next set |
| Shift + R3 | FX clear |

Release a stick and the effect glides back to dry. Echoes and reverbs ring out.

| Set | ↑ | ↓ | ← | → | Left stick ← | Left stick → |
|---|---|---|---|---|---|---|
| 1 SPACE | Hall | Dub echo | Ping-pong | Shimmer | Gated verb | Noise riser |
| 2 SWEEP | High-pass | Low-pass | Vowel | Comb | Wobble | Flanger |
| 3 MOTION | Phaser | Flanger | Chorus | Auto pan | Vowel | Ping-pong |
| 4 CHOP | Trance gate | Pump | Beat repeat | Transformer | Reverse | Gated verb |
| 5 GRIT | Bit crush | Drive | Ring mod | Wavefold | Low-pass | Transformer |
| 6 WARP | Freeze | Vinyl brake | Reverse | Pitch drop | Sub drop | Beat repeat |

**Button-only effect:** Cut. **Tempo-synced:** beat repeat, reverse, freeze, transformer, trance gate, pump, auto pan, vowel, wobble, phaser, flanger, dub echo, ping-pong, gated verb, noise riser, sub drop.

**More**

- **Mouse:** each deck's FX row shows the set (click = next, right-click = previous) and its four effects (hold = play at 75 %). The XY pad plays the set like the right stick, and FREEZE latches it.
- **Second controller:** see [CONTROLLERS.md](CONTROLLERS.md) for the FX Pad, the paddles and the GameSir G7 Pro.
- **On-screen feedback:** every controller action shows a large card over the waveforms for 1.5 s. It names the effect or action, the amount, the stick or button, the deck, the set and its four directions.

# Pad performer profile

The table below describes the Pad performer preset introduced in MixDock 1.3. See the [controller mapping](../../README.md#controller-mapping) for the default Stick FX profile.

Choose **Map Pad** to edit either layer. The Save button applies changes immediately. Open **PAD** or press **Shift+F1** for a diagram of your current bindings. Settings are stored in `%APPDATA%\HARP-MXR\config.json`.

**1.3 turns the face buttons into a 16-pad MIDI-style grid:** the D-pad picks one of 4 banks, A/B/X/Y are the 4 pads of that bank, each bank has a mode (Sample, One-shot, Chord, Drum), pads launch on a beat grid, RT is the velocity, LB held stacks pads as layers, and everything is also sent as real MIDI (see "Pad banks" below).

One *focus* deck at a time (LB / RB, shown as ◆ PAD). The right stick is a Kaoss-style space/FX pad that is **always on** (no Shift needed), the triggers are time-bend and beat-roll, the face buttons are four sample pads per deck, and SHIFT gives every control a second function. The crossfader is no longer on the controller sticks: use the on-screen fader, the mouse, the keyboard hotkeys, or Shift+RB (centre).

## Pad performer Xbox mapping

| Input | Normal | Shift (hold View, or hold LB+RB) |
|---|---|---|
| Left stick ↕ | Filter sweep, spring: returns to the latched value on release | same |
| Left stick ↔ | Filter fine creep: slow, latching | same |
| L3 (click) | Filter centre | EQ + kills reset |
| **Right stick** | **FX pad.** Distance = wet amount (radial deadzone 0.2, soft curve, 60 ms smoothing). ↔ = size / rate / time, ↕ = character / depth / feedback. Release: the amount springs back dry smoothly and reverb/echo tails ring out | same |
| R3 (click) | **Cycle FX:** Space → Reverb → Flanger → Phaser → Crush → Gate → Echo → Space… (short rumble, big name on the FX readout) | **Freeze reverb tail** (press again to release) |
| LT | **Time bend:** half pressure = half speed (tape slow), full press = tape stop; release spins back up and re-locks on the next beat | **Spin-back** (record rewinds and decays, then re-locks on the beat) |
| RT | **Beat roll / stutter:** depth by pressure 1/4 → 1/8 → 1/16 → 1/32; released on the next 1/16 line, back on the grid | **Reverse while held** (censor style; the playhead keeps running underneath) |
| A / B / X / Y | **Pads 1-4 of the current bank** (see Pad banks). Sample bank: tap = retrigger on the quantize grid, hold (> 250 ms) = gate-repeat | **Alt pad:** Sample / One-shot = capture the last 1 beat into that pad; Chord = the alternative chord set; Drum = the alternative drum set |
| LB (tap) | Focus deck A. **LB held + pads = layer** (latched) | **Quantize:** Off → 1/16 → 1/8 → 1/4 → 1 bar |
| RB (tap) | Focus deck B | SYNC (was View) |
| D-pad ↑ → ↓ ← | **Bank 1 / 2 / 3 / 4** (clockwise from Up) | Loop on/off (↑) · Cue (↓) |
| D-pad ← / → | (bank 4 / bank 2, see above) | Beatjump − / + loop size (song files); **in a Chord bank: chord key −1 / +1 semitone** |
| View (tap) | Tap BPM | **Bank mode:** Sample → One-shot → Chord → Drum (reach it with the LB+RB chord, since View itself is the shift button) |
| Menu (tap) | AUTO MIX on/off | **Pad grid window** on/off |
| View + Menu held 500 ms | **Panic: kill all audio, tails and pad state / restore** | |

Rules: View, Menu, LB and RB act on a short **tap** (released within 350 ms without being used as a modifier), so holding them for shift or panic never fires their own function. Button presses are debounced (40 ms). `+`/`-` actions repeat after 350 ms every 60 ms. Rumble: a short pulse on every pad/FX action and on every R3 effect change; the left motor follows LT depth and the right motor follows RT depth while a trigger is held; a soft tick on each beat while a trigger or loop runs; a long pulse on panic.

The effect name, level and the current move (TIME BEND 50 %, ROLL 1/16, REVERSE, PAD 2 · REPEAT…) are shown on screen: the FX readout in the centre panel and four pad LEDs under each deck. Clicking a pad plays it, Shift+click captures it.

### Pad banks (1.3)

| Bank (D-pad) | Default mode | What the 4 pads (A B X Y) do |
|---|---|---|
| 1 (↑) | **Sample** (cyan) | The 1.2 behaviour: replace the deck with the captured beat (quantized retrigger, hold = gate-repeat). Shift+pad captures the last beat |
| 2 (→) | **One-shot** (amber) | Plays its captured slice to the end once, additive over the deck; retriggerable; one **choke group per bank** (a new hit fades the previous one). Shift+pad captures |
| 3 (↓) | **Chord** (green) | Scale-locked chord pads from the focus deck's key |
| 4 (←) | **Drum** (white/blue) | Built-in synthesised kick, snare, hat, clap (Shift: tom, rim, open hat, crash) |

Shift+View cycles the current bank's mode (bank modes, chord degrees, the active bank and quantize persist in config; captured slices are saved next to it in `slices\`). Bank switching is instant; a held pad keeps sounding and is released by its own button, whatever bank is selected by then.

**Chord mode.** The key is the focus deck's detected key (chroma + Krumhansl-Schmuckler, confidence above 0.2, shown as "KEY A minor (auto)"), otherwise the manual key (Shift+D-pad ← / → steps it by a semitone in a Chord bank; the MIDI window sets the exact key and Auto). The 4 pads are the degrees I, IV, V, vi (minor keys use the natural minor scale: i, iv, v, VI); Shift gives ii, iii, vii°, I (octave up). **Right stick (instead of the FX pad, which Shift+stick still gives you):** X picks the inversion (rest/left = root, right = 1st, far right = 2nd), Y the voicing (down = octave stack, rest = close, up = open/spread, far up = add9); zones snap with hysteresis. The shape is read when the pad is hit; the readout shows e.g. "Am / 1st inv / open". The sound is a built-in supersaw pluck/pad (5 detuned PolyBLEP saws per note, low-pass opened by velocity, 5 ms attack, 70 ms release) routed into the focus deck's FX chain, so FX, crossfader and fader apply to it.

**Velocity.** Velocity = RT depth when the pad is hit (curve soft / normal / hard, 30 when RT is idle, configurable minimum; 127 = full). It scales volume linearly (and the chord synth's brightness), is sent as the MIDI velocity and drives the rumble strength. **Sample bank pads keep the 1.2 full level** (MIDI still reports the velocity). **While a One-shot, Chord or Drum pad is held, RT is velocity, not beat roll.**

**Quantize.** Shift+LB cycles Off, 1/16 (default), 1/8, 1/4, 1 bar. A launch waits for the next grid line of the focus deck's beat clock (a hit up to 30 ms late fires immediately); the grid shows pending pads blinking. A bar is 4 beats counted from the first beat lock (MixDock has no downbeat detection for this). With quantize Off, pads fire on the next audio sample.

**Layers.** Hold LB and hit pads: each pad is latched (Sample/One-shot slices loop, drums repeat every beat, chords sustain) until LB is released or the pad is hit again. Banks can be switched while LB is held, so layers span banks. At most 8 voices per deck; the oldest is stolen through a 5 ms fade. In a Sample bank a layered pad plays additively through the pad engine (the plain Sample hit still replaces the deck).

### MIDI (1.3)

MixDock creates a virtual MIDI port named **MixDock** when the Tobias Erichsen teVirtualMIDI driver DLL is present (installed with loopMIDI / rtpMIDI); otherwise it opens an existing loopMIDI (or "MixDock") port through winmm; otherwise the footer says "MIDI: no virtual port" and nothing else changes. Only loopMIDI / MixDock ports are ever opened, never a hardware synth. The **MIDI** button opens the settings (port, channels, lowest note, clock, input port, key, velocity).

| Message | Meaning |
|---|---|
| Note on/off, channel 1, note 36 + bank*4 + pad (36-51), velocity | Pad hit / release (sent when the sound starts, after the quantize wait) |
| Note on/off, channel 2, the chord's notes | Chord pads |
| CC 20-23 (channel 1) | Left stick X, Y, right stick X, Y (64 = centre) |
| CC 24 / 25 | LT / RT depth |
| CC 26 + program change | Bank number (1-4) / (0-3) |
| Clock (F8) | 24 per beat from the focus deck's beat clock (toggle in settings); no start/stop messages are sent |
| In: notes 36-51 | Light the matching pad on the grid (a Launchpad-style DAW echo) |
| In: CC 1 (configurable) | FX amount |

The MIDI thread is separate from the audio thread; the UI only enqueues into a lock-free queue and a port error is swallowed. Panic sends all-notes-off (CC 123) on both channels.

### Conflicts with 1.2 (what moved)

| 1.2 | 1.3 |
|---|---|
| D-pad ↑ / ↓ loop size ×2 / ÷2, ← / → beatjump | D-pad = banks. Beatjump is on **Shift+D-pad ← / →** (key −/+ in a Chord bank). Loop size ×2/÷2 has no default binding (still bindable: `Focus.LoopDouble`, `Focus.LoopHalve`) |
| Shift+D-pad ↑ / ↓ loop on/off, cue | unchanged |
| Shift+D-pad ← / → browse previous / next | no default binding (`Focus.BrowsePrev`, `Focus.BrowseNext`) |
| Shift+View = SYNC | Shift+View = bank mode. **SYNC is Shift+RB** |
| Shift+LB = play/pause | Shift+LB = quantize cycle (`Focus.PlayPause` unbound) |
| Shift+RB = crossfader centre | Shift+RB = SYNC (`CrossfaderReset` unbound; the on-screen fader / hotkeys still work) |
| Shift+Menu = load browsed source | Shift+Menu = pad grid window (`Focus.Load` unbound) |
| Right stick always the FX pad | Still, except in a Chord bank (chord shape); Shift+stick is the FX pad there |
| RT = beat roll always | RT = velocity while a One-shot / Chord / Drum pad is held |

### The effects (focus deck)

| FX | ↔ (X) | ↕ (Y) | Notes |
|---|---|---|---|
| Space | room size / brightness | up = reverb, down = delay (equal-power morph) | the default; Freeverb reverb + tempo-synced echo |
| Reverb | size / brightness | (unused) | 8 combs + 4 allpasses per channel; Shift+R3 freezes the tail |
| Flanger | LFO rate, 8 beats … 1 beat (beat-synced) | depth + feedback | 0.8-7 ms swept delay |
| Phaser | LFO rate, 8 beats … 1 beat | sweep depth + feedback | 6 allpass stages, notch sweep from 250 Hz |
| Crush | sample-rate divisor 1 … 32 | bit depth 16 … 3 | sample-and-hold downsampler + quantizer |
| Gate | left = 1/8 notes, right = 1/16 notes | open length 25 … 75 % | trance gate locked to the deck beat, 1.5 ms / 5 ms ramps |
| Echo | time: 1/4, 3/16, 1/8, 1/16 beat (set when the gesture starts) | feedback | the existing EchoFx |

All parameter and effect changes are smoothed (60 ms stick amount, 5 ms effect switching, 20 ms parameters), so nothing clicks. Time bend, spin-back, reverse and pad playback run in the per-deck slip engine on the heard stream with 5 ms ramps.

## Live app decks vs song files

| Feature | Song file deck | Live app deck (Spotify, browser…) |
|---|---|---|
| Loop roll, loop on/off | Slip loop: the song keeps running underneath | Same (render-side capture loop, no seeking needed) |
| Beatjump | Real seek by the loop size, on the next beat | Not possible (apps cannot seek): status message, use rolls |
| Play/pause | Pauses the decoder | Freezes the playhead and buffers ahead. When full, the source is paused if Windows exposes its media session. |
| Echo, riser, filter, kills, sync | Yes | Yes, within the limits of the live buffer and detected beat grid |
| Quantization | From detected beat; tap BPM fills in until detected | Same |

Doubling an active loop works once the longer slice has been heard; halving always works.

## Config keys

```json
"GamepadAxes":    { "LeftThumbY": "Focus.Filter", "LeftThumbX": "Focus.FilterFine", "RightThumbX": "Focus.FxX",
                    "RightThumbY": "Focus.FxY", "LeftTrigger": "Focus.TimeBend", "RightTrigger": "Focus.BeatRoll" },
"ShiftAxes":      { "LeftTrigger": "Focus.SpinBack", "RightTrigger": "Focus.Reverse" },
"GamepadButtons": { "A": "Focus.Pad:1", "B": "Focus.Pad:2", "X": "Focus.Pad:3", "Y": "Focus.Pad:4", "Up": "Bank:1", "Right": "Bank:2",
                    "Down": "Bank:3", "Left": "Bank:4", "RightThumb": "FxCycle" },
"ShiftButtons":   { "A": "Focus.PadCapture:1", "LeftShoulder": "QuantCycle", "Back": "BankMode", "Start": "PadGrid", "RightThumb": "FxFreeze" },
"ShiftButton": "Back", "ShiftChord": "LeftShoulder+RightShoulder",
"PanicChord": "Back+Start", "PanicHoldMs": 500, "EchoBeats": 0.5, "RiserBeats": 8,
"FxDeadzone": 0.2, "FxSmoothMs": 60,
"Banks": [ { "Mode": "Sample" }, { "Mode": "OneShot" }, { "Mode": "Chord", "Degrees": [0, 3, 4, 5], "AltDegrees": [1, 2, 6, 7] }, { "Mode": "Drum" } ],
"ActiveBank": 0, "Quantize": "Sixteenth", "VelocityCurve": "normal", "VelocityMin": 30, "VelocitySensitive": true,
"KeyAuto": true, "ManualKeyPc": 0, "ManualKeyMinor": false,
"MidiEnabled": true, "MidiOutPort": "", "MidiInPort": "", "MidiChannel": 1, "MidiChordChannel": 2, "MidiNoteBase": 36, "MidiClock": true, "MidiFxCc": 1,
"ConfigVersion": 4
```

Deck prefixes: `Focus.` (focused deck), `A.`, `B.`.

- Axis actions: `<deck>.Filter` (spring), `<deck>.FilterFine` (latching creep), `<deck>.FxX` + `<deck>.FxY` (the FX pad; map both stick axes), `<deck>.TimeBend`, `<deck>.BeatRoll`, `<deck>.Reverse`, `<deck>.SpinBack`; the older momentary actions still work if you map them: `<deck>.EchoSend`, `<deck>.EchoThrow`, `Riser`; any control below as a rate knob (`Crossfader`, `Master`, `<deck>.Low/Mid/High/Trim`). `ShiftAxes` replaces an axis action while shift is held; an axis that is not listed keeps its normal action.
- Button actions: `<deck>.Pad:<1-4>` (pad of the current bank; press and release both matter), `<deck>.PadCapture:<1-4>` (alt pad / capture), `Bank:<1-4>`, `BankMode`, `QuantCycle`, `PadGrid`, `.Cue`, `.Sync`, `.Roll:<beats>` (hold, e.g. `Roll:1/16`), `.Loop`, `.LoopDouble`, `.LoopHalve`, `.BeatJumpBack`, `.BeatJumpFwd`, `.FilterFineReset`, `.FilterReset`, `.EqReset`, `.TapBpm`, `.LowKill`, `.MidKill`, `.HighKill`, `.EchoFreeze`, `.PlayPause`, `.BrowsePrev`, `.BrowseNext`, `.Load`, `.Mute`, `.Low+`/`-`, `.Mid+`/`-`, `.High+`/`-`, `.Filter+`/`-`, `.Trim+`/`-`; global `FxCycle`, `FxFreeze`, `FocusA`, `FocusB`, `AutoMix`, `MasterMute`, `Panic`, `CrossfaderReset`, `Crossfader+`/`-`, `Master+`/`-`, `PadOverlay`.
- Valid analog inputs: `LeftThumbX`, `LeftThumbY`, `RightThumbX`, `RightThumbY`, `LeftTrigger`, `RightTrigger`. Valid buttons: `A`, `B`, `X`, `Y`, `Start` (Menu), `Back` (View), `LeftShoulder`, `RightShoulder`, `LeftThumb`, `RightThumb`, `Up`, `Down`, `Left`, `Right`.

**Upgrading to ConfigVersion 4 (1.3):** a 1.2 config gets the 4 default banks, quantize 1/16 and the MIDI defaults. Each D-pad and Shift binding that is still at its 1.2 default moves to the 1.3 default (table above); a binding you changed is left exactly as it is. `ConfigVersion` is then 4 and never migrated again.

**Upgrading (ConfigVersion 3):** a config that still has the 1.0 or 1.1 default axes/buttons is moved to the 1.2 defaults automatically. A customised map keeps your bindings except: `RightThumbX = Crossfader` becomes `Focus.FxX`, `RightThumbY = Focus.EchoSend` becomes `Focus.FxY`, `LeftTrigger = Focus.EchoThrow` becomes `Focus.TimeBend`, `RightTrigger = Riser` becomes `Focus.BeatRoll`, and `RightThumb = Focus.Loop` becomes `FxCycle` (the loop is on Shift+D-pad ↑). The low/mid/high kills, echo freeze, deck mute, trim and sync-on-B buttons are no longer in the default pad layout (the KILL and MUTE buttons on screen still work, Shift+View is SYNC, and every action can be re-bound). `ConfigVersion` is then set to 3 and never migrated again.

## Keyboard hotkeys

```json
"Hotkeys": { "Ctrl+Alt+Shift+Q": "A.Low+", "Ctrl+Alt+Shift+E": "Crossfader+" }
```

Global keyboard bindings must contain at least one of `Ctrl`, `Alt`, `Shift`, `Win`, then a Windows key name such as `A`, `F8`, `Left`. Hotkeys accept every button action above except hold-type rolls (`Roll:`), including `Focus.` actions. Shortcuts use Win32 `RegisterHotKey`, which may fail silently if another app owns them.

HARP//MXR reads connected XInput controllers in slots 0–3. See [controller roles](CONTROLLERS.md) for dual-controller setup.

## Filter feel (config.json)
- FilterDeadzone (default 0.22): radial deadzone for the left stick Y spring filter.
- FilterExpo (default 0.6): response = expo*x^3 + (1-expo)*x; 0 = linear.
- FilterSmoothMs (default 60): smoothing time of the spring filter.

