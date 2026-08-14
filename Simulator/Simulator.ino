// Simulator.ino — v2.0  (Phase 1 refactor)
//
// Low-level command interface only.  All config logic lives in C#.
// This file simulates physical hardware and dispatches low-level commands.
//
// Protocol (115200 baud, line-based):
//   C# → Arduino : "CMD [args]\n"      (full lines, no single chars)
//   Arduino → C# : response line(s)
//   Lines starting with '!' = status messages, always displayed by C# immediately.
//   All other lines = protocol responses, queued in C# capture mode.

#include <EEPROM.h>
#include <avr/pgmspace.h>
#include <avr/wdt.h>

// ---------------------------------------------------------------------------
// Site configuration -- adjust per site
// ---------------------------------------------------------------------------
#define MAX_PENS         26
#define FIRST_PENS       'B'    // Fossli
#define LAST_PENS        'I'    // Fossli
#define NUM_LED_OUTPUTS  16     // physical LED hardware outputs on this site

// ---------------------------------------------------------------------------
// EEPROM layout
// ---------------------------------------------------------------------------
#define EEPROM_MAGIC_ADDR   0x01
#define EEPROM_MAGIC_VALUE  0xA5
#define REGION1_BASE        0x10   // motor pair index     (MAX_PENS bytes)
#define REGION1_POL         0x30   // motor polarity       (MAX_PENS bytes)
#define REGION2_RETT        0x50   // feedback Rett pin    (MAX_PENS bytes)
#define REGION2_AVVIK       0x70   // feedback Avvik pin   (MAX_PENS bytes)
#define REGION3_BASE        0x90   // manual switch pin    (MAX_PENS bytes)
#define REGION4_RETT        0xB0   // Rett LED index       (MAX_PENS bytes)
#define REGION4_AVVIK       0xD0   // Avvik LED index      (MAX_PENS bytes)
#define REGION5_MOTOR_PIN   0xF0
#define REGION5_MOTOR_POL   0xF1
#define REGION5_SW_CW       0xF2
#define REGION5_SW_CCW      0xF3
#define REGION5_MOMENT      0xF4
#define REGION6_BASE       0x100   // LED routing matrix
#define LED_NO_PIN          0xFE   // REGION4_* value meaning "no physical LED"

// Routing matrix dimensions (derived -- do not adjust per site)
#define LED_COUNT  ((LAST_PENS - FIRST_PENS + 1) * 2)
#define MAX_COND    4
#define STRIDE      (1 + MAX_COND * 2)

// ---------------------------------------------------------------------------
// Simulated hardware -- fixed internal wiring (indexed by pair 0..8)
// ---------------------------------------------------------------------------
#define SIM_NUM_PAIRS        9
#define SIM_DREIESKIVE_PAIR  4

// 0xFF = no feedback / no switch (Dreieskive pair)
static const uint8_t SIM_FB_A[SIM_NUM_PAIRS] PROGMEM
    = {  0,  2,  4,  6, 0xFF,  8, 10, 12, 14 };
static const uint8_t SIM_FB_B[SIM_NUM_PAIRS] PROGMEM
    = {  1,  3,  5,  7, 0xFF,  9, 11, 13, 15 };
static const uint8_t SIM_SW[SIM_NUM_PAIRS]   PROGMEM
    = { 16, 17, 18, 19, 0xFF, 20, 21, 22, 23 };

#define SIM_DREI_SW_CW   24
#define SIM_DREI_SW_CCW  25
#define SIM_MOMENT_PIN   26

// ---------------------------------------------------------------------------
// Simulator runtime state
// ---------------------------------------------------------------------------
static uint8_t simActiveFb[SIM_NUM_PAIRS];   // active feedback pin per pair (0xFF = none)
static bool    simLed[NUM_LED_OUTPUTS];
static int8_t  simLastFiredPair = -1;        // for SCA: most recently fired pair

// ---------------------------------------------------------------------------
// Helpers
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

// Read one line from Serial (blocking, respects setTimeout).
// Strips trailing \r\n.  Returns true if any bytes were read.
static bool readLine(char* buf, uint8_t maxLen) {
    uint8_t n = Serial.readBytesUntil('\n', buf, maxLen - 1);
    buf[n] = '\0';
    if (n > 0 && buf[n - 1] == '\r') buf[--n] = '\0';
    return n > 0;
}

// Parse a hex string (uppercase or lowercase) into a uint16_t.
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

// Parse a decimal string into a uint16_t.
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
// (strtok state is live on entry -- use strtok(nullptr, " ") for next token)
// ---------------------------------------------------------------------------

static void cmdPing() { Serial.println(F("PONG")); }

static void cmdSI() {
    char buf[28];
    snprintf(buf, sizeof(buf), "SITE %c %c %d %d %d",
             (char)FIRST_PENS, (char)LAST_PENS,
             NUM_LED_OUTPUTS, SIM_NUM_PAIRS, SIM_DREIESKIVE_PAIR);
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
    if (!parseUint(strtok(nullptr, " "), &pair) || pair >= SIM_NUM_PAIRS) { errCmd(F("bad pair")); return; }
    if (!parseUint(strtok(nullptr, " "), &pol)  || pol > 1)               { errCmd(F("bad pol"));  return; }

    uint8_t fbA = pgm_read_byte(&SIM_FB_A[pair]);
    uint8_t fbB = pgm_read_byte(&SIM_FB_B[pair]);
    if (fbA != 0xFF) {
        simActiveFb[pair]   = (pol == 0) ? fbA : fbB;
        simLastFiredPair    = (int8_t)pair;
    }
    ok();
}

static void cmdMS() {
    // Motors stopped; stall-hold keeps positions, so simActiveFb stays as-is.
    ok();
}

static void cmdFW() {
    uint16_t pair, timeoutMs;
    if (!parseUint(strtok(nullptr, " "), &pair)      || pair >= SIM_NUM_PAIRS) { errCmd(F("bad pair"));    return; }
    if (!parseUint(strtok(nullptr, " "), &timeoutMs))                           { errCmd(F("bad timeout")); return; }

    uint8_t fbA = pgm_read_byte(&SIM_FB_A[pair]);
    if (fbA == 0xFF) {
        // Dreieskive: no feedback, always timeout
        delay((unsigned long)min(timeoutMs, (uint16_t)1500));
        Serial.println(F("TIMEOUT"));
        return;
    }
    // Pens pair: simulate motor travel, then report active feedback pin
    delay(400);
    uint8_t pin = simActiveFb[pair];
    if (pin == 0xFF) {
        Serial.println(F("TIMEOUT"));
    } else {
        char buf[10];
        snprintf(buf, sizeof(buf), "HIT %d", pin);
        Serial.println(buf);
    }
}

static void cmdSW() {
    uint16_t pin;
    if (!parseUint(strtok(nullptr, " "), &pin)) { errCmd(F("bad pin")); return; }
    // Simulator: all switches default to 0 (Rett position)
    Serial.println('0');
}

static void cmdSCA() {
    uint16_t timeoutMs;
    if (!parseUint(strtok(nullptr, " "), &timeoutMs)) { errCmd(F("bad timeout")); return; }

    if (simLastFiredPair < 0) {
        delay((unsigned long)min(timeoutMs, (uint16_t)2000));
        Serial.println(F("TIMEOUT"));
        return;
    }
    uint8_t sw = pgm_read_byte(&SIM_SW[simLastFiredPair]);
    if (sw == 0xFF) {
        delay((unsigned long)min(timeoutMs, (uint16_t)2000));
        Serial.println(F("TIMEOUT"));
        return;
    }
    // Simulate operator flipping the switch for the last-fired pair
    delay(800);
    char buf[14];
    snprintf(buf, sizeof(buf), "CHANGED %d", sw);
    Serial.println(buf);
}

static void cmdLD() {
    uint16_t idx, state;
    if (!parseUint(strtok(nullptr, " "), &idx)   || idx >= NUM_LED_OUTPUTS) { errCmd(F("bad idx"));   return; }
    if (!parseUint(strtok(nullptr, " "), &state) || state > 1)              { errCmd(F("bad state")); return; }
    simLed[idx] = (state == 1);
    ok();
}

static void cmdLA() {
    memset(simLed, 0, sizeof(simLed));
    ok();
}

// ---------------------------------------------------------------------------
// Routing matrix upload (RMU) and download (RMD)
// Same READY/OK/STORED/END protocol as before, now via named commands.
// ---------------------------------------------------------------------------

static void cmdRMU() {
    Serial.println(F("READY"));

    uint8_t buf[LED_COUNT * STRIDE];
    bool ok2 = true;
    char dataBuf[40];

    for (uint8_t i = 0; i < LED_COUNT && ok2; i++) {
        if (!readLine(dataBuf, sizeof(dataBuf))) {
            Serial.println(F("ERR timeout"));
            ok2 = false; break;
        }
        if (strcmp_P(dataBuf, PSTR("ABORT")) == 0) {
            status(F("Upload aborted."));
            ok2 = false; break;
        }

        uint16_t base = (uint16_t)i * STRIDE;
        uint16_t count;
        if (!parseHex(strtok(dataBuf, " "), &count)) { Serial.println(F("ERR")); ok2 = false; break; }
        buf[base] = (uint8_t)count;

        uint8_t expected = (count == 0xFF || count == 0x00) ? 0 : (uint8_t)(count * 2);
        for (uint8_t t = 0; t < expected && ok2; t++) {
            uint16_t v;
            if (!parseHex(strtok(nullptr, " "), &v)) { Serial.println(F("ERR")); ok2 = false; break; }
            buf[base + 1 + t] = (uint8_t)v;
        }
        if (ok2) Serial.println(F("OK"));
    }

    if (ok2) {
        if (!readLine(dataBuf, sizeof(dataBuf)) || strcmp_P(dataBuf, PSTR("END")) != 0) {
            status(F("Expected END -- not committed."));
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
// Command dispatcher
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

    memset(simActiveFb, 0xFF, sizeof(simActiveFb));
    memset(simLed,      0,    sizeof(simLed));

    status(F("LysKontroll Simulator v2.0"));
    status(F("Commands: PING SI ER EW EC MF MS FW SW SCA LD LA RMU RMD RST"));
}

void loop() {
    char line[48];
    if (!readLine(line, sizeof(line))) return;
    dispatch(line);
}
