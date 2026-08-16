using System.IO.Ports;

namespace DrammenMJKConfig;

// Finds the Arduino's COM port automatically where possible: tries the
// port remembered from last time, then auto-probes every available port
// (same approach as DrammenMJKConfig.IntegrationTests' ArduinoTestHelper),
// and only falls back to an interactive prompt if neither works.
static class PortSelector
{
    static readonly string MemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DrammenMJKConfig", "lastport.txt");

    public static (ArduinoConnection Conn, ArduinoDevice Device, string PortName)? Connect()
    {
        string? remembered = TryLoadRemembered();
        if (remembered != null)
        {
            Console.WriteLine($"Trying last-used port {remembered}...");
            var result = TryConnect(remembered);
            if (result != null) { Remember(remembered); return result; }
            Console.WriteLine($"  {remembered} didn't respond.");
        }

        string[] ports = SerialPort.GetPortNames();
        string[] candidates = ports.Where(p => p != remembered).ToArray();
        if (candidates.Length > 0)
        {
            Console.WriteLine("Probing available COM ports for the Arduino...");
            foreach (string port in candidates)
            {
                var result = TryConnect(port);
                if (result != null) { Remember(port); return result; }
            }
            Console.WriteLine("  No port responded automatically.");
        }

        return InteractivePrompt(ports);
    }

    static (ArduinoConnection, ArduinoDevice, string)? InteractivePrompt(string[] ports)
    {
        if (ports.Length == 0)
        {
            Console.WriteLine("No COM ports found. Is the Arduino connected?");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("Available COM ports:");
        for (int i = 0; i < ports.Length; i++)
            Console.WriteLine($"  {i + 1}. {ports[i]}");
        Console.WriteLine();
        Console.Write("Enter port number or name: ");
        string? input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return null;

        string portName = int.TryParse(input, out int idx) && idx >= 1 && idx <= ports.Length
            ? ports[idx - 1]
            : input;

        Console.WriteLine($"Connecting to {portName} at 115200 baud...");
        var result = TryConnect(portName, verbose: true);
        if (result != null) Remember(portName);
        return result;
    }

    static (ArduinoConnection, ArduinoDevice, string)? TryConnect(string portName, bool verbose = false)
    {
        ArduinoConnection? conn = null;
        try
        {
            conn = new ArduinoConnection(portName);
            conn.Open();
            Thread.Sleep(2000); // Arduino resets on DTR/serial connect
            var device = new ArduinoDevice(conn);
            if (device.Ping() && device.Init())
                return (conn, device, portName);

            if (verbose) Console.WriteLine("No response from Arduino (PING/SI failed). Check connection.");
            conn.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            if (verbose) Console.WriteLine($"Failed to open port: {ex.Message}");
            conn?.Dispose();
            return null;
        }
    }

    static string? TryLoadRemembered()
    {
        try { return File.Exists(MemoryPath) ? File.ReadAllText(MemoryPath).Trim() : null; }
        catch { return null; }
    }

    static void Remember(string portName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryPath)!);
            File.WriteAllText(MemoryPath, portName);
        }
        catch { /* best-effort; failing to remember isn't fatal */ }
    }
}
