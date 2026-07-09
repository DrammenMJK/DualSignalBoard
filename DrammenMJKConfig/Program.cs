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
using var arduino = new ArduinoConnection(portName);
try
{
    arduino.Open();
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to open port: {ex.Message}");
    return;
}

// Arduino resets when the serial port opens (CH340 DTR line).
Console.WriteLine("Waiting for Arduino to initialise...");
Thread.Sleep(2000);
arduino.Send('S');   // request EEPROM status -- response arrives via ! lines
Thread.Sleep(400);   // allow all status lines to arrive before showing menu
Console.WriteLine();
Console.WriteLine("Ready.");
Console.WriteLine();

// Main menu
while (true)
{
    Console.WriteLine("D = Debug mode    C = Config mode    V = Verify    Q = Quit");
    Console.Write("> ");
    char cmd = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
    Console.WriteLine(cmd);

    switch (cmd)
    {
        case 'D':
            DebugSession.Run(arduino);
            break;
        case 'C':
            ConfigSession.Run(arduino);
            break;
        case 'V':
            VerifySession.Run(arduino);
            break;
        case 'Q':
            arduino.Send('Q');
            Console.WriteLine("Goodbye.");
            return;
        default:
            Console.WriteLine("Unknown command.");
            Console.WriteLine();
            break;
    }
}
