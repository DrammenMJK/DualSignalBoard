namespace DrammenMJKConfig;

// Upload/download hardware.json <-> the board hardware table. Run before
// Motor Scan -- Motor Scan assumes ports are already set up (Port B output,
// Port A input+pullup on the board being scanned).
static class HardwareConfigSession
{
    const string DefaultPath = "hardware.json";

    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Hardware config (hardware.json) ---");
        Console.WriteLine("Board port direction/pull-up facts -- declared, not scanned.");

        new Menu(
            [
                ('U', "Upload hardware.json to Arduino", () => Upload(arduino)),
                ('D', "Download from Arduino to hardware.json", () => Download(arduino)),
            ],
            quitOption: ('Q', "Back")
        ).Run();
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

        HardwareConfigFile file;
        try { file = HardwareConfig.Load(path); }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse {path}: {ex.Message}");
            Console.WriteLine();
            return;
        }

        var rows = HardwareConfig.Flatten(file, out string? error);
        if (rows == null)
        {
            Console.WriteLine($"Cannot upload: {error}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"Parsed {rows.Count} chip(s) across {file.Boards.Count} board(s).");
        Console.Write("Uploading...");

        if (!arduino.HardwareConfigUploadStart())
        {
            Console.WriteLine(" Arduino did not respond with READY. Aborting.");
            Console.WriteLine();
            return;
        }

        bool ok = true;
        foreach (var row in rows)
        {
            string line = $"{row.VAddr:X2} {row.IodirA:X2} {row.IodirB:X2} {row.GppuA:X2} {row.GppuB:X2}";
            if (!arduino.HardwareConfigSendLine(line))
            {
                Console.WriteLine($"\nError uploading {row.BoardName} (0x{row.VAddr:X2}). Aborting.");
                arduino.HardwareConfigUploadAbort();
                ok = false;
                break;
            }
        }

        if (ok)
        {
            Console.WriteLine(arduino.HardwareConfigUploadFinish()
                ? " Done. Board hardware table stored and applied live."
                : " Upload finished but device did not confirm storage.");
        }
        Console.WriteLine();
    }

    static void Download(ArduinoDevice arduino)
    {
        string path = PromptPath();
        HardwareConfigFile file = File.Exists(path) ? HardwareConfig.Load(path) : new HardwareConfigFile();

        var rows = new List<(byte VAddr, byte IodirA, byte IodirB, byte GppuA, byte GppuB)>();
        arduino.HardwareConfigDownloadStart();
        try
        {
            while (true)
            {
                string? line = arduino.GetNextHardwareLine(2000);
                if (line == null) { Console.WriteLine("Timeout waiting for device."); return; }
                if (line == "END") break;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5) { Console.WriteLine($"Bad line: '{line}'"); continue; }
                rows.Add((
                    Convert.ToByte(parts[0], 16),
                    Convert.ToByte(parts[1], 16),
                    Convert.ToByte(parts[2], 16),
                    Convert.ToByte(parts[3], 16),
                    Convert.ToByte(parts[4], 16)));
            }
        }
        finally { arduino.HardwareConfigDownloadFinish(); }

        Console.WriteLine($"Downloaded {rows.Count} chip(s) from the device.");

        // Merge into existing boards by vaddr where possible, so names/categories
        // from the file survive a re-download of raw register values.
        foreach (var row in rows)
        {
            string vaddrHex = $"0x{row.VAddr:X2}";
            var chip = file.Boards.SelectMany(b => b.Chips).FirstOrDefault(c => c.VAddrByte == row.VAddr);
            if (chip == null)
            {
                var board = new HwBoard { Name = $"Unknown board {vaddrHex}", Category = "SCB" };
                chip = new HwChip { VAddr = vaddrHex };
                board.Chips.Add(chip);
                file.Boards.Add(board);
            }
            chip.PortA = DecodePortArray(row.IodirA, row.GppuA);
            chip.PortB = DecodePortArray(row.IodirB, row.GppuB);
        }

        try
        {
            HardwareConfig.Save(file, path);
            Console.WriteLine($"Saved to {path}");
        }
        catch (Exception ex) { Console.WriteLine($"Failed to save file: {ex.Message}"); }
        Console.WriteLine();
    }

    static string[] DecodePortArray(byte iodir, byte gppu)
    {
        var arr = new string[8];
        for (int bit = 0; bit < 8; bit++)
        {
            bool isInput = (iodir & (1 << bit)) != 0;
            bool hasPullup = (gppu & (1 << bit)) != 0;
            arr[bit] = !isInput ? "out" : hasPullup ? "in-pu" : "in";
        }
        return arr;
    }
}
