using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// SVB panel scans -- the panel side of a stillverk (Fossli SVB etc.):
//   LED scan:    lights every output pin of the panel board in turn; the
//                operator says which pens it belongs to (Rett/Avvik), or that
//                it's a signal lamp (Red/Green1/Green2), or unused.
//   Switch scan: the operator flips a panel switch / presses the button; the
//                changed input pin is detected and the operator says what it
//                controls (a pens, dreieskive CW/CCW, or the green request).
// Which pins are outputs/inputs is read live from the chip's IODIR, so
// hardware.json must have been uploaded for the panel board first. Results go
// to SystemConfig.json (panelLeds / svbSwitches) and to the Arduino's EEPROM.
// Storage only -- nothing drives the LEDs or acts on the inputs yet.
static class PanelScanSession
{
    const byte IodirA = 0x00, IodirB = 0x01;

    // Pens labels the panel can control -- every switch on this SVB's SCBs.
    // ponytail: single-key 1-9 menus, Fossli has 7 pens; letters if a panel ever has more.
    static List<string> PensLabels() =>
        BoardConfig.AllSwitchSlots().Select(s => s.Label).Take(9).ToList();

    static (string Name, IReadOnlyList<byte> VAddrs)? PickPanelBoard()
    {
        var boards = BoardConfig.PanelBoards;
        if (boards.Count == 0) { Console.WriteLine("  No SVB/Combined board in boards.json."); return null; }
        Console.WriteLine("Panel boards: " + string.Join("  ", boards.Select((b, i) =>
            $"{i + 1}={b.Name} ({string.Join(",", b.VAddrs.Select(v => $"0x{v:X2}"))})")));
        Console.Write("Select board (or Esc): ");
        char c = ReadChar(ch => ch == EscKey || (ch >= '1' && ch <= '9' && ch - '1' < boards.Count));
        Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
        return c == EscKey ? null : boards[c - '1'];
    }

    // Every (vaddr, port, bit) whose IODIR bit equals `wantInput`. Null if a
    // chip doesn't answer.
    static List<(byte VAddr, char Port, int Bit)>? Pins(ArduinoDevice arduino, IReadOnlyList<byte> vaddrs, bool wantInput)
    {
        var pins = new List<(byte, char, int)>();
        foreach (byte v in vaddrs)
        {
            foreach (char port in "AB")
            {
                int iodir = arduino.McpReadRegister(v, port == 'A' ? IodirA : IodirB);
                if (iodir < 0) { Console.WriteLine($"  0x{v:X2} doesn't answer -- board connected, hardware.json uploaded?"); return null; }
                for (int b = 0; b < 8; b++)
                    if ((((iodir >> b) & 1) == 1) == wantInput) pins.Add((v, port, b));
            }
        }
        return pins;
    }

    static string Pin(byte vaddr, char port, int bit) => $"0x{vaddr:X2} {port}{bit}";

    // -------------------------------------------------------------------------
    // Step 1: panel LED scan
    // -------------------------------------------------------------------------
    public static void LedScan(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Panel LED scan (SVB) ---");
        Console.WriteLine("Lights each output pin of the panel in turn -- say what the lit LED is.");
        Console.WriteLine();

        if (PickPanelBoard() is not { } board) return;
        if (Pins(arduino, board.VAddrs, wantInput: false) is not { } outputs) return;
        if (outputs.Count == 0) { Console.WriteLine("  No output pins -- upload hardware.json first?"); return; }

        var labels = PensLabels();
        string pensMenu = string.Join("  ", labels.Select((l, i) => $"{i + 1}={l}"));
        var leds = new List<PanelLedEntry>();

        foreach (var (v, port, bit) in outputs)
        {
            arduino.McpSetBit(v, port, bit, true);
            Console.WriteLine();
            Console.WriteLine($"  {Pin(v, port, bit)} lit.  Pens: {pensMenu}");
            Console.Write("  Which?  (pens number, S = signal lamp, 0 = nothing lit/unused, Esc = abort): ");
            char c = char.ToUpper(ReadChar(ch => ch == EscKey || ch == '0' || char.ToUpper(ch) == 'S'
                || (ch >= '1' && ch <= '9' && ch - '1' < labels.Count)));
            Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());

            string? target = null, role = null;
            if (c == 'S')
            {
                Console.Write("  Signal lamp:  R = Red   1 = Green1   2 = Green2: ");
                char s = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'R' || ch is '1' or '2'));
                Console.WriteLine(s);
                target = SystemConfigJson.SignalTarget;
                role = s switch { 'R' => "red", '1' => "green1", _ => "green2" };
            }
            else if (c is >= '1' and <= '9')
            {
                Console.Write($"  {labels[c - '1']}:  R = Rett   A = Avvik: ");
                char r = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'R' or 'A'));
                Console.WriteLine(r);
                target = labels[c - '1'];
                role = r == 'R' ? "rett" : "avvik";
            }
            arduino.McpSetBit(v, port, bit, false);

            if (c == EscKey) { Console.WriteLine("Panel LED scan aborted -- nothing stored."); Console.WriteLine(); return; }
            if (target != null)
                leds.Add(new PanelLedEntry { VAddr = $"0x{v:X2}", Port = port.ToString(), Bit = bit, Target = target, Role = role });
        }

        Console.WriteLine();
        Console.WriteLine($"Found {leds.Count} LED(s):");
        foreach (var g in leds.GroupBy(l => l.Target))
            Console.WriteLine($"  {g.Key,-7} " + string.Join("  ", g.Select(l => $"{l.Role}={l.VAddr} {l.Port}{l.Bit}")));

        var file = SystemConfigSession.LoadOrNew();
        file.PanelLeds = leds;
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
        Console.WriteLine(arduino.SystemConfigSend(SystemConfigJson.PanelLedLines(leds))
            ? "Stored (SystemConfig.json + Arduino)."
            : "Saved to SystemConfig.json, but the Arduino upload failed -- restore later with Config -> J.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Step 2: panel switch scan
    // -------------------------------------------------------------------------
    public static void SwitchScan(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Panel switch scan (SVB) ---");
        Console.WriteLine("Flip one panel switch (or press the button) at a time -- the changed input is detected.");
        Console.WriteLine("Dreieskive: move it from the middle to CW, back to the middle, then to CCW.");
        Console.WriteLine();

        if (PickPanelBoard() is not { } board) return;
        if (Pins(arduino, board.VAddrs, wantInput: true) is not { } inputs) return;
        if (inputs.Count == 0) { Console.WriteLine("  No input pins -- upload hardware.json first?"); return; }

        // Input mask per (vaddr, port), so output pins (LEDs) never count as a change.
        var masks = inputs.GroupBy(p => (p.VAddr, p.Port))
            .ToDictionary(g => g.Key, g => g.Aggregate(0, (m, p) => m | (1 << p.Bit)));

        Dictionary<(byte, char), int>? Read()
        {
            var r = new Dictionary<(byte, char), int>();
            foreach (var key in masks.Keys)
            {
                int val = arduino.McpReadPort(key.Item1, key.Item2);
                if (val < 0) return null;
                r[key] = val & masks[key];
            }
            return r;
        }

        var labels = PensLabels();
        string pensMenu = string.Join("  ", labels.Select((l, i) => $"{i + 1}={l}"));
        var found = new Dictionary<string, SvbSwitchEntry>();

        while (true)
        {
            var baseline = Read();
            if (baseline == null) { Console.WriteLine("  I2C error reading inputs."); return; }

            Console.Write("Waiting for a panel input to change (Esc = done)... ");
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            Dictionary<(byte, char), int>? now = null, last = null;
            bool done = false;
            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape) { done = true; break; }
                now = Read();
                // Changed AND the same on two reads in a row -- rides out contact bounce.
                if (now != null && last != null && now.Keys.Any(k => now[k] != baseline[k])
                    && now.Keys.All(k => now[k] == last[k])) break;
                last = now;
                Thread.Sleep(50);
            }
            if (done) { Console.WriteLine("[Esc]"); break; }

            var changed = now!.Keys.SelectMany(k => Enumerable.Range(0, 8)
                    .Where(b => (((now[k] ^ baseline[k]) >> b) & 1) == 1)
                    .Select(b => (VAddr: k.Item1, Port: k.Item2, Bit: b, Level: (now[k] >> b) & 1)))
                .ToList();
            Console.WriteLine();
            if (changed.Count != 1)
            {
                Console.WriteLine($"  {changed.Count} inputs changed at once ({string.Join(", ", changed.Select(c => Pin(c.VAddr, c.Port, c.Bit)))}) -- one at a time, please.");
                Thread.Sleep(300);
                continue;
            }

            var (v, port, bit, level) = changed[0];
            Console.WriteLine($"  {Pin(v, port, bit)} changed to {(level == 1 ? "High" : "Low")}.  Pens: {pensMenu}");
            Console.Write("  What is it?  (pens number, C = dreieskive CW, W = dreieskive CCW, G = green request, 0 = ignore): ");
            char c = char.ToUpper(ReadChar(ch => ch == '0' || char.ToUpper(ch) is 'C' or 'W' or 'G'
                || (ch >= '1' && ch <= '9' && ch - '1' < labels.Count)));
            Console.WriteLine(c);

            string vs = $"0x{v:X2}", ps = port.ToString();
            if (c is >= '1' and <= '9')
            {
                string label = labels[c - '1'];
                Console.Write($"  Is the {label} panel switch now at Rett or Avvik?  R/A: ");
                char r = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'R' or 'A'));
                Console.WriteLine(r);
                // Polarity = the input level that means Rett.
                found[label] = new SvbSwitchEntry { VAddr = vs, Port = ps, Bit = bit, Polarity = r == 'R' ? level : 1 - level };
            }
            else if (c is 'C' or 'W')
            {
                var d = found.TryGetValue(SystemConfigJson.Dreieskive, out var e) ? e : new SvbSwitchEntry { VAddr = vs, Port = ps };
                if (d.VAddr != vs || d.Port != ps)
                    Console.WriteLine($"  Warning: CW and CCW are on different chips/ports ({d.VAddr} {d.Port} vs {vs} {ps}) -- the table holds one; keeping {vs} {ps}.");
                d.VAddr = vs; d.Port = ps;
                if (c == 'C') d.BitCw = bit; else d.BitCcw = bit;
                found[SystemConfigJson.Dreieskive] = d;
            }
            else if (c == 'G')
            {
                found[SystemConfigJson.AskGreen] = new SvbSwitchEntry { VAddr = vs, Port = ps, Bit = bit };
            }
            // Let a released button / the operator's hand settle before the next baseline.
            Thread.Sleep(300);
        }

        if (found.Count == 0) { Console.WriteLine("Nothing assigned -- nothing stored."); Console.WriteLine(); return; }

        Console.WriteLine();
        Console.WriteLine($"Assigned {found.Count} input(s):");
        foreach (var (key, e) in found)
            Console.WriteLine(key == SystemConfigJson.Dreieskive
                ? $"  {key,-10} {e.VAddr} {e.Port}  CW=bit{e.BitCw?.ToString() ?? "?"}  CCW=bit{e.BitCcw?.ToString() ?? "?"}"
                : $"  {key,-10} {e.VAddr} {e.Port}{e.Bit}" + (e.Polarity is int p ? $"  Rett = {(p == 1 ? "High" : "Low")}" : ""));

        // Merge: inputs not touched this run keep whatever was stored before.
        var file = SystemConfigSession.LoadOrNew();
        foreach (var (key, e) in found) file.SvbSwitches[key] = e;
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
        Console.WriteLine(arduino.SystemConfigSend(SystemConfigJson.SvbSwitchLines(file.SvbSwitches))
            ? "Stored (SystemConfig.json + Arduino)."
            : "Saved to SystemConfig.json, but the Arduino upload failed -- restore later with Config -> J.");
        Console.WriteLine();
    }
}
