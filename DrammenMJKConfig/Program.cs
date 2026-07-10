using System.IO.Ports;
using DrammenMJKConfig;

Console.WriteLine("DrammenMJKConfig — Arduino Configuration Tool");
Console.WriteLine("==============================================");
Console.WriteLine();

// Port selection
string[] ports = SerialPort.GetPortNames();
if (ports.Length == 0)
{
    Console.WriteLine("No COM ports found. Is the Arduino connected?");
    return;
}

string portName;
if (ports.Length == 1)
{
    portName = ports[0];
    Console.WriteLine($"Using {portName} (only available port).");
}
else
{
    Console.WriteLine("Available COM ports:");
    for (int i = 0; i < ports.Length; i++)
        Console.WriteLine($"  {i + 1}. {ports[i]}");
    Console.WriteLine();
    Console.Write("Enter port number or name: ");
    string? input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) return;

    if (int.TryParse(input, out int idx) && idx >= 1 && idx <= ports.Length)
        portName = ports[idx - 1];
    else
        portName = input;
}

Console.WriteLine($"Connecting to {portName} at 115200 baud...");
using var conn = new ArduinoConnection(portName);
try
{
    conn.Open();
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to open port: {ex.Message}");
    return;
}

// Arduino resets on serial connect (DTR). Wait for boot.
Console.WriteLine("Waiting for Arduino to initialise...");
Thread.Sleep(2000);

var arduino = new ArduinoDevice(conn);

// Verify connection
if (!arduino.Ping())
{
    Console.WriteLine("No response from Arduino (PING failed). Check connection.");
    return;
}

// Read site configuration
if (!arduino.Init())
{
    Console.WriteLine("Failed to read site info from Arduino (SI command failed).");
    return;
}
Console.WriteLine($"Site: Pens {arduino.FirstPens}–{arduino.LastPens}  " +
                  $"LED outputs: {arduino.NumLedOutputs}  " +
                  $"Motor pairs: {arduino.NumPairs}");
Console.WriteLine();

// Show EEPROM status
EepromStatus.Print(arduino);
Console.WriteLine();
Console.WriteLine("Ready.");
Console.WriteLine();

var menu = new Menu(
    [
        ('D', "Debug mode", () => DebugSession.Run(arduino)),
        ('C', "Config mode", () => ConfigSession.Run(arduino)),
        ('V', "Verify", () => VerifySession.Run(arduino)),
    ],
    quitOption: ('Q', "Quit")
);
menu.Run();

Console.WriteLine("Goodbye.");
