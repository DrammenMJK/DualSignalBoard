using static DrammenMJKConfig.ConfigInputHelpers;

namespace DrammenMJKConfig;

// Post-scan corrections for the SVB panel, the panel counterpart of
// SwitchEditSession: fix a misjudged Rett/Avvik, a mislabeled switch or LED,
// swapped dreieskive directions, or drop an entry -- without re-running a
// scan. Every edit starts from what the Arduino holds (EEPROM is the source
// of truth), changes it in memory, sends ONLY the entries it changed back to
// the Arduino, and mirrors the panel tables into SystemConfig.json.
sealed class PanelEditSession(PanelScanSession panelScan)
{
    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- Edit panel config (SVB inputs and LEDs) ---");
        Show();

        new Menu(
            [
                ('T', "Show table",                                              Show),
                ('P', "Swap Rett/Avvik for one panel switch (misjudged R/A)",    () => Edit(SwapSwitchPolarity)),
                ('S', "Swap two panel switches (mislabeled switch)",             () => Edit(SwapSwitches)),
                ('D', "Swap dreieskive CW/CCW",                                  () => Edit(SwapDreieskive)),
                ('L', "Reassign one LED (or remove it)",                         () => Edit(ReassignLed)),
                ('R', "Swap Rett/Avvik LEDs for one pens",                       () => Edit(SwapLedRoles)),
                ('X', "Remove one panel input",                                  () => Edit(RemoveInput)),
            ],
            quitOption: ('Q', "Back")
        ).Run();

        Console.WriteLine();
    }

    // Read -> edit -> write back -> show. `edit` returns the SCU lines for
    // exactly the entries it changed (null = cancelled/nothing changed), so a
    // single fix never rewrites or clears the rest of the panel tables.
    void Edit(Func<SystemConfigFile, List<string>?> edit)
    {
        Console.WriteLine();
        if (panelScan.ReadPanel() is not { } panel) return;
        if (edit(panel) is not { Count: > 0 } lines) return;
        panelScan.Store(panel, lines);
        Print(panel);
    }

    void Show()
    {
        if (panelScan.ReadPanel() is { } panel) Print(panel);
    }

    static void Print(SystemConfigFile panel)
    {
        Console.WriteLine();
        Console.WriteLine("  Panel inputs:");
        foreach (string key in SystemConfigJson.SvbSwitchLabelOrder().OfType<string>())
        {
            if (!panel.SvbSwitches.TryGetValue(key, out var e)) { Console.WriteLine($"    {key,-10} -"); continue; }
            Console.WriteLine("    " + PanelScanSession.FormatInput(key, e));
        }
        Console.WriteLine("  Panel LEDs:");
        if (panel.PanelLeds.Count == 0) Console.WriteLine("    -");
        for (int i = 0; i < panel.PanelLeds.Count; i++)
        {
            var l = panel.PanelLeds[i];
            Console.WriteLine($"    {i + 1,2}  {l.VAddr} {l.Port}{l.Bit}  {l.Target,-7} {l.Role}");
        }
        Console.WriteLine();
    }

    static List<string> InputLines(SystemConfigFile panel, params string[] keys) =>
        SystemConfigJson.SvbSwitchLines(panel.SvbSwitches.Where(kv => keys.Contains(kv.Key)).ToDictionary());

    // Pens picked by number key, like the scans. Null on Esc.
    static string? PickPens(string prompt, Func<string, bool> allowed)
    {
        Console.WriteLine(prompt);
        return PanelScanSession.PickPens(allowed);
    }

    static List<string>? SwapSwitchPolarity(SystemConfigFile panel)
    {
        if (PickPens("Panel switch:", l => panel.SvbSwitches.ContainsKey(l)) is not { } label) return null;
        var e = panel.SvbSwitches[label];
        e.Polarity = e.Polarity == 1 ? 0 : 1;
        Console.WriteLine($"  {label}: Rett is now {(e.Polarity == 1 ? "High" : "Low")}.");
        return InputLines(panel, label);
    }

    static List<string>? SwapSwitches(SystemConfigFile panel)
    {
        if (PickPens("First:", _ => true) is not { } a) return null;
        if (PickPens("Second:", l => l != a) is not { } b) return null;
        bool hasA = panel.SvbSwitches.Remove(a, out var ea), hasB = panel.SvbSwitches.Remove(b, out var eb);
        if (hasB) panel.SvbSwitches[a] = eb!;
        if (hasA) panel.SvbSwitches[b] = ea!;
        Console.WriteLine($"  Swapped {a} <-> {b}.");
        // A side that ends up empty must be cleared on the board too.
        var lines = InputLines(panel, a, b);
        if (!hasB) lines.Add(SystemConfigJson.ClearSvbSwitchLine(a));
        if (!hasA) lines.Add(SystemConfigJson.ClearSvbSwitchLine(b));
        return lines;
    }

    static List<string>? SwapDreieskive(SystemConfigFile panel)
    {
        if (!panel.SvbSwitches.TryGetValue(SystemConfigJson.Dreieskive, out var d)) { Console.WriteLine("  Dreieskive input not configured."); return null; }
        (d.BitCw, d.BitCcw) = (d.BitCcw, d.BitCw);
        Console.WriteLine($"  Dreieskive: CW=bit{d.BitCw?.ToString() ?? "?"}  CCW=bit{d.BitCcw?.ToString() ?? "?"}.");
        return InputLines(panel, SystemConfigJson.Dreieskive);
    }

    static List<string>? ReassignLed(SystemConfigFile panel)
    {
        if (panel.PanelLeds.Count == 0) { Console.WriteLine("  No panel LEDs configured."); return null; }
        Print(panel);
        Console.Write($"LED number (1-{panel.PanelLeds.Count}, blank = cancel): ");
        if (!int.TryParse(Console.ReadLine()?.Trim(), out int n) || n < 1 || n > panel.PanelLeds.Count) { Console.WriteLine("  Cancelled."); return null; }

        var led = panel.PanelLeds[n - 1];
        Console.WriteLine($"  {led.VAddr} {led.Port}{led.Bit} is now {led.Target} {led.Role}.");
        if (!PanelScanSession.AskLedTarget(out string? target, out string? role)) return null;
        int index = led.Index ?? n - 1;
        if (target != null)
        {
            led.Target = target; led.Role = role;
            return [SystemConfigJson.LedLine(index, led)];
        }
        // Free just this entry on the board; the others keep their places.
        panel.PanelLeds.RemoveAt(n - 1);
        Console.WriteLine("  Removed.");
        return [SystemConfigJson.ClearLedLine(index)];
    }

    static List<string>? SwapLedRoles(SystemConfigFile panel)
    {
        if (PickPens("Pens:", l => panel.PanelLeds.Any(x => x.Target == l)) is not { } label) return null;
        var lines = new List<string>();
        for (int i = 0; i < panel.PanelLeds.Count; i++)
        {
            var led = panel.PanelLeds[i];
            if (led.Target != label) continue;
            led.Role = led.Role == "rett" ? "avvik" : "rett";
            lines.Add(SystemConfigJson.LedLine(led.Index ?? i, led));
        }
        Console.WriteLine($"  {label}: Rett and Avvik LEDs swapped.");
        return lines;
    }

    static List<string>? RemoveInput(SystemConfigFile panel)
    {
        var keys = SystemConfigJson.SvbSwitchLabelOrder().OfType<string>().Where(panel.SvbSwitches.ContainsKey).ToList();
        if (keys.Count == 0) { Console.WriteLine("  No panel inputs configured."); return null; }
        Console.WriteLine("  " + string.Join("  ", keys.Select((k, i) => $"{i + 1}={k}")));
        Console.Write($"Remove which (1-{keys.Count}, blank = cancel): ");
        if (!int.TryParse(Console.ReadLine()?.Trim(), out int n) || n < 1 || n > keys.Count) { Console.WriteLine("  Cancelled."); return null; }
        panel.SvbSwitches.Remove(keys[n - 1]);
        Console.WriteLine($"  Removed {keys[n - 1]}.");
        return [SystemConfigJson.ClearSvbSwitchLine(keys[n - 1])];
    }
}
