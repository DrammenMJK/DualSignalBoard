namespace DrammenMJKConfig;

// Read-only soundness check of the config in EEPROM, from one HWD and one
// SCD dump: every record must point at a board in the hardware table, use
// pins that exist and have the direction the record needs (driven pins are
// outputs, read pins are inputs), and no output pin may be driven by two
// records. One line per problem; empty list = sound.
sealed class EepromCheck
{
    readonly Dictionary<byte, HardwareConfig.ResolvedChip> _chips = new();
    readonly Dictionary<int, string> _slotLabels = BoardConfig.AllSwitchSlots().ToDictionary(s => s.Slot, s => s.Label);
    readonly Dictionary<(byte VAddr, char Port, int Bit), string> _outputs = new();
    readonly List<string> _problems = [];

    EepromCheck() { }

    public static List<string> Problems(IEnumerable<HardwareConfig.ResolvedChip> chips, IEnumerable<string> scdLines)
    {
        var check = new EepromCheck();
        check.CheckBoards(chips);
        foreach (string line in scdLines) check.CheckLine(line);
        return check._problems;
    }

    void CheckBoards(IEnumerable<HardwareConfig.ResolvedChip> chips)
    {
        var declared = BoardConfig.Svb.Scbs.SelectMany(b => b.VirtualAddresses)
            .Concat(BoardConfig.PanelBoards.SelectMany(b => b.VAddrs))
            .ToHashSet();
        foreach (var c in chips)
        {
            if (!_chips.TryAdd(c.VAddr, c)) _problems.Add($"Hardware table lists 0x{c.VAddr:X2} twice.");
            else if (!declared.Contains(c.VAddr)) _problems.Add($"Hardware table has 0x{c.VAddr:X2}, which no board in boards.json uses.");
        }
    }

    void CheckLine(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (p)
            {
                case ["S", _, _, _, _, _, _, _]: CheckSwitch(p); break;
                case ["P", _, _, _, _, _, _, _, _]: CheckPanelInput(p); break;
                case ["Q", _, _, _, _, _, _]: CheckPanelLed(p); break;
                case ["D", _, _, _]:
                    Pin(Hex(p[1]), 'B', Hex(p[2]), true, "Dreieskive CW");
                    Pin(Hex(p[1]), 'B', Hex(p[2]) + 1, true, "Dreieskive CCW");
                    if (Hex(p[3]) is not (0 or 1 or 0xFF)) _problems.Add($"Dreieskive: CW polarity {p[3]} must be 00 or 01.");
                    break;
                case ["L", _, _]: Pin(Hex(p[1]), 'B', Hex(p[2]), true, $"Status LED of 0x{Hex(p[1]):X2}"); break;
                case ["G", _, _, _, _]:
                    string[] colors = ["red", "green1", "green2"];
                    for (int i = 0; i < 3; i++) Pin(Hex(p[1]), 'B', Hex(p[i + 2]), true, $"Signal {colors[i]} of 0x{Hex(p[1]):X2}");
                    break;
                case ["V", _, _, _]: Pin(Hex(p[1]), p[2][0], Hex(p[3]), true, $"Inverter enable of 0x{Hex(p[1]):X2}"); break;
                case ["T", _, _, _]:
                    Pin(Hex(p[1]), 'A', Hex(p[2]), false, $"Track detection of 0x{Hex(p[1]):X2}");
                    if (Hex(p[3]) > 1) _problems.Add($"Track detection of 0x{Hex(p[1]):X2}: active level {p[3]} must be 00 or 01.");
                    break;
                default: _problems.Add($"Unreadable SCD line '{line}'."); break;
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            _problems.Add($"Unreadable SCD line '{line}'.");
        }
    }

    // S <slot> <motorVAddr> <motorBit> <polarity> <fbVAddr> <rettBit> <avvikBit>
    void CheckSwitch(string[] p)
    {
        int slot = int.Parse(p[1]);
        if (!_slotLabels.TryGetValue(slot, out string? label)) { _problems.Add($"Switch slot {slot} has data but no switch in boards.json."); return; }
        string what = $"Switch {label}";
        Pin(Hex(p[2]), 'B', Hex(p[3]), true, $"{what} motor");
        if (Hex(p[4]) > 1) _problems.Add($"{what}: polarity {p[4]} must be 00 or 01.");
        Pin(Hex(p[5]), 'A', Hex(p[6]), false, $"{what} Rett feedback");
        Pin(Hex(p[5]), 'A', Hex(p[7]), false, $"{what} Avvik feedback");
        if (p[6] == p[7]) _problems.Add($"{what}: Rett and Avvik feedback use the same bit {p[6]}.");
    }

    // P <slot> <vaddr> <port> <bitPri> <bitSec> <kind> <targetSlot> <pol>
    void CheckPanelInput(string[] p)
    {
        int slot = int.Parse(p[1]);
        byte vaddr = Hex(p[2]);
        char port = p[3][0];
        switch (Hex(p[6]))
        {
            case 0:
                if (slot >= SystemConfigJson.DreieskiveSlot || !_slotLabels.TryGetValue(slot, out string? label)) { _problems.Add($"Panel input slot {slot} is a pens, but no switch in boards.json has that slot."); return; }
                if (Hex(p[7]) != slot) _problems.Add($"Panel input {label}: target slot {p[7]} must be its own slot {slot:X2}.");
                if (Hex(p[8]) > 1) _problems.Add($"Panel input {label}: polarity {p[8]} must be 00 or 01.");
                Pin(vaddr, port, Hex(p[4]), false, $"Panel input {label}");
                break;
            case 1:
                if (slot != SystemConfigJson.DreieskiveSlot) _problems.Add($"Panel input slot {slot}: dreieskive belongs in slot {SystemConfigJson.DreieskiveSlot}.");
                if (p[4] != "FF") Pin(vaddr, port, Hex(p[4]), false, "Panel input dreieskive CW");
                if (p[5] != "FF") Pin(vaddr, port, Hex(p[5]), false, "Panel input dreieskive CCW");
                if (p[4] != "FF" && p[4] == p[5]) _problems.Add($"Panel input dreieskive: CW and CCW use the same bit {p[4]}.");
                break;
            case 2:
                if (slot != SystemConfigJson.AskGreenSlot) _problems.Add($"Panel input slot {slot}: green request belongs in slot {SystemConfigJson.AskGreenSlot}.");
                Pin(vaddr, port, Hex(p[4]), false, "Panel input green request");
                break;
            default:
                _problems.Add($"Panel input slot {slot}: unknown kind {p[6]}.");
                break;
        }
    }

    // Q <idx> <vaddr> <port> <bit> <targetSlot> <role>; role 0-1 = pens, 2-4 = signal.
    void CheckPanelLed(string[] p)
    {
        string what = $"Panel LED #{p[1]}";
        int role = Hex(p[6]);
        if (role >= SystemConfigJson.LedRoles.Length) { _problems.Add($"{what}: unknown role {p[6]}."); return; }
        bool forSignal = role >= 2;
        if (forSignal != (p[5] == "FF") || (!forSignal && !_slotLabels.ContainsKey(Hex(p[5]))))
            _problems.Add($"{what}: target {p[5]} does not fit role {SystemConfigJson.LedRoles[role]}.");
        Pin(Hex(p[2]), p[3][0], Hex(p[4]), true, what);
    }

    // The pin must be on a known chip, in range, with the direction the
    // record needs. Each output pin may be claimed only once.
    void Pin(byte vaddr, char port, int bit, bool output, string what)
    {
        if (!_chips.TryGetValue(vaddr, out var chip)) { _problems.Add($"{what}: board 0x{vaddr:X2} is not in the hardware table."); return; }
        if (port is not ('A' or 'B') || bit > 7) { _problems.Add($"{what}: pin {port}{bit} does not exist."); return; }

        byte iodir = port == 'A' ? chip.IodirA : chip.IodirB;
        bool isInput = ((iodir >> bit) & 1) == 1;
        if (isInput == output) _problems.Add($"{what}: 0x{vaddr:X2} {port}{bit} is an {(isInput ? "input" : "output")} in the hardware table.");
        if (output && !_outputs.TryAdd((vaddr, port, bit), what)) _problems.Add($"{what}: 0x{vaddr:X2} {port}{bit} is already driven by {_outputs[(vaddr, port, bit)]}.");
    }

    static byte Hex(string token) => Convert.ToByte(token, 16);
}
