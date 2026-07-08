// Simulator.ino
// Simulates the LysKontroll Arduino firmware over serial so the DrammenMJKConfig
// C# program can be exercised without real hardware.
//
// Protocol:
//   C# → Arduino : single chars (no newline) for commands and keypresses
//                  full lines ending \n only during routing matrix upload
//   Arduino → C# : Serial.println() — lines ending \r\n
//                  Lines starting with '!' are status messages, always displayed.
//                  All other lines are protocol responses (READY / OK / STORED / END / ...).

#include <EEPROM.h>

// ---------------------------------------------------------------------------
// EEPROM layout (mirrors the real firmware — same addresses, same semantics)
// ---------------------------------------------------------------------------
static const uint16_t EEPROM_MAGIC_ADDR   = 0x01;
static const uint8_t  EEPROM_MAGIC_VALUE  = 0xA5;

static const uint16_t REGION0_DREIESKIVE = 0x0A;   // last commanded position
static const uint16_t REGION1_BASE       = 0x10;   // motor pin-pair base, B–I (8 bytes)
static const uint16_t REGION1_POL        = 0x18;   // motor polarity, B–I     (8 bytes)
static const uint16_t REGION2_BASE       = 0x20;   // feedback Rett pin, B–I  (8 bytes)
static const uint16_t REGION2_AVVIK      = 0x28;   // feedback Avvik pin, B–I (8 bytes)
static const uint16_t REGION3_BASE       = 0x30;   // manual switch pin, B–I  (8 bytes)
static const uint16_t REGION4_RETT       = 0x40;   // Rett LED pin, B–I       (8 bytes)
static const uint16_t REGION4_AVVIK      = 0x48;   // Avvik LED pin, B–I      (8 bytes)
static const uint16_t REGION5_MOTOR_PIN  = 0x50;   // Dreieskive motor pin
static const uint16_t REGION5_MOTOR_POL  = 0x51;   // Dreieskive CW polarity
static const uint16_t REGION5_SW_CW      = 0x52;   // Dreieskive switch CW pin
static const uint16_t REGION5_SW_CCW     = 0x53;   // Dreieskive switch CCW pin
static const uint16_t REGION5_MOMENT     = 0x54;   // Moment button pin
static const uint16_t REGION6_BASE       = 0x60;   // LED routing matrix (144 bytes)

// ---------------------------------------------------------------------------
// Simulated hardware — fixed internal wiring known only to the simulator
// ---------------------------------------------------------------------------
// 9 motor pairs: pairs 0–7 = Pens B–I, pair 4 happens to be the Dreieskive.
// (Motor pair index 4 = "no feedback" motor)
static const uint8_t SIM_DREIESKIVE_PAIR = 4;

// Simulated feedback pin pairs for each Pens (0-based indices into a 16-pin space)
static const uint8_t SIM_FB_RETT[8]  = {  0,  2,  4,  6,  8, 10, 12, 14 };
static const uint8_t SIM_FB_AVVIK[8] = {  1,  3,  5,  7,  9, 11, 13, 15 };

// Simulated panel switch pins for each Pens B–I
static const uint8_t SIM_SW_PIN[8]   = { 16, 17, 18, 19, 20, 21, 22, 23 };

// Simulated Rett and Avvik LED pins for each Pens B–I
static const uint8_t SIM_LED_RETT[8]  = { 0, 1, 2, 3,  4,  5,  6,  7  };
static const uint8_t SIM_LED_AVVIK[8] = { 8, 9,10,11, 12, 13, 14, 15  };

// Simulated Dreieskive switch pins
static const uint8_t SIM_DREI_SW_CW  = 24;
static const uint8_t SIM_DREI_SW_CCW = 25;
static const uint8_t SIM_DREI_MOTOR  = 0;   // motor pin-pair base

// Simulated moment button pin
static const uint8_t SIM_MOMENT_PIN  = 26;

// Simulated current Pens positions: 0=Rett, 1=Avvik
static uint8_t simPensPos[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };

// ---------------------------------------------------------------------------
// Top-level state
// ---------------------------------------------------------------------------
enum class Mode : uint8_t { NORMAL, CONFIG, DEBUG, VERIFY };
static Mode g_mode = Mode::NORMAL;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static void status(const char* msg) {
    // Status messages always shown on C# side regardless of capture mode
    Serial.print('!');
    Serial.println(msg);
}

static void statusf(const char* fmt, ...) {
    char buf[80];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    status(buf);
}

static bool eepromConfigured() {
    return EEPROM.read(EEPROM_MAGIC_ADDR) == EEPROM_MAGIC_VALUE;
}

static char waitKey() {
    while (!Serial.available()) { /* spin */ }
    return (char)toupper(Serial.read());
}

// ---------------------------------------------------------------------------
// Command 1 — Motor scan (single continuous scan)
// ---------------------------------------------------------------------------
static void cmdMotorScan() {
    status("Motor scan started. 9 pairs total (8 Pens + 1 Dreieskive).");
    status("Keys: Y=confirm  N=skip  R=Rett  A=Avvik  1=CW is 01  2=CW is 10  X=stop  Q=quit");

    // Track which Pens letters have been assigned (bitmask, bit0=B)
    uint8_t assignedPens = 0;
    bool dreiekiveFound = false;

    for (uint8_t pair = 0; pair < 9; pair++) {
        statusf("--- Pair %d: driving to '10' ---", pair);

        bool isDreieskive = (pair == SIM_DREIESKIVE_PAIR);

        if (isDreieskive) {
            // Simulate timeout: no feedback change
            statusf("Pair %d: timeout — no feedback switch changed.", pair);
            statusf("This is the Dreieskive motor. Confirm? (Y/N)");

            char k = waitKey();
            if (k == 'Q') { status("Motor scan aborted."); return; }
            if (k != 'Y') { status("Skipping."); continue; }

            statusf("Is CW direction '01' or '10'? Press 1 or 2.");
            k = waitKey();
            if (k == 'Q') { status("Motor scan aborted."); return; }
            uint8_t cwPol = (k == '2') ? 1 : 0;
            EEPROM.update(REGION5_MOTOR_PIN, SIM_DREI_MOTOR);
            EEPROM.update(REGION5_MOTOR_POL, cwPol);
            dreiekiveFound = true;
            statusf("Dreieskive stored. CW = '%s'.", cwPol == 0 ? "01" : "10");

        } else {
            // Determine which simulated Pens this motor belongs to.
            // Pairs 0–3 → Pens B–E, pairs 5–8 → Pens F–I.
            uint8_t pensIdx = (pair < SIM_DREIESKIVE_PAIR) ? pair : (pair - 1);
            char pensLetter = 'B' + pensIdx;

            statusf("Pair %d: feedback changed on pins %d (Rett) and %d (Avvik).",
                    pair, SIM_FB_RETT[pensIdx], SIM_FB_AVVIK[pensIdx]);
            statusf("Pens %c moved. Correct? (Y/N)", pensLetter);

            char k = waitKey();
            if (k == 'Q') { status("Motor scan aborted."); return; }
            if (k == 'X') { status("Emergency stop."); return; }
            if (k != 'Y') { status("Skipping."); continue; }

            statusf("Is Pens %c now at Rett or Avvik? (R/A)", pensLetter);
            k = waitKey();
            if (k == 'Q') { status("Motor scan aborted."); return; }
            uint8_t pos = (k == 'A') ? 1 : 0;
            simPensPos[pensIdx] = pos;

            EEPROM.update(REGION1_BASE + pensIdx, pair);
            EEPROM.update(REGION1_POL  + pensIdx, pos);      // polarity: 0=01 is Rett
            EEPROM.update(REGION2_BASE + pensIdx, SIM_FB_RETT[pensIdx]);
            EEPROM.update(REGION2_AVVIK + pensIdx, SIM_FB_AVVIK[pensIdx]);
            assignedPens |= (1 << pensIdx);
            statusf("Pens %c stored (pair %d, now at %s).",
                    pensLetter, pair, pos == 0 ? "Rett" : "Avvik");
        }
    }

    status("Motor scan complete.");
    if (!dreiekiveFound) status("WARNING: Dreieskive was not identified.");
}

// ---------------------------------------------------------------------------
// Command 2 — Manual switch mapping
// ---------------------------------------------------------------------------
static void cmdSwitchMapping() {
    status("Switch mapping. Type B–I to select a Pens, Q to finish.");

    while (true) {
        char k = waitKey();
        if (k == 'Q') { status("Switch mapping done."); return; }
        if (k < 'B' || k > 'I') { status("Type B–I to select a Pens."); continue; }

        uint8_t idx = k - 'B';
        statusf("Flip the panel switch for Pens %c to Avvik then back to Rett...", k);
        delay(2000);   // simulate operator flipping the switch

        statusf("Detected switch on simulated pin %d. Correct? (Y/N)", SIM_SW_PIN[idx]);
        char confirm = waitKey();
        if (confirm != 'Y') { status("Retry: flip the switch again."); continue; }

        EEPROM.update(REGION3_BASE + idx, SIM_SW_PIN[idx]);
        statusf("Pens %c switch stored (pin %d).", k, SIM_SW_PIN[idx]);
    }
}

// ---------------------------------------------------------------------------
// Command 3 — LED mapping
// ---------------------------------------------------------------------------
static void cmdLedMapping() {
    status("LED mapping. Type B–I to select a Pens, Q to finish.");
    status("N=next LED  P=prev LED  S=save this LED");

    while (true) {
        char k = waitKey();
        if (k == 'Q') { status("LED mapping done."); return; }
        if (k < 'B' || k > 'I') { status("Type B–I to select a Pens."); continue; }

        uint8_t idx = k - 'B';
        statusf("Pens %c — configuring RETT LED. N/P to step, S to save.", k);

        uint8_t ledCursor = SIM_LED_RETT[idx];
        while (true) {
            statusf("  LED %d lit.", ledCursor);
            char nav = waitKey();
            if (nav == 'N') { ledCursor = (ledCursor + 1) % 16; }
            else if (nav == 'P') { ledCursor = (ledCursor + 15) % 16; }
            else if (nav == 'S') {
                EEPROM.update(REGION4_RETT + idx, ledCursor);
                statusf("Pens %c Rett LED saved as %d.", k, ledCursor);
                break;
            }
            else if (nav == 'Q') { status("LED mapping done."); return; }
        }

        statusf("Pens %c — configuring AVVIK LED. N/P to step, S to save.", k);
        ledCursor = SIM_LED_AVVIK[idx];
        while (true) {
            statusf("  LED %d lit.", ledCursor);
            char nav = waitKey();
            if (nav == 'N') { ledCursor = (ledCursor + 1) % 16; }
            else if (nav == 'P') { ledCursor = (ledCursor + 15) % 16; }
            else if (nav == 'S') {
                EEPROM.update(REGION4_AVVIK + idx, ledCursor);
                statusf("Pens %c Avvik LED saved as %d.", k, ledCursor);
                break;
            }
            else if (nav == 'Q') { status("LED mapping done."); return; }
        }
    }
}

// ---------------------------------------------------------------------------
// Command M — Moment button
// ---------------------------------------------------------------------------
static void cmdMomentSwitch() {
    status("Press the physical moment button when ready...");
    delay(3000);   // simulate operator pressing the button
    statusf("Detected button on simulated pin %d. Correct? (Y/N)", SIM_MOMENT_PIN);

    while (true) {
        char k = waitKey();
        if (k == 'Y') {
            EEPROM.update(REGION5_MOMENT, SIM_MOMENT_PIN);
            statusf("Moment button stored (pin %d).", SIM_MOMENT_PIN);
            return;
        }
        if (k == 'N') {
            status("Retrying — press the button again...");
            delay(3000);
            statusf("Detected pin %d again. Correct? (Y/N)", SIM_MOMENT_PIN);
        }
        if (k == 'Q') { status("Moment button config aborted."); return; }
    }
}

// ---------------------------------------------------------------------------
// Command D — Dreieskive switch
// ---------------------------------------------------------------------------
static void cmdDreieskiveSwitch() {
    status("Move Dreieskive switch to MIDDLE, then confirm with Y.");
    char k = waitKey();
    if (k == 'Q') return;

    status("Move switch to CW (clockwise)...");
    delay(2000);
    statusf("CW detected on simulated pin %d.", SIM_DREI_SW_CW);
    EEPROM.update(REGION5_SW_CW, SIM_DREI_SW_CW);

    status("Move switch back to MIDDLE, then to CCW (counter-clockwise)...");
    delay(2000);
    statusf("CCW detected on simulated pin %d.", SIM_DREI_SW_CCW);
    EEPROM.update(REGION5_SW_CCW, SIM_DREI_SW_CCW);

    status("Dreieskive switch stored.");
}

// ---------------------------------------------------------------------------
// Command 4 — Routing matrix upload / download
// ---------------------------------------------------------------------------
static const uint8_t LED_COUNT     = 16;
static const uint8_t MAX_COND      = 4;
static const uint8_t STRIDE        = 1 + MAX_COND * 2;   // 9 bytes per LED

static void cmdRoutingMatrix() {
    status("Routing matrix: U=upload  D=download  Q=done");

    while (true) {
        char k = waitKey();
        if (k == 'Q') { status("Routing matrix done."); return; }

        if (k == 'U') {
            // Upload: C# sends READY-ack then 16 hex lines then END
            Serial.println("READY");
            Serial.setTimeout(5000);

            uint8_t buf[LED_COUNT * STRIDE];
            bool ok = true;

            for (uint8_t i = 0; i < LED_COUNT && ok; i++) {
                String line = Serial.readStringUntil('\n');
                line.trim();
                if (line == "ABORT") { status("Upload aborted by host."); ok = false; break; }

                // Parse hex tokens into buf
                uint16_t base = i * STRIDE;
                char tmp[40];
                line.toCharArray(tmp, sizeof(tmp));
                char* token = strtok(tmp, " ");
                if (!token) { Serial.println("ERR"); ok = false; break; }

                uint8_t count = (uint8_t)strtoul(token, nullptr, 16);
                buf[base] = count;
                uint8_t expected = (count == 0xFF || count == 0x00) ? 0 : count * 2;
                for (uint8_t t = 0; t < expected; t++) {
                    token = strtok(nullptr, " ");
                    if (!token) { Serial.println("ERR"); ok = false; break; }
                    buf[base + 1 + t] = (uint8_t)strtoul(token, nullptr, 16);
                }
                if (ok) Serial.println("OK");
            }

            if (ok) {
                String end = Serial.readStringUntil('\n');
                end.trim();
                if (end == "END") {
                    for (uint16_t i = 0; i < LED_COUNT * STRIDE; i++)
                        EEPROM.update(REGION6_BASE + i, buf[i]);
                    Serial.println("STORED");
                    status("Routing matrix written to EEPROM.");
                } else {
                    status("Expected END, upload not committed.");
                }
            }
            Serial.setTimeout(1000);

        } else if (k == 'D') {
            // Download: send 16 hex lines then END
            status("Sending routing matrix...");
            for (uint8_t i = 0; i < LED_COUNT; i++) {
                uint16_t base = REGION6_BASE + i * STRIDE;
                uint8_t count = EEPROM.read(base);
                if (count == 0xFF || count == 0x00) {
                    Serial.println(count == 0xFF ? "FF" : "00");
                    continue;
                }
                String line = String(count, HEX);
                line.toUpperCase();
                for (uint8_t c = 0; c < count; c++) {
                    uint8_t r = EEPROM.read(base + 1 + c * 2);
                    uint8_t a = EEPROM.read(base + 2 + c * 2);
                    char hex[8];
                    snprintf(hex, sizeof(hex), " %02X %02X", r, a);
                    line += hex;
                }
                Serial.println(line);
            }
            Serial.println("END");
            status("Routing matrix sent.");
        }
    }
}

// ---------------------------------------------------------------------------
// Command R — Reset
// ---------------------------------------------------------------------------
static void cmdReset() {
    status("Erasing EEPROM regions 1–6...");
    for (uint16_t addr = 0x10; addr < 0xF0; addr++)
        EEPROM.update(addr, 0xFF);
    EEPROM.update(EEPROM_MAGIC_ADDR, 0xFF);
    status("Reset complete. All config erased.");
}

// ---------------------------------------------------------------------------
// Config mode
// ---------------------------------------------------------------------------
static void runConfig() {
    status("Config mode. Commands: 1 2 3 M D 4 R Q");

    while (true) {
        char k = waitKey();
        switch (k) {
            case '1': cmdMotorScan();       break;
            case '2': cmdSwitchMapping();   break;
            case '3': cmdLedMapping();      break;
            case 'M': cmdMomentSwitch();    break;
            case 'D': cmdDreieskiveSwitch(); break;
            case '4': cmdRoutingMatrix();   break;
            case 'R': cmdReset();           break;
            case 'Q': status("Exiting config mode."); return;
            default:  statusf("Unknown config command: %c", k); break;
        }
    }
}

// ---------------------------------------------------------------------------
// Debug mode (stub)
// ---------------------------------------------------------------------------
static void runDebug() {
    status("Debug mode. A=toggle all LEDs  L=snake  Q=exit");
    while (true) {
        char k = waitKey();
        if (k == 'Q') { status("Exiting debug mode."); return; }
        if (k == 'A') { status("[SIM] All LEDs toggled."); }
        if (k == 'L') { status("[SIM] Snake animation running — press Q to stop."); }
    }
}

// ---------------------------------------------------------------------------
// Verify mode (stub)
// ---------------------------------------------------------------------------
static void runVerify() {
    status("Verify mode. 1=motors and feedback  Q=exit");
    while (true) {
        char k = waitKey();
        if (k == 'Q') { status("Exiting verify mode."); return; }
        if (k == '1') {
            status("Verifying motors...");
            for (uint8_t i = 0; i < 8; i++) {
                char letter = 'B' + i;
                statusf("Pens %c: PASS (simulated).", letter);
            }
            status("All Penser verified.");
        }
    }
}

// ---------------------------------------------------------------------------
// setup / loop
// ---------------------------------------------------------------------------
void setup() {
    Serial.begin(115200);
    Serial.setTimeout(1000);

    status("LysKontroll Simulator v1.0");
    if (eepromConfigured())
        status("EEPROM: configured.");
    else
        status("EEPROM: blank (run Config mode to configure).");
    status("Commands: C=Config  D=Debug  V=Verify  Q=Quit");
}

void loop() {
    if (!Serial.available()) return;

    char c = (char)toupper(Serial.read());
    switch (c) {
        case 'C': runConfig();  break;
        case 'D': runDebug();   break;
        case 'V': runVerify();  break;
        case 'Q': status("Simulator idle."); break;
        default:  statusf("Unknown command: %c. Use C/D/V/Q.", c); break;
    }
}
