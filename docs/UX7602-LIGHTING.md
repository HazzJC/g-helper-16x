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
| 0 | 0–20 | *dead — no physical LEDs* | — |
| 1 | 21–41 | Esc, F1–F12, PrtSc, Insert, Delete | 0–15 |
| 2 | 42–62 | `` ` `` 1–0 - = Backspace×3, Home | 0–16 |
| 3 | 63–83 | Tab, Q–P, `[`, `]`, `#`, *(2 dead)*, PgUp | 0–13, 16 |
| 4 | 84–104 | Caps, A–L, `;`, `'`, Enter×3, PgDn | 0–16 |
| 5 | 105–125 | LShift, `\`, Z–`/`, RShift×3, End | 0–16 |
| 6 | 126–146 | Ctrl, Fn, Win, Alt, Space, arrows | 0–14 |
| 7 | 147–167 | Left/right lightbars **and** the small arrow keys | see below |
| 8 | 168–175 | *dead* | — |

Notes:

- **Columns 17–20 of every row are dead.** They are numpad positions the chassis doesn't have.
- **Wide keys occupy three consecutive slots** — Backspace (55–57), Enter (97–99), RShift
  (117–119). Lighting all three gives an even wash; lighting one gives a hotspot.
- **The arrow cluster straddles rows 6 and 7**, as the physical half-height inverted-T does. Slot
  139 lights the up arrow; slots around 159–161 light left/down/right. The exact per-arrow
  assignment is **not yet pinned down** — the keys are small enough that light bleed between them
  made the photo ambiguous.
- **Row 7 drives the side lightbars.** Slot `147` (left) and `163` (right) are corroborated by
  ASUS's own per-key editor, which names its two lightbar UI elements `key147` and `key163`.
  Lighting all of 147–167 lit the sidebars, so the bars are likely more than two addressable LEDs
  — **worth pinning down**, since it would allow gradients along the bars rather than two blocks.
- Because columns are "nth key along the row" and rows have different key counts, a single column
  index does **not** trace a straight vertical line — it follows the keyboard's natural stagger.
  This looks correct for falling-drop effects and is not a bug.

---

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

1. **Lid logo.** `AsusACPI.SetMonogramLogo()` (ACPI `DEVS(0x00100066)`) returns result `0` on
   every call — failure by this codebase's own convention. The `0xC1`/`0xC2` reports on `MI_02`
   are the untested alternative path.
2. **Lightbar resolution.** Whether row 7 exposes more than the two known lightbar slots.
3. **Arrow-key slots.** Exact per-arrow assignment across rows 6/7.
4. **164 vs 176 slots.** One capture implies 164; 176 works regardless.
5. **`0x5B` and `0xC1`/`0xC2`.** Both return structured data; purpose unknown.
