# Plan: Phase 1 Implementation (FSCB over direct I2C)

Implements the Phase 1 scope defined in `PlanExtended.md`. This document is the
concrete build plan: code layering, protocol, EEPROM layout and task order.

## Relationship to other docs

- `PlanExtended.md` is the source of truth for scope and hardware facts. This
  file only turns it into an implementation plan.
- `PlanExtended.md` describes a **new, bigger architecture** (SVB + SCB boards,
  numbered switches, 4 I2C buses via a mux) that supersedes the older
  letter-based Penser (A–Z) extension of the original DualSignal lamp
  controller described in `SPECS.md` / `PLAN_PensControl.md` / `PLAN_Refactor.md`.
- `Firmware_FirstAttempt/Firmware.ino` is an implementation of that **older**
  Pens-letter system (`MF`/`MS`/`FW`/`SW`/`SCA`, EEPROM Region1–6, Pens A–Z).
  Per `PlanExtended.md`: "we will not reuse this". It's still useful as a
  reference for MCP23017 driver code style and pin-encoding tricks, but the
  command protocol and EEPROM layout below are new and unrelated to Region1–6.
- `Simulator/Simulator.ino` is the copy source for `Firmware/`. We keep its
  **transport plumbing** (line reader, capture-mode-safe response format,
  `!`-prefixed status lines) and its **EEPROM primitive commands**
  (`PING`/`SI`/`ER`/`EW`/`EC`/`RST`), and discard the simulated-Pens business
  logic entirely.

## Guiding architectural decision (revised)

`PLAN_Refactor.md`'s "config logic lives in C#" rule applies to
**config-time, interactive** logic — scan loops, operator prompts, "which one
moved?" bookkeeping. It does **not** mean the PC is the runtime brain: in
production there is no PC attached. The Arduino (CB) must be able to power up
and run the layout entirely on its own, driven by:

- its own downloaded EEPROM config (built once, during config sessions), and
- hardware events on the buses — an SVB's panel switch changing state, later
  (Phase 2+) triggering the corresponding SCB motor, all inside the firmware.

So the split is:

| Lives in C# (`DrammenMJKConfig`) | Lives in firmware (`Firmware.ino`) |
|---|---|
| Editing `hardware.json` (board types × site board inventory) and `SystemConfig.json` | Applying the uploaded board hardware table (IODIR/GPPU per board) at boot and live on upload |
| Interactive discovery UX: motor scan prompts, "which switch moved?", Rett/Avvik confirmation | The switch table itself (slot → board + motor bit + polarity + feedback bits), read at runtime |
| Human-friendly naming: SVB name/number ↔ switch number ↔ switch slot | Resolving "drive switch N" / "read switch N" from that table, without needing a PC |
| Building/uploading both tables during config | — |
| — | (Later) reacting autonomously to SVB switch-change events, same table, same resolver |
| — | (Later) signal-lamp PWM/dimming logic, native firmware, not decomposed to C# — see [Signal lamp logic](#signal-lamp-logic-out-of-scope) |

Practically, this means the wire protocol has two tiers:

- **Raw discovery primitives** (`MDIR`/`MPU`/`MW`/`MR`/`MPOLL`/`MBIT`) — take
  an explicit `(vaddr, port, bit)` because during Motor Scan that mapping is
  exactly what's *unknown* and being discovered. These are config-time-only.
- **Switch-level commands** (`SW`/`SR`, new) — take only a switch **slot**
  number. No port, no bit, no vaddr. The firmware looks those up itself from
  the switch table already written into EEPROM by Motor Scan. This is what
  Command mode uses, and it's the same lookup the future autonomous runtime
  loop will use — so Command mode is effectively "manually trigger the same
  resolver the SVB-event loop will call later," not a separate code path.

---

## Virtual addressing

`PlanExtended.md`'s "System description" section gives the site's full address
map and states the formula as "Bus number * 20 + local address". The worked
table underneath it is the authoritative data, and it's consistent with a
multiplier of **`0x10`**, not `0x20`:

| Bus | Boards | Real address(es) | Virtual address(es) |
|---|---|---|---|
| 0 — Stillverk | SVB1 LedAndSwitchesFossli, SVB2 LedAndSwitchesHavna | `0x20`,`0x21`,`0x22`,`0x23` | same (`0x20`–`0x23`) |
| 1 — Havna/Vallekilen | Havna SCB, Vallekilen SCB, Vallekilen SVB | `0x20`,`0x21`,`0x22` | `0x30`,`0x31`,`0x32`,`0x33` |
| 2 — Fossli | Fossli Høyre (FSCB), Fossli Venstre | `0x20`,`0x21` | `0x40`,`0x41` |
| 3 — Sidespor | Sidespor | `0x20` | `0x50` |

(Vallekilen is two virtual addresses, `0x32` SCB + `0x33` SVB — not a single
combined board as an earlier draft of `PlanExtended.md` implied.)

Each real address diffs from its virtual address by exactly `bus * 0x10`
(Bus1: `+0x10`, Bus2: `+0x20`, Bus3: `+0x30`). So:

```
virtual_address = bus_number * 0x10 + real_i2c_address        // real_i2c_address is always 0x20–0x27 (MCP23017)

// decode (inverse), given real addresses are always in 0x20–0x2F:
bus_number      = (virtual_address >> 4) - 2
real_i2c_address = 0x20 | (virtual_address & 0x0F)
```

This holds for every entry in the table above, including Bus 0 where the
"virtual == real" statement falls out of the formula for free (`bus=0` adds
nothing). It also holds for FSCB itself: Phase 1's direct-wired address
`0x20` **is already a valid virtual address** for `bus=0` — so Phase 1 doesn't
need a special case, it's just the `bus=0` instance of the same scheme, and
the value doesn't change when FSCB is later moved to Bus 2 (`0x40`) — only the
`BoardConfig` entry's virtual address constant changes.

Bus handling lives in the firmware's I2C layer, and only there: the raw
discovery commands and the switch table both carry `vaddr`, never `bus`. The
resolver decodes `bus`/`real_addr` from `vaddr` and selects the mux channel
before issuing the transaction. Dispatch code never mentions `bus`.

**No board-discovery/scan command exists or is wanted.** Every board's
virtual address is a known, fixed constant from the site map above — it's
data in `BoardConfig`, not something firmware or the PC probes for. A board
that doesn't answer on its known address means its I2C link is broken or it
isn't wired in yet, not "not found".

---

## Firmware layering (`Firmware/Firmware.ino`)

```
┌──────────────────────────────────────────────────────────────────┐
│ 1. Serial transport (PSL)                                        │
│    readLine(), dispatch(), OK/ERR/status(!) conventions          │
│    — copied from Simulator.ino, simulation stripped              │
├──────────────────────────────────────────────────────────────────┤
│ 2. Command dispatch                                               │
│    PING / SI / ER / EW / EC / RST        (local, unchanged)       │
│    MDIR / MPU / MW / MR / MPOLL / MBIT   (raw, discovery-only)    │
│    SW / SR                                (switch-table, operational)│
│    HWU / HWD                              (board hw table, bulk) │
│    SCU / SCD                    (switch/SVB-switch/dreieskive/LED, bulk)│
├──────────────────────────────────────────────────────────────────┤
│ 3a. Board hardware table (EEPROM-backed — see EEPROM layout)      │
│    resolve_board_hw(vaddr) -> {iodirA, iodirB, gppuA, gppuB}      │
│    or NOT_CONFIGURED. Applied to every registered vaddr at boot,  │
│    and immediately (live) whenever HWU stores a new table.        │
│ 3b. Switch table (EEPROM-backed, generic schema)                  │
│    resolve_switch(slot) -> {motorVAddr, motorBit, polarity,       │
│      feedbackVAddr, fbRettBit, fbAvvikBit}  or NOT_CONFIGURED     │
│    motor/feedback vaddr independent — a 2-chip board's feedback   │
│    can land on the other chip from its motor.                     │
│    Used by SW/SR now; will be used by the autonomous SVB-event    │
│    loop later (Phase 2+) — same function, no protocol change.     │
├──────────────────────────────────────────────────────────────────┤
│ 4. Virtual-address resolver (bus lives here, and only there)      │
│    resolve_vaddr(vaddr) -> { bus, real_addr }                     │
│      bus       = (vaddr >> 4) - 2                                  │
│      real_addr = 0x20 | (vaddr & 0x0F)                             │
│    i2c_select_bus(bus)                                             │
│    Phase 1: bus is always 0 -> select is a no-op                  │
│              (no mux fitted; single direct bus)                    │
│    Phase 2+: drives the I2C mux channel select                      │
├──────────────────────────────────────────────────────────────────┤
│ 5. Generic MCP23017 driver                                        │
│    mcp_write_reg / mcp_read_reg / mcp_set_dir / mcp_set_pu        │
│    Same 3 primitives as PLAN_Refactor.md's "custom driver"        │
│    Reused unchanged by every future board type.                    │
└──────────────────────────────────────────────────────────────────┘
```

There is now **no compiled-in board-specific code at all**, not even the SCB
port template — layers 3a/3b are both generic table interpreters over
EEPROM data. That's an improvement on the previous draft, which still
hardcoded FSCB's port setup directly in firmware C++ (recompile+reflash
needed for any port-role change). Now it's a small uploaded table, exactly
the same shape/spirit as the switch table.

### `hardware.json` and the board hardware table

Corrected from an earlier whole-port draft: MCP23017's `IODIR`/`GPPU`
registers are genuinely per-bit, and `PlanExtended.md`'s "Hardware
configuration" section confirms boards actually mix input and output *within*
one port — an SVB's LED-output port can have one bit repurposed as a motor
output on the Vallekilen combined board, for instance. A whole-port
`{direction, pullup}` can't express that, so each port is an **8-element
array**, index 0 = bit 0 .. index 7 = bit 7, each element one of:

- `"out"` — output
- `"in"` — input, no pull-up
- `"in-pu"` — input, internal pull-up enabled

No shared type templates — an earlier draft had a `boardTypes` indirection
("SCB" boards all inherit one port layout), which turns out not to fit this
system: `PlanExtended.md`'s site map has 5 SCBs and 3 SVBs, and each one has
*its own* input/output configuration — there's no common template to actually
share, so the indirection was pure overhead with nothing behind it. Every
board just declares its own ports directly.

`boards` is a plain **array**, one entry per physical board, matching
`PlanExtended.md`'s site map. Each board has a `category` (`SCB` / `SVB` /
`Combined`, per the doc's "one combined stillverk and switch control board")
and a `chips` array — **1 or 2 MCP23017 entries**, each with its own `vaddr`
and its own two 8-element port arrays (index 0 = bit 0 .. index 7 = bit 7):

- `"out"` — output
- `"in"` — input, no pull-up
- `"in-pu"` — input, internal pull-up enabled

```json
{
  "boards": [
    {
      "name": "Fossli Høyre (FCSBR)",
      "category": "SCB",
      "chips": [
        {
          "vaddr": "0x20",
          "portA": ["in-pu", "in-pu", "in-pu", "in-pu", "in-pu", "in-pu", "in-pu", "in-pu"],
          "portB": ["out",   "out",   "out",   "out",   "out",   "out",   "out",   "out"]
        }
      ]
    }
  ]
}
```

(Renamed from FSCB to `FCSBR` — `PlanExtended.md` now distinguishes "Fossli
Switch Control Right Side Board" from a left-side counterpart; see below.)

Port A bits 0–7 all `in-pu` (the four feedback pairs), Port B bits 0–7 all
`out` (0–3 motors, 4–5 dreieskive, 6 spare, 7 status LED) — matches FCSBR's
spec exactly, and happens to be uniform per port for this one board. A
2-chip board (e.g. SVB1 `LedAndSwitchesFossli` at `0x20` **and** `0x21`) is
just a `chips` array with two entries instead of one — same board, two
independent port configurations, since each MCP23017 is wired however it's
wired:

```json
{
  "name": "LedAndSwitchesFossli",
  "category": "SVB",
  "chips": [
    { "vaddr": "0x20", "portA": [ /* … */ ], "portB": [ /* … */ ] },
    { "vaddr": "0x21", "portA": [ /* … */ ], "portB": [ /* … */ ] }
  ]
}
```

**Fourth enum value, `"tbd"`**: exists for exactly the situation `PlanExtended.md`'s
newly added FCSBL board (Fossli left side, `0x21`) was in briefly — its I/O
*counts* were known (3 switch motors, 3 signal LEDs, 1 status LED, 6 feedback
bits, 1 track-detect bit — 14 of 16 bits, so it fits one chip) before its
exact bit *positions* were. `"tbd"` joins `"out"`/`"in"`/`"in-pu"` as a fourth
legal per-bit value meaning "not yet determined", and `HardwareConfigUpload`
must refuse to include any chip with a `"tbd"` entry in an `HWU` upload — so
a placeholder file can never reach real hardware while it's still incomplete.
FCSBL's own layout is fully known now (its "Port B config Left"/"Port A
config Left" spec), so its actual `hardware.json` entry doesn't use `"tbd"`
any more — but the mechanism stays, for the next board that starts out
partially specified:

```json
{
  "name": "Fossli Venstre (FCSBL)",
  "category": "SCB",
  "chips": [
    {
      "vaddr": "0x21",
      "portA": ["in-pu", "in-pu", "in-pu", "in-pu", "in-pu", "in-pu", "in-pu", "in-pu"],
      "portB": ["out",   "out",   "out",   "out",   "out",   "out",   "out",   "out"]
    }
  ]
}
```

Converted from `PlanExtended.md`'s literal wording, which mixes two
numbering bases across the two ports — Port B given 1-based (pins 1–8), Port
A given 0-based (pins 0–5, 7) — self-disambiguating since a 0-based range
can't contain "8" and a 1-based range can't contain "0". Port B pin *N* → bit
*N−1*: bits 0–2 = the 3 real switch motors, bits 4–6 = the 3 signal LEDs
(Red/Green1/Green2, order still unknown), bit 7 = the connectivity warning
LED. Port A pins used as-is: bits 0–5 = 3 feedback pairs (0-1/2-3/4-5,
matching FCSBR's pairing convention), bit 7 = track detection.

Of the 16 bits, 15 are actually connected to something and 1 (Port A bit 6)
is not connected at all. Of those 15, 14 are the functionally meaningful
ones just listed; the 15th (Port B bit 3) **is** wired — to the motor driver
output stage, same as bits 0–2 — but has no switch attached to it, so it's
connected yet functionally idle. Both spares still get a direction in
`hardware.json` (`"out"` for bit 3, since it's genuinely wired as a motor
output; `"in-pu"` for Port A bit 6, to avoid leaving a truly floating input)
even though neither has a `SystemConfig.json` entry.

Direction is now fully resolved for both boards, and turns out identical —
Port A all `in-pu`, Port B all `out` — even though `PlanExtended.md` warned
pin *assignments* would differ per board ("commonality: all SCBs will have
similar setup, but the pin assignments will differ"). That statement held:
which specific bit does what differs completely between FCSBR and FCSBL: only
the port-level direction pattern happens to coincide.

Actual file: `DrammenMJKConfig/hardware.json` — also carries a top-level
`"_todo"` array (see [System configuration persistence](#system-configuration-persistence-systemconfigjson)
for the same convention applied to `SystemConfig.json`) recording what's
still open: the numbering-base conversion above hasn't been independently
verified against a schematic, and how FCSBL's signal-lamp dimming is actually
achieved given MCP23017 GPIO has no native PWM.

`category` isn't used by firmware at all (it never sees `hardware.json`, only
the flat resolved register table) — it's for C#, so logic like "which boards
are eligible targets for Motor Scan" (`SCB` and `Combined`, never plain
`SVB`) or "which board carries this SVB's operator panel + indicator LEDs"
doesn't have to guess from port layout.

The mixed-port exception `PlanExtended.md`'s "Switch drivers" section already
flags for plain SCBs ("in some cases we have to use parts of Port A also for
output") needs nothing special either — it's just a chip whose `portA` array
has an `"out"` mixed in among the `"in-pu"` entries. Nothing about the schema
distinguishes "the common case" from "the exception" anymore, since there
turned out not to be a common case.

C# flattens every board's every chip into one resolved row
(`vaddr, iodirA, iodirB, gppuA, gppuB` — plain register bytes, one bit per
array element: `"out"` → IODIR bit `0`, `"in"`/`"in-pu"` → IODIR bit `1`,
`"in-pu"` → GPPU bit `1` else `0`) before uploading via `HWU` — one line per
chip, regardless of which board it belongs to or how many chips that board
has. Firmware never parses JSON, never sees `category`, `name`, or the
board/chip nesting; it only ever sees the flat resolved table.

Not scanned or discovered — unlike the switch table, this is **declared**,
straight from known site facts (per [Virtual addressing](#virtual-addressing),
no board-discovery happens anywhere in this system). Uploading it is a
prerequisite for Motor Scan, not a product of it: Motor Scan assumes Port B is
already output and Port A is already input+pullup on the board it's scanning.

**What this file deliberately does *not* say**: `PlanExtended.md`'s new
section also lists *purposes* per bit (motors, on-board LED, dimmed track
signal LEDs, switch feedback, toggle/moment switches, track detection for
SCBs; position LEDs, an occasional motor, signal indication, on/off and
3-position toggles, moment button for SVBs). None of that belongs in
`hardware.json` — direction/pull-up is a fixed electrical fact of the board
design, but *which specific bit drives which specific switch/LED/signal* is
a per-site wiring fact, discovered by Motor Scan (or its future LED/switch/
signal counterparts) and stored in `SystemConfig.json`. `hardware.json`
answers "is this pin an input or output"; `SystemConfig.json` answers "what
is this specific pin actually connected to here". Keeping purpose out of
`hardware.json` is what keeps the boot-time bring-up routine fully generic —
it only ever needs direction and pull-up, never purpose.

### Boot-time hardware bring-up (data-driven — replaces the earlier "C# does it" design)

Turning off FSCB's "no comms" red LED (GPB7) was previously sketched as a C#
startup step. That's wrong for the same reason as everything else here: it
must work with no PC attached. It's firmware's `setup()`, driven entirely by
the board hardware table uploaded via `HWU`:

```
for each configured entry in the board hardware table (BoardVAddr[i] != 0xFF):
    MDIR(BoardVAddr[i], PORT_A, BoardIodirA[i])
    MDIR(BoardVAddr[i], PORT_B, BoardIodirB[i])
    MPU (BoardVAddr[i], PORT_A, BoardGppuA[i])
    MPU (BoardVAddr[i], PORT_B, BoardGppuB[i])
    MW  (BoardVAddr[i], PORT_A, 0x00)   // universal safe default: LEDs/inputs
    MW  (BoardVAddr[i], PORT_B, 0x00)   // off, motors arbitrary-but-valid direction
                                         // (safe for FSCB's one-pin-per-motor design —
                                         // see PLAN_PensControl.md for why a two-pin
                                         // H-bridge board would need a different default)
```

The same routine runs immediately after `HWU` finishes storing (so a freshly
uploaded table takes effect without a reboot), and once at boot. On a
completely fresh Arduino (board hardware table never uploaded), boot does
nothing to any board — ports stay at the MCP23017's own power-on default
(everything an input) until `hardware.json` is uploaded once. That's a safer
default than the earlier hardcoded-template version, which assumed FSCB's
specific setup unconditionally.

### Firmware protocol

All addresses/values as 2-digit uppercase hex, same convention as the
existing `ER`/`EW` commands. `port` is `A` or `B`. `vaddr` is the virtual
address byte (see [Virtual addressing](#virtual-addressing)). `slot` is a
switch-table index (see [EEPROM layout](#eeprom-layout)).

**Raw discovery primitives** — used only by Motor Scan, where the mapping is
still unknown, and for bench debugging:

| Command | Params | Response | Meaning |
|---|---|---|---|
| `MDIR` | `<vaddr> <port> <mask>` | `OK` / `ERR` | Set IODIR (bit=1 input, 0 output) |
| `MPU`  | `<vaddr> <port> <mask>` | `OK` / `ERR` | Set GPPU pull-ups |
| `MW`   | `<vaddr> <port> <val>`  | `OK` / `ERR` | Write OLAT (drive outputs) |
| `MR`   | `<vaddr> <port>`        | `<hex byte>` / `ERR` | Read GPIO register |
| `MPOLL`| `<vaddr> <port> <baseline> <timeoutMs>` | `CHANGED <hex>` / `TIMEOUT` | Block locally, polling every ~5 ms, until the port value differs from `baseline` or the timeout elapses |
| `MBIT` | `<vaddr> <port> <bit> <val>` | `OK` / `ERR` | Read-modify-write a single OLAT bit (used during scan to fire one motor without disturbing others already found) |

**Switch-table commands** — used by Command mode, and later by the autonomous
runtime loop internally (no wire protocol involved at that point, just the
same internal `resolve_switch()` call):

| Command | Params | Response | Meaning |
|---|---|---|---|
| `SW` | `<slot> <R\|A> <timeoutMs>` | `OK <RETT\|AVVIK\|BETWEEN>` / `TIMEOUT` / `ERR unconfigured` | Look up `slot`, drive its motor bit to the polarity-adjusted level for the requested position, then block polling its feedback pair (like `MPOLL`, internally) until it settles or times out |
| `SR` | `<slot>` | `RETT` / `AVVIK` / `BETWEEN` / `FAULT` / `ERR unconfigured` | Look up `slot`, read its feedback pair, decode via stored polarity |

**Board hardware bulk transfer** — `hardware.json` upload/download (see
[hardware.json and the board hardware table](#hardwarejson-and-the-board-hardware-table)).
Uploading also (re)applies the table live, not just at next boot:

| Command | Params | Response | Meaning |
|---|---|---|---|
| `HWU` | — | `READY`, then per line `OK`/`ERR`, then `STORED` after `END`; then immediately applies every entry (as at boot) | Upload: C# sends one line per board, `END` to finish |
| `HWD` | — | one line per **configured** board, then `END` | Download: dump every board with `BoardVAddr != 0xFF` |

Line format: `<vaddr> <iodirAHex> <iodirBHex> <gppuAHex> <gppuBHex>`.

**System-config bulk transfer** — `SystemConfig.json` backup/restore (see
[System configuration persistence](#system-configuration-persistence-systemconfigjson)),
structured line protocol identical in shape to the existing
`RMU`/`RMD` routing-matrix commands:

| Command | Params | Response | Meaning |
|---|---|---|---|
| `SCU` | — | `READY`, then per line `OK`/`ERR`, then `STORED` after `END` | Upload: C# sends the switch-table, SVB-switch-table, dreieskive, status-LED and fade-config lines, `END` to finish |
| `SCD` | — | all five record shapes, then `END` | Download: dump everything configured, across all five tables |

Each line starts with a one-letter tag so all five record shapes share one
stream:

| Tag | Format | Meaning |
|---|---|---|
| `S` | `S <slot> <motorVAddrHex> <motorBitHex> <polarityHex> <feedbackVAddrHex> <fbRettHex> <fbAvvikHex>` | One switch-table slot |
| `P` | `P <slot> <vaddrHex> <bitPrimaryHex> <bitSecondaryHex> <targetIsDreieskiveHex> <targetSlotHex>` | One SVB-switch-table slot (`bitSecondaryHex = FF` for a plain on/off switch; `targetSlotHex` ignored when `targetIsDreieskiveHex = 1`) |
| `D` | `D <motorVAddrHex> <pinBaseHex> <cwPolarityHex>` | The dreieskive **motor** facts — always known for FSCB in Phase 1 |
| `L` | `L <vaddrHex> <bitHex>` | The status-LED fixed facts |
| `F` | `F <fadeMsHex4> <fadeStepsHex> <pwmPeriodUsHex4>` | Fade config — the only 16-bit fields on this wire (`fadeMsHex4`/`pwmPeriodUsHex4` are 4 hex digits, not 2, since `1000` doesn't fit one byte) |

`signals`/`trackDetection` (in `SystemConfig.json` already, see below) don't
have EEPROM regions or wire-line tags yet — a gap, not a deliberate
omission, since neither has functional firmware use yet (FCSBL isn't built).
Worth closing before FCSBL work actually starts, not before.

Same style as the routing matrix's `N RR AA ...` lines — hex bytes,
space-separated.

`SW`/`SR` never mention port or bit — that's the whole point. C# only ever
needs to know a slot number (which it gets from its own SVB-name/switch-number
→ slot lookup in `BoardConfig`, see below) and `R`/`A`.

---

## EEPROM layout (Phase 1, new — unrelated to old Region1–6)

Two flat, board-agnostic tables, both parallel byte arrays, both applying the
same "cheap headroom now beats a schema-breaking resize later" reasoning —
plus a handful of singleton facts that don't need a table at all.

### Fixed board facts (dreieskive, status LED)

Not part of the switch table — these aren't discovered per-slot data, they're
declared, board-specific facts straight from `PlanExtended.md`'s FSCB spec
(Port B bits 4–5 = dreieskive, bit 7 = status LED). Still EEPROM-backed like
everything else "the system needs to run standalone" — just two bytes each,
no array needed since each board has at most one of each:

| Address | Content | Size |
|---|---|---|
| `0x02` | `DreieskiveVAddr` — `0xFF` = not configured | 1 |
| `0x03` | `DreieskiveMotorPinBase` — lower of the 2-bit pair (4, per FSCB); upper is always base+1 | 1 |
| `0x04` | `DreieskiveCwPolarity` — `0xFF` = not yet determined; `0`/`1` once set | 1 |
| `0x05` | `StatusLedVAddr` | 1 |
| `0x06` | `StatusLedBit` | 1 |

This is the **motor** side of the dreieskive only (FSCB, bits 4–5 — a fixed
board fact, known now). `DreieskiveCwPolarity` stays `0xFF` through Phase 1:
resolving it needs the operator's physical control switch to already be
mapped, which is the SVB switch table below — not something Phase 1 can
determine on its own with no SVB present.

`DreieskiveVAddr`/`DreieskiveMotorPinBase` are declared, not scanned —
written from `BoardConfig`'s known constants (FSCB's fixed `(4,5)`) at the
start of a Motor Scan session, no operator interaction needed.

### Fade config

Also singleton facts, but a different kind from the ones above: not board
wiring at all, just the fade loop's timing parameters (see
[Signal lamp logic](#signal-lamp-logic-out-of-scope)) — fixed, identical for
every fade that ever runs, ported from the original
`SignalLys_2X_Momentbrytere.ino`'s `static const` values but now genuinely
editable config rather than compiled-in constants:

| Address | Content | Size |
|---|---|---|
| `0x07`–`0x08` | `FadeMs` (uint16) — total fade duration; `1000` ported from the original | 2 |
| `0x09` | `FadeSteps` (uint8) — linear step count; `60` ported from the original | 1 |
| `0x0A`–`0x0B` | `PwmPeriodUs` (uint16) — software-PWM carrier period; `1000` ported from the original | 2 |

Read once by the fade loop at the start of each fade (not re-read per step —
these don't change during a fade, only between config uploads). Populated
via `SystemConfigSession`/`SCU`, same as everything else in
`SystemConfig.json` — not something Motor Scan or any discovery flow
touches, since there's nothing to discover: the values are simply declared,
and stay at the ported defaults until someone deliberately retunes the
dimming.

### SVB switch table

A genuinely different concept from the switch table above, so it gets its
own table rather than being folded into switch or dreieskive records: the
switch table describes **SCB** wiring (a motor + its feedback, discovered by
Motor Scan). This table describes **SVB** wiring — the operator's physical
panel switches, each one assigned to control exactly one target (almost
always a specific switch's Rett/Avvik position; the dreieskive's 3-position
toggle is the one exception). Different board, different direction
(input, not output), different discovery process (Phase 2's Command 2/D
equivalent, not Motor Scan) — mixing it into the switch table would conflate
two unrelated wiring facts just because they both eventually relate to "the
same switch."

Indexed by `svbSwitchSlot` (32 capacity, same "cheap headroom" reasoning as
the switch table).

| Address | Content | Size |
|---|---|---|
| `0x120`–`0x13F` | `SvbSwVAddr[32]` — SVB chip this operator switch lives on; `0xFF` = unconfigured | 32 |
| `0x140`–`0x15F` | `SvbSwBitPrimary[32]` — bit 0–7 | 32 |
| `0x160`–`0x17F` | `SvbSwBitSecondary[32]` — bit 0–7, or `0xFF` = single-bit switch (the common case — a plain on/off panel switch has only one bit) | 32 |
| `0x180`–`0x19F` | `SvbSwTargetIsDreieskive[32]` — `1` = this entry is the dreieskive's 3-position toggle (`BitPrimary`=CW, `BitSecondary`=CCW), ignore `TargetSlot`; `0` = a normal switch | 32 |
| `0x1A0`–`0x1BF` | `SvbSwTargetSlot[32]` — which switch-table `slot` this panel switch drives, when `TargetIsDreieskive = 0` | 32 |

An entry is "configured" iff `SvbSwVAddr[slot] != 0xFF`. Every field here
stays unconfigured through all of Phase 1 — populating it needs an actual
SVB board and a discovery flow (Phase 2's equivalent of the old system's
"Command 2 — Manual switch → Pens mapping" / "Command D — Dreieskive switch",
per `PlanExtended.md`'s `DrammenMJKConfig` menu list), neither of which
exists yet. The table is built now purely so wiring up the SVB later is a
data change, not a schema change — see
[Explicitly deferred](#explicitly-deferred-not-phase-1).

### Board hardware table

Indexed by board slot (**16** capacity — plenty; the whole eventual system
per `PlanExtended.md`'s site map has ~8 boards total). Populated by `HWU`
from `hardware.json`.

| Address | Content | Size |
|---|---|---|
| `0xB0`–`0xBF` | `BoardVAddr[16]` — `0xFF` = unconfigured slot | 16 |
| `0xC0`–`0xCF` | `BoardIodirA[16]` | 16 |
| `0xD0`–`0xDF` | `BoardIodirB[16]` | 16 |
| `0xE0`–`0xEF` | `BoardGppuA[16]` | 16 |
| `0xF0`–`0xFF` | `BoardGppuB[16]` | 16 |

### Switch table

Indexed by switch `slot`. Sized for **32 slots** now (only 4 populated in
Phase 1) rather than exactly 4 — at 6 bytes/slot this costs 192 bytes out of
1024, cheap enough to avoid a schema-breaking resize the moment a second
board's switches get configured in Phase 2. Populated by `SCU`/Motor Scan.

| Address | Content | Size |
|---|---|---|
| `0x01` | Magic byte (`0xA5`) | 1 |
| `0x10`–`0x2F` | `SlotMotorVAddr[32]` — board this slot's motor lives on; `0xFF` = unconfigured | 32 |
| `0x30`–`0x4F` | `SlotMotorBit[32]` — bit 0–7 on that board's Port B | 32 |
| `0x50`–`0x6F` | `SlotPolarity[32]` — 0/1: which level = Rett | 32 |
| `0x70`–`0x8F` | `SlotFeedbackRettBit[32]` — bit 0–7 on Port A | 32 |
| `0x90`–`0xAF` | `SlotFeedbackAvvikBit[32]` — bit 0–7 on Port A | 32 |
| `0x100`–`0x11F` | `SlotFeedbackVAddr[32]` — board this slot's **feedback** lives on; independent of `SlotMotorVAddr` | 32 |

(EEPROM is 1024 bytes total — `0x120` is nowhere near the ceiling; this new
region just goes after the board hardware table at `0xB0`–`0xFF` rather than
disturbing any existing address.)

A slot is "configured" iff `SlotMotorVAddr[slot] != 0xFF`. **Motor and
feedback vaddr are independent, not assumed equal** — on a board with 2
MCP23017 chips, a switch's feedback pair can land on the *other* chip from
its motor bit, so both need their own board reference. (For FSCB specifically
— one chip — they're always the same value in practice, but the schema
doesn't get to assume that generally.) Feedback is still assumed to live on
Port A of whichever board `SlotFeedbackVAddr` names (matches `PlanExtended.md`'s
stated SCB convention); if some future board type breaks *that* assumption
too, the schema gains a feedback-port byte then — not speculatively added now.

Switch **numbers/labels** (`1`, `3`, `5/6`, `7`, and eventually SVB
name/number) are **not** stored in EEPROM at all — they're purely a
`DrammenMJKConfig`-side (`BoardConfig`) concern for operator display and for
mapping "which slot does the operator mean" during Motor Scan / Command mode.
Firmware only ever deals in slot numbers.

Dreieskive stays a fixed compile-time constant (Port B bits 4–5), not part of
the switch table, not scanned, not yet reachable from `SW`/`SR` — matches "we
should not try to find dreieskive, as that is preassigned now". Command mode
for the dreieskive (3-state, not Rett/Avvik) is future scope.

---

## DrammenMJKConfig layering (C#)

```
ArduinoConnection        — unchanged: serial I/O, ReadLoop, capture mode
ArduinoDevice             — raw discovery wrappers: McpSetDirection/
                             McpSetPullup/McpWritePort/McpReadPort/
                             McpPollChange/McpSetBit (used by MotorScan only)
                             + switch-table wrappers: DriveSwitch(slot, pos,
                             timeoutMs) / ReadSwitch(slot)  (thin wrappers
                             over SW/SR; used by CommandSession)
                             + bulk transfer: SystemConfigUpload/Download
                             (thin wrappers over SCU/SCD, same capture-mode
                             shape as RoutingMatrixUploadStart/SendLine/Finish)
                             + bulk transfer: HardwareConfigUpload/Download
                             (thin wrappers over HWU/HWD, same shape again)
HardwareConfig (new)      — `boards[]` (each with 1–2 `chips[]`) model read
                             from `hardware.json`; flattens every chip into a
                             `(vaddr, iodirA, iodirB, gppuA, gppuB)` row sent
                             over `HWU`. Phase 1: one board (FSCB), one chip.
                             Not discovered — declared straight from the
                             known site board list.
BoardConfig (new)         — SVB/switch-number ↔ slot mapping + display labels.
                             Phase 1: one hardcoded SVB entry ("Fossli",
                             SVB 1) owning FSCB's 4 switches, slots 0–3,
                             labels ["1","3","5/6","7"].
                             This is also site configuration, not hardware —
                             it's the site topology (which SVBs, which SCBs,
                             which switch numbers) from PlanExtended.md's
                             "System description", just hardcoded for now.
                             Phase 2+: belongs in SystemConfig.json alongside
                             the switch-wiring data, not a separate file —
                             both are per-site facts discovered/declared once
                             and downloaded, without touching ConfigSession,
                             CommandSession, or ArduinoDevice.
ConfigSession              — Command 1 rewritten against BoardConfig + the
                             raw Mcp* primitives (still needed — this is the
                             discovery step that populates the switch table);
                             + new `H` item wrapping HardwareConfigSession
                             (upload/download hardware.json) — run before `1`,
                             since Motor Scan assumes ports are already set up;
                             + new `J` item wrapping SystemConfigSession
                             (upload/download SystemConfig.json).
HardwareConfigSession (new) — load hardware.json, expand via HardwareConfig,
                             upload via HWU (which also applies it live); or
                             HWD → save hardware.json.
SystemConfigSession (new)  — mirrors RoutingMatrixSession: load SystemConfig.json
                             → SCU, or SCD → save SystemConfig.json. Also
                             called automatically from MotorScan after each
                             slot is stored. Deliberately *not* called
                             "hardware" — it holds discovered, per-site,
                             changeable facts (which motor drives which
                             switch, which feedback pins belong to it), not
                             fixed per-board-type hardware facts. See
                             [System configuration persistence](#system-configuration-persistence-systemconfigjson)
                             for the hardware-vs-configuration distinction
                             this naming is built on.
CommandSession (new)       — new top-level mode (sibling of DebugSession /
                             VerifySession); operates already-configured
                             switches by SVB-name + switch-number label,
                             resolved to a slot, via DriveSwitch/ReadSwitch —
                             never touches Mcp*/port/bit directly.
```

### `BoardConfig` — Phase 1 hardcoded values

An SVB owns a **list** of SCB virtual addresses, not a single one — Phase 1
only populates one entry, but Fossli will have two (Høyre + Venstre) as soon
as Phase 2 starts, and the scan algorithm below is written against the list
from day one so that addition doesn't change the loop shape, only the data.

```csharp
Svb = "Fossli" (SVB 1),
Scbs = [
    {
        Name = "Fossli Høyre (FSCB)",
        VirtualAddress = 0x20,          // bus 0, real addr 0x20 — becomes 0x40 once
                                         // FSCB is rewired to Bus 2 (see Virtual addressing)
        MotorBitRange = 0..3,           // motor bits scanned on this SCB's Port B
        Switches = [
            (Label: "1"),
            (Label: "3"),
            (Label: "5/6"),
            (Label: "7"),
        ],
        DreieskivePins = (4, 5),        // pre-assigned, not scanned, not in the switch table
        StatusLedPin = (Port.B, 7),     // informational only — firmware clears it at boot now
    },
    // Phase 2: a second entry here for Fossli Venstre (VirtualAddress = 0x41, its own
    // Switches list) is all that's needed to extend the scan/command flow to it.
],
```

Slot numbers themselves are assigned sequentially across `Scbs` in order (SCB
0's switches get slots 0..N-1, the next SCB continues from N, etc.) — the flat
slot index isn't stored in `BoardConfig`, it falls out of iteration order when
`MotorScan` writes the table.

Note the FSCB motor drive is **one pin per switch**, not the two-pin H-bridge
pair used in `PLAN_PensControl.md`'s SwitchMotors board — the 74HC14 inverter
on FSCB produces the second H-bridge line in hardware. Confirmed by
`PlanExtended.md`: "Pin 0-3 controls the switch motors. High and Low indicate
the two directions" (single pin, not a pair) and "74HC14 IC... irrelevant for
the communication".

`ArduinoDevice` gets a matching helper so config entries can be written
readably instead of as raw hex: `static byte VirtualAddress(int bus, byte realAddr) => (byte)(bus * 0x10 + realAddr)`.
E.g. the future Fossli Høyre entry would read `VirtualAddress(bus: 2, realAddr: 0x20)` → `0x40`.

### Command 1 — Motor scan (Phase 1 scope)

Per `PlanExtended.md`: only find/map the 4 switch motors + their feedback
pairs and Rett/Avvik polarity. **Do not scan for the dreieskive** — its pins
are fixed (`DreieskivePins`), and it's outside the scan loop entirely (see
below — this also means Phase 1 doesn't need the old system's "timeout =
Dreieskive" branch at all). "Not scanned" doesn't mean "not configured",
though: at the start of a Motor Scan session, before the switch loop runs,
C# writes the dreieskive and status-LED facts (`D`/`L` lines, straight from
`BoardConfig`'s known constants) so `SystemConfig.json`/EEPROM fully describe
the board from the first scan onward, not just its discovered switches. Also
determine + store open/closed (Rett/Avvik) positions by commanding the motor
and asking the operator, and let the operator assign the real-world switch
number (`1`, `3`, `5/6`, `7`) to each discovered motor pin.

#### Discovery algorithm

One fact fixes the shape of this: **which motor bit maps to which feedback
pair is exactly what's unknown** going in — the pairs themselves (`0-1`,
`2-3`, `4-5`, `6-7`) are fixed by wiring convention, but board assembly
doesn't guarantee motor bit N drives the pair starting at bit N.

What's *not* assumed (corrected from an earlier draft): that a motor's
feedback pair lives on the same chip as the motor. On a board with 2
MCP23017 chips, feedback can land on either chip — so in general the scan
needs to watch Port A on **every chip belonging to the board being scanned**,
not just the motor's own chip, and record whichever one actually changed.

Outer loop over the SVB's boards (1 entry in Phase 1: FSCB, 1 chip), for each
board inner-looping over its chips' motor bit ranges (`0..3` on FSCB's one
chip in Phase 1 — dreieskive's `4..5` is never touched by this loop):

```
for each board in Svb.Boards:
    chips = board.Chips                                          // 1 or 2 vaddrs

    for each chip in chips:
        for each motorBit in chip.MotorBitRange:
            if motorBit already assigned -> skip

            baseline[c] = MR(c, PORT_A)  for each c in chips       // baseline on EVERY chip of the board
            MBIT(chip.vaddr, PORT_B, motorBit, 0)                  // fire this motor only, polarity 0
            (fbVaddr, result) = poll every c in chips' Port A against baseline[c]
                                 until ANY of them changes, or timeoutMs elapses

            if no change on any chip:
                report "no feedback change — wiring problem?" ; offer retry/skip
                continue                                            // NOT "this is the Dreieskive": that
                                                                     // branch doesn't exist in Phase 1,
                                                                     // Dreieskive's bits are never in this loop

            diff = result.byte XOR baseline[fbVaddr]
            assert exactly 2 bits set in diff, and they form one of the 4 known
            adjacent pairs (0-1/2-3/4-5/6-7) — otherwise report an anomaly
            (unexpected bits changed) and let the operator retry/abort

            highBit = whichever of the pair's two bits is currently 1 in result.byte

            prompt operator: "which switch moved?" -> pick one of the still-unassigned
            labels on this board (or 0=skip, Esc=abort) -> slot = next free slot
            prompt operator: "is it now at R or A?" -> pos0

            // record: bit value 0 on the motor = pos0; the pair bit that's high right
            // now = pos0's feedback bit; the other pair bit = the opposite position
            SlotPolarity[slot]          = (pos0 == Rett) ? 0 : 1
            (SlotFeedbackRettBit or SlotFeedbackAvvikBit)[slot] = highBit   // whichever pos0 was
            (the other one)[slot]                                = the pair's other bit

            // verification move — not strictly required to fill the table (the pair
            // structure already implies the second bit), but drives the opposite
            // polarity and confirms the feedback pair actually flips as expected,
            // catching wiring/mechanical faults at config time rather than later
            MBIT(chip.vaddr, PORT_B, motorBit, 1)
            confirm = poll fbVaddr's Port A against result.byte, timeoutMs
            if confirm times out or doesn't land on the expected other bit:
                report "opposite position not confirmed" ; offer retry

            SlotMotorVAddr[slot]    = chip.vaddr
            SlotMotorBit[slot]      = motorBit
            SlotFeedbackVAddr[slot] = fbVaddr           // may differ from SlotMotorVAddr
            write all six regions to EEPROM (ER/EW), mark motorBit + slot done
```

**Phase 1 caveat**: "poll every chip of the board simultaneously" needs a
multi-target capability `MPOLL` doesn't have (it watches exactly one
`vaddr`/port/baseline). FSCB's board has exactly one chip, so Phase 1's own
scan never actually needs to watch more than one — `fbVaddr` is always
`chip.vaddr` in practice, and a single `MPOLL(chip.vaddr, PORT_A, baseline, timeoutMs)`
suffices, same as the previous draft. The algorithm above is written for the
general (2-chip) case because the *schema* now supports it
(`SlotFeedbackVAddr` independent of `SlotMotorVAddr`), but actually
implementing simultaneous multi-chip polling is deferred until a 2-chip
board is actually being scanned — see [Explicitly deferred](#explicitly-deferred-not-phase-1).

Each switch is scanned **to completion** (including the verification move)
before the next one starts — motors are driven one at a time deliberately, so
a feedback change on Port A can always be attributed to the motor bit that
was just fired. Two motors settling concurrently on the same board would make
the diff-pair detection ambiguous, since they'd share the same Port A
byte(s) — true whether the board has 1 or 2 chips.

Updated from an earlier draft: the scan carries the motor's `(vaddr, motorBit)`
through the loop as before, but feedback discovery is no longer assumed to
stay on that same `vaddr` — it's whichever chip of the board actually
changed, recorded separately as `SlotFeedbackVAddr`. Motor and feedback vaddr
happen to be equal for every switch on FSCB (one chip, nothing else it could
be), but the table no longer bakes that in as a rule.

"5/6" is scanned and stored exactly like any other label — one motor bit, one
feedback pair — since per the spec these two physical switches are driven and
read together as a single unit. **Open question for the user**: confirm 5 and
6 really share one motor output and one feedback pair with no way to
distinguish them electrically, since that's what determines whether this is
just a display-label nuance or a real hardware constraint worth flagging back.

Commands 2/3/4/M/D and the routing matrix stay out of the Phase 1 menu — the
existing `ConfigSession` menu should show only item `1` for now (per
`PlanExtended.md`: "For phase 1, we will only need to cover item 1"). Leave
the other menu entries commented out or behind a feature flag rather than
deleted, since their logic (LED mapping, switch mapping) will very likely be
reusable in Phase 2 once ported to the generic Mcp* primitives.

### System configuration persistence (`SystemConfig.json`)

**Hardware vs. configuration, precisely**: hardware is whatever is true for
every board of a given *type*, fixed by its PCB/schematic, regardless of
where it's installed — e.g. "an SCB's Port B is output, Port A is
input+pullup". Motor Scan never discovers that; it's a board-design constant
(currently just hardcoded per Phase 1's single board type, see
[Explicitly deferred](#explicitly-deferred-not-phase-1)). Everything Motor
Scan *does* discover — which motor bit drives which switch, which feedback
pins belong to it, its Rett/Avvik polarity — depends on how *this specific
installation's* cabinet got wired, and would differ on an otherwise-identical
board wired up differently. That's **configuration**, not hardware, and so is
`BoardConfig`'s SVB/switch-number/label topology (Phase 1: hardcoded; Phase
2+: belongs in this same file, not compiled into C#).

So the file is named `SystemConfig.json`, not `hardware.json` — same naming
principle as before (match what the file actually is), corrected: what it
holds is discovered/declared per-site configuration, not physical hardware
facts. Same reasoning as why the LED routing matrix file is named for what
*it* is (`FossliSwitchMatrix.json`, a routing matrix) rather than something
generic.

EEPROM is the only thing firmware reads at runtime, but it's a single point
of failure — a reset, a chip swap, or a new Arduino would otherwise mean
physically re-running Motor Scan (walking every switch motor by hand again)
just to rediscover wiring that hasn't actually changed. **The upload path
exists specifically to avoid that**: as long as the switch-table schema is
unchanged (same regions/record layout — the common case by far, since it only
breaks across a firmware upgrade that changes the EEPROM layout itself), the
saved `SystemConfig.json` alone is enough to repopulate EEPROM. Re-scanning is
only actually required when the physical wiring itself changed — not for a
blank/replaced EEPROM or Arduino.

- **Written automatically**: after each slot is confirmed during Motor Scan
  (not just at the end — so an aborted scan still leaves a `SystemConfig.json`
  that matches whatever EEPROM actually has).
- **`J` — new `ConfigSession` menu item** ("System config (upload/download
  SystemConfig.json)"), alongside `1`, offering:
  - **Download**: `SCD` → read every configured slot back from EEPROM, write/overwrite `SystemConfig.json`. Useful to regenerate the file if it's ever missing or stale.
  - **Upload**: read `SystemConfig.json`, send `SCU` + one line per switch, `END` → restores EEPROM from a saved file, e.g. after an EEPROM/Arduino replacement, with no physical re-scan.
- EEPROM remains authoritative for what firmware actually runs against —
  `SystemConfig.json` is a mirror, not a second source of truth firmware ever
  reads from directly.

Keyed by switch **label**, not raw slot number, so the file stays meaningful
if slot numbering ever shifts (e.g. Region reshuffle, or Phase 2 inserting a
second SCB's switches ahead of these in slot order). Nested under a
`"switches"` section specifically so the file has room to grow a second
top-level section later (Phase 2's `BoardConfig` topology — SVBs, SCBs, which
switch numbers belong to which — also site configuration, so it belongs here
too) without a breaking format change:

Actual file: `DrammenMJKConfig/SystemConfig.json`. It carries a top-level
`"_todo"` array — plain strings, not consumed by any code, just a place to
record open questions directly in the file itself rather than losing them in
chat history (JSON has no comment syntax, this is the pragmatic substitute):

```json
{
  "_todo": [
    "FCSBL's switch labels are now known: 101, 2, 4 (matching the older letter-based system's Pens B, C, D at Fossli). Not yet in the switches section below -- that section means 'discovered wiring', and only the labels are known so far, not which of motor bits 0,1,2 is which. A Motor-Scan-style pass (fire each bit, ask the operator which switch moved) is what fills that in, same process as FCSBR; the physical facts needed to run that scan (motor bits 0,1,2 on vaddr 0x21; feedback pairs (0,1)/(2,3)/(4,5) on the same chip) are already known from hardware.json.",
    "FCSBL drives one signal (3 lamps: Red, Green1, Green2) on Port B bits 4,5,6 -- range now known, but which specific bit is which color is not. The doc also says 'we have two of these, so they should be numbered' -- Fossli has 2 signals station-wide (mirroring the original DualSignal's Signal A/B), but only one is confirmed to be on FCSBL. Where the second signal lives is not yet stated; only one signals entry is populated below.",
    "Signal numbering is explicitly deferred in PlanExtended.md ('The letters we use there is now converted to numbers. Will come back to that.') -- the signals section below stays structurally unconfigured even though the candidate bits are known.",
    "Track detection is now known to be Port A bit 7 on 0x21 (below). Its polarity (which level = train present) still needs configuring -- same as the old DualSignal system's active-HIGH Train input, which was the one input that didn't follow the usual active-low convention.",
    "fadeConfig values are the original SignalLys_2X_Momentbrytere.ino defaults (FadeMs=1000, FadeSteps=60, PwmPeriodUs=1000), ported as-is rather than recomputed. Unlike the original's hardcoded static const, these are now genuine config -- editable here without a firmware recompile -- but nobody has had a reason to change them yet, so they stay at the values already proven on the original hardware."
  ],
  "site": "Fossli",
  "svb": "Fossli",
  "switches": {
    "1":   { "motorVAddr": "0x20", "motorBit": 0, "polarity": 0, "feedbackVAddr": "0x20", "feedbackRettBit": 0, "feedbackAvvikBit": 1 },
    "3":   { "motorVAddr": "0x20", "motorBit": 1, "polarity": 1, "feedbackVAddr": "0x20", "feedbackRettBit": 3, "feedbackAvvikBit": 2 },
    "5/6": { "motorVAddr": "0x20", "motorBit": 2, "polarity": 0, "feedbackVAddr": "0x20", "feedbackRettBit": 4, "feedbackAvvikBit": 5 },
    "7":   { "motorVAddr": "0x20", "motorBit": 3, "polarity": 0, "feedbackVAddr": "0x20", "feedbackRettBit": 6, "feedbackAvvikBit": 7 }
  },
  "dreieskive": {
    "motorVAddr": "0x20",
    "motorPinBase": 4,
    "cwPolarity": null
  },
  "statusLed": {
    "vaddr": "0x20",
    "bit": 7
  },
  "svbSwitches": {
    "1":          { "vaddr": null, "bit": null },
    "3":          { "vaddr": null, "bit": null },
    "5/6":        { "vaddr": null, "bit": null },
    "7":          { "vaddr": null, "bit": null },
    "dreieskive": { "vaddr": null, "bitCw": null, "bitCcw": null }
  },
  "signals": {
    "1": { "vaddr": "0x21", "redBit": null, "green1Bit": null, "green2Bit": null }
  },
  "trackDetection": {
    "vaddr": "0x21",
    "bit": 7,
    "activeHigh": null
  },
  "fadeConfig": {
    "fadeMs": 1000,
    "fadeSteps": 60,
    "pwmPeriodUs": 1000
  }
}
```

`motorVAddr`/`feedbackVAddr` (split from a single `vaddr` field — a switch's
feedback can land on a different chip than its motor, on boards with 2
MCP23017s; see [Discovery algorithm](#discovery-algorithm)) are deliberately
just the address, not a board name: the name lives once, in `hardware.json`
(cross-reference `boards[].chips[].vaddr` there), not duplicated here so the
two files can't drift out of sync with each other. On FCSBR both fields are
always `"0x20"` since it's a single-chip board — the split only matters once
a 2-chip board's switches get scanned.

`statusLed` is a fixed FCSBR fact (Port B bit 7 per `PlanExtended.md`), not
discovered — same category as the switches ("what this pin is for", not
direction/pull-up, so it belongs here rather than in `hardware.json`).

`dreieskive` here is the **motor** side only (FCSBR, bits 4–5, a fixed board
fact, known now — mirroring the two-pin encoding `ArduinoDevice.cs`'s older
`Region5MotorPin`/`Pol` fields used for the same concept). `cwPolarity` stays
`null` in Phase 1 — resolving it needs the matching entry in `svbSwitches`
below to exist first.

`signals`/`trackDetection` are new sections, added straight from FCSBL's
spec: FCSBL drives one signal (`redBit`/`green1Bit`/`green2Bit`, all still
`null`) and reads a single track-detection bit whose active level isn't
determined yet (`activeHigh: null`) — same shape reasoning as everything
else here, wiring facts declared or discovered, never invented. Neither
section models the *signal-lamp state machine itself* (Direction, PWM
dimming, train-present timeout — see
[Signal lamp logic](#signal-lamp-logic-out-of-scope)) — that stays pure
firmware logic with no config surface, exactly like the original DualSignal
system, where only pin *assignment* was configurable, never the behavior.

`svbSwitches` is a genuinely separate concept from `switches`/`dreieskive`
(matches the [SVB switch table](#svb-switch-table) EEPROM split): every SVB
panel switch is assigned to exactly one target — a switch's Rett/Avvik
position (`bit`, single bit — a plain on/off toggle) or, for the one
exception, the dreieskive's 3-position toggle (`bitCw`/`bitCcw`, two bits,
per `PlanExtended.md`'s "toggle switch on1/off/on2 ... use two bits" SVB
input). Keyed by the same label space as `switches` (plus the special
`"dreieskive"` key) so the two sections read as two facts about the same
named things, not unrelated tables. `cwPolarity` above is what a future
runtime loop uses together with `svbSwitches.dreieskive` to translate "the
SVB's CW bit went active" into "drive these two SCB motor bits to whichever
pattern is actually clockwise" — same role `polarity` plays linking a normal
switch's `svbSwitches` entry to its `switches` entry.

Every `svbSwitches` value stays `null` through all of Phase 1: Phase 1 has no
SVB at all, so there's nothing to discover yet — this section (like
`dreieskive.cwPolarity`) is scaffolding for Phase 2's manual-switch and
dreieskive-switch discovery flows, not something Phase 1 populates.

On upload, C# resolves each label back to its slot via `BoardConfig` (the
same label list Motor Scan uses) before sending the `S` lines, and sends the
`dreieskive`/`statusLed`/`svbSwitches` sections as the `D`/`L`/`P` lines — so
the file itself never needs to know about EEPROM slot numbers at all, only
labels + wiring facts.

### Command mode (new top-level mode, `CommandSession`)

Per `PlanExtended.md`: "add a command mode, so we can send direct commands to
the switch motors, by giving their number... That way we can actually operate
the switches from the PC" — and per the follow-up correction, addressed by
**SVB name/number + switch number**, not by raw pin.

Added as a new top-level entry alongside `C`/`D`/`V`/`Q` in `Program.cs`
(e.g. `O` — Operate), not squeezed into `ConfigSession`, since it's a runtime
operation mode, not a discovery/config one.

Flow:
1. Operator types a switch number (`1`, `3`, `5/6`, `7`) — reject if
   `BoardConfig` has no slot for it, or the slot's EEPROM entry is still
   unconfigured ("run Motor scan first").
2. Operator types the target position (`R`=Rett/closed, `A`=Avvik/open — reuse
   the existing R/A convention, not O/C).
3. C# resolves the label to a slot and calls `DriveSwitch(slot, pos, timeoutMs)` → `SW <slot> <pos> <timeoutMs>`. All the port/bit/polarity resolution happens in firmware.
4. Report the result (`RETT`/`AVVIK`/`BETWEEN`/`TIMEOUT`) to the operator.
5. `Esc` returns to the top-level menu.

Since `SW` already blocks until the motor settles or times out, there's no
separate poll step on the C# side — this is simpler than the original
Mcp*-based sketch, precisely because port/bit resolution moved server-side.

---

## Runtime / production behavior (design note, not Phase 1 scope)

Not built in Phase 1, but this is *why* the switch table lives in firmware
rather than C#: eventually the CB polls each SVB's panel-switch input port,
and on a change, looks up the corresponding slot and calls the same internal
`resolve_switch()` + drive logic that `SW` calls today — no PC involved, no
protocol change needed. Phase 1's `SW`/`SR` are literally "manually invoke the
function the runtime loop will call automatically later." Getting the
table/resolver shape right now avoids reworking it when that loop is built.

**Device state — per device, not global.** Every physical board (one entry
per `vaddr` in the board hardware table) carries its own state, independent
of every other board's:

| State | Meaning |
|---|---|
| `UNINITIALIZED` | Every device starts here at power-up, before anything's been checked |
| `CONFIGURATION` | The PC config tool is actively driving *this* device (Motor Scan, Command Mode, `hardware.json`/`SystemConfig.json` upload targeting it) |
| `NORMAL` | Standard runtime — this device's inputs/outputs respond to events as usual |
| `FADING` | This device is running a blocking fade (only ever reachable on the two boards that can fade at all — FCSBL, MotorVallekilen) |

Transitions, per device, each power session:

- **First time ever** (this `vaddr` has no entry in the EEPROM board
  hardware table yet — the existing `BoardVAddr[i] != 0xFF` check already
  answers this): `UNINITIALIZED → CONFIGURATION → NORMAL`. It has to be
  configured via the PC tool before it can run.
- **Every subsequent boot** (already configured): `UNINITIALIZED → NORMAL`
  directly — no config session needed, same distinction the boot-time
  hardware bring-up already makes.
- **PC config tool connects and targets this device** (even one already
  configured — re-running Motor Scan, Command Mode, a fresh upload):
  `NORMAL → CONFIGURATION`, back to `NORMAL` when that session ends.
- **A fade starts** (fading-capable devices only): `NORMAL → FADING`, back
  to `NORMAL` when the fade's blocking call returns.

What this buys, precisely, and corrected from an earlier draft that got the
scope wrong: `FADING` blocks messages **to that one device**, not messages
in general. Between individual I2C transactions the Arduino is free — it
isn't locked into an uninterruptible ~1-second block just because one device
is fading. Before any I2C message goes out, the check is "is the *target*
device currently `FADING`?" — if the message is for the fading device
itself, it queues until that device is back to `NORMAL`; if it's for any
other device (`NORMAL`, regardless of what the fading device is doing), it
goes out immediately, interleaved between the fading device's own toggle
writes. See [Signal lamp logic](#signal-lamp-logic-out-of-scope) for how the
fade loop's own wait is structured to make room for that. The states are
also reusable for things unrelated to fading, e.g. rejecting an `SW` sent to
a device still in `CONFIGURATION`.

**Port-output state: one shadow byte per port, shared by every writer of
that port.** "Persistent" in an earlier draft was a misleading label — every
piece of RAM state here lives for the same lifetime (until reboot), so
naming one shadow "persistent" as if it were a different kind of thing from
another was the confusing part, not the mechanism itself. There's just one
shadow per physical register, full stop.

This is a deliberate exception to `MBIT`'s "always read live hardware, never
cache" rule (see [Command mode](#command-mode-new-top-level-mode-commandsession)),
justified by boot-time necessity rather than performance: at boot, the
runtime loop has to read every SVB switch on a board and compute one
combined motor-pattern byte before writing anything to that SCB's Port B —
that computed byte **is** a shadow whether it's named one or not. From then
on it's simply kept accurate: seeded once, updated in place by every write,
never re-read.

**Why one shadow, not one per concern.** On a board where more than one
runtime concern writes the same physical register — FCSBL's Port B holds
both the motor bits (0–2) and the signal-LED bits (4–6, driven by the fade
loop, see below — Signal lamp logic) — those two concerns write **disjoint
bitmasks** of the same byte. A motor-bit write only ever touches bits 0–2
(`shadow = (shadow & ~motorMask) | motorBits`); a fade toggle only ever
touches bits 4–6. Neither needs to know anything about the other's pending
change — each just leaves whatever's already in the bits it doesn't own
alone, which is automatically correct as long as both are reading and
writing the *same* shadow. Two independent copies (an earlier draft's
mistake) would let them drift the moment both fired close together, since
neither would see the other's update.

**The only real requirement: writes can't interleave with each other.**
"Compute the new byte from the shadow → send it over I2C → update the
shadow" has to run to completion as one unit before another writer touches
the same shadow — not because the *bits* conflict (they don't, being
disjoint), but because two writers could otherwise both read the same stale
shadow and each write back a version missing the other's change. This holds
by construction here, not by adding locks: the ISR (`PlanExtended.md`'s INTA
wiring, deferred past Phase 1) is only ever allowed to set a flag and return
— it never calls `Wire`/I2C or touches the shadow directly, both because
Arduino's `Wire` library isn't ISR-safe regardless of this design, and
because it's what keeps every actual write single-threaded on the main
loop. Given that, there is no code path that can interrupt a write already
in progress — "blocked writing" is automatic, not something that needs
explicit `noInterrupts()`/`interrupts()` guarding, *as long as that one rule
(ISRs never write) keeps holding*. Worth a comment at the ISR call site
saying so, since it's the kind of invariant that breaks silently if someone
later "simplifies" by writing directly from an interrupt handler.

Deliberately **not** used by `MBIT`/Motor Scan/Command Mode, which keep
reading live hardware on every operation exactly as designed — those are
infrequent, human-paced, interactive operations where anchoring every write
to the register's actual current content matters more than avoiding one
extra I2C read (see [System configuration persistence](#system-configuration-persistence-systemconfigjson)
for what that read-first behavior actually buys: no accumulated drift, and a
failed read is visible at the point of the read rather than laundered into a
wrong write later).

One residual risk, accepted rather than silently ignored: if an SCB chip
resets or glitches independently *while the Arduino keeps running* (not a
full power-cycle — that resets the shadow along with it and is
self-correcting), the shadow has no way to detect the mismatch and would
keep pushing stale bits back. Not a reason to reject the shadow — that
failure mode is rare, and read-modify-write on every poll cycle across every
switch system-wide would be real, ongoing I2C overhead for a benefit that
almost never materializes. A cheap, non-urgent hardening for later: a
periodic full resync (re-apply the shadow to hardware every so often
regardless of whether a change was detected), not built now.

## Signal lamp logic (out of scope)

The original DualSignal signal-lamp behavior (`SPECS.md`, actual source:
`SignalLys_2X_Momentbrytere/SignalLys_2X_Momentbrytere.ino`) needs gradual
dimming during lamp transitions (not just steady on/off), which is a hard
real-time concern — it will be **ported natively into firmware** when it's
built, not decomposed into portable "generic" C#-driven rules the way the
switch config logic was. Phase 1 has no lamps or SVBs at all, so this is
purely a heads-up for later phases: don't expect the lamp state machine to
follow the "logic lives in C#" pattern used elsewhere in this plan.

**Dimming mechanism, confirmed**: the original code uses **software PWM**
throughout, never `analogWrite`/hardware timers, even on the pins that could
support it (D9/D10/D11 are hardware-PWM-capable, D7/D8/D12 aren't — uniform
software PWM sidesteps having some lamps fade differently than others).
`FadeGroup()`'s algorithm: a 1-second fade (`FadeMs = 1000`) in 60 linear
steps (`FadeSteps = 60`), each step busy-toggling the target pin(s) HIGH then
LOW for `duty`/`(255-duty)` fractions of a 1ms period (`PwmPeriodUs = 1000`)
via `digitalWrite` + `delayMicroseconds` — entirely blocking for the fade's
duration. `ApplyNormalOutputsSlowly()` diffs the desired lamp state against
current state on a direction/track change, splits into "fading off" and
"fading on" groups, and fades them sequentially.

Per direction: **port the algorithm exactly** — same constants, same 60-step
linear timing, same visual result — but the original's waiting mechanism
(`delayMicroseconds`, a hard busy-wait) doesn't port as directly as the rest
of it, and stated precisely in terms of
[Device state](#runtime--production-behavior-design-note-not-phase-1-scope)
above, here's why: in the original, there was nothing else the MCU could
usefully do during that wait (one board, no I2C, no other devices), so a
hard busy-wait cost nothing. That's no longer true — this system has ~10
I2C devices, and blocking all of them for up to a second because *one*
device (FCSBL or MotorVallekilen) is fading would be real, avoidable
collateral damage, not an inherent cost of the port.

So the fade's wait becomes **deadline-polling, not blocking**: instead of
`delayMicroseconds(highUs)`, compute the target timestamp via `micros()` and
loop until it's reached — and inside that loop, service any pending message
whose *target* device is not the one currently `FADING`, exactly as
[Device state](#runtime--production-behavior-design-note-not-phase-1-scope)
describes. A message for a `NORMAL` device goes out immediately, interleaved
between the fading device's own toggle writes; only a message for the
fading device itself queues until it's back to `NORMAL`. Same-bus devices
interleave for the cost of one extra I2C transaction; a device on a
*different* bus costs an extra mux channel switch there and back (to return
to the fading device's bus in time for its next toggle) — more overhead,
still far cheaper than making every other bus wait up to a second. At the
extremes of duty cycle one phase can be too short to fit an extra
transaction (near 0% or 100%, one of `highUs`/`lowUs` approaches zero) — the
loop should skip interleaving when there isn't enough of the window left,
not stall the fade to force one through.

**Scope, precisely**: only two of the ~10 MCP devices in the whole system
can ever reach `FADING` at all — FCSBL (Fossli Venstre) and MotorVallekilen,
the only boards that combine motor-driving and signal-fading on the same
chip. The other 8 never run a blocking fade loop and never leave `NORMAL`
for this reason — and per the redesign above, their own pending events don't
even need to wait for someone else's fade, only a message addressed to the
fading device itself does.

**What the fade loop owns beyond the shared shadow.** Not everything about a
fade belongs in the port shadow — that byte only ever holds "what's
currently written to hardware." Three more pieces of state exist, and
they're not all the same *kind* of state — worth not bundling them together
the way an earlier draft did:

- **Fade parameters** — `FadeMs` (total duration), `FadeSteps` (step count),
  `PwmPeriodUs` (software-PWM carrier period) — **config, not per-call
  state**: identical for every fade that ever runs, set once, read every
  time. Same category as everything else tunable in this plan, so they
  belong in `SystemConfig.json` (a new `fadeConfig` section) rather than
  hardcoded — see below.
- **The end value** — the target pattern the fade is converging toward (the
  original's `desired[]`, computed fresh by the `ApplyNormalOutputsSlowly()`
  port on every direction/track-state change). Scoped to one fade call.
- **Fade progress** — genuinely transient, reset every call: which of the
  `FadeSteps` steps it's on, the current duty, the step deadline. Purely
  local loop state, never written to hardware, never shared with anything
  else, and the only one of the three that actually changes *during* a fade.

None of these three is a second copy of the shadow, so there's nothing to
keep in sync between them and it — the fade reads its parameters once,
computes each step's target byte from the end value and its progress state,
applies it to the *one* shared shadow via its bitmask (bits 4–6 only), and
writes.

That leaves interrupts. Two cases, and they resolve differently:

- **Track detection is a read, not a write, so it was never a collision
  risk in the first place** — on any device, fading or not. It's on Port A;
  the fade only ever writes Port B. Those are physically distinct MCP23017
  registers (separate register addresses for GPIO/OLAT per port) — reading
  Port A at any moment, mid-fade or not, never touches what's sitting in
  Port B's OLAT. This was never blocked on anything and doesn't need the
  interleaving mechanism above at all.
- **A switch command on the *fading device itself* has to wait** — that
  does mean a motor write to Port B, the same register the fade is
  toggling, so it's a genuine same-shadow conflict (see Port-output state
  above). But the fade is at most 1 second, and the physical switch motor's
  own throw time is "way above that", so queuing it costs nothing
  meaningful. A switch command on **any other device**, though, isn't in
  this situation at all — that device isn't `FADING`, so per
  [Device state](#runtime--production-behavior-design-note-not-phase-1-scope)
  its message just goes out, interleaved into the fading device's wait
  windows like anything else targeting a `NORMAL` device.

The ISR still does the absolute minimum — record that something changed (a
`volatile` flag, tagged with which device it's for) and return, never
touching `Wire`/I2C or the shadow directly, since `Wire` isn't ISR-safe
regardless of any of this design. `FadeGroup`'s deadline-polling loop
(described above) is what actually drains that queue: on every wait-window
check, service any pending message for a device that isn't the one fading;
leave a pending message for the fading device itself queued. Once the fade
finishes and the device transitions `FADING → NORMAL`, that last
device-specific message is serviced against the **shared** port shadow —
correctly, because the shadow already reflects wherever the fade left the
signal bits, so it preserves them automatically without having needed to
coordinate with the fade while it ran.

---

## Task order

1. `git mv`/copy `Simulator/Simulator.ino` → `Firmware/Firmware.ino`.
2. Strip all simulated-Pens state/business logic; keep `readLine`, `dispatch`,
   `ok()`/`errCmd()`/`status()` helpers, and the `PING`/`SI`/`ER`/`EW`/`EC`/`RST`
   handlers (these operate on the Arduino's own EEPROM, no hardware change needed).
3. Add `Wire.h` + the 3-primitive generic MCP23017 driver (`mcp_write_reg`,
   `mcp_read_reg`, direction/pullup helpers) — same shape as
   `PLAN_Refactor.md`'s driver, reference `Firmware_FirstAttempt`'s
   `mcp_write`/`mcp_read` for the register-access pattern only.
4. Add the virtual-address resolver (`resolve_vaddr(vaddr) -> {bus, real_addr}`) and `i2c_select_bus(uint8_t bus)`; Phase 1 the latter is a validating no-op (only `bus==0` is legal).
5. Add the board hardware table EEPROM regions + `resolve_board_hw(vaddr)` reader, and the data-driven boot-time bring-up routine that applies every configured entry (IODIR/GPPU/OLAT=0x00).
6. Implement `MDIR` / `MPU` / `MW` / `MR` / `MPOLL` / `MBIT` dispatch handlers, each going through the resolver.
7. Implement `HWU` / `HWD` bulk transfer dispatch handlers for the board hardware table (`HWU` also re-runs the boot-time bring-up routine live after storing).
8. Add the switch-table EEPROM regions + `resolve_switch(slot)` reader.
9. Implement `SW` / `SR` dispatch handlers on top of `resolve_switch()`.
10. Implement `SCU` / `SCD` bulk transfer dispatch handlers (same shape as the existing `RMU`/`RMD` handlers, just against the switch-table regions instead of Region6).
11. Bench test against FSCB directly: manual `HWU` with a hand-built line, confirm GPB7 LED goes off, then `MDIR`/`MW`/`MR` to sanity-check Port A feedback reads pull-up idle-high correctly.
12. `DrammenMJKConfig`: add `HardwareConfig` (`hardware.json` model) and the `HardwareConfigUpload`/`Download` wrappers, the raw `Mcp*` wrappers (`MotorScan`-only), and the `DriveSwitch`/`ReadSwitch` + `SystemConfigUpload`/`Download` wrappers to `ArduinoDevice`.
13. `DrammenMJKConfig`: add `BoardConfig` with the hardcoded Fossli/FSCB/4-switch descriptor.
14. `DrammenMJKConfig`: add `HardwareConfigSession` (`hardware.json` ⇄ `HWU`/`HWD`) and wire it into `ConfigSession` as item `H`.
15. `DrammenMJKConfig`: rewrite `MotorScan` for the 4-switch/no-dreieskive Phase 1 scope, targeting `BoardConfig` + raw `Mcp*` calls, writing all six switch-table regions (including the separate `SlotFeedbackVAddr`) per discovered slot, and updating `SystemConfig.json` after each slot is stored; also write the fixed dreieskive/status-LED facts (`DreieskiveVAddr`/`DreieskiveMotorPinBase`/`StatusLedVAddr`/`StatusLedBit`) at scan start, straight from `BoardConfig`, before the switch loop runs.
16. `DrammenMJKConfig`: add `SystemConfigSession` (`SystemConfig.json` ⇄ `SCU`/`SCD`) and wire it into `ConfigSession` as item `J`.
17. Trim `ConfigSession`'s menu to items `H`, `1`, and `J` only (comment out 2/3/4/M/D/R, or gate behind a constant) — reassess once Phase 2 boards exist.
18. `DrammenMJKConfig`: add `CommandSession` + wire it into `Program.cs`'s top-level menu (`O` — Operate), using `DriveSwitch`/`ReadSwitch` only.
19. End-to-end run against real FSCB hardware: power up with a blank EEPROM and confirm nothing happens (no board hardware table yet); upload `hardware.json` via `H` and confirm the status LED clears itself immediately, without a reboot; run Motor Scan and confirm `SystemConfig.json` is written; power-cycle with no PC attached and confirm the LED still clears itself automatically; erase EEPROM (`R`), re-upload `hardware.json` **and** restore `SystemConfig.json` via `J`'s upload **without touching the switch hardware**, confirming Command mode still works afterward.

---

## Explicitly deferred (not Phase 1)

- INTA/interrupt-driven feedback (spec says polling only for now).
- The physical I2C mux and its channel-select mechanism — the *address encoding*
  (`vaddr = bus*0x10 + real_addr`) is now settled and implemented in the
  resolver, but `i2c_select_bus()`'s body (which register/command actually
  switches the mux to a given bus) is a no-op until the mux hardware/datasheet
  is in hand.
- The other 7 boards' `hardware.json` entries (their actual per-chip port
  arrays) — not deferred as a mechanism (`hardware.json`, `HWU`/`HWD`, the
  board hardware table, the boot-time bring-up routine are all already
  generic and board-count-agnostic), just nothing to declare yet since Phase 1
  only has FSCB. Adding any of them later is purely a data change: one more
  entry in the `boards` array.
- Simultaneous multi-chip feedback polling during Motor Scan. The switch-table
  schema already supports a switch's feedback landing on a different chip
  than its motor (`SlotFeedbackVAddr` independent of `SlotMotorVAddr`), but
  `MPOLL` only watches one `vaddr` at a time — actually discovering feedback
  on the *other* chip of a 2-chip board needs a poll that watches all of a
  board's chips together and reports which one changed. FSCB is single-chip,
  so Phase 1 never needs this; build it when the first 2-chip board (e.g. an
  SVB) is actually scanned.
- Populating the SVB switch table (`svbSwitches` in `SystemConfig.json`,
  `SvbSw*` in EEPROM) and `dreieskive.cwPolarity` — needs a Phase 2 discovery
  flow (the old system's "Command 2 — Manual switch → Pens mapping" and
  "Command D — Dreieskive switch" both apply here, adapted to the new
  schema) and an actual SVB board to run it against, neither of which Phase 1
  has. The table itself is built now; nothing in it is populated yet.
- The other 4 SCBs, 2 SVBs, and Vallekilen's combined SCB+SVB pair — including
  the mixed-port cases `PlanExtended.md`'s "Hardware configuration" section
  describes (a motor bit on an otherwise-LED SVB port, a 3-position toggle
  input using 2 bits). The per-bit `hardware.json` schema already handles
  these structurally; there's just nothing to declare yet since Phase 1 only
  has FSCB, which is uniform per port.
- Track-signal LEDs with dimming (`PlanExtended.md`: "Signals on track (3
  leds), used with dimming up/down") living on SCB outputs — same real-time
  PWM concern as [Signal lamp logic](#signal-lamp-logic-out-of-scope), just
  now confirmed to be SCB-hosted rather than only on the original DualSignal
  board. Still native-firmware scope when built, still out of Phase 1 (FSCB
  has no signal outputs).
- Multi-board topology config (moving `BoardConfig` from a hardcoded C# value
  into `SystemConfig.json` alongside the switch-wiring section) — worth doing
  as soon as board #2 shows up, not before.
- The autonomous SVB-event-driven runtime loop (see [Runtime / production behavior](#runtime--production-behavior-design-note-not-phase-1-scope)) — Phase 1 only builds the table + resolver + manual trigger (`SW`/`SR`), not the loop that will call them automatically.
- Signal-lamp PWM/dimming logic ported from the old DualSignal firmware (see [Signal lamp logic](#signal-lamp-logic-out-of-scope)).
- The second Arduino + 2×16 display.
- Reconciling/porting the older Pens-letter (A–Z) EEPROM regions and menu commands (2/3/4/M/D) onto the new generic `Mcp*`/switch-table primitives.
