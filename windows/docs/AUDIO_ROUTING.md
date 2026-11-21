# Audio routing (1.3.2)

Selecting a live app captures that process tree with Windows process loopback. MixDock uses the chosen real master endpoint, or the source's original endpoint when no master preference is selected. Both master and headphone players use WASAPI shared mode; other applications retain access to those endpoints.

The original app path must be separate from the listening endpoint to avoid doubled audio. MixDock attempts to redirect selected source processes to an available virtual sink (Steam Streaming, cable, virtual mixer). The exact prior per-process endpoints are journaled under `%LOCALAPPDATA%\MixDock`, verified, and restored on RELEASE, close, or startup recovery after a crash. Fake-policy regression tests verify the transactional behavior; real Windows refusal, app restarts and crash recovery remain hardware acceptance items.

Automatic sink selection excludes master/cue/protected endpoints and all ordinary speakers, headphones, HDMI, High Definition Audio and display endpoints. The pure policy accepts an explicit non-protected preferred HDMI ID, but the app does not automatically supply such a preference. A device name does not prove it is inaudible: do not monitor the virtual sink back onto your speakers. If no safe sink is available or Windows refuses redirection, MixDock warns that the source stays audible.

## Manual cable fallback

MixDock does not install drivers. If a virtual cable is already installed, route only Spotify/browser to its input through Windows Volume mixer, then choose real speakers/USB/HDMI as the MixDock master. Keep the Windows default output on the user's chosen real device. Choose the live app in a deck source picker. Do not mute the source in Windows as a routing workaround; that can silence loopback capture too.

## Protected apps and unrelated apps

Process names listed in `ProtectedApps` in `config.json` (case-insensitive substring, empty by default) are excluded from discovery and rejected by TakeoverController before capture or endpoint lookup. Use it for anything that must never be redirected, such as a voice chat or a game. MixDock does not alter the Windows default endpoint or unrelated application routes.

## Cue, sync and latency

CUE is pre-crossfader/post-EQ and requires a separate real endpoint. CUE VOL and CUE / MIX control the headphone feed. Output separation has engine-boundary checks. Physical disconnect/fallback still needs a device test.

SETTINGS exposes Standard (120 ms), Low (60 ms), Safe (200 ms) requested buffers and an explicit output-restart action. Callback p50/p95/p99/max and overruns are processing-time measurements, not input-to-output latency. No measured low-latency claim is made. Beat sync, slip loops and file beat jumps exist; live decks cannot seek. Browser tabs are not guaranteed separate process decks. Protected content cannot be forced through.

KILL AUDIO silences MixDock's mix. It cannot silence a source left on the raw path after a failed redirect. RELEASE returns the source route. Read TEST_ME.md before a listening test.
