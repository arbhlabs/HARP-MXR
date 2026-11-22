# MixDock architecture and design decisions

```
Spotify process -> Windows render engine (routed to virtual cable)
                -> WASAPI ProcessLoopback(pid A) -> IEEE 32f 44.1k stereo
                -> DeckDSP[A]: low shelf 120Hz, mid bell 1050Hz Q=.8,
                   high shelf 7500Hz, LP/HP sweep, gain ramp
                -> bounded stereo SampleRing A --\
                                              MasterMixer(equal-power X-fader)
Vivaldi process -> Windows render engine       / | tanh safety soft clip
                -> WASAPI ProcessLoopback(pid B) | 
                -> DeckDSP[B] -> SampleRing B --/  v
                     WasapiPlayer shared mode at user-selected physical endpoint
                                      -> TV/speakers
```

- Language: C# / .NET 9 WinForms. Dependency: `NAudio` 3.2.0 on NuGet (MIT). All DSP source code is authored in this package. No borrowed third-party source snippets need relicensing.
- Capture: `WasapiRecorderBuilder.WithProcessLoopback(...).BuildAsync()` (process and descendants), `WithFormat(44.1kHz float stereo)`; event delivers `ReadOnlySpan<byte>`.
- One capture callback per deck processes samples into a rented scratch span and writes an interleaved bounded ring. Ring stores ~1 second; old samples discarded on overflow, silence on underrun.
- Output: `WasapiPlayer` pulls a continuously populated `ISampleProvider` via `ToWaveProvider`. It mixes equal-power A/B (`cos/sin`), global volume, and applies a bounded soft clip so overlapping tracks won't produce >0 dBFS.
- UI: crossfader and knobs are custom GDI+ WinForms controls; UI and audio threads communicate via immutable `DeckParameters` record swapped with `Volatile.Write/Read`.
- Hotkeys: Win32 `RegisterHotKey` and WndProc. Xbox: Win32 XInput `xinput1_4.dll`. Mappings/last state stored in user profile JSON.
- Process discovery enumerates audio sessions of all active render endpoints and adds popular audio app PID candidates. Does not intercept HTTPS, media URLs, or DRM.

## Controller performance layer (1.1)

```
XInput -> Gamepad.Process (radial deadzone 0.15 + rescale, trigger deadzone)
       -> axes: Gamepad.AxisLocked -> AxisDriver (spring, unchanged 1.0 feel) / FilterLatch (fine creep) / momentary amounts
       -> buttons: PadMapper (shift button or LB+RB chord, tap-on-release modifiers, panic hold, debounce, repeat)
       -> MainForm.Pad.Dispatch -> deck panels, DeckSync, DeckReader roll, MasterMixer echo/riser
```

- `Platform/PadMapper.cs`, `Platform/PadLogic.cs` (FilterLatch, TapTempo, BeatQuant) are pure and unit-tested in `tests/MixDock.GamepadTests`.
- Filter: effective = clamp(latched + spring). Left stick X creeps the latch (quadratic, 4.8 units/s max = 0.08/frame, double precision); left stick Y is the 1.0 spring (`AxisDriver` absolute) riding on the latch. Mouse edits rewrite the latch. `Knob.Precise` carries the fraction to `DeckDsp`, which glides the filter value in 64-frame steps (~10 ms) and rebuilds coefficients per step: no zipper steps.
- Loop roll / loop (`DeckReader.ApplyRoll`): render-side **slip** loop on the heard (pre-DSP) stream. The first pass is the live audio, recorded into a loop buffer (max 8 s); later passes replay it with a 128-frame seam crossfade into the pre-start lead-in; exit crossfades back to the live stream. Works on live app decks (no seeking). Start is beat-quantized from `DeckSync.HeardPhase` (± one audio period).
- Echo: one `EchoFx` per deck in `MasterMixer`, fed post-crossfader (cutting a deck stops new input, the tail rings out), summed after the fader. Freeze = loop the echo buffer with feedback 1, no input.
- Riser: `RiserFx` white noise -> high-pass 250 Hz..8 kHz following the build (held beats / RiserBeats), sixteenth-note gate locked to the focused deck's beat, release stops on the next beat.
- Beatjump: file decks only (`DeckSource.SeekTo`, exact multiples of the loop size, executed on the next beat). Live app decks report that they cannot seek.
- Overlay: `Ui/PadOverlay.cs` draws the pad and labels every input from the live config (`GamepadAxes`, `GamepadButtons`, `ShiftButtons`).

## Performance layer (1.2 "Right stick FX")

```
RS (X,Y) -> FxCurves.Amount (radial deadzone 0.2, smoothstep) + Param(x), Param(y)
         -> PerformanceFx.Controls snapshot (effect, amount, x, y, bpm, beat phase + timestamp, freeze)  [UI thread, every 16 ms]
LT / RT / pads / R3 -> PerformanceSampler commands (bend, reverse, spin-back, pad capture/play) + Reader roll + R3 effect cycle

MasterMixer.Read, per deck:  DeckReader (delay, resample, EQ/filter) -> cue tap
   -> PerformanceSampler  (history ring 4 s; pads; tape bend / reverse / spin-back; re-lock crossfade on the next beat)
   -> PerformanceFx       (flanger | phaser | crush | gate inserts, crossfaded by the smoothed amount; Freeverb reverb as a post-fader send)
   -> EchoFx (existing)   -> crossfader gain -> master mix + sends -> tanh soft clip
```

- `Audio/FxCurves.cs` holds every parameter curve and the gate/roll/bend timing as pure functions (unit-tested in `tests/MixDock.DspTests`).
- `Audio/PerformanceFx.cs`: reverb uses the Freeverb tunings (8 combs, 4 allpasses, right channel spread +23) and is fed by the post-fader deck so the tail rings out after a deck is cut; freeze = no input, feedback 1, no damping. Inserts are skipped while their weight is ~0; weights move on 5 ms ramps when the effect changes, the stick amount on a 60 ms one-pole, parameters on 20 ms. The LFOs and the gate follow the deck beat phase: the UI sends phase + a timestamp, the render thread ages it and only re-locks when it drifted more than ~5 ms, so there is no per-tick jitter.
- `Audio/PerformanceSampler.cs`: commands arrive through a `ConcurrentQueue`; the render thread owns all buffers. Tape engine: the read cursor lags the write head by `lag` frames (`lag += 1 - speed`), speed slews towards the target (1 - LT pressure, or -1 for reverse, or the spin-back profile) with a brake length that depends on pressure; gain only fades in the last 12 % of speed so a stop leaves no DC step. After release the speed spins up and a 5 ms crossfade jumps back to the live stream on the next beat (immediately when no tempo is known). Pads store exactly one beat, capture ends on a beat line, retrigger starts on a 1/16 line, edges blend through 5 ms.
- RT beat roll reuses `DeckReader.StartRoll/ResizeRoll` (slip loop on the heard stream); it starts on the grid for its length and stops on the next 1/16 line.
- Output safety (`Platform/OutputPolicy.cs`): virtual devices (CABLE, VB-Audio, Voicemeeter, Steam Streaming, FxSound) and the active redirect sink are never offered, selected, saved or used; an invalid saved output falls back to the Windows default if it is real, then the first real device. `AudioEngine.SetOutputAsync` also refuses virtual names, and `TakeoverController` replaces a virtual "where the app was playing" device.
- Paint safety: `Theme.Guard` wraps every custom `OnPaint`/`OnDrawItem` (a glitch skips the frame and logs once, never a dialog); `Theme.Arc/Ellipse/Gradient` skip zero, negative or non-finite rectangles and sweeps, which is what made `Graphics.DrawArc` throw `OutOfMemoryException` from `WmPaint`.

## MIDI Pad (1.3)

```
A/B/X/Y + D-pad -> PadMapper (Bank:n, Pad:n, PadCapture:n, QuantCycle, BankMode, PadGrid) -> MainForm.PadBank
RT depth -> PadBanks.Velocity (curve, min 30) --> PadEngine voice spec (amplitude = velocity/127, synth brightness)
                                              \-> MidiService queue (note on/off, due time = quantize wait)
BeatClock (focus deck bpm + phase, wrap counting) -> PadQuant.MsToNext -> delay frames for PadEngine / due ticks for MIDI

MasterMixer.Read, per deck:  DeckReader -> KeyDetector.Feed (tap, no change to the audio) -> PerformanceSampler (16 pads)
   -> PadEngine (additive: slice / drum / chord voices, <= 8, 5 ms fades) -> PerformanceFx -> EchoFx -> crossfader -> master
```

- `Audio/PadEngine.cs`: render-thread voice player. UI calls only enqueue commands (`ConcurrentQueue`); the first sample of a voice is produced in the same block the command is dequeued (no extra buffering), the quantize delay is a frame countdown inside the block. A voice is one of: a slice read from the sampler's pad buffer (the capture and the playback run on the same render thread, so there is no copy and no race), a drum buffer, or a chord synth. Amplitude envelope: 5 ms linear attack from the first sample, release by ramp (5 ms; chords 70 ms with a 100 ms minimum stab so a tap shorter than the quantize wait still sounds). Retrigger of a key, a choke group and the 8-voice limit all end the old voice through the same 5 ms ramp (stolen = oldest). Latched voices (layers) loop or sustain until `Unlatch`.
- `Audio/PadSynth.cs`: `ChordVoiceDsp` (5 detuned PolyBLEP saws per note, stereo spread, two one-pole low-passes whose cutoff opens with velocity and plucks open at the note start, body envelope to a .72 sustain) and `DrumKit` (kick, snare, hat, clap, tom, rim, open hat, crash rendered once at start from oscillators and noise; no samples shipped).
- `Audio/ChordTheory.cs`: scale degrees, triads, inversions, voicings, names; `ZoneSelector` snaps the right stick to discrete zones with hysteresis. Tested over all 24 keys.
- `Audio/KeyDetector.cs` (chroma from Goertzel bins E2-E6 + Krumhansl-Schmuckler) is now fed from `MasterMixer` for both decks (heard stream, before pads are added) and reset when a new song or app is loaded.
- `Audio/PerformanceSampler.cs` now holds 16 pads (bank*4 + pad), allocated on first capture; `PadData`, `LoadSlice`, `CopySlice` expose them. `Settings/SliceStore.cs` saves captured slices next to config.json.
- `Platform/PadBanks.cs`: bank/pad numbering, mode cycle, velocity curve, MIDI note layout, `PadQuant` (grid maths with the 30 ms late tolerance) and `BeatClock` (continuous beat position from phase wraps, also drives the MIDI clock).
- `Platform/Midi.cs`: `MidiMsg` encoding, `MidiService` (lock-free queue, a dedicated thread, due-time delivery, 24 ppqn clock from a beat-clock snapshot, flush on dispose), `TeVirtualMidi` (teVirtualMIDI64.dll via P/Invoke, only when present), `WinmmOut` / `WinmmIn` (loopMIDI / MixDock ports only). Every backend failure degrades to "MIDI: no virtual port"; nothing in this path can throw into the UI or audio thread.
- UI: `Ui/PadGridView.cs` (4x4 grid, used in the centre panel and as the Shift+Menu overlay; colours by mode, flash brightness = velocity, held / latched / pending / DAW-lit states), `Ui/MidiSettingsForm.cs`, updated `Ui/PadOverlay.cs` cheatsheet. No purple anywhere (the MID knob was lavender and is now green; red is only used for errors and KILL AUDIO). `Program.cs` no longer shows an exception dialog: unhandled UI-thread exceptions are logged and the app carries on.
- Tests: `tests/MixDock.PadTests` (banks, chord theory, key detector, velocity, quantize, layers and stealing, MIDI, latency benchmark), `tests/MixDock.ShotTests` renders the new controls to PNG without starting HARP-MXR.exe.

## Risk register (fixes in priority order)

1. **Build/runtime not verified**: authored in Linux environment without `dotnet` or Windows audio devices. Windows runner and manual smoke tests are the acceptance gates. Do not present as verified EXE until these pass.
2. **Original source still audible**: user must route source apps to an alternate (e.g. virtual-cable) output; no documented safe automatic policy API used.
3. **Processes vs tabs**: multiple browser processes and shared audio service may make picking audio session PIDs confusing. Expose audio peak detection per captured PID and/or Chromium extension in v0.2.
4. **Latency/drift**: independent process loopback callbacks are not synchronized; FIFO can drift; long sets need clock drift correction, per-deck adaptive resampling and targeted latency instrumentation.
5. **DSP parameter transitions**: gain smoothing in place, but biquad coefficient swaps can click on aggressive sweeps; implement coefficient interpolation/ramped wet/dry bypass and UI update coalescing.
6. **Default output unplug**: user must choose output again. Add endpoint-change notifications/recovery.
7. **No true brickwall limiter**: current `tanh` is transparent soft saturation up to -0.5dBFS but not a lookahead peak limiter; measurements are pre-saturation deck peaks.
8. **Driverless capture may be affected by protected app policies**: distinguish technical failure from silent source/DRM, and do not attempt bypass.

## No permissions or external operations

Live playback uses an in-memory buffer. Waveform memory stores derived measurements and available track metadata, and saved performance pads retain short audio slices. See [local data](../../README.md#local-data) for storage locations and controls. The remote-control prototype in `Remote/` is loopback-only and the desktop app does not start it. NuGet restore contacts NuGet, and optional audio drivers must be installed separately.

Filter feel settings (FilterDeadzone 0.22, FilterExpo 0.6, FilterSmoothMs 60) live in AppConfig; see docs/MAPPINGS.md. Waveforms: Ui/WaveformStrip.cs + Audio/TrackWaveform.cs (file decks analysed on load, live decks rolling from capture).


## DJ layout (1.3.2)

The DJ layout was approved from mockups. MainForm.Dj.cs now recomposes the existing engine-bound controls into Ui/DjLayout.cs, with the same graphite surface, cyan A / amber B, green mids/meters and flat surfaces. The 1366x768 coordinate canvas fits the physical client area; UiMetrics supplies a single scale for custom paint and hit testing. Pixel fonts avoid a second desktop-DPI multiplication. The normal app opens maximized; no user DPI setting is changed.

Both decks retain their source selection, DSP parameter events, meters, mute, and controller paths. The center owns trim, three EQs and kills, filter, cue and equal-power crossfader; a horizontal master signal meter is in the header. Waveforms retain actual file/live data, grids, cue markers and seek rules; mids are now green. Mouse FX and momentary controls feed the existing DSP; MASTER sends a real sync-role command. Per-deck source RELEASE restores the previous app route and stops its decoder/capture.

Ui/LibraryView.cs persists file paths and shows unknown metadata as em dashes. Settings/HotCueStore.cs stores 16 file-position cues by normalized path using atomic replacement. Sample/OneShot/Chord/Drum retain their existing engines and slice persistence; hot cues are a separate mode. Pad overlays and MIDI configuration remain available from the header. There is no playlist/sidebar or streaming-service track metadata.

SETTINGS exposes latency requests and callback diagnostics; apply restarts existing players while preserving cue state. LatencyMs persists in AppConfig. SinkPolicy no longer automatically chooses HDMI/HD Audio/display endpoints. TakeoverController rejects apps listed in ProtectedApps before capture and respects the selected allowed master endpoint. Protected apps are also excluded from discovery. Shared WASAPI remains unchanged. SessionTakeover recovery is retained for legacy journals; it is not a new routing path.

Program's --ui-check branch runs before AppRedirect.ClearSelf and creates preview-only state. It opens no WASAPI player, audio capture, MIDI port, global hotkeys or controller, and touches no routing journal. The real form stays transparent/offscreen. Generated WAV decoding, offline mixer pulls, control mouse/key actions, persistence under the evidence directory and DrawToBitmap support reproducible checks.

Build and release scripts create a new versioned portable folder, gate on regressions and the published executable's UI check, and produce a per-user self-contained .NET installer without launching the app. Installation never changes audio policy and never removes old releases or user settings. Uninstall removes only the checked version folder, that version's registration, and shortcuts still targeting it. The unsigned installer is a test candidate pending a listening and hardware pass.
