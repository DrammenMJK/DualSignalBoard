# Debug log — Firmware boot I2C hang (2026-09-10)

## Symptom

After uploading `Firmware/Firmware.ino` to the bench Arduino (Uno-compatible,
ATmega328P, on COM3) and re-running the hardware-in-the-loop test:

```
dotnet test DrammenMJKConfig.IntegrationTests --filter "FullyQualifiedName~StatusSessionIntegrationTests"
```

all three tests reported:

```
No Arduino responded on any available COM port -- connect the board and retry.
```

`ArduinoTestHelper.Connect()` probes every COM port, opens it, waits for boot,
then expects `PING` -> `PONG` and `SI` -> `SITE 1`. Got nothing, on every port,
at every baud tried (115200 / 57600 / 9600), even after forcing a DTR/RTS
reset pulse manually.

## Diagnosis

1. Confirmed the upload itself was good: `arduino-cli upload --verify` reported
   `14008 bytes of flash verified`, device signature `1E 95 0F` (ATmega328P).
2. Confirmed the serial/USB path was healthy by flashing a **throwaway
   diagnostic sketch** (`Serial.begin(115200)` + print a banner + dump EEPROM)
   — it talked immediately. So the bootloader and UART were fine; the
   problem was specific to `Firmware.ino`'s own `setup()`.
3. The diagnostic sketch's EEPROM dump showed the board's EEPROM already held
   **valid, current-layout config** for this firmware: `BoardVAddr[0..1] =
   0x20, 0x21` (two signal boards), plus matching IODIR/GPPU, signal-lamp,
   inverter-enable and one switch slot — consistent with the
   `01d2608 Oppdatert med 2nd kort, Fossli Venstre` commit. Not stale data
   from the old firmware layout.
4. `Firmware.ino`'s `setup()` calls `apply_all_board_hw()`, which does
   `Wire.beginTransmission()/endTransmission()` to every board vaddr found in
   that EEPROM table (0x20 and 0x21 here) **before** printing its boot banner
   or entering the command loop.
5. The classic AVR `Wire` library has **no timeout** on its internal
   `while (!(TWCR & _BV(TWINT)))` waits. If the I2C bus never reaches the
   expected state — no pull-ups so SDA/SCL never rise, a device not present,
   or a device holding the clock low — `endTransmission()` spins forever.
   With two boards declared and no I2C hardware actually answering on the
   bench bus at the time, `setup()` hung on the very first transaction and
   never returned, so the firmware never printed anything and never answered
   `PING`.

This explains all three symptoms at once: avrdude (bootloader-only, no I2C)
worked, the throwaway sketch (no I2C) worked, and `Firmware.ino` (I2C at
boot, non-empty board table) hung.

## Fix

`Firmware/Firmware.ino`, `setup()` — one line after `Wire.begin()`:

```cpp
Wire.begin();
// Bound every TWI wait: without this the AVR Wire lib spins forever on a
// bus fault (no pull-ups / no device / SDA or SCL stuck low), so a single
// configured-but-absent board would hang the whole firmware here at boot,
// before the command loop ever runs. On timeout endTransmission() returns
// non-zero, which mcp_*_reg() already reports as "not OK" and
// apply_board_hw() already tolerates -- bring-up just skips the missing board.
Wire.setWireTimeout(3000 /* us */, true /* reset TWI HW on timeout */);
apply_all_board_hw();
```

`Wire.setWireTimeout()` is available in the installed `arduino:avr` core
(1.8.7). `mcp_write_reg()`/`mcp_read_reg()` already treat a non-zero
`endTransmission()` return as "not OK", and `apply_board_hw()` already
ignores that per-board failure — so a timed-out board is simply skipped
instead of hanging everything downstream. EEPROM config was not touched.

Verified after re-flash: boots and answers immediately —

```
!LysKontroll Firmware Phase 1
!Commands: PING SI ER EW EC MDIR MPU MW MR MRR MPOLL MBIT HWU HWD SW SR SCU SCD RST
PING -> PONG
SI   -> SITE 1
```

## Side fix found by the same test run

`DrammenMJKConfig.IntegrationTests/StatusSessionIntegrationTests.cs`,
`SystemConfigDownload_ReturnsWellFormedLines`: the line-tag regex was
`^[SPDLF] `, but `Firmware.ino`'s `cmdSCD()` also emits `G` (signal lamp
group), `V` (inverter-enable) and `T` (track detection) lines — tags added
to the protocol after this test's regex was written. The board's own `G 21
04 02 00` line (signal config for vaddr 0x21) is what exposed it. Widened to
`^[SPDLGVT] `.

Result: `StatusSessionIntegrationTests` 3/3 pass, `DrammenMJKConfig.Tests`
51/51 pass.

## Bus-check tooling (already exists)

No need to add anything to re-check the I2C bus — `DrammenMJKConfig`
already has it:

**Config mode (`C`) -> `B` "Bench test" -> `W` "Sweep 0x20-0x27"**
(or `P` "Probe a chip" for one address). On this Phase-1 wiring (bus 0, no
mux) the virtual address you type *is* the real I2C address, so enter `20`,
`21`. Before the `setWireTimeout` fix this sweep would have hung the
firmware the same way boot did (it calls `MR`, which hits the same
`endTransmission()`); now a missing address returns "no response" in ~3 ms.

Not yet built (proposed, not done — ask if wanted):
- a firmware `I2C`/`SCAN` command that address-probes `0x20`-`0x27` and
  replies with one line (e.g. `I2C 20 21`) instead of the PC looping `MR`
  eight times;
- an automatic scan at boot, logged as a `!I2C: 20 21` / `!I2C: none` status
  line, so every connect reports bus state without a manual sweep.

## Open question — external board's "command received" LED

Observation: the external signal board has an LED that stays on until it has
received a command from the Arduino, and it was observed **off** even while
the firmware was (at the time) hanging at boot with no I2C reaching it.
Flagged as strange, possibly a broken/misremembered LED.

Reasoning on why "LED off" doesn't actually prove the bus was alive during
the hang:
- A true "bus never reaches a valid state" hang (no pull-ups, SDA/SCL never
  rise) means the MCP never saw a valid START or any clocked bits at all —
  LED should stay **on** in that case. LED being off argues against that
  being the *whole* story.
- More likely explanation: the LED reflects an **earlier successful
  session**. The EEPROM already holds real config for 0x20/0x21 from the
  "2nd kort, Fossli Venstre" work, which could only have been written by
  Motor/Signal Scan talking to those chips over I2C successfully at some
  point. If the board latches "command received -> LED off" until it loses
  power, the LED is just showing that old state, not anything from the
  hung boot.
- Also possible: the bus/chips are fine and the hang was transient (a chip
  clock-stretching / holding SCL low, a marginal pull-up, or the boards
  briefly unpowered while reflashing) — a hung slave produces the same
  infinite `TWINT` wait as missing pull-ups, not just missing hardware.
- Or the LED is simply dead / wired other than remembered.

**Not resolved yet.** Next step: run the Bench-test sweep now that it can't
hang the firmware, and read that as ground truth over the LED:
- `0x20`/`0x21` ACK -> bus and chips are genuinely fine; the boot hang was
  transient and `setWireTimeout` is now just the safety net.
- nothing ACKs -> real bus/wiring fault. Check in order: I2C pull-ups
  (4.7k(ohm) SDA/SCL -> +5V; the Uno's internal ~20-50k(ohm) are often too
  weak for a ribbon cable), GND continuity Uno<->board, A4=SDA/A5=SCL
  wiring, the MCP23017 address-strap pins (A0-A2) actually matching
  0x20/0x21, and that the external board has power.

## Reference

- `arduino-cli` is not on PATH; use the copy bundled with Arduino IDE:
  `C:\Users\TerjeSandstrom\AppData\Local\Programs\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe`
  (v1.5.1, `arduino:avr` core 1.8.7).
- Board FQBN: `arduino:avr:uno` (clone reports as "Unknown" to `board list`,
  must be given explicitly).
- Compile: `arduino-cli compile --fqbn arduino:avr:uno ./Firmware`
- Flash (verified): `arduino-cli upload -p COM3 --fqbn arduino:avr:uno --verify ./Firmware`
  (default upload is no-verify; add `--verbose` to see avrdude's own output).
- Run the config tool: `cd DrammenMJKConfig/DrammenMJKConfig && dotnet run`
  (auto-probes COM ports with the same `PING`/`SI` handshake as the
  integration test).
