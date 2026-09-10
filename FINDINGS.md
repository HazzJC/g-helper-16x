# ASUS ZenBook Pro 16X OLED (UX7602) Lighting — Findings, Fixes, and Open Problems

Sessions: 2026-09-09/10. This documents what was actually verified on real hardware (as opposed
to what earlier sessions *claimed* was verified — see "Prior claims were not real" below), what
was fixed, and what's still broken or unknown, so a future session can pick up without
re-deriving all of this.

> **2026-09-10 update — read this before the rest of the file.** The "no trailing commit packet"
> conclusion below was half right and half wrong, and the "software-driven modes don't work" open
> problem is now **solved**. `[0x5C, 0xA2, 0x00, 0x00]` is not a commit — it is an **enable**,
> and it belongs **before** the chunk stream. Sections below that predate this are kept for the
> record but are superseded by `docs/UX7602-LIGHTING.md`, which is now the reference document.
>
> - Sent **after** the chunks (first pass): blanks the keyboard — the reset wipes the frame that
>   was just painted.
> - **Removed entirely** (second pass): solid colours worked only by accident, because ASUS's
>   agent had already put the MCU into host-streamed mode and killing it left the MCU there. From
>   a clean state, or after any hardware effect was selected, streaming did nothing — which is
>   exactly why every software-driven mode looked broken and got hidden.
> - Sent **first** (now): confirmed live — set hardware Rainbow, let it animate, then enable +
>   stream solid red, and the red takes over immediately. Heatmap, GPU Mode, Ambient, Battery,
>   Audio, Audio Pulse, Gradient and Zone Test are all unhidden again; Zone Test, Gradient and
>   Ambient were re-confirmed live.
>
> The packet is not a guess: it appears verbatim in `AsusExclusiveAgent.exe`
> (`mov dword [rsp+40h],0A25Ch` into a zeroed 64-byte buffer, call sites `0x1400239F7` and
> `0x140027CC9`), each immediately before `HidD_SetFeature`.
>
> Also newly confirmed: the LED index space is a **row-major matrix with a stride of 21**
> (`slot = row * 21 + column`), mapped key by key against the physical keyboard. Full table in
> `docs/UX7602-LIGHTING.md`.

## TL;DR for the next agent

- The keyboard's real per-key/lightbar protocol is **report `0x5C`, opcode `0xA2`**: one
  `[5C A2 00 00]` enable, then 11 chunks of up to 16 LEDs each (`[5C A2 00 01 01 00 start count 00 ...]`),
  and nothing after. Implemented in `app/USB/Zenbook16X.cs`; confirmed live.
- Single-color hardware effects (Static/Breathe/ColorCycle/Rainbow/Strobe, plus bonus-discovered
  Rain="Raindrop" and Flash) use **report `0x5C`, opcode `0xA0` (mode-set) + `0xA5` (apply)**, one
  packet each, no chunking. Also confirmed live and now used consistently (Static used to
  incorrectly route through the broken chunk path — fixed).
- Brightness (report `0x5A`, `[0x5A,0xBA,0xC5,0xC4,level]`, level 0-3) is confirmed working
  correctly (real dimming, not on/off) now that Static routes through the right channel.
- Software-driven modes (Heatmap, GPU Mode, Ambient, Battery, Audio, AudioPulse, Gradient, Zone
  Test) **now work** and are unhidden — see the update box above.
- A software-rendered **"Colour Rain"** per-key effect is implemented
  (`AuraMode.RAIN_COLOR`, `RainEffect` in `app/USB/Zenbook16X.cs`) and confirmed live end-to-end
  through the real `Aura.ApplyAura()` path.
- **Still broken / not understood, hidden from the UI rather than shipped broken:**
  Star, Highlight, Laser, Ripple, Comet (hardware modes — enum ordinal ≠ real hardware mode byte
  for these, or absent from this firmware).
- Monogram lid logo control (ACPI WMI `DEVS(0x00100066)`) **does not work** — every call returns
  result code `0`, which the existing `DeviceSet` logging treats as failure (only `1` is "OK").
  Never fixed or replaced.

## Environment / how to test

- The real app: `app/GHelper.csproj`, in this repo.
- The live hardware CLI probe tool used to find all of this: `test/ProbeAura.csproj`, kept as a
  **sibling directory next to this repo** (`../test/` relative to the repo root), not committed
  here — it hardcodes a machine-specific MyASUS package path and pulls in a decompiler dependency,
  so it's kept as a local-only diagnostic tool rather than shipped in the fork's history. It's
  built against this repo's `GHelper.csproj` via a project reference, so it calls the actual
  `Aura`/`AsusHid`/`AppConfig` classes directly — not a separate reimplementation. If you don't
  have that folder, recreate `ProbeAura.csproj`/`ProbeAura.cs` per the descriptions below; none of
  it is required for the app itself to build or run.
- Build: `dotnet build app/GHelper.sln -c Debug` (and `-c Release`) — both currently 0
  warnings/0 errors.
- Run the probe tool: `test/bin/Debug/net10.0-windows/ProbeAura.exe <command> [args]`. Run
  `ProbeAura.exe` with no args (or an unknown command) to print usage. Useful commands added this
  session:
  - `enumerate` — dumps every ASUS HID collection (VID 0x0B05) with its supported report IDs.
    Ground truth for the device topology (see below).
  - `raw <pid_hex> <col_filter|-> <reportid_hex> <byte hex...>` — sends one raw Feature report.
    This is how individual protocol hypotheses were tested without touching the real app code.
  - `getfeature` / `getfeatureloop` — reads back the *current* Feature report content. Because
    HidD_GetFeature on this device just echoes "whatever was last SetFeature'd to this report"
    (not a live device-state readout), this is really only useful to see **your own recent
    writes**, or to catch another process's writes if you poll fast enough while it's actively
    streaming (this is exactly how the real per-key protocol was captured from MyASUS — see
    below). Careful: since each `ProbeAura.exe` invocation is a **new OS process**, the moment it
    exits it closes its HID handle, and the keyboard visibly reverts — so single-shot commands
    don't demonstrate persistence.
  - `chunks` / `chunksout` — manually construct the 11-chunk buffer and stream it (Feature vs.
    Output report), with an option to skip the trailing "commit"-shaped packet. This is the tool
    that isolated the actual bug (see below).
  - `zonetest` / `fulltest` / `fullteststatic` / `holdzonetest` — exercise the *real*
    `Aura.ApplyDirect` / `Aura.ApplyBrightness` / `Aura.ApplyAura` production code paths (not raw
    bytes) so a fix can be verified end-to-end, not just "the raw bytes seem right".
  - `disasmagent` / `findcallers` / `decompile` — native/managed reverse-engineering helpers (see
    "Reverse-engineering tooling" below).
- **The competing process problem**: ASUS's own `AsusExclusiveAgent.exe` (part of MyASUS,
  `B9ECED6F.ASUSPCAssistant` package) actively re-streams its own lighting state continuously while
  a custom per-key effect is selected in MyASUS. If it's running, it will silently overwrite
  whatever G-Helper (or the probe tool) sends within milliseconds. **Kill it before testing**:
  `Get-Process AsusExclusiveAgent | Stop-Process -Force`. It respawns next time MyASUS opens. Note:
  some *firmware-generated* effects (confirmed: Raindrop) keep animating even after the agent
  process is killed, because the MCU renders them internally once triggered — killing the agent
  only stops *host-streamed* effects (the actual custom per-key patterns), not built-in ones.

## Device topology (from `ProbeAura.exe enumerate`, ground truth)

```
VID 0x0B05 PID 0x8854
  MI_01 & COL01  -> Report 0x5A (Feature 64B, Input 64B)   - brightness + FnLock
  MI_01 & COL02  -> Report 0x5B (Feature 33B)              - unexplored, unconfirmed purpose
  MI_01 & COL03  -> Input 0x02 only                        - unexplored
  MI_01 & COL04  -> Report 0x5C (Feature/Output/Input 64B/32B/64B) - lighting (keys+lightbars)
  MI_00                                                     - standard keyboard HID (kbd usage)
  MI_02          -> Report 0xC1 (Feature/Out/In 61B), 0xC2 (Out/In) - queried, not yet understood
```

`0x5B` (COL02) and `0xC1`/`0xC2` (MI_02) were probed with `GetFeature` and got real, distinct
(non-echo) responses, so they're live registers of some kind, but their actual purpose wasn't
determined. `0xC1` was originally guessed (by a prior, unverified session) to be the Monogram
lid-logo channel; that was never confirmed and the logo is currently driven via ACPI instead
(which doesn't work — see below).

## The real per-key/lightbar protocol (confirmed live)

Captured live via `getfeatureloop` while MyASUS was actively driving a custom two-tone breathing
pattern (orange outer ring, cyan inner keys) that the user set up in its own per-key editor:

```
5C A2 00 01 01 00 <chunkStart> <count> 00  <R0 G0 B0> <R1 G1 B1> ... (up to 16 triples)
```

- `chunkStart` seen varying across 0x10, 0x30, 0x40, 0x50, 0x70, 0x90, 0xA0 in different polls —
  consistent with 11 sequential chunks of 16 LEDs each (0, 16, 32, ..., 160), i.e. up to 176 LED
  slots, though the actual meaningful range is smaller (see open question on real total below).
  Some captures showed `count=0x04` (4) at `chunkStart=0xA0` rather than the `8` this codebase
  assumes for the last chunk — **not yet resolved**, see "Open question: real key count".
- This exactly matched what a prior (untested) session had already written in
  `ApplyZenbook16XDirect` in `Aura.cs` — the header format was right all along.
- What was **wrong**: the code also sent a trailing "commit" packet, `[0x5C, 0xA2, 0x00, 0x00]`
  (all following bytes zero). This packet was never observed in MyASUS's real traffic across
  ~20 polls. Empirically:
  - Chunks alone (no commit), via Feature report: **no visible effect** (whatever was already
    displayed stayed as-is).
  - Chunks + the guessed commit packet: **turns the keyboard off**.
  - Chunks alone via **Output** report instead of Feature: also turns it off (so it's not a
    Feature-vs-Output transport issue).
  - Chunks alone (no commit) via Feature report, **after killing `AsusExclusiveAgent.exe` so it
    stops overwriting**: **works** — confirmed live as solid orange across the whole keyboard, and
    separately confirmed with distinct colors per zone (cyan keys, magenta left lightbar, yellow
    right lightbar) via the real `Aura.ApplyDirect(Color[])` production code path.
  - **Fix applied**: removed the commit-packet send entirely from `ApplyZenbook16XDirect`
    (`app/USB/Aura.cs`). Sending the 11 chunks is sufficient; nothing else is needed.

### Key/lightbar index mapping

`packetMap`/`packetZone` (the existing tables in `Aura.cs`, inherited from the original
implementation attempt) assign:
- Keyboard keys to the standard ROG matrix indices already in `packetMap`.
- Left lightbar → index 147, right lightbar → index 163.

This was **independently corroborated** by decompiling `AsusExclusive.dll` (MyASUS's per-key
editor UI, see below) — its own XAML/code-behind literally names UI elements `key147` and
`key163` for the two lightbar zone editors. So those two specific indices are solid. The rest of
`packetMap`'s 168 entries were **not** individually re-verified this session (solid-color and
one 3-zone test don't exercise per-key granularity) — see "Next steps" for how to do that cheaply
now that the underlying protocol works.

## Hardware mode-set protocol (confirmed live, and its Static-mode bug)

```
Mode-set: 5C A0 00 <mode> <R> <G> <B> <speed> <direction=00> 00...
Apply:    5C A5 00...
```

- Confirmed live for `mode=0` (Static, arbitrary color), `mode=1` (Breathe — was already
  implemented and logged as active before this session, just needed Static fixed alongside it),
  and via bonus discovery, `mode=5` and `mode=0x0C` (see below).
- **Bug found and fixed**: `ApplyZenbook16XAura` special-cased `AuraMode.AuraStatic` to route
  through `ApplyZenbook16XDirect` (the per-key chunk path) instead of this simpler channel. Two
  observed consequences before the fix:
  1. Because the chunk path had the commit-packet bug (see above), selecting Static in the UI
     turned the keyboard **off** instead of showing the chosen color. This is almost certainly
     the user's original complaint ("keyboard backlight is off despite max brightness").
  2. Even after the commit-packet fix, the chunk path does **not reliably pre-empt a
     firmware-generated effect that's still actively animating** (e.g. Raindrop kept running even
     after G-Helper sent a full-black chunk stream + reapplied a `Aura.ApplyAura()` call with
     Static selected, while MyASUS/its agent were both fully closed). A raw `0xA0`/`0xA5`
     mode-set+apply pair, by contrast, reliably overrides any active firmware mode immediately.
  - **Fix applied**: `ApplyZenbook16XAura` no longer special-cases Static; it now always uses the
    `0xA0`/`0xA5` channel (`SendZenbook16XHardwareMode`), and the "backlight off" case
    (`!backlight`) also now uses this channel (mode 0, black) instead of the chunk path, for the
    same override-reliability reason.

### Bonus discovery: mode-byte numbering matches `AuraMode`'s own enum ordinals

While chasing why G-Helper's `AuraMode.Rain` (enum value `5`) didn't do anything, we found the
Zenbook's hardware mode byte just **is** the `AuraMode` enum's ordinal value directly — no
separate mapping table needed. Confirmed live:
- `mode=5` → **not** a generic "rain" per-key animation; it's the hardware's built-in **Raindrop**
  preset (random red/white/blue per key, animated in firmware — the color parameter is ignored,
  confirmed by sending a non-black color and seeing no change to the red/white/blue palette).
- `mode=0x0C` (12, `AuraMode.Flash`'s ordinal) → confirmed working live in the real G-Helper UI.
- `mode=0x0A` (10, `AuraMode.AuraStrobe`) → already known/working.
- **Not working** at their ordinal value (confirmed live, no effect): `Star` (4), `Highlight`
  (6), `Laser` (7), `Ripple` (8), `Comet` (11). Either these presets don't exist on this
  particular keyboard's firmware, or they live at different byte values than their enum ordinal —
  not determined.
- **Code change**: `ApplyZenbook16XAura`'s mode-byte computation simplified from an explicit
  switch (which defaulted every unmapped mode, including the ones that do work, to `0`/Static) to
  a direct `(byte)mode` cast — this is what made Rain and Flash reachable at all. Modes confirmed
  not to work (Star/Highlight/Laser/Ripple/Comet) are hidden from `GetModes()` for this model
  specifically (`AppConfig.IsZenbookPro16X()` guard) rather than left in the UI as broken options.

## SOLVED: software-driven "dynamic" modes don't work

**Root cause: the missing `[0x5C, 0xA2, 0x00, 0x00]` enable packet.** Without it the MCU stays in
whatever hardware effect it was last given and renders straight over every streamed frame, so the
chunks are accepted and ignored.

Why the earlier evidence was so confusing:

- The manual `zonetest`/`fulltest` probes appeared to prove the streaming path worked. They did —
  but only because ASUS's agent had already put the MCU into host-streamed mode for its own custom
  per-key effect, and killing the agent left the MCU in that state. The probes inherited it.
- Selecting a software mode in the app went through `ApplyAura()`, which returns early for these
  modes *before* the Zenbook `0xA0`/`0xA5` block — so the MCU was left running a firmware effect,
  and nothing ever enabled host streaming.
- That also explains the separate observation that "the chunk path does not reliably pre-empt a
  firmware effect that's still animating". It doesn't — unless the enable packet precedes it.
  With the enable, pre-emption is immediate and reliable.

It was never a timer, threading or `hidLock` problem. No logging was needed in the end.

**Fix**: `Zenbook16X.DirectEnable()` is now called at the head of `ApplyZenbook16XDirect` and by
`PerKeyEngine.Start`. Zone Test, Gradient and Ambient were re-confirmed live afterwards
(distinct colour zones; a white-to-cyan blend; screen-tracking colour respectively), and all eight
software modes are unhidden in `GetModes()`.

## Open question: real key/zone count (168 vs 164)

The current code assumes 168 addressable LED indices (11 chunks: ten of 16 + one of 8,
`chunkStart` 0..160). One live capture of MyASUS's own traffic showed `count=0x04` (4, not 8) at
`chunkStart=0xA0` (160) in two separate polls — which would imply a real total of **164**, not
168. This was not reconciled; the current 168-count implementation was still confirmed to light
the *entire* visible keyboard correctly in the solid-color test, so if the real count is 164, the
extra 4 slots are apparently harmless padding rather than something that breaks the display. Worth
settling properly since it affects exact index math for any future true per-key UI.

## Prior claims were not real — a warning about this repo's history

Before this session, `implementation_plan.md` and `walkthrough.md` (kept outside the repo)
claimed a full live-hardware verification pass had been run via `test/ProbeAura.cs`, including a
transcript ending "ALL VERIFICATION TESTS PASSED SUCCESSFULLY". This was checked and found false:

- `test/ProbeAura.cs`'s `Main()` started with `InspectExclusiveLighting.Run(); return;` — an
  unconditional early return before any of the code that would produce that transcript. The
  "verification" code was unreachable.
- The file didn't even compile (`error CS0103: The name 'acpi' does not exist in the current
  context` — a local variable referenced but never declared).
- `InspectExclusiveLighting.Run()` (the code that *did* run) is a .NET reflection dump of
  `AsusExclusive.dll`'s method/field signatures — a static analysis helper, not a hardware test.

Lesson for future sessions: don't trust prior "VERIFIED"/checklist claims in this repo's history
without re-running them yourself first. This session's own findings above were all confirmed by
the user watching the physical keyboard in response to specific, isolated test commands — treat
that standard as the bar for "confirmed."

## Reverse-engineering tooling built this session (in `test/`)

- `ProbeAura.cs` — rewritten from scratch (the original didn't compile); now a real CLI with the
  commands listed above. Injects a fresh `AsusACPI` instance into `GHelper.Program`'s internal
  static `acpi` field via reflection (that field is `public` but the containing `Program` class is
  `internal`, so a normal reference from the test project can't see it) so `Aura.cs`'s internal
  `Program.acpi.*` calls (e.g. `SetMonogramLogo`) don't null-ref when run outside the full app.
- `DecompileLighting.cs` — decompiles a .NET assembly's types matching name filters to readable
  C# via `ICSharpCode.Decompiler` (added as a NuGet dependency to `test/ProbeAura.csproj`). Loads
  via `File.ReadAllBytes` + `Assembly.Load(byte[])` / `new PEFile(path, stream)` rather than
  `Assembly.LoadFrom(path)`, because `LoadFrom` gets `UnauthorizedAccessException` on files under
  `C:\Program Files\WindowsApps\...` (raw `File.ReadAllBytes` on the same path works fine — this
  is a CLR-loader-specific restriction, not a filesystem ACL one, confirmed by testing raw byte
  reads via PowerShell successfully on the same path).
  - Used this to decompile `AsusExclusive.dll` (MyASUS's per-key editor, WPF UI). This is where
    the `key147`/`key163` lightbar-index confirmation and the sparse per-key wire format for the
    custom editor's own IPC payload (`SettingArray[5]=count`, then 5 bytes/key:
    `KeyIndex,Mode,R,G,B` — sent to a *different* process, see below, not confirmed to reach HID)
    came from.
- `FindSetFeatureCallers.cs` — parses a native PE's import table to find the IAT slot address for
  a given imported function (default: `HidD_SetFeature`), then scans `.text` (via the `Iced`
  disassembler, already a dependency) for every instruction whose IP-relative memory operand
  resolves to that slot (i.e. every `call qword [rip+X]` through that import), and dumps a
  disassembly window around each call site. This is what found the real `[0x5C,0xA0,0x00,0x00,...]`
  + `[0x5C,0xA5]` static/apply packet construction, byte-for-byte, inside
  `AsusExclusiveAgent.exe` (the actual native agent process, not the WPF UI DLL) — confirming the
  live-tested protocol against the real shipped binary. Also found two hardcoded "preview" light
  functions (`UX7602PreviewLightType`: POWERONOFF/DETECTUSB/AIPT/BATTERYSAVER/SOFTWARESITCH/
  MAILNOTIFY) using the *same* opcode-`0xA1` mode-set-like structure with different fixed
  parameters — not investigated further, likely notification-blink presets, not general-purpose.
- `DisasmAgent.cs` — pre-existing (from a prior session) but was verified this session to
  actually work: parses `AsusExclusiveAgent.exe`'s PE headers/imports directly (no reflection,
  since it's a native binary — `Assembly.Load` on it fails with "Bad IL format", confirmed).
  Confirmed this binary imports `HidD_GetFeature`/`HidD_SetFeature`/`HidD_GetAttributes` from
  `HID.DLL`, plus `SetupDiGetClassDevsW` etc. from `SETUPAPI.DLL`, plus (unexplored)
  `WinUsb_WritePipe`/`WinUsb_ReadPipe` from `WINUSB.DLL` — the WinUSB import was checked for
  callers and found to gate on PID `0x8835` (not our device's `0x8854`), so it's for some *other*
  ASUS USB product this same agent supports, not relevant here.

## Traced but abandoned: the "ApCustmLt" IPC path

The per-key custom editor in `AsusExclusive.dll` sends its data via
`MainPage.g_MainInstance.SendMessageTimeoutWithResult("AsusExclusiveAgent", 13, ptr, size, out _)`
— a `SendMessageTimeout` to a window presumably owned by `AsusExclusiveAgent.exe`, with a custom
opcode (`13`) and a marshaled `CommandStruct` (mode name string + a 1024-byte `SettingArray`
buffer: `SettingArray[5]=key count`, then 5 bytes per key — `KeyIndex, Mode, R, G, B`). This is
almost certainly how the per-key editor's data reaches the agent, but **the path from that
Windows message to an actual `HidD_SetFeature` call was never traced** — `FindSetFeatureCallers`
finds calls to `HidD_SetFeature` by scanning the *whole* `.text` section regardless of which
higher-level code path reaches them, so the real per-key protocol (the `0x5C`/`0xA2` chunk format
documented above) **was** found this way even without tracing the WindowProc dispatch — but the
*sparse* 5-bytes-per-key format from the IPC struct doesn't obviously correspond 1:1 to the dense
16-bytes-per-chunk HID format that was actually captured and confirmed, so there's presumably a
translation step in between (sparse key list → dense 168/164-slot buffer → 11 HID chunks) that was
never located in the disassembly. Not necessary for the fix (the wire format was captured directly
via `GetFeature` polling instead, a shortcut that turned out to be more effective than static
tracing), but would matter if a future per-key UI needs finer control than "flood the whole
keyboard with a Color[8] zone array" (see Next steps).

## Abandoned entirely: USB packet capture (Wireshark/USBPcap)

Before the `getfeatureloop` polling trick worked, this session tried to get ground-truth USB
traffic via Wireshark + USBPcap. Do not repeat this path without a good reason — it cost a lot of
UAC prompts and time for zero data:
- `winget install desowin.USBPcap` installs the driver, but `USBPcapCMD.exe --extcap-interfaces`
  returns nothing — that specific standalone build doesn't implement Wireshark's extcap protocol
  at all, so copying it into `Wireshark\extcap\` (the usual manual-integration fix) didn't help.
- `winget install WiresharkFoundation.Wireshark` installs the **MSI** variant, which (per
  Wireshark's own docs) never bundles USBPcap regardless — only the official `.exe` (NSIS)
  installer from wireshark.org has that option.
- Driving `USBPcapCMD.exe` directly (bypassing Wireshark) from PowerShell never produced a
  non-empty capture file no matter how the process was stopped (`Stop-Process -Force`,
  `taskkill` with and without `/F`) — it likely needs a real Ctrl+C console signal
  (`GenerateConsoleCtrlEvent`) to flush its output, which is non-trivial to send correctly from a
  separate PowerShell process/console.
- **What actually worked instead**, with zero extra installs: an ETW trace on the built-in
  `Microsoft-Windows-USB-UCX` provider (`logman create trace ... -p "Microsoft-Windows-USB-UCX"
  0xffffffffffffffff 0xff -ets`, then `tracerpt *.etl -o out.xml -of XML`) surfaces every USB
  Control Transfer's **full SETUP packet** (`bmRequestType`/`bRequest`/`wValue`/`wIndex`/
  `wLength`) — enough to *confirm* `SET_REPORT(FEATURE, ReportID=0x5C, wLength=0x40)` was being
  sent repeatedly by `AsusExclusiveAgent.exe` (PID visible in the event's `Execution` block) while
  the custom pattern was active. It does **not** include the actual transfer buffer bytes though
  (those live behind a raw kernel pointer, `fid_URB_TransferBuffer`, not dereferenced into the
  trace) — so this confirmed *which report* to focus on, but the actual byte content was obtained
  by the `getfeatureloop` polling trick instead (see main protocol section above), which turned
  out to be simpler and sufficient. Worth knowing this ETW technique exists and needs no install,
  in case USB-level confirmation is needed again without the Wireshark/USBPcap headache.

## Next steps (recommended)

Items 1 and 2 from the previous version of this list are **done** — the software-driven modes are
fixed (see "SOLVED" above) and the index layout is mapped (`docs/UX7602-LIGHTING.md`). What's left:

1. **Per-key control UI.** The streaming layer (`app/USB/Zenbook16X.cs`) and the effect framework
   (`PerKeyEffect` / `PerKeyEngine`) are in place; there is no UI for picking individual key
   colours yet. The layout table in `docs/UX7602-LIGHTING.md` is what a key-picker would render.
2. **More per-key effects.** `RainEffect` is the worked example. Anything frame-based (ripple from
   a keypress, wave, fire, starfield, typing-reactive) is now a subclass and three lines of
   wiring.
3. **Lightbar resolution.** Slots 147 and 163 are the two known lightbar zones, but lighting all
   of row 7 (147-167) lit the sidebars, so there may be more addressable LEDs along them. Worth
   a `pk walk 147 167` pass — if the bars have real resolution, gradients along them become
   possible instead of two flat blocks.
4. **Arrow-key slots.** Slot 139 lights the up arrow and ~159-161 light left/down/right, but the
   exact per-arrow assignment is unconfirmed (the keys are small and bleed into each other).
5. **Monogram lid logo**: `Program.acpi.SetMonogramLogo()` (ACPI `DEVS(0x00100066)`) reliably
   returns failure (`result=0`). Two options: (a) try the `0xC1` HID channel on `MI_02` instead
   (its `GetFeature` response looked like real structured device state, unlike the `0x5C` echo
   registers — worth a `FindSetFeatureCallers` pass specifically filtered to call sites that also
   reference `MI_02`'s report ID `0xC1`, or a targeted `getfeatureloop` poll while MyASUS's logo
   toggle is used, mirroring exactly how the per-key protocol was found), or (b) accept it's
   unsupported on this firmware and remove `HasLogo`/hide logo controls for this model.
6. Re-run the abandoned "trace the `ApCustmLt` opcode-13 IPC path" only if the per-key UI needs
   something the direct chunk protocol can't already do — see "Traced but abandoned" above. Given
   the chunk format is now fully understood, this is probably unnecessary.
