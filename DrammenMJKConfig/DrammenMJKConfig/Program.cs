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
