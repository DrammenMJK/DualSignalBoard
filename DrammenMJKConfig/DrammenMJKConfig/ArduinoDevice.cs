using System.Globalization;

namespace DrammenMJKConfig;

// Typed wrapper around ArduinoConnection providing the Phase 1 low-level protocol.
// Raw discovery primitives (Mcp*) are used only by MotorScan, where the pin
// mapping is exactly what's being discovered. Switch-table commands (Drive/Read
// Switch) never mention port/bit -- firmware resolves that itself from the
// switch table. See PLAN_Phase1.md for the full protocol design.
sealed class ArduinoDevice
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

    public byte EepromRead(int addr)
    {
        string? resp = Ask($"ER {addr:X2}");
        if (resp != null && byte.TryParse(resp, NumberStyles.HexNumber, null, out byte val))
            return val;
        return Unset;
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
    // Switch-table commands — used by Command mode. Never mention port/bit.
    // -------------------------------------------------------------------------

    public enum SwitchState { Rett, Avvik, Between, Fault, Timeout, Error }

    public SwitchState DriveSwitch(int slot, char pos, int timeoutMs)
    {
        string? resp = Ask($"SW {slot} {pos} {timeoutMs}", timeoutMs + 1500);
        if (resp == null) return SwitchState.Error;
        if (resp == "TIMEOUT") return SwitchState.Timeout;
        if (resp.StartsWith("ERR", StringComparison.Ordinal)) return SwitchState.Error;
        var p = resp.Split(' ');
        return p.Length == 2 && p[0] == "OK" ? ParseState(p[1]) : SwitchState.Error;
    }

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
    // Board hardware table bulk transfer (HWU/HWD) — hardware.json
    // -------------------------------------------------------------------------

    public bool HardwareConfigUploadStart()
    {
        _conn.StartCapture();
        _conn.SendLine("HWU");
        string? ack = _conn.GetCapturedLine(1500)?.Trim();
        if (ack != "READY") { _conn.StopCapture(); return false; }
        return true;
    }

    public bool HardwareConfigSendLine(string line)
    {
        _conn.SendLine(line);
        return _conn.GetCapturedLine(1500)?.Trim() == "OK";
    }

    public bool HardwareConfigUploadFinish()
    {
        _conn.SendLine("END");
        bool result = _conn.GetCapturedLine(2000)?.Trim() == "STORED";
        _conn.StopCapture();
        return result;
    }

    public void HardwareConfigUploadAbort() => _conn.StopCapture();

    public bool HardwareConfigDownloadStart()
    {
        _conn.StartCapture();
        _conn.SendLine("HWD");
        return true;
    }

    public string? GetNextHardwareLine(int timeoutMs = 2000) =>
        _conn.GetCapturedLine(timeoutMs)?.Trim();

    public void HardwareConfigDownloadFinish() => _conn.StopCapture();

    // -------------------------------------------------------------------------
    // System-config bulk transfer (SCU/SCD) — SystemConfig.json
    // -------------------------------------------------------------------------

    public bool SystemConfigUploadStart()
    {
        _conn.StartCapture();
        _conn.SendLine("SCU");
        string? ack = _conn.GetCapturedLine(1500)?.Trim();
        if (ack != "READY") { _conn.StopCapture(); return false; }
        return true;
    }

    public bool SystemConfigSendLine(string line)
    {
        _conn.SendLine(line);
        return _conn.GetCapturedLine(1500)?.Trim() == "OK";
    }

    public bool SystemConfigUploadFinish()
    {
        _conn.SendLine("END");
        bool result = _conn.GetCapturedLine(2000)?.Trim() == "STORED";
        _conn.StopCapture();
        return result;
    }

    public void SystemConfigUploadAbort() => _conn.StopCapture();

    // Declares one board's status LED (vaddr + bit) via a single-line SCU
    // session. Firmware resolves which EEPROM slot that is itself via
    // board_find_slot(vaddr) -- the board must already be in the hardware
    // table (hardware.json uploaded) or this fails.
    public bool SetStatusLed(byte vaddr, int bit)
    {
        if (!SystemConfigUploadStart()) return false;
        if (!SystemConfigSendLine($"L {vaddr:X2} {bit:X2}"))
        {
            SystemConfigUploadAbort();
            return false;
        }
        return SystemConfigUploadFinish();
    }

    // Declares one board's signal lamp group (Red/Green1/Green2 bits) via a
    // single-line SCU session. Same board_find_slot() resolution as
    // SetStatusLed.
    public bool SetSignal(byte vaddr, int redBit, int green1Bit, int green2Bit)
    {
        if (!SystemConfigUploadStart()) return false;
        if (!SystemConfigSendLine($"G {vaddr:X2} {redBit:X2} {green1Bit:X2} {green2Bit:X2}"))
        {
            SystemConfigUploadAbort();
            return false;
        }
        return SystemConfigUploadFinish();
    }

    // Declares one board's inverter-enable pin (port + bit) via a
    // single-line SCU session. Same board_find_slot() resolution as
    // SetStatusLed/SetSignal.
    public bool SetInverterEnable(byte vaddr, char port, int bit)
    {
        if (!SystemConfigUploadStart()) return false;
        if (!SystemConfigSendLine($"V {vaddr:X2} {port} {bit:X2}"))
        {
            SystemConfigUploadAbort();
            return false;
        }
        return SystemConfigUploadFinish();
    }

    // Declares one board's track detection input (bit + active level) via a
    // single-line SCU session. Same board_find_slot() resolution as above.
    public bool SetTrackDetection(byte vaddr, int bit, bool activeHigh)
    {
        if (!SystemConfigUploadStart()) return false;
        if (!SystemConfigSendLine($"T {vaddr:X2} {bit:X2} {(activeHigh ? 1 : 0):X2}"))
        {
            SystemConfigUploadAbort();
            return false;
        }
        return SystemConfigUploadFinish();
    }

    public bool SystemConfigDownloadStart()
    {
        _conn.StartCapture();
        _conn.SendLine("SCD");
        return true;
    }

    public string? GetNextSystemConfigLine(int timeoutMs = 2000) =>
        _conn.GetCapturedLine(timeoutMs)?.Trim();

    public void SystemConfigDownloadFinish() => _conn.StopCapture();

    // -------------------------------------------------------------------------
    // Misc
    // -------------------------------------------------------------------------

    public void SoftReset() => _conn.SendLine("RST");
}
