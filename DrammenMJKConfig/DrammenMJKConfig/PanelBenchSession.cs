namespace DrammenMJKConfig;

// Bench test for a panel board on its own (no motor boards needed): all LEDs
// on/off, one LED at a time, and a live view of every switch input. Output
// and input pins come from the chip's IODIR, like the panel scans, so
// hardware.json must be uploaded first. Nothing is stored.
sealed class PanelBenchSession(IArduinoDevice arduino, PanelScanSession scan)
{
    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- Panel bench test (SVB) ---");
        if (PanelScanSession.PickPanelBoard() is not { } board) return;

        new Menu(
            [
                ('A', "All LEDs on, then off",                 () => AllLeds(board.VAddrs)),
                ('L', "One LED at a time (step through)",      () => StepLeds(board.VAddrs)),
                ('N', "Snake -- each LED 1s in turn, any key = stop", () => Snake(board.VAddrs)),
                ('S', "Switches -- live view, flip to see changes", () => LiveSwitches(board.VAddrs)),
            ],
            quitOption: ('Q', "Back")
        ).Run();
    }

    void AllLeds(IReadOnlyList<byte> vaddrs)
    {
        if (Outputs(vaddrs) is not { } leds) return;
        SetAll(leds, true);
        Console.WriteLine($"  {leds.Count} LED output(s) on. Any key = all off.");
        Console.ReadKey(intercept: true);
        SetAll(leds, false);
        Console.WriteLine("  All off.");
    }

    void StepLeds(IReadOnlyList<byte> vaddrs)
    {
        if (Outputs(vaddrs) is not { } leds) return;
        SetAll(leds, false);
        Console.WriteLine("  Space/Right = next   Left = previous   Esc = done");

        int i = 0;
        while (true)
        {
            var (v, port, bit) = leds[i];
            bool ok = arduino.McpSetBit(v, port, bit, PanelScanSession.LedLevel(lit: true));
            Console.WriteLine($"  [{i + 1}/{leds.Count}] {PanelScanSession.Pin(v, port, bit)} {(ok ? "lit" : "-- no response")}");

            var key = Console.ReadKey(intercept: true).Key;
            arduino.McpSetBit(v, port, bit, PanelScanSession.LedLevel(lit: false));
            if (key == ConsoleKey.Escape) break;
            i = key == ConsoleKey.LeftArrow ? (i + leds.Count - 1) % leds.Count : (i + 1) % leds.Count;
        }
        Console.WriteLine();
    }

    // Lights the next LED before turning off the current one, so the run never goes dark.
    void Snake(IReadOnlyList<byte> vaddrs)
    {
        if (Outputs(vaddrs) is not { } leds) return;
        SetAll(leds, false);
        Console.WriteLine("  Any key = stop.");

        int i = 0;
        arduino.McpSetBit(leds[0].VAddr, leds[0].Port, leds[0].Bit, PanelScanSession.LedLevel(lit: true));
        while (true)
        {
            var (v, port, bit) = leds[i];
            Console.WriteLine($"  [{i + 1}/{leds.Count}] {PanelScanSession.Pin(v, port, bit)}");
            for (int ms = 0; ms < 1000 && !Console.KeyAvailable; ms += 50) Thread.Sleep(50);
            if (Console.KeyAvailable) break;

            var next = leds[(i + 1) % leds.Count];
            arduino.McpSetBit(next.VAddr, next.Port, next.Bit, PanelScanSession.LedLevel(lit: true));
            arduino.McpSetBit(v, port, bit, PanelScanSession.LedLevel(lit: false));
            i = (i + 1) % leds.Count;
        }
        Console.ReadKey(intercept: true);
        SetAll(leds, false);
        Console.WriteLine();
    }

    void LiveSwitches(IReadOnlyList<byte> vaddrs)
    {
        if (scan.Pins(vaddrs, wantInput: true) is not { } inputs) return;
        if (inputs.Count == 0) { Console.WriteLine("  No input pins -- upload hardware.json first?"); return; }

        var inputMasks = InputMasks(inputs);
        Console.WriteLine("  New line on each change, * = changed. 1 = High, 0 = Low (pull-up: closed switch = 0). Any key = stop.");
        foreach (string line in FormatHeader(inputs)) Console.WriteLine("  " + line);

        Dictionary<(byte, char), int>? last = null;
        while (!Console.KeyAvailable)
        {
            var now = new Dictionary<(byte, char), int>();
            foreach (var key in inputMasks.Keys) now[key] = arduino.McpReadPort(key.Item1, key.Item2);
            if (now.Values.Any(v => v < 0)) { Console.WriteLine("  No response from the panel."); Thread.Sleep(50); continue; }

            if (last == null || now.Any(kv => ((kv.Value ^ last[kv.Key]) & inputMasks[kv.Key]) != 0))
                Console.WriteLine("  " + FormatInputs(inputs, now, last));
            if (last != null && ChangedNonInputPins(inputMasks, now, last) is { Count: > 0 } odd)
                Console.WriteLine($"  Changed on a pin set as OUTPUT: {string.Join(", ", odd)} -- switch wired to an LED pin, or hardware.json wrong?");
            last = now;
            Thread.Sleep(50);
        }
        Console.ReadKey(intercept: true);
        Console.WriteLine();
    }

    // Bits that are inputs, per (vaddr, port) that has any.
    static Dictionary<(byte, char), int> InputMasks(IEnumerable<(byte VAddr, char Port, int Bit)> inputs) =>
        inputs.GroupBy(p => (p.VAddr, p.Port)).ToDictionary(g => g.Key, g => g.Aggregate(0, (m, p) => m | (1 << p.Bit)));

    // Pins outside the input masks whose read level changed between two reads
    // -- an output pin that something external pulls, e.g. a miswired switch.
    internal static List<string> ChangedNonInputPins(IReadOnlyDictionary<(byte, char), int> inputMasks,
        IReadOnlyDictionary<(byte, char), int> now, IReadOnlyDictionary<(byte, char), int> previous) =>
        now.SelectMany(kv => Enumerable.Range(0, 8)
                .Where(b => ((((kv.Value ^ previous[kv.Key]) & ~inputMasks[kv.Key]) >> b) & 1) == 1)
                .Select(b => PanelScanSession.Pin(kv.Key.Item1, kv.Key.Item2, b)))
            .ToList();

    const int ColumnWidth = 5;

    // Two header lines over the level rows: the chip's hex address over its
    // first column, then the port+bit of every column.
    internal static string[] FormatHeader(IEnumerable<(byte VAddr, char Port, int Bit)> inputs) =>
    [
        Columns(inputs, (p, first) => first ? $"0x{p.VAddr:X2}" : ""),
        Columns(inputs, (p, _) => $"{p.Port}{p.Bit}"),
    ];

    // One row with every input's level under its header column. A pin whose
    // level differs from `previous` gets a *.
    internal static string FormatInputs(IEnumerable<(byte VAddr, char Port, int Bit)> inputs,
        IReadOnlyDictionary<(byte, char), int> now, IReadOnlyDictionary<(byte, char), int>? previous) =>
        Columns(inputs, (p, _) =>
        {
            int level = (now[(p.VAddr, p.Port)] >> p.Bit) & 1;
            bool changed = previous != null && ((previous[(p.VAddr, p.Port)] >> p.Bit) & 1) != level;
            return $"{level}{(changed ? "*" : "")}";
        });

    // One left-aligned cell per input, chips separated by "| ". `cell` gets
    // the pin and whether it is the chip's first column.
    static string Columns(IEnumerable<(byte VAddr, char Port, int Bit)> inputs,
        Func<(byte VAddr, char Port, int Bit), bool, string> cell) =>
        string.Join("| ", inputs.GroupBy(p => p.VAddr).Select(chip =>
            string.Concat(chip.Select((p, i) => cell(p, i == 0).PadRight(ColumnWidth))))).TrimEnd();

    List<(byte VAddr, char Port, int Bit)>? Outputs(IReadOnlyList<byte> vaddrs)
    {
        var leds = scan.Pins(vaddrs, wantInput: false);
        if (leds is { Count: 0 }) Console.WriteLine("  No output pins -- upload hardware.json first?");
        return leds is { Count: > 0 } ? leds : null;
    }

    void SetAll(List<(byte VAddr, char Port, int Bit)> leds, bool lit)
    {
        foreach (var (v, port, bit) in leds) arduino.McpSetBit(v, port, bit, PanelScanSession.LedLevel(lit));
    }
}
