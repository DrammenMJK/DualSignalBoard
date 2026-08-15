namespace DrammenMJKConfig;

// One switch expected on an SCB, by its real-world label ("1", "3", "5/6", "7").
sealed record BoardSwitch(string Label);

// One SCB: which switches Motor Scan expects to find on it, and its two
// board-specific fixed facts (dreieskive pins, status LED) that get declared
// (not scanned) at the start of a Motor Scan session.
sealed record BoardScb(
    string Name,
    byte VirtualAddress,
    int MotorBitFirst,
    IReadOnlyList<BoardSwitch> Switches,
    (int Base, int Upper) DreieskivePins,
    (char Port, int Bit) StatusLedPin
);

// An SVB owns a *list* of SCBs, not a single one -- Phase 1 populates one
// entry (FCSBR), but Fossli will have two (Høyre + Venstre) as soon as
// Phase 2 starts, and everything iterating this list already expects that.
sealed record BoardSvb(string Name, int Number, IReadOnlyList<BoardScb> Scbs);

static class BoardConfig
{
    public static readonly BoardSvb Svb = new(
        Name: "Fossli",
        Number: 1,
        Scbs:
        [
            new BoardScb(
                Name: "Fossli Høyre (FCSBR)",
                VirtualAddress: ArduinoDevice.VirtualAddress(bus: 0, realAddr: 0x20),
                MotorBitFirst: 0,
                Switches:
                [
                    new BoardSwitch("1"),
                    new BoardSwitch("3"),
                    new BoardSwitch("5/6"),
                    new BoardSwitch("7"),
                ],
                DreieskivePins: (4, 5),
                StatusLedPin: ('B', 7)
            ),
            // Phase 2: a second entry here for Fossli Venstre (FCSBL) --
            // VirtualAddress = ArduinoDevice.VirtualAddress(bus: 0, realAddr: 0x21),
            // switches "101", "2", "4" -- is all that's needed to extend the
            // scan/command flow to it. See PLAN_Phase1.md.
        ]
    );

    // Slot index for a switch = its position across Scbs[].Switches[] in
    // declaration order -- must match the order MotorScan writes the EEPROM
    // switch table in.
    public static IEnumerable<(BoardScb Scb, int MotorBit, string Label, int Slot)> AllSwitchSlots()
    {
        int slot = 0;
        foreach (var scb in Svb.Scbs)
        {
            for (int i = 0; i < scb.Switches.Count; i++)
            {
                yield return (scb, scb.MotorBitFirst + i, scb.Switches[i].Label, slot);
                slot++;
            }
        }
    }

    public static bool TryFindSlotByLabel(string label, out int slot)
    {
        foreach (var (_, _, lbl, s) in AllSwitchSlots())
        {
            if (lbl == label) { slot = s; return true; }
        }
        slot = -1;
        return false;
    }
}
