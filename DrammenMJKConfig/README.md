# DrammenMJKConfig

Console tool for configuring the LysKontroll Arduino controller over a serial connection.
Run it on a PC connected to the Arduino via USB. The program stores all configuration
in the Arduino's EEPROM and does not need to run during normal layout operation.

---

## Requirements

- .NET 8 runtime
- Arduino connected via USB (CH340 or genuine USB)
- Baud rate: 115200

---

## Starting the program

```txt
DrammenMJKConfig.exe
```

or

```txt
dotnet run
```

The program lists available COM ports. If only one is present it connects automatically;
otherwise you are prompted to choose. After connecting it waits 2 seconds for the Arduino
to finish its reset, then shows the main menu.

```txt
D = Debug mode    C = Config mode    V = Verify    Q = Quit
```

---

## Debug mode (D)

Used to check LED wiring before configuration.

| Key | Action |
|-----|--------|
| A | Toggle all LEDs on / off — reveals short circuits |
| L | Cycle all LEDs one at a time, 1 second each (any key to stop) |
| Q | Exit debug mode |

---

## Config mode (C)

Stores hardware mappings in EEPROM. Run each command once during initial setup,
or again whenever the hardware changes.

**Recommended first-time order:** 1 → 2 → 3 → M → D → 4

### Command 1 — Motor scan

Discovers all switch motors in a single scan. Each motor pair is driven in turn:
- **Feedback changes** → Pens motor — operator confirms and sets Rett/Avvik polarity.
- **Timeout, no feedback change** → Dreieskive — operator confirms and sets CW/CCW polarity.

| Key | Action |
|-----|--------|
| Y | Confirm / correct detection |
| N | Skip to next motor |
| R | Pens is currently at Rett position |
| A | Pens is currently at Avvik position |
| 1 | The `01` bit pattern drives CW (Dreieskive polarity) |
| 2 | The `10` bit pattern drives CW (Dreieskive polarity) |
| X | Emergency stop all motors |
| Q | Done, return to config menu |

### Command 2 — Manual switch → Pens mapping

Links each physical panel switch (B–I) to the corresponding Pens motor and feedback inputs.

1. Move **all** panel switches to Rett before starting.
2. Select a Pens letter (B–I).
3. Flip its panel switch to Avvik and back; the Arduino detects which input changed.
4. Confirm or retry, then set polarity.

| Key | Action |
|-----|--------|
| B–I | Select a Pens to configure |
| Y | Confirm detected switch |
| N | Retry |
| R | Set polarity: this position is Rett |
| A | Set polarity: this position is Avvik |
| Q | Done |

### Command 3 — LED mapping

Maps each Pens (B–I) to its physical Rett and Avvik indicator LEDs on the panel.
All Penser should be at Rett before starting (run Command 2 first).

| Key | Action |
|-----|--------|
| B–I | Select Pens to configure |
| N | Next LED |
| P | Previous LED |
| S | Save this LED for current switch position (Rett first, then Avvik) |
| Q | Done |

### Command M — Moment button

Detects which input pin the physical moment signal button is connected to.
Press the button when prompted. Confirm with Y or retry with N.

### Command D — Dreieskive switch

Configures the three-position turntable switch (middle / CW / CCW).

1. Move the switch to the **middle** position and press Enter.
2. Move to **CW** when prompted.
3. Move back to middle, then to **CCW**.
4. Confirm each step with Y.

### Command 4 — LED routing matrix

Controls which switch conditions must be satisfied before each indicator LED lights.
Configuration is loaded from a site-specific JSON file and uploaded to EEPROM.
See [RoutingMatrix.md](RoutingMatrix.md) for the file format and a complete Fossli example.

```
Routing> L   Load JSON file and upload to Arduino
Routing> S   Download current matrix from Arduino and save to JSON file
Routing> ?   Show JSON file format and example
Routing> Q   Done
```

When loading, the program:
1. Prompts for the path to a `.json` file (any name, one per site).
2. Displays the `site` label from the file so you can confirm it is the right one.
3. Validates all conditions and reports every error before asking to upload.
4. Sends the matrix to Arduino and confirms each LED row is accepted.

### Command R — Reset

**Destructive.** Erases all configuration from EEPROM (motor mappings, switch mappings,
LED mappings, polarity, routing matrix). The layout signal direction state is not affected.
Requires typing Y to confirm.

After a reset, run Commands 1, 2, 3, M, D and 4 to reconfigure.

---

## Verify mode (V)

Checks that the hardware still works correctly using the stored configuration.
Nothing is written to EEPROM.

### Command 1 — Motors and Pens feedback

For each configured Pens the Arduino:
1. Reads current position from feedback switches.
2. Moves the motor to the opposite position and confirms feedback changes.
3. Moves back and confirms again.
4. Reports PASS or FAIL.

The Dreieskive motor is skipped (no feedback switches).

| Key | Action |
|-----|--------|
| X | Emergency stop all motors |
| Q | Abort and return to verify menu |

---

## Site-specific files

The LED routing matrix is the only configuration stored outside the Arduino.
Keep one JSON file per physical layout:

| Site | File |
|------|------|
| Fossli | `Fossli.json` |
| Other site | `Sentrum.json`, etc. |

Pass the full path when prompted by Command 4 → L. The file may be stored anywhere.
See [RoutingMatrix.md](RoutingMatrix.md) for the format specification.
