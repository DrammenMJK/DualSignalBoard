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

    // Single-key choices, hexadecimal 0-F -- 16 per list (per SCB / per
    // SCB list), past the old 1-9 cap. Havna, the largest, has 11 pens.
    const string Keys = "0123456789ABCDEF";

    static int ReadKeyIndex(int count)
    {
        char c = char.ToUpper(ReadChar(ch => ch == EscKey || Keys.IndexOf(char.ToUpper(ch)) is int i && i >= 0 && i < count));
        Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
        return c == EscKey ? -1 : Keys.IndexOf(c);
    }

    // Picks a pens in two steps -- SCB, then a pens on it -- so any number of
    // SCBs/pens fits, and a panel can drive LEDs for pens on another SVB's
    // SCBs (Havna LEDs on the Fossli panel). The SCB step is skipped when
    // only one SCB has a pens to offer. `mark` decorates a label (e.g. * =
    // already configured). Null on Esc.
    internal static string? PickPens(Func<string, bool>? allowed = null, Func<string, string>? mark = null)
    {
        var scbs = BoardConfig.Svb.Scbs
            .Select(s => (s.Name, Labels: s.Switches.Select(w => w.Label).Where(allowed ?? (_ => true)).ToList()))
            .Where(g => g.Labels.Count > 0)
            .ToList();
        if (scbs.Count == 0) { Console.WriteLine("  No pens to pick."); return null; }

        var scb = scbs[0];
        if (scbs.Count > 1)
        {
            Console.Write("  SCB:  " + string.Join("  ", scbs.Select((g, i) => $"{Keys[i]}={g.Name}")) + "  (Esc = cancel): ");
            int i = ReadKeyIndex(scbs.Count);
            if (i < 0) return null;
            scb = scbs[i];
        }
        Console.Write($"  Pens on {scb.Name}:  " + string.Join("  ", scb.Labels.Select((l, i) => $"{Keys[i]}={(mark ?? (x => x))(l)}")) + "  (Esc = cancel): ");
        int p = ReadKeyIndex(scb.Labels.Count);
        return p < 0 ? null : scb.Labels[p];
    }

    // Panel tables as the Arduino holds them (EEPROM is the source of truth).
    internal static SystemConfigFile? ReadPanel(ArduinoDevice arduino)
    {
        var lines = arduino.SystemConfigDownload();
        if (lines == null) { Console.WriteLine("  Timeout reading config from the Arduino."); return null; }
        var file = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(file, lines);
        return file;
    }

    // Mirrors the panel tables into SystemConfig.json and sends `lines` to the Arduino.
    internal static void Store(ArduinoDevice arduino, SystemConfigFile panel, List<string> lines)
    {
        var file = SystemConfigSession.LoadOrNew();
        file.SvbSwitches = panel.SvbSwitches;
        file.PanelLeds = panel.PanelLeds;
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);
        Console.WriteLine(arduino.SystemConfigSend(lines)
            ? "Stored (SystemConfig.json + Arduino)."
            : "Saved to SystemConfig.json, but the Arduino upload failed -- restore later with Config -> J.");
    }

    // Asks what an LED is. False on Esc; target null = unused/nothing lit.
    internal static bool AskLedTarget(out string? target, out string? role)
    {
        target = role = null;
        while (true)
        {
            Console.Write("  Which?  (P = pens, S = signal lamp, 0 = nothing lit/unused, Esc = abort): ");
            char c = char.ToUpper(ReadChar(ch => ch == EscKey || ch == '0' || char.ToUpper(ch) is 'P' or 'S'));
            Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
            if (c == EscKey) return false;
            if (c == '0') return true;
            if (c == 'S')
            {
                Console.Write("  Signal lamp:  R = Red   1 = Green1   2 = Green2: ");
                char s = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'R' || ch is '1' or '2'));
                Console.WriteLine(s);
                target = SystemConfigJson.SignalTarget;
                role = s switch { 'R' => "red", '1' => "green1", _ => "green2" };
                return true;
            }
            if (PickPens() is not { } label) continue; // Esc in the picker = ask again
            Console.Write($"  {label}:  R = Rett   A = Avvik: ");
            char r = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'R' or 'A'));
            Console.WriteLine(r);
            target = label;
            role = r == 'R' ? "rett" : "avvik";
            return true;
        }
    }

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

    internal static string Pin(byte vaddr, char port, int bit) => $"0x{vaddr:X2} {port}{bit}";

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

        if (ReadPanel(arduino) is not { } panel) return;
        var vaddrs = board.VAddrs.Select(v => $"0x{v:X2}").ToHashSet();
        bool OnThisBoard(PanelLedEntry l) => vaddrs.Contains(l.VAddr ?? "");

        // Resume: LEDs this board already has can be kept and their pins skipped.
        var leds = new List<PanelLedEntry>();
        int existing = panel.PanelLeds.Count(OnThisBoard);
        if (existing > 0)
        {
            Console.Write($"{existing} LED(s) on this board are already configured.  K = keep them, scan only the rest   O = start over   Esc = cancel: ");
            char k = char.ToUpper(ReadChar(ch => ch == EscKey || char.ToUpper(ch) is 'K' or 'O'));
            Console.WriteLine(k == EscKey ? "[Esc]" : k.ToString());
            if (k == EscKey) { Console.WriteLine(); return; }
            if (k == 'K') leds.AddRange(panel.PanelLeds.Where(OnThisBoard));
        }
        var done = leds.Select(l => (l.VAddr, l.Port, l.Bit)).ToHashSet();
        var todo = outputs.Where(p => !done.Contains(($"0x{p.VAddr:X2}", p.Port.ToString(), p.Bit))).ToList();
        Console.WriteLine($"{todo.Count} output pin(s) to go. Pins answered 0 (unused) aren't stored, so a resumed scan asks them again.");

        bool stopped = false;
        foreach (var (v, port, bit) in todo)
        {
            arduino.McpSetBit(v, port, bit, true);
            Console.WriteLine();
            Console.WriteLine($"  {Pin(v, port, bit)} lit.");
            bool answered = AskLedTarget(out string? target, out string? role);
            arduino.McpSetBit(v, port, bit, false);

            if (!answered) { stopped = true; break; }
            if (target != null)
                leds.Add(new PanelLedEntry { VAddr = $"0x{v:X2}", Port = port.ToString(), Bit = bit, Target = target, Role = role });
        }

        if (stopped)
        {
            Console.Write($"Stopped. Store the {leds.Count} LED(s) so far, to continue later with K?  Y/N: ");
            char y = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'Y' or 'N'));
            Console.WriteLine(y);
            if (y == 'N') { Console.WriteLine("Nothing stored."); Console.WriteLine(); return; }
        }

        Console.WriteLine();
        Console.WriteLine($"{leds.Count} LED(s) on this board:");
        foreach (var g in leds.GroupBy(l => l.Target))
            Console.WriteLine($"  {g.Key,-7} " + string.Join("  ", g.Select(l => $"{l.Role}={l.VAddr} {l.Port}{l.Bit}")));

        // Only this board's LEDs are replaced -- other panel boards' stay.
        panel.PanelLeds = panel.PanelLeds.Where(l => !OnThisBoard(l)).Concat(leds).ToList();
        Store(arduino, panel, SystemConfigJson.PanelLedLines(panel.PanelLeds));
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

        if (ReadPanel(arduino) is not { } panel) return;
        // Resume: what's already assigned stays; flip only the remaining switches.
        if (panel.SvbSwitches.Count > 0)
            Console.WriteLine($"Already configured (kept unless reassigned): {string.Join(", ", panel.SvbSwitches.Keys)}");

        // Which key (if any) already uses this input pin.
        string? Owner(string vs, string ps, int bit) => panel.SvbSwitches.FirstOrDefault(kv =>
            kv.Value.VAddr == vs && kv.Value.Port == ps && (kv.Value.Bit == bit || kv.Value.BitCw == bit || kv.Value.BitCcw == bit)).Key;

        var found = new Dictionary<string, SvbSwitchEntry>();
        var cleared = new List<string>();

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
            string vs = $"0x{v:X2}", ps = port.ToString();
            string? owner = Owner(vs, ps, bit);
            Console.WriteLine($"  {Pin(v, port, bit)} changed to {(level == 1 ? "High" : "Low")}{(owner != null ? $"  (currently: {owner})" : "")}.");
            Console.Write("  What is it?  (P = pens, C = dreieskive CW, W = dreieskive CCW, G = green request, 0 = ignore): ");
            char c = char.ToUpper(ReadChar(ch => ch == '0' || char.ToUpper(ch) is 'P' or 'C' or 'W' or 'G'));
            Console.WriteLine(c);

            // Pens already assigned get a * in the picker. Esc there = ignore this input.
            string? label = c == 'P' ? PickPens(mark: l => panel.SvbSwitches.ContainsKey(l) ? l + "*" : l) : null;
            if (c == 'P' && label == null) c = '0';

            // Reassigning a pin moves it: the key that had it loses it.
            if (c != '0' && owner != null && owner != SystemConfigJson.Dreieskive)
            {
                panel.SvbSwitches.Remove(owner);
                found.Remove(owner);
                cleared.Add(owner);
            }

            if (label != null)
            {
                Console.Write($"  Is the {label} panel switch now at Rett or Avvik?  R/A: ");
                char r = char.ToUpper(ReadChar(ch => char.ToUpper(ch) is 'R' or 'A'));
                Console.WriteLine(r);
                // Polarity = the input level that means Rett.
                found[label] = new SvbSwitchEntry { VAddr = vs, Port = ps, Bit = bit, Polarity = r == 'R' ? level : 1 - level };
            }
            else if (c is 'C' or 'W')
            {
                // Builds on a stored half (e.g. CW done before a restart).
                var d = panel.SvbSwitches.TryGetValue(SystemConfigJson.Dreieskive, out var e) ? e : new SvbSwitchEntry { VAddr = vs, Port = ps };
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
            foreach (var (key, entry) in found) { panel.SvbSwitches[key] = entry; cleared.Remove(key); }
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

        // Only what changed this run is sent; everything else stays as stored.
        var lines = SystemConfigJson.SvbSwitchLines(found);
        lines.AddRange(cleared.Distinct().Select(SystemConfigJson.ClearSvbSwitchLine));
        Store(arduino, panel, lines);
        Console.WriteLine();
    }
}
