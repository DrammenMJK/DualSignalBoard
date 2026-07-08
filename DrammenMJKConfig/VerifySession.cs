namespace DrammenMJKConfig;

static class VerifySession
{
    public static void Run(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Verify Mode ===");
        Console.WriteLine("Checks that motors and Pens feedback switches still operate correctly.");
        Console.WriteLine("Uses stored configuration — nothing is saved.");
        arduino.Send('V');
        Thread.Sleep(200);
        Console.WriteLine();
        PrintMenu();

        while (true)
        {
            Console.Write("Verify> ");
            char cmd = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
            Console.WriteLine(cmd);

            switch (cmd)
            {
                case '1': VerifyMotorsAndSwitches(arduino); break;
                case 'Q':
                    arduino.Send('Q');
                    Console.WriteLine("Exiting verify mode.");
                    Console.WriteLine();
                    return;
                default:
                    PrintMenu();
                    break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Verify 1: Motors and Pens feedback switches
    // -------------------------------------------------------------------------
    static void VerifyMotorsAndSwitches(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Verify 1: Motors and Pens Switches ---");
        Console.WriteLine("For each configured Pens the Arduino will:");
        Console.WriteLine("  1. Read current position from feedback switches.");
        Console.WriteLine("  2. Move motor to the opposite position and confirm feedback changes.");
        Console.WriteLine("  3. Move back to original position and confirm feedback changes.");
        Console.WriteLine("  4. Report PASS or FAIL for each Pens.");
        Console.WriteLine();
        Console.WriteLine("The Dreieskive motor is skipped (no feedback switches).");
        Console.WriteLine();
        Console.WriteLine("  X — Emergency stop all motors");
        Console.WriteLine("  Q — Abort and return to verify menu");
        Console.WriteLine();
        Console.WriteLine("Press Enter to begin...");
        Console.ReadLine();

        arduino.Send('1');
        Relay(arduino);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static void PrintMenu()
    {
        Console.WriteLine("  1 — Verify motors and Pens feedback switches");
        Console.WriteLine("  Q — Exit verify mode");
        Console.WriteLine();
    }

    static void Relay(ArduinoConnection arduino)
    {
        Console.WriteLine("(Watching Arduino output.  X = emergency stop  Q = abort)");
        Console.WriteLine();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            char c = char.ToUpper(key.KeyChar);
            Console.WriteLine($"→ {c}");
            arduino.Send(c);

            if (c == 'Q' || key.Key == ConsoleKey.Escape)
                break;
        }
        Console.WriteLine();
    }
}
