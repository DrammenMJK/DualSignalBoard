namespace DrammenMJKConfig;

// Live verification view of the configured panel LED table (Config -> 4,
// Panel LED scan's result): sorted by pens number (not scan order, which is
// just whatever order the output pins happened to be probed in) and grouped
// under the SCB each pens belongs to, with signal-lamp indicators last. Lets
// you sanity-check a batch of newly-scanned LEDs -- right board, right pens,
// does it actually light -- without re-running the scan. Complements
// PanelBenchSession (raw pins, no labels, no pens grouping) and
// PanelEditSession (labels, but only a static list, no live on/off).
// Reassigning what a LED means is still Config -> 6, not duplicated here.
sealed class PanelLedVerifySession(IArduinoDevice arduino, PanelScanSession scan)
{
    internal sealed record Row(int Number, string GroupLabel, string BoardName, PanelLedEntry Led);

    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- Panel LEDs (configured, by pens/board) ---");
        if (scan.ReadPanel() is not { } panel) return;
        if (panel.PanelLeds.Count == 0)
        {
            Console.WriteLine("  No panel LEDs configured -- run Config -> 4 (Panel LED scan) first.");
            Console.WriteLine();
            return;
        }

        var rows = Rows(panel.PanelLeds);
        Print(rows);

        new Menu(
            [
                ('T', "Show table (live on/off)", () => Print(Rows(panel.PanelLeds))),
                ('A', "All on",                    () => SetAll(rows, lit: true)),
                ('F', "All off",                   () => SetAll(rows, lit: false)),
                ('N', "Toggle LEDs by number (no Enter, Esc = stop)", () => ToggleLoop(rows)),
            ],
            quitOption: ('Q', "Back (Config -> 6 to reassign/remove a LED)")
        ).Run();
    }

    // Pens sort key: the leading digits of the label ("5/6" -> 5, "101" ->
    // 101), so the list reads 1,2,3,4,5/6,7,101,... "from 1 up" regardless of
    // boards.json declaration order. Signal-lamp entries (no pens number)
    // sort after every real pens.
    static int PensKey(string target) =>
        target == SystemConfigJson.SignalTarget
            ? int.MaxValue
            : int.TryParse(new string(target.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue - 1;

    static int RoleKey(string? role) => role switch
    {
        "rett" => 0, "avvik" => 1,
        "red" => 0, "green1" => 1, "green2" => 2,
        _ => 9,
    };

    static List<Row> Rows(IReadOnlyList<PanelLedEntry> leds)
    {
        var boardOf = BoardConfig.Svb.Scbs
            .SelectMany(s => s.Switches.Select(w => (w.Label, s.Name)))
            .ToDictionary(x => x.Label, x => x.Name);

        return leds
            .OrderBy(l => PensKey(l.Target ?? ""))
            .ThenBy(l => l.Target, StringComparer.Ordinal)
            .ThenBy(l => RoleKey(l.Role))
            .Select((l, i) => new Row(
                i + 1,
                l.Target == SystemConfigJson.SignalTarget ? "Signal" : $"Pens {l.Target}",
                l.Target == SystemConfigJson.SignalTarget ? "-" : boardOf.GetValueOrDefault(l.Target ?? "", "?"),
                l))
            .ToList();
    }

    void Print(List<Row> rows)
    {
        Console.WriteLine();
        string[] headers = ["#", "Group", "Board", "Role", "Pin", "State"];
        var cells = rows.Select(r => new[]
        {
            r.Number.ToString(),
            r.GroupLabel,
            r.BoardName,
            r.Led.Role ?? "?",
            r.Led.VAddr != null && r.Led.Port != null && r.Led.Bit != null
                ? $"{r.Led.VAddr} {r.Led.Port}{r.Led.Bit}" : "?",
            Level(r.Led),
        }).ToList();

        int[] widths = headers.Select((h, i) => Math.Max(h.Length, cells.Max(c => c[i].Length))).ToArray();
        void PrintRow(string[] cols) => Console.WriteLine(string.Join("  ", cols.Select((c, i) => c.PadRight(widths[i]))));

        PrintRow(headers);
        PrintRow(widths.Select(w => new string('-', w)).ToArray());
        foreach (var row in cells) PrintRow(row);
        Console.WriteLine();
    }

    // Live GPIO read, decoded through the same active-low convention
    // McpSetBit writes with (PanelScanSession.LedLevel) -- "ON" means lit,
    // not "pin high".
    string Level(PanelLedEntry led)
    {
        if (led.VAddr == null || led.Port == null || led.Bit == null) return "?";
        int gpio = arduino.McpReadPort(Convert.ToByte(led.VAddr, 16), led.Port[0]);
        if (gpio < 0) return "no response";
        bool lit = ((gpio >> led.Bit.Value) & 1) == 0; // active-low
        return lit ? "ON" : "off";
    }

    void SetAll(List<Row> rows, bool lit)
    {
        int ok = 0;
        foreach (var r in rows)
        {
            if (r.Led.VAddr == null || r.Led.Port == null || r.Led.Bit == null) continue;
            if (arduino.McpSetBit(Convert.ToByte(r.Led.VAddr, 16), r.Led.Port[0], r.Led.Bit.Value, PanelScanSession.LedLevel(lit)))
                ok++;
        }
        Console.WriteLine($"  {ok}/{rows.Count} LED(s) set {(lit ? "on" : "off")}.");
    }

    // Type a row number to toggle it -- no Enter. Digits build up a number
    // (so boards with >9 LEDs are still reachable) and auto-submit the
    // moment another digit couldn't possibly extend it into a still-valid
    // row number, or otherwise after a short idle pause -- that's what makes
    // single-digit rows (the common case) feel instant while still allowing
    // two-digit ones. Esc stops the whole loop, not just one entry.
    void ToggleLoop(List<Row> rows)
    {
        Console.WriteLine();
        Console.WriteLine("  Type a row number to toggle it (digits auto-submit, no Enter needed). Esc = stop.");
        while (Console.KeyAvailable) Console.ReadKey(intercept: true); // drop stale keys

        var digits = new System.Text.StringBuilder();

        void Commit()
        {
            string typed = digits.ToString();
            digits.Clear();
            Console.WriteLine();
            if (!int.TryParse(typed, out int n) || n < 1 || n > rows.Count)
            {
                Console.WriteLine($"  No row {typed}.");
                return;
            }

            var led = rows[n - 1].Led;
            if (led.VAddr == null || led.Port == null || led.Bit == null) { Console.WriteLine("  Incomplete entry."); return; }

            byte vaddr = Convert.ToByte(led.VAddr, 16);
            char port = led.Port[0];
            int gpio = arduino.McpReadPort(vaddr, port);
            if (gpio < 0) { Console.WriteLine("  No response from the board."); return; }

            bool currentlyLit = ((gpio >> led.Bit.Value) & 1) == 0;
            bool ok = arduino.McpSetBit(vaddr, port, led.Bit.Value, PanelScanSession.LedLevel(lit: !currentlyLit));
            Console.WriteLine(ok
                ? $"  #{n} ({rows[n - 1].GroupLabel} {led.Role}) -> {(!currentlyLit ? "ON" : "off")}."
                : "  I2C error -- no response.");
        }

        // True if appending any one more digit could still land at or below
        // the highest row number -- i.e. it's still worth waiting for more input.
        bool CouldExtend(string typed) => typed.Length > 0 && long.Parse(typed) * 10 <= rows.Count;

        while (true)
        {
            if (!Console.KeyAvailable)
            {
                if (digits.Length > 0)
                {
                    Thread.Sleep(350); // idle window for a second digit
                    if (!Console.KeyAvailable) { Commit(); continue; }
                }
                else Thread.Sleep(30);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                if (digits.Length > 0) Commit();
                Console.WriteLine();
                return;
            }
            if (char.IsDigit(key.KeyChar))
            {
                digits.Append(key.KeyChar);
                Console.Write(key.KeyChar);
                if (!CouldExtend(digits.ToString())) Commit();
            }
        }
    }
}
