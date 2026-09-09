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
                ('2', "Signal scan — find signal lamp bits (Red/Green1/Green2)", () => SignalScan(arduino)),
                ('3', "Track detection — set which bit + level means train present", () => TrackDetectionScan(arduino)),
                ('J', "System Config Backup (backup/restore)",              () => SystemConfigSession.Run(arduino)),
                ('E', "Edit switch config — fix labeling/polarity mistakes", () => SwitchEditSession.Run(arduino)),
                ('B', "Bench test — probe/read/write raw MCP23017 pins",   () => BenchTestSession.Run(arduino)),
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
        // Real switch motors take several seconds to travel end-to-end (~5s
        // observed) -- needs real margin above that, not just above the I2C
        // round-trip time.
        const int FeedbackTimeoutMs = 10000;

        Console.WriteLine();
        Console.WriteLine("--- Command 1: Motor Scan ---");
        Console.WriteLine("Fires each motor pin in turn. Watch the layout to see what moves.");
        Console.WriteLine("Esc = abort at any point.");
        Console.WriteLine();

        var svb = BoardConfig.Svb;
        var scbs = svb.Scbs;
        Console.WriteLine("Boards: " + string.Join("  ", scbs.Select((b, i) => $"{i + 1}={b.Name}")));
        Console.Write("Select board (or Esc): ");
        char boardChoice = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < scbs.Count));
        Console.WriteLine(boardChoice == EscKey ? "[Esc]" : boardChoice.ToString());
        if (boardChoice == EscKey) { Console.WriteLine(); return; }
        var scb = scbs[boardChoice - '1'];
        byte vaddr = scb.VirtualAddress;
        var file = SystemConfigSession.LoadOrNew();

        // Declare dreieskive (only for the one board that owns it) + status
        // LED facts up front — not scanned, but known now, so
        // SystemConfig.json/EEPROM fully describe the board from the first
        // scan onward, not just its discovered switches.
        if (scb.DreieskivePins is { } drei)
        {
            arduino.EepromWrite(ArduinoDevice.AddrDreieskiveVAddr, vaddr);
            arduino.EepromWrite(ArduinoDevice.AddrDreieskiveMotorPinBase, (byte)drei.Base);
            SystemConfigSession.SaveDreieskive(file,
                new DreieskiveEntry { MotorVAddr = $"0x{vaddr:X2}", MotorPinBase = drei.Base, CwPolarity = null });
            Console.WriteLine($"Declared dreieskive (pins {drei.Base}/{drei.Upper}).");
        }

        if (scb.StatusLedPin is { } led)
        {
            if (!arduino.SetStatusLed(vaddr, led.Bit))
            {
                Console.WriteLine($"Failed to declare status LED — is 0x{vaddr:X2} in the board hardware table yet? Upload hardware.json first.");
                Console.WriteLine();
                return;
            }
            SystemConfigSession.SaveStatusLed(file, scb.Name,
                new StatusLedEntry { VAddr = $"0x{vaddr:X2}", Bit = led.Bit });
            Console.WriteLine($"Declared status LED (bit {led.Bit}).");
        }

        if (scb.InverterEnablePin is { } inv)
        {
            if (!arduino.SetInverterEnable(vaddr, inv.Port, inv.Bit))
            {
                Console.WriteLine($"Failed to declare inverter enable — is 0x{vaddr:X2} in the board hardware table yet? Upload hardware.json first.");
                Console.WriteLine();
                return;
            }
            SystemConfigSession.SaveInverterEnable(file, scb.Name,
                new InverterEnableEntry { VAddr = $"0x{vaddr:X2}", Port = inv.Port.ToString(), Bit = inv.Bit });
            Console.WriteLine($"Declared inverter enable (port {inv.Port} bit {inv.Bit}).");
        }
        Console.WriteLine();

        // Check what's already in EEPROM for THIS board's switches before
        // deciding scope -- scoped to scb.Switches, not every SCB system-wide.
        // Silently skipping already-configured switches with no explanation
        // looked exactly like the scan doing nothing; ask instead.
        var scbLabels = scb.Switches.Select(s => s.Label).ToHashSet();
        var alreadyConfigured = new List<(string Label, int MotorBit)>();
        foreach (var (_, motorBit, label, slot) in BoardConfig.AllSwitchSlots())
        {
            if (!scbLabels.Contains(label)) continue;
            if (arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot) != ArduinoDevice.Unset)
                alreadyConfigured.Add((label, motorBit));
        }

        var assignedLabels = new HashSet<string>();
        var firedBits = new HashSet<int>();

        if (alreadyConfigured.Count > 0)
        {
            Console.WriteLine($"{alreadyConfigured.Count} switch(es) on {scb.Name} already have EEPROM config:");
            foreach (var (label, motorBit) in alreadyConfigured)
                Console.WriteLine($"  {label} (motor bit {motorBit})");
            Console.WriteLine();
            Console.Write("K = keep existing, only scan what's new   O = overwrite all, rescan everything   Esc = abort: ");
            char choice = ReadChar(ch => char.ToUpper(ch) == 'K' || char.ToUpper(ch) == 'O' || ch == EscKey);
            Console.WriteLine(choice == EscKey ? "[Esc]" : char.ToUpper(choice).ToString());
            Console.WriteLine();

            if (choice == EscKey)
            {
                Console.WriteLine("Motor scan aborted.");
                Console.WriteLine();
                return;
            }
            if (char.ToUpper(choice) == 'K')
            {
                foreach (var (label, motorBit) in alreadyConfigured)
                {
                    assignedLabels.Add(label);
                    firedBits.Add(motorBit);
                }
            }
            // 'O': leave both sets empty -- rescan everything, overwriting stored slots.
        }

        bool userAborted = false;

        for (int i = 0; i < scb.Switches.Count && !userAborted; i++)
        {
            int motorBit = scb.MotorBitFor(i);
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

                // Fire by toggling away from whatever the bit is currently at --
                // NOT a hardcoded false. apply_board_hw() already leaves every
                // motor bit at false (OLATB=0x00 safe default) right after boot,
                // so firing with a hardcoded false was a no-op on a fresh board:
                // nothing ever transitioned, so nothing moved. Reading back first
                // guarantees an actual transition regardless of starting state.
                int restOlatB = arduino.McpReadPort(vaddr, 'B');
                if (restOlatB < 0)
                {
                    Console.WriteLine("  I2C error reading motor rest state. Aborting scan.");
                    userAborted = true;
                    break;
                }
                bool restState = ((restOlatB >> motorBit) & 1) != 0;
                bool fireState = !restState;

                if (!arduino.McpSetBit(vaddr, 'B', motorBit, fireState))
                {
                    Console.WriteLine("  I2C error firing motor. Aborting scan.");
                    userAborted = true;
                    break;
                }

                int changed = arduino.McpPollChange(vaddr, 'A', (byte)baseline, FeedbackTimeoutMs);
                if (changed < 0)
                {
                    Console.WriteLine("  No feedback detected (timed out). Nothing may be installed at this position yet.");
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

                // polarity must be the literal OLATB bit value that drives to
                // Rett -- Firmware.ino's cmdSW reads it that way directly
                // (targetLevel = pos=='R' ? polarity : 1-polarity). fireState
                // is the bit value the switch is actually AT right now, which
                // `pos` was just confirmed against; if pos is Rett, fireState
                // IS the Rett value, otherwise Rett is the opposite (restState).
                // A hardcoded 0/1 here (ignoring fireState) only happened to
                // work while firing always drove to a fixed value -- now that
                // firing toggles from the real rest state, it doesn't.
                bool rettBitValue = pos == 'R' ? fireState : !fireState;
                byte polarity = (byte)(rettBitValue ? 1 : 0);
                int otherBit = highBit == pairLow ? pairLow + 1 : pairLow;
                byte rettBit  = pos == 'R' ? (byte)highBit : (byte)otherBit;
                byte avvikBit = pos == 'R' ? (byte)otherBit : (byte)highBit;

                // Verification move — not strictly required (the pair structure
                // already implies the second bit), but catches wiring/mechanical
                // faults at config time by confirming the opposite position too.
                // Toggles back to the original rest state (not a hardcoded true),
                // for the same reason firing above toggles instead of hardcoding.
                Console.WriteLine("  Verifying opposite position...");
                arduino.McpSetBit(vaddr, 'B', motorBit, restState);
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
    // Command 2: Signal scan — finds which Port B bits drive a signal's 3
    // lamps (Red/Green1/Green2). Lights one at a time and asks; once 2 of the
    // 3 are answered, the last one is inferred, no need to ask or light it.
    // No boards.json field for "this board has a signal" yet (deliberately
    // deferred, see PLAN_Phase3.md), so the candidate bits are asked for
    // directly rather than declared.
    // -------------------------------------------------------------------------
    static void SignalScan(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 2: Signal Scan ---");
        Console.WriteLine("Lights each signal lamp in turn. Watch the signal to see which lights.");
        Console.WriteLine("Esc = abort at any point.");
        Console.WriteLine();

        var scbs = BoardConfig.Svb.Scbs;
        Console.WriteLine("Boards: " + string.Join("  ", scbs.Select((b, i) => $"{i + 1}={b.Name}")));
        Console.Write("Select board (or Esc): ");
        char boardChoice = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < scbs.Count));
        Console.WriteLine(boardChoice == EscKey ? "[Esc]" : boardChoice.ToString());
        if (boardChoice == EscKey) { Console.WriteLine(); return; }
        var scb = scbs[boardChoice - '1'];
        byte vaddr = scb.VirtualAddress;

        int[] bits;
        if (scb.SignalBits is { } declared)
        {
            // Fixed hardware fact, declared in boards.json -- same reasoning
            // as motor bits, no need to ask each time.
            bits = declared.ToArray();
            Console.WriteLine($"Using declared signal bits {string.Join(", ", bits)} (boards.json).");
        }
        else
        {
            Console.Write("Which 3 Port B bits does this signal use (e.g. 4 5 6, blank to abort): ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) { Console.WriteLine(); return; }
            var bitTokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bitTokens.Length != 3 || !bitTokens.All(t => int.TryParse(t, out int b) && b is >= 0 and <= 7))
            {
                Console.WriteLine("  Need exactly 3 bit numbers 0-7.");
                Console.WriteLine();
                return;
            }
            bits = bitTokens.Select(int.Parse).ToArray();
        }

        var remaining = new List<string> { "Green", "Red", "Green2" };
        var assignment = new Dictionary<int, string>(); // bit -> color
        bool userAborted = false;

        foreach (int bit in bits)
        {
            if (remaining.Count == 1)
            {
                // Only one color left -- it has to be this bit. No need to
                // light it or ask.
                assignment[bit] = remaining[0];
                Console.WriteLine($"  Bit {bit} must be {remaining[0]} (only one left).");
                continue;
            }

            arduino.McpSetBit(vaddr, 'B', bit, true);
            Console.WriteLine($"  Bit {bit} lit.");
            Console.Write($"  Which color?  {string.Join("   ", remaining.Select((c, idx) => $"{idx + 1}={c}"))}  (or Esc to abort): ");
            char choice = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < remaining.Count));
            Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());
            arduino.McpSetBit(vaddr, 'B', bit, false); // off again before the next one

            if (choice == EscKey) { userAborted = true; break; }

            string color = remaining[choice - '1'];
            assignment[bit] = color;
            remaining.Remove(color);
        }

        if (userAborted)
        {
            Console.WriteLine("Signal scan aborted.");
            Console.WriteLine();
            return;
        }

        int redBit = assignment.First(kv => kv.Value == "Red").Key;
        int green1Bit = assignment.First(kv => kv.Value == "Green").Key;
        int green2Bit = assignment.First(kv => kv.Value == "Green2").Key;

        if (!arduino.SetSignal(vaddr, redBit, green1Bit, green2Bit))
        {
            Console.WriteLine($"Failed to store — is 0x{vaddr:X2} in the board hardware table yet? Upload hardware.json first.");
            Console.WriteLine();
            return;
        }

        var file = SystemConfigSession.LoadOrNew();
        SystemConfigSession.SaveSignal(file, scb.Name, new SignalEntry
        {
            VAddr = $"0x{vaddr:X2}",
            RedBit = redBit,
            Green1Bit = green1Bit,
            Green2Bit = green2Bit,
        });

        Console.WriteLine($"Stored: Red=bit{redBit} Green1=bit{green1Bit} Green2=bit{green2Bit}");
        Console.WriteLine("Signal scan complete.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command 3: Track detection — which Port A bit reads train presence, and
    // which level (high/low) means a train is actually there. The bit itself
    // is asked for here rather than declared in boards.json (same reasoning
    // as Signal Scan's candidate bits) since this whole routine only exists
    // because the polarity needs a live, human-confirmed observation anyway.
    // -------------------------------------------------------------------------
    static void TrackDetectionScan(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 3: Track Detection ---");
        Console.WriteLine("Reads the live bit and asks you to confirm what it means.");
        Console.WriteLine();

        var scbs = BoardConfig.Svb.Scbs;
        Console.WriteLine("Boards: " + string.Join("  ", scbs.Select((b, i) => $"{i + 1}={b.Name}")));
        Console.Write("Select board (or Esc): ");
        char boardChoice = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < scbs.Count));
        Console.WriteLine(boardChoice == EscKey ? "[Esc]" : boardChoice.ToString());
        if (boardChoice == EscKey) { Console.WriteLine(); return; }
        var scb = scbs[boardChoice - '1'];
        byte vaddr = scb.VirtualAddress;

        Console.Write("Which Port A bit is track detection on (e.g. 7, blank to abort): ");
        string? bitInput = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(bitInput) || !int.TryParse(bitInput, out int bit) || bit is < 0 or > 7)
        {
            Console.WriteLine(string.IsNullOrEmpty(bitInput) ? "" : "  Must be 0-7.");
            Console.WriteLine();
            return;
        }

        int portA = arduino.McpReadPort(vaddr, 'A');
        if (portA < 0)
        {
            Console.WriteLine("  No response reading Port A.");
            Console.WriteLine();
            return;
        }
        bool bitIsHigh = ((portA >> bit) & 1) != 0;
        Console.WriteLine($"  Bit {bit} currently reads {(bitIsHigh ? "HIGH" : "LOW")}.");

        Console.Write("  Is there a train on the track right now?  Y/N (or Esc to abort): ");
        char present = char.ToUpper(ReadChar(ch => char.ToUpper(ch) == 'Y' || char.ToUpper(ch) == 'N' || ch == EscKey));
        if (present == EscKey) { Console.WriteLine("[Esc]"); Console.WriteLine(); return; }
        Console.WriteLine(present);
        bool trainPresent = present == 'Y';

        bool activeHigh = bitIsHigh == trainPresent;

        if (!arduino.SetTrackDetection(vaddr, bit, activeHigh))
        {
            Console.WriteLine($"Failed to store — is 0x{vaddr:X2} in the board hardware table yet? Upload hardware.json first.");
            Console.WriteLine();
            return;
        }

        var file = SystemConfigSession.LoadOrNew();
        SystemConfigSession.SaveTrackDetection(file, scb.Name, new TrackDetectionEntry
        {
            VAddr = $"0x{vaddr:X2}",
            Bit = bit,
            ActiveHigh = activeHigh,
        });

        Console.WriteLine($"Stored: bit {bit}, {(activeHigh ? "HIGH" : "LOW")} = train present.");
        Console.WriteLine("Track detection complete.");
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
