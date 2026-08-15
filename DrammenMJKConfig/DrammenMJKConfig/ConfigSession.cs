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
                ('H', "Hardware config (upload/download hardware.json)",   () => HardwareConfigSession.Run(arduino)),
                ('1', "Motor scan — find switch motors + feedback",         () => MotorScan(arduino)),
                ('J', "System config (upload/download SystemConfig.json)", () => SystemConfigSession.Run(arduino)),
                ('R', "Reset: erase all config from EEPROM",               () => ResetConfig(arduino)),
            ],
            quitOption: ('Q', "Exit config mode")
        );
        menu.Run();

        Console.WriteLine("Exiting config mode.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command 1: Motor scan (Phase 1 scope — see PLAN_Phase1.md)
    //
    // Assumes hardware.json has already been uploaded (Port B output, Port A
    // input+pullup on the board being scanned) — Motor Scan doesn't touch
    // MDIR/MPU itself. Dreieskive is declared (fixed pins), never scanned.
    // -------------------------------------------------------------------------
    static void MotorScan(ArduinoDevice arduino)
    {
        const int FeedbackTimeoutMs = 3000;

        Console.WriteLine();
        Console.WriteLine("--- Command 1: Motor Scan ---");
        Console.WriteLine("Fires each motor pin in turn. Watch the layout to see what moves.");
        Console.WriteLine("Esc = abort at any point.");
        Console.WriteLine();

        var svb = BoardConfig.Svb;
        var scb = svb.Scbs[0]; // Phase 1: exactly one SCB (FCSBR)
        byte vaddr = scb.VirtualAddress;
        var file = SystemConfigSession.LoadOrNew();

        // Declare dreieskive + status LED facts up front — not scanned, but
        // known now, so SystemConfig.json/EEPROM fully describe the board
        // from the first scan onward, not just its discovered switches.
        arduino.EepromWrite(ArduinoDevice.AddrDreieskiveVAddr, vaddr);
        arduino.EepromWrite(ArduinoDevice.AddrDreieskiveMotorPinBase, (byte)scb.DreieskivePins.Base);
        arduino.EepromWrite(ArduinoDevice.AddrStatusLedVAddr, vaddr);
        arduino.EepromWrite(ArduinoDevice.AddrStatusLedBit, (byte)scb.StatusLedPin.Bit);
        SystemConfigSession.SaveDreieskiveAndLed(
            file,
            new DreieskiveEntry { MotorVAddr = $"0x{vaddr:X2}", MotorPinBase = scb.DreieskivePins.Base, CwPolarity = null },
            new StatusLedEntry { VAddr = $"0x{vaddr:X2}", Bit = scb.StatusLedPin.Bit });
        Console.WriteLine($"Declared dreieskive (pins {scb.DreieskivePins.Base}/{scb.DreieskivePins.Upper}) and status LED (bit {scb.StatusLedPin.Bit}).");
        Console.WriteLine();

        // Pre-populate from EEPROM so a re-run only scans what's still unassigned.
        var assignedLabels = new HashSet<string>();
        var firedBits = new HashSet<int>();
        foreach (var (_, motorBit, label, slot) in BoardConfig.AllSwitchSlots())
        {
            if (arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot) == ArduinoDevice.Unset) continue;
            assignedLabels.Add(label);
            firedBits.Add(motorBit);
        }

        bool userAborted = false;

        for (int i = 0; i < scb.Switches.Count && !userAborted; i++)
        {
            int motorBit = scb.MotorBitFirst + i;
            if (firedBits.Contains(motorBit)) continue;

            bool pinHandled = false;
            while (!pinHandled && !userAborted)
            {
                Console.WriteLine($"Firing motor bit {motorBit}...");
                int baseline = arduino.McpReadPort(vaddr, 'A');
                if (baseline < 0)
                {
                    Console.WriteLine("  I2C error reading baseline. Aborting scan.");
                    userAborted = true;
                    break;
                }

                if (!arduino.McpSetBit(vaddr, 'B', motorBit, false))
                {
                    Console.WriteLine("  I2C error firing motor. Aborting scan.");
                    userAborted = true;
                    break;
                }

                int changed = arduino.McpPollChange(vaddr, 'A', (byte)baseline, FeedbackTimeoutMs);
                if (changed < 0)
                {
                    Console.WriteLine("  No feedback change detected — wiring problem?");
                    Console.Write("  1 = retry   0 = skip this pin   Esc = abort: ");
                    char c = ReadChar(ch => ch == '1' || ch == '0' || ch == EscKey);
                    Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
                    if (c == EscKey) { userAborted = true; break; }
                    if (c == '0') pinHandled = true;
                    continue;
                }

                int diff = changed ^ baseline;
                int pairLow = FindPairLow(diff);
                if (pairLow < 0)
                {
                    Console.WriteLine($"  Anomaly: unexpected bits changed (diff=0x{diff:X2}).");
                    Console.Write("  1 = retry   0 = skip this pin   Esc = abort: ");
                    char c = ReadChar(ch => ch == '1' || ch == '0' || ch == EscKey);
                    Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
                    if (c == EscKey) { userAborted = true; break; }
                    if (c == '0') pinHandled = true;
                    continue;
                }

                int highBit = ((changed >> (pairLow + 1)) & 1) != 0 ? pairLow + 1 : pairLow;

                var remaining = scb.Switches.Select(s => s.Label).Where(l => !assignedLabels.Contains(l)).ToList();
                Console.WriteLine($"  Feedback changed on pair ({pairLow},{pairLow + 1}).");
                Console.WriteLine("  Which switch moved?");
                for (int r = 0; r < remaining.Count; r++)
                    Console.WriteLine($"    {r + 1}) {remaining[r]}");
                Console.Write("  Choice (0=skip, Esc=abort): ");

                char choice = ReadChar(ch => ch == '0' || ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < remaining.Count));
                Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());

                if (choice == EscKey) { userAborted = true; break; }
                if (choice == '0')
                {
                    arduino.McpSetBit(vaddr, 'B', motorBit, false); // leave stalled — safe default
                    pinHandled = true;
                    continue;
                }

                string chosenLabel = remaining[choice - '1'];

                Console.Write($"  Is {chosenLabel} now at:  R = Rett   A = Avvik: ");
                char pos = char.ToUpper(ReadChar(ch => char.ToUpper(ch) == 'R' || char.ToUpper(ch) == 'A'));
                Console.WriteLine(pos);

                byte polarity = pos == 'R' ? (byte)0 : (byte)1;
                int otherBit = highBit == pairLow ? pairLow + 1 : pairLow;
                byte rettBit  = pos == 'R' ? (byte)highBit : (byte)otherBit;
                byte avvikBit = pos == 'R' ? (byte)otherBit : (byte)highBit;

                // Verification move — not strictly required (the pair structure
                // already implies the second bit), but catches wiring/mechanical
                // faults at config time by confirming the opposite position too.
                Console.WriteLine("  Verifying opposite position...");
                arduino.McpSetBit(vaddr, 'B', motorBit, true);
                int confirmChanged = arduino.McpPollChange(vaddr, 'A', (byte)changed, FeedbackTimeoutMs);
                bool confirmed = confirmChanged >= 0 && ((confirmChanged >> otherBit) & 1) != 0;
                if (!confirmed)
                    Console.WriteLine("  Warning: opposite position not confirmed as expected — check wiring.");

                if (!BoardConfig.TryFindSlotByLabel(chosenLabel, out int slot))
                {
                    Console.WriteLine("  Internal error: label has no slot. Skipping store.");
                    pinHandled = true;
                    continue;
                }

                arduino.EepromWrite(ArduinoDevice.RegionSlotMotorVAddr    + slot, vaddr);
                arduino.EepromWrite(ArduinoDevice.RegionSlotMotorBit      + slot, (byte)motorBit);
                arduino.EepromWrite(ArduinoDevice.RegionSlotPolarity      + slot, polarity);
                arduino.EepromWrite(ArduinoDevice.RegionSlotFeedbackVAddr + slot, vaddr);
                arduino.EepromWrite(ArduinoDevice.RegionSlotFeedbackRett  + slot, rettBit);
                arduino.EepromWrite(ArduinoDevice.RegionSlotFeedbackAvvik + slot, avvikBit);

                SystemConfigSession.SaveSwitch(file, chosenLabel, new SwitchEntry
                {
                    MotorVAddr = $"0x{vaddr:X2}",
                    MotorBit = motorBit,
                    Polarity = polarity,
                    FeedbackVAddr = $"0x{vaddr:X2}",
                    FeedbackRettBit = rettBit,
                    FeedbackAvvikBit = avvikBit,
                });

                assignedLabels.Add(chosenLabel);
                firedBits.Add(motorBit);
                pinHandled = true;

                Console.WriteLine($"  Stored: {chosenLabel} -> motorBit={motorBit} pol={polarity} Rett={rettBit} Avvik={avvikBit}");
                Console.WriteLine();
            }
        }

        Console.WriteLine(userAborted ? "Motor scan aborted." : "Motor scan complete.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command R: Reset — erase all board hardware / switch / dreieskive /
    // status LED / fade config from EEPROM. Carried over from the pre-Phase-1
    // ConfigSession; EC wipes 0x02..EEPROM_ERASE_END on the firmware side.
    // -------------------------------------------------------------------------
    static void ResetConfig(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command R: Reset Configuration ---");
        Console.WriteLine("WARNING: This will erase ALL stored configuration from EEPROM.");
        Console.WriteLine("Board hardware table, switch table, dreieskive, status LED and fade config will all be lost.");
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
            ? " Done. Run Hardware config, Motor scan and System config to reconfigure."
            : " Failed — no response from Arduino.");
        Console.WriteLine();
    }

    // Returns the lower bit of whichever adjacent pair (0-1/2-3/4-5/6-7) exactly
    // matches `diff`, or -1 if diff isn't exactly one of those four pairs.
    static int FindPairLow(int diff)
    {
        foreach (int low in new[] { 0, 2, 4, 6 })
        {
            int mask = 0b11 << low;
            if ((diff & mask) == mask && (diff & ~mask & 0xFF) == 0) return low;
        }
        return -1;
    }
}
