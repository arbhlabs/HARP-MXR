MixDock Lite for Linux (x64)

A two-deck terminal mixer for song files with Xbox controller support. It uses the same EQ, kill,
filter and limiter code as MixDock for Windows. File decks only: no per-app capture, pads, FX or MIDI.

Needs:  ffmpeg (to decode tracks) and one of pacat (PulseAudio, also on PipeWire), pw-cat (PipeWire) or aplay (ALSA).
        Debian/Ubuntu: sudo apt install ffmpeg pulseaudio-utils     Fedora: sudo dnf install ffmpeg pulseaudio-utils

Run:    ./mixdock song-a.mp3 song-b.flac
Check:  ./mixdock --doctor      (shows which audio player, ffmpeg and controller were found)

Controller (/dev/input/js*, xpad driver for USB, xpadneo for Bluetooth):
  LB / RB focus deck A / B     A play/pause     B cue (back to start)     X bass kill     Y reset deck
  LT / RT crossfader to A / B  left stick up/down filter (springs back)  right stick up/down deck volume
  D-pad left/right seek, up/down master volume   L3 centre crossfader   View + Menu stop both decks

Keys: Tab focus · Space play · c cue · arrows seek/volume · z/x crossfader · m centre · 1/2/3 kills
      f/v filter · r reset · +/- master · q quit
