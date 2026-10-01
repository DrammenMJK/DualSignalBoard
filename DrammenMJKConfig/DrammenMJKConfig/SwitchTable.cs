namespace DrammenMJKConfig;

// Console-rendered, column-aligned view of every switch's current EEPROM
// config -- read fresh from the device (EEPROM is authoritative), not from
// SystemConfig.json, which is only a mirror and could be stale.
static class SwitchTable
{
    public static void Print(ArduinoDevice arduino)
    {
        var rows = BoardConfig.AllSwitchSlots()
            .Select(s => (s.Label, Data: SwitchSlotData.Read(arduino, s.Slot)))
            .ToList();

        // Live presence check per vaddr -- same MR-based technique Bench
        // Test's Probe/Sweep already use (a failed I2C read means no ACK,
        // i.e. nothing there). Cached because motor and feedback vaddr are
        // often the same chip (or repeat across switches), and each check
        // is a real serial round-trip.
        var presenceCache = new Dictionary<byte, bool>();
        bool IsPresent(byte vaddr) => presenceCache.TryGetValue(vaddr, out bool p)
            ? p
            : presenceCache[vaddr] = arduino.McpReadPort(vaddr, 'A') >= 0;
        string FormatVAddr(byte vaddr) => IsPresent(vaddr) ? $"0x{vaddr:X2}" : $"0x{vaddr:X2} [MISSING]";

        string[] headers = ["Label", "Motor VAddr", "Bit", "Pol", "Feedback VAddr", "Rett", "Avvik"];
        var lines = rows.Select(r => r.Data.IsConfigured
            ? new[]
              {
                  r.Label,
                  FormatVAddr(r.Data.MotorVAddr),
                  r.Data.MotorBit.ToString(),
                  r.Data.Polarity.ToString(),
                  FormatVAddr(r.Data.FeedbackVAddr),
                  r.Data.FeedbackRett.ToString(),
                  r.Data.FeedbackAvvik.ToString(),
              }
            : [r.Label, "-", "-", "-", "-", "-", "-"])
            .ToList();

        int[] widths = headers
            .Select((h, i) => Math.Max(h.Length, lines.Count == 0 ? 0 : lines.Max(l => l[i].Length)))
            .ToArray();

        void PrintRow(string[] cells) =>
            Console.WriteLine(string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))));

        Console.WriteLine();
        PrintRow(headers);
        PrintRow(widths.Select(w => new string('-', w)).ToArray());
        foreach (var line in lines) PrintRow(line);
        Console.WriteLine();

        PrintDuplicateWarnings(rows);
    }

    // Two switches scanned onto the exact same (motor vaddr, motor bit) --
    // e.g. stale EEPROM left behind when a prior scan's "0 = skip" didn't
    // clear the old slot -- silently drives them identically; firing either
    // one moves the other's motor. Surface it here instead of only finding
    // out by watching the wrong motor move.
    static void PrintDuplicateWarnings(List<(string Label, SwitchSlotData Data)> rows)
    {
        var dupes = rows
            .Where(r => r.Data.IsConfigured)
            .GroupBy(r => (r.Data.MotorVAddr, r.Data.MotorBit))
            .Where(g => g.Count() > 1);

        foreach (var g in dupes)
        {
            string labels = string.Join(", ", g.Select(r => r.Label));
            Console.WriteLine($"  WARNING: {labels} all share motor vaddr 0x{g.Key.MotorVAddr:X2} bit {g.Key.MotorBit} -- driving any one of them will move the same physical motor. Re-run Motor scan (Overwrite) for this board.");
        }
        if (dupes.Any()) Console.WriteLine();
    }
}
