namespace DrammenMJKConfig;

// Raw MCP23017 bring-up/bench tools -- probe a chip, force a port direction,
// toggle a single output bit. Talks straight to the MDIR/MW/MR/MBIT commands
// via ArduinoDevice's Mcp* wrappers (the same primitives Motor Scan uses
// internally); nothing here touches EEPROM or hardware.json. Useful for
// checking new wiring before it's declared anywhere.
static class BenchTestSession
{
    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Bench test (raw MCP23017 access) ---");
        Console.WriteLine("For new/undeclared wiring. Direction is NOT set automatically --");
        Console.WriteLine("an output bit does nothing electrically until its port's IODIR bit is cleared.");

        new Menu(
            [
                ('P', "Probe a chip (is it on the bus?)",   () => Probe(arduino)),
                ('W', "Sweep 0x20-0x27 (all MCP23017 addrs)", () => SweepAddresses(arduino)),
                ('R', "Read a port",                       () => ReadPort(arduino)),
                ('C', "Continuous read (watch a port for changes)", () => WatchPort(arduino)),
                ('D', "Set a port's direction (raw mask)",  () => SetDirection(arduino)),
                ('S', "Set/clear a single output bit",      () => SetBit(arduino)),
                ('F', "Flip all bits on a port",            () => FlipPort(arduino)),
                ('T', "Generate traffic (for scope/logic analyzer)", () => GenerateTraffic(arduino)),
            ],
            quitOption: ('Q', "Back")
        ).Run();

        Console.WriteLine();
    }

    static bool TryPromptByte(string label, out byte value)
    {
        Console.Write($"{label} (hex, e.g. 20): ");
        string? input = Console.ReadLine()?.Trim();
        if (byte.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out value)) return true;
        Console.WriteLine("  Not a valid hex byte.");
        value = 0;
        return false;
    }

    static bool TryPromptPort(out char port)
    {
        Console.Write("Port (A/B): ");
        string? input = Console.ReadLine()?.Trim().ToUpperInvariant();
        if (input is "A" or "B") { port = input[0]; return true; }
        Console.WriteLine("  Must be A or B.");
        port = 'A';
        return false;
    }

    static bool TryPromptBit(out int bit)
    {
        Console.Write("Bit (0-7): ");
        string? input = Console.ReadLine()?.Trim();
        if (int.TryParse(input, out bit) && bit is >= 0 and <= 7) return true;
        Console.WriteLine("  Must be 0-7.");
        bit = 0;
        return false;
    }

    static void Probe(ArduinoDevice arduino)
    {
        Console.WriteLine();
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;

        int a = arduino.McpReadPort(vaddr, 'A');
        if (a < 0)
        {
            Console.WriteLine($"  0x{vaddr:X2}: no response (not on the bus, or bus/vaddr wrong).");
            return;
        }
        int b = arduino.McpReadPort(vaddr, 'B');
        Console.WriteLine($"  0x{vaddr:X2}: found. GPIOA=0x{a:X2}" + (b >= 0 ? $" GPIOB=0x{b:X2}" : " GPIOB=?"));
        Console.WriteLine();
    }

    static void SweepAddresses(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("Sweeping 0x20-0x27 (every address an MCP23017 can be strapped to)...");

        var found = new List<byte>();
        for (int addr = 0x20; addr <= 0x27; addr++)
        {
            int v = arduino.McpReadPort((byte)addr, 'A');
            bool ok = v >= 0;
            Console.WriteLine(ok ? $"  0x{addr:X2}: FOUND (GPIOA=0x{v:X2})" : $"  0x{addr:X2}: no response");
            if (ok) found.Add((byte)addr);
        }

        Console.WriteLine();
        Console.WriteLine(found.Count == 0
            ? "  Nothing responded on any address."
            : $"  Found: {string.Join(", ", found.Select(a => $"0x{a:X2}"))}");
        Console.WriteLine();
    }

    static void GenerateTraffic(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("Every attempt clocks out address+R/W on SCL/SDA regardless of whether");
        Console.WriteLine("anything ACKs -- point a scope/logic analyzer at the bus before starting.");
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;
        Console.Write("Duration in seconds [5]: ");
        string? input = Console.ReadLine()?.Trim();
        int seconds = int.TryParse(input, out int s) && s > 0 ? s : 5;

        Console.WriteLine($"Hammering 0x{vaddr:X2} for {seconds}s. Press any key to stop early.");

        int attempts = 0, successes = 0;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !Console.KeyAvailable)
        {
            attempts++;
            if (arduino.McpReadPort(vaddr, 'A') >= 0) successes++;
        }
        if (Console.KeyAvailable) Console.ReadKey(intercept: true);

        Console.WriteLine($"  {attempts} attempt(s), {successes} succeeded.");
        Console.WriteLine();
    }

    static void FlipPort(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("Inverts the whole port's output latch once (0xFF<->0x00 per bit) --");
        Console.WriteLine("only bits actually configured as outputs will visibly change.");
        Console.WriteLine("Stays flipped until you run this again.");
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;
        if (!TryPromptPort(out char port)) return;

        int current = arduino.McpReadPort(vaddr, port);
        if (current < 0) { Console.WriteLine("  No response."); return; }

        byte next = (byte)~current;
        Console.WriteLine(arduino.McpWritePort(vaddr, port, next)
            ? $"  0x{vaddr:X2} port {port}: 0x{current:X2} -> 0x{next:X2}."
            : "  Write failed -- no response.");
        Console.WriteLine();
    }

    static void WatchPort(ArduinoDevice arduino)
    {
        Console.WriteLine();
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;
        if (!TryPromptPort(out char port)) return;
        Console.Write("Poll interval in ms [200]: ");
        string? input = Console.ReadLine()?.Trim();
        int intervalMs = int.TryParse(input, out int ms) && ms > 0 ? ms : 200;

        Console.WriteLine($"Watching 0x{vaddr:X2} port {port} every {intervalMs}ms -- prints only on change.");
        Console.WriteLine("Press any key to stop.");

        int last = -2; // never equals a real read (-1) or any byte value, forces first print
        while (!Console.KeyAvailable)
        {
            int v = arduino.McpReadPort(vaddr, port);
            if (v != last)
            {
                Console.WriteLine(v >= 0
                    ? $"  0x{vaddr:X2} port {port} = 0x{v:X2}  ({Convert.ToString(v, 2).PadLeft(8, '0')})"
                    : "  No response.");
                last = v;
            }
            Thread.Sleep(intervalMs);
        }
        if (Console.KeyAvailable) Console.ReadKey(intercept: true);

        Console.WriteLine();
    }

    static void ReadPort(ArduinoDevice arduino)
    {
        Console.WriteLine();
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;
        if (!TryPromptPort(out char port)) return;

        int v = arduino.McpReadPort(vaddr, port);
        if (v < 0) { Console.WriteLine("  No response."); return; }
        Console.WriteLine($"  0x{vaddr:X2} port {port} = 0x{v:X2}  ({Convert.ToString(v, 2).PadLeft(8, '0')})");
        Console.WriteLine();
    }

    static void SetDirection(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("Mask bit = 1 -> input, 0 -> output (matches MCP23017 IODIR directly).");
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;
        if (!TryPromptPort(out char port)) return;
        if (!TryPromptByte("IODIR mask", out byte mask)) return;

        Console.WriteLine(arduino.McpSetDirection(vaddr, port, mask)
            ? $"  Done. 0x{vaddr:X2} port {port} IODIR = 0x{mask:X2}."
            : "  Failed -- no response.");
        Console.WriteLine();
    }

    static void SetBit(ArduinoDevice arduino)
    {
        Console.WriteLine();
        if (!TryPromptByte("Virtual address", out byte vaddr)) return;
        if (!TryPromptPort(out char port)) return;
        if (!TryPromptBit(out int bit)) return;
        Console.Write("On or off (1/0): ");
        string? input = Console.ReadLine()?.Trim();
        if (input != "0" && input != "1") { Console.WriteLine("  Must be 1 or 0."); return; }
        bool on = input == "1";

        Console.WriteLine(arduino.McpSetBit(vaddr, port, bit, on)
            ? $"  Done. 0x{vaddr:X2} port {port} bit {bit} = {(on ? "1" : "0")}."
            : "  Failed -- no response.");
        Console.WriteLine();
    }
}
