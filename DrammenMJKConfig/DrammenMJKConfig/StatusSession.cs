namespace DrammenMJKConfig;

// Read-only "what's actually in EEPROM right now" view -- see PLAN_Phase2.md
// "Visibility & Making It Work", part (a). Reuses HWD/SCD (same as
// HardwareConfigSession/SystemConfigSession downloads) but only prints;
// never touches hardware.json/SystemConfig.json.
static class StatusSession
{
    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Status: EEPROM summary ===");

        int chipCount = PrintBoardHardwareTable(arduino);
        var (switchCount, totalSwitches) = PrintSystemConfig(arduino);
        PrintMode(chipCount, switchCount, totalSwitches);

        Console.WriteLine();
    }

    // No real firmware-tracked mode exists yet -- PLAN_Phase1.md's device
    // state machine (Uninitialized -> Configuration -> Normal -> Fading) is a
    // design note only, not implemented (no MODE byte, no command reports it).
    // This is a best-effort read derived from EEPROM contents, not a live
    // firmware state -- until the real state machine gets built.
    static void PrintMode(int chipCount, int switchCount, int totalSwitches)
    {
        Console.WriteLine();
        Console.WriteLine("--- Mode (inferred, not firmware-tracked) ---");

        string mode = chipCount == 0
            ? "UNINITIALIZED — no hardware.json uploaded yet"
            : switchCount < totalSwitches
                ? "CONFIGURATION — hardware set, switches incomplete"
                : "NORMAL — hardware and all switches configured";
        Console.WriteLine($"  {mode}");
    }

    static int PrintBoardHardwareTable(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Board hardware table ---");

        var rows = new List<(byte VAddr, byte IodirA, byte IodirB, byte GppuA, byte GppuB)>();
        arduino.HardwareConfigDownloadStart();
        try
        {
            while (true)
            {
                string? line = arduino.GetNextHardwareLine(2000);
                if (line == null) { Console.WriteLine("  Timeout waiting for device."); return 0; }
                if (line == "END") break;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5) continue;
                rows.Add((
                    Convert.ToByte(parts[0], 16),
                    Convert.ToByte(parts[1], 16),
                    Convert.ToByte(parts[2], 16),
                    Convert.ToByte(parts[3], 16),
                    Convert.ToByte(parts[4], 16)));
            }
        }
        finally { arduino.HardwareConfigDownloadFinish(); }

        if (rows.Count == 0)
        {
            Console.WriteLine("  No boards configured -- upload hardware.json first.");
            return 0;
        }

        // Live presence check -- EEPROM says this chip is configured, but is
        // it actually on the bus right now? Same MR-based technique Bench
        // Test's Probe/Sweep use (a failed I2C read means no ACK).
        foreach (var r in rows)
        {
            bool present = arduino.McpReadPort(r.VAddr, 'A') >= 0;
            string marker = present ? "" : "  [NOT FOUND ON BUS]";
            Console.WriteLine($"  0x{r.VAddr:X2}: IODIR A={r.IodirA:X2} B={r.IodirB:X2}  GPPU A={r.GppuA:X2} B={r.GppuB:X2}{marker}");
        }

        return rows.Count;
    }

    static (int SwitchCount, int TotalSwitches) PrintSystemConfig(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Switches / dreieskive / status LED / fade config ---");

        var allLabels = BoardConfig.AllSwitchSlots().Select(s => s.Label).ToList();

        var lines = new List<string>();
        arduino.SystemConfigDownloadStart();
        try
        {
            while (true)
            {
                string? line = arduino.GetNextSystemConfigLine(2000);
                if (line == null) { Console.WriteLine("  Timeout waiting for device."); return (0, allLabels.Count); }
                if (line == "END") break;
                lines.Add(line);
            }
        }
        finally { arduino.SystemConfigDownloadFinish(); }

        var file = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(file, lines);

        Console.WriteLine($"  Switches: {file.Switches.Count}/{allLabels.Count} configured");
        SwitchTable.Print(arduino);

        Console.WriteLine(file.Dreieskive.MotorVAddr != null
            ? $"  Dreieskive: motor={file.Dreieskive.MotorVAddr} pinBase={file.Dreieskive.MotorPinBase}" +
              $" cwPol={(file.Dreieskive.CwPolarity is { } cw ? cw.ToString() : "?")}"
            : "  Dreieskive: not configured");

        if (file.StatusLeds.Count == 0)
            Console.WriteLine("  Status LED: not configured");
        else
            foreach (var (boardName, led) in file.StatusLeds)
                Console.WriteLine($"  Status LED ({boardName}): vaddr={led.VAddr} bit={led.Bit}");

        return (file.Switches.Count, allLabels.Count);
    }
}
