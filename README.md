# Railroad Signal Controller – Design Specification

## State Transitions and Signal Behavior

This section describes how the controller transitions between states based on
inputs and events.

---

### Direction State

**Direction** defines which end of the block is permitted to show green.

| Direction | Meaning |
|----------|---------|
| **A → B** | **Signal A** is the active green end |
| **B → A** | **Signal B** is the active green end |
| **None**  | **Signal A** and **Signal B** is both Red |

Direction **None** is the default state.

---

### Input Types

| Input | Trigger Type | Active State |
|------|--------------|--------------|
| PBA | **Edge-triggered** | LOW (press) |
| PBB | **Edge-triggered** | LOW (press) |
| SCA | **Level-based** | LOW (Closed) |
| SCB | **Level-based** | LOW (Closed) |
| SCC | **Level-based** | LOW (Closed) |
| Train | **Level-based** | LOW (Train present) |

---

### Direction State Transitions

Direction changes **only on button press edges** and only when **no train is present**, and only if the  direction is not "None".

The possible directions are:

**A → B**
**B → A**
**None**

| Current Direction | Train present | Button event | Next Direction |
| ------------------ | --------------- | -------------- | ---------------- |
| None | No | **PBA pressed** | **A → B** |
| None | No | **PBB pressed** | **B → A** |
| Any | No | PBA or PBB pressed | No change |
| Any | Yes | PBA or PBB pressed| No change (stays None) |

Notes:

- PBA and PBB are evaluated on the **press edge only**
- Holding a button does **not** retrigger
- Pressing the button corresponding to the current Direction has no effect

Switches affects the direction signals

If any switch changes, then Direction is None thereafter. It does NOT change back.

---

### Signal Output Logic (Normal Operation)

| Condition | Signal A – Red | Signal A – Green 1 | Signal A – Green 2 | Signal B – Red | Signal B – Green 1 | Signal B – Green 2 |
|----------|----------------|-------------------|-------------------|----------------|-------------------|-------------------|
| Direction **None** (or Train present) | ON | OFF | OFF | ON | OFF | OFF |
| Direction **A → B**, SCB open | ON | OFF | OFF | ON | OFF | OFF |
| Direction **A → B**, SCB closed, SCA open | OFF | ON | OFF | ON | OFF | OFF |
| Direction **A → B**, SCB closed, SCA closed | OFF | ON | ON | ON | OFF | OFF |
| Direction **B → A**, SCB open | ON | OFF | OFF | ON | OFF | OFF |
| Direction **B → A**, SCB closed, SCC open | ON | OFF | OFF | OFF | ON | OFF |
| Direction **B → A**, SCB closed, SCC closed | ON | OFF | OFF | OFF | ON | ON |

---

### Debug Mode State Transitions

| Current Debug Mode | Debug button pressed | Next Debug Mode |
|-------------------|----------------------|-----------------|
| Off | Yes | AllOn |
| AllOn | Yes | Auto |
| Auto | Yes | Manual |
| Manual (step < last) | Yes | Manual (next step) |
| Manual (last step) | Yes | Off |

---

### Debug Cancellation

| Event | Effect |
|------|--------|
| PBA or PBB pressed while in debug | Debug mode exits immediately |
| Debug cancelled | Direction does **not** change on same press |

---

### Power-On / Reset State

| Condition at Power-On | Resulting State |
|----------------------|-----------------|
| Debug button not held | Direction restored from EEPROM |
| Debug button held | Direction forced to **None**, EEPROM reset |

---

### Summary Rules

- Direction is **explicitly set**, never toggled
- PBA always selects **A → B**
- PBB always selects **B → A**
- Pushbuttons are **edge-triggered - high -> low**
- Switches and Train detection are **level-based**
- Train presence overrides all other logic
- Debug modes override lamp outputs but **do not modify Direction**
- EEPROM reset always restores a **known, safe Direction**

---

## Controller

- Board: **Arduino Uno–compatible (GeekCreit)**
- MCU: **ATmega328P**
- Clock: **16 MHz**
- EEPROM: **1 KB (on-chip)**

---

## Power

- System supply: **+10–12 V**
- Logic supply: **Buck regulator → 5.0–5.1 V**
- Arduino powered via **5V pin**
- VIN not used
- Grounds common for logic and outputs

---

## Inputs (All via Optocouplers)

- Optocoupler: **PC817 / EL817**
- Input voltage: **10–12 V**
- LED series resistor: **3.3 kΩ**
- Typical LED current: **~2–3 mA**
- Arduino side: `INPUT_PULLUP`
- Logic level: **LOW = active**

### Input Functions and Pins

| Function | Arduino Pin | Board Label |
|--------|-------------|-------------|
| Train detect | D2 | 2 |
| Pushbutton A | D3 | 3 |
| Pushbutton B | D4 | 4 |
| Switch A closed | D5 | 5 |
| Switch B closed | D6 | 6 |
| Switch B closed | A3 | A3 |
| Debug button | A4 | A4 |
| Serial enable jumper | A5 | A5 |

---

## Outputs (Discrete Transistor Drivers)

- Topology: **NPN low-side switching**
- Transistor: **BC547 / BC337 / 2N2222**
- Base resistor: **4.7 kΩ**
- Base pull-down: **100 kΩ (base → emitter)**
- Load: **Signal LEDs**
- LED supply: **+10 V**
- LED current: **10 mA**

### LED Series Resistor

- Nominal value: **680 Ω**
- Acceptable range: **680–820 Ω**
- Power rating: **¼ W**

### Output Functions and Pins

| Signal | Arduino Pin | Board Label |
|------|-------------|-------------|
| Signal A – Red | D7 | 7 |
| Signal A – Green 1 | D9 | 9 |
| Signal A – Green 2 | D8 | 8 |
| Signal B – Red | D10 | 10 |
| Signal B – Green 1 | D12 | 12 |
| Signal B – Green 2 | D11 | 11 |

- Logic: **Arduino HIGH = LED ON**
- Hardware inversion handled by transistor

---

## Debug LEDs (5 V Logic)

- Active-high
- Direct drive from Arduino pin
- Resistor: **1 kΩ**
- LED current: **~3 mA**

---

## Debug Button Behavior

- Button connected to **A4 → GND**
- `INPUT_PULLUP`
- Active-low

### Debug State Sequence

1. ALL_ON (=1) **All signal LEDs ON**
2. CYCLE (=2) **Automatic cycle** (1 s per LED):  
   A_G1 → A_G2 → A_R → B_G1 → B_G2 → B_R
3. MAN_0 (=3), MAN_1 (=4) ... MAN_5 (=8) **Manual step** (one press per LED, same order)
4. OFF (=0) (When enter state, Flash all 3 times, 0.5 sec between, then exit debug) **Exit debug → normal operation**

- Any press of **signal button A or B**:
  - Cancels debug immediately
  - Does **not** toggle signal state on same press

---

## Normal Signal Logic

- Default Direction is None
  Both signals are Red
- Startup state:
  - Direction None
- Train present:
  - Direction None
- Pushbutton A or B:
  - Sets direction only if Direction is None
  - Ignored if train present
- Switch A closed :
  - Both Greens for A on.  
- Switch B and C closed:
  - Both Greens for B is on.

- When a Train is present, both signals go Red.
- When train is no more present, signals stay Red.

---

### Reset Mechanism

- Reset is performed using **power cycling**
- No dedicated reset jumper required

### Reset Steps

1. Power **OFF** the board using **JP2**
2. **Press and hold** the **Debug button (A4)**
3. Power **ON**
4. Release the Debug button after startup

### Reset Effect

- `g_stateQ` is forced to **false**
- Signal **A** becomes the active green end
- EEPROM address `0` is updated accordingly

---

## Serial Debug Output

- Baud rate: **115200**
- Status printed once per second
- Controlled by jumper on **A5**
  - Jumper installed (LOW): Serial enabled
  - Jumper removed (HIGH): Serial disabled

---

## Wiring Notes

- Input wire lengths: **2–5 m**
- Optocouplers required on all inputs
- Optional: **100 nF capacitor** across opto LED for noisy runs
- Avoid using pins **D0/D1**
- Pin **D13** unused (on-board LED available)

---
