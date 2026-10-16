using NSubstitute;

namespace DrammenMJKConfig.Tests;

// Operate > Motors: which driver bit each motor key flips.
public class CommandSessionTests
{
    IArduinoDevice _arduino = null!;
    CommandSession _session = null!;
    BoardScb _venstre = null!;
    int _slot101;

    [SetUp]
    public void Setup()
    {
        TestBoardsFixture.Apply();
        _arduino = Substitute.For<IArduinoDevice>();
        _session = new CommandSession(_arduino, new DreieskiveSession(_arduino));
        _venstre = BoardConfig.Svb.Scbs.Single(s => s.Name == "Fossli Motors Venstre");
        BoardConfig.TryFindSlotByLabel("101", out _slot101);
        _arduino.EepromRead(Arg.Any<int>()).Returns((byte?)ArduinoDevice.Unset);
    }

    [Test]
    public void ResolveMotors_SlotReadFails_ReturnsNullInsteadOfDeclaredPin()
    {
        _arduino.EepromRead(ArduinoDevice.RegionSlotPolarity + _slot101).Returns((byte?)null);

        Assert.That(_session.ResolveMotors(_venstre), Is.Null);
    }

    [Test]
    public void ResolveMotors_UnconfiguredSlots_UseDeclaredPins()
    {
        var motors = _session.ResolveMotors(_venstre)!;

        Assert.That(motors.Select(m => (m.Slot, m.Bit)), Is.EqualTo(new[] { ((int?)null, 0), (null, 1), (null, 2) }));
    }

    [Test]
    public void ResolveMotors_ConfiguredSlot_UsesStoredPinAndPolarity()
    {
        _arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr + _slot101).Returns((byte?)0x40);
        _arduino.EepromRead(ArduinoDevice.RegionSlotMotorBit + _slot101).Returns((byte?)3);
        _arduino.EepromRead(ArduinoDevice.RegionSlotPolarity + _slot101).Returns((byte?)0);

        var m = _session.ResolveMotors(_venstre)![0];

        Assert.That(m, Is.EqualTo(((int?)_slot101, (byte)0x40, 3, (int?)0)));
    }
}
