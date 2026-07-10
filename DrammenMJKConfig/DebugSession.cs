namespace DrammenMJKConfig;

static class DebugSession
{
    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Debug Mode ===");

        new Menu(
            [
                ('A', "All LEDs on",        () => { for (int i = 0; i < arduino.NumLedOutputs; i++) arduino.SetLed(i, true); Console.WriteLine("All LEDs on."); }),
                ('O', "All LEDs off",       () => { arduino.AllLedsOff(); Console.WriteLine("All LEDs off."); }),
                ('L', "LED snake",          () => RunLedSnake(arduino)),
                ('I', "Light LED by index", () => LightLedByIndex(arduino)),
            ],
            quitOption: ('Q', "Back")
        ).Run();

        arduino.AllLedsOff();
        Console.WriteLine();
    }

    private static void LightLedByIndex(ArduinoDevice arduino)
    {
        Console.WriteLine($"Press 0–9 to light that LED (max index {arduino.NumLedOutputs - 1}), O = all off, Esc = back.");
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return; }

            char k = char.ToUpper(key.KeyChar);
            if (k == 'O')
            {
                arduino.AllLedsOff();
                Console.WriteLine("O  — all off.");
                continue;
            }
            if (k >= '0' && k <= '9')
            {
                int idx = k - '0';
                if (idx < arduino.NumLedOutputs)
                {
                    arduino.SetLed(idx, true);
                    Console.WriteLine($"{k}  — LED {idx} on.");
                }
                else
                {
                    Console.WriteLine($"{k}  — out of range (max {arduino.NumLedOutputs - 1}).");
                }
            }
        }
    }

    private static void RunLedSnake(ArduinoDevice arduino)
    {
        Console.WriteLine("LED snake running... press any key to stop.");
        arduino.AllLedsOff();
        int idx = 0;
        int direction = 1;
        int numLeds = arduino.NumLedOutputs;

        while (!Console.KeyAvailable)
        {
            arduino.SetLed(idx, true);
            Thread.Sleep(80);
            arduino.SetLed(idx, false);

            idx += direction;
            if (idx >= numLeds) { idx = numLeds - 2; direction = -1; }
            if (idx < 0)        { idx = 1;            direction =  1; }
        }

        Console.ReadKey(intercept: true);
        arduino.AllLedsOff();
        Console.WriteLine("Snake stopped.");
    }
}
