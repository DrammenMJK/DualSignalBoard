using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// Operate already-configured switches: pick the board (SCB) first, then the
// switch number on that board, then R/A -- current position is read and
// shown before driving. Never touches port/bit/vaddr directly — resolves
// label -> slot via BoardConfig, then DriveSwitch/ReadSwitch (SW/SR) do all
// the port/bit/polarity resolution in firmware. See PLAN_Phase1.md, Command
// mode.
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
        Console.WriteLine($"Operate configured switches for SVB {BoardConfig.Svb.Name}.");
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
            Console.Write("Switch number (blank for board list): ");

            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) { Console.WriteLine(); return; }

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
}
