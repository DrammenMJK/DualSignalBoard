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

        PrintBoardHardwareTable(arduino);
        PrintSystemConfig(arduino);

        Console.WriteLine();
    }

    static void PrintBoardHardwareTable(ArduinoDevice arduino)
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
                if (line == null) { Console.WriteLine("  Timeout waiting for device."); return; }
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
            return;
        }

        foreach (var r in rows)
            Console.WriteLine($"  0x{r.VAddr:X2}: IODIR A={r.IodirA:X2} B={r.IodirB:X2}  GPPU A={r.GppuA:X2} B={r.GppuB:X2}");
    }

    static void PrintSystemConfig(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Switches / dreieskive / status LED / fade config ---");

        var lines = new List<string>();
        arduino.SystemConfigDownloadStart();
        try
        {
            while (true)
            {
                string? line = arduino.GetNextSystemConfigLine(2000);
                if (line == null) { Console.WriteLine("  Timeout waiting for device."); return; }
                if (line == "END") break;
                lines.Add(line);
            }
        }
        finally { arduino.SystemConfigDownloadFinish(); }

        var file = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(file, lines);

        var allLabels = BoardConfig.AllSwitchSlots().Select(s => s.Label).ToList();
        Console.WriteLine($"  Switches: {file.Switches.Count}/{allLabels.Count} configured");
        foreach (string label in allLabels)
        {
            if (file.Switches.TryGetValue(label, out var sw))
                Console.WriteLine($"    {label}: motor={sw.MotorVAddr} bit={sw.MotorBit} pol={sw.Polarity}" +
                                   $"  fb Rett={sw.FeedbackRettBit} Avvik={sw.FeedbackAvvikBit}");
            else
                Console.WriteLine($"    {label}: not scanned");
        }

        Console.WriteLine(file.Dreieskive.MotorVAddr != null
            ? $"  Dreieskive: motor={file.Dreieskive.MotorVAddr} pinBase={file.Dreieskive.MotorPinBase}" +
              $" cwPol={(file.Dreieskive.CwPolarity is { } cw ? cw.ToString() : "?")}"
            : "  Dreieskive: not configured");

        Console.WriteLine(file.StatusLed.VAddr != null
            ? $"  Status LED: vaddr={file.StatusLed.VAddr} bit={file.StatusLed.Bit}"
            : "  Status LED: not configured");

        // Fade config always has firmware-side defaults, but an EEPROM never
        // written to reads as 0xFF per byte -- distinguish that from a real value.
        bool fadeUnset = file.FadeConfig.FadeMs == 0xFFFF && file.FadeConfig.PwmPeriodUs == 0xFFFF;
        Console.WriteLine(fadeUnset
            ? "  Fade config: not set (EEPROM blank)"
            : $"  Fade config: {file.FadeConfig.FadeMs}ms / {file.FadeConfig.FadeSteps} steps / {file.FadeConfig.PwmPeriodUs}us PWM period");
    }
}
