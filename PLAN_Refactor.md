# Refactoring Plan: Move Config Logic from Arduino to C#

## Problem with Current Architecture

All config logic (scan loops, prompts, EEPROM layout knowledge, decision making) lives
in the Arduino simulator. When the real firmware is written, every piece of that logic
must be re-implemented from scratch — twice — and kept in sync. Every bug fix or
behaviour change must be applied in two places.

---

## Target Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  C# (DrammenMJKConfig)                                           │
│                                                                  │
│  ConfigSession   — all config logic, all prompts, all loops      │
│  EEPROM layout   — C# knows all region addresses                 │
│  Motor scan      — fires pairs, reads responses, asks operator   │
│  Switch mapping  — drives Pens, detects switch flip              │
│  LED mapping     — steps LEDs, operator identifies               │
│  Routing matrix  — upload/download (unchanged)                   │
└────────────────────┬─────────────────────────────────────────────┘
                     │  Serial 115200 — low-level command protocol
                     ▼
┌──────────────────────────────────────────────────────────────────┐
│  Arduino (Simulator OR Real Firmware — same interface)           │
│                                                                  │
│  LOW-LEVEL COMMAND LIBRARY (thin layer only)                     │
│    PING / SI / ER / EW / EC                                      │
│    MF / MS / FW / SW / SCA / LD / LA                            │
│                                                                  │
│  HARDWARE LAYER                                                  │
│    Simulator: simulated Pens positions, switches, LEDs           │
│    Real firmware: MCP23017 I2C, actual pins                      │
└──────────────────────────────────────────────────────────────────┘
```

The C# program is **identical** whether talking to the simulator or real hardware.
The Arduino side only changes in the hardware layer — the command interface is the same.

---

## Low-Level Command Protocol

All commands are sent as ASCII lines (`\n` terminated). Responses are plain lines
(no `!` prefix — all go through capture mode). Status/debug lines from the device
keep the `!` prefix and are displayed immediately as before.

### Commands

| Command | Parameters | Response | Meaning |
|---------|-----------|----------|---------|
| `PING` | — | `PONG` | Connectivity check |
| `SI` | — | `SITE B I 16 9` | Site info: FIRST_PENS, LAST_PENS, NUM_LED_OUTPUTS, NUM_PAIRS |
| `ER` | `<hex_addr>` | `<hex_byte>` | EEPROM read |
| `EW` | `<hex_addr> <hex_byte>` | `OK` | EEPROM write |
| `EC` | — | `OK` | EEPROM erase (fill 0xFF from 0x10 onward) |
| `MF` | `<pair> <pol>` | `OK` | Motor fire: activate pair in polarity 0=`01` or 1=`10` |
| `MS` | — | `OK` | Motor stop: set all pairs to `00` |
| `FW` | `<pair> <timeout_ms>` | `RETT <pin>` \| `AVVIK <pin>` \| `TIMEOUT` | Wait for feedback on pair |
| `SW` | `<pin>` | `0` \| `1` | Read single switch/feedback pin |
| `SCA` | `<timeout_ms>` | `CHANGED <pin>` \| `TIMEOUT` | Scan all switch pins, report first to change |
| `LD` | `<idx> <0\|1>` | `OK` | Set physical LED at index on/off |
| `LA` | — | `OK` | All LEDs off |
| `RST` | — | *(resets device)* | Watchdog reset |

All hex values are two uppercase hex digits (e.g. `10`, `FF`, `A5`).

### Error responses
Any command that cannot execute returns `ERR <reason>` instead of its normal response.

---

## What Stays on the Arduino

Only two things:

### 1. Low-Level Command Library
A `dispatch(line)` function parses the incoming command and calls the appropriate
hardware function. Thin — no loops, no prompts, no config logic.

```
loop()
  read serial line
  dispatch(line)
    "PING" → println("PONG")
    "ER 10" → println(EEPROM.read(0x10), HEX)
    "MF 2 0" → fire motor pair 2 polarity 0, println("OK")
    "FW 2 2000" → wait up to 2000ms for feedback on pair 2, println result
    ...
```

### 2. Hardware Layer
- **Simulator**: arrays simulating Pens positions, switch states, LED states.
  When `MF` fires a pair, the simulator moves the internal Pens position and
  activates the corresponding feedback pin so `FW` can return `RETT`/`AVVIK`.
- **Real firmware**: MCP23017 I2C drivers, actual pin reads, actual motor output.

The hardware layer is entirely behind the command interface — C# never knows or cares
which one it is talking to.

---

## What Moves to C#

Everything that is currently in `cmdMotorScan()`, `cmdSwitchMapping()`,
`cmdLedMapping()` in Simulator.ino moves to C#:

- EEPROM region addresses (constants in C#)
- Site parameters (read once via `SI` command on connect)
- All scan loops and state tracking (`assigned[]`, `pairDone[]`, `usedLed[]`)
- All operator prompts and key handling
- All `buildRemaining()` / `buildRemainingLeds()` string building
- All "show remaining Pens" logic
- Motor scan: fires pairs, calls `FW`, interprets `RETT`/`AVVIK`/`TIMEOUT`
- Switch mapping: calls `MF` to drive Rett, calls `SCA` to detect flip
- LED mapping: calls `LD` to step through LEDs, operator presses S/N/P in C#

`Relay()` mode is **removed** for config commands. C# drives everything directly
via capture mode. The operator interacts only with C#; C# issues atomic low-level
commands to the device.

---

## Site Parameters

On connect, C# sends `SI` and receives: `SITE <first_pens> <last_pens> <num_led_outputs> <num_pairs>`

C# stores these and uses them for all logic — no hardcoded B–I or 16 in C#.
The `#define FIRST_PENS`, `LAST_PENS`, `NUM_LED_OUTPUTS` remain on the Arduino
side (where they belong), exposed only through `SI`.

---

## EEPROM Layout (owned by C#)

C# holds all region base addresses as constants. The Arduino only provides
raw `ER`/`EW` access.

```csharp
const int Region1Base  = 0x10;  // motor pair index
const int Region1Pol   = 0x30;  // motor polarity
const int Region2Rett  = 0x50;  // feedback Rett pin
const int Region2Avvik = 0x70;  // feedback Avvik pin
const int Region3Base  = 0x90;  // manual switch pin
const int Region4Rett  = 0xB0;  // Rett LED index
const int Region4Avvik = 0xD0;  // Avvik LED index
const int Region5MotorPin = 0xF0;
const int Region5MotorPol = 0xF1;
const int Region5SwCw     = 0xF2;
const int Region5SwCcw    = 0xF3;
const int Region5Moment   = 0xF4;
const int Region6Base  = 0x100; // routing matrix
const int LedNoPinSentinel = 0xFE;
```

---

## Migration Path

### Phase 1 — Define and implement the low-level protocol
1. Write the command dispatcher for the simulator (thin `loop()` replacement).
2. Write `ArduinoDevice` class in C# wrapping `ArduinoConnection` with typed methods:
   `Ping()`, `GetSiteInfo()`, `EepromRead(addr)`, `EepromWrite(addr, val)`,
   `MotorFire(pair, pol)`, `MotorStop()`, `WaitFeedback(pair, timeoutMs)`,
   `ReadSwitch(pin)`, `ScanSwitches(timeoutMs)`, `SetLed(idx, on)`, `AllLedsOff()`.
3. Verify with a simple test: ping, read EEPROM, fire motor, read feedback.

### Phase 2 — Port config commands to C#
Port one command at a time, using the new `ArduinoDevice` methods.
Order: `MotorScan` → `SwitchMapping` → `LedMapping` → `MomentSwitch` → `DreieskiveSwitch`.
After each port, remove the corresponding handler from `Simulator.ino`.

### Phase 3 — Clean up simulator
After all commands are ported, the simulator contains only:
- Hardware simulation arrays and state
- The command dispatcher (Phase 1)
- `setup()` / `loop()`

At this point the simulator and real firmware share the same command interface.
Writing real firmware = implement the hardware layer behind the same dispatcher.

### Phase 4 — Real firmware
Implement `MF`, `MS`, `FW`, `SW`, `SCA`, `LD`, `LA` against actual MCP23017 I2C.
EEPROM commands (`ER`, `EW`, `EC`) are identical to the simulator.

**C# config logic is written once and tested fully against the simulator.**
Moving to real firmware means implementing the command interface in real firmware.
C# changes at this stage should be limited to:

- **Timeouts** — real motors are slower than simulated delays; `FW` and `SCA`
  timeout values will likely need tuning.
- **Error handling** — real hardware can fail in ways the simulator never does:
  I2C bus errors, motors that stall, feedback that never fires. C# will need to
  handle `ERR` responses gracefully where the simulator always returns `OK`.
- **Inter-command timing** — small delays may be needed between `MF` and `FW`
  that the simulator does not require.
- **Command set gaps** — something may turn out to be impossible with the current
  command set once real hardware is in front of you. If so, add the missing command
  to the dispatcher on both sides and update C# — but this is a protocol gap, not
  a logic change.

The config logic itself (scan loops, EEPROM layout, operator prompts, remaining-Pens
tracking) should not need to change.

---

## What Does NOT Change

- `ArduinoConnection` (serial port, read loop, `!` prefix handling, capture mode)
- `Program.cs` main menu
- Routing matrix upload/download (already structured protocol, already in C#)
- EEPROM layout and region addresses
- `SI` / `RST` / `PING` simple commands
- Key bindings visible to the operator (same UX, just driven from C#)
