using System.Text.Json;

namespace DrammenMJKConfig;

// Converts between SystemConfig.json (label-keyed, human-facing) and the
// SCU/SCD wire lines (slot-keyed, firmware-facing). C# owns this translation
// so the file itself never needs to know about EEPROM slot numbers.
static class SystemConfigJson
{
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static SystemConfigFile Load(string path) =>
        JsonSerializer.Deserialize<SystemConfigFile>(File.ReadAllText(path), ReadOptions)
        ?? new SystemConfigFile();

    public static void Save(SystemConfigFile file, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, WriteOptions));

    public const string Dreieskive = "dreieskive";
    public const string AskGreen = "askGreen";
    public const string SignalTarget = "signal";

    // Ordered labels for svbSwitches, matching the slot order used on the wire:
    // every switch label (same order as BoardConfig.AllSwitchSlots()), then
    // "dreieskive", then "askGreen".
    public static IReadOnlyList<string> SvbSwitchLabelOrder() =>
        BoardConfig.AllSwitchSlots().Select(s => s.Label).Append(Dreieskive).Append(AskGreen).ToList();

    // Panel LED role byte on the wire/in EEPROM <-> name in SystemConfig.json.
    public static readonly string[] LedRoles = ["rett", "avvik", "red", "green1", "green2"];

    static string Hex(string? vaddrHexString)
    {
        if (vaddrHexString == null) return "00";
        byte b = Convert.ToByte(vaddrHexString, 16);
        return b.ToString("X2");
    }

    static string HexStr(string wireToken) => $"0x{Convert.ToByte(wireToken, 16):X2}";

    // Shared by L (status LED) and G (signal) download handling -- both key
    // their SystemConfig.json entry by board name, resolved from the wire
    // line's vaddr via BoardConfig, same as HWD's board rows do implicitly.
    static string? BoardNameForVAddr(byte vaddr) =>
        BoardConfig.Svb.Scbs.FirstOrDefault(b => b.VirtualAddress == vaddr)?.Name;

    public static List<string> BuildUploadLines(SystemConfigFile file, out int switchCount, out int svbSwitchCount)
    {
        var lines = new List<string>();
        switchCount = 0;
        svbSwitchCount = 0;

        foreach (var (_, _, label, slot) in BoardConfig.AllSwitchSlots())
        {
            if (!file.Switches.TryGetValue(label, out var sw) || !sw.IsConfigured) continue;
            lines.Add($"S {slot} {Hex(sw.MotorVAddr)} {sw.MotorBit:X2} {sw.Polarity:X2} {Hex(sw.FeedbackVAddr)} {sw.FeedbackRettBit:X2} {sw.FeedbackAvvikBit:X2}");
            switchCount++;
        }

        var svbLines = SvbSwitchLines(file.SvbSwitches);
        svbSwitchCount = svbLines.Count;
        lines.AddRange(svbLines);
        lines.AddRange(PanelLedLines(file.PanelLeds));

        if (file.Dreieskive.MotorVAddr != null)
        {
            byte cwPol = (byte)(file.Dreieskive.CwPolarity ?? 0xFF);
            lines.Add($"D {Hex(file.Dreieskive.MotorVAddr)} {file.Dreieskive.MotorPinBase ?? 0:X2} {cwPol:X2}");
        }

        foreach (var led in file.StatusLeds.Values)
        {
            if (led.VAddr == null) continue;
            lines.Add($"L {Hex(led.VAddr)} {led.Bit ?? 0:X2}");
        }

        foreach (var signal in file.Signals.Values)
        {
            if (signal.VAddr == null) continue;
            lines.Add($"G {Hex(signal.VAddr)} {signal.RedBit ?? 0:X2} {signal.Green1Bit ?? 0:X2} {signal.Green2Bit ?? 0:X2}");
        }

        foreach (var inv in file.InverterEnables.Values)
        {
            if (inv.VAddr == null) continue;
            lines.Add($"V {Hex(inv.VAddr)} {inv.Port ?? "A"} {inv.Bit ?? 0:X2}");
        }

        foreach (var track in file.TrackDetections.Values)
        {
            if (track.VAddr == null) continue;
            lines.Add($"T {Hex(track.VAddr)} {track.Bit ?? 0:X2} {(track.ActiveHigh == true ? 1 : 0):X2}");
        }

        return lines;
    }

    // One "P" line per configured panel input:
    // P <slot> <vaddr> <port> <bitPri> <bitSec> <kind> <targetSlot> <pol>
    // kind 0 = pens, 1 = dreieskive (bitPri CW, bitSec CCW), 2 = green request.
    public static List<string> SvbSwitchLines(IReadOnlyDictionary<string, SvbSwitchEntry> svbSwitches)
    {
        var lines = new List<string>();
        var svbLabels = SvbSwitchLabelOrder();
        for (int i = 0; i < svbLabels.Count; i++)
        {
            string label = svbLabels[i];
            if (!svbSwitches.TryGetValue(label, out var sv) || sv.VAddr == null) continue;

            bool isDreieskive = label == Dreieskive;
            byte bitPrimary = (byte)(isDreieskive ? sv.BitCw ?? 0xFF : sv.Bit ?? 0);
            byte bitSecondary = (byte)(isDreieskive ? sv.BitCcw ?? 0xFF : 0xFF);
            byte kind = (byte)(isDreieskive ? 1 : label == AskGreen ? 2 : 0);
            byte targetSlot = (byte)(kind == 0 && BoardConfig.TryFindSlotByLabel(label, out int s) ? s : 0xFF);
            byte polarity = (byte)(kind == 0 ? sv.Polarity ?? 0xFF : 0xFF);

            lines.Add($"P {i} {Hex(sv.VAddr)} {sv.Port ?? "A"} {bitPrimary:X2} {bitSecondary:X2} {kind:X2} {targetSlot:X2} {polarity:X2}");
        }
        return lines;
    }

    // "Z" (clear the board's LED table) then one "Q" per LED, so a restore
    // leaves exactly the file's LEDs -- none left over from an earlier scan.
    // Nothing at all when the file has no LEDs, so an older SystemConfig.json
    // without panelLeds doesn't wipe the board's table.
    public static List<string> PanelLedLines(IReadOnlyList<PanelLedEntry> leds)
    {
        var lines = new List<string>();
        if (leds.Count == 0) return lines;
        lines.Add("Z");
        for (int i = 0; i < leds.Count; i++)
        {
            var led = leds[i];
            byte target = (byte)(led.Target != SignalTarget && BoardConfig.TryFindSlotByLabel(led.Target ?? "", out int s) ? s : 0xFF);
            int role = Array.IndexOf(LedRoles, led.Role);
            lines.Add($"Q {i} {Hex(led.VAddr)} {led.Port ?? "A"} {led.Bit ?? 0:X2} {target:X2} {role:X2}");
        }
        return lines;
    }

    public static void ApplyDownloadLines(SystemConfigFile file, IEnumerable<string> lines)
    {
        var slotToLabel = BoardConfig.AllSwitchSlots().ToDictionary(s => s.Slot, s => s.Label);
        var svbLabels = SvbSwitchLabelOrder();
        bool clearedLeds = false; // the board's LED table replaces the file's, not appends to it

        foreach (string line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            try
            {
                switch (parts[0])
                {
                    case "S" when parts.Length == 8:
                    {
                        int slot = int.Parse(parts[1]);
                        if (!slotToLabel.TryGetValue(slot, out string? label)) break;
                        file.Switches[label] = new SwitchEntry
                        {
                            MotorVAddr = HexStr(parts[2]),
                            MotorBit = Convert.ToInt32(parts[3], 16),
                            Polarity = Convert.ToInt32(parts[4], 16),
                            FeedbackVAddr = HexStr(parts[5]),
                            FeedbackRettBit = Convert.ToInt32(parts[6], 16),
                            FeedbackAvvikBit = Convert.ToInt32(parts[7], 16),
                        };
                        break;
                    }
                    case "P" when parts.Length == 9:
                    {
                        // P <slot> <vaddr> <port> <bitPri> <bitSec> <kind> <targetSlot> <pol>
                        int slot = int.Parse(parts[1]);
                        if (slot < 0 || slot >= svbLabels.Count) break;
                        string label = svbLabels[slot];
                        var entry = new SvbSwitchEntry { VAddr = HexStr(parts[2]), Port = parts[3] };
                        if (label == Dreieskive)
                        {
                            if (parts[4] != "FF") entry.BitCw = Convert.ToInt32(parts[4], 16);
                            if (parts[5] != "FF") entry.BitCcw = Convert.ToInt32(parts[5], 16);
                        }
                        else
                        {
                            entry.Bit = Convert.ToInt32(parts[4], 16);
                            if (parts[8] != "FF") entry.Polarity = Convert.ToInt32(parts[8], 16);
                        }
                        file.SvbSwitches[label] = entry;
                        break;
                    }
                    case "Q" when parts.Length == 7:
                    {
                        // Q <idx> <vaddr> <port> <bit> <targetSlot> <role>
                        int role = Convert.ToInt32(parts[6], 16);
                        if (role >= LedRoles.Length) break;
                        string? target = parts[5] == "FF" ? SignalTarget
                            : slotToLabel.GetValueOrDefault(Convert.ToInt32(parts[5], 16));
                        if (target == null) break;
                        if (!clearedLeds) { file.PanelLeds.Clear(); clearedLeds = true; }
                        file.PanelLeds.Add(new PanelLedEntry
                        {
                            VAddr = HexStr(parts[2]),
                            Port = parts[3],
                            Bit = Convert.ToInt32(parts[4], 16),
                            Target = target,
                            Role = LedRoles[role],
                        });
                        break;
                    }
                    case "D" when parts.Length == 4:
                        file.Dreieskive = new DreieskiveEntry
                        {
                            MotorVAddr = HexStr(parts[1]),
                            MotorPinBase = Convert.ToInt32(parts[2], 16),
                            CwPolarity = parts[3] == "FF" ? null : Convert.ToInt32(parts[3], 16),
                        };
                        break;
                    case "L" when parts.Length == 3:
                    {
                        string? boardName = BoardNameForVAddr(Convert.ToByte(parts[1], 16));
                        if (boardName == null) break; // vaddr not in BoardConfig -- nowhere to key this by name
                        file.StatusLeds[boardName] = new StatusLedEntry
                        {
                            VAddr = HexStr(parts[1]),
                            Bit = Convert.ToInt32(parts[2], 16),
                        };
                        break;
                    }
                    case "G" when parts.Length == 5:
                    {
                        string? boardName = BoardNameForVAddr(Convert.ToByte(parts[1], 16));
                        if (boardName == null) break;
                        file.Signals[boardName] = new SignalEntry
                        {
                            VAddr = HexStr(parts[1]),
                            RedBit = Convert.ToInt32(parts[2], 16),
                            Green1Bit = Convert.ToInt32(parts[3], 16),
                            Green2Bit = Convert.ToInt32(parts[4], 16),
                        };
                        break;
                    }
                    case "V" when parts.Length == 4:
                    {
                        string? boardName = BoardNameForVAddr(Convert.ToByte(parts[1], 16));
                        if (boardName == null) break;
                        file.InverterEnables[boardName] = new InverterEnableEntry
                        {
                            VAddr = HexStr(parts[1]),
                            Port = parts[2],
                            Bit = Convert.ToInt32(parts[3], 16),
                        };
                        break;
                    }
                    case "T" when parts.Length == 4:
                    {
                        string? boardName = BoardNameForVAddr(Convert.ToByte(parts[1], 16));
                        if (boardName == null) break;
                        file.TrackDetections[boardName] = new TrackDetectionEntry
                        {
                            VAddr = HexStr(parts[1]),
                            Bit = Convert.ToInt32(parts[2], 16),
                            ActiveHigh = parts[3] != "00",
                        };
                        break;
                    }
                }
            }
            catch (FormatException) { /* skip malformed line */ }
        }
    }
}
