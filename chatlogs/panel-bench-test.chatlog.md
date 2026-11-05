# Fossli panel bench test -- LEDs, switches, config without motor boards

Setup: Fossli Stillverk panel (SVB) on the bench, Arduino over USB, no motor
boards (SCB) connected and no I2C mux fitted. Panel chips:

| Chip | Port A                         | Port B       |
|------|--------------------------------|--------------|
| 0x20 | A0-A7 switch inputs (in-pu)    | B0-B7 LEDs   |
| 0x21 | A0-A5 LEDs, A6-A7 switch inputs | B0-B7 LEDs  |

This matches hardware.json.

## Panel bench test (Config -> P)

`PanelBenchSession.cs`. Pick the panel board, then:

| Key | What it does                                                         |
|-----|----------------------------------------------------------------------|
| A   | All LEDs on, any key = all off                                       |
| L   | One LED at a time. Space/Right = next, Left = previous, Esc = done   |
| N   | Snake: each LED 1 s in turn (next lit before the previous goes off), any key = stop |
| S   | Live switch view, a new row on each change, `*` = changed pin        |

- Output and input pins come from the chip's live IODIR, the same as the
  panel scans (`PanelScanSession.Pins`), not from hardware.json.
- Switch view: the chip's hex address and the port/bit are column headers,
  printed once. Rows show only the levels. 1 = High, 0 = Low. With pull-ups,
  a closed switch reads 0.
- A level change on a pin that IODIR says is an output prints its own line:
  `Changed on a pin set as OUTPUT: <pin>`. Before this, such a change printed
  a row with no visible difference.
- GPIO reads the pin level whatever the direction is. A changing value does
  not prove the pin is an input.

## LED polarity

- Panel LEDs are active-low: on = low. `PanelScanSession.LedLevel(lit)`
  is the single place for this. The bench test and the panel LED scan both
  use it.
- SCB signal lamps (motor boards, Port B bits 0,2,4) are active-high:
  on = high. Signal scan and Operate -> Signals already drive them that way.
  Unchanged.
- The firmware sets OLAT = 0x00 on every chip at boot, the safe default for
  motor boards. On the panel that lights every LED at power-up. The LED scan
  now turns all LEDs off before it starts. The firmware is not changed: that
  would need a per-chip OLAT default.

## 0x20 A7 read as output -- address aliasing without the mux

Symptom: the switch view had no column for 0x20 A7, and one switch printed
rows with no visible change. Bench test -> D showed IODIRA = 0x7F. Writing
0xFF held.

Cause: virtual address = bus*0x10 + real address. The firmware ignores the
mux ACK on purpose, so with no mux fitted every bus reaches the same chips.
"Fossli Motors Venstre" (vaddr 0x40 = bus 2, real 0x20) is then the panel
chip. At boot `apply_board_hw` applies every EEPROM board in slot order: the
panel 0x20 gets IODIRA 0xFF, then Venstre 0x40 writes IODIRA 0x7F and GPPUA
0x7F (A7 = inverter-enable `out`) to the same chip.

- Bench workaround after each Arduino boot: Bench test (B), address `20`,
  port `A`: D -> `FF` (all inputs), U -> `FF` (all pull-ups).
- Uploading hardware.json again (Config -> H) does not fix it. HWU merges
  slots and never removes one, so 0x40 stays. Only Config -> R removes it,
  and that erases all EEPROM config.
- "Fossli Motors Hoyre" (0x44) maps to real 0x24. No panel chip is there,
  so it is harmless.
- Do not run Operate -> Fossli Motors Venstre on the bench: it drives motor
  bits on the panel chip.
- No conflict on the layout with the mux: 0x20 and 0x40 are different buses.
- Until A7 is an input again, do not run the LED tests (A, L, N). They drive
  every output pin, A7 too, and "LED off" drives it high. A closed switch to
  GND then shorts the pin past the 25 mA limit.
- A downloaded hardware.json also showed "Unknown board 0x24": the EEPROM
  has a board slot with vaddr 0x24. Not checked yet.

## Configuring the panel without motor boards

Already there, no new code needed. Neither scan needs the motor boards: pens
labels and table slots come from boards.json, and the firmware stores the
panel `P`/`Q` records without checking the motor switch table.

- Config -> 4, Panel LED scan: lights one LED at a time. Answer P (pens, then
  pick the pens, then R = Rett / A = Avvik), S (signal lamp: Red/Green1/
  Green2) or 0 (unused). Esc stops and offers to store the LEDs done so far.
  K on the next run keeps them and scans only the rest.
- Config -> 5, Panel switch scan: flip one switch, the changed input is
  detected. P = pens, then "is the switch now at Rett or Avvik?". Polarity is
  stored as the input level that means Rett. C/W = dreieskive CW/CCW,
  G = green request button. Do the A7 workaround first, or 0x20 A7 is skipped.
- Config -> 6: edit panel config to fix wrong answers.
- Scans 4 and 5 store to SystemConfig.json and EEPROM at the same time.

## EEPROM backup

- Config -> J, System Config Backup: D = backup (Arduino -> SystemConfig.json),
  U = restore (SystemConfig.json -> Arduino). It covers the switch table,
  panel switches and LEDs, signals and dreieskive.
- Config -> H: chip directions and pull-ups (hardware.json), same U/D keys.
