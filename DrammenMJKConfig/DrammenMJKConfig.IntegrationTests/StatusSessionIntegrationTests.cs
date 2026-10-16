namespace DrammenMJKConfig.IntegrationTests;

[Explicit("Requires a physical Arduino running Firmware.ino connected via USB serial.")]
public class StatusSessionIntegrationTests
{
    [Test]
    public void Run_DownloadsBoardAndSwitchTablesWithoutError()
    {
        var (conn, device) = ArduinoTestHelper.Connect();
        using (conn)
        {
            Assert.DoesNotThrow(() => new StatusSession(device, new SwitchTable(device)).Run());
        }
    }

    [Test]
    public void HardwareConfigDownload_ReturnsRows()
    {
        var (conn, device) = ArduinoTestHelper.Connect();
        using (conn)
        {
            Assert.That(device.HardwareConfigDownload(), Is.Not.Null, "Device timed out mid-download (HWD never sent END).");
        }
    }

    [Test]
    public void SystemConfigDownload_ReturnsWellFormedLines()
    {
        var (conn, device) = ArduinoTestHelper.Connect();
        using (conn)
        {
            var lines = device.SystemConfigDownload();
            Assert.That(lines, Is.Not.Null, "Device timed out mid-download (SCD never sent END).");

            // SCD line tags emitted by Firmware.ino cmdSCD(): S=switch slot,
            // P=SVB panel input, Q=panel LED, D=dreieskive, L=status LED,
            // G=signal lamp group, V=inverter-enable pin, T=track detection.
            foreach (string line in lines!)
                Assert.That(line, Does.Match("^[SPQDLGVT] "), $"Unrecognized SCD line tag: '{line}'");
        }
    }
}
