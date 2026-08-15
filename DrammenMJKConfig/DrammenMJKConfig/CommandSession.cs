using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// Operate already-configured switches by their real-world number, e.g.
// "Fossli switch 1" -> R/A. Never touches port/bit/vaddr directly — resolves
// label -> slot via BoardConfig, then DriveSwitch/ReadSwitch (SW/SR) do all
// the port/bit/polarity resolution in firmware. See PLAN_Phase1.md, Command
// mode.
static class CommandSession
{
    const int TimeoutMs = 5000;

    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Command Mode ===");
        Console.WriteLine($"Operate configured switches for SVB {BoardConfig.Svb.Name}.");
        Console.WriteLine("Esc = back.");
        Console.WriteLine();

        var labels = BoardConfig.AllSwitchSlots().Select(s => s.Label).ToList();

        while (true)
        {
            Console.WriteLine("Switches: " + string.Join("  ", labels.Select((l, i) => $"{i + 1}={l}")));
            Console.Write("Select switch (or Esc): ");

            char choice = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && (ch - '1') < labels.Count));
            Console.WriteLine(choice == EscKey ? "[Esc]" : choice.ToString());
            if (choice == EscKey) { Console.WriteLine(); return; }

            string label = labels[choice - '1'];
            if (!BoardConfig.TryFindSlotByLabel(label, out int slot)) continue;

            if (arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + slot) == ArduinoDevice.Unset)
            {
                Console.WriteLine($"Switch {label} is not configured — run Motor scan first.");
                Console.WriteLine();
                continue;
            }

            Console.Write($"Drive {label} to:  R = Rett   A = Avvik: ");
            char pos = char.ToUpper(ReadChar(ch => char.ToUpper(ch) == 'R' || char.ToUpper(ch) == 'A'));
            Console.WriteLine(pos);

            var result = arduino.DriveSwitch(slot, pos, TimeoutMs);
            Console.WriteLine($"Result: {result}");
            Console.WriteLine();
        }
    }
}
