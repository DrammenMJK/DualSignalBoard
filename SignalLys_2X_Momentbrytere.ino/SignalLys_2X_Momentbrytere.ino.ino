struct Pins
{
  static const uint8_t Train = 2;   // opto -> LOW = occupied
  static const uint8_t PBA   = 3;   // opto -> LOW = pressed
  static const uint8_t PBB   = 4;   // opto -> LOW = pressed
  static const uint8_t SCA   = 5;   // opto -> LOW = Closed
  static const uint8_t SCB   = 6;   // opto -> LOW = Closed

  static const uint8_t A_R  = 8;
  static const uint8_t A_G1 = 9;
  static const uint8_t A_G2 = 10;
  static const uint8_t B_R  = 11;
  static const uint8_t B_G1 = 12;
  static const uint8_t B_G2 = 13;
};

class DebouncedActiveLow
{
public:
  void Begin(uint8_t pin)
  {
    _pin = pin;
    _stable = ReadRaw();
    _lastStable = _stable;
    _changedAt = millis();
  }

  bool PressedEvent(uint32_t debounceMs)
  {
    bool raw = ReadRaw();
    if (raw != _stable)
    {
      _stable = raw;
      _changedAt = millis();
    }

    if ((millis() - _changedAt) < debounceMs)
      return false;

    bool now = _stable;
    bool was = _lastStable;
    _lastStable = now;

    // active-low: pressed means "false"
    return (was == true && now == false);
  }

  bool IsActive(uint32_t debounceMs)
  {
    bool raw = ReadRaw();
    if (raw != _stable)
    {
      _stable = raw;
      _changedAt = millis();
    }

    if ((millis() - _changedAt) < debounceMs)
      return !raw; // best guess during transition

    return !_stable; // active-low
  }

private:
  bool ReadRaw() const { return digitalRead(_pin) == HIGH; }

  uint8_t _pin = 0;
  bool _stable = true;
  bool _lastStable = true;
  uint32_t _changedAt = 0;
};

static bool g_stateQ = false; // false => A green end, true => B green end
static DebouncedActiveLow g_btnA;
static DebouncedActiveLow g_btnB;
static DebouncedActiveLow g_swA;
static DebouncedActiveLow g_swB;

static void WriteLamp(uint8_t pin, bool on)
{
  digitalWrite(pin, on ? HIGH : LOW); // HIGH -> ULN sinks -> lamp ON
}

static void ApplyOutputs(bool train, bool scaClosed, bool scbClosed, bool q)
{
  bool aGen = (!train) && (!q);
  bool bGen = (!train) && ( q);

  WriteLamp(Pins::A_R,  train || q);
  WriteLamp(Pins::B_R,  train || (!q));
  WriteLamp(Pins::A_G1, aGen);
  WriteLamp(Pins::A_G2, aGen && scaClosed);
  WriteLamp(Pins::B_G1, bGen);
  WriteLamp(Pins::B_G2, bGen && scbClosed);
}

void setup()
{
  pinMode(Pins::Train, INPUT_PULLUP);
  pinMode(Pins::PBA,   INPUT_PULLUP);
  pinMode(Pins::PBB,   INPUT_PULLUP);
  pinMode(Pins::SCA,   INPUT_PULLUP);
  pinMode(Pins::SCB,   INPUT_PULLUP);

  pinMode(Pins::A_R,  OUTPUT);
  pinMode(Pins::A_G1, OUTPUT);
  pinMode(Pins::A_G2, OUTPUT);
  pinMode(Pins::B_R,  OUTPUT);
  pinMode(Pins::B_G1, OUTPUT);
  pinMode(Pins::B_G2, OUTPUT);

  g_btnA.Begin(Pins::PBA);
  g_btnB.Begin(Pins::PBB);
  g_swA.Begin(Pins::SCA);
  g_swB.Begin(Pins::SCB);

  Serial.begin(115200);

}

static void PrintStatus(bool train, bool scaClosed, bool scbClosed, bool q)
{
  static uint32_t last = 0;
  if (millis() - last < 1000) return;
  last = millis();

  bool aGen = (!train) && (!q);
  bool bGen = (!train) && ( q);

  bool aR  = train || q;
  bool bR  = train || (!q);
  bool aG1 = aGen;
  bool aG2 = aGen && scaClosed;
  bool bG1 = bGen;
  bool bG2 = bGen && scbClosed;

  Serial.print("T="); Serial.print(train);
  Serial.print(" Q="); Serial.print(q ? 'B' : 'A');
  Serial.print(" SCA="); Serial.print(scaClosed);
  Serial.print(" SCB="); Serial.print(scbClosed);
  Serial.print(" A[G1="); Serial.print(aG1);
  Serial.print(" G2="); Serial.print(aG2);
  Serial.print(" R=");  Serial.print(aR);
  Serial.print("] B[G1="); Serial.print(bG1);
  Serial.print(" G2="); Serial.print(bG2);
  Serial.print(" R=");  Serial.print(bR);
  Serial.println("]");
}



void loop()
{
  const uint32_t DebounceMs = 25;

  bool train = digitalRead(Pins::Train) == LOW; // active-low via opto
  bool pressed = g_btnA.PressedEvent(DebounceMs) || g_btnB.PressedEvent(DebounceMs);

  if (pressed && !train)
    g_stateQ = !g_stateQ;

  bool scaClosed = g_swA.IsActive(DebounceMs);
  bool scbClosed = g_swB.IsActive(DebounceMs);

  ApplyOutputs(train, scaClosed, scbClosed, g_stateQ);
  PrintStatus(train, scaClosed, scbClosed, g_stateQ);

}
