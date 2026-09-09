using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrammenMJKConfig;

// ---------------------------------------------------------------------------
// boards.json DTOs. Declares the site's board topology (name, category,
// production bus, chip real addresses, switches, dreieskive/status-LED
// presence) as data instead of hardcoding it -- see PLAN_Phase3.md, "Site
// scale: 9 boards, moving BoardConfig.cs to JSON".
// ---------------------------------------------------------------------------

sealed class DreieskiveDeclaration
{
    [JsonPropertyName("base")] public int Base { get; set; }
    [JsonPropertyName("upper")] public int Upper { get; set; }
}

// Some one-bit-drive boards need an external inverter actively enabled after
// bring-up (see PLAN_Phase3.md, "Motor drive"). A fully-known hardware fact
// once wired, not something needing interactive discovery, so -- unlike
// signal lamp bits or track detection polarity -- it's declared here and
// pushed automatically during MotorScan's declare-facts step, same as
// dreieskive/statusLedBit.
sealed class InverterEnableDeclaration
{
    [JsonPropertyName("port")] public string Port { get; set; } = "A";
    [JsonPropertyName("bit")] public int Bit { get; set; }
}

// One physical board. Category is SCB (drives switch motors), SVB (reads
// operator panel switches only), or Combined (both, on the same vaddr --
// declared as separate SCB- and SVB-shaped entries sharing chips, per the
// site owner). Only SCB/Combined are consumed into BoardScb today; SVB-only
// boards are kept in AllBoards for visibility but have no functional
// scan/command support yet.
sealed class BoardDeclaration
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "SCB";
    [JsonPropertyName("bus")] public int Bus { get; set; } // production bus -- ignored when addressMode=test
    [JsonPropertyName("chips")] public List<string> Chips { get; set; } = new(); // real I2C addresses, e.g. "0x20"
    // Motor bit per switch, in the SAME order as `switches`. Most boards'
    // motor bits are consecutive from a start (motorBitFirst is enough), but
    // that's not universal -- Fossli Venstre's motors and signal lamps are
    // interleaved on Port B (motors on 1,3,5; signals on 0,2,4), so a single
    // "first" value can't represent it. If `motorBits` is given it's used
    // directly (must be the same length as `switches`); otherwise falls back
    // to motorBitFirst + index.
    [JsonPropertyName("motorBitFirst")] public int? MotorBitFirst { get; set; }
    [JsonPropertyName("motorBits")] public List<int>? MotorBits { get; set; }
    [JsonPropertyName("switches")] public List<string> Switches { get; set; } = new();
    [JsonPropertyName("dreieskive")] public DreieskiveDeclaration? Dreieskive { get; set; }
    [JsonPropertyName("statusLedBit")] public int? StatusLedBit { get; set; } // Port B assumed -- no board needs Port A yet
    [JsonPropertyName("inverterEnable")] public InverterEnableDeclaration? InverterEnable { get; set; }
    // Fixed hardware fact (which 3 Port B bits the signal lamps are wired
    // to, in any order -- unlike which lamp is which color, this doesn't
    // need observation, so it's declared rather than asked for by Signal
    // Scan each time). Always explicit, not a "first" convenience -- Fossli
    // Venstre's signal bits (0,2,4) aren't consecutive either.
    [JsonPropertyName("signalBits")] public List<int>? SignalBits { get; set; }
}

sealed class SvbDeclaration
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("number")] public int Number { get; set; }
}

sealed class BoardsFile
{
    [JsonPropertyName("_todo")] public List<string> Todo { get; set; } = new();
    // "test": every chip resolves as bus 0 regardless of declared `bus` --
    // matches bench reality (one board's chip(s) wired up at a time, no
    // mux). Different boards sharing real addresses is expected and
    // harmless in this mode, since they're never live simultaneously.
    // "prod": declared `bus` is used for real -- needs the I2C mux built.
    [JsonPropertyName("addressMode")] public string AddressMode { get; set; } = "test";
    [JsonPropertyName("svb")] public SvbDeclaration Svb { get; set; } = new();
    [JsonPropertyName("boards")] public List<BoardDeclaration> Boards { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Domain model -- what the rest of the app (ConfigSession, CommandSession,
// SystemConfigJson, ...) actually consumes. Resolved from BoardsFile by
// Load()/ApplyBoardsFile(), not hand-built, except in tests.
// ---------------------------------------------------------------------------

sealed record BoardSwitch(string Label);

// One SCB (or the SCB half of a Combined board): which switches Motor Scan
// expects to find on it, and its board-specific fixed facts (dreieskive
// pins, status LED) declared (not scanned) at the start of a Motor Scan
// session. Both are nullable -- most boards have neither.
//
// VirtualAddresses holds one resolved vaddr per chip (multi-chip boards list
// more than one, e.g. Havna Motors). VirtualAddress is a single-chip
// convenience shim -- MotorScan and the rest of the current functional code
// haven't been generalized to multi-chip operation yet (see PLAN_Phase3.md,
// "Multi-chip SCB support"), so they still only ever look at chip 0.
sealed record BoardScb(
    string Name,
    IReadOnlyList<byte> VirtualAddresses,
    int MotorBitFirst,
    IReadOnlyList<int>? MotorBits,
    IReadOnlyList<BoardSwitch> Switches,
    (int Base, int Upper)? DreieskivePins,
    (char Port, int Bit)? StatusLedPin,
    (char Port, int Bit)? InverterEnablePin,
    IReadOnlyList<int>? SignalBits
)
{
    public byte VirtualAddress => VirtualAddresses[0];

    // The one place that knows how to turn "switch index" into "motor bit" --
    // explicit MotorBits list if declared, else MotorBitFirst + index.
    // Centralized so MotorScan's firing loop and AllSwitchSlots() can't
    // silently diverge the way they did before this existed.
    public int MotorBitFor(int switchIndex) => MotorBits != null ? MotorBits[switchIndex] : MotorBitFirst + switchIndex;
}

// An SVB owns a *list* of SCBs, not a single one -- Fossli alone has two
// (Hoyre + Venstre), and the site has several more SVBs besides Fossli's.
sealed record BoardSvb(string Name, int Number, IReadOnlyList<BoardScb> Scbs);

static class BoardConfig
{
    static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BoardSvb Svb { get; private set; } =
        new BoardSvb("(not loaded)", 0, Array.Empty<BoardScb>());

    // Every declared board, including SVB-only ones -- not consumed into
    // Scbs, but kept for visibility (status views, future SVB support).
    public static IReadOnlyList<BoardDeclaration> AllBoards { get; private set; } = Array.Empty<BoardDeclaration>();

    public static void Load(string path)
    {
        var file = JsonSerializer.Deserialize<BoardsFile>(File.ReadAllText(path), ReadOptions) ?? new BoardsFile();
        ApplyBoardsFile(file);
    }

    // Split out from Load() so tests can build a BoardsFile in-memory
    // instead of needing a real file on disk (same pattern as
    // HardwareConfig.Flatten() taking an in-memory HardwareConfigFile).
    public static void ApplyBoardsFile(BoardsFile file)
    {
        bool testMode = file.AddressMode != "prod";

        var scbs = file.Boards
            .Where(b => b.Category is "SCB" or "Combined")
            .Select(b => ToBoardScb(b, testMode))
            .ToList();

        Svb = new BoardSvb(file.Svb.Name, file.Svb.Number, scbs);
        AllBoards = file.Boards;
    }

    static BoardScb ToBoardScb(BoardDeclaration b, bool testMode)
    {
        var vaddrs = b.Chips.Select(hex =>
        {
            byte realAddr = Convert.ToByte(hex, 16);
            int bus = testMode ? 0 : b.Bus;
            return ArduinoDevice.VirtualAddress(bus, realAddr);
        }).ToList();

        return new BoardScb(
            b.Name,
            vaddrs,
            b.MotorBitFirst ?? 0,
            b.MotorBits,
            b.Switches.Select(s => new BoardSwitch(s)).ToList(),
            b.Dreieskive is { } d ? (d.Base, d.Upper) : null,
            b.StatusLedBit is { } bit ? ('B', bit) : null,
            b.InverterEnable is { } inv ? (inv.Port[0], inv.Bit) : null,
            b.SignalBits
        );
    }

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
                yield return (scb, scb.MotorBitFor(i), scb.Switches[i].Label, slot);
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
