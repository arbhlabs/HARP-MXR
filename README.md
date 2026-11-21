# HARP//MXR

*Formerly MixDock.*

**A two-deck DJ mixer you can play with an Xbox controller.** Load two songs, or grab the live audio of Spotify or a browser, then mix them with EQ, kills, a filter, a crossfader and performance effects, all from the sofa with a gamepad.

**Runs on Windows 10 and 11 (64-bit).** That is the only released version. macOS and Linux versions are in development and are not available yet.

<!-- Screenshot placeholder: add docs/screenshot-windows.png and replace this comment with
     ![HARP//MXR on Windows](docs/screenshot-windows.png) -->

## What it does

- **Two decks from anything that plays sound.** Pick Spotify, a browser or any other app as a deck source, or load a song file. The app's own sound is taken over, so you only hear the mix.
- **Mixer:** 3-band EQ with kills, filter, crossfader, auto-gain and a look-ahead limiter.
- **Waveforms with look-ahead:** live decks can be heard up to 30 s after capture, so you see what is coming.
- **Beat tools:** BPM and key detection, continuous beat sync with key lock, slip loops, beat rolls and hot cues.
- **Performance:** an FX pad on the sticks (echo, reverb and more), pads, chords and drums, and virtual MIDI out.
- **Xbox controller first:** everything is playable from a gamepad, and every button can be remapped.
- **Pause** a deck from the mixer. A live app is paused through its Windows media session.

### New in 1.4.1

- Fixed: with SYNC on, live audio could jump back and replay a moment. Live decks now only ever move forward. Sync lines the beat up once, then holds it by speed alone, and clock drift between the app and your sound card is corrected inaudibly (at most 0.5 %).

## Install

Download the latest build from the [Releases](../../releases/latest) page.

1. Download `HARP-MXR-<version>-windows-x64.zip` and unzip it anywhere, for example to `Documents\HARP-MXR`.
2. Run `MixDock.exe`. Nothing else needs installing: .NET is included.
3. Windows SmartScreen may say *"Windows protected your PC"* because the app is not code-signed. Click **More info**, then **Run anyway**.

Settings live in `%APPDATA%\MixDock`, logs in `%LOCALAPPDATA%\MixDock`. To remove HARP//MXR, delete the folder.

To mix Spotify or a browser, start the music and pick the app under **SOURCE** on a deck. Nothing is recorded or saved.

## Controller mapping

The default "Live DJ" layout. Remap it with **Mappings**; **PAD** or **Shift+F1** shows a live cheat sheet.

Hold **View** (or LB+RB) for **SHIFT**. While SHIFT is held, the footer lists what every button does right now.

| Input | Normal | SHIFT |
|---|---|---|
| Left stick up/down | Filter: up = high-pass, down = low-pass, springs back to off | same |
| Left stick left/right | **FX blend** (Kaoss style): roll right = echo, roll left = reverb, depth = how far you push; circling morphs filter into FX, release returns to dry | Filter fine creep (latches) |
| Right stick | FX pad: distance = wet, direction = character (R3 picks the effect) | same |
| LT / RT | Echo throw / beat roll (depth = speed) | Time bend / reverse |
| A / B / X / Y | Bass kill / slip loop / roll 1/4 (hold) / roll 1/8 (hold) | Pads 1-4 of the current bank |
| D-pad left/right | **Crossfader** toward A / B: tap nudges, hold sweeps and speeds up | Mid kill / high kill |
| D-pad up/down | Loop size x2 / ÷2 | Next / previous pad bank |
| LB / RB | Focus deck A / B | Quantize / crossfader centre |
| L3 / R3 | Filter reset / next FX | EQ + kills reset / freeze reverb tail |
| View (tap) / Menu | Tap BPM / sync | Bank mode / **pause or resume the focus deck** |
| Hold View + Menu | Panic: silence everything | |

**Pause:** Shift+Menu pauses the focused deck. For a live app, HARP//MXR sends pause to the app's Windows media session (Spotify, browser tabs). If the app has no session, the deck is **frozen** in HARP//MXR instead and the app keeps playing unheard. **Pause all:** Ctrl+Alt+Shift+Space, or bind *Pause / resume all decks* in Mappings.

**Look-ahead:** live decks are heard 10 s after capture (0-30 s per deck, under **Settings**), so the waveform shows what is coming up to the right of the playhead. Set a deck to 0 for browser video, so the sound stays in sync with the picture.

The 1.4.0 layout (left stick X = crossfader, D-pad left/right = mid/high kill, Shift+D-pad = banks 1-4) is migrated automatically if you never edited it. An edited layout is left as it is. Full details: [windows/docs/MAPPINGS.md](windows/docs/MAPPINGS.md) (it describes the 1.3 pad layout, which is still available as the "Pad performer" preset).

## Build from source

Requires the .NET 9 SDK on Windows.

```powershell
dotnet publish windows/src/MixDock/MixDock.csproj -c Release -r win-x64
dotnet run -c Release --project windows/tests/MixDock.DspTests/MixDock.DspTests.csproj   # and the other suites in windows/tests
```

Every push to `main` is tested. When the version number changes, [`.github/workflows/release.yml`](.github/workflows/release.yml) builds that version and publishes it to Releases automatically, so the latest release is always the current build.

The `macos/` and `linux/` folders hold work in progress for other platforms. They are not released and not supported yet.

## Privacy

HARP//MXR processes audio in memory. It does not record full tracks, has no account, sends no telemetry and opens no network port. The Windows remote-control code is off by default; if you enable it, it binds to `127.0.0.1` unless you explicitly allow LAN access.

## License

[MIT](LICENSE). See [CONTRIBUTING.md](CONTRIBUTING.md) to help out.
