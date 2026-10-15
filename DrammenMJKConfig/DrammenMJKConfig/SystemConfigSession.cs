namespace DrammenMJKConfig;

// Upload/download SystemConfig.json <-> the switch/SVB-switch/dreieskive/
// status-LED/fade-config EEPROM regions. EEPROM stays authoritative for what
// firmware runs against; this file is a mirror, kept in sync automatically by
// the scans and explicitly via this session's Upload/Download actions.
sealed class SystemConfigSession(IArduinoDevice arduino)
{
    public const string DefaultPath = "SystemConfig.json";

    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- System Config Backup (SystemConfig.json) ---");
        Console.WriteLine("Discovered switch wiring, dreieskive, status LED, fade config.");

        new Menu(
            [
                ('U', "Restore — upload SystemConfig.json to Arduino", Upload),
                ('D', "Backup — download from Arduino to SystemConfig.json", Download),
            ],
            quitOption: ('Q', "Back")
        ).Run();
    }

    public static SystemConfigFile LoadOrNew(string path = DefaultPath) =>
        File.Exists(path)
            ? SystemConfigJson.Load(path)
            : new SystemConfigFile { Site = "Fossli", Svb = BoardConfig.Svb.Name };

    static string PromptPath()
    {
        Console.Write($"Path [{DefaultPath}]: ");
        string? input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? DefaultPath : input;
    }

    void Upload()
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

        string? error = arduino.SystemConfigSend(lines);
        Console.WriteLine(error == null
            ? " Done. System config stored — no physical re-scan needed."
            : $" Failed: {error}.");
        Console.WriteLine();
    }

    // The board's tables replace the file's: SCD leaves out empty slots, so
    // merging into the old file would keep entries the board no longer has.
    void Download()
    {
        string path = PromptPath();
        SystemConfigFile old;
        try { old = LoadOrNew(path); }
        catch (Exception ex) { Console.WriteLine($"Failed to parse {path}: {ex.Message}"); Console.WriteLine(); return; }

        var lines = arduino.SystemConfigDownload();
        if (lines == null) { Console.WriteLine("Timeout waiting for device."); return; }

        var file = new SystemConfigFile { Todo = old.Todo, Site = old.Site, Svb = old.Svb };
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
