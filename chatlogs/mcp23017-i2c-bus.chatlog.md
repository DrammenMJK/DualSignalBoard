# MCP23017 boards on the switch panel -- decoupling, pull-ups and cabling

Setup: first switch panel has 4 MCP23017 breakout boards (SMD, AliExpress
item 1005009586841138). Cat6 cable, about 50 cm from the Arduino to a plug
in the middle of the panel, then about 10 cm to each pair of boards. A small
extra regulator with 10uF on its output sits about 10 cm from the boards.

## Decoupling

- Every MCP23017 needs a 100nF ceramic (X7R) from VDD (pin 9) to VSS
  (pin 10), within a few mm of the pins. The 10uF at the regulator 10 cm
  away is bulk capacitance, not decoupling: wire inductance stops it from
  supplying fast switching current.
- The breakout board has 2 small SMD caps. Verify with a multimeter
  (continuity, unpowered): pad to VCC + pad to GND means decoupling, and
  nothing needs adding.
- Keep the 10uF at the regulator. Add 1-10uF near the boards only if they
  drive LEDs or relays directly.
- MCP23017 current limits: 25mA per pin, 125mA total into VDD, 150mA total
  out of VSS.

## RESET

- RESET (pin 18) must be pulled high, or it gives random resets that look
  like a decoupling problem. Measured: the board has 10k to VCC. OK.

## I2C pull-ups

- Each board has 4k7 on SDA and SCL. 4 boards in parallel = 1.18k.
- I2C spec: max 3mA sink, so Rmin = (VCC - 0.4V) / 3mA.
  - 5V: Rmin 1.53k. 1.18k gives 4.2mA, slightly over spec (usually works,
    less noise margin).
  - 3.3V: Rmin 0.97k. 1.18k gives 2.5mA, within spec.
- Decision: remove the pull-ups from one board in each pair, so 2 sets
  remain (2.35k, 2.1mA at 5V). Look for a solder jumper before desoldering.
- The Arduino internal pull-ups (20-50k, enabled by Wire) are negligible.

## Bus capacitance and speed

- Cat6 is about 45-50 pF/m (typical, not measured on this cable). Total bus
  estimate: about 100-130pF including chips. I2C limit is 400pF.
- Rise time (0.85 x R x C): 2.35k gives about 0.25us, fine at 100kHz
  (limit 1us), marginal at 400kHz (limit 0.3us).
- Keep Wire at the default 100kHz. Do not call `Wire.setClock(400000)`.

## Cat6 pair assignment

| Pair | Wires     | Purpose                              |
|------|-----------|--------------------------------------|
| 1    | SDA + GND | Data with its own return             |
| 2    | SCL + GND | Clock with its own return            |
| 3    | VCC + GND | Power                                |
| 4    | Spare     | INT line, or extra VCC + GND         |

- Never put SDA and SCL in the same pair: clock edges couple into data.
- Tie all GND wires together at both ends. Panel GND must be common with
  the Arduino GND.
- Optional: MCP23017 INTA/INTB set to open-drain and mirrored can share one
  wire for all 4 boards, so the Arduino reads only on change instead of
  polling. Not checked against the firmware.
- 24AWG is about 0.08 ohm/m; voltage drop over 0.5m is negligible at tens of
  mA. Double VCC/GND if LEDs draw more.
- Each board needs a unique address via A0-A2: 0x20-0x23.

## Troubleshooting

- Random NACKs or garbage reads: scope SDA/SCL before changing anything.
  Rounded edges mean pull-ups too weak. Sharp edges with glitches mean
  crosstalk or a GND problem.
