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
            Assert.DoesNotThrow(() => StatusSession.Run(device));
        }
    }

    [Test]
    public void HardwareConfigDownload_ReturnsWellFormedLines()
    {
        var (conn, device) = ArduinoTestHelper.Connect();
        using (conn)
        {
            device.HardwareConfigDownloadStart();
            try
            {
                while (true)
                {
                    string? line = device.GetNextHardwareLine(2000);
                    Assert.That(line, Is.Not.Null, "Device timed out mid-download (HWD never sent END).");
                    if (line == "END") break;

                    var parts = line!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Assert.That(parts, Has.Length.EqualTo(5), $"Malformed HWD line: '{line}'");
                }
            }
            finally { device.HardwareConfigDownloadFinish(); }
        }
    }

    [Test]
    public void SystemConfigDownload_ReturnsWellFormedLines()
    {
        var (conn, device) = ArduinoTestHelper.Connect();
        using (conn)
        {
            device.SystemConfigDownloadStart();
            try
            {
                while (true)
                {
                    string? line = device.GetNextSystemConfigLine(2000);
                    Assert.That(line, Is.Not.Null, "Device timed out mid-download (SCD never sent END).");
                    if (line == "END") break;

                    Assert.That(line, Does.Match("^[SPDLF] "), $"Unrecognized SCD line tag: '{line}'");
                }
            }
            finally { device.SystemConfigDownloadFinish(); }
        }
    }
}
