namespace DrammenMJKConfig.Tests;

// Covers the pure/static parts of ArduinoDevice — everything else needs a
// live serial connection and isn't unit-testable in isolation.
public class ArduinoDeviceTests
{
    // vaddr = bus*0x10 + real_i2c_address — see PLAN_Phase1.md "Virtual addressing".
    // Cross-checked against PlanExtended.md's own worked site-map examples.
    [TestCase(0, 0x20, 0x20)] // FCSBR, Phase 1 direct wiring
    [TestCase(0, 0x21, 0x21)] // FCSBL, Phase 1 direct wiring
    [TestCase(2, 0x20, 0x40)] // FCSBR once moved to Bus 2
    [TestCase(2, 0x21, 0x41)] // FCSBL once moved to Bus 2
    [TestCase(1, 0x20, 0x30)] // Havna SCB
    [TestCase(1, 0x22, 0x32)] // Vallekilen SCB
    [TestCase(3, 0x20, 0x50)] // Sidespor
    public void VirtualAddress_MatchesSiteMapWorkedExamples(int bus, byte realAddr, byte expectedVaddr)
    {
        Assert.That(ArduinoDevice.VirtualAddress(bus, realAddr), Is.EqualTo(expectedVaddr));
    }

    [Test]
    public void Unset_IsThePerBoardUnconfiguredSentinel()
    {
        Assert.That(ArduinoDevice.Unset, Is.EqualTo(0xFF));
    }
}
