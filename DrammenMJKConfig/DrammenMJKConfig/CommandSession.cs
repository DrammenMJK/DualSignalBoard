using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// Operate a board: pick it (SCB) first, then Motors, Signals, Status Light,
// Motor Pin Test, Input Test, or Track Detection, then loop within that
// section so repeated checks don't need re-navigating each time. Motors
// never touch port/bit/vaddr directly — resolves label -> slot via
// BoardConfig, then DriveSwitch/ReadSwitch (SW/SR) do all the
// port/bit/polarity resolution in firmware. Signals, the status light, and
// Motor Pin Test go through raw McpSetBit instead (see
// RunSignals/RunStatusLight/RunMotorPinTest) -- there's no "drive and wait"
// concept for a lamp, and Motor Pin Test deliberately skips that logic too,
// for verifying raw wiring during bring-up. Input Test and Track Detection
// are read-only live polls (McpReadPort) -- see RunInputTest/
// RunTrackDetection. See PLAN_Phase1.md, Command mode.
static class CommandSession
{
    // Same motor, same ~5s observed travel time as MotorScan's
    // FeedbackTimeoutMs -- kept consistent so normal operation doesn't
    // spuriously time out right at the edge of a real throw.
    const int TimeoutMs = 10000;

    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Command Mode ===");
        Console.WriteLine($"Operate SVB {BoardConfig.Svb.Name}.");
        Console.WriteLine("Esc = back.");
        Console.WriteLine();

        var scbs = BoardConfig.Svb.Scbs;

        while (true)
        {
            Console.WriteLine("Boards: " + string.Join("  ", scbs.Select((b, i) => $"{i + 1}={b.Name}")));
            Console.Write("Select board (or Esc): ");

            char boardChoice = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < scbs.Count));
            Console.WriteLine(boardChoice == EscKey ? "[Esc]" : boardChoice.ToString());
            if (boardChoice == EscKey) { Console.WriteLine(); return; }

            RunBoard(arduino, scbs[boardChoice - '1']);
        }
    }

    static void RunBoard(ArduinoDevice arduino, BoardScb scb)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"{scb.Name}:");
            Console.WriteLine("  1 = Motors");
            Console.WriteLine("  2 = Signals");
            Console.WriteLine("  3 = Status Light");
            Console.WriteLine("  4 = Motor Pin Test (raw on/off, for wiring checks)");
            Console.WriteLine("  5 = Input Test (live poll of feedback / input pins)");
            Console.WriteLine("  6 = Track Detection (live poll, train present/clear)");
            Console.Write("Choice (Esc for board list): ");

            char input = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '6'));
            Console.WriteLine(input == EscKey ? "[Esc]" : input.ToString());
            if (input == EscKey) { Console.WriteLine(); return; }

            switch (input)
            {
                case '1': RunMotors(arduino, scb); break;
                case '2': RunSignals(arduino, scb); break;
                case '3': RunStatusLight(arduino, scb); break;
                case '4': RunMotorPinTest(arduino, scb); break;
                case '5': RunInputTest(arduino, scb); break;
                case '6': RunTrackDetection(arduino, scb); break;
            }
        }
    }

    static void RunMotors(ArduinoDevice arduino, BoardScb scb)
    {
        // Switch numbers are typed directly rather than picked from an
        // indexed menu -- "5/6" is two physically-coupled switches sharing
        // one motor/feedback pair, so either "5" or "6" must resolve to it.
        var numberToLabel = new Dictionary<string, string>();
        foreach (var sw in scb.Switches)
            foreach (string token in sw.Label.Split('/'))
                numberToLabel[token] = sw.Label;

        while (true)
        {
            // Mark which switches are actually configured (motor found and
            // stored) vs. not -- useful both here during bring-up (only some
            // motors physically installed yet) and later for fault-finding
            // (a switch that should be configured but shows "?" points at
            // EEPROM having been cleared or never scanned).
            var listing = scb.Switches.Select(s =>
            {
                bool configured = BoardConfig.TryFindSlotByLabel(s.Label, out int slot)
                    && arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot) != ArduinoDevice.Unset;
                return configured ? s.Label : $"{s.Label}?";
            });

            Console.WriteLine();
            Console.WriteLine($"{scb.Name} switches: " + string.Join("  ", listing) + "   (? = not configured)");
            Console.Write("Switch number (blank or Esc for board menu): ");

            string? input = ReadLineOrEsc();
            if (input == null) { Console.WriteLine(); return; }

            if (!numberToLabel.TryGetValue(input, out string? label))
            {
                Console.WriteLine("  Unknown switch number.");
                continue;
            }

            if (!BoardConfig.TryFindSlotByLabel(label, out int slot)) continue;

            bool configured = arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot) != ArduinoDevice.Unset;

            if (!configured)
            {
                // No trained feedback mapping yet (Motor scan hasn't stored this
                // slot) -- that only blocks the Rett/Avvik-confirmed drive below,
                // not just running the motor. Fire it raw, the same way Motor
                // scan itself does before it knows which switch moved, and
                // report whatever the feedback chip saw (there may be none
                // wired up yet, which is fine -- that's exactly the case this
                // path exists for).
                int swIndex = scb.Switches.ToList().FindIndex(s => s.Label == label);
                byte vaddr = scb.VirtualAddress;
                int motorBit = scb.MotorBitFor(swIndex);

                int baseline = arduino.McpReadPort(vaddr, 'A');
                int restOlatB = arduino.McpReadPort(vaddr, 'B');
                if (baseline < 0 || restOlatB < 0)
                {
                    Console.WriteLine("  I2C error reading board state.");
                    Console.WriteLine();
                    continue;
                }
                bool restState = ((restOlatB >> motorBit) & 1) != 0;
                bool fireState = !restState;

                Console.WriteLine($"{label} has no trained feedback mapping (Motor scan not run/overwritten for this slot) --");
                Console.WriteLine($"firing raw: motor bit {motorBit} on 0x{vaddr:X2} ({restState} -> {fireState}).");

                if (!arduino.McpSetBit(vaddr, 'B', motorBit, fireState))
                {
                    Console.WriteLine("  I2C error firing motor.");
                    Console.WriteLine();
                    continue;
                }

                int changed = arduino.McpPollChange(vaddr, 'A', (byte)baseline, TimeoutMs);
                if (changed < 0)
                    Console.WriteLine($"  Feedback 0x{vaddr:X2}: no change seen within {TimeoutMs}ms (nothing wired yet, or motor didn't move).");
                else
                    ReportFeedbackChange(vaddr, baseline, changed);

                Console.WriteLine();
                continue;
            }

            var current = arduino.ReadSwitch(slot);
            Console.WriteLine($"Currently at: {current}");

            // No point offering to drive to the position it's already
            // confirmed at -- only offer that when current is Between/Fault/
            // unknown, where neither R nor A is "where it already is".
            bool canR = current != ArduinoDevice.SwitchState.Rett;
            bool canA = current != ArduinoDevice.SwitchState.Avvik;

            char pos;
            if (canR && canA)
            {
                Console.Write($"Drive {label} to:  R = Rett   A = Avvik  (or Esc to cancel): ");
                pos = char.ToUpper(ReadChar(ch => char.ToUpper(ch) == 'R' || char.ToUpper(ch) == 'A' || ch == EscKey));
            }
            else
            {
                // Only one destination is possible -- no need to make them pick a letter.
                pos = canR ? 'R' : 'A';
                string target = pos == 'R' ? "Rett" : "Avvik";
                Console.Write($"Toggle {label} to {target} -- any key to confirm, Esc to cancel: ");
                char key = ReadChar(_ => true);
                if (key == EscKey) pos = EscKey;
            }
            if (pos == EscKey) { Console.WriteLine("[Esc]"); Console.WriteLine(); continue; }
            Console.WriteLine(pos);

            byte fbVAddr = arduino.EepromRead(ArduinoDevice.RegionSlotFeedbackVAddr + slot);
            int fbBefore = arduino.McpReadPort(fbVAddr, 'A');

            var result = arduino.DriveSwitch(slot, pos, TimeoutMs);
            Console.WriteLine($"Result: {result}");

            int fbAfter = arduino.McpReadPort(fbVAddr, 'A');
            ReportFeedbackChange(fbVAddr, fbBefore, fbAfter);

            Console.WriteLine();
        }
    }

    // "Which feedback address changed" report shared by the trained
    // (Rett/Avvik-confirmed) and raw firing paths above -- names the vaddr
    // and the GPIOA bits that flipped, so cross-wiring between switches
    // (two slots driving the same bit, wrong feedback chip, etc.) is visible
    // immediately instead of only showing up as "wrong switch moved".
    static void ReportFeedbackChange(byte vaddr, int before, int after)
    {
        if (before < 0 || after < 0)
        {
            Console.WriteLine($"  Feedback 0x{vaddr:X2}: I2C error reading GPIOA.");
            return;
        }
        if (before == after)
        {
            Console.WriteLine($"  Feedback 0x{vaddr:X2}: no change (GPIOA stayed 0x{before:X2}).");
            return;
        }
        int diff = before ^ after;
        var bits = Enumerable.Range(0, 8).Where(b => ((diff >> b) & 1) != 0).ToList();
        Console.WriteLine($"  Feedback 0x{vaddr:X2}: GPIOA 0x{before:X2} -> 0x{after:X2} (bit{(bits.Count > 1 ? "s" : "")} {string.Join(",", bits)} changed).");
    }

    // Wiring bring-up tool. Holds an explicit model of every output bit on
    // the board -- the inverter-enable pin (if any) plus one bit per switch
    // motor -- and rewrites ALL of them on every change via raw McpSetBit,
    // which forces each bit to output before writing it. That makes this
    // independent of whether the board's hardware.json has been re-pushed
    // since a pin's direction last changed. After each write it reads the
    // port(s) back (McpReadPort -> GPIO) and shows commanded-vs-measured for
    // every bit, so a pin that won't follow its command -- stale EEPROM,
    // wiring fault, dead inverter, I2C error -- is visible immediately.
    // Single keypress throughout (ReadChar), no Enter.
    static void RunMotorPinTest(ArduinoDevice arduino, BoardScb scb)
    {
        byte vaddr = scb.VirtualAddress;
        bool hasEnable = scb.InverterEnablePin != null;
        (char Port, int Bit) en = hasEnable ? scb.InverterEnablePin!.Value : ('A', 0);

        var motorCmd = new bool[scb.Switches.Count];
        bool enableCmd = false;
        int bitCount = scb.Switches.Count + (hasEnable ? 1 : 0);

        bool WriteAll()
        {
            bool ok = true;
            if (hasEnable) ok &= arduino.McpSetBit(vaddr, en.Port, en.Bit, enableCmd);
            for (int i = 0; i < scb.Switches.Count; i++)
                ok &= arduino.McpSetBit(vaddr, 'B', scb.MotorBitFor(i), motorCmd[i]);
            return ok;
        }

        static string Lvl(int portVal, int bit) =>
            portVal < 0 ? "?" : (((portVal >> bit) & 1) == 1 ? "H" : "L");

        // IODIR bit: 0 = output, 1 = input (MCP23017 convention).
        static string Dir(int iodir, int bit) =>
            iodir < 0 ? "?" : (((iodir >> bit) & 1) == 0 ? "out" : "in");

        const byte IodirA = 0x00, IodirB = 0x01;

        while (true)
        {
            int gpioA = arduino.McpReadPort(vaddr, 'A');
            int gpioB = arduino.McpReadPort(vaddr, 'B');
            int iodirA = arduino.McpReadRegister(vaddr, IodirA);
            int iodirB = arduino.McpReadRegister(vaddr, IodirB);

            Console.WriteLine();
            Console.WriteLine($"{scb.Name} motor pin test  (vaddr 0x{vaddr:X2})");
            Console.WriteLine("  key  name              pin  dir  cmd  measured");
            if (hasEnable)
            {
                bool enB = en.Port == 'B';
                Console.WriteLine($"   E   {"Inverter Enable",-16} {en.Port}{en.Bit}  {Dir(enB ? iodirB : iodirA, en.Bit),-4} {(enableCmd ? "H" : "L")}    {Lvl(enB ? gpioB : gpioA, en.Bit)}");
            }
            for (int i = 0; i < scb.Switches.Count; i++)
            {
                int b = scb.MotorBitFor(i);
                Console.WriteLine($"   {i + 1}   {scb.Switches[i].Label,-16} B{b}  {Dir(iodirB, b),-4} {(motorCmd[i] ? "H" : "L")}    {Lvl(gpioB, b)}");
            }
            Console.Write("Select E/1-9, then H/L  (Esc = board menu): ");

            char choice = ReadChar(ch =>
                ch == EscKey ||
                (hasEnable && char.ToUpper(ch) == 'E') ||
                (ch >= '1' && ch <= '9' && (ch - '0') <= scb.Switches.Count));
            Console.WriteLine(choice == EscKey ? "[Esc]" : char.ToUpper(choice).ToString());
            if (choice == EscKey) { Console.WriteLine(); return; }

            bool isEnable = char.ToUpper(choice) == 'E';
            string what = isEnable ? "Inverter Enable" : scb.Switches[choice - '1'].Label;

            Console.Write($"Set {what} to:  H = High   L = Low  (Esc to cancel): ");
            char hl = ReadChar(ch => char.ToUpper(ch) is 'H' or 'L' || ch == EscKey);
            Console.WriteLine(hl == EscKey ? "[Esc]" : char.ToUpper(hl).ToString());
            if (hl == EscKey) { Console.WriteLine(); continue; }

            bool high = char.ToUpper(hl) == 'H';
            if (isEnable) enableCmd = high;
            else motorCmd[choice - '1'] = high;

            Console.WriteLine(WriteAll()
                ? $"  Wrote all {bitCount} bits."
                : "  WARNING: a bit write did not return OK -- I2C or board fault.");
        }
    }

    // Live poll of every input-configured pin on the board -- switch feedback
    // and track detection, all declared in-pu, so they idle High and read Low
    // when shorted to GND (panel contact / detector closing). No interrupt
    // line is wired yet, so this just re-reads GPIOA/GPIOB on a fixed
    // interval and redraws one line in place. Which bits are inputs is read
    // back from the chip's IODIR, not assumed. Any key stops it. For wiring
    // bring-up: short each feedback pin to GND and watch its bit flip H->L.
    static void RunInputTest(ArduinoDevice arduino, BoardScb scb)
    {
        byte vaddr = scb.VirtualAddress;
        const byte IodirA = 0x00, IodirB = 0x01;

        int iodirA = arduino.McpReadRegister(vaddr, IodirA);
        int iodirB = arduino.McpReadRegister(vaddr, IodirB);
        if (iodirA < 0 || iodirB < 0)
        {
            Console.WriteLine("  Could not read IODIR from the board.");
            Console.WriteLine();
            return;
        }

        var inputs = new List<(char Port, int Bit)>();
        for (int b = 0; b < 8; b++) if (((iodirA >> b) & 1) == 1) inputs.Add(('A', b));
        for (int b = 0; b < 8; b++) if (((iodirB >> b) & 1) == 1) inputs.Add(('B', b));

        if (inputs.Count == 0)
        {
            Console.WriteLine("  No input-configured pins on this board -- upload hardware.json first?");
            Console.WriteLine();
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{scb.Name} input test  (vaddr 0x{vaddr:X2}) -- {inputs.Count} input pins.");
        Console.WriteLine("  in-pu pins idle High; short to GND to read Low.  Any key to stop.");
        Console.WriteLine();

        while (Console.KeyAvailable) Console.ReadKey(intercept: true); // drop stale keys

        while (!Console.KeyAvailable)
        {
            int gpioA = arduino.McpReadPort(vaddr, 'A');
            int gpioB = arduino.McpReadPort(vaddr, 'B');

            var sb = new System.Text.StringBuilder("\r  ");
            var low = new List<string>();
            foreach (var (port, bit) in inputs)
            {
                int gpio = port == 'A' ? gpioA : gpioB;
                bool bad = gpio < 0;
                bool lvlHigh = !bad && ((gpio >> bit) & 1) == 1;
                sb.Append($"{port}{bit}:{(bad ? "?" : lvlHigh ? "H" : "L")}  ");
                if (!bad && !lvlHigh) low.Add($"{port}{bit}");
            }
            if (low.Count > 0) sb.Append($"  LOW: {string.Join(",", low)}");
            Console.Write(sb.ToString().PadRight(78));

            System.Threading.Thread.Sleep(300);
        }
        Console.ReadKey(intercept: true); // consume the stop key
        Console.WriteLine();
        Console.WriteLine();
    }

    // Live poll of the board's track detection bit (always Port A -- see
    // ConfigSession.TrackDetectionScan), reporting train present/clear every
    // 0.3s. Bit + polarity come from SystemConfig.json (Track Detection
    // Scan's result), not boards.json -- discovered, not declared, same as
    // RunSignals needing SystemConfig for its bits. Any key stops it.
    static void RunTrackDetection(ArduinoDevice arduino, BoardScb scb)
    {
        var file = SystemConfigSession.LoadOrNew();
        if (!file.TrackDetections.TryGetValue(scb.Name, out var td) || td.VAddr == null || td.Bit == null || td.ActiveHigh == null)
        {
            Console.WriteLine($"  {scb.Name} has no track detection configured — run Track detection scan first.");
            Console.WriteLine();
            return;
        }
        byte vaddr = Convert.ToByte(td.VAddr, 16);
        int bit = td.Bit.Value;
        bool activeHigh = td.ActiveHigh.Value;

        Console.WriteLine();
        Console.WriteLine($"{scb.Name} track detection  (vaddr 0x{vaddr:X2}, Port A bit {bit}, active {(activeHigh ? "high" : "low")})");
        Console.WriteLine("Polling every 0.3s. Any key to stop.");
        Console.WriteLine();

        while (Console.KeyAvailable) Console.ReadKey(intercept: true); // drop stale keys

        while (!Console.KeyAvailable)
        {
            int gpioA = arduino.McpReadPort(vaddr, 'A');
            string state;
            if (gpioA < 0)
            {
                state = "? no response";
            }
            else
            {
                bool bitHigh = ((gpioA >> bit) & 1) == 1;
                state = bitHigh == activeHigh ? "TRAIN DETECTED" : "clear";
            }
            Console.Write($"\r  {state}".PadRight(40));

            System.Threading.Thread.Sleep(300);
        }
        Console.ReadKey(intercept: true); // consume the stop key
        Console.WriteLine();
        Console.WriteLine();
    }

    // Sets a board's signal to one of its states directly (raw McpSetBit
    // writes, like Bench Test -- no EEPROM/SW involvement, since there's no
    // "drive to state and wait" concept for a lamp the way there is for a
    // switch motor). Bit assignment comes from SystemConfig.json (Signal
    // Scan's result), not boards.json -- it was discovered, not declared.
    // Loops so repeated checks don't need re-entering this section each time.
    static void RunSignals(ArduinoDevice arduino, BoardScb scb)
    {
        var file = SystemConfigSession.LoadOrNew();
        if (!file.Signals.TryGetValue(scb.Name, out var sig) || sig.VAddr == null)
        {
            Console.WriteLine($"  {scb.Name} has no signal configured — run Signal scan first.");
            Console.WriteLine();
            return;
        }
        byte vaddr = Convert.ToByte(sig.VAddr, 16);
        int redBit = sig.RedBit ?? 0, green1Bit = sig.Green1Bit ?? 0, green2Bit = sig.Green2Bit ?? 0;

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"{scb.Name} signal:");
            Console.WriteLine("  1 = Allowed to pass -- Rett   (Green)");
            Console.WriteLine("  2 = Allowed to pass -- Avvik  (Green + Green2)");
            Console.WriteLine("  3 = Not allowed to pass       (Red)");
            Console.WriteLine("  4 = All off");
            Console.Write("Choice (Esc for board menu): ");

            char input = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '4'));
            Console.WriteLine(input == EscKey ? "[Esc]" : input.ToString());
            if (input == EscKey) { Console.WriteLine(); return; }

            bool red = input == '3';
            bool green1 = input is '1' or '2';
            bool green2 = input == '2';
            // input == '4' (all off): red/green1/green2 all stay false.

            arduino.McpSetBit(vaddr, 'B', redBit, red);
            arduino.McpSetBit(vaddr, 'B', green1Bit, green1);
            arduino.McpSetBit(vaddr, 'B', green2Bit, green2);

            string label = input switch
            {
                '1' => "Allowed to pass (Rett) -- Green",
                '2' => "Allowed to pass (Avvik) -- Green + Green2",
                '3' => "Not allowed to pass -- Red",
                _ => "All off",
            };
            Console.WriteLine($"  Set: {label}");
        }
    }

    // Status LED's vaddr+port+bit come straight from boards.json
    // (scb.StatusLedPin) -- declared, not discovered, so no SystemConfig.json
    // lookup needed the way RunSignals needs one for Signal Scan's result.
    static void RunStatusLight(ArduinoDevice arduino, BoardScb scb)
    {
        if (scb.StatusLedPin is not { } led)
        {
            Console.WriteLine($"  {scb.Name} has no status LED configured.");
            Console.WriteLine();
            return;
        }
        byte vaddr = scb.VirtualAddress;

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"{scb.Name} status light:");
            Console.WriteLine("  1 = On");
            Console.WriteLine("  2 = Off");
            Console.WriteLine("  3 = Flash test (6x, 0.5s period)");
            Console.Write("Choice (Esc for board menu): ");

            char input = ReadChar(ch => ch == EscKey || ch is '1' or '2' or '3');
            Console.WriteLine(input == EscKey ? "[Esc]" : input.ToString());
            if (input == EscKey) { Console.WriteLine(); return; }

            if (input == '3')
            {
                FlashStatusLight(arduino, scb, led, vaddr);
                continue;
            }

            bool on = input == '1';
            arduino.McpSetBit(vaddr, led.Port, led.Bit, on);
            Console.WriteLine($"  Set: {(on ? "On" : "Off")}");
        }
    }

    // 6 on/off cycles at a 0.5s period (0.25s on, 0.25s off) -- a quick
    // visual check that the lamp and its wiring are good. Enables the
    // inverter first, if this board has one: the status lamp is wired
    // through the same enable rail as the motor driver stage on some
    // boards, so without it the lamp wouldn't light regardless of the GPIO
    // bit. Active-low -- McpSetBit(..., false) drives it low = enabled.
    // Left enabled afterward (not restored) since that's normally what you
    // want before going on to test the rest of the board.
    static void FlashStatusLight(ArduinoDevice arduino, BoardScb scb, (char Port, int Bit) led, byte vaddr)
    {
        if (scb.InverterEnablePin is { } en)
            arduino.McpSetBit(vaddr, en.Port, en.Bit, false);

        Console.WriteLine("  Flashing 6x...");
        for (int i = 0; i < 6; i++)
        {
            arduino.McpSetBit(vaddr, led.Port, led.Bit, true);
            System.Threading.Thread.Sleep(250);
            arduino.McpSetBit(vaddr, led.Port, led.Bit, false);
            System.Threading.Thread.Sleep(250);
        }
        Console.WriteLine("  Done.");
    }
}
