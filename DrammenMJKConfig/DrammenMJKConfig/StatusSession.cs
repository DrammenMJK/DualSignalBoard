namespace DrammenMJKConfig;

// Read-only "what's actually in EEPROM right now" view -- see PLAN_Phase2.md
// "Visibility & Making It Work", part (a). Reuses HWD/SCD (same as
// HardwareConfigSession/SystemConfigSession downloads) but only prints;
// never touches hardware.json/SystemConfig.json.
sealed class StatusSession(IArduinoDevice arduino, SwitchTable switchTable)
{
    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== Status: EEPROM summary ===");

        int chipCount = PrintBoardHardwareTable();
        var (switchCount, totalSwitches) = PrintSystemConfig();
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

    int PrintBoardHardwareTable()
    {
        Console.WriteLine();
        Console.WriteLine("--- Board hardware table ---");

        var rows = arduino.HardwareConfigDownload();
        if (rows == null) { Console.WriteLine("  Timeout or unreadable row from device."); return 0; }

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

    (int SwitchCount, int TotalSwitches) PrintSystemConfig()
    {
        Console.WriteLine();
        Console.WriteLine("--- Switches / dreieskive / status LED / fade config ---");

        var allLabels = BoardConfig.AllSwitchSlots().Select(s => s.Label).ToList();

        var lines = arduino.SystemConfigDownload();
        if (lines == null) { Console.WriteLine("  Timeout waiting for device."); return (0, allLabels.Count); }

        var file = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(file, lines);

        Console.WriteLine($"  Switches: {file.Switches.Count}/{allLabels.Count} configured");
        switchTable.Print();

        Console.WriteLine(file.Dreieskive.MotorVAddr != null
            ? $"  Dreieskive: motor={file.Dreieskive.MotorVAddr} pinBase={file.Dreieskive.MotorPinBase}" +
              $" cwPol={(file.Dreieskive.CwPolarity is { } cw ? cw.ToString() : "?")}"
            : "  Dreieskive: not configured");

        if (file.StatusLeds.Count == 0)
            Console.WriteLine("  Status LED: not configured");
        else
            foreach (var (boardName, led) in file.StatusLeds)
                Console.WriteLine($"  Status LED ({boardName}): vaddr={led.VAddr} bit={led.Bit}");

        Console.WriteLine($"  Panel (SVB): {file.SvbSwitches.Count} input(s) [{string.Join(", ", file.SvbSwitches.Keys)}], {file.PanelLeds.Count} LED(s)");

        return (file.Switches.Count, allLabels.Count);
    }
}
