using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// The Config-mode commands that talk to the board directly: motor, signal
// and track detection scans, Test/Prod mode, EEPROM reset and restart. The
// Config menu itself (which also offers the other sessions) is built in
// Program.cs.
sealed class ConfigSession(IArduinoDevice arduino)
{
    // -------------------------------------------------------------------------
    // Command 1: Motor scan (Phase 1 scope — see PLAN_Phase1.md)
    //
    // Assumes hardware.json has already been uploaded (Port B output, Port A
    // input+pullup on the board being scanned) — Motor Scan doesn't touch
    // MDIR/MPU itself. Dreieskive is declared (fixed pins), never scanned.
    // -------------------------------------------------------------------------
    // Real switch motors take several seconds to travel end-to-end (~5s
    // observed) -- needs real margin above that, not just above the I2C
    // round-trip time.
    const int FeedbackTimeoutMs = 10000;

    public void MotorScan()
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 1: Motor Scan ---");
        Console.WriteLine("Fires each motor pin in turn. Watch the layout to see what moves.");
        Console.WriteLine("Esc = abort at any point.");
        Console.WriteLine();

        if (PickScb() is not { } scb) return;
        var file = SystemConfigSession.LoadOrNew();
        if (!DeclareBoardFacts(scb, file)) return;

        var assignedLabels = new HashSet<string>();
        var keptBits = new HashSet<int>();
        if (!ChooseScope(scb, file, assignedLabels, keptBits)) { Console.WriteLine("Motor scan aborted."); Console.WriteLine(); return; }

        bool userAborted = false;
        for (int i = 0; i < scb.Switches.Count && !userAborted; i++)
        {
            int motorBit = scb.MotorBitFor(i);
            if (!keptBits.Contains(motorBit))
                userAborted = !ScanBit(scb, motorBit, assignedLabels, file);
        }

        Console.WriteLine(userAborted ? "Motor scan aborted." : "Motor scan complete.");
        Console.WriteLine();
    }

    // Declare dreieskive (only for the one board that owns it) + status
    // LED + inverter enable facts up front — not scanned, but known now, so
    // SystemConfig.json/EEPROM fully describe the board from the first
    // scan onward, not just its discovered switches. False = stop the scan.
    bool DeclareBoardFacts(BoardScb scb, SystemConfigFile file)
    {
        byte vaddr = scb.VirtualAddress;
        string vaddrHex = $"0x{vaddr:X2}";

        if (scb.DreieskivePins is { } drei)
        {
            if (!arduino.EepromWrite(ArduinoDevice.AddrDreieskiveVAddr, vaddr) ||
                !arduino.EepromWrite(ArduinoDevice.AddrDreieskiveMotorPinBase, (byte)drei.Base))
            {
                Console.WriteLine("Failed to declare dreieskive — no response from Arduino.");
                Console.WriteLine();
                return false;
            }
            file.Dreieskive = new DreieskiveEntry { MotorVAddr = vaddrHex, MotorPinBase = drei.Base, CwPolarity = null };
            SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
            Console.WriteLine($"Declared dreieskive (pins {drei.Base}/{drei.Upper}).");
        }

        if (scb.StatusLedPin is { } led)
        {
            if (!arduino.SetStatusLed(vaddr, led.Bit))
            {
                Console.WriteLine($"Failed to declare status LED — is {vaddrHex} in the board hardware table yet? Upload hardware.json first.");
                Console.WriteLine();
                return false;
            }
            file.StatusLeds[scb.Name] = new StatusLedEntry { VAddr = vaddrHex, Bit = led.Bit };
            SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
            Console.WriteLine($"Declared status LED (bit {led.Bit}).");
        }

        if (scb.InverterEnablePin is { } inv)
        {
            if (!arduino.SetInverterEnable(vaddr, inv.Port, inv.Bit))
            {
                Console.WriteLine($"Failed to declare inverter enable — is {vaddrHex} in the board hardware table yet? Upload hardware.json first.");
                Console.WriteLine();
                return false;
            }
            file.InverterEnables[scb.Name] = new InverterEnableEntry { VAddr = vaddrHex, Port = inv.Port.ToString(), Bit = inv.Bit };
            SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
            Console.WriteLine($"Declared inverter enable (port {inv.Port} bit {inv.Bit}).");
        }
        Console.WriteLine();
        return true;
    }

    // Check what's already in EEPROM for THIS board's switches before
    // deciding scope -- scoped to scb.Switches, not every SCB system-wide.
    // Silently skipping already-configured switches with no explanation
    // looked exactly like the scan doing nothing; ask instead. K fills
    // `keptLabels`/`keptBits`; O clears the board's slots. False = abort.
    bool ChooseScope(BoardScb scb, SystemConfigFile file, HashSet<string> keptLabels, HashSet<int> keptBits)
    {
        var scbLabels = scb.Switches.Select(s => s.Label).ToHashSet();
        var alreadyConfigured = new List<(string Label, int MotorBit, int Slot)>();
        foreach (var (_, motorBit, label, slot) in BoardConfig.AllSwitchSlots())
        {
            if (!scbLabels.Contains(label)) continue;
            byte? motorVAddr = arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot);
            if (motorVAddr == null) { Console.WriteLine("EEPROM read failed — check the connection."); return false; }
            if (motorVAddr != ArduinoDevice.Unset) alreadyConfigured.Add((label, motorBit, slot));
        }
        if (alreadyConfigured.Count == 0) return true;

        Console.WriteLine($"{alreadyConfigured.Count} switch(es) on {scb.Name} already have EEPROM config:");
        foreach (var (label, motorBit, _) in alreadyConfigured)
            Console.WriteLine($"  {label} (motor bit {motorBit})");
        Console.WriteLine();
        Console.Write("K = keep existing, only scan what's new   O = overwrite all, rescan everything   Esc = abort: ");
        char choice = char.ToUpper(ReadChar(ch => char.ToUpper(ch) == 'K' || char.ToUpper(ch) == 'O' || ch == EscKey));
        Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());
        Console.WriteLine();
        if (choice == EscKey) return false;

        if (choice == 'K')
        {
            foreach (var (label, motorBit, _) in alreadyConfigured)
            {
                keptLabels.Add(label);
                keptBits.Add(motorBit);
            }
            return true;
        }

        // 'O': clear this board's existing slot data up front, not just
        // whichever slots get reassigned below. Otherwise a skipped/timed-out
        // bit (no feedback seen) leaves the OLD slot bytes behind -- which is
        // how two switches on this board once ended up both pointing at the
        // same physical motor bit. The mirror drops them too, so a later
        // Restore can't write them back.
        foreach (var (label, _, slot) in alreadyConfigured)
        {
            if (!arduino.EepromWrite(ArduinoDevice.RegionSlotMotorVAddr + slot, ArduinoDevice.Unset))
            {
                Console.WriteLine($"Failed to clear {label} — no response from Arduino.");
                SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
                return false;
            }
            file.Switches.Remove(label);
        }
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
        return true;
    }

    // Fires one motor bit until the operator assigns it to a switch, skips
    // it, or aborts. Stores an assignment in EEPROM, then mirrors it.
    // False = abort the whole scan.
    bool ScanBit(BoardScb scb, int motorBit, HashSet<string> assignedLabels, SystemConfigFile file)
    {
        byte vaddr = scb.VirtualAddress;
        while (true)
        {
            Console.WriteLine($"Firing motor bit {motorBit}...");
            int baseline = arduino.McpReadPort(vaddr, 'A');
            if (baseline < 0) { Console.WriteLine("  I2C error reading baseline. Aborting scan."); return false; }

            // Fire by toggling away from whatever the bit is currently at --
            // NOT a hardcoded false. apply_board_hw() already leaves every
            // motor bit at false (OLATB=0x00 safe default) right after boot,
            // so firing with a hardcoded false was a no-op on a fresh board:
            // nothing ever transitioned, so nothing moved. Reading back first
            // guarantees an actual transition regardless of starting state.
            int restOlatB = arduino.McpReadPort(vaddr, 'B');
            if (restOlatB < 0) { Console.WriteLine("  I2C error reading motor rest state. Aborting scan."); return false; }
            bool restState = ((restOlatB >> motorBit) & 1) != 0;
            bool fireState = !restState;

            if (!arduino.McpSetBit(vaddr, 'B', motorBit, fireState)) { Console.WriteLine("  I2C error firing motor. Aborting scan."); return false; }

            int changed = arduino.McpPollChange(vaddr, 'A', (byte)baseline, FeedbackTimeoutMs);
            int pairLow = changed < 0 ? -1 : FindPairLow(changed ^ baseline);
            if (pairLow < 0)
            {
                Console.WriteLine(changed < 0
                    ? "  No feedback detected (timed out). Nothing may be installed at this position yet."
                    : $"  Anomaly: unexpected bits changed (diff=0x{changed ^ baseline:X2}).");
                Console.Write("  1 = retry   0 = skip this pin   Esc = abort: ");
                char c = ReadChar(ch => ch == '1' || ch == '0' || ch == EscKey);
                Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
                if (c == EscKey) return false;
                if (c == '0') return true;
                continue;
            }

            var remaining = scb.Switches.Select(s => s.Label).Where(l => !assignedLabels.Contains(l)).ToList();
            Console.WriteLine($"  Feedback changed on pair ({pairLow},{pairLow + 1}).");
            Console.WriteLine("  Which switch moved?");
            for (int r = 0; r < remaining.Count; r++)
                Console.WriteLine($"    {r + 1}) {remaining[r]}");
            Console.Write("  Choice (0=skip, R=repeat, Esc=abort): ");

            char choice = ReadChar(ch => ch == '0' || ch == EscKey || char.ToUpper(ch) == 'R' || (ch >= '1' && ch <= '9' && (ch - '1') < remaining.Count));
            Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());

            if (choice == EscKey) return false;
            // R: re-fire from the top so the user can watch it move again.
            if (char.ToUpper(choice) == 'R') continue;
            if (choice == '0')
            {
                arduino.McpSetBit(vaddr, 'B', motorBit, false); // leave stalled — safe default
                return true;
            }

            string chosenLabel = remaining[choice - '1'];
            Console.Write($"  Is {chosenLabel} now at:  R = Rett   A = Avvik: ");
            char pos = char.ToUpper(ReadChar(ch => char.ToUpper(ch) == 'R' || char.ToUpper(ch) == 'A'));
            Console.WriteLine(pos);

            var (polarity, rettBit, avvikBit) = FeedbackMapping(fireState, pos == 'R', pairLow, changed);
            int otherBit = pos == 'R' ? avvikBit : rettBit;

            // Verification move — not strictly required (the pair structure
            // already implies the second bit), but catches wiring/mechanical
            // faults at config time by confirming the opposite position too.
            // Toggles back to the original rest state (not a hardcoded true),
            // for the same reason firing above toggles instead of hardcoding.
            Console.WriteLine("  Verifying opposite position...");
            arduino.McpSetBit(vaddr, 'B', motorBit, restState);
            int confirmChanged = arduino.McpPollChange(vaddr, 'A', (byte)changed, FeedbackTimeoutMs);
            if (confirmChanged < 0 || ((confirmChanged >> otherBit) & 1) == 0)
                Console.WriteLine("  Warning: opposite position not confirmed as expected — check wiring.");

            if (!BoardConfig.TryFindSlotByLabel(chosenLabel, out int slot))
            {
                Console.WriteLine("  Internal error: label has no slot. Skipping store.");
                return true;
            }

            var data = new SwitchSlotData(vaddr, (byte)motorBit, polarity, vaddr, rettBit, avvikBit);
            if (!data.Write(arduino, slot))
            {
                Console.WriteLine($"  EEPROM write failed for {chosenLabel} -- slot may be half-written, not mirrored. Aborting scan.");
                return false;
            }
            data.ApplyTo(file, chosenLabel);
            SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
            assignedLabels.Add(chosenLabel);

            Console.WriteLine($"  Stored: {chosenLabel} -> motorBit={motorBit} pol={polarity} Rett={rettBit} Avvik={avvikBit}");
            Console.WriteLine();
            return true;
        }
    }

    // Maps one observed motor move to the slot's polarity and feedback bits.
    // `fireState` is the OLATB bit value the motor was just driven to, and
    // `atRett` whether the operator saw it end at Rett. `changed` is the
    // feedback port after the move; of the changed pair (pairLow, pairLow+1),
    // the bit now high is the contact for the position just reached.
    // Polarity must be the literal OLATB bit value that drives to Rett --
    // Firmware.ino's cmdSW reads it that way directly (targetLevel = pos=='R'
    // ? polarity : 1-polarity): fireState if the move ended at Rett, else
    // the opposite.
    internal static (byte Polarity, byte RettBit, byte AvvikBit) FeedbackMapping(bool fireState, bool atRett, int pairLow, int changed)
    {
        int reachedBit = ((changed >> (pairLow + 1)) & 1) != 0 ? pairLow + 1 : pairLow;
        int otherBit = reachedBit == pairLow ? pairLow + 1 : pairLow;
        byte polarity = (byte)((atRett ? fireState : !fireState) ? 1 : 0);
        return atRett
            ? (polarity, (byte)reachedBit, (byte)otherBit)
            : (polarity, (byte)otherBit, (byte)reachedBit);
    }

    // -------------------------------------------------------------------------
    // Command 2: Signal scan — finds which Port B bits drive a signal's 3
    // lamps (Red/Green1/Green2). Lights one at a time and asks; once 2 of the
    // 3 are answered, the last one is inferred, no need to ask or light it.
    // No boards.json field for "this board has a signal" yet (deliberately
    // deferred, see PLAN_Phase3.md), so the candidate bits are asked for
    // directly rather than declared.
    // -------------------------------------------------------------------------
    public void SignalScan()
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 2: Signal Scan ---");
        Console.WriteLine("Lights each signal lamp in turn. Watch the signal to see which lights.");
        Console.WriteLine("Esc = abort at any point.");
        Console.WriteLine();

        if (PickScb() is not { } scb) return;
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

        // Lamp check: all lamps on at once (bulbs/wiring alive?) before
        // working out which bit is which color.
        while (true)
        {
            Console.Write("S = scan colors   L = all lamps on   Esc = abort: ");
            char mode = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'S' or 'L' || ch == EscKey));
            Console.WriteLine(mode == EscKey ? "[Esc]" : mode.ToString());
            if (mode == EscKey) { Console.WriteLine(); return; }
            if (mode == 'S') break;

            foreach (int bit in bits) arduino.McpSetBit(vaddr, 'B', bit, true);
            Console.Write($"  Bits {string.Join(", ", bits)} on. Any key to turn them off: ");
            ReadChar(_ => true);
            foreach (int bit in bits) arduino.McpSetBit(vaddr, 'B', bit, false);
            Console.WriteLine("off.");
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
        file.Signals[scb.Name] = new SignalEntry
        {
            VAddr = $"0x{vaddr:X2}",
            RedBit = redBit,
            Green1Bit = green1Bit,
            Green2Bit = green2Bit,
        };
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);

        Console.WriteLine($"Stored: Red=bit{redBit} Green1=bit{green1Bit} Green2=bit{green2Bit}");
        Console.WriteLine("Signal scan complete.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command 3: Track detection — which Port A bit reads train presence, and
    // which level (high/low) means a train is actually there. The bit comes
    // from boards.json's trackDetectBit when declared (it's fixed wiring);
    // only the polarity needs a live, human-confirmed observation. Asked for
    // as a fallback on boards that don't declare it.
    // -------------------------------------------------------------------------
    public void TrackDetectionScan()
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 3: Track Detection ---");
        Console.WriteLine("Reads the live bit and asks you to confirm what it means.");
        Console.WriteLine();

        if (PickScb() is not { } scb) return;
        byte vaddr = scb.VirtualAddress;

        int bit;
        if (scb.TrackDetectBit is int declared)
        {
            bit = declared;
            Console.WriteLine($"Track detection is Port A bit {bit} (declared in boards.json).");
        }
        else
        {
            Console.Write("Which Port A bit is track detection on (e.g. 7, blank to abort): ");
            string? bitInput = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(bitInput) || !int.TryParse(bitInput, out bit) || bit is < 0 or > 7)
            {
                Console.WriteLine(string.IsNullOrEmpty(bitInput) ? "" : "  Must be 0-7.");
                Console.WriteLine();
                return;
            }
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
        file.TrackDetections[scb.Name] = new TrackDetectionEntry
        {
            VAddr = $"0x{vaddr:X2}",
            Bit = bit,
            ActiveHigh = activeHigh,
        };
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);

        Console.WriteLine($"Stored: bit {bit}, {(activeHigh ? "HIGH" : "LOW")} = train present.");
        Console.WriteLine("Track detection complete.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Command R: Reset — erase all board hardware / switch / dreieskive /
    // status LED / fade config from EEPROM. Carried over from the pre-Phase-1
    // ConfigSession; EC wipes 0x02..EEPROM_ERASE_END on the firmware side.
    // -------------------------------------------------------------------------
    // Test mode (the default -- also whatever a blank/erased board reads as)
    // never lets the firmware touch the TWI peripheral at all, so a new
    // install's wiring can be safely characterized (I2CD, hand-probing) with
    // zero risk of the board trying to actually drive a bus that hasn't been
    // verified yet. Switch to Prod once satisfied -- takes a reset to apply.
    public void ModeConfig()
    {
        Console.WriteLine();
        Console.WriteLine("--- Command M: Test/Prod Mode ---");

        bool? current = arduino.IsProdMode();
        Console.WriteLine(current switch
        {
            true => "Current mode: PROD (I2C active at boot).",
            false => "Current mode: TEST (I2C off at boot -- SDA/SCL are plain pulled-up inputs).",
            null => "Current mode: unknown -- no response from Arduino.",
        });
        Console.WriteLine();
        Console.WriteLine("  T = Test  -- safe: verify wiring first (Bench test -> I2C bus diagnostic).");
        Console.WriteLine("  P = Prod  -- fires up I2C at boot, same as normal operation.");
        Console.Write("Choice (Esc to leave unchanged): ");

        char choice = ReadChar(ch => ch == EscKey || char.ToUpper(ch) == 'T' || char.ToUpper(ch) == 'P');
        Console.WriteLine(choice == EscKey ? "[Esc]" : char.ToUpper(choice).ToString());
        if (choice == EscKey) { Console.WriteLine(); return; }

        bool wantProd = char.ToUpper(choice) == 'P';
        if (!arduino.SetMode(wantProd))
        {
            Console.WriteLine("  Failed — no response from Arduino.");
            Console.WriteLine();
            return;
        }
        Console.WriteLine($"  Stored ({(wantProd ? "PROD" : "TEST")}).");

        // A real watchdog-triggered reset (RST) re-runs setup() the same way
        // power-cycling would -- no need to physically unplug the board.
        Console.Write("Reset now to apply? Y = yes, any other key = later: ");
        char resetNow = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
        Console.WriteLine(resetNow);
        if (resetNow == 'Y')
            Restart();
        else
            Console.WriteLine("  Not applied yet -- reset later (Config -> A) when ready.");
        Console.WriteLine();
    }

    public void Restart()
    {
        arduino.SoftReset();
        Console.WriteLine("  Reset sent -- waiting for the board to restart...");
        Console.WriteLine();
        Console.WriteLine();
        // The reboot banner (status() lines, '!'-prefixed) streams in
        // asynchronously via the background read thread, not through
        // Ask()/capture -- there's nothing to await. Without this pause
        // Menu.Run() redraws the menu immediately on return, and the banner
        // lands underneath it a moment later, out of order. Long enough for
        // the watchdog reset + bootloader + setup() + the banner itself to
        // arrive over serial.
        Thread.Sleep(1500);
        Console.WriteLine();
    }

    // Read-only: dumps the hardware and system tables and lists every record
    // that points at a missing board, a wrong-direction pin, or an output
    // pin another record already drives.
    public void VerifyEeprom()
    {
        Console.WriteLine();
        Console.WriteLine("--- Verify EEPROM (read-only) ---");
        var chips = arduino.HardwareConfigDownload();
        var lines = arduino.SystemConfigDownload();
        if (chips == null || lines == null) { Console.WriteLine("Timeout reading EEPROM from the Arduino."); Console.WriteLine(); return; }

        var problems = EepromCheck.Problems(chips, lines);
        foreach (string p in problems) Console.WriteLine($"  {p}");
        Console.WriteLine(problems.Count == 0
            ? $"EEPROM OK: {chips.Count} board(s), {lines.Count} config record(s)."
            : $"{problems.Count} problem(s) found.");
        Console.WriteLine();
    }

    public void ResetConfig()
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
