# MixDock

**A two-deck DJ mixer you can play with an Xbox controller.** Load two songs, or (on Windows and macOS) grab the live audio of Spotify or a browser, then mix them with EQ, kills, a filter, a crossfader and performance effects, all from the sofa with a gamepad.

<!-- Screenshot placeholder: add docs/screenshot-windows.png and replace this comment with
     ![MixDock on Windows](docs/screenshot-windows.png) -->
> Screenshot coming soon.

| | Windows | macOS | Linux |
|---|---|---|---|
| App | Full app (WinForms) | Full app (SwiftUI) | **MixDock Lite** (terminal) |
| Mix song files | Yes | Yes | Yes (any format ffmpeg reads) |
| Mix live app audio (Spotify, browser) | Yes (process loopback) | Yes, macOS 14.4+ (process taps) | No |
| EQ, kills, filter, crossfader, limiter | Yes | Yes | Yes (same DSP code as Windows) |
| Loops, rolls, FX pad, pads, chords, drums | Yes | Loops, rolls, FX, hot cues | No |
| Xbox controller | Yes (XInput) | Yes (GameController) | Yes (`/dev/input/js*`) |
| MIDI | Virtual MIDI out (loopMIDI) | Any USB MIDI controller, with LEARN | No |
| Built and tested here | Builds + 8 test suites | CI only (`swift test` on macOS runners) | Builds + self-test; sound on real hardware not yet verified |

## Install

Download the file for your system from the [Releases](../../releases) page.

### Windows 10 / 11 (64-bit)

1. Download `MixDock-<version>-windows-x64.zip` and unzip it anywhere, for example to `Documents\MixDock`.
2. Run `MixDock.exe`. Nothing else needs installing: .NET is included.
3. Windows SmartScreen may say *"Windows protected your PC"* because the app is not code-signed. Click **More info**, then **Run anyway**.

Settings live in `%APPDATA%\MixDock`, logs in `%LOCALAPPDATA%\MixDock`. To remove MixDock, delete the folder.

### macOS 14 (Sonoma) or newer, Apple silicon and Intel

1. Download `MixDock-<version>-macos-universal.dmg`, open it and drag **MixDock** into **Applications**.
2. Double-click MixDock. The first time, macOS blocks it with *"Apple could not verify MixDock..."*. Click **Done**, then allow it once:
   - **macOS 15 Sequoia and newer:** open **System Settings > Privacy & Security**, scroll down to **Security** and click **Open Anyway** next to MixDock, then confirm.
   - **macOS 14 Sonoma:** right-click (Control-click) MixDock in Applications, choose **Open**, then **Open** again.
   - **Or in Terminal** (any version):
     ```sh
     xattr -dr com.apple.quarantine /Applications/MixDock.app
     ```

**Why this step?** MixDock is ad-hoc signed but not notarized (that needs a paid Apple developer account). Browsers, AirDrop and chat apps mark downloaded files as "quarantined", so an un-notarized app will not open until you allow it. If you pass the DMG on to someone, they need to do this step too. A DMG must be built on a Mac: the release workflow builds it on a macOS runner with `hdiutil` and signs the app with `codesign --sign -`.

To mix Spotify or a browser (macOS 14.4+), start the music and pick the app under **SOURCE** on a deck. macOS asks to allow audio recording once; nothing is recorded or saved.

### Linux (x64): MixDock Lite

MixDock Lite is a terminal mixer for song files. It reuses the Windows app's deck DSP (3-band EQ, kills, filter, auto-gain) and look-ahead limiter. It does **not** capture other apps' audio, and has no pads, FX, waveforms or MIDI.

1. Install the helpers:
   - Debian/Ubuntu: `sudo apt install ffmpeg pulseaudio-utils` (`pacat` also works on PipeWire desktops; `alsa-utils` is the fallback).
   - Fedora: `sudo dnf install ffmpeg pulseaudio-utils`
2. Download `MixDock-Lite-<version>-linux-x64.tar.gz` and unpack it:
   ```sh
   tar xzf MixDock-Lite-*-linux-x64.tar.gz && cd MixDock-Lite-*-linux-x64
   ./mixdock --doctor                 # shows the audio player, ffmpeg and controller it found
   ./mixdock song-a.mp3 song-b.flac
   ```
3. Xbox controller: USB and the Xbox Wireless Adapter work through the kernel `xpad` driver. For Bluetooth, install [xpadneo](https://github.com/atar-axis/xpadneo).

## Controller mapping

### Windows (default layout; remap with **Mappings**, live cheat sheet with **PAD** or **Shift+F1**)

Hold **View** (or LB+RB) for **SHIFT**. While SHIFT is held, the footer lists what every button does in the current bank.

| Input | Normal | SHIFT |
|---|---|---|
| Left stick up/down | Filter sweep (springs back) | same |
| Left stick left/right | Filter fine creep (latches) | same |
| Right stick | FX pad: distance = wet, direction = character | same |
| R3 | Next FX (Space, Reverb, Flanger, Phaser, Crush, Gate, Echo) | Freeze reverb tail |
| LT | Time bend / tape stop | Spin-back |
| RT | Beat roll 1/4 to 1/32 by pressure | Reverse while held |
| A B X Y | Pads 1-4 of the current bank | Capture / alternative chords / alternative drums |
| D-pad | Bank 1-4 (Sample, One-shot, Chord, Drum) | Up loop, Down cue, Left/Right beatjump |
| LB / RB (tap) | Focus deck A / B | Quantize / Sync |
| View (tap) | Tap BPM | Bank mode |
| Menu (tap) | AUTO MIX on/off | Pad grid window |
| Hold View + Menu | Panic: silence everything | |

Full details: [windows/docs/MAPPINGS.md](windows/docs/MAPPINGS.md).

### macOS

| Input | Normal | SHIFT (hold View) |
|---|---|---|
| Left stick up/down | Filter (springs back) | |
| Left stick left/right | Crossfader | |
| Right stick | FX pad | |
| LT / RT | Echo throw / beat roll | Brake / reverse |
| A / B / X / Y | Bass kill / loop / roll 1/4 / roll 1/8 | Hot cues 1-4 |
| D-pad | Up/down loop x2 / ÷2, left/right mid / high kill | |
| LB / RB | Focus deck A / B | RB: crossfader centre |
| L3 / R3 | Filter reset / next FX | |
| Menu / View tap | Sync / tap BPM | Menu: play/pause |
| Hold View + Menu | Kill all audio | |

Any USB MIDI DJ controller also works: press **MIDI**, then **LEARN** next to a function and move the control.

### Linux (MixDock Lite)

| Input | Action |
|---|---|
| LB / RB | Focus deck A / B |
| A / B | Play-pause / cue (back to start) |
| X / Y | Bass kill / reset the deck's EQ and filter |
| LT / RT | Move the crossfader toward A / B |
| Left stick up/down | Filter (springs back) |
| Right stick up/down | Deck volume |
| D-pad | Left/right seek, up/down master volume |
| L3 | Centre the crossfader |
| View + Menu | Stop both decks |

Keyboard: Tab focus, Space play, `c` cue, arrows seek/volume, `z`/`x` crossfader, `1`/`2`/`3` kills, `f`/`v` filter, `r` reset, `q` quit.

## Build from source

| | Requires | Build | Test |
|---|---|---|---|
| Windows | .NET 9 SDK | `dotnet publish windows/src/MixDock/MixDock.csproj -c Release -r win-x64` | `dotnet run -c Release --project windows/tests/MixDock.DspTests/MixDock.DspTests.csproj` (and the other suites) |
| macOS | Xcode 15+ / Swift 5.9 | `cd macos && bash scripts/bundle.sh` | `cd macos && swift test` |
| Linux | .NET 9 SDK | `dotnet publish linux -c Release -r linux-x64 --self-contained` | `dotnet run --project linux -- --selftest` |

Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml) when a `v*` tag is pushed.

## Privacy

MixDock processes audio in memory. It does not record full tracks, has no account, sends no telemetry and opens no network port. The Windows remote-control code is off by default; if you enable it, it binds to `127.0.0.1` unless you explicitly allow LAN access.

## License

[MIT](LICENSE). See [CONTRIBUTING.md](CONTRIBUTING.md) to help out.
