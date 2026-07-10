namespace DrammenMJKConfig;

// Typed wrapper around ArduinoConnection providing the low-level command protocol.
// All config logic uses this class; ArduinoConnection handles only raw serial I/O.
sealed class ArduinoDevice
{
    readonly ArduinoConnection _conn;

    // Site info populated by Init()
    public char FirstPens      { get; private set; }
    public char LastPens       { get; private set; }
    public int  NumLedOutputs  { get; private set; }
    public int  NumPairs       { get; private set; }
    public int  DreieskivePair { get; private set; }
    public int  NumPens        => LastPens - FirstPens + 1;

    // EEPROM region base addresses (C# owns the layout)
    public const int Region1Base     = 0x10;
    public const int Region1Pol      = 0x30;
    public const int Region2Rett     = 0x50;
    public const int Region2Avvik    = 0x70;
    public const int Region3Base     = 0x90;
    public const int Region4Rett     = 0xB0;
    public const int Region4Avvik    = 0xD0;
    public const int Region5MotorPin = 0xF0;
    public const int Region5MotorPol = 0xF1;
    public const int Region5SwCw     = 0xF2;
    public const int Region5SwCcw    = 0xF3;
    public const int Region5Moment   = 0xF4;
    public const int Region6Base     = 0x100;
    public const byte LedNoPin       = 0xFE;
    public const byte Unset          = 0xFF;

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
    // Connection / site info
    // -------------------------------------------------------------------------

    public bool Ping() => Ask("PING", 1000) == "PONG";

    // Reads site configuration from device. Must be called before any method
    // that uses FirstPens/LastPens/NumPairs/etc.
    // Returns false if device does not respond or response is malformed.
    public bool Init()
    {
        string? resp = Ask("SI", 1000);
        if (resp == null) return false;

        // "SITE B I 16 9 4"
        var p = resp.Split(' ');
        if (p.Length < 6 || p[0] != "SITE") return false;
        FirstPens      = p[1][0];
        LastPens       = p[2][0];
        NumLedOutputs  = int.Parse(p[3]);
        NumPairs       = int.Parse(p[4]);
        DreieskivePair = int.Parse(p[5]);
        return true;
    }

    // -------------------------------------------------------------------------
    // EEPROM
    // -------------------------------------------------------------------------

    public byte EepromRead(int addr)
    {
        string? resp = Ask($"ER {addr:X2}");
        if (resp != null && byte.TryParse(resp, System.Globalization.NumberStyles.HexNumber, null, out byte val))
            return val;
        return 0xFF;
    }

    public bool EepromWrite(int addr, byte val) =>
        Ask($"EW {addr:X2} {val:X2}") == "OK";

    public bool EepromErase() => Ask("EC", 3000) == "OK";

    // -------------------------------------------------------------------------
    // Motors
    // -------------------------------------------------------------------------

    // Fire motor pair in polarity: pol=0 means '01' pattern, pol=1 means '10'.
    public bool MotorFire(int pair, int pol) =>
        Ask($"MF {pair} {pol}") == "OK";

    // Stop all motors (de-energise; positions are held by stall-hold).
    public bool MotorStop() => Ask("MS") == "OK";

    // Wait for feedback on pair. Returns pin number that triggered, or -1 on timeout.
    public int WaitFeedback(int pair, int timeoutMs)
    {
        string? resp = Ask($"FW {pair} {timeoutMs}", timeoutMs + 1500);
        if (resp == null || resp == "TIMEOUT") return -1;
        // "HIT 4"
        var p = resp.Split(' ');
        if (p.Length == 2 && p[0] == "HIT" && int.TryParse(p[1], out int pin))
            return pin;
        return -1;
    }

    // -------------------------------------------------------------------------
    // Switches / feedback reads
    // -------------------------------------------------------------------------

    // Read a single pin. Returns 0 or 1, -1 on error.
    public int ReadPin(int pin)
    {
        string? resp = Ask($"SW {pin}");
        return resp == "1" ? 1 : resp == "0" ? 0 : -1;
    }

    // Scan all switch pins and return the first one that changes.
    // Returns pin number, or -1 on timeout.
    public int ScanSwitches(int timeoutMs)
    {
        string? resp = Ask($"SCA {timeoutMs}", timeoutMs + 1500);
        if (resp == null || resp == "TIMEOUT") return -1;
        // "CHANGED 17"
        var p = resp.Split(' ');
        if (p.Length == 2 && p[0] == "CHANGED" && int.TryParse(p[1], out int pin))
            return pin;
        return -1;
    }

    // -------------------------------------------------------------------------
    // LEDs
    // -------------------------------------------------------------------------

    public bool SetLed(int idx, bool on) =>
        Ask($"LD {idx} {(on ? 1 : 0)}") == "OK";

    public bool AllLedsOff() => Ask("LA") == "OK";

    // -------------------------------------------------------------------------
    // Routing matrix (structured protocol, same READY/OK/STORED/END as before)
    // -------------------------------------------------------------------------

    // Start a routing matrix upload session.
    // Returns true if device acknowledged with READY.
    public bool RoutingMatrixUploadStart()
    {
        _conn.StartCapture();
        _conn.SendLine("RMU");
        string? ack = _conn.GetCapturedLine(1500)?.Trim();
        if (ack != "READY")
        {
            _conn.StopCapture();
            return false;
        }
        // Leave capture mode open -- caller sends LED lines and calls RoutingMatrixUploadFinish
        return true;
    }

    // Send one LED line and get "OK" or "ERR".
    public bool RoutingMatrixSendLine(string line)
    {
        _conn.SendLine(line);
        return _conn.GetCapturedLine(500)?.Trim() == "OK";
    }

    // Send "END" and wait for "STORED".
    public bool RoutingMatrixUploadFinish()
    {
        _conn.SendLine("END");
        bool ok = _conn.GetCapturedLine(1500)?.Trim() == "STORED";
        _conn.StopCapture();
        return ok;
    }

    public void RoutingMatrixUploadAbort()
    {
        _conn.SendLine("ABORT");
        _conn.StopCapture();
    }

    // Start a routing matrix download session. Caller reads lines with GetNextDownloadLine().
    public bool RoutingMatrixDownloadStart()
    {
        _conn.StartCapture();
        _conn.SendLine("RMD");
        return true;
    }

    public string? GetNextDownloadLine(int timeoutMs = 2000) =>
        _conn.GetCapturedLine(timeoutMs)?.Trim();

    public void RoutingMatrixDownloadFinish() => _conn.StopCapture();

    // -------------------------------------------------------------------------
    // Misc
    // -------------------------------------------------------------------------

    public void SoftReset() => _conn.SendLine("RST");
}
