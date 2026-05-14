#include <EEPROM.h>

// Uncomment to enable serial monitor output
#define SERIAL_MONITOR_ENABLED

enum Direction {
  DIR_NONE = 0,
  DIR_A_TO_B = 1,
  DIR_B_TO_A = 2
};

struct Pins {
  // Inputs (active-low)
  static const uint8_t Train = 2;
  static const uint8_t PBA = 3;  // Pushbutton A
  static const uint8_t PBB = 4;  // Pushbutton B
  static const uint8_t SCA = 5;  // Switch A closed
  static const uint8_t SCB = A2; // Switch B closed
  static const uint8_t SCC = 6;  // Switch C closed
  static const uint8_t SCD = A3; // Switch D closed
  static const uint8_t Debug = A4;
  static const uint8_t SerialEnable = A5;

  // Outputs – Signal A
  static const uint8_t A_R = 7;
  static const uint8_t A_G1 = 9;
  static const uint8_t A_G2 = 8;

  // Outputs – Signal B
  static const uint8_t B_R = 10;
  static const uint8_t B_G1 = 12;
  static const uint8_t B_G2 = 11;
};

class DebouncedActiveLow {
public:
  void Begin(uint8_t pin) {
    _pin = pin;
    _stable = ReadRaw();
    _lastStable = _stable;
    _changedAt = millis();
  }

  bool PressedEvent(uint32_t debounceMs, const char* debugName = nullptr) {
    bool raw = ReadRaw();
    if (raw != _stable) {
      _stable = raw;
      _changedAt = millis();
    }
    if ((millis() - _changedAt) < debounceMs) return false;

    bool now = _stable;
    bool was = _lastStable;

    // Only print when state actually changes (edge detection moment)
    if (now != was && debugName != nullptr) {
      Serial.print("EDGE DETECT ");
      Serial.print(debugName);
      Serial.print(": was=");
      Serial.print(was);
      Serial.print(" now=");
      Serial.print(now);
      Serial.print(" result=");
      Serial.println((was == true && now == false) ? "PRESS" : "release");
    }

    _lastStable = now;

    return (was == true && now == false);  // falling edge = press
  }

  bool IsActive(uint32_t debounceMs) {
    bool raw = ReadRaw();
    if (raw != _stable) {
      _stable = raw;
      _changedAt = millis();
    }
    if ((millis() - _changedAt) < debounceMs) return !raw;
    return !_stable;  // active-low
  }

private:
  bool ReadRaw() const {
    return digitalRead(_pin) == HIGH;
  }

  uint8_t _pin = 0;
  bool _stable = true;
  bool _lastStable = true;
  uint32_t _changedAt = 0;
};

// Debug states: 0=OFF, 1=ALL_ON, 2=CYCLE, 3=MAN_0, 4=MAN_1, 5=MAN_2, 6=MAN_3, 7=MAN_4, 8=MAN_5
static const uint8_t DBG_OFF = 0;
static const uint8_t DBG_ALL_ON = 1;
static const uint8_t DBG_CYCLE = 2;
static const uint8_t DBG_MAN_0 = 3;
static const uint8_t DBG_MAN_1 = 4;
static const uint8_t DBG_MAN_2 = 5;
static const uint8_t DBG_MAN_3 = 6;
static const uint8_t DBG_MAN_4 = 7;
static const uint8_t DBG_MAN_5 = 8;

static const int EepromAddrDirection = 0;

static Direction g_direction = DIR_NONE;  // Current direction state
static uint8_t g_dbgState = DBG_OFF;
static uint8_t g_cycleStep = 0;      // for CYCLE mode LED stepping
static uint32_t g_dbgLast = 0;
static uint8_t g_flashCount = 6;     // for exit flash sequence (6 = complete/not flashing)
static bool g_flashOn = false;

// Switch state tracking for change detection
static bool g_prevSCA = false;
static bool g_prevSCB = false;
static bool g_prevSCC = false;
static bool g_prevSCD = false;

// Train detection - IIR filter with hysteresis (Schmitt trigger) + delay
static int32_t g_trainAccum = 0;             // Accumulated "HIGH time" in ms
static uint32_t g_trainLastSample = 0;       // Last sample timestamp
static const int32_t TrainFilterMs = 20;     // Filter window ~20ms
static const int32_t TrainHighThresh = 12;   // 60% - must exceed to go HIGH
static const int32_t TrainLowThresh = 8;     // 40% - must drop below to go LOW
static bool g_trainFiltered = false;         // Filtered train state (after debounce)
static uint32_t g_trainDetectedAt = 0;       // When filtered state first went HIGH
static const uint32_t TrainDelayMs = 1000;   // 2 second delay before taking effect

static const uint32_t FadeMs      = 1000;
static const uint16_t PwmPeriodUs = 1000;
static const uint8_t  FadeSteps   = 60;

static DebouncedActiveLow g_btnA;
static DebouncedActiveLow g_btnB;
static DebouncedActiveLow g_swA;
static DebouncedActiveLow g_swB;
static DebouncedActiveLow g_swC;
static DebouncedActiveLow g_swD;
static DebouncedActiveLow g_btnDbg;


// EEPROM functions - kept for future use but not currently active
static void LoadDirection() {
  uint8_t val = EEPROM.read(EepromAddrDirection);
  if (val <= DIR_B_TO_A) {
    g_direction = static_cast<Direction>(val);
  } else {
    g_direction = DIR_NONE;
  }
}

static void SaveDirection() {
  EEPROM.update(EepromAddrDirection, static_cast<uint8_t>(g_direction));
}

static void ResetDirectionToKnown() {
  g_direction = DIR_NONE;
  EEPROM.update(EepromAddrDirection, DIR_NONE);
}

static void WriteLamp(uint8_t pin, bool on) {
  digitalWrite(pin, on ? HIGH : LOW);
}

static void SetAllLamps(bool on) {
  WriteLamp(Pins::A_R, on);
  WriteLamp(Pins::A_G1, on);
  WriteLamp(Pins::A_G2, on);
  WriteLamp(Pins::B_R, on);
  WriteLamp(Pins::B_G1, on);
  WriteLamp(Pins::B_G2, on);
}

static void SetOneLampByStep(uint8_t step) {
  SetAllLamps(false);
  switch (step % 6) {
    case 0: WriteLamp(Pins::A_G1, true); break;
    case 1: WriteLamp(Pins::A_G2, true); break;
    case 2: WriteLamp(Pins::A_R, true); break;
    case 3: WriteLamp(Pins::B_G1, true); break;
    case 4: WriteLamp(Pins::B_G2, true); break;
    default: WriteLamp(Pins::B_R, true); break;
  }
}

static void FadeGroup(uint8_t* pins, uint8_t count, bool fadeIn, uint32_t totalMs) {
  if (count == 0) return;
  uint32_t stepMs = totalMs / FadeSteps;
  for (uint8_t i = 0; i < FadeSteps; i++) {
    uint8_t duty = fadeIn
      ? (uint8_t)((uint32_t)i * 255 / (FadeSteps - 1))
      : (uint8_t)(255 - (uint32_t)i * 255 / (FadeSteps - 1));
    uint32_t deadline = millis() + stepMs;
    uint16_t highUs = (uint32_t)duty * PwmPeriodUs / 255;
    uint16_t lowUs  = PwmPeriodUs - highUs;
    while (millis() < deadline) {
      if (highUs > 0) {
        for (uint8_t j = 0; j < count; j++) digitalWrite(pins[j], HIGH);
        delayMicroseconds(highUs);
      }
      if (lowUs > 0) {
        for (uint8_t j = 0; j < count; j++) digitalWrite(pins[j], LOW);
        delayMicroseconds(lowUs);
      }
    }
  }
  for (uint8_t j = 0; j < count; j++)
    digitalWrite(pins[j], fadeIn ? HIGH : LOW);
}


static const uint8_t LampPins[6] = {
  Pins::A_R, Pins::A_G1, Pins::A_G2,
  Pins::B_R, Pins::B_G1, Pins::B_G2
};

static void ApplyNormalOutputs(bool scaClosed, bool scbClosed, bool sccClosed, bool scdClosed, Direction dir) {
  // Direction None → both Red
  if (dir == DIR_NONE) {
    WriteLamp(Pins::A_R, true);
    WriteLamp(Pins::A_G1, false);
    WriteLamp(Pins::A_G2, false);
    WriteLamp(Pins::B_R, true);
    WriteLamp(Pins::B_G1, false);
    WriteLamp(Pins::B_G2, false);
    return;
  }

  if (dir == DIR_A_TO_B) {
    // Signal A can only be green if SCB is closed (thrown)
    bool aCanBeGreen = scbClosed;
    WriteLamp(Pins::A_R, !aCanBeGreen);
    WriteLamp(Pins::A_G1, aCanBeGreen);
    WriteLamp(Pins::A_G2, aCanBeGreen && scaClosed);
    WriteLamp(Pins::B_R, true);
    WriteLamp(Pins::B_G1, false);
    WriteLamp(Pins::B_G2, false);
  } else { // DIR_B_TO_A
    // Signal B requires SCB closed for any green
    // Green2 requires SCB, SCC, and SCD all closed
    bool bCanBeGreen = scbClosed;
    WriteLamp(Pins::A_R, true);
    WriteLamp(Pins::A_G1, false);
    WriteLamp(Pins::A_G2, false);
    WriteLamp(Pins::B_R, !bCanBeGreen);
    WriteLamp(Pins::B_G1, bCanBeGreen);
    WriteLamp(Pins::B_G2, bCanBeGreen && sccClosed && scdClosed);
  }
}

static void ApplyNormalOutputsSlowly(bool scaClosed, bool scbClosed, bool sccClosed, bool scdClosed, Direction dir) {
  bool desired[6];
  if (dir == DIR_NONE) {
    desired[0] = true;  desired[1] = false; desired[2] = false;
    desired[3] = true;  desired[4] = false; desired[5] = false;
  } else if (dir == DIR_A_TO_B) {
    bool aGreen = scbClosed;
    desired[0] = !aGreen;
    desired[1] = aGreen;
    desired[2] = aGreen && scaClosed;
    desired[3] = true;  desired[4] = false; desired[5] = false;
  } else {
    bool bGreen = scbClosed;
    desired[0] = true;  desired[1] = false; desired[2] = false;
    desired[3] = !bGreen;
    desired[4] = bGreen;
    desired[5] = bGreen && sccClosed && scdClosed;
  }

  uint8_t toOff[6]; uint8_t offCount = 0;
  uint8_t toOn[6];  uint8_t onCount  = 0;
  for (uint8_t i = 0; i < 6; i++) {
    bool cur = (digitalRead(LampPins[i]) == HIGH);
    if (cur && !desired[i])  toOff[offCount++] = LampPins[i];
    if (!cur && desired[i])  toOn[onCount++]   = LampPins[i];
  }
  if (offCount == 0 && onCount == 0) return;

  FadeGroup(toOff, offCount, false, FadeMs);
  FadeGroup(toOn,  onCount,  true,  FadeMs);
}

static void AdvanceDebugState() {
  if (g_dbgState == DBG_MAN_5) {
    // Transition to OFF - start flash sequence
    g_dbgState = DBG_OFF;
    g_flashCount = 0;
    g_flashOn = true;
    g_dbgLast = millis();
    SetAllLamps(true);
  } else {
    g_dbgState++;
    if (g_dbgState == DBG_CYCLE) {
      g_cycleStep = 0;
      g_dbgLast = millis();
    }
  }
}

static void RunDebug(uint32_t now) {
  if (g_dbgState == DBG_ALL_ON) {
    SetAllLamps(true);
    return;
  }

  if (g_dbgState == DBG_CYCLE) {
    if (now - g_dbgLast >= 1000) {
      g_dbgLast = now;
      g_cycleStep = (g_cycleStep + 1) % 6;
    }
    SetOneLampByStep(g_cycleStep);
    return;
  }

  // MAN_0 to MAN_5: show one LED based on state (state 3 = step 0, state 8 = step 5)
  if (g_dbgState >= DBG_MAN_0 && g_dbgState <= DBG_MAN_5) {
    SetOneLampByStep(g_dbgState - DBG_MAN_0);
  }
}

// Returns true while flashing is in progress
static bool RunExitFlash(uint32_t now) {
  if (g_flashCount >= 6) return false;  // 3 on + 3 off = 6 transitions done

  if (now - g_dbgLast >= 500) {
    g_dbgLast = now;
    g_flashOn = !g_flashOn;
    g_flashCount++;
    SetAllLamps(g_flashOn);
  }
  return true;
}

#ifdef SERIAL_MONITOR_ENABLED
// Unified debug state tracking
struct DebugState {
  uint8_t pba, pbb, train;
  uint8_t sca, scb, scc, scd;
  Direction dir;
  uint32_t lastPrintTime;
  bool initialized;
};
static DebugState g_dbg = {0, 0, 0, 0, 0, 0, 0, DIR_NONE, 0, false};

static void PrintFullState(const char* reason) {
  Serial.println();
  Serial.print("=== ");
  Serial.print(reason);
  Serial.println(" ===");

  // Raw pin readings (0=LOW/active, 1=HIGH/inactive for active-low inputs)
  Serial.println("RAW PINS (0=LOW, 1=HIGH):");
  Serial.print("  Buttons: PBA(D3)=");
  Serial.print(digitalRead(Pins::PBA));
  Serial.print("  PBB(D4)=");
  Serial.println(digitalRead(Pins::PBB));

  Serial.print("  Train:   D2=");
  Serial.println(digitalRead(Pins::Train));

  Serial.print("  Switches: SCA(D5)=");
  Serial.print(digitalRead(Pins::SCA));
  Serial.print("  SCB(A2)=");
  Serial.print(digitalRead(Pins::SCB));
  Serial.print("  SCC(D6)=");
  Serial.print(digitalRead(Pins::SCC));
  Serial.print("  SCD(A3)=");
  Serial.println(digitalRead(Pins::SCD));

  // Debounced button states (important for edge detection)
  Serial.print("DEBOUNCED BUTTONS: PBA=");
  Serial.print(g_btnA.IsActive(25) ? "PRESSED" : "released");
  Serial.print("  PBB=");
  Serial.println(g_btnB.IsActive(25) ? "PRESSED" : "released");

  // Switches (raw is fine, they're stable)
  Serial.print("SWITCHES: A=");
  Serial.print(digitalRead(Pins::SCA) == LOW ? "CLOSED" : "open");
  Serial.print("  B=");
  Serial.print(digitalRead(Pins::SCB) == LOW ? "CLOSED" : "open");
  Serial.print("  C=");
  Serial.print(digitalRead(Pins::SCC) == LOW ? "CLOSED" : "open");
  Serial.print("  D=");
  Serial.println(digitalRead(Pins::SCD) == LOW ? "CLOSED" : "open");

  // Current state
  Serial.println("LOGIC STATE:");
  Serial.print("  Direction: ");
  if (g_direction == DIR_NONE) Serial.println("None");
  else if (g_direction == DIR_A_TO_B) Serial.println("A->B");
  else Serial.println("B->A");

  Serial.print("  Train present: ");
  Serial.println(digitalRead(Pins::Train) == HIGH ? "YES (raw)" : "no (raw)");
  Serial.print("Train accumulated : ");
  Serial.println(g_trainAccum);

  // Signal lamp outputs
  Serial.println("LAMP OUTPUTS:");
  Serial.print("  Signal A: R=");
  Serial.print(digitalRead(Pins::A_R) ? "ON" : "off");
  Serial.print("  G1=");
  Serial.print(digitalRead(Pins::A_G1) ? "ON" : "off");
  Serial.print("  G2=");
  Serial.println(digitalRead(Pins::A_G2) ? "ON" : "off");

  Serial.print("  Signal B: R=");
  Serial.print(digitalRead(Pins::B_R) ? "ON" : "off");
  Serial.print("  G1=");
  Serial.print(digitalRead(Pins::B_G1) ? "ON" : "off");
  Serial.print("  G2=");
  Serial.println(digitalRead(Pins::B_G2) ? "ON" : "off");
  Serial.println();
}

static void HandleSerialInput() {
  if (Serial.available() > 0) {
    char c = Serial.read();
    if (c == 's' || c == 'S') {
      PrintFullState("STATUS REQUEST");
    } else if (c == '?') {
      Serial.println();
      Serial.println("Commands: s=status, ?=help");
    }
  }
}

static void DebugCheckAndPrint(bool pressedA, bool pressedB) {
  HandleSerialInput();
  uint32_t now = millis();

  // Read current raw states
  uint8_t pba = digitalRead(Pins::PBA);
  uint8_t pbb = digitalRead(Pins::PBB);
  uint8_t train = digitalRead(Pins::Train);
  uint8_t sca = digitalRead(Pins::SCA);
  uint8_t scb = digitalRead(Pins::SCB);
  uint8_t scc = digitalRead(Pins::SCC);
  uint8_t scd = digitalRead(Pins::SCD);

  // Check for button press events
  if (pressedA) {
    PrintFullState("PBA PRESSED");
    g_dbg.lastPrintTime = now;
    return;
  }
  if (pressedB) {
    PrintFullState("PBB PRESSED");
    g_dbg.lastPrintTime = now;
    return;
  }

  // Check for direction change
  if (g_dbg.initialized && g_dbg.dir != g_direction) {
    PrintFullState("DIRECTION CHANGED");
    g_dbg.dir = g_direction;
    g_dbg.lastPrintTime = now;
    return;
  }

  // Check for any input state change
  bool changed = !g_dbg.initialized ||
                 pba != g_dbg.pba || pbb != g_dbg.pbb ||
                 train != g_dbg.train ||
                 sca != g_dbg.sca || scb != g_dbg.scb ||
                 scc != g_dbg.scc || scd != g_dbg.scd;

  // Print on change or every 60 seconds
  bool timeout = (now - g_dbg.lastPrintTime) >= 60000;

  if (changed) {
    PrintFullState("INPUT CHANGED");
  } else if (timeout) {
    PrintFullState("PERIODIC (60s)");
  } else {
    return;  // No print needed
  }

  // Update saved state
  g_dbg.pba = pba;
  g_dbg.pbb = pbb;
  g_dbg.train = train;
  g_dbg.sca = sca;
  g_dbg.scb = scb;
  g_dbg.scc = scc;
  g_dbg.scd = scd;
  g_dbg.dir = g_direction;
  g_dbg.lastPrintTime = now;
  g_dbg.initialized = true;
}
#endif

void setup() {
  pinMode(Pins::Debug, INPUT_PULLUP);
  pinMode(Pins::SerialEnable, INPUT_PULLUP);

  delay(5);
  bool debugHeldAtBoot = (digitalRead(Pins::Debug) == LOW);
  if (debugHeldAtBoot) {
    ResetDirectionToKnown();
  }
  // EEPROM loading disabled - direction starts as None
  // else LoadDirection();
  g_direction = DIR_NONE;

#ifdef SERIAL_MONITOR_ENABLED
  Serial.begin(115200);
  Serial.println("Signal controller started");
#endif

  pinMode(Pins::Train, INPUT_PULLUP);
  pinMode(Pins::PBA, INPUT_PULLUP);
  pinMode(Pins::PBB, INPUT_PULLUP);
  pinMode(Pins::SCA, INPUT_PULLUP);
  pinMode(Pins::SCB, INPUT_PULLUP);
  pinMode(Pins::SCC, INPUT_PULLUP);
  pinMode(Pins::SCD, INPUT_PULLUP);

  pinMode(Pins::A_R, OUTPUT);
  pinMode(Pins::A_G1, OUTPUT);
  pinMode(Pins::A_G2, OUTPUT);
  pinMode(Pins::B_R, OUTPUT);
  pinMode(Pins::B_G1, OUTPUT);
  pinMode(Pins::B_G2, OUTPUT);

  g_btnA.Begin(Pins::PBA);
  g_btnB.Begin(Pins::PBB);
  g_swA.Begin(Pins::SCA);
  g_swB.Begin(Pins::SCB);
  g_swC.Begin(Pins::SCC);
  g_swD.Begin(Pins::SCD);
  g_btnDbg.Begin(Pins::Debug);

  // Initialize previous switch states
  g_prevSCA = g_swA.IsActive(0);
  g_prevSCB = g_swB.IsActive(0);
  g_prevSCC = g_swC.IsActive(0);
  g_prevSCD = g_swD.IsActive(0);

  // Initialize train filter timestamp
  g_trainLastSample = millis();
}

void loop() {
  const uint32_t DebounceMs = 5;  // Reduced - hardware RC filter handles debounce
  uint32_t now = millis();

  // Train detection with IIR filter (leaky integrator) for debounce
  bool trainRawPin = (digitalRead(Pins::Train) == HIGH);
  uint32_t elapsed = now - g_trainLastSample;
  g_trainLastSample = now;

  // Leaky integrator: accumulate HIGH time, drain LOW time
  if (trainRawPin) {
    g_trainAccum += elapsed;
    if (g_trainAccum > TrainFilterMs) g_trainAccum = TrainFilterMs;
  } else {
    g_trainAccum -= elapsed;
    if (g_trainAccum < 0) g_trainAccum = 0;
  }

  // Schmitt trigger: hysteresis to prevent oscillation
  bool trainFiltered;
  if (g_trainFiltered) {
    // Currently HIGH - must drop below 40% to go LOW
    trainFiltered = (g_trainAccum >= TrainLowThresh);
  } else {
    // Currently LOW - must exceed 60% to go HIGH
    trainFiltered = (g_trainAccum > TrainHighThresh);
  }

  // Track when filtered state first goes HIGH (start 2-second delay timer)
  if (trainFiltered && !g_trainFiltered) {
    g_trainDetectedAt = now;
  }
  g_trainFiltered = trainFiltered;

  // Train is only "present" for logic after 2 seconds of continuous filtered detection
  bool train = g_trainFiltered && (now - g_trainDetectedAt >= TrainDelayMs);

  // Track raw button state for debug
#ifdef SERIAL_MONITOR_ENABLED
  static uint8_t lastRawPBA = 1;
  static uint8_t lastRawPBB = 1;
  uint8_t rawPBA = digitalRead(Pins::PBA);
  uint8_t rawPBB = digitalRead(Pins::PBB);
  if (rawPBA != lastRawPBA) {
    Serial.print("*** PBA RAW CHANGED: ");
    Serial.print(lastRawPBA);
    Serial.print(" -> ");
    Serial.println(rawPBA);
    lastRawPBA = rawPBA;
  }
  if (rawPBB != lastRawPBB) {
    Serial.print("*** PBB RAW CHANGED: ");
    Serial.print(lastRawPBB);
    Serial.print(" -> ");
    Serial.println(rawPBB);
    lastRawPBB = rawPBB;
  }
#endif

  bool dbgPressed = g_btnDbg.PressedEvent(DebounceMs, nullptr);
  bool pressedA = g_btnA.PressedEvent(DebounceMs, "PBA");
  bool pressedB = g_btnB.PressedEvent(DebounceMs, "PBB");
  bool anySignalPressed = pressedA || pressedB;

#ifdef SERIAL_MONITOR_ENABLED
  if (pressedA) Serial.println("*** PressedEvent: PBA = TRUE ***");
  if (pressedB) Serial.println("*** PressedEvent: PBB = TRUE ***");
  DebugCheckAndPrint(pressedA, pressedB);
#endif

  // Handle exit flash sequence (runs after MAN_5 -> OFF transition)
  if (g_dbgState == DBG_OFF && g_flashCount < 6) {
    if (RunExitFlash(now)) {
      return;  // still flashing
    }
  }

  // Cancel debug mode if signal button pressed
  if (anySignalPressed && g_dbgState != DBG_OFF) {
    g_dbgState = DBG_OFF;
    g_flashCount = 6;  // skip flash sequence on cancel
    SetAllLamps(false);
    return;  // cancel debug only; do not change direction on same press
  }

  // Advance debug state on debug button press
  if (dbgPressed) {
    AdvanceDebugState();
  }

  // Run debug mode if active
  if (g_dbgState != DBG_OFF) {
    RunDebug(now);
    return;
  }

  // Read switch states
  bool scaClosed = g_swA.IsActive(DebounceMs);
  bool scbClosed = g_swB.IsActive(DebounceMs);
  bool sccClosed = g_swC.IsActive(DebounceMs);
  bool scdClosed = g_swD.IsActive(DebounceMs);

  // Train present forces Direction to None
  if (train) {
    g_direction = DIR_NONE;
  }

  // Check for switch changes - any change forces Direction to None
  // Only check when direction is already set (not None)
  if (g_direction != DIR_NONE) {
    if (scaClosed != g_prevSCA || scbClosed != g_prevSCB || sccClosed != g_prevSCC || scdClosed != g_prevSCD) {
      g_direction = DIR_NONE;
    }
  }

  // Always update previous states before button handling
  g_prevSCA = scaClosed;
  g_prevSCB = scbClosed;
  g_prevSCC = sccClosed;
  g_prevSCD = scdClosed;

  // Normal operation - button presses only work when Direction is None and no train
  // Additionally, SCB must be closed for either direction to be set
#ifdef SERIAL_MONITOR_ENABLED
  if (pressedA || pressedB) {
    Serial.println();
    Serial.println(">>> BUTTON EVENT <<<");
    Serial.print("  pressedA=");
    Serial.print(pressedA);
    Serial.print("  pressedB=");
    Serial.println(pressedB);
    Serial.print("  Condition: !train=");
    Serial.print(!train);
    Serial.print("  dir==None=");
    Serial.print(g_direction == DIR_NONE);
    Serial.print("  scbClosed=");
    Serial.println(scbClosed);
    Serial.print("  ALL CONDITIONS MET: ");
    Serial.println((!train && g_direction == DIR_NONE && scbClosed) ? "YES" : "NO");
  }
#endif
  if (!train && g_direction == DIR_NONE && scbClosed) {
    if (pressedA) {
      g_direction = DIR_A_TO_B;
#ifdef SERIAL_MONITOR_ENABLED
      Serial.println("  -> Direction set to A->B");
#endif
    } else if (pressedB) {
      g_direction = DIR_B_TO_A;
#ifdef SERIAL_MONITOR_ENABLED
      Serial.println("  -> Direction set to B->A");
#endif
    }
  }

  ApplyNormalOutputsSlowly(scaClosed, scbClosed, sccClosed, scdClosed, g_direction);
}
