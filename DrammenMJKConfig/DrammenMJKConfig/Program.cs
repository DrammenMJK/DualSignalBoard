using DrammenMJKConfig;

Console.WriteLine("DrammenMJKConfig — Arduino Configuration Tool");
Console.WriteLine("==============================================");
Console.WriteLine();

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
        ('O', "Operate — drive configured switches", () => CommandSession.Run(arduino)),
        ('S', "Status — EEPROM summary", () => StatusSession.Run(arduino)),
    ],
    quitOption: ('Q', "Quit")
);
menu.Run();

Console.WriteLine("Goodbye.");
