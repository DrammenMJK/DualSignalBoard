// Simulator.ino
// Simulates the LysKontroll Arduino firmware over serial so the DrammenMJKConfig
// C# program can be exercised without real hardware.
//
// Protocol:
//   C# -> Arduino : single chars (no newline) for commands/keypresses
//                   full lines ending \n only during routing matrix upload
//   Arduino -> C# : Serial.println() -- lines ending \r\n
//                   Lines starting with '!' are status messages, always displayed.
//                   All other lines are protocol responses (READY/OK/STORED/END/ERR).
//
// NOTE: All string literals use F() to keep them in flash, not SRAM.

#include <EEPROM.h>
#include <avr/pgmspace.h>
#include <avr/wdt.h>

// ---------------------------------------------------------------------------
// Site configuration -- adjust per site
// ---------------------------------------------------------------------------
#define MAX_PENS         26      // absolute maximum: A-Z
#define FIRST_PENS       'B'    // this site: first Pens letter (Fossli)
#define LAST_PENS        'I'    // this site: last Pens letter  (Fossli)
#define NUM_LED_OUTPUTS  16     // physical LED hardware outputs on this site

#define ESC  '\x1B'        // Escape key -- used for quit/abort inside commands

// ---------------------------------------------------------------------------
// EEPROM layout  (regions sized for MAX_PENS entries)
// ---------------------------------------------------------------------------
#define EEPROM_MAGIC_ADDR   0x01
#define EEPROM_MAGIC_VALUE  0xA5

#define REGION1_BASE        0x10   // motor pin-pair, FIRST_PENS onward (MAX_PENS bytes)
#define REGION1_POL         0x30   // motor polarity, FIRST_PENS onward (MAX_PENS bytes)
#define REGION2_RETT        0x50   // feedback Rett pin (MAX_PENS bytes)
#define REGION2_AVVIK       0x70   // feedback Avvik pin (MAX_PENS bytes)
#define REGION3_BASE        0x90   // manual switch pin (MAX_PENS bytes)
#define REGION4_RETT        0xB0   // Rett LED pin (MAX_PENS bytes)
#define REGION4_AVVIK       0xD0   // Avvik LED pin (MAX_PENS bytes)
#define REGION5_MOTOR_PIN   0xF0   // Dreieskive motor pin
#define REGION5_MOTOR_POL   0xF1   // Dreieskive CW polarity
#define REGION5_SW_CW       0xF2   // Dreieskive switch CW pin
#define REGION5_SW_CCW      0xF3   // Dreieskive switch CCW pin
#define REGION5_MOMENT      0xF4   // Moment button pin
#define REGION6_BASE       0x100   // LED routing matrix (LED_COUNT * STRIDE bytes)
#define LED_NO_PIN          0xFE   // stored in REGION4_* when Pens position has no physical LED
#define LED_COUNT  ((LAST_PENS - FIRST_PENS + 1) * 2)  // logical LEDs: Rett+Avvik per Pens
#define MAX_COND    4
#define STRIDE      (1 + MAX_COND * 2)

// ---------------------------------------------------------------------------
// Simulated hardware -- fixed internal wiring known only to the simulator
// ---------------------------------------------------------------------------
#define SIM_DREIESKIVE_PAIR  4     // pair index 4 has no feedback (the Dreieskive)
#define SIM_PENS_COUNT       8     // this simulator has 8 Pens (B-I)

static const uint8_t SIM_FB_RETT[SIM_PENS_COUNT]  = {  0,  2,  4,  6,  8, 10, 12, 14 };
static const uint8_t SIM_FB_AVVIK[SIM_PENS_COUNT] = {  1,  3,  5,  7,  9, 11, 13, 15 };
static const uint8_t SIM_SW_PIN[SIM_PENS_COUNT]   = { 16, 17, 18, 19, 20, 21, 22, 23 };
static const uint8_t SIM_LED_RETT[SIM_PENS_COUNT] = {  0,  1,  2,  3,  4,  5,  6,  7 };
static const uint8_t SIM_LED_AVVIK[SIM_PENS_COUNT]= {  8,  9, 10, 11, 12, 13, 14, 15 };
#define SIM_DREI_SW_CW   24
#define SIM_DREI_SW_CCW  25
#define SIM_DREI_MOTOR   0
#define SIM_MOMENT_PIN   26

// Indexed by k-'A' (universal), size MAX_PENS
static uint8_t simPensPos[MAX_PENS];

// ---------------------------------------------------------------------------
// Status helpers -- format strings stay in flash via F() / PGM_P
// ---------------------------------------------------------------------------
static void status(const __FlashStringHelper* msg) {
    Serial.print('!');
    Serial.println(msg);
}

static void statusf(const __FlashStringHelper* fmt, ...) {
    char buf[96];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf_P(buf, sizeof(buf), (PGM_P)fmt, ap);
    va_end(ap);
    Serial.print('!');
    Serial.println(buf);
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static bool eepromConfigured() {
    return EEPROM.read(EEPROM_MAGIC_ADDR) == EEPROM_MAGIC_VALUE;
}

static char waitKey() {
    while (!Serial.available()) {}
    return (char)toupper(Serial.read());
}

static void readLine(char* buf, uint8_t maxLen) {
    uint8_t len = Serial.readBytesUntil('\n', buf, maxLen - 1);
    buf[len] = '\0';
    if (len > 0 && buf[len - 1] == '\r') buf[--len] = '\0';
}

static void softReset() {
    status(F("Simulator resetting..."));
    delay(100);
    wdt_enable(WDTO_15MS);
    while (true) {}
}

// ---------------------------------------------------------------------------
// Command 1 -- Motor scan (single continuous scan)
// ---------------------------------------------------------------------------

// Build "[B,C,E,...]" listing unassigned Pens into buf.
// assigned[] is indexed by k-'A'; only letters FIRST_PENS..LAST_PENS are shown.
static void buildRemaining(char* buf, uint8_t bufLen, const bool* assigned) {
    uint8_t pos = 0;
    buf[pos++] = '[';
    bool first = true;
    for (uint8_t i = (uint8_t)(FIRST_PENS - 'A'); i <= (uint8_t)(LAST_PENS - 'A'); i++) {
        if (!assigned[i]) {
            if (!first && pos < bufLen - 2) buf[pos++] = ',';
            if (pos < bufLen - 1) buf[pos++] = (char)('A' + i);
            first = false;
        }
    }
    if (pos < bufLen - 1) buf[pos++] = ']';
    buf[pos] = '\0';
}

static void cmdMotorScan() {
    status(F("Motor scan. Watch the layout to see what moves."));
    statusf(F("Type the Pens letter, 0=skip, X=stop, Esc=abort. (%c-%c)"),
            (char)FIRST_PENS, (char)LAST_PENS);

    // assigned[] and assignedPair[] indexed by k-'A' (universal)
    bool    assigned[MAX_PENS];
    uint8_t assignedPair[MAX_PENS];
    memset(assigned,     0,    sizeof(assigned));
    memset(assignedPair, 0xFF, sizeof(assignedPair));
    bool dreieskiveDone = false;

    // Pre-populate from EEPROM so already-configured pairs are skipped
    bool pairDone[9];
    memset(pairDone, 0, sizeof(pairDone));
    uint8_t numPens = (uint8_t)(LAST_PENS - FIRST_PENS + 1);
    for (uint8_t i = 0; i < numPens; i++) {
        uint8_t storedPair = EEPROM.read(REGION1_BASE + i);
        if (storedPair < 9) {
            uint8_t pensIdx = i + (uint8_t)(FIRST_PENS - 'A');
            assigned[pensIdx]     = true;
            assignedPair[pensIdx] = storedPair;
            pairDone[storedPair]  = true;
        }
    }
    if (EEPROM.read(REGION5_MOTOR_PIN) != 0xFF) {
        dreieskiveDone = true;
        pairDone[SIM_DREIESKIVE_PAIR] = true;
    }

    while (true) {
        // If everything is configured, offer single-Pens re-edit by letter
        bool allDone = dreieskiveDone;
        if (allDone)
            for (uint8_t i = (uint8_t)(FIRST_PENS-'A'); i <= (uint8_t)(LAST_PENS-'A'); i++)
                if (!assigned[i]) { allDone = false; break; }

        if (allDone) {
            char sel;
            while (true) {
                statusf(F("All configured. Re-configure which Pens? (%c-%c Esc=done):"),
                        (char)FIRST_PENS, (char)LAST_PENS);
                sel = waitKey();
                if (sel == ESC) { status(F("Motor scan done.")); return; }
                if (sel >= FIRST_PENS && sel <= LAST_PENS) break;
            }
            uint8_t editPensIdx = sel - 'A';
            uint8_t clearPair   = assignedPair[editPensIdx];
            assigned[editPensIdx]     = false;
            assignedPair[editPensIdx] = 0xFF;
            if (clearPair < 9) pairDone[clearPair] = false;
        }

        for (uint8_t pair = 0; pair < 9; pair++) {
            if (pairDone[pair]) goto next_pair;
            statusf(F("--- Pair %d: running ---"), pair);

            if (pair == SIM_DREIESKIVE_PAIR) {
                if (dreieskiveDone) {
                    status(F("Unexpected second timeout -- Dreieskive already assigned. Skipping."));
                    pairDone[pair] = true;
                    goto next_pair;
                }
                statusf(F("Pair %d: no feedback detected -- this is the Dreieskive."), pair);
                status(F("CW direction: '01' or '10'? Press 1 or 2."));
                char k;
                while (true) {
                    k = waitKey();
                    if (k == ESC) { status(F("Motor scan aborted.")); return; }
                    if (k == '1' || k == '2') break;
                    status(F("Press 1 or 2."));
                }
                uint8_t cwPol = (k == '2') ? 1 : 0;
                EEPROM.update(REGION5_MOTOR_PIN, SIM_DREI_MOTOR);
                EEPROM.update(REGION5_MOTOR_POL, cwPol);
                statusf(F("Dreieskive stored (pair %d). CW = '%S'."),
                        pair, cwPol == 0 ? PSTR("01") : PSTR("10"));
                dreieskiveDone = true;
                pairDone[pair] = true;

            } else {
                // If all Pens are assigned, nothing left to do with this pair
                bool allAssigned = true;
                for (uint8_t i = (uint8_t)(FIRST_PENS - 'A'); i <= (uint8_t)(LAST_PENS - 'A'); i++)
                    if (!assigned[i]) { allAssigned = false; break; }
                if (allAssigned) { pairDone[pair] = true; goto next_pair; }

                // simIdx: maps pair index to simulator hardware array index (0-based, skips Dreieskive)
                uint8_t simIdx = (pair < SIM_DREIESKIVE_PAIR) ? pair : (pair - 1);

                // Ask which Pens moved, with duplicate detection
                char    k;
                uint8_t pensIdx;    // k - 'A', index into assigned[]/assignedPair[]/simPensPos[]
                uint8_t eepromIdx;  // k - FIRST_PENS, index into EEPROM regions and sim arrays
                while (true) {
                    char remaining[60];
                    buildRemaining(remaining, sizeof(remaining), assigned);
                    statusf(F("Pair %d: pins %d,%d. Which Pens? (%s 0=skip)"),
                            pair, SIM_FB_RETT[simIdx], SIM_FB_AVVIK[simIdx], remaining);
                    k = waitKey();
                    if (k == ESC) { status(F("Motor scan aborted.")); return; }
                    if (k == 'X') { status(F("Emergency stop.")); return; }
                    if (k == '0') { status(F("Pair skipped.")); goto next_pair; }
                    if (k < FIRST_PENS || k > LAST_PENS) continue;

                    pensIdx   = k - 'A';
                    eepromIdx = k - FIRST_PENS;

                    if (assigned[pensIdx]) {
                        statusf(F("Pens %c was already assigned (pair %d). Options:"), k, assignedPair[pensIdx]);
                        status(F("  Y = override  (you made an error earlier)"));
                        status(F("  N = re-enter  (you made an error just now)"));
                        char ans = waitKey();
                        if (ans == ESC) { status(F("Motor scan aborted.")); return; }
                        if (ans == 'Y') {
                            statusf(F("Overriding previous assignment of Pens %c."), k);
                            break;
                        }
                        status(F("OK -- enter the correct Pens letter."));
                        continue;
                    }
                    break;
                }

                statusf(F("Is Pens %c now at Rett or Avvik? (R/A)"), k);
                char ra;
                while (true) {
                    ra = waitKey();
                    if (ra == ESC) { status(F("Motor scan aborted.")); return; }
                    if (ra == 'R' || ra == 'A') break;
                    status(F("Press R for Rett or A for Avvik."));
                }
                uint8_t pensPos = (ra == 'A') ? 1 : 0;
                simPensPos[pensIdx] = pensPos;

                uint8_t firedPin = SIM_FB_RETT[simIdx];
                uint8_t otherPin = SIM_FB_AVVIK[simIdx];
                uint8_t rettPin  = (pensPos == 0) ? firedPin : otherPin;
                uint8_t avvikPin = (pensPos == 0) ? otherPin : firedPin;

                EEPROM.update(REGION1_BASE   + eepromIdx, pair);
                EEPROM.update(REGION1_POL    + eepromIdx, pensPos);
                EEPROM.update(REGION2_RETT   + eepromIdx, rettPin);
                EEPROM.update(REGION2_AVVIK  + eepromIdx, avvikPin);

                assigned[pensIdx]     = true;
                assignedPair[pensIdx] = pair;
                pairDone[pair]        = true;

                statusf(F("Pens %c stored (pair %d, Rett pin %d, Avvik pin %d, now at %S)."),
                        k, pair, rettPin, avvikPin, pensPos == 0 ? PSTR("Rett") : PSTR("Avvik"));
            }
            next_pair:;

            // Exit early if all Pens and Dreieskive are done
            if (dreieskiveDone) {
                bool allAssigned = true;
                for (uint8_t i = (uint8_t)(FIRST_PENS - 'A'); i <= (uint8_t)(LAST_PENS - 'A'); i++)
                    if (!assigned[i]) { allAssigned = false; break; }
                if (allAssigned) break;
            }
        }

        // After the pair scan: if still not all configured (pairs skipped), exit normally
        bool allDone2 = dreieskiveDone;
        if (allDone2)
            for (uint8_t i = (uint8_t)(FIRST_PENS-'A'); i <= (uint8_t)(LAST_PENS-'A'); i++)
                if (!assigned[i]) { allDone2 = false; break; }
        if (!allDone2) break;  // incomplete -- exit outer while
        // else: all done -> loop back to offer single-Pens re-edit
    }

    status(F("Motor scan complete. Press Esc to return to config menu."));
    while (waitKey() != ESC) {}
}

// ---------------------------------------------------------------------------
// Command 2 -- Manual switch mapping
// ---------------------------------------------------------------------------
static void cmdSwitchMapping() {
    // Track which Pens have a switch mapped (indexed by k-'A')
    bool assigned[MAX_PENS];
    memset(assigned, 0, sizeof(assigned));
    uint8_t numPens = (uint8_t)(LAST_PENS - FIRST_PENS + 1);
    for (uint8_t i = 0; i < numPens; i++)
        if (EEPROM.read(REGION3_BASE + i) != 0xFF)
            assigned[i + (uint8_t)(FIRST_PENS - 'A')] = true;

    while (true) {
        char remaining[60];
        buildRemaining(remaining, sizeof(remaining), assigned);
        statusf(F("Switch map %s (Esc=done):"), remaining);
        char k = waitKey();
        if (k == ESC) { status(F("Switch mapping done.")); return; }
        if (k < FIRST_PENS || k > LAST_PENS) continue;

        uint8_t eepromIdx = k - FIRST_PENS;
        if (eepromIdx >= SIM_PENS_COUNT) continue;

        uint8_t pair = EEPROM.read(REGION1_BASE + eepromIdx);
        if (pair == 0xFF) {
            statusf(F("Pens %c motor not configured -- run Command 1 first."), k);
            continue;
        }

        statusf(F("Driving Pens %c to Rett (pair %d, simulated)."), k, pair);
        delay(300);
        statusf(F("Flip panel switch for Pens %c to Avvik then back to Rett..."), k);
        delay(2000);

        EEPROM.update(REGION3_BASE + eepromIdx, SIM_SW_PIN[eepromIdx]);
        assigned[k - 'A'] = true;
        statusf(F("Pens %c switch stored (pin %d)."), k, SIM_SW_PIN[eepromIdx]);
    }
}

// ---------------------------------------------------------------------------
// Command 3 -- LED mapping
// ---------------------------------------------------------------------------

// Build "[0,3,5,...]" of unused LED indices (0..LED_COUNT-1) into buf.
static void buildRemainingLeds(char* buf, uint8_t bufLen, const bool* used) {
    uint8_t pos = 0;
    if (pos < bufLen) buf[pos++] = '[';
    bool first = true;
    for (uint8_t i = 0; i < NUM_LED_OUTPUTS; i++) {
        if (!used[i]) {
            if (!first && pos + 3 < bufLen) buf[pos++] = ',';
            if (i >= 10 && pos + 2 < bufLen) { buf[pos++] = '1'; buf[pos++] = (char)('0' + i - 10); }
            else if (pos + 1 < bufLen)        { buf[pos++] = (char)('0' + i); }
            first = false;
        }
    }
    if (pos < bufLen) buf[pos++] = ']';
    if (pos < bufLen) buf[pos] = '\0'; else buf[bufLen - 1] = '\0';
}

static void cmdLedMapping() {
    // Track per-Pens assignment (indexed by k-'A')
    bool assignedRett[MAX_PENS], assignedAvvik[MAX_PENS];
    memset(assignedRett,  0, sizeof(assignedRett));
    memset(assignedAvvik, 0, sizeof(assignedAvvik));

    // Track which physical LED indices are already in use
    bool usedLed[NUM_LED_OUTPUTS];
    memset(usedLed, 0, sizeof(usedLed));

    // Pre-populate from EEPROM
    uint8_t numPens = (uint8_t)(LAST_PENS - FIRST_PENS + 1);
    for (uint8_t i = 0; i < numPens; i++) {
        uint8_t pensIdx  = i + (uint8_t)(FIRST_PENS - 'A');
        uint8_t rettLed  = EEPROM.read(REGION4_RETT  + i);
        uint8_t avvikLed = EEPROM.read(REGION4_AVVIK + i);
        if (rettLed  != 0xFF) { assignedRett[pensIdx]  = true; if (rettLed  < NUM_LED_OUTPUTS) usedLed[rettLed]  = true; }
        if (avvikLed != 0xFF) { assignedAvvik[pensIdx] = true; if (avvikLed < NUM_LED_OUTPUTS) usedLed[avvikLed] = true; }
    }

    while (true) {
        // Build prompt: remaining Pens (both LEDs not yet done) + remaining LED indices
        bool done[MAX_PENS];
        for (uint8_t i = 0; i < MAX_PENS; i++) done[i] = assignedRett[i] && assignedAvvik[i];
        char remPens[40], remLeds[48];
        buildRemaining(remPens, sizeof(remPens), done);
        buildRemainingLeds(remLeds, sizeof(remLeds), usedLed);
        statusf(F("LED map Pens:%s LEDs:%s (0=no LED Esc=done):"), remPens, remLeds);

        char k = waitKey();
        if (k == ESC) { status(F("LED mapping done.")); return; }
        if (k < FIRST_PENS || k > LAST_PENS) continue;
        uint8_t eepromIdx = k - FIRST_PENS;
        if (eepromIdx >= SIM_PENS_COUNT) continue;
        uint8_t pensIdx = k - 'A';

        // --- Rett LED ---
        {
            uint8_t stored = EEPROM.read(REGION4_RETT + eepromIdx);
            uint8_t cursor = (stored < NUM_LED_OUTPUTS) ? stored : 0;
            while (true) {
                statusf(F("Pens %c Rett: LED %d lit. N=next P=prev S=save 0=no LED:"), k, cursor);
                char nav = waitKey();
                if      (nav == 'N') { cursor = (uint8_t)((cursor + 1) % NUM_LED_OUTPUTS); }
                else if (nav == 'P') { cursor = (uint8_t)((cursor + NUM_LED_OUTPUTS - 1) % NUM_LED_OUTPUTS); }
                else if (nav == 'S') {
                    if (stored < NUM_LED_OUTPUTS && stored != cursor) usedLed[stored] = false;
                    EEPROM.update(REGION4_RETT + eepromIdx, cursor);
                    usedLed[cursor] = true;
                    assignedRett[pensIdx] = true;
                    statusf(F("Pens %c Rett LED = %d."), k, cursor);
                    break;
                }
                else if (nav == '0') {
                    if (stored < NUM_LED_OUTPUTS) usedLed[stored] = false;
                    EEPROM.update(REGION4_RETT + eepromIdx, LED_NO_PIN);
                    assignedRett[pensIdx] = true;
                    statusf(F("Pens %c Rett: no LED."), k);
                    break;
                }
                else if (nav == ESC) { status(F("LED mapping done.")); return; }
            }
        }

        // --- Avvik LED ---
        {
            uint8_t stored = EEPROM.read(REGION4_AVVIK + eepromIdx);
            uint8_t cursor = (stored < NUM_LED_OUTPUTS) ? stored : 0;
            while (true) {
                statusf(F("Pens %c Avvik: LED %d lit. N=next P=prev S=save 0=no LED:"), k, cursor);
                char nav = waitKey();
                if      (nav == 'N') { cursor = (uint8_t)((cursor + 1) % NUM_LED_OUTPUTS); }
                else if (nav == 'P') { cursor = (uint8_t)((cursor + NUM_LED_OUTPUTS - 1) % NUM_LED_OUTPUTS); }
                else if (nav == 'S') {
                    if (stored < NUM_LED_OUTPUTS && stored != cursor) usedLed[stored] = false;
                    EEPROM.update(REGION4_AVVIK + eepromIdx, cursor);
                    usedLed[cursor] = true;
                    assignedAvvik[pensIdx] = true;
                    statusf(F("Pens %c Avvik LED = %d."), k, cursor);
                    break;
                }
                else if (nav == '0') {
                    if (stored < NUM_LED_OUTPUTS) usedLed[stored] = false;
                    EEPROM.update(REGION4_AVVIK + eepromIdx, LED_NO_PIN);
                    assignedAvvik[pensIdx] = true;
                    statusf(F("Pens %c Avvik: no LED."), k);
                    break;
                }
                else if (nav == ESC) { status(F("LED mapping done.")); return; }
            }
        }
        // Outer loop reprints the prompt immediately with updated remaining lists
    }
}

// ---------------------------------------------------------------------------
// Command M -- Moment button
// ---------------------------------------------------------------------------
static void cmdMomentSwitch() {
    status(F("Press the physical moment button when ready (Esc=abort)..."));
    delay(3000);
    statusf(F("Detected button on simulated pin %d. Correct? (Y/N)"), SIM_MOMENT_PIN);

    while (true) {
        char k = waitKey();
        if (k == 'Y') {
            EEPROM.update(REGION5_MOMENT, SIM_MOMENT_PIN);
            statusf(F("Moment button stored (pin %d). Press Esc to return."), SIM_MOMENT_PIN);
            while (waitKey() != ESC) {}
            return;
        }
        if (k == 'N') {
            status(F("Retrying -- press the button again..."));
            delay(3000);
            statusf(F("Detected pin %d again. Correct? (Y/N)"), SIM_MOMENT_PIN);
        }
        if (k == ESC) { status(F("Moment button config aborted.")); return; }
    }
}

// ---------------------------------------------------------------------------
// Command D -- Dreieskive switch
// ---------------------------------------------------------------------------
static void cmdDreieskiveSwitch() {
    status(F("Move Dreieskive switch to MIDDLE, then press any key (Esc=abort)."));
    char k = waitKey();
    if (k == ESC) return;

    status(F("Move switch to CW (clockwise)..."));
    delay(2000);
    statusf(F("CW detected on simulated pin %d."), SIM_DREI_SW_CW);
    EEPROM.update(REGION5_SW_CW, SIM_DREI_SW_CW);

    status(F("Move back to MIDDLE, then to CCW (counter-clockwise)..."));
    delay(2000);
    statusf(F("CCW detected on simulated pin %d."), SIM_DREI_SW_CCW);
    EEPROM.update(REGION5_SW_CCW, SIM_DREI_SW_CCW);

    status(F("Dreieskive switch stored. Press Esc to return."));
    while (waitKey() != ESC) {}
}

// ---------------------------------------------------------------------------
// Command 4 -- Routing matrix upload / download
// ---------------------------------------------------------------------------

static void cmdRoutingMatrix() {
    status(F("Routing matrix: U=upload  D=download  Esc=done"));

    while (true) {
        char k = waitKey();
        if (k == ESC) { status(F("Routing matrix done.")); return; }

        if (k == 'U') {
            Serial.println(F("READY"));
            Serial.setTimeout(5000);

            uint8_t buf[LED_COUNT * STRIDE];
            bool ok = true;
            char line[40];

            for (uint8_t i = 0; i < LED_COUNT && ok; i++) {
                readLine(line, sizeof(line));
                if (strcmp(line, "ABORT") == 0) {
                    status(F("Upload aborted by host."));
                    ok = false;
                    break;
                }
                uint16_t base = (uint16_t)i * STRIDE;
                char* token = strtok(line, " ");
                if (!token) { Serial.println(F("ERR")); ok = false; break; }

                uint8_t count = (uint8_t)strtoul(token, nullptr, 16);
                buf[base] = count;
                uint8_t expected = (count == 0xFF || count == 0x00) ? 0 : count * 2;

                for (uint8_t t = 0; t < expected && ok; t++) {
                    token = strtok(nullptr, " ");
                    if (!token) { Serial.println(F("ERR")); ok = false; break; }
                    buf[base + 1 + t] = (uint8_t)strtoul(token, nullptr, 16);
                }
                if (ok) Serial.println(F("OK"));
            }

            if (ok) {
                readLine(line, sizeof(line));
                if (strcmp(line, "END") == 0) {
                    for (uint16_t i = 0; i < (uint16_t)LED_COUNT * STRIDE; i++)
                        EEPROM.update(REGION6_BASE + i, buf[i]);
                    Serial.println(F("STORED"));
                    status(F("Routing matrix written to EEPROM."));
                } else {
                    status(F("Expected END -- upload not committed."));
                }
            }
            Serial.setTimeout(1000);

        } else if (k == 'D') {
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
            status(F("Routing matrix sent."));
        }
    }
}

// ---------------------------------------------------------------------------
// Command R -- Reset EEPROM
// ---------------------------------------------------------------------------
static void cmdReset() {
    status(F("Erasing EEPROM..."));
    for (uint16_t addr = 0x10; addr < 0x200; addr++)
        EEPROM.update(addr, 0xFF);
    EEPROM.update(EEPROM_MAGIC_ADDR, 0xFF);
    status(F("Reset complete. All config erased."));
}

// ---------------------------------------------------------------------------
// Config mode  (Q exits back to main loop -- sent by C# config menu)
// ---------------------------------------------------------------------------
static void runConfig() {
    status(F("Config mode. Commands: 1 2 3 M D 4 R Q"));
    while (true) {
        char k = waitKey();
        switch (k) {
            case '1': cmdMotorScan();        break;
            case '2': cmdSwitchMapping();    break;
            case '3': cmdLedMapping();       break;
            case 'M': cmdMomentSwitch();     break;
            case 'D': cmdDreieskiveSwitch(); break;
            case '4': cmdRoutingMatrix();    break;
            case 'R': cmdReset();            break;
            case 'Q': status(F("Exiting config mode.")); return;
            default:  statusf(F("Unknown config command: %c"), k); break;
        }
    }
}

// ---------------------------------------------------------------------------
// Debug mode (stub)
// ---------------------------------------------------------------------------
static void runDebug() {
    status(F("Debug mode. A=toggle all LEDs  L=snake  Esc=exit"));
    while (true) {
        char k = waitKey();
        if (k == ESC) { status(F("Exiting debug mode.")); return; }
        if (k == 'A') status(F("[SIM] All LEDs toggled."));
        if (k == 'L') status(F("[SIM] Snake animation running."));
    }
}

// ---------------------------------------------------------------------------
// Verify mode (stub)
// ---------------------------------------------------------------------------
static void runVerify() {
    status(F("Verify mode. 1=motors and feedback  Esc=exit"));
    while (true) {
        char k = waitKey();
        if (k == ESC) { status(F("Exiting verify mode.")); return; }
        if (k == '1') {
            status(F("Verifying motors..."));
            for (uint8_t i = 0; i < SIM_PENS_COUNT; i++)
                statusf(F("Pens %c: PASS (simulated)."), (char)(FIRST_PENS + i));
            status(F("All Penser verified."));
        }
    }
}

// ---------------------------------------------------------------------------
// EEPROM status report -- called at startup
// ---------------------------------------------------------------------------
static void reportEepromStatus() {
    uint8_t numPens = (uint8_t)(LAST_PENS - FIRST_PENS + 1);
    char miss[18];
    uint8_t done, mp;

    // Cmd 1: motor scan
    done = 0; mp = 0;
    for (uint8_t i = 0; i < numPens; i++) {
        if (EEPROM.read(REGION1_BASE + i) != 0xFF) done++;
        else { if (mp) miss[mp++]=','; miss[mp++]=(char)(FIRST_PENS+i); }
    }
    miss[mp] = '\0';
    bool dreiM = EEPROM.read(REGION5_MOTOR_PIN) != 0xFF;
    if (mp) statusf(F("Cmd 1 Motor: %d/%d (miss:%s) Drei:%S"), done, numPens, miss, dreiM?PSTR("ok"):PSTR("--"));
    else    statusf(F("Cmd 1 Motor: %d/%d Dreieskive:%S"),      done, numPens,       dreiM?PSTR("ok"):PSTR("--"));

    // Cmd 2: switch mapping
    done = 0; mp = 0;
    for (uint8_t i = 0; i < numPens; i++) {
        if (EEPROM.read(REGION3_BASE + i) != 0xFF) done++;
        else { if (mp) miss[mp++]=','; miss[mp++]=(char)(FIRST_PENS+i); }
    }
    miss[mp] = '\0';
    if (mp) statusf(F("Cmd 2 Switch: %d/%d (miss:%s)"), done, numPens, miss);
    else    statusf(F("Cmd 2 Switch: %d/%d"),            done, numPens);

    // Cmd 3: LED mapping
    done = 0; mp = 0;
    for (uint8_t i = 0; i < numPens; i++) {
        bool ok = EEPROM.read(REGION4_RETT+i)!=0xFF && EEPROM.read(REGION4_AVVIK+i)!=0xFF;
        if (ok) done++;
        else { if (mp) miss[mp++]=','; miss[mp++]=(char)(FIRST_PENS+i); }
    }
    miss[mp] = '\0';
    if (mp) statusf(F("Cmd 3 LED: %d/%d (miss:%s)"), done, numPens, miss);
    else    statusf(F("Cmd 3 LED: %d/%d"),            done, numPens);

    bool moment = EEPROM.read(REGION5_MOMENT) != 0xFF;
    bool dreiSw = EEPROM.read(REGION5_SW_CW)!=0xFF && EEPROM.read(REGION5_SW_CCW)!=0xFF;
    statusf(F("Cmd M Moment:%S  Cmd D Dreieskive sw:%S"),
            moment?PSTR("ok"):PSTR("--"), dreiSw?PSTR("ok"):PSTR("--"));

    uint8_t routing = 0;
    for (uint8_t i = 0; i < LED_COUNT; i++)
        if (EEPROM.read(REGION6_BASE + (uint16_t)i * STRIDE) != 0xFF) routing++;
    statusf(F("Cmd 4 Routing: %d/%d LEDs custom"), routing, LED_COUNT);
}

// ---------------------------------------------------------------------------
// setup / loop
// ---------------------------------------------------------------------------
void setup() {
    wdt_disable();
    Serial.begin(115200);
    Serial.setTimeout(1000);
    memset(simPensPos, 0, sizeof(simPensPos));

    status(F("LysKontroll Simulator v1.0"));
    status(F("Commands: C=Config  D=Debug  V=Verify  Q=Quit"));
}

void loop() {
    if (!Serial.available()) return;
    char c = (char)toupper(Serial.read());
    switch (c) {
        case 'C': runConfig();          break;
        case 'D': runDebug();           break;
        case 'V': runVerify();          break;
        case 'S': reportEepromStatus(); break;  // silent: C# requests status after connect
        case 'Q': softReset();          break;  // C# program quit -- reset to clean state
        default:  statusf(F("Unknown command: %c. Use C/D/V/Q."), c); break;
    }
}
