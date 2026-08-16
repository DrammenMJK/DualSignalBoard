// Firmware.ino — LysKontroll Phase 1 (FSCB/FCSBR over direct I2C)
//
// Copied from Simulator.ino and trimmed of all simulated-Pens business logic
// per PLAN_Phase1.md. Transport plumbing (readLine/dispatch/OK/ERR/status)
// and the EEPROM primitive commands (PING/SI/ER/EW/EC/RST) are unchanged in
// spirit from the simulator; everything else is new.
//
// No compiled-in board topology: the board hardware table (direction/pullup
// per MCP23017 chip) and the switch table (which motor bit is which switch)
// are both EEPROM-backed and populated entirely from the PC side (hardware.json
// via HWU, SystemConfig.json via SCU / Motor Scan). See PLAN_Phase1.md for the
// full design and reasoning.
//
// Protocol (115200 baud, line-based):
//   C# -> Arduino : "CMD [args]\n"
//   Arduino -> C# : response line(s)
//   Lines starting with '!' = status messages, always displayed by C# immediately.
//   All other lines = protocol responses, queued in C# capture mode.

#include <EEPROM.h>
#include <Wire.h>
#include <avr/pgmspace.h>
#include <avr/wdt.h>

// Defined here, ahead of everything else, because the Arduino build's
// auto-generated function prototypes are inserted right after the last
// #include — any function whose signature names one of these types must
// have the type already visible at that point, not just before its own body.
struct VAddrResolved { uint8_t bus; uint8_t realAddr; bool ok; };
struct SwitchRec {
    uint8_t motorVAddr, motorBit, polarity, fbVAddr, fbRettBit, fbAvvikBit;
    bool configured;
};
enum SwitchState : uint8_t { SW_RETT, SW_AVVIK, SW_BETWEEN, SW_FAULT };

// ---------------------------------------------------------------------------
// EEPROM layout (new — unrelated to the older Pens-letter Region1-6 layout)
// ---------------------------------------------------------------------------
#define EEPROM_MAGIC_ADDR   0x01
#define EEPROM_MAGIC_VALUE  0xA5

// Fixed board facts: dreieskive motor side + status LED (declared, not scanned)
#define ADDR_DREI_VADDR     0x02
#define ADDR_DREI_PINBASE   0x03
#define ADDR_DREI_CWPOL     0x04
#define ADDR_LED_VADDR      0x05
#define ADDR_LED_BIT        0x06

// Fade config: fixed timing parameters for the (not yet built) signal-lamp fade loop
#define ADDR_FADE_MS        0x07   // uint16, 2 bytes
#define ADDR_FADE_STEPS     0x09   // uint8
#define ADDR_FADE_PWMUS     0x0A   // uint16, 2 bytes

// Switch table — 32-slot capacity
#define SWITCH_CAP           32
#define REGION_SLOT_MVADDR  0x10   // SlotMotorVAddr[32]
#define REGION_SLOT_MBIT    0x30   // SlotMotorBit[32]
#define REGION_SLOT_POL     0x50   // SlotPolarity[32]
#define REGION_SLOT_FBRETT  0x70   // SlotFeedbackRettBit[32]
#define REGION_SLOT_FBAVVIK 0x90   // SlotFeedbackAvvikBit[32]
#define REGION_SLOT_FVADDR  0x100  // SlotFeedbackVAddr[32]

// Board hardware table — 16-slot capacity
#define BOARD_CAP            16
#define REGION_BOARD_VADDR  0xB0   // BoardVAddr[16]
#define REGION_BOARD_IODIRA 0xC0   // BoardIodirA[16]
#define REGION_BOARD_IODIRB 0xD0   // BoardIodirB[16]
#define REGION_BOARD_GPPUA  0xE0   // BoardGppuA[16]
#define REGION_BOARD_GPPUB  0xF0   // BoardGppuB[16]

// SVB switch table — 32-slot capacity (unpopulated in Phase 1, no SVB present)
#define SVBSW_CAP            32
#define REGION_SVBSW_VADDR   0x120 // SvbSwVAddr[32]
#define REGION_SVBSW_BITPRI  0x140 // SvbSwBitPrimary[32]
#define REGION_SVBSW_BITSEC  0x160 // SvbSwBitSecondary[32]
#define REGION_SVBSW_TGTDREI 0x180 // SvbSwTargetIsDreieskive[32]
#define REGION_SVBSW_TGTSLOT 0x1A0 // SvbSwTargetSlot[32]

#define EEPROM_ERASE_END     0x1BF // EC clears 0x02..0x1BF plus the magic byte

// ---------------------------------------------------------------------------
// MCP23017 register addresses
// ---------------------------------------------------------------------------
#define MCP_IODIRA 0x00
#define MCP_IODIRB 0x01
#define MCP_GPPUA  0x0C
#define MCP_GPPUB  0x0D
#define MCP_GPIOA  0x12
#define MCP_GPIOB  0x13
#define MCP_OLATA  0x14
#define MCP_OLATB  0x15

// ---------------------------------------------------------------------------
// Helpers (unchanged in shape from Simulator.ino)
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
// Strips trailing \r\n. Returns true if any bytes were read.
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

// Port token is exactly "A" or "B".
static bool parsePort(const char* s, uint8_t* isPortB) {
    if (!s || (s[0] != 'A' && s[0] != 'B') || s[1] != '\0') return false;
    *isPortB = (s[0] == 'B') ? 1 : 0;
    return true;
}

// ---------------------------------------------------------------------------
// Virtual-address resolver — bus lives here, and only here.
// vaddr = bus*0x10 + real_i2c_address (real address always 0x20-0x27)
// ---------------------------------------------------------------------------
static VAddrResolved resolve_vaddr(uint8_t vaddr) {
    VAddrResolved r;
    r.bus      = (uint8_t)((vaddr >> 4) - 2);
    r.realAddr = 0x20 | (vaddr & 0x0F);
    r.ok       = (r.bus == 0); // Phase 1: no mux fitted, only bus 0 is wired
    return r;
}

// Phase 1: no mux fitted; validating no-op. Phase 2+: drives the mux channel select.
static bool i2c_select_bus(uint8_t bus) {
    return bus == 0;
}

// ---------------------------------------------------------------------------
// Generic MCP23017 driver — 2 primitives, reused unchanged by every board.
// ---------------------------------------------------------------------------
static void mcp_write_reg(uint8_t vaddr, uint8_t reg, uint8_t val, bool* okOut) {
    VAddrResolved r = resolve_vaddr(vaddr);
    if (!r.ok || !i2c_select_bus(r.bus)) { if (okOut) *okOut = false; return; }
    Wire.beginTransmission(r.realAddr);
    Wire.write(reg);
    Wire.write(val);
    uint8_t err = Wire.endTransmission();
    if (okOut) *okOut = (err == 0);
}

static uint8_t mcp_read_reg(uint8_t vaddr, uint8_t reg, bool* okOut) {
    VAddrResolved r = resolve_vaddr(vaddr);
    if (!r.ok || !i2c_select_bus(r.bus)) { if (okOut) *okOut = false; return 0xFF; }
    Wire.beginTransmission(r.realAddr);
    Wire.write(reg);
    uint8_t err = Wire.endTransmission(false);
    if (err != 0) { if (okOut) *okOut = false; return 0xFF; }
    Wire.requestFrom((int)r.realAddr, 1);
    if (!Wire.available()) { if (okOut) *okOut = false; return 0xFF; }
    uint8_t v = Wire.read();
    if (okOut) *okOut = true;
    return v;
}

// ---------------------------------------------------------------------------
// Board hardware table — data-driven boot-time bring-up, no compiled board topology.
// ---------------------------------------------------------------------------
static bool board_find_slot(uint8_t vaddr, uint8_t* slotOut) {
    for (uint8_t i = 0; i < BOARD_CAP; i++) {
        if (EEPROM.read(REGION_BOARD_VADDR + i) == vaddr) { *slotOut = i; return true; }
    }
    return false;
}

static bool board_alloc_slot(uint8_t* slotOut) {
    for (uint8_t i = 0; i < BOARD_CAP; i++) {
        if (EEPROM.read(REGION_BOARD_VADDR + i) == 0xFF) { *slotOut = i; return true; }
    }
    return false;
}

static void apply_board_hw(uint8_t slot) {
    uint8_t vaddr = EEPROM.read(REGION_BOARD_VADDR + slot);
    if (vaddr == 0xFF) return;
    uint8_t iodirA = EEPROM.read(REGION_BOARD_IODIRA + slot);
    uint8_t iodirB = EEPROM.read(REGION_BOARD_IODIRB + slot);
    uint8_t gppuA  = EEPROM.read(REGION_BOARD_GPPUA  + slot);
    uint8_t gppuB  = EEPROM.read(REGION_BOARD_GPPUB  + slot);
    bool okFlag;
    mcp_write_reg(vaddr, MCP_IODIRA, iodirA, &okFlag);
    mcp_write_reg(vaddr, MCP_IODIRB, iodirB, &okFlag);
    mcp_write_reg(vaddr, MCP_GPPUA,  gppuA,  &okFlag);
    mcp_write_reg(vaddr, MCP_GPPUB,  gppuB,  &okFlag);
    mcp_write_reg(vaddr, MCP_OLATA,  0x00,   &okFlag); // universal safe default
    mcp_write_reg(vaddr, MCP_OLATB,  0x00,   &okFlag); // (see PLAN_Phase1.md)
}

static void apply_all_board_hw() {
    for (uint8_t i = 0; i < BOARD_CAP; i++) apply_board_hw(i);
}

// ---------------------------------------------------------------------------
// Switch table — resolve_switch() is the one function both SW/SR and the
// (future, Phase 2+) autonomous runtime loop will call.
// ---------------------------------------------------------------------------
static SwitchRec resolve_switch(uint8_t slot) {
    SwitchRec r;
    r.motorVAddr = EEPROM.read(REGION_SLOT_MVADDR + slot);
    r.configured = (r.motorVAddr != 0xFF);
    if (!r.configured) return r;
    r.motorBit   = EEPROM.read(REGION_SLOT_MBIT    + slot);
    r.polarity   = EEPROM.read(REGION_SLOT_POL     + slot);
    r.fbVAddr    = EEPROM.read(REGION_SLOT_FVADDR  + slot);
    r.fbRettBit  = EEPROM.read(REGION_SLOT_FBRETT  + slot);
    r.fbAvvikBit = EEPROM.read(REGION_SLOT_FBAVVIK + slot);
    return r;
}

static SwitchState read_switch_state(const SwitchRec& r, bool* okOut) {
    uint8_t v = mcp_read_reg(r.fbVAddr, MCP_GPIOA, okOut);
    if (!*okOut) return SW_FAULT;
    uint8_t rettVal  = (v >> r.fbRettBit)  & 1;
    uint8_t avvikVal = (v >> r.fbAvvikBit) & 1;
    if (rettVal && !avvikVal) return SW_RETT;
    if (!rettVal && avvikVal) return SW_AVVIK;
    if (rettVal && avvikVal)  return SW_BETWEEN;
    return SW_FAULT;
}

static const __FlashStringHelper* switchStateName(SwitchState s) {
    switch (s) {
        case SW_RETT:    return F("RETT");
        case SW_AVVIK:   return F("AVVIK");
        case SW_BETWEEN: return F("BETWEEN");
        default:         return F("FAULT");
    }
}

// ---------------------------------------------------------------------------
// Command handlers
// (strtok state is live on entry -- use strtok(nullptr, " ") for next token)
// ---------------------------------------------------------------------------

static void cmdPing() { Serial.println(F("PONG")); }

static void cmdSI() { Serial.println(F("SITE 1")); }

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
    for (uint16_t a = 0x02; a <= EEPROM_ERASE_END; a++) EEPROM.update(a, 0xFF);
    EEPROM.update(EEPROM_MAGIC_ADDR, 0xFF);
    ok();
}

// ---- Raw discovery primitives (Motor Scan / bench debug only) -------------

static void cmdMDIR() {
    uint16_t vaddr, mask; uint8_t isB;
    if (!parseHex(strtok(nullptr, " "), &vaddr)) { errCmd(F("bad vaddr")); return; }
    if (!parsePort(strtok(nullptr, " "), &isB))  { errCmd(F("bad port"));  return; }
    if (!parseHex(strtok(nullptr, " "), &mask))  { errCmd(F("bad mask"));  return; }
    bool okFlag;
    mcp_write_reg((uint8_t)vaddr, isB ? MCP_IODIRB : MCP_IODIRA, (uint8_t)mask, &okFlag);
    if (okFlag) ok(); else errCmd(F("i2c"));
}

static void cmdMPU() {
    uint16_t vaddr, mask; uint8_t isB;
    if (!parseHex(strtok(nullptr, " "), &vaddr)) { errCmd(F("bad vaddr")); return; }
    if (!parsePort(strtok(nullptr, " "), &isB))  { errCmd(F("bad port"));  return; }
    if (!parseHex(strtok(nullptr, " "), &mask))  { errCmd(F("bad mask"));  return; }
    bool okFlag;
    mcp_write_reg((uint8_t)vaddr, isB ? MCP_GPPUB : MCP_GPPUA, (uint8_t)mask, &okFlag);
    if (okFlag) ok(); else errCmd(F("i2c"));
}

static void cmdMW() {
    uint16_t vaddr, val; uint8_t isB;
    if (!parseHex(strtok(nullptr, " "), &vaddr)) { errCmd(F("bad vaddr")); return; }
    if (!parsePort(strtok(nullptr, " "), &isB))  { errCmd(F("bad port"));  return; }
    if (!parseHex(strtok(nullptr, " "), &val))   { errCmd(F("bad val"));   return; }
    bool okFlag;
    mcp_write_reg((uint8_t)vaddr, isB ? MCP_OLATB : MCP_OLATA, (uint8_t)val, &okFlag);
    if (okFlag) ok(); else errCmd(F("i2c"));
}

static void cmdMR() {
    uint16_t vaddr; uint8_t isB;
    if (!parseHex(strtok(nullptr, " "), &vaddr)) { errCmd(F("bad vaddr")); return; }
    if (!parsePort(strtok(nullptr, " "), &isB))  { errCmd(F("bad port"));  return; }
    bool okFlag;
    uint8_t v = mcp_read_reg((uint8_t)vaddr, isB ? MCP_GPIOB : MCP_GPIOA, &okFlag);
    if (!okFlag) { errCmd(F("i2c")); return; }
    char buf[3];
    snprintf(buf, sizeof(buf), "%02X", v);
    Serial.println(buf);
}

static void cmdMPOLL() {
    uint16_t vaddr, baseline, timeoutMs; uint8_t isB;
    if (!parseHex(strtok(nullptr, " "), &vaddr))      { errCmd(F("bad vaddr"));    return; }
    if (!parsePort(strtok(nullptr, " "), &isB))       { errCmd(F("bad port"));     return; }
    if (!parseHex(strtok(nullptr, " "), &baseline))   { errCmd(F("bad baseline")); return; }
    if (!parseUint(strtok(nullptr, " "), &timeoutMs)) { errCmd(F("bad timeout"));  return; }

    // A slow-motion switch's two feedback contacts don't flip together: one
    // releases early (pulled high) as it leaves the start position, and the
    // bus sits at that single-bit-changed "between" reading for most of the
    // multi-second travel -- only the second contact closing at the very end
    // produces the real, final pair transition. So it's not enough to wait
    // for a stable reading (the "between" plateau IS stable, just not done);
    // require at least 2 bits different from baseline, matching the fact
    // that a real switch transition always flips a whole feedback pair (see
    // FindPairLow on the C# side, which validates the same thing).
    const uint8_t SETTLE_SAMPLES = 4; // ~20ms of agreement at 5ms/poll
    uint8_t reg = isB ? MCP_GPIOB : MCP_GPIOA;
    unsigned long deadline = millis() + timeoutMs;
    uint8_t lastVal = (uint8_t)baseline;
    uint8_t stableCount = 0;
    while ((long)(millis() - deadline) < 0) {
        bool okFlag;
        uint8_t v = mcp_read_reg((uint8_t)vaddr, reg, &okFlag);
        if (!okFlag) { delay(5); continue; }

        if (v == lastVal) stableCount++;
        else { lastVal = v; stableCount = 1; }

        uint8_t diffBits = __builtin_popcount((uint8_t)(v ^ (uint8_t)baseline));
        if (diffBits >= 2 && stableCount >= SETTLE_SAMPLES) {
            char buf[12];
            snprintf(buf, sizeof(buf), "CHANGED %02X", v);
            Serial.println(buf);
            return;
        }
        delay(5);
    }
    Serial.println(F("TIMEOUT"));
}

static void cmdMBIT() {
    uint16_t vaddr, bit, val; uint8_t isB;
    if (!parseHex(strtok(nullptr, " "), &vaddr))            { errCmd(F("bad vaddr")); return; }
    if (!parsePort(strtok(nullptr, " "), &isB))             { errCmd(F("bad port"));  return; }
    if (!parseUint(strtok(nullptr, " "), &bit) || bit > 7)  { errCmd(F("bad bit"));   return; }
    if (!parseUint(strtok(nullptr, " "), &val) || val > 1)  { errCmd(F("bad val"));   return; }

    uint8_t reg = isB ? MCP_OLATB : MCP_OLATA;
    bool okFlag;
    uint8_t cur = mcp_read_reg((uint8_t)vaddr, reg, &okFlag);
    if (!okFlag) { errCmd(F("i2c read")); return; }
    uint8_t next = val ? (uint8_t)(cur | (1 << bit)) : (uint8_t)(cur & ~(1 << bit));
    mcp_write_reg((uint8_t)vaddr, reg, next, &okFlag);
    if (okFlag) ok(); else errCmd(F("i2c write"));
}

// ---- Board hardware table bulk transfer (HWU/HWD) --------------------------

static void cmdHWU() {
    Serial.println(F("READY"));
    char line[48];
    bool okAll = true;
    while (okAll) {
        if (!readLine(line, sizeof(line))) { Serial.println(F("ERR timeout")); okAll = false; break; }
        if (strcmp_P(line, PSTR("END")) == 0) break;

        uint16_t vaddr, iodirA, iodirB, gppuA, gppuB;
        char* t1 = strtok(line, " ");
        char* t2 = strtok(nullptr, " ");
        char* t3 = strtok(nullptr, " ");
        char* t4 = strtok(nullptr, " ");
        char* t5 = strtok(nullptr, " ");
        if (!parseHex(t1, &vaddr) || !parseHex(t2, &iodirA) || !parseHex(t3, &iodirB) ||
            !parseHex(t4, &gppuA) || !parseHex(t5, &gppuB)) {
            Serial.println(F("ERR parse")); okAll = false; break;
        }
        uint8_t slot;
        if (!board_find_slot((uint8_t)vaddr, &slot) && !board_alloc_slot(&slot)) {
            Serial.println(F("ERR full")); okAll = false; break;
        }
        EEPROM.update(REGION_BOARD_VADDR  + slot, (uint8_t)vaddr);
        EEPROM.update(REGION_BOARD_IODIRA + slot, (uint8_t)iodirA);
        EEPROM.update(REGION_BOARD_IODIRB + slot, (uint8_t)iodirB);
        EEPROM.update(REGION_BOARD_GPPUA  + slot, (uint8_t)gppuA);
        EEPROM.update(REGION_BOARD_GPPUB  + slot, (uint8_t)gppuB);
        Serial.println(F("OK"));
    }
    if (okAll) {
        Serial.println(F("STORED"));
        apply_all_board_hw(); // live re-apply, no reboot needed
    }
}

static void cmdHWD() {
    for (uint8_t i = 0; i < BOARD_CAP; i++) {
        uint8_t vaddr = EEPROM.read(REGION_BOARD_VADDR + i);
        if (vaddr == 0xFF) continue;
        char buf[24];
        snprintf(buf, sizeof(buf), "%02X %02X %02X %02X %02X",
            vaddr,
            EEPROM.read(REGION_BOARD_IODIRA + i),
            EEPROM.read(REGION_BOARD_IODIRB + i),
            EEPROM.read(REGION_BOARD_GPPUA  + i),
            EEPROM.read(REGION_BOARD_GPPUB  + i));
        Serial.println(buf);
    }
    Serial.println(F("END"));
}

// ---- Switch-table commands (Command mode; later the autonomous loop) ------

static void cmdSW() {
    uint16_t slot, timeoutMs;
    char* slotTok = strtok(nullptr, " ");
    char* posTok  = strtok(nullptr, " ");
    char* toTok   = strtok(nullptr, " ");
    if (!parseUint(slotTok, &slot) || slot >= SWITCH_CAP) { errCmd(F("bad slot")); return; }
    if (!posTok || (posTok[0] != 'R' && posTok[0] != 'A') || posTok[1] != '\0') { errCmd(F("bad pos")); return; }
    if (!parseUint(toTok, &timeoutMs)) { errCmd(F("bad timeout")); return; }

    SwitchRec r = resolve_switch((uint8_t)slot);
    if (!r.configured) { errCmd(F("unconfigured")); return; }

    uint8_t targetLevel = (posTok[0] == 'R') ? r.polarity : (uint8_t)(1 - r.polarity);
    bool okFlag;
    uint8_t cur = mcp_read_reg(r.motorVAddr, MCP_OLATB, &okFlag); // motor bits always Port B
    if (!okFlag) { errCmd(F("i2c read")); return; }
    uint8_t next = targetLevel ? (uint8_t)(cur | (1 << r.motorBit)) : (uint8_t)(cur & ~(1 << r.motorBit));
    mcp_write_reg(r.motorVAddr, MCP_OLATB, next, &okFlag);
    if (!okFlag) { errCmd(F("i2c write")); return; }

    unsigned long deadline = millis() + timeoutMs;
    while ((long)(millis() - deadline) < 0) {
        SwitchState s = read_switch_state(r, &okFlag);
        if (okFlag && (s == SW_RETT || s == SW_AVVIK)) {
            Serial.print(F("OK "));
            Serial.println(switchStateName(s));
            return;
        }
        delay(5);
    }
    Serial.println(F("TIMEOUT"));
}

static void cmdSR() {
    uint16_t slot;
    if (!parseUint(strtok(nullptr, " "), &slot) || slot >= SWITCH_CAP) { errCmd(F("bad slot")); return; }
    SwitchRec r = resolve_switch((uint8_t)slot);
    if (!r.configured) { errCmd(F("unconfigured")); return; }
    bool okFlag;
    SwitchState s = read_switch_state(r, &okFlag);
    if (!okFlag) { errCmd(F("i2c")); return; }
    Serial.println(switchStateName(s));
}

// ---- System-config bulk transfer (SCU/SCD) ---------------------------------

static void cmdSCU() {
    Serial.println(F("READY"));
    char line[48];
    bool okAll = true;
    while (okAll) {
        if (!readLine(line, sizeof(line))) { Serial.println(F("ERR timeout")); okAll = false; break; }
        if (strcmp_P(line, PSTR("END")) == 0) break;

        char* tag = strtok(line, " ");
        if (!tag) { Serial.println(F("ERR empty")); okAll = false; break; }

        if (strcmp_P(tag, PSTR("S")) == 0) {
            uint16_t slot, mv, mb, pol, fv, fr, fa;
            if (!parseUint(strtok(nullptr, " "), &slot) || slot >= SWITCH_CAP ||
                !parseHex(strtok(nullptr, " "), &mv) ||
                !parseHex(strtok(nullptr, " "), &mb) ||
                !parseHex(strtok(nullptr, " "), &pol) ||
                !parseHex(strtok(nullptr, " "), &fv) ||
                !parseHex(strtok(nullptr, " "), &fr) ||
                !parseHex(strtok(nullptr, " "), &fa)) {
                Serial.println(F("ERR parse")); okAll = false; break;
            }
            EEPROM.update(REGION_SLOT_MVADDR  + slot, (uint8_t)mv);
            EEPROM.update(REGION_SLOT_MBIT    + slot, (uint8_t)mb);
            EEPROM.update(REGION_SLOT_POL     + slot, (uint8_t)pol);
            EEPROM.update(REGION_SLOT_FVADDR  + slot, (uint8_t)fv);
            EEPROM.update(REGION_SLOT_FBRETT  + slot, (uint8_t)fr);
            EEPROM.update(REGION_SLOT_FBAVVIK + slot, (uint8_t)fa);
            Serial.println(F("OK"));
        }
        else if (strcmp_P(tag, PSTR("P")) == 0) {
            uint16_t slot, va, bp, bs, tid, ts;
            if (!parseUint(strtok(nullptr, " "), &slot) || slot >= SVBSW_CAP ||
                !parseHex(strtok(nullptr, " "), &va) ||
                !parseHex(strtok(nullptr, " "), &bp) ||
                !parseHex(strtok(nullptr, " "), &bs) ||
                !parseHex(strtok(nullptr, " "), &tid) ||
                !parseHex(strtok(nullptr, " "), &ts)) {
                Serial.println(F("ERR parse")); okAll = false; break;
            }
            EEPROM.update(REGION_SVBSW_VADDR   + slot, (uint8_t)va);
            EEPROM.update(REGION_SVBSW_BITPRI  + slot, (uint8_t)bp);
            EEPROM.update(REGION_SVBSW_BITSEC  + slot, (uint8_t)bs);
            EEPROM.update(REGION_SVBSW_TGTDREI + slot, (uint8_t)tid);
            EEPROM.update(REGION_SVBSW_TGTSLOT + slot, (uint8_t)ts);
            Serial.println(F("OK"));
        }
        else if (strcmp_P(tag, PSTR("D")) == 0) {
            uint16_t mv, pb, cw;
            if (!parseHex(strtok(nullptr, " "), &mv) ||
                !parseHex(strtok(nullptr, " "), &pb) ||
                !parseHex(strtok(nullptr, " "), &cw)) {
                Serial.println(F("ERR parse")); okAll = false; break;
            }
            EEPROM.update(ADDR_DREI_VADDR,   (uint8_t)mv);
            EEPROM.update(ADDR_DREI_PINBASE, (uint8_t)pb);
            EEPROM.update(ADDR_DREI_CWPOL,   (uint8_t)cw);
            Serial.println(F("OK"));
        }
        else if (strcmp_P(tag, PSTR("L")) == 0) {
            uint16_t va, bit;
            if (!parseHex(strtok(nullptr, " "), &va) ||
                !parseHex(strtok(nullptr, " "), &bit)) {
                Serial.println(F("ERR parse")); okAll = false; break;
            }
            EEPROM.update(ADDR_LED_VADDR, (uint8_t)va);
            EEPROM.update(ADDR_LED_BIT,   (uint8_t)bit);
            Serial.println(F("OK"));
        }
        else if (strcmp_P(tag, PSTR("F")) == 0) {
            uint16_t fms, fst, pwm;
            if (!parseHex(strtok(nullptr, " "), &fms) ||
                !parseHex(strtok(nullptr, " "), &fst) ||
                !parseHex(strtok(nullptr, " "), &pwm)) {
                Serial.println(F("ERR parse")); okAll = false; break;
            }
            EEPROM.update(ADDR_FADE_MS,         (uint8_t)(fms >> 8));
            EEPROM.update(ADDR_FADE_MS + 1,     (uint8_t)(fms & 0xFF));
            EEPROM.update(ADDR_FADE_STEPS,      (uint8_t)fst);
            EEPROM.update(ADDR_FADE_PWMUS,      (uint8_t)(pwm >> 8));
            EEPROM.update(ADDR_FADE_PWMUS + 1,  (uint8_t)(pwm & 0xFF));
            Serial.println(F("OK"));
        }
        else {
            Serial.println(F("ERR unknown tag"));
            okAll = false; break;
        }
    }
    if (okAll) Serial.println(F("STORED"));
}

static void cmdSCD() {
    for (uint8_t slot = 0; slot < SWITCH_CAP; slot++) {
        uint8_t mv = EEPROM.read(REGION_SLOT_MVADDR + slot);
        if (mv == 0xFF) continue;
        char buf[40];
        snprintf(buf, sizeof(buf), "S %d %02X %02X %02X %02X %02X %02X",
            slot, mv,
            EEPROM.read(REGION_SLOT_MBIT    + slot),
            EEPROM.read(REGION_SLOT_POL     + slot),
            EEPROM.read(REGION_SLOT_FVADDR  + slot),
            EEPROM.read(REGION_SLOT_FBRETT  + slot),
            EEPROM.read(REGION_SLOT_FBAVVIK + slot));
        Serial.println(buf);
    }
    for (uint8_t slot = 0; slot < SVBSW_CAP; slot++) {
        uint8_t va = EEPROM.read(REGION_SVBSW_VADDR + slot);
        if (va == 0xFF) continue;
        char buf[40];
        snprintf(buf, sizeof(buf), "P %d %02X %02X %02X %02X %02X",
            slot, va,
            EEPROM.read(REGION_SVBSW_BITPRI  + slot),
            EEPROM.read(REGION_SVBSW_BITSEC  + slot),
            EEPROM.read(REGION_SVBSW_TGTDREI + slot),
            EEPROM.read(REGION_SVBSW_TGTSLOT + slot));
        Serial.println(buf);
    }
    uint8_t dv = EEPROM.read(ADDR_DREI_VADDR);
    if (dv != 0xFF) {
        char buf[24];
        snprintf(buf, sizeof(buf), "D %02X %02X %02X",
            dv, EEPROM.read(ADDR_DREI_PINBASE), EEPROM.read(ADDR_DREI_CWPOL));
        Serial.println(buf);
    }
    uint8_t lv = EEPROM.read(ADDR_LED_VADDR);
    if (lv != 0xFF) {
        char buf[16];
        snprintf(buf, sizeof(buf), "L %02X %02X", lv, EEPROM.read(ADDR_LED_BIT));
        Serial.println(buf);
    }
    {
        uint16_t fms = ((uint16_t)EEPROM.read(ADDR_FADE_MS) << 8)     | EEPROM.read(ADDR_FADE_MS + 1);
        uint8_t  fst = EEPROM.read(ADDR_FADE_STEPS);
        uint16_t pwm = ((uint16_t)EEPROM.read(ADDR_FADE_PWMUS) << 8)  | EEPROM.read(ADDR_FADE_PWMUS + 1);
        char buf[24];
        snprintf(buf, sizeof(buf), "F %04X %02X %04X", fms, fst, pwm);
        Serial.println(buf);
    }
    Serial.println(F("END"));
}

// ---------------------------------------------------------------------------
// Command dispatcher
// ---------------------------------------------------------------------------
static void dispatch(char* line) {
    char* cmd = strtok(line, " ");
    if (!cmd || !*cmd) return;

    if      (strcmp_P(cmd, PSTR("PING"))  == 0) cmdPing();
    else if (strcmp_P(cmd, PSTR("SI"))    == 0) cmdSI();
    else if (strcmp_P(cmd, PSTR("ER"))    == 0) cmdER();
    else if (strcmp_P(cmd, PSTR("EW"))    == 0) cmdEW();
    else if (strcmp_P(cmd, PSTR("EC"))    == 0) cmdEC();
    else if (strcmp_P(cmd, PSTR("MDIR"))  == 0) cmdMDIR();
    else if (strcmp_P(cmd, PSTR("MPU"))   == 0) cmdMPU();
    else if (strcmp_P(cmd, PSTR("MW"))    == 0) cmdMW();
    else if (strcmp_P(cmd, PSTR("MR"))    == 0) cmdMR();
    else if (strcmp_P(cmd, PSTR("MPOLL")) == 0) cmdMPOLL();
    else if (strcmp_P(cmd, PSTR("MBIT"))  == 0) cmdMBIT();
    else if (strcmp_P(cmd, PSTR("HWU"))   == 0) cmdHWU();
    else if (strcmp_P(cmd, PSTR("HWD"))   == 0) cmdHWD();
    else if (strcmp_P(cmd, PSTR("SW"))    == 0) cmdSW();
    else if (strcmp_P(cmd, PSTR("SR"))    == 0) cmdSR();
    else if (strcmp_P(cmd, PSTR("SCU"))   == 0) cmdSCU();
    else if (strcmp_P(cmd, PSTR("SCD"))   == 0) cmdSCD();
    else if (strcmp_P(cmd, PSTR("RST"))   == 0) softReset();
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

    Wire.begin();
    apply_all_board_hw(); // data-driven bring-up; a no-op until hardware.json is uploaded once

    status(F("LysKontroll Firmware Phase 1"));
    status(F("Commands: PING SI ER EW EC MDIR MPU MW MR MPOLL MBIT HWU HWD SW SR SCU SCD RST"));
}

void loop() {
    char line[48];
    if (!readLine(line, sizeof(line))) return;
    dispatch(line);
}
