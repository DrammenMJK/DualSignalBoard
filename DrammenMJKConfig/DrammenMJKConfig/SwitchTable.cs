namespace DrammenMJKConfig;

// Console-rendered, column-aligned view of every switch's current EEPROM
// config -- read fresh from the device in one SCD dump (EEPROM is
// authoritative), not from SystemConfig.json, which is only a mirror and
// could be stale.
sealed class SwitchTable(IArduinoDevice arduino)
{
    public void Print()
    {
        var lines = arduino.SystemConfigDownload();
        if (lines == null) { Console.WriteLine("  Timeout reading the switch table from the Arduino."); return; }
        var file = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(file, lines);

        var rows = BoardConfig.AllSwitchSlots()
            .Select(s => (s.Label, Entry: file.Switches.GetValueOrDefault(s.Label)))
            .ToList();

        // Live presence check per vaddr -- same MR-based technique Bench
        // Test's Probe/Sweep already use (a failed I2C read means no ACK,
        // i.e. nothing there). Cached because motor and feedback vaddr are
        // often the same chip (or repeat across switches), and each check
        // is a real serial round-trip.
        var presenceCache = new Dictionary<string, bool>();
        bool IsPresent(string vaddr) => presenceCache.TryGetValue(vaddr, out bool p)
            ? p
            : presenceCache[vaddr] = arduino.McpReadPort(Convert.ToByte(vaddr, 16), 'A') >= 0;
        string FormatVAddr(string? vaddr) => vaddr == null ? "?" : IsPresent(vaddr) ? vaddr : $"{vaddr} [MISSING]";

        string[] headers = ["Label", "Motor VAddr", "Bit", "Pol", "Feedback VAddr", "Rett", "Avvik"];
        var cells = rows.Select(r => r.Entry is { } e
            ? new[]
              {
                  r.Label,
                  FormatVAddr(e.MotorVAddr),
                  e.MotorBit.ToString()!,
                  e.Polarity.ToString()!,
                  FormatVAddr(e.FeedbackVAddr),
                  e.FeedbackRettBit.ToString()!,
                  e.FeedbackAvvikBit.ToString()!,
              }
            : [r.Label, "-", "-", "-", "-", "-", "-"])
            .ToList();

        int[] widths = headers
            .Select((h, i) => Math.Max(h.Length, cells.Count == 0 ? 0 : cells.Max(l => l[i].Length)))
            .ToArray();

        void PrintRow(string[] row) =>
            Console.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));

        Console.WriteLine();
        PrintRow(headers);
        PrintRow(widths.Select(w => new string('-', w)).ToArray());
        foreach (var row in cells) PrintRow(row);
        Console.WriteLine();

        PrintDuplicateWarnings(rows);
    }

    // Two switches scanned onto the exact same (motor vaddr, motor bit) --
    // e.g. stale EEPROM left behind when a prior scan's "0 = skip" didn't
    // clear the old slot -- silently drives them identically; firing either
    // one moves the other's motor. Surface it here instead of only finding
    // out by watching the wrong motor move.
    static void PrintDuplicateWarnings(List<(string Label, SwitchEntry? Entry)> rows)
    {
        var dupes = rows
            .Where(r => r.Entry != null)
            .GroupBy(r => (r.Entry!.MotorVAddr, r.Entry.MotorBit))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var g in dupes)
        {
            string labels = string.Join(", ", g.Select(r => r.Label));
            Console.WriteLine($"  WARNING: {labels} all share motor vaddr {g.Key.MotorVAddr} bit {g.Key.MotorBit} -- driving any one of them will move the same physical motor. Re-run Motor scan (Overwrite) for this board.");
        }
        if (dupes.Count > 0) Console.WriteLine();
    }
}
