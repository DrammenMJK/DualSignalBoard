namespace DrammenMJKConfig.IntegrationTests;

[Explicit("Requires a physical Arduino running Firmware.ino connected via USB serial.")]
public class ArduinoConnectivityTests
{
    [Test]
    public void PingAndInit_RespondSuccessfully()
    {
        var (conn, device) = ArduinoTestHelper.Connect();
        using (conn)
        {
            Assert.That(device.Ping(), Is.True);
            Assert.That(device.Init(), Is.True);
        }
    }
}
