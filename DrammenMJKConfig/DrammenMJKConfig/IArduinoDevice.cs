namespace DrammenMJKConfig;

// The board's serial protocol, as the sessions use it. An interface so tests
// can replace the real board (see ArduinoDevice for what each command does).
interface IArduinoDevice
{
    bool Ping();
    bool Init();
    bool? IsProdMode();
    bool SetMode(bool prod);

    byte? EepromRead(int addr);
    bool EepromWrite(int addr, byte val);
    bool EepromErase();

    bool McpSetDirection(byte vaddr, char port, byte mask);
    bool McpSetPullup(byte vaddr, char port, byte mask);
    bool McpWritePort(byte vaddr, char port, byte val);
    int McpReadPort(byte vaddr, char port);
    int McpReadRegister(byte vaddr, byte reg);
    ArduinoDevice.I2cDiagResult? I2CDiagnostic();
    int McpPollChange(byte vaddr, char port, byte baseline, int timeoutMs);
    bool McpSetBit(byte vaddr, char port, int bit, bool val);

    ArduinoDevice.SwitchState ReadSwitch(int slot);

    string? HardwareConfigSend(IEnumerable<HardwareConfig.ResolvedChip> chips);
    List<HardwareConfig.ResolvedChip>? HardwareConfigDownload();
    string? SystemConfigSend(IEnumerable<string> lines);
    List<string>? SystemConfigDownload();
    bool SetStatusLed(byte vaddr, int bit);
    bool SetSignal(byte vaddr, int redBit, int green1Bit, int green2Bit);
    bool SetInverterEnable(byte vaddr, char port, int bit);
    bool SetTrackDetection(byte vaddr, int bit, bool activeHigh);

    void SoftReset();
}
