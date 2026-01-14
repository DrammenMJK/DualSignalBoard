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

  bool PressedEvent(uint32_t debounceMs) {
    bool raw = ReadRaw();
    if (raw != _stable) {
      _stable = raw;
      _changedAt = millis();
    }
    if ((millis() - _changedAt) < debounceMs) return false;

    bool now = _stable;
    bool was = _lastStable;
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
static void PrintStatus(bool train, bool scaClosed, bool scbClosed, bool sccClosed, bool scdClosed, Direction dir) {
  static uint32_t last = 0;
  if (millis() - last < 1000) return;
  last = millis();

  Serial.print("T=");
  Serial.print(train);
  Serial.print(" Dir=");
  if (dir == DIR_NONE) Serial.print("None");
  else if (dir == DIR_A_TO_B) Serial.print("A->B");
  else Serial.print("B->A");
  Serial.print(" SCA=");
  Serial.print(scaClosed);
  Serial.print(" SCB=");
  Serial.print(scbClosed);
  Serial.print(" SCC=");
  Serial.print(sccClosed);
  Serial.print(" SCD=");
  Serial.print(scdClosed);
  Serial.print(" DBG=");
  Serial.println(g_dbgState);
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
}

void loop() {
  const uint32_t DebounceMs = 25;
  uint32_t now = millis();

  bool train = (digitalRead(Pins::Train) == LOW);

  bool dbgPressed = g_btnDbg.PressedEvent(DebounceMs);
  bool pressedA = g_btnA.PressedEvent(DebounceMs);
  bool pressedB = g_btnB.PressedEvent(DebounceMs);
  bool anySignalPressed = pressedA || pressedB;

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
  if (!train && g_direction == DIR_NONE && scbClosed) {
    if (pressedA) {
      g_direction = DIR_A_TO_B;
      // SaveDirection();  // EEPROM disabled
    } else if (pressedB) {
      g_direction = DIR_B_TO_A;
      // SaveDirection();  // EEPROM disabled
    }
  }

  ApplyNormalOutputs(scaClosed, scbClosed, sccClosed, scdClosed, g_direction);
#ifdef SERIAL_MONITOR_ENABLED
  PrintStatus(train, scaClosed, scbClosed, sccClosed, scdClosed, g_direction);
#endif
}
