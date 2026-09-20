using DrammenMJKConfig;

Console.WriteLine("DrammenMJKConfig — Arduino Configuration Tool");
Console.WriteLine("==============================================");
Console.WriteLine();

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

var menu = new Menu(
    [
        ('C', "Config mode", () => ConfigSession.Run(arduino)),
        ('O', "Operate — drive motors, signals, status lights", () => CommandSession.Run(arduino)),
        ('S', "Status — EEPROM summary", () => StatusSession.Run(arduino)),
    ],
    quitOption: ('Q', "Quit")
);
menu.Run();

Console.WriteLine("Goodbye.");
