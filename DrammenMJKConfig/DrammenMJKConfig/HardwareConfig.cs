using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrammenMJKConfig;

// One MCP23017 chip: its virtual address and its two 8-element per-bit port
// arrays (index 0 = bit 0 .. index 7 = bit 7). Each element is "out", "in",
// "in-pu", or "tbd" (not yet determined -- must never reach HWU).
sealed class HwChip
{
    [JsonPropertyName("vaddr")] public string VAddr { get; set; } = "0x00";
    [JsonPropertyName("portA")] public string[] PortA { get; set; } = new string[8];
    [JsonPropertyName("portB")] public string[] PortB { get; set; } = new string[8];

    [JsonIgnore]
    public byte VAddrByte => Convert.ToByte(VAddr, 16);
}

// One physical board: 1 or 2 chips. No shared type templates -- every SCB and
// every SVB in this system has its own wiring, so each chip declares its own
// ports directly rather than inheriting from a "board type".
sealed class HwBoard
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "SCB"; // SCB / SVB / Combined
    [JsonPropertyName("chips")] public List<HwChip> Chips { get; set; } = new();
}

sealed class HardwareConfigFile
{
    [JsonPropertyName("_todo")] public List<string> Todo { get; set; } = new();
    [JsonPropertyName("boards")] public List<HwBoard> Boards { get; set; } = new();
}

static class HardwareConfig
{
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static HardwareConfigFile Load(string path) =>
        JsonSerializer.Deserialize<HardwareConfigFile>(File.ReadAllText(path), ReadOptions)
        ?? new HardwareConfigFile();

    public static void Save(HardwareConfigFile file, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, WriteOptions));

    // One flattened chip row, exactly what HWU sends over the wire.
    public readonly record struct ResolvedChip(byte VAddr, byte IodirA, byte IodirB, byte GppuA, byte GppuB, string BoardName);

    // Flattens every board's every chip into resolved register rows. Returns
    // null (with `error` set) if any chip still has a "tbd" entry -- a chip
    // with an unresolved bit must never be included in an HWU upload.
    public static List<ResolvedChip>? Flatten(HardwareConfigFile file, out string? error)
    {
        var result = new List<ResolvedChip>();
        foreach (var board in file.Boards)
        {
            foreach (var chip in board.Chips)
            {
                if (Array.IndexOf(chip.PortA, "tbd") >= 0 || Array.IndexOf(chip.PortB, "tbd") >= 0)
                {
                    error = $"Board '{board.Name}' chip {chip.VAddr}: has 'tbd' entries, cannot upload until resolved.";
                    return null;
                }

                byte iodirA = 0, gppuA = 0, iodirB = 0, gppuB = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    if (!ApplyBit(chip.PortA[bit], bit, ref iodirA, ref gppuA, out error) ||
                        !ApplyBit(chip.PortB[bit], bit, ref iodirB, ref gppuB, out error))
                    {
                        error = $"Board '{board.Name}' chip {chip.VAddr}: {error}";
                        return null;
                    }
                }
                result.Add(new ResolvedChip(chip.VAddrByte, iodirA, iodirB, gppuA, gppuB, board.Name));
            }
        }
        error = null;
        return result;
    }

    static bool ApplyBit(string value, int bit, ref byte iodir, ref byte gppu, out string? error)
    {
        switch (value)
        {
            case "out":
                break; // iodir bit stays 0 (output)
            case "in":
                iodir |= (byte)(1 << bit);
                break;
            case "in-pu":
                iodir |= (byte)(1 << bit);
                gppu |= (byte)(1 << bit);
                break;
            default:
                error = $"unknown port-bit value '{value}' at bit {bit}";
                return false;
        }
        error = null;
        return true;
    }
}
