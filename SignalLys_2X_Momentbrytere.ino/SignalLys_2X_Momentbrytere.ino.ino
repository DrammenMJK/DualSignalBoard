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

enum class DebugMode : uint8_t { Off,
                                 AllOn,
                                 Auto,
                                 Manual };

static const int EepromAddrStateQ = 0;

static bool g_stateQ = false;  // false => A green end, true => B green end
static DebugMode g_dbgMode = DebugMode::Off;
static uint8_t g_dbgStep = 0;
static uint32_t g_dbgLast = 0;

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

static void AdvanceDebugMode() {
  if (g_dbgMode == DebugMode::Off) {
    g_dbgMode = DebugMode::AllOn;
    g_dbgStep = 0;
    return;
  }
  if (g_dbgMode == DebugMode::AllOn) {
    g_dbgMode = DebugMode::Auto;
    g_dbgStep = 0;
    g_dbgLast = millis();
    return;
  }
  if (g_dbgMode == DebugMode::Auto) {
    g_dbgMode = DebugMode::Manual;
    g_dbgStep = 0;
    return;
  }
  g_dbgMode = DebugMode::Off;
  g_dbgStep = 0;
}

static void RunDebug(uint32_t now, bool dbgPressed) {
  if (g_dbgMode == DebugMode::AllOn) {
    SetAllLamps(true);
    return;
  }

  if (g_dbgMode == DebugMode::Auto) {
    if (now - g_dbgLast >= 1000) {
      g_dbgLast = now;
      g_dbgStep = (g_dbgStep + 1) % 6;
    }
    SetOneLampByStep(g_dbgStep);
    return;
  }

  if (g_dbgMode == DebugMode::Manual) {
    if (dbgPressed) {
      g_dbgStep++;
      if (g_dbgStep >= 6) {
        g_dbgMode = DebugMode::Off;
        g_dbgStep = 0;
        SetAllLamps(false);
        return;
      }
    }
    SetOneLampByStep(g_dbgStep);
  }
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
  Serial.print((int)g_dbgMode);
  Serial.print(" STEP=");
  Serial.println(g_dbgStep);
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
  if (dbgPressed) AdvanceDebugMode();

  bool pressedA = g_btnA.PressedEvent(DebounceMs);
  bool pressedB = g_btnB.PressedEvent(DebounceMs);
  bool anySignalPressed = pressedA || pressedB;

  if (anySignalPressed && g_dbgMode != DebugMode::Off) {
    g_dbgMode = DebugMode::Off;
    SetAllLamps(false);
    return;  // cancel debug only; do not toggle direction on same press
  }

  if (g_dbgMode != DebugMode::Off) {
    RunDebug(now, dbgPressed);
    UpdateDebugLeds(train, g_stateQ);
    return;
  }

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
