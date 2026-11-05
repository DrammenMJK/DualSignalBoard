# Plan: Firmware runtime loop -- panel controls the layout without the PC

Goal: with no PC attached, the Arduino reads the SVB panel switches, drives
the pens motors and the dreieskive, and lights the panel LEDs from the pens
feedback. Everything it needs is already in EEPROM; today `loop()` only
serves serial commands.

Background: `PLAN_Phase1.md` "Runtime / production behavior",
`chatlogs/stillverk.chatlog.md` "Motor drive vs feedback" and "Motor travel
timing", `chatlogs/panel-bench-test.chatlog.md`.

## Rules (from the site owner, 2026-11-05)

- Motors move ONLY on request from the panel. Nothing else moves them at
  runtime.
- Everything is polled all the time, and every panel switch / pens pair is
  independent: while one pens moves, another switch can change.
- Event driven, no per-pens state machine. Two kinds of event, each acted on
  at once:
  1. **Panel switch changed** -> send the new position to its pens.
  2. **Pens feedback changed** -> left its position (feedback `00` in the
     owner's notation): turn off its LEDs. Reached a position: turn on that
     position's LED.
- A switch flipped back during a move needs no special case: it is just one
  more panel event, and the feedback events follow the motor.
- Track monitoring comes later.
- Panel LEDs are always active-low. SCB signal LEDs are always active-high.
  Fixed per board type, so no polarity is stored.
- At boot, every pens is synced to its panel switch.
- Dreieskive with CW and CCW both active = error: stop the motor.
- Signal lamps and the green request come later.

## Feedback encoding

The site owner writes a move as `10 -> 00 -> 01`, with `11` = error. That
notation is 1 = contact closed. The firmware reads raw pin levels with
pull-ups, where a closed contact reads 0, so the same move is
`10 -> 11 -> 01` raw (as in `stillverk.chatlog.md`), and the error is raw
`00`. `read_switch_state()` already maps raw levels this way:

| Raw (pin level) | Owner notation | `read_switch_state()` | Meaning                    |
|-----------------|----------------|-----------------------|----------------------------|
| 10 / 01         | 01 / 10        | RETT / AVVIK          | At an end position         |
| 11              | 00             | BETWEEN               | Travelling, left the start |
| 00              | 11             | FAULT                 | Error: both contacts closed |

## Scope

In (version 1): pens, panel LEDs, dreieskive, serial commands keep working,
config tool can pause the runtime.

Out: signal lamps, green request, panel signal-lamp LEDs (`Q` roles 2-4,
left dark), track detection, MCP23017 interrupts (polling is enough here),
how an error is shown (site owner decides later -- see Errors).

## What already exists

| Need                                 | Where                                                   |
|--------------------------------------|---------------------------------------------------------|
| Pens slot -> motor vaddr/bit/polarity, feedback vaddr/bits | `resolve_switch()`, `REGION_SLOT_*`          |
| Feedback state                       | `read_switch_state()`                                   |
| Panel input -> kind, target slot, polarity | `REGION_SVBSW_*` (`P` records)                   |
| Panel LED -> target slot, role       | `REGION_PLED_*` (`Q` records)                           |
| Dreieskive motor vaddr, pins, CW polarity | `ADDR_DREI_*`                                      |
| Mux select per access, I2C timeout   | `mcp_read_reg`/`mcp_write_reg`, `Wire.setWireTimeout`   |

## Design

### 1. Non-blocking serial

`loop()` today calls `readLine()`, which uses `Serial.readBytesUntil` with a
5 s timeout. With no PC that blocks for 5 s on every pass. Change: collect
characters from `Serial.available()` into a line buffer without waiting;
dispatch when `'\n'` arrives. Bulk commands (`HWU`, `SCU`) keep their
blocking `readLine()` inside the handler; the runtime just pauses while one
runs.

```cpp
void loop() {
    serviceSerial();               // non-blocking, dispatches complete lines
    if (g_runtimeOn) runtimeTick();
}
```

### 2. Polling (always, every 10 ms)

Each tick reads, once per chip and port:

- every panel input port in the `P` table (Fossli: 0x20 A, 0x21 A),
- every feedback port in the switch table (`SlotFeedbackVAddr`, port A;
  Fossli: the 2 SCB chips).

RAM keeps only the last debounced value of each read port. An event is a
debounced bit that differs from that value: the same level on 3 reads in a
row (~30 ms). That rides out contact bounce on the panel switches and on the
feedback contacts at arrival.

### 3. Events

No per-pens state is kept; the LEDs follow the feedback.

**Panel switch event** (`P` table, by kind):

- Pens (kind 0): `rett = (level == SvbSwPolarity)`. Motor bit =
  `rett ? SlotPolarity : 1 - SlotPolarity`. Read-modify-write `OLATB` on the
  motor chip, the same as `cmdSW`, without the wait. Nothing else.
- Dreieskive (kind 1): CW active -> CW, CCW active -> CCW, neither -> stop.
  Both active -> stop and report an error.

**Feedback event** (per pens slot, from its Rett/Avvik bits):

| New feedback (raw) | Owner notation | Action                                   |
|--------------------|----------------|------------------------------------------|
| BETWEEN (11)       | 00             | Left its position: both LEDs off         |
| RETT (10)          | 01             | Rett LED on, Avvik LED off               |
| AVVIK (01)         | 10             | Avvik LED on, Rett LED off               |
| FAULT (00)         | 11             | Error: both LEDs off, report             |

Results:

- Several pens can move at once; each one's events are independent.
- Switch flipped back mid-move: the motor bit flips back, the feedback
  returns to the old end, its LED comes back on.
- A fast pens that skips the BETWEEN reading still lights the right LED: the
  arrival event sets both LEDs.
- A pens moved by hand: the LEDs follow it. The motor is not driven back.

Trade-off of having no state: the firmware does not know where a pens was
told to go, so it cannot detect "never arrived" (timeout) or "arrived at the
wrong end". The operator sees it instead: both LEDs stay off, or the LED
does not match the panel switch (as in `stillverk.chatlog.md`, "Motor drive
vs feedback"). If those errors are wanted later, one byte per pens (the
requested end) and a start time are enough.

### 4. Panel LEDs

- Only the runtime writes the panel LED ports, so keep the last written byte
  per LED port in RAM and write a port only when its byte changes.
- On = low (active-low, all panel LEDs).
- SCB signal LEDs are not driven in version 1. When they are: on = high.

### 5. Boot

PROD mode, after `apply_all_board_hw()`:

1. Write all panel LED ports dark. (Boot clears OLAT to 0x00, which lights
   every active-low panel LED.)
2. Read every panel input and feedback port once. Treat every panel input
   as changed: each pens gets its panel position, and the dreieskive its
   switch position.
3. Treat every feedback as changed: pens already at an end light that LED at
   once; the rest light as they arrive (~5 s).

TEST mode: the runtime never runs (I2C is off).

### 6. Pausing for the config tool

New command `RT <0|1>`: `RT 0` stops the runtime (no panel reads, no motor
writes, no LED writes), `RT 1` starts it again. `RT` alone answers
`RT <0|1> <pens error mask, 8 hex digits> <dreieskive error 0|1>`. `SI` is
unchanged (the C# side matches its `MODE P` ending).

- The config tool sends `RT 0` right after connecting, so Operate and the
  bench tests don't fight the runtime. It sends `RT 1` on Quit.
- `RT 1` runs steps 2-3 of the boot (section 5): the panel decides where
  every pens goes. Pens moved in Operate go back to their panel position.
- If the PC disconnects without `RT 1`, the runtime stays stopped until
  reboot. Most Uno boards reset when the serial port opens or closes (DTR),
  so in practice it restarts anyway.

### 7. Errors

How to show an error is open (site owner decides later). Version 1 has two
errors: pens feedback FAULT (raw 00), and dreieskive CW + CCW both active.

- Pens in error: both LEDs off (the feedback event already does that).
- Print one line on serial, e.g. `ERR pens 3 fault`. No PC is usually
  attached, so this is for bench and debugging only.
- Keep a bit per pens and one for the dreieskive, set while the error is
  present; `RT` reports them and Status shows them. The bit clears on the
  next good feedback or switch reading.
- Error lines start with `!`, so the config tool prints them at once
  instead of taking them as a command reply.

### 8. Budget (ATmega328P)

- RAM: last debounced byte + debounce counter per read port (a handful),
  32 error bits, LED port bytes: well under 100 bytes. Tables stay in
  EEPROM and are read on use, the same as today.
- I2C at 100 kHz: a register read is ~0.3 ms. Fossli per 10 ms tick:
  2 panel + 2 feedback reads = ~1.2 ms, ~12 % bus load. Motor and LED writes
  only on events.

## Config tool changes (C#)

1. `RT 0` on connect, `RT 1` on Quit (`Program.cs`, `IArduinoDevice`).
2. Status (`StatusSession`): runtime on/off and current errors.
3. No LED polarity change: `PanelScanSession.LedLevel` (active-low) is
   correct for every panel.

## Tests

- C#: unit tests for the `RT` and error wire formats (NSubstitute, as the
  existing tests do).
- Integration (hardware, `[Explicit]`): `RT 0`/`RT 1` answer `OK`; `SI`
  reports the state.
- Bench, panel only: without the mux, motor vaddr 0x40 aliases the panel
  chip at 0x20 (`chatlogs/panel-bench-test.chatlog.md`). Remove the Venstre
  slot or fit the mux first. Then: flip a switch, check the motor `OLATB`
  with Bench test -> D.
- Layout with motor boards: switch -> old LED stays lit until the pens
  leaves -> old LED off -> new LED on after ~5 s. Move 2 switches at once ->
  both pens move. Flip a switch back mid-move -> the pens reverses and the
  old LED comes back. Short both feedback contacts -> error.

## Steps

Status 2026-11-05, branch `Phase2`: steps 1-5 implemented, compiled and
uploaded to the bench board; not yet run against the panel.

Implementation notes:

- Dreieskive inputs are assumed active-low (pull-up, switch to GND); there
  is no stored polarity for them. Marked `shortcut:` in `rt_panel_event`.
- Watch lists: up to 16 read ports and 8 LED ports (`RT_PORT_CAP`,
  `RT_LEDPORT_CAP`).
- The LED port byte starts from the chip's OLAT, so non-LED bits on an LED
  port are kept.
- C#: `RuntimeStatus` parses `RT`; `ArduinoConnection.ReconnectCommand`
  re-sends `RT 0` after a reconnect; Config -> A (restart) pauses again.

1. Firmware: non-blocking serial (`serviceSerial`), `RT` command, `SI`
   state. No behavior change while the runtime is off.
2. Firmware: polling + debounce, panel and feedback events, LEDs, boot
   sync.
3. Firmware: dreieskive.
4. Firmware: error reporting on serial and in `SI`.
5. C#: `RT` on connect/quit, status line.
6. Chatlog with the bench and layout results.

## Open questions

1. How should an error be shown on the panel? (Site owner will decide.)
2. Signal lamps and the green request. (Later.)
