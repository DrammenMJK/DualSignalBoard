namespace DrammenMJKConfig;

// Post-scan corrections for human error: the operator judged R/A wrong, or
// misidentified which physical switch moved and gave it the wrong label.
// Re-running the whole physical Motor Scan just to fix a labeling mistake
// is wasteful -- these edit EEPROM (source of truth) and SystemConfig.json
// (its mirror) directly, no motor movement involved. The mirror is only
// updated once every EEPROM read and write has succeeded.
sealed class SwitchEditSession(IArduinoDevice arduino, SwitchTable switchTable)
{
    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- Edit switch config (fix labeling/polarity mistakes) ---");
        switchTable.Print();

        new Menu(
            [
                ('T', "Show table",                                            switchTable.Print),
                ('P', "Swap Rett/Avvik for one switch (fixes a misjudged R/A)", SwapPolarity),
                ('S', "Swap two switches (fixes a mislabeled switch)",         SwapLabels),
            ],
            quitOption: ('Q', "Back")
        ).Run();

        Console.WriteLine();
    }

    static bool TryPromptLabel(string prompt, out string label, out int slot)
    {
        Console.Write($"{prompt}: ");
        string? input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input) && BoardConfig.TryFindSlotByLabel(input, out slot)) { label = input; return true; }
        Console.WriteLine("  Unknown label.");
        label = "";
        slot = -1;
        return false;
    }

    SwitchSlotData? Read(int slot)
    {
        var d = SwitchSlotData.Read(arduino, slot);
        if (d == null) Console.WriteLine("  EEPROM read failed -- nothing changed. Check the connection and retry.");
        return d;
    }

    bool Write(SwitchSlotData d, int slot, string label)
    {
        if (d.Write(arduino, slot)) return true;
        Console.WriteLine($"  EEPROM write failed for {label} -- the slot may be half-written and SystemConfig.json was NOT updated. Check the table and retry.");
        return false;
    }

    void SwapPolarity()
    {
        Console.WriteLine();
        if (!TryPromptLabel("Label", out string label, out int slot)) return;

        if (Read(slot) is not { } d) return;
        if (!d.IsConfigured) { Console.WriteLine("  Not configured yet."); return; }

        byte newPol = (byte)(d.Polarity == 0 ? 1 : 0);
        var swapped = d with { Polarity = newPol, FeedbackRett = d.FeedbackAvvik, FeedbackAvvik = d.FeedbackRett };
        if (!Write(swapped, slot, label)) return;

        var file = SystemConfigSession.LoadOrNew();
        swapped.ApplyTo(file, label);
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);

        Console.WriteLine($"  {label}: polarity {d.Polarity} -> {newPol}, " +
                           $"Rett/Avvik {d.FeedbackRett}/{d.FeedbackAvvik} -> {swapped.FeedbackRett}/{swapped.FeedbackAvvik}.");
        switchTable.Print();
    }

    void SwapLabels()
    {
        Console.WriteLine();
        if (!TryPromptLabel("First label", out string labelA, out int slotA)) return;
        if (!TryPromptLabel("Second label", out string labelB, out int slotB)) return;
        if (labelA == labelB) { Console.WriteLine("  Same label twice -- nothing to do."); return; }

        if (Read(slotA) is not { } dataA || Read(slotB) is not { } dataB) return;
        if (!Write(dataB, slotA, labelA) || !Write(dataA, slotB, labelB)) return;

        var file = SystemConfigSession.LoadOrNew();
        dataB.ApplyTo(file, labelA);
        dataA.ApplyTo(file, labelB);
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);

        Console.WriteLine($"  Swapped {labelA} <-> {labelB}.");
        switchTable.Print();
    }
}
