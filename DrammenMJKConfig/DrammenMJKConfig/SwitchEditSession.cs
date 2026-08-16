namespace DrammenMJKConfig;

// Post-scan corrections for human error: the operator judged R/A wrong, or
// misidentified which physical switch moved and gave it the wrong label.
// Re-running the whole physical Motor Scan just to fix a labeling mistake
// is wasteful -- these edit EEPROM (source of truth) and SystemConfig.json
// (its mirror) directly, no motor movement involved.
static class SwitchEditSession
{
    public static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Edit switch config (fix labeling/polarity mistakes) ---");
        SwitchTable.Print(arduino);

        new Menu(
            [
                ('T', "Show table",                                            () => SwitchTable.Print(arduino)),
                ('P', "Swap Rett/Avvik for one switch (fixes a misjudged R/A)", () => SwapPolarity(arduino)),
                ('S', "Swap two switches (fixes a mislabeled switch)",         () => SwapLabels(arduino)),
            ],
            quitOption: ('Q', "Back")
        ).Run();

        Console.WriteLine();
    }

    static bool TryPromptLabel(string prompt, out string label)
    {
        Console.Write($"{prompt}: ");
        string? input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input) && BoardConfig.TryFindSlotByLabel(input, out _)) { label = input; return true; }
        Console.WriteLine("  Unknown label.");
        label = "";
        return false;
    }

    static void SwapPolarity(ArduinoDevice arduino)
    {
        Console.WriteLine();
        if (!TryPromptLabel("Label", out string label)) return;
        BoardConfig.TryFindSlotByLabel(label, out int slot);

        var d = SwitchSlotData.Read(arduino, slot);
        if (!d.IsConfigured) { Console.WriteLine("  Not configured yet."); return; }

        byte newPol = (byte)(d.Polarity == 0 ? 1 : 0);
        var swapped = d with { Polarity = newPol, FeedbackRett = d.FeedbackAvvik, FeedbackAvvik = d.FeedbackRett };
        swapped.Write(arduino, slot);

        var file = SystemConfigSession.LoadOrNew();
        swapped.ApplyTo(file, label);
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);

        Console.WriteLine($"  {label}: polarity {d.Polarity} -> {newPol}, " +
                           $"Rett/Avvik {d.FeedbackRett}/{d.FeedbackAvvik} -> {swapped.FeedbackRett}/{swapped.FeedbackAvvik}.");
        SwitchTable.Print(arduino);
    }

    static void SwapLabels(ArduinoDevice arduino)
    {
        Console.WriteLine();
        if (!TryPromptLabel("First label", out string labelA)) return;
        if (!TryPromptLabel("Second label", out string labelB)) return;
        if (labelA == labelB) { Console.WriteLine("  Same label twice -- nothing to do."); return; }
        BoardConfig.TryFindSlotByLabel(labelA, out int slotA);
        BoardConfig.TryFindSlotByLabel(labelB, out int slotB);

        var dataA = SwitchSlotData.Read(arduino, slotA);
        var dataB = SwitchSlotData.Read(arduino, slotB);
        dataB.Write(arduino, slotA);
        dataA.Write(arduino, slotB);

        var file = SystemConfigSession.LoadOrNew();
        dataB.ApplyTo(file, labelA);
        dataA.ApplyTo(file, labelB);
        SystemConfigJson.Save(file, SystemConfigSession.DefaultPath);

        Console.WriteLine($"  Swapped {labelA} <-> {labelB}.");
        SwitchTable.Print(arduino);
    }
}
