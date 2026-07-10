using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

static class ConfigSession
{
    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Config Mode ===");

        var menu = new Menu(
            [
                ('1', "Motor scan — find Dreieskive + all Pens motors", () => MotorScan(arduino)),
                ('2', "Manual switch → Pens mapping",                   () => SwitchMapping(arduino)),
                ('3', "LED mapping",                                     () => LedMapping(arduino)),
                ('4', "LED routing matrix (upload/download JSON)",       () => RoutingMatrixSession.Run(arduino)),
                ('M', "Moment button",                                   () => MomentSwitch(arduino)),
                ('D', "Dreieskive switch",                               () => DreieskiveSwitch(arduino)),
                ('R', "Reset: erase all config from EEPROM",            () => ResetConfig(arduino)),
                ('S', "Show EEPROM status",                              () => EepromStatus.Print(arduino)),
            ],
            quitOption: ('Q', "Exit config mode")
        );
        menu.Run();

        Console.WriteLine("Exiting config mode.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command 1: Motor scan
    // -------------------------------------------------------------------------
    static void MotorScan(ArduinoDevice arduino)
    {
        const int FeedbackTimeoutMs = 3000;

        char first = arduino.FirstPens;
        char last = arduino.LastPens;
        int numPens = arduino.NumPens;
        int numPairs = arduino.NumPairs;
        int drPair = arduino.DreieskivePair;

        bool[] pensAssigned = new bool[numPens];
        bool[] pairDone = new bool[numPairs];
        bool dreieskiveDone = false;

        for (int i = 0; i < numPens; i++)
        {
            byte p = arduino.EepromRead(ArduinoDevice.Region1Base + i);
            if (p != ArduinoDevice.Unset && p < numPairs)
            {
                pensAssigned[i] = true;
                pairDone[p] = true;
            }
        }
        {
            byte drp = arduino.EepromRead(ArduinoDevice.Region5MotorPin);
            if (drp != ArduinoDevice.Unset && drp < numPairs)
            {
                dreieskiveDone = true;
                pairDone[drp] = true;
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- Command 1: Motor Scan ---");
        Console.WriteLine("Fires each motor pair. Watch the layout to see what moves.");
        Console.WriteLine($"Keys: {first}–{last} = identify Pens   0 = skip pair   Esc = abort");
        Console.WriteLine();

        bool userAborted = false;

        while (!userAborted)
        {
            bool allPensDone = AllTrue(pensAssigned);
            bool allDone = allPensDone && dreieskiveDone;

            if (allDone)
            {
                Console.WriteLine("All Pens and Dreieskive are configured.");
                Console.Write($"Type Pens letter ({first}–{last}) to re-configure, or Esc to finish: ");
                char edit = ReadChar(c =>
                    c == EscKey || (char.ToUpper(c) >= first && char.ToUpper(c) <= last));
                edit = char.ToUpper(edit);
                Console.WriteLine(edit == EscKey ? "[Esc]" : edit.ToString());

                if (edit == EscKey) break;

                int ei = edit - first;
                byte oldPair = arduino.EepromRead(ArduinoDevice.Region1Base + ei);
                if (oldPair != ArduinoDevice.Unset && oldPair < numPairs)
                    pairDone[oldPair] = false;
                pensAssigned[ei] = false;
                Console.WriteLine($"Pens {edit} cleared — will re-scan.");
                Console.WriteLine();
            }

            bool anyWork = false;

            for (int pair = 0; pair < numPairs && !userAborted; pair++)
            {
                if (pairDone[pair]) continue;
                if (pair != drPair && AllTrue(pensAssigned)) continue;

                anyWork = true;
                Console.WriteLine($"Firing motor pair {pair}...");
                arduino.MotorFire(pair, 0);
                int pinA = arduino.WaitFeedback(pair, FeedbackTimeoutMs);

                if (pair == drPair || pinA < 0)
                {
                    Console.WriteLine("No feedback — Dreieskive pair. Assign CW polarity:");
                    Console.Write("  1 = '01' bit pattern is CW   2 = '10' bit pattern is CW: ");
                    char polKey = ReadChar(c => c == '1' || c == '2');
                    Console.WriteLine(polKey);
                    byte drPol = polKey == '1' ? (byte)0 : (byte)1;
                    arduino.EepromWrite(ArduinoDevice.Region5MotorPin, (byte)pair);
                    arduino.EepromWrite(ArduinoDevice.Region5MotorPol, drPol);
                    dreieskiveDone = true;
                    pairDone[pair] = true;
                    Console.WriteLine($"Dreieskive stored: pair {pair}, CW = pol {drPol}.");
                    Console.WriteLine();
                    continue;
                }

                bool pairHandled = false;
                while (!pairHandled && !userAborted)
                {
                    string rem = BuildRemaining(first, last, pensAssigned);
                    Console.WriteLine($"Feedback on pin {pinA}. Unassigned Pens: {rem}");
                    Console.Write($"Which Pens moved? ({first}–{last} / 0=skip / Esc=abort): ");

                    char choice = ReadChar(c =>
                        c == EscKey || c == '0' ||
                        (char.ToUpper(c) >= first && char.ToUpper(c) <= last));
                    choice = char.ToUpper(choice);
                    Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());

                    if (choice == EscKey) { userAborted = true; break; }

                    if (choice == '0')
                    {
                        Console.WriteLine("Skipped.");
                        pairDone[pair] = true;
                        pairHandled = true;
                        Console.WriteLine();
                        continue;
                    }

                    int pi = choice - first;

                    if (pensAssigned[pi])
                    {
                        byte existingPair = arduino.EepromRead(ArduinoDevice.Region1Base + pi);
                        Console.WriteLine($"Pens {choice} already assigned (pair {existingPair}).");
                        Console.Write("Override?  1 = yes   0 = re-enter: ");
                        char ov = ReadChar(c => c == '1' || c == '0');
                        Console.WriteLine(ov);
                        if (ov == '0') continue;
                        if (existingPair != ArduinoDevice.Unset && existingPair < numPairs)
                            pairDone[existingPair] = false;
                        pensAssigned[pi] = false;
                    }

                    Console.Write($"Pens {choice} is now at:  R = Rett   A = Avvik: ");
                    char pos = char.ToUpper(ReadChar(c =>
                        char.ToUpper(c) == 'R' || char.ToUpper(c) == 'A'));
                    Console.WriteLine(pos);

                    byte pol0result = pos == 'R' ? (byte)0 : (byte)1;

                    string otherPos = pos == 'R' ? "Avvik" : "Rett";
                    Console.WriteLine($"Moving to {otherPos} to capture second feedback pin...");
                    arduino.MotorFire(pair, 1);
                    int pinB = arduino.WaitFeedback(pair, FeedbackTimeoutMs);

                    byte rettPin, avvikPin;
                    if (pos == 'R')
                    {
                        rettPin = (byte)pinA;
                        avvikPin = pinB >= 0 ? (byte)pinB : ArduinoDevice.Unset;
                    }
                    else
                    {
                        avvikPin = (byte)pinA;
                        rettPin = pinB >= 0 ? (byte)pinB : ArduinoDevice.Unset;
                    }

                    arduino.EepromWrite(ArduinoDevice.Region1Base + pi, (byte)pair);
                    arduino.EepromWrite(ArduinoDevice.Region1Pol + pi, pol0result);
                    arduino.EepromWrite(ArduinoDevice.Region2Rett + pi, rettPin);
                    arduino.EepromWrite(ArduinoDevice.Region2Avvik + pi, avvikPin);

                    pensAssigned[pi] = true;
                    pairDone[pair] = true;
                    pairHandled = true;

                    Console.WriteLine(
                        $"Pens {choice} stored: pair={pair} pol={pol0result} " +
                        $"Rett={rettPin} Avvik={avvikPin}");
                    Console.WriteLine();
                }
            }

            if (!anyWork) break;
        }

        arduino.MotorStop();

        if (userAborted)
        {
            Console.WriteLine("Motor scan aborted.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("Motor scan complete.");
        Console.Write("Press Esc to return: ");
        while (ReadKey() != EscKey) { }
        Console.WriteLine("[Esc]");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command 2: Manual switch → Pens mapping
    // -------------------------------------------------------------------------
    static void SwitchMapping(ArduinoDevice arduino)
    {
        const int ScanTimeoutMs = 5000;

        char first = arduino.FirstPens;
        char last = arduino.LastPens;
        int numPens = arduino.NumPens;

        bool[] assigned = new bool[numPens];
        for (int i = 0; i < numPens; i++)
            assigned[i] = arduino.EepromRead(ArduinoDevice.Region3Base + i) != ArduinoDevice.Unset;

        Console.WriteLine();
        Console.WriteLine("--- Command 2: Manual Switch Mapping ---");
        Console.WriteLine($"Type a Pens letter ({first}–{last}) to map its panel switch.");
        Console.WriteLine("Type the same letter again to re-map. Esc when finished.");
        Console.WriteLine();

        while (true)
        {
            string remaining = BuildRemaining(first, last, assigned);
            Console.Write($"Unmapped: {remaining}   Type Pens letter or Esc: ");

            char choice = ReadChar(c =>
                c == EscKey || (char.ToUpper(c) >= first && char.ToUpper(c) <= last));
            choice = char.ToUpper(choice);
            Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());

            if (choice == EscKey)
            {
                Console.WriteLine("Switch mapping done.");
                Console.WriteLine();
                return;
            }

            int pi = choice - first;

            byte pair = arduino.EepromRead(ArduinoDevice.Region1Base + pi);
            if (pair == ArduinoDevice.Unset)
            {
                Console.WriteLine($"Pens {choice}: motor not yet configured — run Command 1 first.");
                Console.WriteLine();
                continue;
            }
            byte rettPol = arduino.EepromRead(ArduinoDevice.Region1Pol + pi);

            Console.WriteLine($"Pens {choice}: driving to Rett (pair {pair}, pol {rettPol})...");
            arduino.MotorFire(pair, rettPol);
            int fb = arduino.WaitFeedback(pair, 3000);
            if (fb < 0) Console.WriteLine("Warning: no feedback after driving to Rett.");

            Console.WriteLine($"Flip the panel switch for Pens {choice} to Avvik then back to Rett.");
            Console.WriteLine($"Detecting switch change ({ScanTimeoutMs / 1000} s timeout)...");

            int swPin = arduino.ScanSwitches(ScanTimeoutMs);
            if (swPin < 0)
            {
                Console.WriteLine("No switch change detected. Try again.");
                Console.WriteLine();
                continue;
            }

            arduino.EepromWrite(ArduinoDevice.Region3Base + pi, (byte)swPin);
            assigned[pi] = true;
            Console.WriteLine($"Pens {choice}: switch pin {swPin} stored.");
            Console.WriteLine();
        }
    }

    // -------------------------------------------------------------------------
    // Command 3: LED mapping
    // -------------------------------------------------------------------------
    static void LedMapping(ArduinoDevice arduino)
    {
        char first = arduino.FirstPens;
        char last = arduino.LastPens;
        int numPens = arduino.NumPens;
        int numLeds = arduino.NumLedOutputs;

        byte[] ledRett = new byte[numPens];
        byte[] ledAvvik = new byte[numPens];
        for (int i = 0; i < numPens; i++)
        {
            ledRett[i] = arduino.EepromRead(ArduinoDevice.Region4Rett + i);
            ledAvvik[i] = arduino.EepromRead(ArduinoDevice.Region4Avvik + i);
        }

        Console.WriteLine();
        Console.WriteLine("--- Command 3: LED Mapping ---");
        Console.WriteLine($"Keys: N = next LED   P = prev LED   S = save   0 = no LED   Esc = back");
        Console.WriteLine();

        while (true)
        {
            bool[] pensFullyAssigned = new bool[numPens];
            bool[] usedLed = new bool[numLeds];
            for (int i = 0; i < numPens; i++)
            {
                pensFullyAssigned[i] =
                    ledRett[i] != ArduinoDevice.Unset &&
                    ledAvvik[i] != ArduinoDevice.Unset;
                if (ledRett[i] < numLeds) usedLed[ledRett[i]] = true;
                if (ledAvvik[i] < numLeds) usedLed[ledAvvik[i]] = true;
            }

            string remainPens = BuildRemaining(first, last, pensFullyAssigned);
            string remainLeds = BuildRemainingLeds(numLeds, usedLed);
            Console.WriteLine($"Unassigned Pens: {remainPens}");
            Console.WriteLine($"Unused LED indices: {remainLeds}");
            Console.Write($"Select Pens ({first}–{last}) or Esc: ");

            char choice = ReadChar(c =>
                c == EscKey || (char.ToUpper(c) >= first && char.ToUpper(c) <= last));
            choice = char.ToUpper(choice);
            Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());

            if (choice == EscKey)
            {
                arduino.AllLedsOff();
                Console.WriteLine("LED mapping done.");
                Console.WriteLine();
                return;
            }

            int pi = choice - first;

            bool backToPensSelect = false;
            for (int posPass = 0; posPass < 2 && !backToPensSelect; posPass++)
            {
                bool isRett = posPass == 0;
                string posName = isRett ? "Rett" : "Avvik";
                byte existing = isRett ? ledRett[pi] : ledAvvik[pi];

                int ledIdx = existing < numLeds ? existing : 0;

                arduino.AllLedsOff();
                arduino.SetLed(ledIdx, true);
                Console.WriteLine($"Pens {choice} {posName}: LED {ledIdx} is lit.");
                Console.WriteLine("N=next  P=prev  S=save  0=no LED  Esc=back to Pens select");

                while (true)
                {
                    char k = char.ToUpper(ReadKey());
                    switch (k)
                    {
                        case 'N':
                            arduino.SetLed(ledIdx, false);
                            ledIdx = (ledIdx + 1) % numLeds;
                            arduino.SetLed(ledIdx, true);
                            Console.WriteLine($"→ LED {ledIdx}");
                            break;

                        case 'P':
                            arduino.SetLed(ledIdx, false);
                            ledIdx = (ledIdx + numLeds - 1) % numLeds;
                            arduino.SetLed(ledIdx, true);
                            Console.WriteLine($"→ LED {ledIdx}");
                            break;

                        case 'S':
                            {
                                arduino.SetLed(ledIdx, false);
                                int region = isRett ? ArduinoDevice.Region4Rett : ArduinoDevice.Region4Avvik;
                                arduino.EepromWrite(region + pi, (byte)ledIdx);
                                if (isRett) ledRett[pi] = (byte)ledIdx;
                                else ledAvvik[pi] = (byte)ledIdx;
                                Console.WriteLine($"→ Pens {choice} {posName} LED saved as {ledIdx}.");
                                goto NextPos;
                            }

                        case '0':
                            {
                                arduino.SetLed(ledIdx, false);
                                int region = isRett ? ArduinoDevice.Region4Rett : ArduinoDevice.Region4Avvik;
                                arduino.EepromWrite(region + pi, ArduinoDevice.LedNoPin);
                                if (isRett) ledRett[pi] = ArduinoDevice.LedNoPin;
                                else ledAvvik[pi] = ArduinoDevice.LedNoPin;
                                Console.WriteLine($"→ Pens {choice} {posName}: no physical LED.");
                                goto NextPos;
                            }

                        case EscKey:
                            arduino.SetLed(ledIdx, false);
                            backToPensSelect = true;
                            goto NextPos;
                    }
                    continue;
                NextPos: break;
                }
            }

            arduino.AllLedsOff();
            Console.WriteLine();
        }
    }

    // -------------------------------------------------------------------------
    // Command M: Moment (signal) button
    // -------------------------------------------------------------------------
    static void MomentSwitch(ArduinoDevice arduino)
    {
        const int ScanTimeoutMs = 5000;

        Console.WriteLine();
        Console.WriteLine("--- Command M: Moment Button Detection ---");
        Console.WriteLine("Press the physical moment signal button when prompted.");
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine($"Press the moment button now ({ScanTimeoutMs / 1000} s timeout)...");
            int pin = arduino.ScanSwitches(ScanTimeoutMs);

            if (pin < 0)
            {
                Console.WriteLine("No button press detected.");
                Console.Write("1 = retry   Esc = done: ");
                char c = ReadChar(ch => ch == '1' || ch == EscKey);
                Console.WriteLine(c == EscKey ? "[Esc]" : "1");
                if (c == EscKey) { Console.WriteLine(); return; }
                continue;
            }

            Console.WriteLine($"Detected pin {pin}.");
            Console.Write("1 = save   0 = retry: ");
            char ans = ReadChar(c => c == '1' || c == '0');
            Console.WriteLine(ans);

            if (ans == '0') continue;

            arduino.EepromWrite(ArduinoDevice.Region5Moment, (byte)pin);
            Console.WriteLine($"Moment button pin {pin} stored.");
            Console.WriteLine();
            return;
        }
    }

    // -------------------------------------------------------------------------
    // Command D: Dreieskive (turntable) switch
    // -------------------------------------------------------------------------
    static void DreieskiveSwitch(ArduinoDevice arduino)
    {
        const int ScanTimeoutMs = 5000;

        Console.WriteLine();
        Console.WriteLine("--- Command D: Dreieskive Switch Configuration ---");
        Console.WriteLine("The panel switch has three positions: middle, CW, CCW.");
        Console.WriteLine();

        Console.Write("Step 1: Move the switch to MIDDLE, then press Enter: ");
        Console.ReadLine();

        Console.WriteLine($"Step 2: Move the switch to CW (clockwise) ({ScanTimeoutMs / 1000} s timeout)...");
        int cwPin = arduino.ScanSwitches(ScanTimeoutMs);
        if (cwPin < 0)
        {
            Console.WriteLine("No change detected. Dreieskive switch configuration aborted.");
            Console.WriteLine();
            return;
        }
        Console.WriteLine($"CW detected: pin {cwPin}.");

        Console.Write("Step 3: Move the switch back to MIDDLE, then press Enter: ");
        Console.ReadLine();

        Console.WriteLine($"Step 4: Move the switch to CCW (counter-clockwise) ({ScanTimeoutMs / 1000} s timeout)...");
        int ccwPin = arduino.ScanSwitches(ScanTimeoutMs);
        if (ccwPin < 0)
        {
            Console.WriteLine("No change detected. Dreieskive switch configuration aborted.");
            Console.WriteLine();
            return;
        }
        Console.WriteLine($"CCW detected: pin {ccwPin}.");

        arduino.EepromWrite(ArduinoDevice.Region5SwCw, (byte)cwPin);
        arduino.EepromWrite(ArduinoDevice.Region5SwCcw, (byte)ccwPin);
        Console.WriteLine($"Dreieskive switch stored: CW={cwPin} CCW={ccwPin}.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command R: Reset (clear) all stored config from EEPROM
    // -------------------------------------------------------------------------
    static void ResetConfig(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command R: Reset Configuration ---");
        Console.WriteLine("WARNING: This will erase ALL stored configuration from EEPROM.");
        Console.WriteLine("Motor mappings, switch mappings, LED mappings and polarity will all be lost.");
        Console.WriteLine();
        Console.Write("Type Y to confirm reset, any other key to cancel: ");
        char confirm = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
        Console.WriteLine(confirm);

        if (confirm != 'Y')
        {
            Console.WriteLine("Reset cancelled.");
            Console.WriteLine();
            return;
        }

        Console.Write("Erasing EEPROM...");
        Console.WriteLine(arduino.EepromErase()
            ? " Done. Run Command 1, 2, 3, M and D to reconfigure."
            : " Failed — no response from device.");
        Console.WriteLine();
    }
}
