#include <EEPROM.h>

struct Pins {
  // Inputs (active-low)
  static const uint8_t Train = 2;
  static const uint8_t PBA = 3;  // Pushbutton A
  static const uint8_t PBB = 4;  // Pushbutton B
  static const uint8_t SCA = 5;  // Switch A closed
  static const uint8_t SCB = 6;  // Switch B closed
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

  // Debug LEDs
  static const uint8_t LedQ = A0;
  static const uint8_t LedTrain = A1;
  static const uint8_t LedAGen = A2;
  static const uint8_t LedBGen = A3;
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

static const int EepromAddrStateQ = 0;

static bool g_stateQ = false;  // false => A green end, true => B green end
static uint8_t g_dbgState = DBG_OFF;
static uint8_t g_cycleStep = 0;      // for CYCLE mode LED stepping
static uint32_t g_dbgLast = 0;
static uint8_t g_flashCount = 6;     // for exit flash sequence (6 = complete/not flashing)
static bool g_flashOn = false;

static DebouncedActiveLow g_btnA;
static DebouncedActiveLow g_btnB;
static DebouncedActiveLow g_swA;
static DebouncedActiveLow g_swB;
static DebouncedActiveLow g_btnDbg;

static bool SerialEnabled() {
  return digitalRead(Pins::SerialEnable) == LOW;  // jumper to GND enables serial
}

static void LoadStateQ() {
  g_stateQ = EEPROM.read(EepromAddrStateQ) != 0;
}

static void SaveStateQ() {
  EEPROM.update(EepromAddrStateQ, g_stateQ ? 1 : 0);
}

static void ResetStateQToKnown() {
  g_stateQ = false;  // known state: A green end
  EEPROM.update(EepromAddrStateQ, 0);
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

static void ApplyNormalOutputs(bool train, bool scaClosed, bool scbClosed, bool q) {
  bool aGen = (!train) && (!q);
  bool bGen = (!train) && (q);

  WriteLamp(Pins::A_R, train || q);
  WriteLamp(Pins::B_R, train || (!q));
  WriteLamp(Pins::A_G1, aGen);
  WriteLamp(Pins::A_G2, aGen && scaClosed);
  WriteLamp(Pins::B_G1, bGen);
  WriteLamp(Pins::B_G2, bGen && scbClosed);
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

static void UpdateDebugLeds(bool train, bool q) {
  bool aGen = (!train) && (!q);
  bool bGen = (!train) && (q);

  digitalWrite(Pins::LedQ, q ? HIGH : LOW);
  digitalWrite(Pins::LedTrain, train ? HIGH : LOW);
  digitalWrite(Pins::LedAGen, aGen ? HIGH : LOW);
  digitalWrite(Pins::LedBGen, bGen ? HIGH : LOW);
}

static void PrintStatus(bool train, bool scaClosed, bool scbClosed, bool q) {
  static uint32_t last = 0;
  if (!SerialEnabled()) return;
  if (millis() - last < 1000) return;
  last = millis();

  Serial.print("T=");
  Serial.print(train);
  Serial.print(" Q=");
  Serial.print(q ? 'B' : 'A');
  Serial.print(" SCA=");
  Serial.print(scaClosed);
  Serial.print(" SCB=");
  Serial.print(scbClosed);
  Serial.print(" DBG=");
  Serial.println(g_dbgState);
}

void setup() {
  pinMode(Pins::Debug, INPUT_PULLUP);
  pinMode(Pins::SerialEnable, INPUT_PULLUP);

  delay(5);
  bool debugHeldAtBoot = (digitalRead(Pins::Debug) == LOW);
  if (debugHeldAtBoot) ResetStateQToKnown();
  else LoadStateQ();

  if (SerialEnabled()) {
    Serial.begin(115200);
    Serial.println("Signal controller started");
  }

  pinMode(Pins::Train, INPUT_PULLUP);
  pinMode(Pins::PBA, INPUT_PULLUP);
  pinMode(Pins::PBB, INPUT_PULLUP);
  pinMode(Pins::SCA, INPUT_PULLUP);
  pinMode(Pins::SCB, INPUT_PULLUP);

  pinMode(Pins::A_R, OUTPUT);
  pinMode(Pins::A_G1, OUTPUT);
  pinMode(Pins::A_G2, OUTPUT);
  pinMode(Pins::B_R, OUTPUT);
  pinMode(Pins::B_G1, OUTPUT);
  pinMode(Pins::B_G2, OUTPUT);

  pinMode(Pins::LedQ, OUTPUT);
  pinMode(Pins::LedTrain, OUTPUT);
  pinMode(Pins::LedAGen, OUTPUT);
  pinMode(Pins::LedBGen, OUTPUT);

  g_btnA.Begin(Pins::PBA);
  g_btnB.Begin(Pins::PBB);
  g_swA.Begin(Pins::SCA);
  g_swB.Begin(Pins::SCB);
  g_btnDbg.Begin(Pins::Debug);
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
      UpdateDebugLeds(train, g_stateQ);
      return;  // still flashing
    }
  }

  // Cancel debug mode if signal button pressed
  if (anySignalPressed && g_dbgState != DBG_OFF) {
    g_dbgState = DBG_OFF;
    g_flashCount = 6;  // skip flash sequence on cancel
    SetAllLamps(false);
    UpdateDebugLeds(train, g_stateQ);
    return;  // cancel debug only; do not toggle direction on same press
  }

  // Advance debug state on debug button press
  if (dbgPressed) {
    AdvanceDebugState();
  }

  // Run debug mode if active
  if (g_dbgState != DBG_OFF) {
    RunDebug(now);
    UpdateDebugLeds(train, g_stateQ);
    return;
  }

  // Normal operation
  if (!train) {
    if (pressedA && g_stateQ != false) {
      g_stateQ = false;  // Direction A -> B
      SaveStateQ();
    } else if (pressedB && g_stateQ != true) {
      g_stateQ = true;  // Direction B -> A
      SaveStateQ();
    }
  }

  bool scaClosed = g_swA.IsActive(DebounceMs);
  bool scbClosed = g_swB.IsActive(DebounceMs);

  ApplyNormalOutputs(train, scaClosed, scbClosed, g_stateQ);
  UpdateDebugLeds(train, g_stateQ);
  PrintStatus(train, scaClosed, scbClosed, g_stateQ);
}
