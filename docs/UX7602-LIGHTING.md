# ZenBook Pro 16X OLED (UX7602) — lighting protocol and LED layout

Reference for the keyboard/lightbar lighting on the ASUS ZenBook Pro 16X OLED. Everything here
was reverse engineered from one machine (`UX7602BZ_UX7602BZ`, firmware `X7602BZ.100`); ASUS does
not publish this protocol.

**How claims are labelled.** *Confirmed* means someone watched the physical keyboard respond to a
specific isolated command. *Derived* means it comes from ASUS's own shipped binaries
(`AsusExclusiveAgent.exe`, `AsusExclusive.dll`) but wasn't separately verified on hardware.
*Unverified* means neither. This distinction matters — an earlier pass on this fork shipped a
"verified" checklist that had never been run, and one of its guesses actively broke the feature.

---

## 1. Device topology

`ProbeAura.exe enumerate` (live HID enumeration) — **confirmed**:

| Interface | Report | Sizes (F/O/I) | Purpose |
|---|---|---|---|
| `MI_01 & COL01` | `0x5A` | 64 / – / 6 | Brightness, FnLock |
| `MI_01 & COL02` | `0x5B` | 33 / – / – | Unknown — returns structured data, purpose never determined |
| `MI_01 & COL03` | — | – / – / 5 | Input only, unexplored |
| `MI_01 & COL04` | `0x5C` | 64 / 64 / 32 | **All lighting** (keys + lightbars) |
| `MI_00` | `0x01` | – / 2 / 33 | Standard keyboard HID |
| `MI_02` | `0xC1`, `0xC2` | 61 / 61 / 17 | Unknown — candidate for the lid logo, never confirmed |

VID `0x0B05`, PID `0x8854`. Everything below is report `0x5C` on `MI_01 & COL04`.

### Reading device state

`HidD_GetFeature` on `0x5C` returns a 64-byte buffer where **bytes 0–15 echo the last command
written to the device** (across collections — a brightness write to `0x5A` shows up here too) and
**bytes 16–26 are the firmware version string**, `X7602BZ.100`. Confirmed.

That echo is genuinely useful: polling it while ASUS's own software drives a pattern is how this
protocol was captured in the first place, without any packet-capture tooling.

---

## 2. Command reference (report `0x5C`)

All commands are 64-byte Feature reports, zero-padded.

### `0xA0` — set hardware effect &nbsp;·&nbsp; `0xA5` — apply

```
5C A0 00 <mode> <R> <G> <B> <speed> <direction>     then     5C A5
```

- `speed`: `1` slow, `2` normal, `3` fast.
- `direction`: `0`/`1`. Only meaningful for directional effects.
- Both packets are required; the effect changes on the `0xA5`.

These run **on the MCU**. They keep animating with no host attached — and with nothing streaming
over them, they survive the host process exiting.

The mode byte matches G-Helper's own `AuraMode` enum ordinals directly. **Confirmed working:**

| Mode | Name | Colour used? | Notes |
|---|---|---|---|
| `0` | Static | yes | |
| `1` | Breathe | yes | |
| `2` | Color Cycle | no | |
| `3` | Rainbow | no | |
| `5` | Raindrop | **no** | Firmware's own random red/white/blue animation; colour parameter ignored |
| `10` | Strobe | yes | |
| `12` | Flash | yes | |

**Confirmed to do nothing** at their enum ordinal: `4` (Star), `6` (Highlight), `7` (Laser),
`8` (Ripple), `11` (Comet). Either absent from this firmware or living at other byte values.
These are hidden from the UI for this model.

ASUS's own UI (`AsusExclusive.dll`, `AsusExclusive.UX7602.EFFECTMODE`) offers exactly
Static / Strobing / Breathing / ColorCycle / Rainbow / Rainny / ApRainbow — consistent with the
above, and with no Star/Laser/Ripple/Comet. *Derived.*

### `0xA2 00 00` — enable host-streamed per-key mode

```
5C A2 00 00     (rest zero)
```

**This is the single most important packet in this document, and it was misunderstood twice.**

It puts the controller into host-streamed mode and resets the per-key buffer. It must be sent
**before** a frame, and it is what lets a streamed frame pre-empt a firmware effect that is still
animating on the MCU.

Its history on this fork:

1. An early pass guessed it was a trailing *commit* and sent it **after** the chunks. That blanks
   the keyboard — the reset wipes the frame that was just painted. This was the actual cause of
   the original "backlight is off despite max brightness" complaint.
2. The next pass removed it **entirely**. Solid colours then worked, but only by accident: ASUS's
   agent had already put the MCU into host-streamed mode, and killing it left the MCU in that
   state. From a clean boot, or after any hardware effect was selected, streaming did nothing —
   which is exactly why every software-driven mode (Heatmap, Gradient, Ambient, Battery, Audio,
   Zone Test) appeared broken on this model and got hidden from the UI.

It is not a guess. It appears verbatim in `AsusExclusiveAgent.exe`, built by
`mov dword [rsp+40h],0A25Ch` into an otherwise-zeroed 64-byte buffer, at two call sites
(`0x1400239F7`, `0x140027CC9`), each immediately preceding `HidD_SetFeature`. *Derived.*

**Confirmed live:** set hardware Rainbow (`mode=3`), let it animate, then send this packet
followed by a solid-red frame stream — the rainbow is replaced by solid red immediately. Without
the packet, the rainbow keeps running and the stream is ignored.

### `0xA2 00 01` — stream a frame chunk

```
5C A2 00 01 01 00 <chunkStart> <count> 00  <R0 G0 B0> <R1 G1 B1> ... (up to 16 triples)
```

A full frame is **11 chunks of 16 LEDs**, `chunkStart` = 0, 16, 32 … 160, covering 176 slots.
There is **no** trailing packet — the chunks are the whole transaction.

Confirmed live, and matched byte-for-byte against the agent's own construction (call site
`0x140023F6B`): `mov dword [rsp+60h],100A25Ch` for the first four bytes, `mov word [rsp+64h],1`
for the next two, then `chunkStart`, `count`, a zero, and RGB triples from offset 9.

> One capture of ASUS's traffic showed `count=0x04` at `chunkStart=0xA0`, implying a real total of
> 164 rather than 176. Streaming all 176 works fine — slots past the end are ignored — so this is
> cosmetic, but it is still unreconciled.

### `0xA1` — notification/preview blinks

A family of commands (`5C A1 00 <n>` and `5C A1 02 01` variants) found in the agent under
`UX7602PreviewLightType`: POWERONOFF, DETECTUSB, AIPT, BATTERYSAVER, SOFTWARESITCH, MAILNOTIFY.
Fixed-parameter notification blinks rather than general-purpose control. *Derived, never tested.*

### Brightness — report `0x5A`

```
5A BA C5 C4 <level>
```

`level` 0–3. Real dimming, not on/off. Confirmed.

---

## 3. LED index layout

**Confirmed live** by lighting slot ranges and reading the result off the physical keyboard.

The index space is a **row-major matrix with a stride of 21**:

```
slot = row * 21 + column
```

| Row | Slots | Contents | Live columns |
|---|---|---|---|
| 0 | 0–20 | **slot 0 = the lid logo** (full RGB); 1–20 dead | 0 |
| 1 | 21–41 | Esc, F1–F12, PrtSc, Insert, Delete | 0–15 |
| 2 | 42–62 | `` ` `` 1–0 - = Backspace, Home | 0–16 |
| 3 | 63–83 | Tab, Q–P, `[`, `]`, `#`, *(2 dead)*, PgUp | 0–13, 16 |
| 4 | 84–104 | Caps, A–L, `;`, `'`, Enter, PgDn | 0–16 |
| 5 | 105–125 | LShift, `\`, Z–`/`, RShift, End | 0–16 |
| 6 | 126–146 | Ctrl, Fn, Win, Alt, Space, ↑ | 0–14 |
| 7 | 147–167 | Both lightbars **and** ←↓→ | 0, 12–14, 16 |
| 8 | 168–175 | *dead* | — |

Exact assignments for the parts that aren't a simple run:

| Element | Slot(s) |
|---|---|
| **Lid logo** | `0` — full RGB, same chunk stream (**not** the ACPI MonogramLogo call) |
| **Left lightbar** | `147` — a single LED |
| **Right lightbar** | `163` — a single LED |
| Up arrow | `139` (row 6) |
| Left / Down / Right arrows | `159` / `160` / `161` (row 7) |
| Spacebar | `132` — one centred LED; 130/131/133/134 dead |
| Backspace / Enter / RShift | one live LED mid-key; write the whole 3–4 slot span for safety |
| Bottom-row modifiers | Ctrl `126`, Fn `127`, Win `128`, Alt `129`, AltGr `135`, Menu `136`, Ctrl `137` |

Notes:

- **The function row is dense** — Esc `21`, F1–F12 `22`–`33`, PrtSc `34`, Insert `35`, Delete `36`.
  The inherited Strix `packetMap` in `Aura.cs` disagrees (it puts gaps at 22/27/32 and F10–F12 at
  34–36); on this machine it is wrong. Confirmed by photograph.
- **Columns 17–20 of every row are dead.** They are numpad positions the chassis doesn't have.
- **The lightbars have no resolution.** Lighting the whole 147–167 band lights them, but only
  147 and 163 actually drive them — everything between is dead, so a gradient along a bar is not
  possible. Two zones is the ceiling. (147/163 are corroborated by ASUS's own per-key editor,
  which names its two lightbar UI elements `key147` and `key163`.)
- **The arrow cluster straddles rows 6 and 7**, as the physical half-height inverted-T does.
- Because columns are "nth key along the row" and rows have different key counts, a single column
  index does **not** trace a straight vertical line — it follows the keyboard's natural stagger.
  This looks correct for falling-drop effects and is not a bug.

### The lid logo

The lid logo is **slot 0 of this same LED buffer, in full RGB**. It is painted, animated and
streamed exactly like a key.

This is worth stating plainly because the app previously drove it through
`AsusACPI.SetMonogramLogo()` — an ACPI `DEVS(0x00100066)` call that takes a *boolean* and returns
failure (`result=0`) on every call on this firmware. That was the wrong channel *and* the wrong
shape: the hardware logo is RGB, as ASUS's own per-key editor shows (`alogoPath.Fill` is a colour
brush, and its IPC has `ALogoLighting`/`ALogoSetting` function ids). Those ACPI calls have been
removed for this model.

It was missed for so long because slot 0 sits in the otherwise-dead row 0, and because the lid
faces away from anyone watching the keyboard during a test.

## 4. How this maps onto the code

| Concern | Where |
|---|---|
| Model gate | `AppConfig.IsZenbookPro16X()` — model string contains `UX7602` |
| Report IDs, stream selection | `USB/AsusHid.cs` (`ZENBOOK_16X_AURA_ID = 0x5C`) |
| Hardware effects (`0xA0`/`0xA5`) | `Aura.SendZenbook16XHardwareMode` |
| Zone flood via per-key stream | `Aura.ApplyZenbook16XDirect` |
| Enable packet, layout, streaming | `USB/Zenbook16X.cs` |
| Software per-key animations | `USB/Zenbook16X.cs` — `PerKeyEffect`, `PerKeyEngine` |

### Adding a per-key effect

Subclass `PerKeyEffect`, draw into the frame buffer, and hand it to `PerKeyEngine.Start`:

```csharp
public class MyEffect : PerKeyEffect
{
    public override void Render(Color[] frame, double time, double dt)
    {
        for (int col = 0; col < Zenbook16X.COLS; col++)
            Zenbook16X.Blend(frame, Zenbook16X.Slot(Zenbook16X.ROW_TOP, col),
                             Zenbook16X.FromHue(time * 0.2 + col * 0.05));
    }
}
```

`PerKeyEngine` handles the enable packet, frame timing, dropped frames and teardown. Then add an
`AuraMode` value, list it in `Aura.GetModes()`, and dispatch it in `Aura.ApplyAura()`.

---

## 5. Testing on hardware

The probe CLI lives in a sibling directory outside this repo (it hardcodes a path to installed
ASUS software and pulls in a decompiler dependency).

```bash
ProbeAura.exe enumerate                       # HID topology
ProbeAura.exe pk solid FF0000 15 1 3          # colour, seconds, enable, pre-set hw mode
ProbeAura.exe pk paint 42-62:FF0000 63-83:00FF00 -hold 25
ProbeAura.exe pk cols 13:FF0000 14:00FF00 -hold 25
ProbeAura.exe pk rain 25 7 neon               # seconds, drops/sec, palette
ProbeAura.exe pk app RAIN_COLOR 25            # drive the real Aura code path end-to-end
```

**Kill ASUS's agent before testing.** `AsusExclusiveAgent.exe` re-streams its own lighting
continuously and will overwrite anything you send within milliseconds:

```powershell
Get-Process AsusExclusiveAgent | Stop-Process -Force
```

It respawns next time MyASUS is opened. Note that firmware-generated effects (e.g. Raindrop) keep
animating after it is killed — the MCU renders those itself.

---

## 6. Still open

1. **164 vs 176 slots.** One capture of ASUS's traffic implies a real total of 164; streaming 176
   works regardless, since slots past the end are ignored.
2. **`0x5B` (COL02) and `0xC1`/`0xC2` (MI_02).** All return real, structured data when queried,
   but their purpose was never determined. `0xC1` was once guessed to be the lid-logo channel;
   that guess is now moot — the logo is slot 0 of the LED buffer — so these are unexplained
   rather than needed.
3. **`0xA1` notification blinks.** Found in the agent, never tested.
4. **Other UX7602 variants.** Everything here comes from one `UX7602BZ` on firmware
   `X7602BZ.100`. The `UX7602ZM` and other firmware revisions are untested, and the model gate is
   a plain `"UX7602"` substring match with no capability probing.

### Recently closed

- ~~Lid logo~~ — it is slot `0`, full RGB, on the LED buffer. The ACPI path was the wrong channel
  and the wrong shape. See §3.
- ~~Lightbar resolution~~ — one LED each, `147` and `163`. No gradient possible.
- ~~Arrow-key slots~~ — up `139`, left `159`, down `160`, right `161`.
- ~~Software-driven modes not working~~ — the missing `0xA2 00 00` enable. See §2.
