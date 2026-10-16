using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// Command D: run the site's one dreieskive each way and have the operator say
// which way is clockwise. The motor has no feedback switches, so direction
// can't be detected -- only observed. Result goes to EEPROM
// (AddrDreieskiveCwPolarity) and SystemConfig.json (dreieskive.cwPolarity):
//   0 = base pin high (upper low) turns it clockwise
//   1 = upper pin high (base low) turns it clockwise
sealed class DreieskiveSession(IArduinoDevice arduino)
{
    // Safety net: the motor never stops on its own, so don't let a missed
    // keypress leave it running.
    const int MaxRunMs = 15000;

    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- Dreieskive test — find which way is clockwise ---");

        var scb = BoardConfig.Svb.Scbs.FirstOrDefault(b => b.DreieskivePins != null);
        if (scb?.DreieskivePins is not { } pins)
        {
            Console.WriteLine("No board in boards.json declares a dreieskive.");
            Console.WriteLine();
            return;
        }
        byte vaddr = scb.VirtualAddress;
        var file = SystemConfigSession.LoadOrNew();
        Console.WriteLine($"{scb.Name} (0x{vaddr:X2}), port B bits {pins.Base}/{pins.Upper}. " +
            $"Current cwPolarity: {(file.Dreieskive.CwPolarity is { } cw ? cw.ToString() : "not set")}.");

        if (!Stop(vaddr, pins))
        {
            Console.WriteLine($"No response from 0x{vaddr:X2} — is the board connected and hardware.json uploaded?");
            Console.WriteLine();
            return;
        }

        try
        {
            bool? basePinIsCw = AskFirstDirection(vaddr, pins);
            if (basePinIsCw == null) { Console.WriteLine("Aborted — nothing saved."); Console.WriteLine(); return; }

            if (!ConfirmOpposite(vaddr, pins, expectCw: !basePinIsCw.Value))
            {
                Console.WriteLine("Direction didn't reverse — check the motor wiring/driver. Nothing saved.");
                Console.WriteLine();
                return;
            }

            byte cwPolarity = (byte)(basePinIsCw.Value ? 0 : 1);
            arduino.EepromWrite(ArduinoDevice.AddrDreieskiveVAddr, vaddr);
            arduino.EepromWrite(ArduinoDevice.AddrDreieskiveMotorPinBase, (byte)pins.Base);
            arduino.EepromWrite(ArduinoDevice.AddrDreieskiveCwPolarity, cwPolarity);
            file.Dreieskive = new DreieskiveEntry { MotorVAddr = $"0x{vaddr:X2}", MotorPinBase = pins.Base, CwPolarity = cwPolarity };
            SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
            Console.WriteLine($"Saved: clockwise = bit {(cwPolarity == 0 ? pins.Base : pins.Upper)} on (cwPolarity {cwPolarity}).");
            Console.WriteLine();
        }
        finally
        {
            Stop(vaddr, pins);
        }
    }

    // Runs with the base pin high until the operator has seen it turn, then
    // asks which way that was. true = clockwise, false = counter-clockwise,
    // null = Esc.
    bool? AskFirstDirection(byte vaddr, (int Base, int Upper) pins)
    {
        while (true)
        {
            Console.Write("Watch the dreieskive. Press any key to start it (Esc = back): ");
            if (ReadKey() == EscKey) { Console.WriteLine("[Esc]"); return null; }
            Console.WriteLine();
            RunUntilKey(vaddr, pins, driveBase: true);

            Console.Write("Which way did it turn?  C = clockwise  W = counter-clockwise  R = run again  Esc = abort: ");
            char c = char.ToUpper(ReadChar(ch => ch == EscKey || "CWR".Contains(char.ToUpper(ch))));
            Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
            if (c == EscKey) return null;
            if (c == 'C') return true;
            if (c == 'W') return false;
        }
    }

    // Runs the other way so the operator can confirm it really reverses.
    bool ConfirmOpposite(byte vaddr, (int Base, int Upper) pins, bool expectCw)
    {
        string expected = expectCw ? "clockwise" : "counter-clockwise";
        while (true)
        {
            Console.Write($"Now the opposite way — it should turn {expected}. Press any key to start: ");
            ReadKey();
            Console.WriteLine();
            RunUntilKey(vaddr, pins, driveBase: false);

            Console.Write($"Did it turn {expected}?  Y = yes  N = no  R = run again: ");
            char c = char.ToUpper(ReadChar(ch => "YNR".Contains(char.ToUpper(ch))));
            Console.WriteLine(c);
            if (c != 'R') return c == 'Y';
        }
    }

    void RunUntilKey(byte vaddr, (int Base, int Upper) pins, bool driveBase)
    {
        if (!Drive(vaddr, onBit: driveBase ? pins.Base : pins.Upper, offBit: driveBase ? pins.Upper : pins.Base))
        {
            Console.WriteLine("  Failed to start — no response.");
            return;
        }
        Console.Write($"  Running (bit {(driveBase ? pins.Base : pins.Upper)} on)... press any key to stop.");
        var deadline = DateTime.UtcNow.AddMilliseconds(MaxRunMs);
        while (!Console.KeyAvailable && DateTime.UtcNow < deadline) Thread.Sleep(20);
        bool timedOut = !Console.KeyAvailable;
        if (!timedOut) Console.ReadKey(intercept: true);
        Stop(vaddr, pins);
        Console.WriteLine(timedOut ? $" auto-stopped after {MaxRunMs / 1000}s." : " stopped.");
    }

    // Drops offBit before raising onBit so both are never high together.
    internal bool Drive(byte vaddr, int onBit, int offBit) =>
        arduino.McpSetBit(vaddr, 'B', offBit, false) &&
        arduino.McpSetBit(vaddr, 'B', onBit, true);

    // Both pins low = stopped.
    internal bool Stop(byte vaddr, (int Base, int Upper) pins) =>
        arduino.McpSetBit(vaddr, 'B', pins.Base, false) &
        arduino.McpSetBit(vaddr, 'B', pins.Upper, false);
}
