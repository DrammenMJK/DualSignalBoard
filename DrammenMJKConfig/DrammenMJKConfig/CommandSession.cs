using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// Operate a board: pick it (SCB) first, then Motors, Signals, or Status
// Light, then loop within that section so repeated checks don't need
// re-navigating each time. Motors never touch port/bit/vaddr directly —
// resolves label -> slot via BoardConfig, then DriveSwitch/ReadSwitch (SW/SR)
// do all the port/bit/polarity resolution in firmware. Signals and the
// status light go through raw McpSetBit instead (see RunSignals/
// RunStatusLight) -- there's no "drive and wait" concept for a lamp the way
// there is for a switch motor. See PLAN_Phase1.md, Command mode.
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
            Console.Write("Choice (blank or Esc for board list): ");

            string? input = ReadLineOrEsc();
            if (input == null) { Console.WriteLine(); return; }

            switch (input)
            {
                case "1": RunMotors(arduino, scb); break;
                case "2": RunSignals(arduino, scb); break;
                case "3": RunStatusLight(arduino, scb); break;
                default: Console.WriteLine("  Unknown choice."); break;
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

            if (arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot) == ArduinoDevice.Unset)
            {
                Console.WriteLine($"Switch {label} is not configured — run Motor scan first.");
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

            var result = arduino.DriveSwitch(slot, pos, TimeoutMs);
            Console.WriteLine($"Result: {result}");
            Console.WriteLine();
        }
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
            Console.Write("Choice (blank or Esc for board menu): ");

            string? input = ReadLineOrEsc();
            if (input == null) { Console.WriteLine(); return; }
            if (input is not ("1" or "2" or "3" or "4"))
            {
                Console.WriteLine("  Unknown choice.");
                continue;
            }

            bool red = input == "3";
            bool green1 = input is "1" or "2";
            bool green2 = input == "2";
            // input == "4" (all off): red/green1/green2 all stay false.

            arduino.McpSetBit(vaddr, 'B', redBit, red);
            arduino.McpSetBit(vaddr, 'B', green1Bit, green1);
            arduino.McpSetBit(vaddr, 'B', green2Bit, green2);

            string label = input switch
            {
                "1" => "Allowed to pass (Rett) -- Green",
                "2" => "Allowed to pass (Avvik) -- Green + Green2",
                "3" => "Not allowed to pass -- Red",
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
            Console.Write("Choice (blank or Esc for board menu): ");

            string? input = ReadLineOrEsc();
            if (input == null) { Console.WriteLine(); return; }
            if (input is not ("1" or "2"))
            {
                Console.WriteLine("  Unknown choice.");
                continue;
            }

            bool on = input == "1";
            arduino.McpSetBit(vaddr, led.Port, led.Bit, on);
            Console.WriteLine($"  Set: {(on ? "On" : "Off")}");
        }
    }
}
