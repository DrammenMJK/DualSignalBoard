// Firmware.ino — Real hardware command dispatcher (Phase 4)
//
// Same line-based protocol as Simulator.ino.
// Hardware layer uses 4× MCP23017 I2C expanders and 2 Arduino direct pins.
//
// Board: Arduino Uno (ATmega328P), 115200 baud
//
// MCP23017 I2C addresses
//   U7 @ 0x20 — PORT B: INPUT  panel switches 0–7
//                PORT A: OUTPUT indicator LEDs  0–7  (B_Rett…E_Avvik)
//   U2 @ 0x21 — PORT B: OUTPUT indicator LEDs  8–15 (F_Rett…I_Avvik)
//                PORT A: INPUT  Dreieskive switch CW/CCW, moment button, spare
//   U1 @ 0x22 — PORT B: OUTPUT motor first-direction pins  pairs 0–7
//                PORT A: OUTPUT motor second-direction pins pairs 0–7
//   U5 @ 0x25 — PORT B: bit0,1 OUTPUT Dreieskive motor; bits2–7 INPUT feedback C,D,I
//                PORT A: INPUT  feedback E(0,1) F(2,3) G(4,5) H(6,7)
//
// Arduino direct pins used for Pens B feedback (freed from SCC/SCD by moving C,D to U5)
//   PENS_B_RETT_PIN   D6  (was SCC)
//   PENS_B_AVVIK_PIN  A3  (was SCD)
//
// IMPORTANT: I2C requires A4=SDA, A5=SCL.  The debug button must be on D13.

#include <EEPROM.h>
#include <Wire.h>
#include <avr/pgmspace.h>
#include <avr/wdt.h>

// ---------------------------------------------------------------------------
// Site configuration
// ---------------------------------------------------------------------------
#define FIRST_PENS       'B'
#define LAST_PENS        'I'
#define NUM_LED_OUTPUTS   16
#define NUM_PAIRS          9   // 8 Pens motors + 1 Dreieskive
#define DREIESKIVE_PAIR    8   // last pair; has no feedback

// ---------------------------------------------------------------------------
// EEPROM layout  (must match ArduinoDevice.cs constants)
// ---------------------------------------------------------------------------
#define EEPROM_MAGIC_ADDR   0x01
#define EEPROM_MAGIC_VALUE  0xA5
#define REGION1_BASE        0x10
#define REGION1_POL         0x30
#define REGION2_RETT        0x50
#define REGION2_AVVIK       0x70
#define REGION3_BASE        0x90
#define REGION4_RETT        0xB0
#define REGION4_AVVIK       0xD0
#define REGION5_MOTOR_PIN   0xF0
#define REGION5_MOTOR_POL   0xF1
#define REGION5_SW_CW       0xF2
#define REGION5_SW_CCW      0xF3
#define REGION5_MOMENT      0xF4
#define REGION6_BASE       0x100

#define LED_COUNT  ((LAST_PENS - FIRST_PENS + 1) * 2)
#define MAX_COND    4
#define STRIDE      (1 + MAX_COND * 2)
#define LED_NO_PIN  0xFE

// ---------------------------------------------------------------------------
// MCP23017 I2C register addresses
// ---------------------------------------------------------------------------
#define MCP_IODIRA  0x00
#define MCP_IODIRB  0x01
#define MCP_GPPUA   0x0C
#define MCP_GPPUB   0x0D
#define MCP_GPIOA   0x12
#define MCP_GPIOB   0x13
#define MCP_OLATA   0x14
#define MCP_OLATB   0x15

static const uint8_t CHIP_ADDR[4] PROGMEM = { 0x20, 0x21, 0x22, 0x25 };
#define CHIP_U7  0
#define CHIP_U2  1
#define CHIP_U1  2
#define CHIP_U5  3
#define PORT_A   0
#define PORT_B   1

// Pin encoding: bit7=1 → Arduino direct pin (bits6:0 = Arduino pin number)
//               bit7=0 → MCP23017 (bits6:5=chip 0-3, bit4=port A/B, bits3:0=bit 0-7)
#define MCP_ENC(chip,port,bit)  ((uint8_t)(((chip)<<5)|((port)<<4)|((bit)&0x0F)))
#define ARD_ENC(pin)            ((uint8_t)(0x80|(pin)))
#define IS_ARD(enc)             ((enc)&0x80)
#define ENC_CHIP(enc)           (((enc)>>5)&0x03)
#define ENC_PORT(enc)           (((enc)>>4)&0x01)
#define ENC_BIT(enc)            ((enc)&0x0F)

// ---------------------------------------------------------------------------
// Arduino direct-pin assignments
// ---------------------------------------------------------------------------
#define PENS_B_RETT_PIN   6    // D6  (freed from SCC)
#define PENS_B_AVVIK_PIN  17   // A3  (freed from SCD; A3 = analog 3 = digital 17)

// ---------------------------------------------------------------------------
// Hardware tables  (all PROGMEM, indexed by pair 0–8)
// ---------------------------------------------------------------------------

// Motor pair → first direction pin (U1 PORT B bits 0–7 for pairs 0–7; U5 PORT B bit 0 for pair 8)
static const uint8_t HW_MOTOR_A[NUM_PAIRS] PROGMEM = {
    MCP_ENC(CHIP_U1,PORT_B,0), MCP_ENC(CHIP_U1,PORT_B,1),
    MCP_ENC(CHIP_U1,PORT_B,2), MCP_ENC(CHIP_U1,PORT_B,3),
    MCP_ENC(CHIP_U1,PORT_B,4), MCP_ENC(CHIP_U1,PORT_B,5),
    MCP_ENC(CHIP_U1,PORT_B,6), MCP_ENC(CHIP_U1,PORT_B,7),
    MCP_ENC(CHIP_U5,PORT_B,0)   // Dreieskive
};

// Motor pair → second direction pin (U1 PORT A bits 0–7 for pairs 0–7; U5 PORT B bit 1 for pair 8)
static const uint8_t HW_MOTOR_B[NUM_PAIRS] PROGMEM = {
    MCP_ENC(CHIP_U1,PORT_A,0), MCP_ENC(CHIP_U1,PORT_A,1),
    MCP_ENC(CHIP_U1,PORT_A,2), MCP_ENC(CHIP_U1,PORT_A,3),
    MCP_ENC(CHIP_U1,PORT_A,4), MCP_ENC(CHIP_U1,PORT_A,5),
    MCP_ENC(CHIP_U1,PORT_A,6), MCP_ENC(CHIP_U1,PORT_A,7),
    MCP_ENC(CHIP_U5,PORT_B,1)   // Dreieskive
};

// LED outputs indexed 0–15 (B_Rett, B_Avvik, C_Rett, … I_Rett, I_Avvik)
// LEDs 0–7  → U7 PORT A bits 0–7
// LEDs 8–15 → U2 PORT B bits 0–7
static const uint8_t HW_LED[NUM_LED_OUTPUTS] PROGMEM = {
    MCP_ENC(CHIP_U7,PORT_A,0), MCP_ENC(CHIP_U7,PORT_A,1),
    MCP_ENC(CHIP_U7,PORT_A,2), MCP_ENC(CHIP_U7,PORT_A,3),
    MCP_ENC(CHIP_U7,PORT_A,4), MCP_ENC(CHIP_U7,PORT_A,5),
    MCP_ENC(CHIP_U7,PORT_A,6), MCP_ENC(CHIP_U7,PORT_A,7),
    MCP_ENC(CHIP_U2,PORT_B,0), MCP_ENC(CHIP_U2,PORT_B,1),
    MCP_ENC(CHIP_U2,PORT_B,2), MCP_ENC(CHIP_U2,PORT_B,3),
    MCP_ENC(CHIP_U2,PORT_B,4), MCP_ENC(CHIP_U2,PORT_B,5),
    MCP_ENC(CHIP_U2,PORT_B,6), MCP_ENC(CHIP_U2,PORT_B,7)
};

// All feedback input pins scanned by FW (active-low: LOW = reached)
// U5 PORT A 0–7: E_Rett, E_Avvik, F_Rett, F_Avvik, G_Rett, G_Avvik, H_Rett, H_Avvik
// U5 PORT B 2–7: I_Rett, I_Avvik, C_Rett, C_Avvik, D_Rett, D_Avvik
// Arduino: Pens B Rett (D6), Pens B Avvik (A3)
#define NUM_FEEDBACK_PINS 16
static const uint8_t HW_FEEDBACK[NUM_FEEDBACK_PINS] PROGMEM = {
    MCP_ENC(CHIP_U5,PORT_A,0), MCP_ENC(CHIP_U5,PORT_A,1),   // E Rett, E Avvik
    MCP_ENC(CHIP_U5,PORT_A,2), MCP_ENC(CHIP_U5,PORT_A,3),   // F Rett, F Avvik
    MCP_ENC(CHIP_U5,PORT_A,4), MCP_ENC(CHIP_U5,PORT_A,5),   // G Rett, G Avvik
    MCP_ENC(CHIP_U5,PORT_A,6), MCP_ENC(CHIP_U5,PORT_A,7),   // H Rett, H Avvik
    MCP_ENC(CHIP_U5,PORT_B,2), MCP_ENC(CHIP_U5,PORT_B,3),   // I Rett, I Avvik
    MCP_ENC(CHIP_U5,PORT_B,4), MCP_ENC(CHIP_U5,PORT_B,5),   // C Rett, C Avvik
    MCP_ENC(CHIP_U5,PORT_B,6), MCP_ENC(CHIP_U5,PORT_B,7),   // D Rett, D Avvik
    ARD_ENC(PENS_B_RETT_PIN), ARD_ENC(PENS_B_AVVIK_PIN)     // B Rett, B Avvik
};

// All operator switch + Dreieskive + moment pins scanned by SCA (active-low)
// U7 PORT B 0–7: panel switches for Penser B–I
// U2 PORT A 0–2: Dreieskive CW, CCW, moment button (remaining bits spare)
#define NUM_SWITCH_PINS 11
static const uint8_t HW_SWITCH[NUM_SWITCH_PINS] PROGMEM = {
    MCP_ENC(CHIP_U7,PORT_B,0), MCP_ENC(CHIP_U7,PORT_B,1),
    MCP_ENC(CHIP_U7,PORT_B,2), MCP_ENC(CHIP_U7,PORT_B,3),
    MCP_ENC(CHIP_U7,PORT_B,4), MCP_ENC(CHIP_U7,PORT_B,5),
    MCP_ENC(CHIP_U7,PORT_B,6), MCP_ENC(CHIP_U7,PORT_B,7),
    MCP_ENC(CHIP_U2,PORT_A,0), MCP_ENC(CHIP_U2,PORT_A,1),   // Dreieskive CW, CCW
    MCP_ENC(CHIP_U2,PORT_A,2)                                 // moment button
};

// ---------------------------------------------------------------------------
// Cached MCP23017 output-latch registers (one per chip per port)
// ---------------------------------------------------------------------------
static uint8_t g_olatA[4];   // indexed by chip index
static uint8_t g_olatB[4];

// ---------------------------------------------------------------------------
// MCP23017 low-level I2C driver
// ---------------------------------------------------------------------------
static void mcp_write(uint8_t addr, uint8_t reg, uint8_t val) {
    Wire.beginTransmission(addr);
    Wire.write(reg);
    Wire.write(val);
    Wire.endTransmission();
}

static uint8_t mcp_read(uint8_t addr, uint8_t reg) {
    Wire.beginTransmission(addr);
    Wire.write(reg);
    Wire.endTransmission(false);
    Wire.requestFrom(addr, (uint8_t)1);
    return Wire.available() ? Wire.read() : 0xFF;
}

// ---------------------------------------------------------------------------
// Pin encoding helpers
// ---------------------------------------------------------------------------
static bool hw_read_pin(uint8_t enc) {
    if (IS_ARD(enc)) return digitalRead(enc & 0x7F);
    uint8_t addr = pgm_read_byte(&CHIP_ADDR[ENC_CHIP(enc)]);
    uint8_t reg  = ENC_PORT(enc) ? MCP_GPIOB : MCP_GPIOA;
    return (mcp_read(addr, reg) >> ENC_BIT(enc)) & 1;
}

// Write to a cached output-latch pin and flush the register.
// For H-bridge safety: always clear the '0' side before setting the '1' side.
// This function just sets one bit; caller must sequence correctly for motor writes.
static void hw_write_pin(uint8_t enc, bool val) {
    if (IS_ARD(enc)) { digitalWrite(enc & 0x7F, val ? HIGH : LOW); return; }
    uint8_t chip = ENC_CHIP(enc);
    uint8_t port = ENC_PORT(enc);
    uint8_t bit  = ENC_BIT(enc);
    uint8_t addr = pgm_read_byte(&CHIP_ADDR[chip]);

    if (port == PORT_A) {
        if (val) g_olatA[chip] |=  (1 << bit);
        else     g_olatA[chip] &= ~(1 << bit);
        mcp_write(addr, MCP_OLATA, g_olatA[chip]);
    } else {
        if (val) g_olatB[chip] |=  (1 << bit);
        else     g_olatB[chip] &= ~(1 << bit);
        mcp_write(addr, MCP_OLATB, g_olatB[chip]);
    }
}

// Set motor pair to polarity: pol=0 → "01" (pinA=LOW, pinB=HIGH)
//                             pol=1 → "10" (pinA=HIGH, pinB=LOW)
// Write order ensures we never pass through '11'.
static void hw_set_motor(uint8_t pair, uint8_t pol) {
    uint8_t pinA = pgm_read_byte(&HW_MOTOR_A[pair]);
    uint8_t pinB = pgm_read_byte(&HW_MOTOR_B[pair]);
    if (pol == 0) {
        hw_write_pin(pinA, false);  // clear A first  ("?0" safe)
        hw_write_pin(pinB, true);   // then set B      ("01")
    } else {
        hw_write_pin(pinB, false);  // clear B first  ("?0" safe)
        hw_write_pin(pinA, true);   // then set A      ("10")
    }
}

// De-energise one motor pair ("00").  Valid only for Dreieskive.
static void hw_stop_motor(uint8_t pair) {
    hw_write_pin(pgm_read_byte(&HW_MOTOR_A[pair]), false);
    hw_write_pin(pgm_read_byte(&HW_MOTOR_B[pair]), false);
}

// ---------------------------------------------------------------------------
// Hardware initialisation
// ---------------------------------------------------------------------------
static void hw_init() {
    Wire.begin();

    // U1 @ 0x22 — all outputs (motor drive)
    mcp_write(0x22, MCP_IODIRA, 0x00);
    mcp_write(0x22, MCP_IODIRB, 0x00);

    // U5 @ 0x25 — PORT B: bits 0,1 output (Dreieskive motor); bits 2-7 input (feedback)
    //              PORT A: all input (feedback E,F,G,H)
    mcp_write(0x25, MCP_IODIRB, 0xFC);  // 1111 1100
    mcp_write(0x25, MCP_IODIRA, 0xFF);
    mcp_write(0x25, MCP_GPPUB,  0xFC);  // pull-ups on feedback input bits
    mcp_write(0x25, MCP_GPPUA,  0xFF);

    // U7 @ 0x20 — PORT B: all input (panel switches); PORT A: all output (LEDs 0-7)
    mcp_write(0x20, MCP_IODIRB, 0xFF);
    mcp_write(0x20, MCP_GPPUB,  0xFF);  // pull-ups on switch inputs
    mcp_write(0x20, MCP_IODIRA, 0x00);

    // U2 @ 0x21 — PORT B: all output (LEDs 8-15); PORT A: all input (Dreieskive/moment)
    mcp_write(0x21, MCP_IODIRB, 0x00);
    mcp_write(0x21, MCP_IODIRA, 0xFF);
    mcp_write(0x21, MCP_GPPUA,  0xFF);  // pull-ups on switch/moment inputs

    // Arduino direct feedback pins for Pens B
    pinMode(PENS_B_RETT_PIN,  INPUT_PULLUP);
    pinMode(PENS_B_AVVIK_PIN, INPUT_PULLUP);

    // Initial motor state: all Pens pairs → "01" (safe stall-hold), Dreieskive → "00"
    // U1 PORT B = 0x00 (first direction pins all LOW)
    // U1 PORT A = 0xFF (second direction pins all HIGH)
    // → all 8 Pens motors in "01" state
    g_olatA[CHIP_U1] = 0xFF;
    g_olatB[CHIP_U1] = 0x00;
    mcp_write(0x22, MCP_OLATA, g_olatA[CHIP_U1]);
    mcp_write(0x22, MCP_OLATB, g_olatB[CHIP_U1]);

    // U5 PORT B bits 0,1 = 0x00 → Dreieskive "00" (stopped at boot)
    g_olatB[CHIP_U5] = 0x00;
    mcp_write(0x25, MCP_OLATB, g_olatB[CHIP_U5]);

    // LEDs: all off
    g_olatA[CHIP_U7] = 0x00;
    g_olatB[CHIP_U2] = 0x00;
    mcp_write(0x20, MCP_OLATA, 0x00);
    mcp_write(0x21, MCP_OLATB, 0x00);
}

// ---------------------------------------------------------------------------
// Helpers (identical to Simulator.ino)
// ---------------------------------------------------------------------------
static void ok()    { Serial.println(F("OK")); }

static void errCmd(const __FlashStringHelper* msg) {
    Serial.print(F("ERR "));
    Serial.println(msg);
}

static void status(const __FlashStringHelper* msg) {
    Serial.print('!');
    Serial.println(msg);
}

static void softReset() {
    status(F("Resetting..."));
    delay(100);
    wdt_enable(WDTO_15MS);
    while (true) {}
}

static bool readLine(char* buf, uint8_t maxLen) {
    uint8_t n = Serial.readBytesUntil('\n', buf, maxLen - 1);
    buf[n] = '\0';
    if (n > 0 && buf[n - 1] == '\r') buf[--n] = '\0';
    return n > 0;
}

static bool parseHex(const char* s, uint16_t* out) {
    if (!s || !*s) return false;
    uint16_t v = 0;
    while (*s) {
        char c = *s++;
        if      (c >= '0' && c <= '9') v = (uint16_t)(v * 16 + (c - '0'));
        else if (c >= 'A' && c <= 'F') v = (uint16_t)(v * 16 + (c - 'A' + 10));
        else if (c >= 'a' && c <= 'f') v = (uint16_t)(v * 16 + (c - 'a' + 10));
        else return false;
    }
    *out = v;
    return true;
}

static bool parseUint(const char* s, uint16_t* out) {
    if (!s || !*s) return false;
    uint16_t v = 0;
    while (*s) {
        char c = *s++;
        if (c < '0' || c > '9') return false;
        v = (uint16_t)(v * 10 + (c - '0'));
    }
    *out = v;
    return true;
}

// ---------------------------------------------------------------------------
// Command handlers
// ---------------------------------------------------------------------------

static void cmdPing() { Serial.println(F("PONG")); }

static void cmdSI() {
    char buf[28];
    snprintf(buf, sizeof(buf), "SITE %c %c %d %d %d",
             (char)FIRST_PENS, (char)LAST_PENS,
             NUM_LED_OUTPUTS, NUM_PAIRS, DREIESKIVE_PAIR);
    Serial.println(buf);
}

static void cmdER() {
    uint16_t addr;
    if (!parseHex(strtok(nullptr, " "), &addr)) { errCmd(F("bad addr")); return; }
    char buf[3];
    snprintf(buf, sizeof(buf), "%02X", EEPROM.read(addr));
    Serial.println(buf);
}

static void cmdEW() {
    uint16_t addr, val;
    if (!parseHex(strtok(nullptr, " "), &addr)) { errCmd(F("bad addr")); return; }
    if (!parseHex(strtok(nullptr, " "), &val))  { errCmd(F("bad val"));  return; }
    EEPROM.update(addr, (uint8_t)val);
    ok();
}

static void cmdEC() {
    for (uint16_t a = 0x10; a < 0x200; a++) EEPROM.update(a, 0xFF);
    EEPROM.update(EEPROM_MAGIC_ADDR, 0xFF);
    ok();
}

static void cmdMF() {
    uint16_t pair, pol;
    if (!parseUint(strtok(nullptr, " "), &pair) || pair >= NUM_PAIRS) { errCmd(F("bad pair")); return; }
    if (!parseUint(strtok(nullptr, " "), &pol)  || pol > 1)           { errCmd(F("bad pol"));  return; }
    hw_set_motor((uint8_t)pair, (uint8_t)pol);
    ok();
}

static void cmdMS() {
    // De-energise Dreieskive motor safely. Pens motors remain at last polarity (stall-hold).
    hw_stop_motor(DREIESKIVE_PAIR);
    ok();
}

// Wait for any feedback pin to go LOW (active) within timeoutMs.
// Returns the encoded pin byte in "HIT <enc>" or "TIMEOUT".
static void cmdFW() {
    uint16_t pair, timeoutMs;
    if (!parseUint(strtok(nullptr, " "), &pair)      || pair >= NUM_PAIRS) { errCmd(F("bad pair"));    return; }
    if (!parseUint(strtok(nullptr, " "), &timeoutMs))                       { errCmd(F("bad timeout")); return; }

    if (pair == DREIESKIVE_PAIR) {
        delay((unsigned long)min(timeoutMs, (uint16_t)2000));
        Serial.println(F("TIMEOUT"));
        return;
    }

    // Capture baseline states for all feedback pins.
    uint8_t baseline[NUM_FEEDBACK_PINS];
    for (uint8_t i = 0; i < NUM_FEEDBACK_PINS; i++) {
        uint8_t enc = pgm_read_byte(&HW_FEEDBACK[i]);
        baseline[i] = hw_read_pin(enc) ? 1 : 0;
    }

    unsigned long deadline = millis() + timeoutMs;
    while ((long)(millis() - deadline) < 0) {
        for (uint8_t i = 0; i < NUM_FEEDBACK_PINS; i++) {
            uint8_t enc = pgm_read_byte(&HW_FEEDBACK[i]);
            uint8_t now = hw_read_pin(enc) ? 1 : 0;
            if (now != baseline[i]) {
                char buf[10];
                snprintf(buf, sizeof(buf), "HIT %d", enc);
                Serial.println(buf);
                return;
            }
        }
        delay(5);
    }
    Serial.println(F("TIMEOUT"));
}

static void cmdSW() {
    uint16_t enc;
    if (!parseUint(strtok(nullptr, " "), &enc)) { errCmd(F("bad pin")); return; }
    Serial.println(hw_read_pin((uint8_t)enc) ? '1' : '0');
}

// Scan all operator/Dreieskive/moment switch pins for any change.
static void cmdSCA() {
    uint16_t timeoutMs;
    if (!parseUint(strtok(nullptr, " "), &timeoutMs)) { errCmd(F("bad timeout")); return; }

    uint8_t baseline[NUM_SWITCH_PINS];
    for (uint8_t i = 0; i < NUM_SWITCH_PINS; i++) {
        uint8_t enc = pgm_read_byte(&HW_SWITCH[i]);
        baseline[i] = hw_read_pin(enc) ? 1 : 0;
    }

    unsigned long deadline = millis() + timeoutMs;
    while ((long)(millis() - deadline) < 0) {
        for (uint8_t i = 0; i < NUM_SWITCH_PINS; i++) {
            uint8_t enc = pgm_read_byte(&HW_SWITCH[i]);
            uint8_t now = hw_read_pin(enc) ? 1 : 0;
            if (now != baseline[i]) {
                char buf[14];
                snprintf(buf, sizeof(buf), "CHANGED %d", enc);
                Serial.println(buf);
                return;
            }
        }
        delay(5);
    }
    Serial.println(F("TIMEOUT"));
}

static void cmdLD() {
    uint16_t idx, state;
    if (!parseUint(strtok(nullptr, " "), &idx)   || idx >= NUM_LED_OUTPUTS) { errCmd(F("bad idx"));   return; }
    if (!parseUint(strtok(nullptr, " "), &state) || state > 1)              { errCmd(F("bad state")); return; }
    uint8_t enc = pgm_read_byte(&HW_LED[idx]);
    hw_write_pin(enc, state == 1);
    ok();
}

static void cmdLA() {
    for (uint8_t i = 0; i < NUM_LED_OUTPUTS; i++)
        hw_write_pin(pgm_read_byte(&HW_LED[i]), false);
    ok();
}

// Routing matrix upload / download — identical to Simulator.ino
static void cmdRMU() {
    Serial.println(F("READY"));

    uint8_t buf[LED_COUNT * STRIDE];
    bool uploadOk = true;
    char dataBuf[40];

    for (uint8_t i = 0; i < LED_COUNT && uploadOk; i++) {
        if (!readLine(dataBuf, sizeof(dataBuf))) {
            Serial.println(F("ERR timeout"));
            uploadOk = false; break;
        }
        if (strcmp_P(dataBuf, PSTR("ABORT")) == 0) {
            status(F("Upload aborted."));
            uploadOk = false; break;
        }
        uint16_t base = (uint16_t)i * STRIDE;
        uint16_t count;
        if (!parseHex(strtok(dataBuf, " "), &count)) { Serial.println(F("ERR")); uploadOk = false; break; }
        buf[base] = (uint8_t)count;

        uint8_t expected = (count == 0xFF || count == 0x00) ? 0 : (uint8_t)(count * 2);
        for (uint8_t t = 0; t < expected && uploadOk; t++) {
            uint16_t v;
            if (!parseHex(strtok(nullptr, " "), &v)) { Serial.println(F("ERR")); uploadOk = false; break; }
            buf[base + 1 + t] = (uint8_t)v;
        }
        if (uploadOk) Serial.println(F("OK"));
    }

    if (uploadOk) {
        if (!readLine(dataBuf, sizeof(dataBuf)) || strcmp_P(dataBuf, PSTR("END")) != 0) {
            status(F("Expected END — not committed."));
        } else {
            for (uint16_t i = 0; i < (uint16_t)LED_COUNT * STRIDE; i++)
                EEPROM.update(REGION6_BASE + i, buf[i]);
            Serial.println(F("STORED"));
            status(F("Routing matrix written to EEPROM."));
        }
    }
}

static void cmdRMD() {
    status(F("Sending routing matrix..."));
    for (uint8_t i = 0; i < LED_COUNT; i++) {
        uint16_t base = REGION6_BASE + (uint16_t)i * STRIDE;
        uint8_t count = EEPROM.read(base);
        if (count == 0xFF) { Serial.println(F("FF")); continue; }
        if (count == 0x00) { Serial.println(F("00")); continue; }
        char line[32];
        uint8_t pos = 0;
        pos += snprintf(line + pos, sizeof(line) - pos, "%02X", count);
        for (uint8_t c = 0; c < count; c++) {
            uint8_t r = EEPROM.read(base + 1 + c * 2);
            uint8_t a = EEPROM.read(base + 2 + c * 2);
            pos += snprintf(line + pos, sizeof(line) - pos, " %02X %02X", r, a);
        }
        Serial.println(line);
    }
    Serial.println(F("END"));
}

// ---------------------------------------------------------------------------
// Command dispatcher  (same names as Simulator.ino)
// ---------------------------------------------------------------------------
static void dispatch(char* line) {
    char* cmd = strtok(line, " ");
    if (!cmd || !*cmd) return;

    if      (strcmp_P(cmd, PSTR("PING")) == 0) cmdPing();
    else if (strcmp_P(cmd, PSTR("SI"))   == 0) cmdSI();
    else if (strcmp_P(cmd, PSTR("ER"))   == 0) cmdER();
    else if (strcmp_P(cmd, PSTR("EW"))   == 0) cmdEW();
    else if (strcmp_P(cmd, PSTR("EC"))   == 0) cmdEC();
    else if (strcmp_P(cmd, PSTR("MF"))   == 0) cmdMF();
    else if (strcmp_P(cmd, PSTR("MS"))   == 0) cmdMS();
    else if (strcmp_P(cmd, PSTR("FW"))   == 0) cmdFW();
    else if (strcmp_P(cmd, PSTR("SW"))   == 0) cmdSW();
    else if (strcmp_P(cmd, PSTR("SCA"))  == 0) cmdSCA();
    else if (strcmp_P(cmd, PSTR("LD"))   == 0) cmdLD();
    else if (strcmp_P(cmd, PSTR("LA"))   == 0) cmdLA();
    else if (strcmp_P(cmd, PSTR("RMU"))  == 0) cmdRMU();
    else if (strcmp_P(cmd, PSTR("RMD"))  == 0) cmdRMD();
    else if (strcmp_P(cmd, PSTR("RST"))  == 0) softReset();
    else {
        Serial.print(F("ERR unknown: "));
        Serial.println(cmd);
    }
}

// ---------------------------------------------------------------------------
// setup / loop
// ---------------------------------------------------------------------------
void setup() {
    wdt_disable();
    Serial.begin(115200);
    Serial.setTimeout(5000);

    hw_init();

    status(F("LysKontroll Firmware v1.0 (config mode)"));
    status(F("Commands: PING SI ER EW EC MF MS FW SW SCA LD LA RMU RMD RST"));
}

void loop() {
    char line[48];
    if (!readLine(line, sizeof(line))) return;
    dispatch(line);
}
