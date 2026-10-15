using NSubstitute;

namespace DrammenMJKConfig.Tests;

public class SwitchSlotDataTests
{
    IArduinoDevice _arduino = null!;

    [SetUp]
    public void Setup() => _arduino = Substitute.For<IArduinoDevice>();

    [Test]
    public void Read_AllBytesAnswer_ReturnsTheSlot()
    {
        _arduino.EepromRead(Arg.Any<int>()).Returns(x => (byte?)((int)x[0] & 0x0F));

        var d = SwitchSlotData.Read(_arduino, 3);

        Assert.That(d, Is.EqualTo(new SwitchSlotData(0x03, 0x03, 0x03, 0x03, 0x03, 0x03)));
    }

    [Test]
    public void Read_OneByteTimesOut_ReturnsNullNotAnUnconfiguredSlot()
    {
        _arduino.EepromRead(Arg.Any<int>()).Returns((byte?)0x20);
        _arduino.EepromRead(ArduinoDevice.RegionSlotPolarity + 3).Returns((byte?)null);

        Assert.That(SwitchSlotData.Read(_arduino, 3), Is.Null);
    }

    [Test]
    public void Write_ByteRejected_ReturnsFalseAndStopsWriting()
    {
        _arduino.EepromWrite(Arg.Any<int>(), Arg.Any<byte>()).Returns(true);
        _arduino.EepromWrite(ArduinoDevice.RegionSlotMotorBit + 3, Arg.Any<byte>()).Returns(false);

        bool ok = new SwitchSlotData(0x20, 1, 0, 0x20, 2, 3).Write(_arduino, 3);

        Assert.That(ok, Is.False);
        _arduino.DidNotReceive().EepromWrite(ArduinoDevice.RegionSlotPolarity + 3, Arg.Any<byte>());
    }

    [Test]
    public void Write_AllBytesAccepted_WritesEveryRegion()
    {
        _arduino.EepromWrite(Arg.Any<int>(), Arg.Any<byte>()).Returns(true);

        bool ok = new SwitchSlotData(0x20, 1, 0, 0x21, 2, 3).Write(_arduino, 3);

        Assert.That(ok, Is.True);
        _arduino.Received().EepromWrite(ArduinoDevice.RegionSlotFeedbackVAddr + 3, 0x21);
        _arduino.Received().EepromWrite(ArduinoDevice.RegionSlotFeedbackAvvik + 3, 3);
    }
}
