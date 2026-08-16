using System.IO.Ports;

namespace DrammenMJKConfig.IntegrationTests;

// Hardware-in-the-loop tests need a live Arduino running Firmware.ino. The COM
// port isn't stable across machines/sessions (it can even drop and reappear
// mid-session), so rather than hardcode one, probe every available port and
// use whichever one actually answers PING/SI.
static class ArduinoTestHelper
{
    public static (ArduinoConnection Conn, ArduinoDevice Device) Connect(int bootDelayMs = 2000)
    {
        foreach (string portName in SerialPort.GetPortNames())
        {
            ArduinoConnection? conn = null;
            try
            {
                conn = new ArduinoConnection(portName);
                conn.Open();
                Thread.Sleep(bootDelayMs); // Arduino resets on DTR/serial connect
                var device = new ArduinoDevice(conn);
                if (device.Ping() && device.Init())
                    return (conn, device);
                conn.Dispose();
            }
            catch
            {
                conn?.Dispose();
            }
        }

        Assert.Inconclusive("No Arduino responded on any available COM port -- connect the board and retry.");
        throw new InvalidOperationException(); // unreachable; Assert.Inconclusive always throws
    }
}
