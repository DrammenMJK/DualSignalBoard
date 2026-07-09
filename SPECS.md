# LysKontroll — System Specification

Model railway layout controller for Drammen MJK, station Fossli.
Hardware: Arduino Uno (ATmega328P). Software: Arduino firmware + C# config tool.

---

## Hardware

| Component | Description |
|-----------|-------------|
| ATmega328P | 32 KB flash, 2 KB SRAM, 1 KB EEPROM (100 K write cycles) |
| MCP23017 | I2C GPIO expanders for additional I/O |
| Pens motors | Turnout motors, stall-hold (`01` or `10`), never `00` |
| Feedback switches | Two per Pens (Rett + Avvik pin), read via I2C |
| Manual switches | One per Pens (operator panel), read via I2C |
| Dreieskive motor | Rotating platform, 3 states: `00`=stopped, CW, CCW |
| Dreieskive switch | 3-position panel switch: middle / CW / CCW |
| LEDs | Rett + Avvik indicator per Pens, driven via I2C |
| Moment button | Physical signal-operation button |

---

## Site Configuration

Stored in EEPROM and in the site JSON file. Change per site without recompiling
(simulator uses `#define`; real firmware reads from EEPROM after initial upload).

| Parameter | Default | Fossli |
|-----------|---------|--------|
| `FIRST_PENS` | `'A'` | `'B'` |
| `LAST_PENS` | site-specific | `'I'` |
| `MAX_PENS` | 26 (A–Z) | 26 |
| `NUM_LED_OUTPUTS` | site-specific | 16 |

`LED_COUNT` (number of logical LED slots in the routing matrix) is derived: `(LAST_PENS - FIRST_PENS + 1) * 2`. It is **not** a site config — it follows automatically from the Pens range. `NUM_LED_OUTPUTS` is the number of physical hardware LED driver outputs and must be set per site.

---

## EEPROM Layout (1 024 bytes, ATmega328P)

All slots initialised to `0xFF`. `0xFF` in any slot = not yet configured.
Regions are indexed from `FIRST_PENS` (index 0 = `FIRST_PENS`, 1 = next letter, …).

| Address | Size | Content |
|---------|------|---------|
| `0x01` | 1 | Magic byte (`0xA5` = EEPROM has been initialised) |
| `0x0A` | 1 | Dreieskive last known position (display hint only) |
| `0x10` (REGION1_BASE) | MAX_PENS | Motor pair index per Pens |
| `0x30` (REGION1_POL) | MAX_PENS | Motor polarity (position at scan time: 0=Rett, 1=Avvik) |
| `0x50` (REGION2_RETT) | MAX_PENS | Feedback Rett pin per Pens |
| `0x70` (REGION2_AVVIK) | MAX_PENS | Feedback Avvik pin per Pens |
| `0x90` (REGION3_BASE) | MAX_PENS | Manual switch pin per Pens |
| `0xB0` (REGION4_RETT) | MAX_PENS | Rett LED index per Pens (`0xFF`=not set, `0xFE`=no LED, `0x00–0x0F`=index) |
| `0xD0` (REGION4_AVVIK) | MAX_PENS | Avvik LED index per Pens (same encoding) |
| `0xF0` (REGION5_MOTOR_PIN) | 1 | Dreieskive motor pair index |
| `0xF1` (REGION5_MOTOR_POL) | 1 | Dreieskive CW polarity (0=`01`, 1=`10`) |
| `0xF2` (REGION5_SW_CW) | 1 | Dreieskive switch CW pin |
| `0xF3` (REGION5_SW_CCW) | 1 | Dreieskive switch CCW pin |
| `0xF4` (REGION5_MOMENT) | 1 | Moment button pin |
| `0x100` (REGION6_BASE) | 144 | LED routing matrix (16 LEDs × 9 bytes) |

Pens positions are NOT stored in EEPROM — kept in RAM, refreshed from I2C feedback
on every loop iteration. EEPROM write cycles are preserved.

---

## Serial Protocol

Baud rate: **115 200**.

### C# → Arduino
- Single ASCII characters (no newline) for interactive commands and key presses.
- Full lines ending `\n` only during routing matrix upload.

### Arduino → C#
- Lines ending `\r\n`.
- **`!` prefix** — status message: always displayed immediately, `!` stripped.
- **No prefix** — protocol response (`READY`, `OK`, `STORED`, `END`, `ERR`):
  queued in capture mode, or displayed with `[A]` prefix in relay mode.

### C# ReadLoop (background thread)
```
line starts with '!'  →  Console.WriteLine(line without '!')
capturing             →  enqueue for caller (GetCapturedLine)
otherwise             →  Console.WriteLine("[A] " + line)
```

### Capture mode
Used for structured exchanges (routing matrix upload/download).
`StartCapture()` redirects non-`!` lines to a queue.
`StopCapture()` resumes normal relay behaviour.

---

## C# Program Structure

```
Program.cs          — port selection, startup, main menu (C / D / V / Q)
ArduinoConnection   — serial port + ReadLoop thread
ConfigSession       — config mode commands
DebugSession        — debug mode
VerifySession       — verify mode
```

After connecting, `Program.cs` waits 2 s then sends `'S'` and waits 400 ms
to let the Arduino return EEPROM status before showing the main menu.

---

## Relay Mode

`Relay()` in `ConfigSession` forwards every keystroke to the Arduino and
displays `!` status lines. C# has no command knowledge in relay mode — the
Arduino drives the entire interaction.

- **ESC** — breaks `Relay()` on the C# side AND is sent to the Arduino so
  the current command can also abort. Both sides exit simultaneously.
- Commands that complete **naturally** (not via ESC) must call
  `while (waitKey() != ESC) {}` before returning, so `Relay()` also exits.

---

## Key Bindings

### Main menu (`Program.cs`)
| Key | Action |
|-----|--------|
| C | Config mode |
| D | Debug mode |
| V | Verify mode |
| Q | Quit (sends Q to Arduino for reset, then C# exits) |

### Config menu (Arduino `runConfig`, C# `ConfigSession`)
| Key | Action |
|-----|--------|
| 1 | Motor scan |
| 2 | Switch mapping |
| 3 | LED mapping |
| 4 | Routing matrix |
| M | Moment button |
| D | Dreieskive switch |
| R | Reset EEPROM |
| Q | Exit config mode |

### Motor scan (relay mode)
| Key | Action |
|-----|--------|
| `FIRST_PENS`–`LAST_PENS` | Identify which Pens just moved |
| `0` | Skip this pair |
| `R` / `A` | Pens is now at Rett / Avvik |
| `1` / `2` | Dreieskive CW = `01` / `10` pattern |
| `X` | Emergency stop |
| `Y` / `N` | Override duplicate / re-enter letter |
| Esc | Abort scan |

### Switch mapping (relay mode)
| Key | Action |
|-----|--------|
| `FIRST_PENS`–`LAST_PENS` | Map or re-map that Pens switch |
| Esc | Finish |

### LED mapping (relay mode)
| Key | Action |
|-----|--------|
| `FIRST_PENS`–`LAST_PENS` | Select Pens to configure (or re-configure) |
| `N` / `P` | Next / previous LED index (wraps) |
| `S` | Save current LED for this position (Rett first, then Avvik) |
| `0` | No physical LED for this position (stores `LED_NO_PIN = 0xFE`) |
| Esc | Finish |

### Routing matrix (structured protocol, not relay)
| Key | Action |
|-----|--------|
| `U` | Upload from C# JSON file |
| `D` | Download to C# JSON file |
| `?` | Show JSON format |
| Esc | Done |

---

## Motor Scan Flow

1. Pre-populate `assigned[]` and `pairDone[]` from EEPROM.
2. If **all** Pens and Dreieskive are already configured:
   - Prompt "Type a Pens letter to re-configure it, or Esc to finish."
   - Operator types a letter (e.g. `C`): clears that one Pens from session state and clears its pair from `pairDone[]`.
   - Falls through to the pair scan — only the cleared pair will run; all others skip.
   - After the pair is re-assigned, loops back to step 2 to offer editing another.
   - `Esc` at this prompt → "Motor scan done." and returns.
3. Loop pairs `0..8`:
   - `pairDone[pair]` → silent skip.
   - All Pens already assigned → silent skip (only Dreieskive pair can still fire).
   - **Dreieskive pair** (no feedback timeout): automatically identified — ask `1`/`2` for CW polarity.
   - **Pens pair** (feedback detected): show remaining unassigned letters, operator types letter.
     - Duplicate detected → `Y` override / `N` re-enter.
     - Ask current position `R`/`A`, store to EEPROM.
4. Early exit when all Pens **and** Dreieskive are done.
5. `"Motor scan complete. Press Esc to return."` — waits for Esc.

---

## Switch Mapping Flow

Loop: operator types any Pens letter to map or re-map it, Esc to finish.
1. Check motor configured for that Pens (REGION1_BASE); error if not.
2. Drive that Pens to Rett (so switch follows).
3. Prompt operator to flip the panel switch to Avvik then back to Rett.
4. Detect switch (2 s), store pin to EEPROM, confirm stored.

No Y/N confirmation needed — the operator typed the letter intentionally.
Already-mapped Pens are silently re-mapped when the letter is typed again.

---

## LED Routing Matrix

- 16 logical LEDs: `B_Rett`, `B_Avvik`, `C_Rett`, `C_Avvik` … `I_Rett`, `I_Avvik`.
- Each LED holds up to 4 conditions (OR of AND-chains).
- Each condition: `rettMask` + `avvikMask` (bitmask of Pens that must be in that position).
- `count = 0xFF` → default 1-to-1 (LED lights when its own Pens is in matching position).
- `count = 0x00` → suppressed (always off; used for shared physical LEDs).
- Loaded from a site-specific JSON file (e.g. `Fossli.json`).

Wire format per LED: `CC [RR AA …]` (hex, space-separated).

---

## Simulator (`Simulator.ino`)

Replaces real hardware for testing the C# config program on an empty Arduino Uno.

- All strings in flash via `F()` and `vsnprintf_P`. No `String` objects.
- `#define ESC '\x1B'` — abort/quit inside all commands.
- `Q` reserved for top-level menus only (`runConfig`, `loop`).
- `'S'` command (silent) — returns EEPROM status; sent by C# after connect.
- `softReset()` — watchdog reset; **does not clear EEPROM**.
- `SIM_DREIESKIVE_PAIR = 4` — pair with no feedback (simulated Dreieskive).
- `SIM_PENS_COUNT = 8` — Fossli has 8 Pens (B–I).
- EEPROM indexing: `eepromIdx = k - FIRST_PENS` (site-relative, 0 = first Pens).
- In-session arrays (`assigned[]`, `simPensPos[]`): indexed by `k - 'A'` (universal, 0 = A).

---

## Key Design Rules

**Y, N, and all A–Z letters are reserved as Pens identifiers.**
They must NEVER be used as yes/no confirmation keys inside any command that
also accepts Pens letter input. Since FIRST_PENS/LAST_PENS may expand to cover
the full A–Z range on other sites, any A–Z letter is off-limits as a confirmation
key in relay mode.

Safe confirmation/action keys (outside A–Z or not reachable from Pens letter range):
- Digits: `0`–`9`
- Esc (`\x1B`)
- `1` / `2` (Dreieskive polarity choice)

Y/N are only permitted in pure C# dialogs that never reach the Arduino in relay mode
(e.g. "Upload to Arduino? Y/N" in `LoadAndUpload`), or in Arduino commands that do
not accept Pens letters at all (moment button, Dreieskive switch) — and only then on
sites where Y/N are outside FIRST_PENS..LAST_PENS.

**Prompts must appear before blocking on input.**
The Arduino must send its status prompt BEFORE calling `waitKey()`, so the user
sees what to type without needing to press a dummy key first. Invalid keys are
silently ignored (loop repeats the prompt).

---

## Conventions

| Term | Meaning |
|------|---------|
| Rett | Straight-through position (value 0) |
| Avvik | Diverging position (value 1) |
| Pens | Physical turnout / switch (Norwegian: veksel) |
| Dreieskive | Rotating platform (turntable) |
| eepromIdx | `k - FIRST_PENS` — EEPROM array offset for Pens letter `k` |
| pensIdx | `k - 'A'` — universal in-memory index for Pens letter `k` |
