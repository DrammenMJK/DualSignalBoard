using System.Text.Json.Serialization;

namespace DrammenMJKConfig;

sealed class SwitchEntry
{
    [JsonPropertyName("motorVAddr")] public string? MotorVAddr { get; set; }
    [JsonPropertyName("motorBit")] public int? MotorBit { get; set; }
    [JsonPropertyName("polarity")] public int? Polarity { get; set; }
    [JsonPropertyName("feedbackVAddr")] public string? FeedbackVAddr { get; set; }
    [JsonPropertyName("feedbackRettBit")] public int? FeedbackRettBit { get; set; }
    [JsonPropertyName("feedbackAvvikBit")] public int? FeedbackAvvikBit { get; set; }

    [JsonIgnore] public bool IsConfigured => MotorVAddr != null;
}

// Dreieskive's motor side only (bits 4-5 on the SCB) -- see PLAN_Phase1.md for
// why the SVB control-switch side lives in svbSwitches["dreieskive"] instead.
sealed class DreieskiveEntry
{
    [JsonPropertyName("motorVAddr")] public string? MotorVAddr { get; set; }
    [JsonPropertyName("motorPinBase")] public int? MotorPinBase { get; set; }
    [JsonPropertyName("cwPolarity")] public int? CwPolarity { get; set; }
}

// One board's status LED. Keyed by board name in SystemConfigFile.StatusLeds
// -- was a single top-level entry until FCSBL showed up with its own status
// LED distinct from FCSBR's (EEPROM followed the same change, see
// Firmware.ino's REGION_LED_BIT).
sealed class StatusLedEntry
{
    [JsonPropertyName("vaddr")] public string? VAddr { get; set; }
    [JsonPropertyName("bit")] public int? Bit { get; set; }
}

// One SVB panel switch. `Bit` for a plain on/off switch; `BitCw`/`BitCcw` for
// the dreieskive's 3-position toggle (the "dreieskive" key only).
sealed class SvbSwitchEntry
{
    [JsonPropertyName("vaddr")] public string? VAddr { get; set; }
    [JsonPropertyName("bit")] public int? Bit { get; set; }
    [JsonPropertyName("bitCw")] public int? BitCw { get; set; }
    [JsonPropertyName("bitCcw")] public int? BitCcw { get; set; }
}

sealed class SignalEntry
{
    [JsonPropertyName("vaddr")] public string? VAddr { get; set; }
    [JsonPropertyName("redBit")] public int? RedBit { get; set; }
    [JsonPropertyName("green1Bit")] public int? Green1Bit { get; set; }
    [JsonPropertyName("green2Bit")] public int? Green2Bit { get; set; }
}

// Keyed by board name in SystemConfigFile.TrackDetections -- was a single
// top-level entry before FCSBL's track detection made "one per board"
// necessary, same reasoning as StatusLeds.
sealed class TrackDetectionEntry
{
    [JsonPropertyName("vaddr")] public string? VAddr { get; set; }
    [JsonPropertyName("bit")] public int? Bit { get; set; }
    [JsonPropertyName("activeHigh")] public bool? ActiveHigh { get; set; }
}

// One board's inverter-enable pin -- declared (from boards.json), not
// discovered, but mirrored here like everything else in SystemConfig.json
// for visibility/round-trip. Keyed by board name.
sealed class InverterEnableEntry
{
    [JsonPropertyName("vaddr")] public string? VAddr { get; set; }
    [JsonPropertyName("port")] public string? Port { get; set; } // "A" or "B"
    [JsonPropertyName("bit")] public int? Bit { get; set; }
}

sealed class SystemConfigFile
{
    [JsonPropertyName("_todo")] public List<string> Todo { get; set; } = new();
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("svb")] public string Svb { get; set; } = "";
    [JsonPropertyName("switches")] public Dictionary<string, SwitchEntry> Switches { get; set; } = new();
    [JsonPropertyName("dreieskive")] public DreieskiveEntry Dreieskive { get; set; } = new();
    [JsonPropertyName("statusLeds")] public Dictionary<string, StatusLedEntry> StatusLeds { get; set; } = new();
    [JsonPropertyName("svbSwitches")] public Dictionary<string, SvbSwitchEntry> SvbSwitches { get; set; } = new();
    [JsonPropertyName("signals")] public Dictionary<string, SignalEntry> Signals { get; set; } = new();
    [JsonPropertyName("trackDetection")] public Dictionary<string, TrackDetectionEntry> TrackDetections { get; set; } = new();
    [JsonPropertyName("inverterEnables")] public Dictionary<string, InverterEnableEntry> InverterEnables { get; set; } = new();
}
