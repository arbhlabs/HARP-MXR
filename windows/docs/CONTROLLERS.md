# Controllers: two pads, extra buttons, GameSir G7 Pro

## Two controllers at once

HARP//MXR reads every connected Xbox-compatible pad (XInput slots 0-3).

- **Controller 1 (mixer)** uses the active profile (Default, Live DJ, Stick FX, …).
- **Controller 2 (FX pad)** uses its own profile, **FX Pad** by default. Its bindings never change controller 1's.

**Who is who.** Roles are chosen by device identity (vendor:product id), not by the order you plug pads in.

- The Bluetooth **Xbox Wireless Controller (045E:0B13)** is the FX pad whenever two pads are connected.
- With only one pad connected, that pad is the mixer.
- Unplugging one pad never moves the other to a different role.
- **Shift + Menu on the FX pad** (action `SwapControllers`) swaps the two roles and remembers the choice in `config.json` (`FxPadDevice`, `MixerDevice`).

**FX Pad profile**

| Input | Normal | Shift (hold View) |
|---|---|---|
| Left stick | Set pad: the 4 effects of the FX pad's set (direction picks, distance = amount, diagonals morph) | same |
| Right stick | Set pad: the 4 effects of the **next** set | same |
| LT / RT | Echo throw (pressure) / riser build | Brake depth / reverse |
| A / B / X / Y | Stutter 1/8 · Stutter 1/16 · Transformer 1/16 · Freeze (hold) | Stutter 1/4 · Stutter 1/32 · Transformer 1/32 · Spin-back |
| LB / RB | Cut (hold) · Sub drop | Reverse (hold) · Brake (hold) — after the 350 ms profile-chord wait |
| D-pad ↑ ↓ | Next / previous set | same |
| D-pad ← → | Target: focus deck · A · B · both decks | same |
| L3 / R3 | Previous / next set | FX clear |
| View tap / Menu tap | FX clear / cheat sheet | Menu: swap controllers |
| Hold Menu 0.9 s | Mapping editor for controller 2 (Find input listens to the FX pad) | |
| View + LB / RB tap | Previous / next FX pad profile | |
| View + Menu held | Panic | |

## Extra buttons (paddles)

XInput reports 14 buttons only. HARP//MXR also reads **Windows.Gaming.Input raw buttons**. Raw buttons 0-9 are the standard A B X Y LB RB View Menu L3 R3. **Raw buttons 10-17 become Extra1-Extra8.**

- A raw button that goes down together with an XInput button is ignored for good. That is a paddle the pad's firmware maps to an existing button, so nothing fires twice.
- The first press of a new raw button is held back one poll (16 ms) to decide this.
- In **Mappings**, the rows *Extra 1-8* can be bound like any button. **Find input** captures them too.

## GameSir G7 Pro

Extra buttons work when the selected hardware mode and firmware expose independent raw inputs to Windows. Check each button with **Find input** before relying on the factory profile.

The **GameSir G7 Pro** profile extends Stick FX with these assignments:

| Button | Action |
|---|---|
| L4 (`Extra1`) | Shift; View still works as Shift too |
| R4 (`Extra2`) | Next set |
| L5 (`Extra3`) | Pause / play the focus deck |
| R5 (`Extra4`) | Sync |

**What to do**

1. Put the G7 Pro on the unprogrammed hardware profile and connect it by USB or its dongle.
2. Open **Mappings** and press **Find input**, then press a back paddle.
   - If the editor jumps to an **Extra** row, the paddle is readable. Check that the order matches the profile above; if not, re-bind with Find input.
   - If nothing happens, the pad does not send that paddle to Windows in this mode. In the GameSir app (GameSir Nexus), assign the paddles to buttons you can spare, for example L3/R3 or View, and bind those instead. Mapping a paddle to an existing button duplicates it, so choose ones you do not otherwise use.
3. Check your chosen buttons with HARP//MXR in the foreground. Background input for raw extra buttons depends on Windows and the device.
