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

    // Ordered labels for svbSwitches, matching the slot order used on the wire:
    // every switch label (same order as BoardConfig.AllSwitchSlots()), then
    // "dreieskive" last.
    static IReadOnlyList<string> SvbSwitchLabelOrder() =>
        BoardConfig.AllSwitchSlots().Select(s => s.Label).Append("dreieskive").ToList();

    static string Hex(string? vaddrHexString)
    {
        if (vaddrHexString == null) return "00";
        byte b = Convert.ToByte(vaddrHexString, 16);
        return b.ToString("X2");
    }

    static string HexStr(string wireToken) => $"0x{Convert.ToByte(wireToken, 16):X2}";

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

        var svbLabels = SvbSwitchLabelOrder();
        for (int i = 0; i < svbLabels.Count; i++)
        {
            string label = svbLabels[i];
            if (!file.SvbSwitches.TryGetValue(label, out var sv) || sv.VAddr == null) continue;

            bool isDreieskive = label == "dreieskive";
            byte bitPrimary = (byte)(isDreieskive ? sv.BitCw ?? 0 : sv.Bit ?? 0);
            byte bitSecondary = (byte)(isDreieskive ? sv.BitCcw ?? 0xFF : 0xFF);
            byte targetIsDrei = (byte)(isDreieskive ? 1 : 0);
            byte targetSlot = (byte)(!isDreieskive && BoardConfig.TryFindSlotByLabel(label, out int s) ? s : 0xFF);

            lines.Add($"P {i} {Hex(sv.VAddr)} {bitPrimary:X2} {bitSecondary:X2} {targetIsDrei:X2} {targetSlot:X2}");
            svbSwitchCount++;
        }

        if (file.Dreieskive.MotorVAddr != null)
        {
            byte cwPol = (byte)(file.Dreieskive.CwPolarity ?? 0xFF);
            lines.Add($"D {Hex(file.Dreieskive.MotorVAddr)} {file.Dreieskive.MotorPinBase ?? 0:X2} {cwPol:X2}");
        }

        if (file.StatusLed.VAddr != null)
            lines.Add($"L {Hex(file.StatusLed.VAddr)} {file.StatusLed.Bit ?? 0:X2}");

        lines.Add($"F {file.FadeConfig.FadeMs:X4} {file.FadeConfig.FadeSteps:X2} {file.FadeConfig.PwmPeriodUs:X4}");

        return lines;
    }

    public static void ApplyDownloadLines(SystemConfigFile file, IEnumerable<string> lines)
    {
        var slotToLabel = BoardConfig.AllSwitchSlots().ToDictionary(s => s.Slot, s => s.Label);
        var svbLabels = SvbSwitchLabelOrder();

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
                    case "P" when parts.Length == 7:
                    {
                        int slot = int.Parse(parts[1]);
                        if (slot < 0 || slot >= svbLabels.Count) break;
                        string label = svbLabels[slot];
                        bool isDrei = label == "dreieskive";
                        var entry = new SvbSwitchEntry { VAddr = HexStr(parts[2]) };
                        if (isDrei)
                        {
                            entry.BitCw = Convert.ToInt32(parts[3], 16);
                            entry.BitCcw = Convert.ToInt32(parts[4], 16);
                        }
                        else
                        {
                            entry.Bit = Convert.ToInt32(parts[3], 16);
                        }
                        file.SvbSwitches[label] = entry;
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
                        file.StatusLed = new StatusLedEntry
                        {
                            VAddr = HexStr(parts[1]),
                            Bit = Convert.ToInt32(parts[2], 16),
                        };
                        break;
                    case "F" when parts.Length == 4:
                        file.FadeConfig = new FadeConfigEntry
                        {
                            FadeMs = Convert.ToInt32(parts[1], 16),
                            FadeSteps = Convert.ToInt32(parts[2], 16),
                            PwmPeriodUs = Convert.ToInt32(parts[3], 16),
                        };
                        break;
                }
            }
            catch (FormatException) { /* skip malformed line */ }
        }
    }
}
