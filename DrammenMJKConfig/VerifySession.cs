namespace DrammenMJKConfig;

static class VerifySession
{
    private const int FeedbackTimeoutMs = 3000;

    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Verify Mode ===");
        Console.WriteLine("Tests each configured motor pair: fires both polarities, checks feedback.");
        Console.WriteLine("Esc = abort at any point.");
        Console.WriteLine();

        char first = arduino.FirstPens;
        char last  = arduino.LastPens;
        int  numPens  = arduino.NumPens;
        int  numPairs = arduino.NumPairs;
        int  drPair   = arduino.DreieskivePair;

        // Load motor config from EEPROM
        var motorPair = new byte[numPens];
        var motorPol  = new byte[numPens];
        var fbRett    = new byte[numPens];
        var fbAvvik   = new byte[numPens];
        var swPin     = new byte[numPens];

        for (int i = 0; i < numPens; i++)
        {
            motorPair[i] = arduino.EepromRead(ArduinoDevice.Region1Base + i);
            motorPol[i]  = arduino.EepromRead(ArduinoDevice.Region1Pol  + i);
            fbRett[i]    = arduino.EepromRead(ArduinoDevice.Region2Rett + i);
            fbAvvik[i]   = arduino.EepromRead(ArduinoDevice.Region2Avvik + i);
            swPin[i]     = arduino.EepromRead(ArduinoDevice.Region3Base  + i);
        }

        int passCount = 0;
        int failCount = 0;
        bool aborted  = false;

        // Verify each Pens
        for (int i = 0; i < numPens && !aborted; i++)
        {
            char pens = (char)(first + i);
            byte pair = motorPair[i];

            if (pair == ArduinoDevice.Unset)
            {
                Console.WriteLine($"  Pens {pens}: [SKIP] Not configured.");
                continue;
            }

            Console.WriteLine($"  Pens {pens} (pair {pair}):");

            // Fire polarity 0 → should reach Rett or Avvik depending on stored pol
            bool rettIsPol0 = motorPol[i] == 0;
            int  rettPol    = rettIsPol0 ? 0 : 1;
            int  avvikPol   = rettIsPol0 ? 1 : 0;

            // Drive to Rett position
            if (!arduino.MotorFire(pair, rettPol))
            {
                Console.WriteLine($"    FAIL: MotorFire pair={pair} pol={rettPol} failed.");
                failCount++; continue;
            }
            int hitPin = arduino.WaitFeedback(pair, FeedbackTimeoutMs);
            arduino.MotorStop();

            if (hitPin < 0)
            {
                Console.WriteLine($"    FAIL: Rett — no feedback within {FeedbackTimeoutMs} ms. Expected pin {fbRett[i]}.");
                failCount++;
            }
            else if ((byte)hitPin != fbRett[i])
            {
                Console.WriteLine($"    FAIL: Rett — got pin {hitPin}, expected {fbRett[i]}.");
                failCount++;
            }
            else
            {
                Console.WriteLine($"    OK  : Rett — pin {hitPin} as expected.");
                passCount++;
            }

            if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
            { aborted = true; break; }

            Thread.Sleep(300);

            // Drive to Avvik position
            if (!arduino.MotorFire(pair, avvikPol))
            {
                Console.WriteLine($"    FAIL: MotorFire pair={pair} pol={avvikPol} failed.");
                failCount++; continue;
            }
            hitPin = arduino.WaitFeedback(pair, FeedbackTimeoutMs);
            arduino.MotorStop();

            if (hitPin < 0)
            {
                Console.WriteLine($"    FAIL: Avvik — no feedback within {FeedbackTimeoutMs} ms. Expected pin {fbAvvik[i]}.");
                failCount++;
            }
            else if ((byte)hitPin != fbAvvik[i])
            {
                Console.WriteLine($"    FAIL: Avvik — got pin {hitPin}, expected {fbAvvik[i]}.");
                failCount++;
            }
            else
            {
                Console.WriteLine($"    OK  : Avvik — pin {hitPin} as expected.");
                passCount++;
            }

            if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
            { aborted = true; break; }

            // Drive back to Rett (leave in known state)
            arduino.MotorFire(pair, rettPol);
            Thread.Sleep(1500);
            arduino.MotorStop();
            Thread.Sleep(200);
        }

        // Verify Dreieskive switch (just check pins respond — no motor feedback on Dreieskive)
        if (!aborted)
        {
            byte cwPin  = arduino.EepromRead(ArduinoDevice.Region5SwCw);
            byte ccwPin = arduino.EepromRead(ArduinoDevice.Region5SwCcw);
            byte moment = arduino.EepromRead(ArduinoDevice.Region5Moment);

            Console.WriteLine();
            Console.WriteLine("  Dreieskive switch pins:");
            PrintPinStatus("CW switch",    cwPin);
            PrintPinStatus("CCW switch",   ccwPin);
            PrintPinStatus("Moment button", moment);
        }

        Console.WriteLine();
        if (aborted) Console.WriteLine("Verify ABORTED.");
        else         Console.WriteLine($"Verify complete — {passCount} pass, {failCount} fail.");
        Console.WriteLine();
    }

    private static void PrintPinStatus(string name, byte pin)
    {
        if (pin == ArduinoDevice.Unset)
            Console.WriteLine($"    {name}: [not configured]");
        else
            Console.WriteLine($"    {name}: pin {pin}");
    }
}
