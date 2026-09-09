# Plan: Phase 3

Collects Phase 3 scope as it's decided, same pattern as `PLAN_Phase2.md`.

## Site scale: 9 boards, moving BoardConfig.cs to JSON

Declared facts from the site owner:

- **9 boards total**, all built around the MCP23017 like FCSBR/FCSBL, but
  each genuinely different, not variations pulled from a shared template.
- Of those 9: **6 are SCB** (drive switch motors — "switches/penser", the
  physical points themselves, matching the old Pens A-Z naming), **2 are
  SVB** (stillverk — operator panel switches only, driving nothing directly;
  "stillverk" is the interlocking/panel-control side, not the motor side —
  corrected from an earlier misreading in this doc), **1 is combined**
  (drives switch motors *and* reads operator panel switches on the same
  physical board). 6+2+1 = 9.
- The two boards built so far are named **"Fossli Motors Hoyre"** and
  **"Fossli Motors Venstre"** — differs from what's currently in
  `BoardConfig.cs` (`"Fossli Hoyre (FCSBR)"` / `"Fossli Venstre (FCSBL)"`).
  Real correction to make when this gets implemented, not applied yet
  (plan-only for now).
- **All but one of the SCB-capable boards have the active status LED.**
  Today `StatusLedPin` is a required field on `BoardScb` (not yet made
  optional) — confirms it needs to become optional, the same way
  `DreieskivePins` already did.
- **Only one board has a dreieskive** — matches what's already built
  (`DreieskivePins` nullable, only FCSBR populates it). No further change
  needed here specifically.
- **`BoardConfig.cs` should move to a JSON file.** Hardcoding 9 genuinely
  different boards in C# doesn't fit this project's established pattern
  (`hardware.json`/`SystemConfig.json` for declared/discovered facts) —
  board topology (name, category, vaddr(s), switches, motor-bit-count,
  dreieskive presence, status-LED presence) should be declared data too, not
  compiled in.
- Only FCSBR/FCSBL are concretely specified so far; **the other 7 come
  later**. For now the goal is just making sure the architecture has *room*
  for this variation, not full implementation.

### Motor drive: one bit vs. two bits (resolved, can wait until the next board)

Some boards drive each switch motor with one MCU-controlled bit (like
FCSBR/FCSBL today), others with two. Simpler than it first looked once
clarified — **EEPROM only ever needs to store one bit number per switch,
plus one board-level flag**, not a whole second per-switch field:

- **The second pin, when used, is always `motorBit + 1`** (successive) —
  never needs its own stored value.
- So the only new EEPROM fact is a **board-level** "this board drives with
  two pins" flag (not per-switch) — belongs next to the board hardware table
  entry, alongside IODIR/GPPU, not in the switch table.
- **Same H-bridge driver circuit either way.** The difference is upstream:
  one-bit boards generate the second H-bridge line from an **external
  inverter** (matches `PLAN_Phase1.md`'s note about FSCB's 74HC14 producing
  the second line in hardware, "irrelevant for the communication") — the MCU
  only ever touches one bit regardless. Two-bit boards drive both lines
  directly from the MCP23017, no inverter.
- **Two inverter variants exist**: one needs an enable step after power-up
  before it works, the other doesn't. For one-bit boards using the
  needs-enabling variant, firmware needs to actively drive that enable line
  at bring-up (likely alongside `apply_board_hw()`'s existing per-board
  bring-up sequence) — an additional per-board fact: does this board have an
  inverter-enable pin, and if so, which vaddr+bit.
- `cmdSW`'s drive logic and `MotorScan`'s fire step both need to branch on
  the board-level two-pin flag (fire/drive one bit, or two successive bits
  together) — not yet implemented, deferred until the first two-bit board
  actually arrives.

### Combined SCB+SVB board (resolved)

Represented as **one physical vaddr declared twice** — once in an
SCB-shaped declaration (drives motors), once in an SVB-shaped declaration
(reads panel switches) — not a new, separate record shape. Confirmed by the
site owner; no further design question here.

### Per-board facts, as described by the site owner (7 of 8 boards so far)

Counts and facilities, not yet full switch labels (those come later per the
site owner) except where already scanned:

| Board | Switch motors | Notable |
|---|---|---|
| Fossli Motors Hoyre | 4 (`1`,`3`,`5/6`,`7` — already scanned) | 1 dreieskive, 1 status LED |
| Fossli Motors Venstre | 3 (`101`,`2`,`4` — already scanned) | 1 signal (3 lamps, 3 bits — see below), 1 status LED |
| Sidespor Motors | 3 (labels TBD) | 1 status LED |
| Havna Motors | 8 (labels TBD) | **all 8 motors are two-bit drive** — the first real two-bit board, no longer hypothetical (see "Motor drive" section above). Motors live on one MCP23017, feedback on the *other* — a clean chip-per-role split, not mixed per-switch. |
| Vallekilen Motors | 2 (labels TBD) | 1 signal (3 lamps, 3 bits), 1 status LED |
| Fossli Stillverk (SVB) | n/a | 10 toggle switches + 1 moment switch (11 total) + 19 LEDs |
| Havna Stillverk (SVB) | n/a | 9 switches + 20 LEDs |
| Vallekilen Stillverk (Combined) | not yet described | — |

Site owner's own words: "Some of the LEDs on the SVBs have different
purposes, I'll come back to that later" — intentionally incomplete, not a
gap to chase down yet.

### Two new schema gaps surfaced by the above (neither modeled in
`boards.json` at all yet)

- **"Signal"**: a 3-lamp group (Red/Green1/Green2, matching the FCSBL-era
  notes already in `PLAN_Phase1.md`) needing 3 bits — distinct from the
  1-bit status LED. Appears on Fossli Motors Venstre and Vallekilen Motors
  so far. `boards.json`'s `BoardDeclaration` has no field for this.
- **SVB switches/LEDs**: pure-SVB boards (Fossli Stillverk, Havna Stillverk)
  need to declare switch counts (with a toggle-vs-moment distinction) and LED
  counts — `BoardDeclaration` currently carries nothing for SVB-category
  boards beyond name/category/bus/chips.

Both are genuinely new schema surface, not just filling in known fields.
Given there's more still coming (Vallekilen Stillverk, the LED-purpose
breakdown), holding off on designing this part of the schema until the
picture is more complete, same reasoning as before.

### Still open

- **`BoardSvb` naming** — purely a code-organization question, not a
  site-owner decision. `BoardSvb` currently means "the site's container that
  owns a list of SCBs" (the Fossli-wide unit); a real physical SVB panel
  board is a different concept that will need its own record type once
  implemented. To be sorted out at implementation time.
- **JSON schema shape** — field names, where the two-pin-drive flag,
  inverter-enable fact, signal (3-bit lamp group), and SVB switch/LED facts
  all live, how multi-chip boards (see "Multi-chip SCB support" below)
  compose with this. Not designed yet — deliberately, until the remaining
  board (Vallekilen Stillverk) and the SVB LED-purpose breakdown are known.

## Multi-chip SCB support

Motivating context: during live bring-up of FCSBR, it came up that the data
model has no way to represent a board built from more than one MCP23017.

Today `BoardScb` (`BoardConfig.cs`) has a single `VirtualAddress: byte`
field, and `ConfigSession.MotorScan` uses that one `vaddr` for both firing
motor bits (Port B) and polling feedback (Port A). If a board actually needed
a second chip, there's nowhere in the record to put the second address, and
polling would only ever watch the first chip — a switch whose motor bit lives
on chip 1 but feedback on chip 2 couldn't be scanned at all.

Not an active problem yet: checking `hardware.json`'s own `_todo` notes,
FCSBL's full bit count (3 motors + 3 signal LEDs + 1 status LED + 6 feedback
+ 1 track-detect = 14 bits, +2 spares = 16) fits inside one chip's 16 bits.
Both boards defined in `BoardConfig` today are single-chip. Deliberately
holding off on generalizing this until a real 2-chip board shows up, so the
design is driven by actual requirements rather than speculation (see the
[[main-thread]] discussion this came out of).

When it does come up, needs:

- `BoardScb` to carry a *set* of chip vaddrs instead of one (motor-bit and
  feedback-bit definitions per switch would need to say which chip they're
  on, since a single board's switches could be split across chips).
- `MotorScan`'s baseline-read/fire/poll loop to read and poll across all of
  a board's chips together, not just one vaddr — a switch moving could show
  up as a feedback change on either chip depending on wiring.
- Scope stays "one board at a time" either way (per the bring-up
  conversation this came from) — multi-chip only changes how many addresses
  *that one board* touches, not whether multiple boards get scanned together.
