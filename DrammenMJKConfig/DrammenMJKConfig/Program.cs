using DrammenMJKConfig;

Console.WriteLine("DrammenMJKConfig — Arduino Configuration Tool");
Console.WriteLine("==============================================");
Console.WriteLine();

// Every relative path from here on (boards.json, hardware.json,
// SystemConfig.json and paths typed at prompts) means the config folder.
if (ConfigFolder.Find(AppContext.BaseDirectory) is not { } configFolder)
{
    Console.WriteLine($"Can't find the repo root (.git) above {AppContext.BaseDirectory} -- run the tool from its git clone.");
    return;
}
Directory.SetCurrentDirectory(configFolder);
Console.WriteLine($"Config folder: {configFolder}");

const string BoardsPath = "boards.json";
try
{
    BoardConfig.Load(BoardsPath);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to load {BoardsPath}: {ex.Message}");
    return;
}

var connected = PortSelector.Connect();
if (connected == null) return;
using var conn = connected.Value.Conn;
var arduino = connected.Value.Device;

Console.WriteLine($"Connected on {connected.Value.PortName}. SVB: {BoardConfig.Svb.Name}");

// Surface Test/Prod mode up front -- in Test mode every I2C/MCP command
// (Motor scan, Operate, Bench test's raw pin tools) will silently report
// "no response" since the board never touches the TWI peripheral at all.
// Without this banner that looks like a hardware fault, not a mode setting.
bool? prod = arduino.IsProdMode();
Console.WriteLine(prod switch
{
    true => "Mode: PROD (I2C active).",
    false => "Mode: TEST (I2C OFF -- board won't respond on the bus. Config -> M to switch to Prod.)",
    null => "Mode: unknown -- no response querying SI.",
});
Console.WriteLine();
Console.WriteLine("Ready.");
Console.WriteLine();

// Composition root: every session gets the board (and what else it needs) here.
var switchTable = new SwitchTable(arduino);
var config = new ConfigSession(arduino);
var hardwareConfig = new HardwareConfigSession(arduino);
var panelScan = new PanelScanSession(arduino);
var panelEdit = new PanelEditSession(panelScan);
var systemConfig = new SystemConfigSession(arduino);
var switchEdit = new SwitchEditSession(arduino, switchTable);
var benchTest = new BenchTestSession(arduino);
var command = new CommandSession(arduino);
var status = new StatusSession(arduino, switchTable);

var configMenu = new Menu(
    [
        ('H', "Hardware config (upload/download hardware.json)",   hardwareConfig.Run),
        ('1', "Motor scan — find switch motors + feedback",         config.MotorScan),
        ('2', "Signal scan — find signal lamp bits (Red/Green1/Green2)", config.SignalScan),
        ('3', "Track detection — set which bit + level means train present", config.TrackDetectionScan),
        ('4', "Panel LED scan (SVB) — which LED is which pens Rett/Avvik / signal lamp", panelScan.LedScan),
        ('5', "Panel switch scan (SVB) — which input is which pens / dreieskive / green request", panelScan.SwitchScan),
        ('6', "Edit panel config (SVB) — fix labeling/polarity mistakes", panelEdit.Run),
        ('J', "System Config Backup (backup/restore)",              systemConfig.Run),
        ('E', "Edit switch config — fix labeling/polarity mistakes", switchEdit.Run),
        ('B', "Bench test — probe/read/write raw MCP23017 pins",   benchTest.Run),
        ('M', "Test/Prod mode — enable/disable I2C at boot",       config.ModeConfig),
        ('V', "Verify EEPROM — check config for broken references/pins", config.VerifyEeprom),
        ('R', "Reset: erase all config from EEPROM",               config.ResetConfig),
        ('A', "Restart Arduino (watchdog reset, re-runs setup())", config.Restart),
    ],
    quitOption: ('Q', "Exit config mode")
);

void RunConfig()
{
    Console.WriteLine();
    Console.WriteLine("=== Config Mode ===");
    configMenu.Run();
    Console.WriteLine("Exiting config mode.");
    Console.WriteLine();
}

new Menu(
    [
        ('C', "Config mode", RunConfig),
        ('O', "Operate — drive motors, signals, status lights", command.Run),
        ('S', "Status — EEPROM summary", status.Run),
    ],
    quitOption: ('Q', "Quit")
).Run();

Console.WriteLine("Goodbye.");
