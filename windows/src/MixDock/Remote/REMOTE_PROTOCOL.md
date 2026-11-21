# MixDock Remote protocol v1 (phone controls the Windows MixDock engine)

Transport: WebSocket (RFC 6455), text frames, one JSON object per frame, path `/mixdock/v1`.
The phone is a controller only: audio stays on the PC. No audio crosses the link.

## Safety defaults (PC side)
- Disabled by default. Enabled only by `remote.enabled=true` in the PC config and an explicit user action.
- Binds `127.0.0.1` only. LAN exposure (`0.0.0.0` or a LAN IP) needs an explicit opt-in and `remote.allowLan=true`.
  Until then a phone can reach it only through `adb reverse tcp:<port> tcp:<port>` over USB.
- Every connection must authenticate before any control message is accepted; otherwise the socket is closed (code 1008).

## Pairing and auth
1. PC shows a 6-digit pairing code, valid 120 s, single use, 5 wrong tries lock pairing for 60 s.
2. Phone sends `{"t":"pair","code":"123456","name":"Pixel 7"}`.
3. PC replies `{"t":"paired","token":"<32 hex chars>"}`. Phone stores the token.
4. Later connections send `{"t":"auth","token":"<token>"}`; PC replies `{"t":"ready","v":1}` or closes.
Tokens are compared in constant time and can be revoked from the PC.

## Phone to PC
| message | meaning |
|---|---|
| `{"t":"set","deck":"A","p":"low","v":3.0}` | deck param. `p` = `low`,`mid`,`high` (dB -12..12), `filter` (-1..1, 0 = off, negative = low-pass, positive = high-pass), `trim` (0..1), `mute` (0/1) |
| `{"t":"set","p":"cross","v":0.5}` | crossfader 0 (A) .. 1 (B) |
| `{"t":"set","p":"master","v":0.8}` | master 0..1 |
| `{"t":"app","deck":"B","id":"<app id>"}` | assign an app to a deck (instant takeover on the PC); empty `id` clears |
| `{"t":"apps"}` | request the list of mixable apps |
| `{"t":"ping"}` | keep-alive, answered by `pong` |

Unknown `t`, unknown `p`, or out-of-range values are ignored or clamped; they never close the socket.

## PC to phone
| message | meaning |
|---|---|
| `{"t":"state","A":{"app":"Spotify","low":0,"mid":0,"high":0,"filter":0,"trim":0.85,"mute":false},"B":{...},"cross":0.5,"master":0.8}` | full state after auth and after any change |
| `{"t":"apps","list":[{"id":"1234","name":"Spotify"}]}` | mixable apps |
| `{"t":"peaks","A":0.42,"B":0.10}` | post-DSP peaks 0..1, about 15 per second |
| `{"t":"error","msg":"..."}` | non-fatal problem |

Max frame 4 KiB, enforced before parsing.
