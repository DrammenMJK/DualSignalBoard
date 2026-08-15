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

sealed class TrackDetectionEntry
{
    [JsonPropertyName("vaddr")] public string? VAddr { get; set; }
    [JsonPropertyName("bit")] public int? Bit { get; set; }
    [JsonPropertyName("activeHigh")] public bool? ActiveHigh { get; set; }
}

// Fade loop timing parameters -- config, not per-call state (see PLAN_Phase1.md,
// Signal lamp logic). Defaults are the original SignalLys_2X_Momentbrytere.ino values.
sealed class FadeConfigEntry
{
    [JsonPropertyName("fadeMs")] public int FadeMs { get; set; } = 1000;
    [JsonPropertyName("fadeSteps")] public int FadeSteps { get; set; } = 60;
    [JsonPropertyName("pwmPeriodUs")] public int PwmPeriodUs { get; set; } = 1000;
}

sealed class SystemConfigFile
{
    [JsonPropertyName("_todo")] public List<string> Todo { get; set; } = new();
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("svb")] public string Svb { get; set; } = "";
    [JsonPropertyName("switches")] public Dictionary<string, SwitchEntry> Switches { get; set; } = new();
    [JsonPropertyName("dreieskive")] public DreieskiveEntry Dreieskive { get; set; } = new();
    [JsonPropertyName("statusLed")] public StatusLedEntry StatusLed { get; set; } = new();
    [JsonPropertyName("svbSwitches")] public Dictionary<string, SvbSwitchEntry> SvbSwitches { get; set; } = new();
    [JsonPropertyName("signals")] public Dictionary<string, SignalEntry> Signals { get; set; } = new();
    [JsonPropertyName("trackDetection")] public TrackDetectionEntry TrackDetection { get; set; } = new();
    [JsonPropertyName("fadeConfig")] public FadeConfigEntry FadeConfig { get; set; } = new();
}
