# Plan: Extending LysKontroll to Control Penser (B–I)

## Overview

Extend the existing signal-lamp controller (Fossli–Vestli) to also drive 8 railroad
switches (Penser B–I) via two new I2C expansion boards, each carrying two MCP23017
16-bit I/O expanders.

---

## Hardware Summary

### New boards and MCP23017 assignments

#### SwitchMotors board

| Chip | I2C Address | Port B | Port A |
|------|-------------|--------|--------|
| U1 | 0x22 | OUTPUT: Motor drive M-B through M-I → H-bridge boards (×5) | OUTPUT: Second direction pin per motor (H-bridge needs 2 pins/motor) |
| U5 | 0x25 | OUTPUT: GPB0 = Dreieskive motor; GPB1–6 = Pens C feedback (2 pins) + Pens D feedback (2 pins) + Pens I feedback (2 pins) via J2 terminal | INPUT: GPA0–7 = end-position feedback (Rett + Avvik) for Penser E, F, G, H (8 pins, 2 per Pens) via J14 terminal |

Each Pens motor is controlled by **two adjacent MCP23017 output pins** (e.g. GPB0 + GPB1
for M-B). Only two bit patterns are legal:

| Bit pattern [pin_high, pin_low] | Effect |
|---------------------------------|--------|
| `01` | Motor runs / holds in direction A |
| `10` | Motor runs / holds in direction B |
| `00` | **Illegal** — no drive |
| `11` | **Illegal** — short circuit through H-bridge |

Using two pins this way eliminates the need for an external inverter component on the
board. The motor output must **always** be `01` or `10` — never `00` or `11`.  
**Exception: the Dreieskive motor** — see [Dreieskive motor control](#dreieskive-motor-control) below.

These are slow-running switch motors (Tortoise-type or similar) that stall safely
against the mechanical end-stop and hold position under continuous power. The output
pins stay set at `01` or `10` even after the Pens arrives; the motor simply stalls.
To reverse, flip the two bits.

8 Penser × 2 pins = 16 pins → one full MCP23017 (U1).
Note, we don't know which motor/pens is connected to which ports, except that both polarity pins for a motor is connected together in pairs of pins. Otherwise, the motor assignments are controlled through the configuration.

On U5, B0 and B1 connects to dreieskive motor.
Ports B2-B7 plus A0 to A7 connects to feedback switches from the penses.  Again, they are connected in pairs, so Rett and Avvik is connected to a pair of pins that are adjacent to each other.
Otherwise we don't know which pens switch feedback is connected to which port.  THis is again controlled by the configuration.

#### LedAndSwitches board

| Chip | I2C Address | Port B | Port A |
|------|-------------|--------|--------|
| U7 | 0x20 | INPUT: Physical operator panel switches 1–8 | OUTPUT: Indicator LEDs 1–8 (330Ω series resistors) |
| U2 | 0x21 | OUTPUT: Indicator LEDs 9–16 (330Ω series resistors) | INPUT: SW3 (momentary push Signal), SW6 (Dreieskive SPDT), SW9 (SPST panel switch 9) |

Per Pens:

- 2 indicator LEDs (Rett + Avvik) → 16 LEDs total across U7 Port A and U2 Port B
- 1 operator panel switch → 8 switches on U7 Port B
- 2 end-position feedback switches per Pens (Rett + Avvik, 3-level detection) → U5 (C, D, E–I) or Arduino direct pins (B)

All four MCP23017 chips share the same I2C bus (SDA/SCL).

---

## Pens Feedback — Where Each Pens Is Read

### Original DualSignal board (Arduino direct pins)

The original board had single-level detection only (one switch per Pens, two states: reached or
not). Penser B, C, D were wired directly to Arduino analog/digital pins, read as `SCA`–`SCD`
in the current code. One-pin detection means the code could only tell "arrived" vs "not arrived"
with no InBetween state.

### Updated arrangement (3-level detection everywhere)

| Pens | Feedback read via | Pins | Notes |
|------|------------------|------|-------|
| B | Arduino direct | 2 Arduino pins (see below) | 3-level; both Rett and Avvik end-stops on Uno |
| C | U5 (0x25) Port B | GPB3 + GPB4 via J2 terminal | Moved from Arduino; now 3-level |
| D | U5 (0x25) Port B | GPB5 + GPB6 via J2 terminal | Moved from Arduino; now 3-level |
| E | U5 (0x25) Port A | GPA0 + GPA1 via J14 | 3-level |
| F | U5 (0x25) Port A | GPA2 + GPA3 via J14 | 3-level |
| G | U5 (0x25) Port A | GPA4 + GPA5 via J14 | 3-level |
| H | U5 (0x25) Port A | GPA6 + GPA7 via J14 | 3-level |
| I | U5 (0x25) Port B | GPB1 + GPB2 via J14 | 3-level |

### Arduino pin cascade for Pens B

Moving C and D off the Arduino frees the two pins they previously occupied (SCC = D6 and
SCD = A3 in the current `Pins` struct). Those two pins are repurposed for Pens B's 3-level
detection:

| New role | Arduino pin | Former role |
|----------|-------------|-------------|
| Pens B — Rett end-stop | To be confirmed | Was SCC (D6) or SCD (A3) |
| Pens B — Avvik end-stop | To be confirmed | Was SCC (D6) or SCD (A3) |

> **Action required:** Confirm exact pin assignment for Pens B Rett and Avvik with the
> updated wiring diagram before coding. The freed pins are D6 and A3; one or both of the
> existing B-related pins (SCA D5, SCB A2) may also be involved or repurposed.

### Impact on existing signal lamp logic

The current `loop()` reads `SCC` (D6) and `SCD` (A3) as `sccClosed` and `scdClosed`,
and uses them in `ApplyNormalOutputs()` for signal B Green 2 (`bCanBeGreen && sccClosed && scdClosed`).
Similarly `SCB` (A2) as `scbClosed` gates all green outputs.

After this change:
- **`sccClosed` and `scdClosed`** can no longer be read from Arduino pins — they must be
  derived from Pens C and D state read via I2C from U5.
- A Pens counts as "closed" for signal logic when its state machine is in `IDLE_RETT` or
  `IDLE_AVVIK` (i.e. confirmed at an endpoint), **not** while `UNKNOWN`, `MOVING_*`, or `FAULT`.
- **`scbClosed`** (currently A2) must also be re-evaluated once the exact Pens-to-pin mapping
  for B is confirmed.

This means the signal lamp code (`ApplyNormalOutputs` / `ApplyNormalOutputsSlowly`) must be
updated to take its switch states from the Pens state machines rather than direct `digitalRead`
calls. The logical meaning of the switch states does not change — only the source of the data.

---

## Critical Pin Conflict — Must Fix Before Any New Code

The Arduino Uno uses **A4 = SDA** and **A5 = SCL** for I2C.  
Both pins are currently occupied:

| Pin | Current use | Problem |
|-----|-------------|---------|
| A4 | Debug button (`Pins::Debug`, `g_btnDbg`) | Blocks SDA |
| A5 | SerialEnable jumper (`Pins::SerialEnable`) | Blocks SCL |

When coding assume these are free.

### Resolution

- **Debug button → move to D13.**  
  D13 is listed in the README as unused (built-in LED is a bonus visual indicator).  
  Change: `static const uint8_t Debug = 13;`

- **SerialEnable on A5 → remove from code.**  
  `Pins::SerialEnable` is defined but never read in `setup()` or `loop()`.  
  Serial output is controlled solely by the compile-time `#define SERIAL_MONITOR_ENABLED`.  
  The pin and its pullup can simply be deleted.

- **A4 → SDA, A5 → SCL** are then free for I2C.

Hardware change required: re-wire the debug button from A4 to D13 on the existing board.

---

## Libraries Required

| Library | Source | Purpose | Flash impact |
|---------|--------|---------|-------------|
| `Wire.h` | Arduino built-in (no install needed) | I2C master communication to MCP23017 chips | ~1,800 bytes |
| `EEPROM.h` | Already included | State persistence | already counted |
| Custom MCP23017 driver | Write ~60-line lightweight wrapper | Avoids Adafruit MCP23017 + Adafruit_BusIO (~3 KB overhead); only three operations needed | ~600 bytes |

### Custom MCP23017 driver — three operations needed

```cpp
void  mcp_write_reg(uint8_t addr, uint8_t reg, uint8_t val);
uint8_t mcp_read_reg(uint8_t addr, uint8_t reg);
void  mcp_set_direction(uint8_t addr, uint8_t port, uint8_t dir_mask); // 0=output, 1=input
```

---

## Software Architecture

```
loop()
 ├── Read I2C inputs (polled every ~10 ms)
 │    ├── U7 Port B  → panel operator switch states (one per Pens)
 │    └── U5 Port A  → end-position feedback (Rett/Avvik reached)
 │
 ├── Debounce all new inputs
 │    └── DebouncedBit class  (same IIR leaky-integrator algorithm as existing
 │                             DebouncedActiveLow, but operates on a cached
 │                             register byte + bit mask instead of an Arduino pin)
 │
 ├── Pens state machine  (8 instances, one per Pens B–I)
 │    ├── IDLE
 │    ├── MOVING_TO_RETT   (motor runs until Rett end-switch fires)
 │    └── MOVING_TO_AVVIK  (motor runs until Avvik end-switch fires)
 │
 ├── Motor output writes → U1 (0x22) via I2C  (only on state change)
 │
 ├── LED output writes   → U7 Port A + U2 Port B via I2C  (reflect actual position)
 │
 └── Existing signal-lamp logic  (unchanged)
```

### New `DebouncedBit` class

Wraps the same leaky-integrator + Schmitt trigger as `DebouncedActiveLow`, but takes
`(uint8_t cachedRegister, uint8_t bitMask)` instead of a pin number.  
The cached register is refreshed by a timed I2C poll in the main loop.

---

## Memory Analysis

**ATmega328P (MEGA328P on GeekCreit Uno):**  
32,256 bytes flash · 2,048 bytes SRAM · 1,024 bytes EEPROM

### Flash (program memory)

| Component | Bytes (estimated) |
|-----------|-------------------|
| Current sketch (with serial debug) | ~11,000 |
| `Wire.h` library | ~1,800 |
| Custom MCP23017 driver | ~600 |
| `DebouncedBit` class (8 instances) | ~400 |
| Pens state machine (8 states, transitions, motor writes) | ~2,500 |
| LED update logic (map Pens state → LED bits per chip) | ~600 |
| EEPROM layout extension | ~200 |
| **Total estimated** | **~17,100** |
| **Remaining headroom** | **~15,100 bytes (47%)** |

Flash is not a concern.

### SRAM (runtime memory) — the tight resource

| Item | Bytes |
|------|-------|
| Arduino framework overhead + stack | ~300 |
| Serial ring buffers (TX + RX, 64 B each) | ~128 |
| String literals without F() macro (current code) | **~700–900** |
| Wire internal TWI buffer | ~32 |
| Existing `DebouncedActiveLow` × 7 | ~56 |
| New `DebouncedBit` × 16 | ~112 |
| MCP23017 register cache (4 chips × 2 ports) | ~8 |
| Pens state × 8 (commanded + actual + motor_state) | ~24 |
| I2C poll timer + misc | ~20 |
| **Estimated total without fix** | **~1,580–1,780** |
| **Remaining from 2,048** | **~270–470** |

### Critical SRAM fix: F() macro on all string literals

The current code has 30+ `Serial.print("literal string")` calls in `PrintFullState()`
alone, plus ~15 more in `loop()`. Without the `F()` macro, every quoted string is
copied into SRAM at startup — roughly **700–900 bytes** just for debug text.

**Converting all `Serial.print("...")` to `Serial.print(F("..."))` is required before
adding new code.** This moves the strings to flash (ample space there) and frees
~700–900 bytes of SRAM.

After the F() pass:
- Estimated SRAM used: **~800–1,000 bytes**
- Remaining headroom: **~1,000+ bytes** — comfortable margin

---

## EEPROM Layout

All writes use `EEPROM.update()` (writes only on value change, preserving write cycles).  
Magic byte at 0x01 distinguishes a configured chip from a blank/reset chip.

The ATmega328P EEPROM is rated for **100,000 write cycles**. Configuration data (motor
mappings, LED mappings, routing matrix) is written once during setup and rarely again,
so this is not a concern for those regions.

**Pens motor positions are not stored in EEPROM.** At startup the main loop reads the
feedback switches from U5 via I2C before taking any other action, giving the actual
current position directly. Caching positions in EEPROM would add frequent writes (every
move) and is unnecessary since the hardware always reflects truth.
The Pens state machine stores position in **RAM only** (`IDLE_RETT` / `IDLE_AVVIK` / etc.).

The Dreieskive has no feedback switches, so its last commanded position is stored in
EEPROM at 0x0A as a display hint only. One write per operator command — negligible wear.

### Region 0: Runtime state (existing)

| Address | Content | Size |
|---------|---------|------|
| 0x00 | Direction state (`g_direction`) | 1 byte |
| 0x01 | Config magic / version (e.g. `0xA5`) | 1 byte |
| 0x02–0x09 | Reserved (was: Pens positions — removed; positions come from I2C feedback) | 8 bytes |
| 0x0A | Dreieskive last commanded position (no feedback available) | 1 byte |
| 0x0B–0x0F | Reserved | 5 bytes |

### Region 1: Motor output mapping (Command 1 result)

Each motor is a **pin pair**: two adjacent MCP23017 output bits that are always driven
as `01` or `10`. One byte per Pens stores the lower pin of the pair; the upper pin is
always lower+1. Encoding: bits [6:5] = chip index (0–3), bit [4] = port (0=A, 1=B),
bits [3:0] = bit position of the lower pin.

| Address | Content | Size |
|---------|---------|------|
| 0x10–0x17 | Motor pin-pair base (lower pin encoding), Penser B–I | 8 bytes |
| 0x18–0x1F | Polarity: 0 = `01` means Rett, 1 = `10` means Rett, per Pens B–I | 8 bytes |

### Region 2: Pens feedback switch mapping (Command 1 result)

Two pins per Pens (Rett end-stop + Avvik end-stop). Each pin encoded as 1 byte:
bits [6:5] = chip index, bit [4] = port (0=A, 1=B), bits [3:0] = bit position.
Special value `0xFF` = not yet configured.

| Address | Content | Size |
|---------|---------|------|
| 0x20–0x27 | Rett end-stop pin encoding, Penser B–I | 8 bytes |
| 0x28–0x2F | Avvik end-stop pin encoding, Penser B–I | 8 bytes |
| 0x30–0x37 | Rett/Avvik polarity flags B–I (0=normal, 1=inverted) | 8 bytes |

### Region 3: Manual switch (operator panel) mapping (Command 2 result)

| Address | Content | Size |
|---------|---------|------|
| 0x38–0x3F | Manual switch pin encoding, Penser B–I (same 1-byte encoding as above) | 8 bytes |

### Region 4: LED mapping (Command 3 result)

| Address | Content | Size |
|---------|---------|------|
| 0x40–0x47 | Rett LED pin encoding, Penser B–I | 8 bytes |
| 0x48–0x4F | Avvik LED pin encoding, Penser B–I | 8 bytes |

### Region 5: Dreieskive and moment switch (Commands D and M result)

| Address | Content | Size |
|---------|---------|------|
| 0x50 | Dreieskive motor pin-pair base encoding | 1 byte |
| 0x51 | Dreieskive motor CW/CCW polarity (0 = `01` is CW, 1 = `10` is CW) | 1 byte |
| 0x52 | Dreieskive switch CW pin encoding | 1 byte |
| 0x53 | Dreieskive switch CCW pin encoding | 1 byte |
| 0x54 | Moment button pin encoding | 1 byte |
| 0x55–0x5F | Reserved | 11 bytes |

### Region 6: LED routing matrix (Command 4 result)

For each of the 16 Pens indicator LEDs (B_Rett, B_Avvik, C_Rett, … I_Rett, I_Avvik)
the routing matrix stores up to **four activation conditions**.  
An LED lights when **any** of its stored conditions is fully satisfied.

Each condition is two bitmasks:

| Mask | Meaning |
|------|---------|
| `rett_mask` | Bit N set → Pens B+N must be **Rett** for this condition to pass |
| `avvik_mask` | Bit N set → Pens B+N must be **Avvik** for this condition to pass |

Bit 0 = Pens B, bit 1 = Pens C, …, bit 7 = Pens I.  
A bit in neither mask means "don't care" for this condition.

**Per-LED layout (9 bytes per LED):**

| Byte | Content |
|------|---------|
| 0 | Count byte: `0xFF` = default 1-to-1 mapping (EEPROM uninitialized or LED not in JSON file); `0x00` = suppressed (LED never lights independently — used when the physical pin is shared with another logical LED); `0x01`–`0x04` = number of active routing conditions |
| 1–2 | Condition 0: rett\_mask, avvik\_mask (or 0xFF, 0xFF if unused) |
| 3–4 | Condition 1 (or 0xFF, 0xFF if unused) |
| 5–6 | Condition 2 (or 0xFF, 0xFF if unused) |
| 7–8 | Condition 3 (or 0xFF, 0xFF if unused) |

**LED index order:**

| Index | LED | | Index | LED |
|-------|-----|-|-------|-----|
| 0 | B Rett | | 8 | F Rett |
| 1 | B Avvik | | 9 | F Avvik |
| 2 | C Rett | | 10 | G Rett |
| 3 | C Avvik | | 11 | G Avvik |
| 4 | D Rett | | 12 | H Rett |
| 5 | D Avvik | | 13 | H Avvik |
| 6 | E Rett | | 14 | I Rett |
| 7 | E Avvik | | 15 | I Avvik |

| Address | Content | Size |
|---------|---------|------|
| 0x60–0xEF | LED routing matrix, 16 LEDs × 9 bytes | 144 bytes |

### Summary

| Region | Addresses | Bytes used |
|--------|-----------|-----------|
| Runtime state | 0x00–0x0F | 16 |
| Motor outputs | 0x10–0x1F | 16 |
| Feedback switches + polarity | 0x20–0x3F | 32 |
| Manual switches | 0x38–0x3F | 8 |
| LEDs | 0x40–0x4F | 16 |
| Dreieskive + moment switch | 0x50–0x5F | 16 |
| LED routing matrix | 0x60–0xEF | 144 |
| **Total** | | **~240 bytes of 1,024** |

---

## Configuration Mode

### Purpose

Configuration mode allows the physical wiring between motors, Pens feedback switches,
operator panel switches, and indicator LEDs to be discovered interactively, without
requiring 100% correct pre-planned wiring. The result is stored in EEPROM so normal
operation uses the discovered mapping without re-running config.

### Entering Configuration Mode

Send **`C`** on the serial interface at any time. The Arduino acknowledges with a
config banner and waits for a command.

No hardware button press is needed. Config mode requires a PC connected with the C#
companion program, so if `C` can be sent at all, the intent is deliberate. In normal
deployed operation no PC is connected, so accidental entry is not possible.

Type **`Q`** at any time to exit config mode and return to normal operation.

The C# companion program handles the user prompts and sends the single-character
commands; the Arduino only needs to receive them and print short response lines.

### Motor startup initialisation

Motor outputs must never be left at `00` or `11`. At every startup:

**Before config has been run** (EEPROM not yet valid):

- Set all motor pin pairs to `01` — an arbitrary safe state.
- The motors will run in one direction until they hit their end-stops and stall there.
  This is safe by design for this motor type.

**After config has been run** (EEPROM valid):

- Read each manual operator switch (via U7 Port B or direct Arduino pin for Pens B).
- Use the stored config mapping to determine which bit pattern (`01` or `10`) corresponds
  to the position the operator switch is commanding.
- Set each motor pin pair to the correct value immediately on startup.

---

### Command `1` — Discover motor ↔ Pens feedback switch linkage

**Goal:** Find out which motor output pin drives which Pens, and which two feedback
switch pins belong to each Pens.

**Procedure:**

All motor pin pairs are set to `01` at the start. The scan then iterates through every
motor pair in order, switching each one to `10` and watching what happens. Each motor
is handled as it is encountered — there is no separate pass for the Dreieskive.

**For each motor pair in turn:**

1. Switch the current pair to `10`. Poll all feedback switch inputs.

2. **If a feedback input changes** → this is a Pens motor. The motor stalls at its new
   end-stop. Report which feedback pins changed to the C# companion.
   - Operator confirms (`Y`) or skips (`N`).
   - On `N`: set motor back to `01`, move to the next pair.
   - On `Y`: store motor pin-pair → feedback pin linkage.
   - Ask: *"Is the Pens now at Rett or Avvik?"* (`R`/`A`). Store the polarity.
   - Mark this pair as assigned; move to the next pair.

3. **If the timeout expires with no feedback change** → this is the Dreieskive motor
   (rotates continuously, no end-stops).
   - Report: *"No feedback detected — this is the Dreieskive motor."*
   - Operator confirms (`Y`).
   - Ask: *"Is CW direction `01` or `10`?"* — operator jogs the turntable and answers.
   - Store the Dreieskive motor pin-pair and CW/CCW polarity.
   - Set this motor to `00` (stopped) and mark it as assigned; move to the next pair.

Repeat until all 9 motor pairs have been handled (8 Penser + 1 Dreieskive).

**Safety:**
- Motor runs until a feedback switch changes — not for a fixed time.
- A **timeout** (e.g. 30 seconds) stops the motor and reports an error if no switch
  changes and this is not the expected Dreieskive position — indicates a wiring fault.
- Emergency stop command **`X`** halts all motor outputs immediately at any point.

### Command `2` — Discover operator switch ↔ Pens linkage

**Goal:** Link each physical operator (manual) switch on the panel to the correct Pens
motor and determine Rett/Avvik orientation.

**Procedure (one Pens at a time):**

1. C# companion instructs the operator to move **all manual switches to their Rett position**.
2. Operator types the **Pens letter** (`B`–`I`) to begin that Pens.
3. C# companion says: *"Flip switch for Pens [X] to Avvik, then back to Rett."*
4. Arduino monitors all switch inputs and detects which input toggled twice.
   - Pens B: monitored on direct Arduino pins — straightforward.
   - Penser C–I: monitored on U7 Port B via I2C poll.
5. Arduino reports the detected port/pin; operator confirms with `Y` or skips with `N`.
6. Store the manual switch → Pens linkage.

**Linking the manual switch to the motor (Penser C–I):**

7. Arduino pulses motor outputs one at a time (starting where it left off; already-assigned
   motors are skipped). For each candidate motor it reports: *"Motor [n] moved — correct Pens? (Y/N)"*
8. Operator watches the layout and replies.
9. On `Y`: motor is linked to this Pens. Ask *"Is the Pens now at Rett or Avvik?"* (`R`/`A`)
   and store the polarity.
10. Move to next Pens letter when done.

### Command `3` — Discover LED ↔ Pens linkage

**Goal:** Map each indicator LED on the panel to the correct Pens and position (Rett/Avvik).

**Procedure (one Pens at a time):**

1. Operator types the **Pens letter** (`B`–`I`). The Pens is currently in Rett position
   (from Command 2).
2. Arduino lights **one LED** at a time (all others off).
3. Operator presses:
   - **`S`** — *Save*: this LED is the Rett indicator for this Pens.
   - **`N`** — *Next*: move to the next LED.
   - **`P`** — *Previous*: step back one LED.
4. Once Rett LED is stored, Arduino drives the motor to move the Pens to Avvik.
5. Repeat LED search for the Avvik indicator; store with `S`.
6. Move to the next Pens letter.

> **Note on shared Avvik LEDs:** Some Penser share an Avvik LED (where two tracks
> diverge to the same side track). This case is deferred to a later config pass and
> handled by allowing the same LED address to be stored for two Penser.

### Command `M` — Discover moment (momentary push) button

**Goal:** Find which input pin the momentary signal button is connected to.

**Procedure:**

1. Arduino monitors all switch inputs continuously.
2. C# companion prompts: *"Press the moment button now."*
3. Arduino detects which input pin transitions LOW then back HIGH (momentary press).
4. Reports the detected pin to the C# companion; operator confirms (`Y`) or retries (`N`).
5. Store the moment button pin encoding in EEPROM.

---

### Command `D` — Discover Dreieskive (turntable) manual switch

**Goal:** Map the 3-position Dreieskive operator switch (SW6, `SW_SPDT_MSM`) to its
two input pins, and identify which pin corresponds to CW and CCW.

The switch has three positions — middle (center-off), clockwise (CW), and
counter-clockwise (CCW) — and connects to two MCP23017 input pins. Both pins are
HIGH in the middle position (internal pull-ups active). Moving to CW or CCW pulls
one pin LOW.

**Procedure:**

1. C# companion: *"Move the Dreieskive switch to the middle (center) position, then press Enter."*
2. Operator confirms. Arduino reads both candidate pins — both should be HIGH.
3. C# companion: *"Move the switch to the clockwise (CW) position."*
4. Arduino detects which pin goes LOW. Reports and stores that as the CW pin.
5. C# companion: *"Move the switch back to middle."* Operator confirms.
6. C# companion: *"Move the switch to the counter-clockwise (CCW) position."*
7. Arduino detects which pin goes LOW. Reports and stores that as the CCW pin.
8. C# companion: *"Move the switch back to middle."* Operator confirms.
9. Store both pin encodings and the CW/CCW polarity in EEPROM.

> **Note:** `D` within config mode is unambiguous — the top-level `D` (debug) is only
> available outside config mode.

### Dreieskive motor control

The Dreieskive (turntable) uses the same two-pin H-bridge output as the Pens motors,
but it has **three valid output states** because the turntable must be able to stop:

| Panel switch position | Motor output | Effect |
|-----------------------|-------------|--------|
| Middle (center-off) | `00` | Motor stopped — no current |
| CW | `01` or `10` (per stored CW polarity) | Turntable rotates clockwise |
| CCW | the opposite pattern | Turntable rotates counter-clockwise |

`00` is valid here because the Dreieskive motor does not need to hold position: it has
no mechanical end-stops and the turntable stays where it is when power is removed.
This is the only motor in the system where `00` is a legal runtime output.
`11` remains illegal for all motors (H-bridge shoot-through).

**Runtime loop behaviour:**
- Each iteration reads the Dreieskive switch pins (via U2 Port A).
- Both pins HIGH → middle → output `00` (stop).
- CW pin LOW → output the stored CW pattern.
- CCW pin LOW → output the stored CCW pattern.
- Both pins LOW simultaneously → treated as a fault; output `00` (safe stop).

The last commanded direction is stored in EEPROM (Region 0, address 0x0A) as a display
hint only — `0x00` = CW, `0x01` = CCW, `0xFF` = middle/unknown. This is written on
each direction change (low write rate; one per operator command).

---

### Command `R` — Reset (erase) all configuration from EEPROM

Sends a single `R` character in config mode. Arduino clears all Regions 1–6 (motor,
switch, LED, routing matrix) and resets the config magic byte. Runtime state (Region 0)
is unaffected. The operator must re-run Commands 1–4 after a reset.

---

### Command `4` — Upload / download LED routing matrix

**Goal:** Store and retrieve the per-LED conditional lighting logic in EEPROM.  
This replaces the simple 1-to-1 (switch position = LED) mapping with routing-aware
conditions — LEDs only light when a complete route through the layout is set.

**Why it is needed:**

In a typical station panel, a switch indicator LED should only light when the switch is
*reachable* via the current switch configuration. Example: a Rett LED on switch G should
stay dark if switch F is set in a way that no train from block 21 can actually reach G.
The LED lights only when the entire chain of prerequisite switch positions is satisfied.

**The routing matrix model:**

Each of the 16 Pens indicator LEDs has a list of **activation conditions** (up to 4).
An LED lights when **any** condition is fully satisfied. Each condition is a pair of
bitmasks — one for required Rett states and one for required Avvik states — covering
all 8 Penser (B–I).

*Example for Fossli station — G_Rett LED:*

| Condition | Meaning |
|-----------|---------|
| Rett={G,H,F} Avvik={E} | G is on the route from block 21 to siding 3 (straight through H, G, F then E diverges) |
| Rett={G,H} Avvik={F} | G is on the route from siding 2 back toward block 21 (F diverges to reach G) |

If neither condition is met, G_Rett stays dark even if G is physically in Rett position.

**Serial protocol (text-based hex lines):**

Arduino sub-commands within Config `4` mode:

| Command | Action |
|---------|--------|
| `U` | Upload: C# sends 16 LED condition lines then `END`; Arduino stores in EEPROM |
| `D` | Download: Arduino sends 16 LED condition lines then `END`; C# reads and saves |
| `Q` | Exit routing matrix sub-mode, return to Config menu |

**Line format** (one per LED, in index order 0–15):

```
N RR AA [RR AA [RR AA [RR AA]]]
```
Where `N` = condition count (0–4), `RR` = rett\_mask hex byte, `AA` = avvik\_mask hex byte.  
Example: `2 2F 08 60 10` = 2 conditions: [rett=0x2F avvik=0x08] and [rett=0x60 avvik=0x10]  
A line with `N=0` means simple 1-to-1 mapping: `0`

Arduino replies `OK` after each line during upload, and `STORED` after `END`.  
Arduino sends `END` after the last line on download.

**Site-specific configuration file (JSON):**

The C# program reads a site-specific JSON file and converts it to the binary protocol:

```json
{
  "site": "Fossli",
  "routing": {
    "G_Rett": [
      { "rett": ["G", "H", "F"], "avvik": ["E"] },
      { "rett": ["G", "H"],      "avvik": ["F"] }
    ],
    "H_Rett": [
      { "rett": ["G", "H", "F"], "avvik": ["E"] },
      { "rett": ["H"],           "avvik": ["F"] }
    ],
    "F_Rett": [
      { "rett": ["G", "H", "F"], "avvik": ["E"] }
    ],
    "F_Avvik": [
      { "rett": ["G", "H"], "avvik": ["F"] }
    ]
  }
}
```

LEDs absent from the `routing` map keep the default (count 0, simple 1-to-1 mapping).  
Maximum 4 conditions per LED. The C# program stays **site-agnostic** — it reads whatever
JSON file the operator provides, converts to bitmasks, and uploads. A different site
supplies a different JSON file; the program code is unchanged.

**Arduino runtime evaluation:**

In the normal run loop, after all Pens state machines update:

```
for each Pens indicator LED (B_Rett, B_Avvik, ... I_Rett, I_Avvik):
    count = EEPROM.read(region6_base + led_index * 9)
    if count == 0:
        light = (pens_state == expected_position_for_this_led)
    else:
        light = false
        for i in range(count):
            offset = region6_base + led_index * 9 + 1 + i * 2
            rett_mask  = EEPROM.read(offset)
            avvik_mask = EEPROM.read(offset + 1)
            if (current_rett_bits & rett_mask) == rett_mask
            AND (current_avvik_bits & avvik_mask) == avvik_mask:
                light = true
                break
    write LED output
```

`current_rett_bits` and `current_avvik_bits` are 8-bit registers built once per loop
from the stable (debounced) states of all 8 Penser.  
Re-evaluating from EEPROM every loop is acceptable: EEPROM.read() takes ~3.3 µs,
16 LEDs × up to 4 conditions × 2 reads = at most 128 EEPROM reads ≈ 420 µs — negligible
against a 10 ms loop period. (Cache the matrix in RAM if this proves too slow in practice.)

---

### Configuration State Machine

```
NORMAL_RUN
  ├─ serial receives 'V' ──► VERIFY_MODE
  │    ├─ '1': for each configured Pens — move to opposite, verify feedback, move back, verify
  │    │        Dreieskive skipped (no feedback switches). Reports PASS/FAIL per Pens.
  │    └─ 'Q' ──► NORMAL_RUN
  ├─ serial receives 'D' ──► DEBUG_MODE
  │    ├─ 'A': toggle all 22 LEDs on/off
  │    ├─ 'L': loop all 22 LEDs one at a time (1 s each); any key stops
  │    └─ 'Q' ──► NORMAL_RUN
  └─ serial receives 'C' ──► CONFIG_IDLE  (prints banner, waits for command)
       ├─ '1' ──► CMD1_MOTOR_SCAN
       │    └─ single scan: each pair → feedback change = Pens (confirm + polarity) / timeout = Dreieskive (CW/CCW)
       │         └─ done ──► CONFIG_IDLE
       ├─ '2' ──► CMD2_SWITCH_SCAN
       │    └─ 'B'–'I': detect manual switch → find motor → confirm polarity → store
       │         └─ done ──► CONFIG_IDLE
       ├─ '3' ──► CMD3_LED_SCAN
       │    └─ 'B'–'I': cycle LEDs ('N'/'P'/'S') for Rett then Avvik → store
       │         └─ done ──► CONFIG_IDLE
       ├─ 'M' ──► CMD_MOMENT_SWITCH
       │    └─ wait for momentary press → detect pin → confirm → store
       │         └─ done ──► CONFIG_IDLE
       ├─ 'D' ──► CMD_DREIESKIVE_SWITCH
       │    └─ middle confirm → CW detect → middle → CCW detect → store pins + polarity
       │         └─ done ──► CONFIG_IDLE
       ├─ 'R' ──► CMD_RESET_CONFIG
       │    └─ clear EEPROM Regions 1–6 → CONFIG_IDLE
       ├─ '4' ──► CMD4_ROUTING_MATRIX
       │    ├─ 'U': receive 16 LED condition hex lines + END → store Region 6 in EEPROM
       │    ├─ 'D': send 16 LED condition hex lines + END from Region 6
       │    └─ 'Q' ──► CONFIG_IDLE
       └─ 'Q' ──► NORMAL_RUN
```

### Startup LED snake animation

Immediately after hardware initialisation and before entering the main loop, the Arduino
runs a brief LED snake animation across all 16 Pens indicator LEDs. This provides a
visible "boot complete" indicator and a quick visual check that all panel LEDs are
functional.

**Parameters:** 0.5 s per LED, 10 seconds total (20 steps = 1.25 passes through the 16-LED
sequence, wrapping back to the start after I_Avvik).

**Sequence (B_Rett → I_Avvik, repeating):**
B_Rett → B_Avvik → C_Rett → C_Avvik → D_Rett → D_Avvik →
E_Rett → E_Avvik → F_Rett → F_Avvik → G_Rett → G_Avvik →
H_Rett → H_Avvik → I_Rett → I_Avvik → (wrap) → B_Rett → ...

At each step, only the current LED is lit; all others are off.

### `snakeLeds` — shared function

Both the startup animation and the Debug `L` command use the **same function**:

```cpp
void snakeLeds(uint16_t delayMs, uint8_t steps);
// steps = 0  → run indefinitely until a serial byte is received
// steps > 0  → run exactly that many LED steps then return
```

| Caller | delayMs | steps |
|--------|---------|-------|
| Startup | 500 | 20 (10 s total) |
| Debug `L` | 1000 | 0 (until key received) |

The function iterates through the 16-LED index table (B_Rett=0 … I_Avvik=15) using
`index % 16`. Each step lights one LED via the stored LED-pin mapping, waits `delayMs`
milliseconds (using `millis()` — non-blocking inside the delay window so serial can be
checked), then turns that LED off and advances the index.

The LED order is fixed and matches the physical board layout: B_Rett, B_Avvik, C_Rett,
C_Avvik, …, I_Rett, I_Avvik — producing a snake effect that sweeps across the operator
panel from Pens B to Pens I.

---

### Debug Mode — `D`

Sending **`D`** enters debug mode. Normal signal and Pens logic is suspended.  
The existing hardware debug button (D13) continues to work as before for the 6 signal
lamp LEDs only; the serial debug mode extends coverage to all 16 **Pens indicator LEDs**
(via U7 and U2 on I2C).

#### Sub-commands in debug mode

| Command | Action |
|---------|--------|
| `A` | Toggle all 16 Pens indicator LEDs: all on → all off. Repeat each time `A` is sent. Use to check for wiring shorts (all should light without blown fuses) and that no LED is missing. |
| `L` | Snake mode: calls `snakeLeds(1000, 0)` — lights one LED at a time for 1 second, advancing B_Rett → B_Avvik → … → I_Rett → I_Avvik → (wrap). Each LED lights alone. Send any key to stop. |
| `Q` | Exit debug mode, return to normal operation. |

> This mode can be extended later (e.g. drive specific LEDs by number, test PWM fade)
> without changing the C# program structure — just add new sub-command letters.

---

### C# Companion Program

A minimal command-line (console) application on the PC:

- Opens the Arduino's COM port at 115200 baud.
- Reads lines printed by the Arduino and displays them.
- Sends single-character commands based on keystrokes or simple menu choices.
- Manages the high-level prompting logic (e.g. *"Move all switches to Rett, then press Enter"*)
  so the Arduino only needs to handle low-level detection and storage.
- No GUI required — a plain `Console.ReadKey()` / `Console.WriteLine()` loop is sufficient.

#### Top-level menu (C# side)

```
D  — Debug mode     (LED all-on test, LED loop test)
C  — Config mode    (motor discovery, switch mapping, LED mapping)
V  — Verify mode    (test motors and Pens switches against stored config)
Q  — Quit
```

Communication is one-directional in terms of intelligence: Arduino detects and stores;
C# program guides the human operator.

#### Top-level menu additions for routing matrix

```
D  — Debug mode
C  — Config mode
     ...
     4  — LED routing matrix: upload from JSON file or download to JSON file
V  — Verify mode
Q  — Quit
```

The routing matrix sub-session (`4` inside Config):

```
L  — Load JSON file from disk and upload routing matrix to Arduino EEPROM
S  — Download routing matrix from Arduino and save to JSON file
Q  — Return to Config menu
```

The C# program converts between the human-readable JSON (Pens letter names, Rett/Avvik
strings) and the compact binary bitmask format on the wire. The JSON file is edited
offline with a text editor for each site; the C# program itself needs no code changes
between sites.

---

## Pens Feedback Switch Encoding

Each Pens has **two feedback input pins** on the MCP23017 (U5 Port A, two pins per
Pens). The MCP23017 internal pull-ups hold both pins HIGH when no switch is active.
The two switches are active-low: the Rett end-stop pulls one pin LOW when the Pens
reaches the Rett position; the Avvik end-stop pulls the other pin LOW when it reaches
Avvik.

Reading the two bits as `[AvvikPin, RettPin]`:

| Bit pattern | Meaning | Note |
|-------------|---------|------|
| `10` | **Rett** | Rett end-stop closed (RettPin LOW), Avvik end-stop open |
| `11` | **InBetween** | Motor still moving, neither end-stop reached |
| `01` | **Avvik** | Avvik end-stop closed (AvvikPin LOW), Rett end-stop open |
| `00` | **Fault** | Both pins pulled LOW — indicates a short circuit or wiring failure |

### Implications for software

- Both pins for a given Pens must be read and evaluated together as a 2-bit value.
- Debouncing is applied per pin independently (using `DebouncedBit`), then the two
  stable values are combined to determine the 3-state position.
- The `00` (Fault) state must be detected and reported; the motor must be stopped
  immediately and the Pens state machine should enter a FAULT state that blocks
  further motor commands until the fault is cleared.
- The state machine only considers a Pens "arrived" when the correct bit pattern has
  been stable for the debounce period — not on the raw reading.

### Pens state machine states (revised)

| State | Description |
|-------|-------------|
| `IDLE_RETT` | At Rett position, confirmed by stable `10` feedback |
| `IDLE_AVVIK` | At Avvik position, confirmed by stable `01` feedback |
| `MOVING_TO_RETT` | Motor running; waiting for stable `10` feedback |
| `MOVING_TO_AVVIK` | Motor running; waiting for stable `01` feedback |
| `UNKNOWN` | Startup state, feedback is `11` (in between) |
| `FAULT` | Feedback reads `00`; motor stopped, operator alert via LED |

---

## Recommended Implementation Order

| Step | Task | Notes |
|------|------|-------|
| 1 | Shorten or conditionally compile out serial debug strings | Frees SRAM during dev; `#define SERIAL_MONITOR_ENABLED` already wraps all serial code |
| 2 | Move Debug pin A4 → D13; remove `SerialEnable` | Unblocks I2C; requires one hardware wire change |
| 3 | Add `Wire.h` + custom MCP23017 driver; verify I2C comms | Read IOCON register from all 4 chips to confirm addresses |
| 4 | Add `DebouncedBit` class | Same algorithm as existing debounce |
| 5 | Read-only first: poll U7 Port B + U5 Port A; print to serial | Verify hardware wiring before writing outputs |
| 6 | Add Pens state machine + motor drive output to U1 | |
| 7 | Add LED output to U7 Port A and U2 Port B | |
| 8 | Expand EEPROM layout for config regions 1–6 | Pens positions are RAM-only; no EEPROM save needed |
| 9 | Implement config mode entry (`C` on serial → config banner) | |
| 10 | Implement Command `1`: motor scan + feedback discovery | Emergency stop (`X`) required before enabling motor pulses |
| 11 | Implement Command `2`: manual switch + motor linkage | |
| 12 | Implement Command `3`: LED mapping (`N`/`P`/`S`) | |
| 13 | Implement Command `4`: routing matrix upload/download | Text-based hex-line protocol; evaluate from EEPROM in normal loop |
| 14 | Write C# companion console app | Simple serial port open + ReadKey loop |
| 15 | Add C# Config `4` sub-session: JSON file → bitmasks → upload; download → JSON save | `System.Text.Json` built into .NET 8 |
| 16 | End-to-end config run on real hardware | Run all commands; verify EEPROM contents via serial dump |

---

## Notes

- I2C is polled, not interrupt-driven. The MCP23017 INTA/INTB pins are wired with
  10K pull-ups on both boards but no free Arduino interrupt pin is available without
  reassigning Train (D2) or PBA (D3). Polling every 10 ms is sufficient for human-
  operated switches and slow switch motors.
- The user says "I2S interface using SDA and SCK" — this is I2C (Inter-Integrated
  Circuit). MCP23017 pin 12 = SCL, pin 13 = SDA. "SCK" in the schematic label refers
  to SCL.
- Penser C, D, E–I end-position feedback is all read via U5 (I2C). Pens B retains
  two direct Arduino pins (the former SCC=D6 and SCD=A3, freed by moving C and D to U5).
  The original DualSignal board had only single-level detection; all Penser now have
  full 3-level detection (Rett / InBetween / Avvik / Fault).
