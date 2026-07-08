namespace DrammenMJKConfig;

static class DebugSession
{
    public static void Run(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Debug Mode ===");
        arduino.Send('D');
        Thread.Sleep(200); // allow Arduino banner to arrive before our menu
        Console.WriteLine();
        Console.WriteLine("  A — Toggle all LEDs on / off  (checks for wiring shorts)");
        Console.WriteLine("  L — Loop all LEDs one at a time, 1 second each");
        Console.WriteLine("  Q — Exit debug mode");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Debug> ");
            char cmd = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
            Console.WriteLine(cmd);

            switch (cmd)
            {
                case 'A':
                    Console.WriteLine("Toggling all LEDs...");
                    arduino.Send('A');
                    break;

                case 'L':
                    Console.WriteLine("LED loop running — press any key to stop.");
                    arduino.Send('L');
                    Console.ReadKey(intercept: true);
                    arduino.Send('Q'); // tell Arduino to stop the loop
                    Thread.Sleep(100);
                    Console.WriteLine("\nLoop stopped.");
                    break;

                case 'Q':
                    arduino.Send('Q');
                    Console.WriteLine("Exiting debug mode.");
                    Console.WriteLine();
                    return;

                default:
                    Console.WriteLine("Unknown. A = all LEDs   L = LED loop   Q = exit");
                    break;
            }
        }
    }
}
