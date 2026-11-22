# HARP//MXR

*Formerly MixDock.*

**A two-deck DJ mixer for Windows you can play with an Xbox-compatible controller.** Mix local music files or live audio from apps such as Spotify and a browser. Each deck has EQ with kills, a filter and performance effects. A crossfader, auto-gain and a limiter shape the mix.

## Download and start

Get the Windows x64 ZIP from [Releases](../../releases/latest), extract it, then run the mixer. The current build runs as `HARP-MXR.exe` and includes the .NET runtime. Windows 11 x64 supports file decks and live app capture. Windows 10 x64 version 2004 or later can use file decks. Per-app loopback requires Windows build 20348 or newer, so use Windows 11 for live decks.

- **Files:** load a track on either deck. The file picker supports MP3, WAV, FLAC, AAC/M4A, Ogg Vorbis, Opus and other formats depending on installed Windows codecs. File decks support seeking, cueing and a full-track waveform.
- **Live apps:** start playback and pick the process under **SOURCE**. Capture is per app or process group. To use two browser tabs as independent sources, open one **BROWSER** window per deck. HARP//MXR creates a separate profile for each deck in Chrome, Edge, Brave or Vivaldi and connects when its window starts playing.
- **Look-ahead:** live decks play 10 seconds behind capture by default. Set each deck to 0–30 seconds under **Settings**. Use 0 when following a browser video.
- **Pause:** Shift+Menu pauses the focus deck in the default controller profile. A file deck pauses its decoder. A live deck first holds the playhead while its in-memory buffer fills. If the buffer fills, HARP//MXR pauses the source through its Windows media session when available. Otherwise the source keeps playing unheard. Click the deck time after resuming to catch up. Ctrl+Alt+Shift+Space pauses or resumes both decks.

## Auto Mix

Load or select a source for each deck, then switch on **Auto Mix**. It uses detected or analyzed tempo, key and phrasing to choose a transition. The transition card shows the planned style and timing. **MIX NOW** starts the next suitable transition, and **SKIP** asks for a different style. Moving a fader, EQ, filter or FX control during a transition gives you control of that setting while automation continues on the others.

File decks can plan a transition around the end of the outgoing track. For a live stream with no known track end, press **MIX NOW**. The nine styles include bass swap, filter blend, echo out, drop swap, double drop, loop roll, tempo ramp, power-down and breakdown entry. They depend on the available beat grid, phrasing and tempo match. Auto Mix can use an echo out when a beat-matched blend is unsuitable.

In the Auto Mix menu, **Pick next track from library** highlights a suggestion based on tempo, key and the selected energy arc, with a preference for fresh tracks and a different artist. Use **LOAD A** or **LOAD B** to load it.

## Controller mapping

Fresh installs start on **Stick FX**. Upgrades preserve the active profile and customized bindings. Use **PROFILE** to switch layouts, **MAP PAD** to remap inputs, or **PAD** or Shift+F1 to see your active bindings. Hold **View** or LB+RB for Shift.

| Input | Stick FX |
|---|---|
| Left stick up/down | Move the crossfader. Hold Shift for a spring filter. |
| Left stick left/right | Play one of the set's two side effects. |
| Right stick | Play the effect in that direction. Distance sets the amount and diagonals blend neighboring effects. |
| L3 / R3 | Previous / next FX set. Shift+R3 clears FX. |
| A / B / X / Y | Bass cut / slip loop / 1/4-beat roll / 1/8-beat roll. With Shift, play pads 1–4. |
| LT / RT | Echo throw / beat roll. With Shift, time bend / reverse. |
| LB / RB | Focus deck A / B. |
| D-pad left/right | Move the crossfader toward A / B. |
| Menu | Sync the focus deck. Shift+Menu pauses or resumes it. |

The 29 effects span six sets. A second controller can run a dedicated **FX Pad** profile independently. The **GameSir G7 Pro** profile supports extra buttons when the controller exposes them to Windows. Use **Find input** in **Mappings** to check and bind them. See [controller setup](windows/docs/CONTROLLERS.md) and the [effect sets and pad mappings](windows/docs/MAPPINGS.md).

## Local data

Settings are stored in `%APPDATA%\HARP-MXR`. On first launch, HARP//MXR copies existing MixDock data into its own folders and leaves the original folders intact. Logs and waveform memory are stored under `%LOCALAPPDATA%\HARP-MXR`.

Normal live playback is buffered in memory. **Remember tracks** is on by default and stores waveform peaks, fingerprints, analysis and available track metadata locally to recognize previously played live tracks. In **Settings**, turn off **Remember tracks** to stop collecting these entries, use **Forget this track** for the focused deck, or use **Clear cache** to remove them all. Audio captured for sample pads is saved locally under the settings folder so the pads can be reused. HARP//MXR has no account or app telemetry.

## Build from source

Use the .NET 9 SDK on Windows.

```powershell
dotnet publish windows/src/MixDock/MixDock.csproj -c Release -r win-x64
dotnet run -c Release --project windows/tests/MixDock.DspTests/MixDock.DspTests.csproj
```

The other test suites are in `windows/tests`.

## License

HARP//MXR is available under the [MIT license](LICENSE). See the [third-party notices](windows/THIRD-PARTY-NOTICES.md) and [contribution guide](CONTRIBUTING.md).

By using or redistributing the Windows binaries, you agree to the applicable [Microsoft .NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm) and [Windows SDK License](https://learn.microsoft.com/en-us/legal/windows-sdk/license) for the bundled Microsoft components.
