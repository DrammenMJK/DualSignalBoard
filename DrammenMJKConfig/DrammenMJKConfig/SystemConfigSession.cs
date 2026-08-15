namespace DrammenMJKConfig;

// Upload/download SystemConfig.json <-> the switch/SVB-switch/dreieskive/
// status-LED/fade-config EEPROM regions. EEPROM stays authoritative for what
// firmware runs against; this file is a mirror, kept in sync automatically by
// MotorScan (SaveSwitch/SaveDreieskiveAndLed) and explicitly via this session's
// Upload/Download actions.
static class SystemConfigSession
{
    public const string DefaultPath = "SystemConfig.json";

    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- System config (SystemConfig.json) ---");
        Console.WriteLine("Discovered switch wiring, dreieskive, status LED, fade config.");

        new Menu(
            [
                ('U', "Upload SystemConfig.json to Arduino", () => Upload(arduino)),
                ('D', "Download from Arduino to SystemConfig.json", () => Download(arduino)),
            ],
            quitOption: ('Q', "Back")
        ).Run();
    }

    public static SystemConfigFile LoadOrNew(string path = DefaultPath) =>
        File.Exists(path)
            ? SystemConfigJson.Load(path)
            : new SystemConfigFile { Site = "Fossli", Svb = BoardConfig.Svb.Name };

    // Called by MotorScan after each slot is confirmed -- an aborted scan still
    // leaves a file that matches whatever EEPROM actually has.
    public static void SaveSwitch(SystemConfigFile file, string label, SwitchEntry entry, string path = DefaultPath)
    {
        file.Switches[label] = entry;
        SystemConfigJson.Save(file, path);
    }

    // Called once at the start of a Motor Scan session -- declared, not scanned.
    public static void SaveDreieskiveAndLed(SystemConfigFile file, DreieskiveEntry drei, StatusLedEntry led, string path = DefaultPath)
    {
        file.Dreieskive = drei;
        file.StatusLed = led;
        SystemConfigJson.Save(file, path);
    }

    static string PromptPath()
    {
        Console.Write($"Path [{DefaultPath}]: ");
        string? input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? DefaultPath : input;
    }

    static void Upload(ArduinoDevice arduino)
    {
        string path = PromptPath();
        if (!File.Exists(path))
        {
            Console.WriteLine($"File not found: {path}");
            Console.WriteLine();
            return;
        }

        SystemConfigFile file;
        try { file = SystemConfigJson.Load(path); }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse {path}: {ex.Message}");
            Console.WriteLine();
            return;
        }

        var lines = SystemConfigJson.BuildUploadLines(file, out int switchCount, out int svbSwitchCount);
        Console.WriteLine($"Parsed {switchCount} switch(es), {svbSwitchCount} SVB switch(es).");
        Console.Write("Uploading...");

        if (!arduino.SystemConfigUploadStart())
        {
            Console.WriteLine(" Arduino did not respond with READY. Aborting.");
            Console.WriteLine();
            return;
        }

        bool ok = true;
        foreach (string line in lines)
        {
            if (!arduino.SystemConfigSendLine(line))
            {
                Console.WriteLine($"\nError on line '{line}'. Aborting upload.");
                arduino.SystemConfigUploadAbort();
                ok = false;
                break;
            }
        }

        if (ok)
        {
            Console.WriteLine(arduino.SystemConfigUploadFinish()
                ? " Done. System config stored — no physical re-scan needed."
                : " Upload finished but device did not confirm storage.");
        }
        Console.WriteLine();
    }

    static void Download(ArduinoDevice arduino)
    {
        string path = PromptPath();
        SystemConfigFile file = LoadOrNew(path);

        var lines = new List<string>();
        arduino.SystemConfigDownloadStart();
        try
        {
            while (true)
            {
                string? line = arduino.GetNextSystemConfigLine(2000);
                if (line == null) { Console.WriteLine("Timeout waiting for device."); return; }
                if (line == "END") break;
                lines.Add(line);
            }
        }
        finally { arduino.SystemConfigDownloadFinish(); }

        SystemConfigJson.ApplyDownloadLines(file, lines);

        try
        {
            SystemConfigJson.Save(file, path);
            Console.WriteLine($"Downloaded {lines.Count} record(s). Saved to {path}");
        }
        catch (Exception ex) { Console.WriteLine($"Failed to save file: {ex.Message}"); }
        Console.WriteLine();
    }
}
