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

    // EEPROM region addresses (must match Firmware.ino exactly). Fixed board
    // facts (dreieskive motor side, status LED) — declared, not scanned.
    public const int AddrDreieskiveVAddr        = 0x02;
    public const int AddrDreieskiveMotorPinBase = 0x03;
    public const int AddrDreieskiveCwPolarity   = 0x04;
    public const int AddrStatusLedVAddr         = 0x05;
    public const int AddrStatusLedBit           = 0x06;

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
