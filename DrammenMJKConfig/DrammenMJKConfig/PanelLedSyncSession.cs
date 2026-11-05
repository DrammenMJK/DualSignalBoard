namespace DrammenMJKConfig;

// Live preview of the (not-yet-built, see PLAN_Phase2.md's autonomous
// runtime loop) PC-less behaviour, using only the panel board itself: reads
// each pens' panel switch (Config -> 5, Panel switch scan's result -- a
// single input bit + polarity, no motor/feedback involved) and drives its
// panel Rett/Avvik LED(s) to match. Needs no motor board at all -- the panel
// switch and the panel LED are both on the SVB/panel chip. Signal-lamp LEDs
// (Target == "signal") aren't touched -- their aspect depends on more than
// one switch and isn't this session's job.
sealed class PanelLedSyncSession(IArduinoDevice arduino, PanelScanSession scan)
{
    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- Panel LED sync (drive LEDs from panel switches) ---");
        Console.WriteLine("No motor/feedback needed -- reads each pens' panel switch, lights its Rett/Avvik LED.");
        Console.WriteLine();

        if (scan.ReadPanel() is not { } panel) return;

        var pensSwitches = panel.SvbSwitches
            .Where(kv => kv.Key != SystemConfigJson.Dreieskive && kv.Key != SystemConfigJson.AskGreen)
            .Where(kv => kv.Value.VAddr != null && kv.Value.Port != null && kv.Value.Bit != null && kv.Value.Polarity != null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var ledsByPens = panel.PanelLeds
            .Where(l => l.Target != null && l.Target != SystemConfigJson.SignalTarget)
            .GroupBy(l => l.Target!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var pens = ledsByPens.Keys.ToList();
        foreach (string label in pens.Where(p => !pensSwitches.ContainsKey(p)))
            Console.WriteLine($"  {label}: has panel LED(s) but no panel switch configured -- run Config -> 5 (Panel switch scan), skipping.");
        pens = pens.Where(pensSwitches.ContainsKey).ToList();
        if (pens.Count == 0) { Console.WriteLine("  Nothing to sync."); Console.WriteLine(); return; }

        Console.WriteLine($"  Syncing {pens.Count} pens from their panel switches. Any key = stop.");
        Console.WriteLine();
        while (Console.KeyAvailable) Console.ReadKey(intercept: true); // drop stale keys

        string? shown = null;
        while (!Console.KeyAvailable)
        {
            // One read per distinct (vaddr, port) -- several pens' switches
            // often share a chip/port, no point reading it twice.
            var reads = new Dictionary<(byte, char), int>();
            bool allOk = true;
            foreach (var sw in pens.Select(p => pensSwitches[p]))
            {
                var key = (Convert.ToByte(sw.VAddr!, 16), sw.Port![0]);
                if (reads.ContainsKey(key)) continue;
                int g = arduino.McpReadPort(key.Item1, key.Item2);
                reads[key] = g;
                if (g < 0) allOk = false;
            }
            if (!allOk)
            {
                Console.Write("\r  No response from the panel.".PadRight(60));
                Thread.Sleep(200);
                continue;
            }

            var parts = new List<string>();
            foreach (string label in pens)
            {
                var sw = pensSwitches[label];
                int level = (reads[(Convert.ToByte(sw.VAddr!, 16), sw.Port![0])] >> sw.Bit!.Value) & 1;
                bool rett = level == sw.Polarity!.Value;

                foreach (var led in ledsByPens[label])
                {
                    if (led.VAddr == null || led.Port == null || led.Bit == null) continue;
                    bool on = led.Role == "rett" ? rett : led.Role == "avvik" && !rett;
                    arduino.McpSetBit(Convert.ToByte(led.VAddr, 16), led.Port[0], led.Bit.Value, PanelScanSession.LedLevel(on));
                }
                parts.Add($"{label}={(rett ? "Rett" : "Avvik")}");
            }

            string line = string.Join("  ", parts);
            if (line != shown) { Console.WriteLine("  " + line); shown = line; }
            Thread.Sleep(150);
        }
        Console.ReadKey(intercept: true); // consume the stop key
        Console.WriteLine();
        Console.WriteLine("Stopped -- LEDs left showing the last switch read.");
        Console.WriteLine();
    }
}
