using System.Globalization;

namespace DrammenMJKConfig;

// Typed wrapper around ArduinoConnection providing the Phase 1 low-level protocol.
// Raw discovery primitives (Mcp*) are used only by MotorScan, where the pin
// mapping is exactly what's being discovered. Switch-table commands (Drive/Read
// Switch) never mention port/bit -- firmware resolves that itself from the
// switch table. See PLAN_Phase1.md for the full protocol design.
sealed class ArduinoDevice : IArduinoDevice
{
    readonly ArduinoConnection _conn;

    public const byte Unset = 0xFF;

    // EEPROM region addresses (must match Firmware.ino exactly). Dreieskive
    // motor side is declared, not scanned, and stays a single global slot --
    // there's exactly one dreieskive for the whole site (FCSBR only).
    public const int AddrDreieskiveVAddr        = 0x02;
    public const int AddrDreieskiveMotorPinBase = 0x03;
    public const int AddrDreieskiveCwPolarity   = 0x04;
    // Status LED has no fixed address here -- Firmware.ino resolves it
    // per-board via board_find_slot(), so it's set through SCU's "L" line
    // (see SetStatusLed below), not a direct EW write.

    // Switch table — 32-slot capacity, parallel byte arrays.
    public const int RegionSlotMotorVAddr    = 0x10;
    public const int RegionSlotMotorBit      = 0x30;
    public const int RegionSlotPolarity      = 0x50;
    public const int RegionSlotFeedbackRett  = 0x70;
    public const int RegionSlotFeedbackAvvik = 0x90;
    public const int RegionSlotFeedbackVAddr = 0x100;

    public ArduinoDevice(ArduinoConnection conn) => _conn = conn;

    // Send a line command and capture one response line.
    string? Ask(string cmd, int timeoutMs = 500)
    {
        _conn.StartCapture();
        try
        {
            _conn.SendLine(cmd);
            return _conn.GetCapturedLine(timeoutMs)?.Trim();
        }
        finally { _conn.StopCapture(); }
    }

    // -------------------------------------------------------------------------
    // Connection
    // -------------------------------------------------------------------------

    public bool Ping() => Ask("PING", 1000) == "PONG";

    // Minimal sanity check -- Phase 1 firmware has no site-specific fields to
    // report (no compiled-in board topology), so this just confirms a sane reply.
    public bool Init()
    {
        string? resp = Ask("SI", 1000);
        return resp != null && resp.StartsWith("SITE", StringComparison.Ordinal);
    }

    // Test/Prod mode (see Firmware.ino's ADDR_MODE). Test (the default,
    // including a blank/erased EEPROM byte) never touches the TWI peripheral
    // -- SDA/SCL stay plain pulled-up inputs -- so bench diagnostics (I2CD
    // etc.) can safely characterize a new install's wiring before anything
    // is allowed to actually drive the bus. Prod fires up I2C immediately at
    // boot, same as before this gate existed. Returns null on no response.
    public bool? IsProdMode()
    {
        string? resp = Ask("SI", 1000);
        if (resp == null) return null;
        if (resp.EndsWith("MODE P", StringComparison.Ordinal)) return true;
        if (resp.EndsWith("MODE T", StringComparison.Ordinal)) return false;
        return null;
    }

    // Only writes the stored EEPROM byte -- takes effect on the next reset
    // (RST), never live, so a mode change can't interrupt a bring-up
    // sequence or a switch mid-throw. Caller is responsible for telling the
    // user to reset/power-cycle.
    public bool SetMode(bool prod) => Ask($"SETMODE {(prod ? 'P' : 'T')}") == "OK";

    // -------------------------------------------------------------------------
    // Virtual addressing — vaddr = bus*0x10 + real_i2c_address
    // -------------------------------------------------------------------------
    public static byte VirtualAddress(int bus, byte realAddr) => (byte)(bus * 0x10 + realAddr);

    // -------------------------------------------------------------------------
    // EEPROM
    // -------------------------------------------------------------------------

    // Null on no/garbled response -- never Unset, so a dropped read can't
    // pass for an unconfigured byte.
    public byte? EepromRead(int addr)
    {
        string? resp = Ask($"ER {addr:X2}");
        if (resp != null && byte.TryParse(resp, NumberStyles.HexNumber, null, out byte val))
            return val;
        return null;
    }

    public bool EepromWrite(int addr, byte val) =>
        Ask($"EW {addr:X2} {val:X2}") == "OK";

    public bool EepromErase() => Ask("EC", 3000) == "OK";

    // -------------------------------------------------------------------------
    // Raw discovery primitives — MotorScan only; not used at runtime.
    // -------------------------------------------------------------------------

    public bool McpSetDirection(byte vaddr, char port, byte mask) =>
        Ask($"MDIR {vaddr:X2} {port} {mask:X2}") == "OK";

    public bool McpSetPullup(byte vaddr, char port, byte mask) =>
        Ask($"MPU {vaddr:X2} {port} {mask:X2}") == "OK";

    public bool McpWritePort(byte vaddr, char port, byte val) =>
        Ask($"MW {vaddr:X2} {port} {val:X2}") == "OK";

    // Returns -1 on error.
    public int McpReadPort(byte vaddr, char port)
    {
        string? resp = Ask($"MR {vaddr:X2} {port}");
        if (resp != null && byte.TryParse(resp, NumberStyles.HexNumber, null, out byte val))
            return val;
        return -1;
    }

    // Generic MCP register read (MRR). Diagnostic only -- lets a bring-up
    // tool read IODIR/OLAT/GPPU to confirm a pin's configured direction and
    // latch, not just its live GPIO level. Common regs (BANK=0): IODIRA 0x00,
    // IODIRB 0x01, GPPUA 0x0C, OLATA 0x14, OLATB 0x15. Returns -1 on error.
    public int McpReadRegister(byte vaddr, byte reg)
    {
        string? resp = Ask($"MRR {vaddr:X2} {reg:X2}");
        if (resp != null && byte.TryParse(resp, NumberStyles.HexNumber, null, out byte val))
            return val;
        return -1;
    }

    // SDA/SCL bus diagnostic (I2CD). The firmware briefly detaches the TWI
    // peripheral and samples both lines as plain GPIO over ~1s per phase --
    // first with the AVR's internal pull-up enabled, then with it disabled
    // -- so a cable run that worked on the bench (short cable) but not once
    // installed can be told apart from a bad board: steady high both ways is
    // healthy; high only with the internal pull-up means no external
    // pull-up is reaching the Arduino; steady low both ways is a short to
    // GND; a mix is a floating/broken wire. Also carries min/max
    // analogRead()-derived millivolts per phase, sampled across that same
    // second -- supplemental to the high/low counts: a weak/resistive
    // connection reads a narrow band well below the ~4.7-5.1V a healthy line
    // shows, while a wide swing (picking up mains hum) is the signature of a
    // genuinely open/floating wire even on samples that happened to read
    // digitally high throughout. Finally, for the no-pull-up phase, the
    // transition count and min/max gap (ms) between consecutive digital
    // transitions -- piggybacked on the same 1ms-spaced samples used for the
    // high-count, so every gap is directly in ms. A real 50Hz pickup crosses
    // the logic threshold roughly every ~10ms fairly consistently (narrow
    // min/max gap); an intermittent mechanical connection shows irregular,
    // unrelated gaps instead. Returns null on no response.
    public sealed record I2cDiagResult(
        int SdaHighWithPullup, int SdaHighNoPullup, int SclHighWithPullup, int SclHighNoPullup, int Samples,
        int SdaMvPuMin, int SdaMvPuMax, int SdaMvNpMin, int SdaMvNpMax,
        int SclMvPuMin, int SclMvPuMax, int SclMvNpMin, int SclMvNpMax,
        int SdaNpTransitions, int SdaNpGapMinMs, int SdaNpGapMaxMs,
        int SclNpTransitions, int SclNpGapMinMs, int SclNpGapMaxMs);

    public I2cDiagResult? I2CDiagnostic()
    {
        // ~2s of firmware-side sampling (two ~1s phases) plus serial margin.
        string? resp = Ask("I2CD", 5000);
        if (resp == null) return null;
        var p = resp.Split(' ');
        if (p.Length != 19) return null;
        var v = new int[19];
        for (int i = 0; i < 19; i++)
            if (!int.TryParse(p[i], out v[i])) return null;
        return new I2cDiagResult(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12],
            v[13], v[14], v[15], v[16], v[17], v[18]);
    }

    // Returns the changed byte value, or -1 on timeout/error.
    public int McpPollChange(byte vaddr, char port, byte baseline, int timeoutMs)
    {
        string? resp = Ask($"MPOLL {vaddr:X2} {port} {baseline:X2} {timeoutMs}", timeoutMs + 1500);
        if (resp == null || resp == "TIMEOUT") return -1;
        var p = resp.Split(' ');
        if (p.Length == 2 && p[0] == "CHANGED" && byte.TryParse(p[1], NumberStyles.HexNumber, null, out byte val))
            return val;
        return -1;
    }

    public bool McpSetBit(byte vaddr, char port, int bit, bool val) =>
        Ask($"MBIT {vaddr:X2} {port} {bit} {(val ? 1 : 0)}") == "OK";

    // -------------------------------------------------------------------------
    // Switch-table read — feedback position of a scanned pens (SR). Driving a
    // pens is just its one motor bit (McpSetBit); nothing waits on feedback
    // outside Motor scan.
    // -------------------------------------------------------------------------

    public enum SwitchState { Rett, Avvik, Between, Fault, Error }

    public SwitchState ReadSwitch(int slot)
    {
        string? resp = Ask($"SR {slot}");
        if (resp == null || resp.StartsWith("ERR", StringComparison.Ordinal)) return SwitchState.Error;
        return ParseState(resp);
    }

    static SwitchState ParseState(string s) => s switch
    {
        "RETT" => SwitchState.Rett,
        "AVVIK" => SwitchState.Avvik,
        "BETWEEN" => SwitchState.Between,
        "FAULT" => SwitchState.Fault,
        _ => SwitchState.Error,
    };

    // -------------------------------------------------------------------------
    // Bulk transfer: HWU (hardware.json) and SCU (SystemConfig.json) share the
    // same READY / line-OK / END-STORED handshake.
    // -------------------------------------------------------------------------

    // Null on success, else what failed.
    string? BulkSend(string command, IEnumerable<string> lines)
    {
        _conn.StartCapture();
        try
        {
            _conn.SendLine(command);
            if (_conn.GetCapturedLine(1500)?.Trim() != "READY") return $"no READY after {command}";
            foreach (string line in lines)
            {
                _conn.SendLine(line);
                if (_conn.GetCapturedLine(1500)?.Trim() != "OK") return $"line '{line}' not accepted";
            }
            _conn.SendLine("END");
            return _conn.GetCapturedLine(2000)?.Trim() == "STORED" ? null : "storage not confirmed";
        }
        finally { _conn.StopCapture(); }
    }

    public string? HardwareConfigSend(IEnumerable<HardwareConfig.ResolvedChip> chips) =>
        BulkSend("HWU", chips.Select(c => $"{c.VAddr:X2} {c.IodirA:X2} {c.IodirB:X2} {c.GppuA:X2} {c.GppuB:X2}"));

    // Board hardware table rows (HWD). Null on timeout or if any row doesn't
    // parse -- a partial table must never pass for the whole one. Reads on
    // to END either way so leftover rows can't leak into the next command.
    public List<HardwareConfig.ResolvedChip>? HardwareConfigDownload()
    {
        var rows = new List<HardwareConfig.ResolvedChip>();
        bool bad = false;
        _conn.StartCapture();
        try
        {
            _conn.SendLine("HWD");
            while (true)
            {
                string? line = _conn.GetCapturedLine(2000)?.Trim();
                if (line == null) return null;
                if (line == "END") return bad ? null : rows;
                if (HardwareConfig.ResolvedChip.Parse(line) is { } chip) rows.Add(chip);
                else bad = true;
            }
        }
        finally { _conn.StopCapture(); }
    }

    public string? SystemConfigSend(IEnumerable<string> lines) => BulkSend("SCU", lines);

    // Single-line SCU sessions declaring one board fact. Firmware resolves
    // the EEPROM slot itself via board_find_slot(vaddr) -- the board must
    // already be in the hardware table (hardware.json uploaded) or these fail.
    public bool SetStatusLed(byte vaddr, int bit) =>
        SystemConfigSend([$"L {vaddr:X2} {bit:X2}"]) == null;

    public bool SetSignal(byte vaddr, int redBit, int green1Bit, int green2Bit) =>
        SystemConfigSend([$"G {vaddr:X2} {redBit:X2} {green1Bit:X2} {green2Bit:X2}"]) == null;

    public bool SetInverterEnable(byte vaddr, char port, int bit) =>
        SystemConfigSend([$"V {vaddr:X2} {port} {bit:X2}"]) == null;

    public bool SetTrackDetection(byte vaddr, int bit, bool activeHigh) =>
        SystemConfigSend([$"T {vaddr:X2} {bit:X2} {(activeHigh ? 1 : 0):X2}"]) == null;

    // Whole SCD dump as lines (without the END marker). Null on timeout.
    public List<string>? SystemConfigDownload()
    {
        var lines = new List<string>();
        _conn.StartCapture();
        try
        {
            _conn.SendLine("SCD");
            while (true)
            {
                string? line = _conn.GetCapturedLine(2000)?.Trim();
                if (line == null) return null;
                if (line == "END") return lines;
                lines.Add(line);
            }
        }
        finally { _conn.StopCapture(); }
    }

    // -------------------------------------------------------------------------
    // Misc
    // -------------------------------------------------------------------------

    public void SoftReset() => _conn.SendLine("RST");
}
