namespace DrammenMJKConfig.Tests;

public class BoardConfigTests
{
    [Test]
    public void AllSwitchSlots_AssignsSlotsInDeclarationOrder()
    {
        var slots = BoardConfig.AllSwitchSlots().ToList();

        Assert.That(slots.Select(s => s.Label), Is.EqualTo(new[] { "1", "3", "5/6", "7" }));
        Assert.That(slots.Select(s => s.Slot), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void AllSwitchSlots_MotorBitsAreSequentialFromScbsMotorBitFirst()
    {
        var slots = BoardConfig.AllSwitchSlots().ToList();

        Assert.That(slots.Select(s => s.MotorBit), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [TestCase("1", 0)]
    [TestCase("3", 1)]
    [TestCase("5/6", 2)]
    [TestCase("7", 3)]
    public void TryFindSlotByLabel_KnownLabel_ReturnsExpectedSlot(string label, int expectedSlot)
    {
        bool found = BoardConfig.TryFindSlotByLabel(label, out int slot);

        Assert.That(found, Is.True);
        Assert.That(slot, Is.EqualTo(expectedSlot));
    }

    [Test]
    public void TryFindSlotByLabel_UnknownLabel_ReturnsFalse()
    {
        bool found = BoardConfig.TryFindSlotByLabel("does-not-exist", out int slot);

        Assert.That(found, Is.False);
        Assert.That(slot, Is.EqualTo(-1));
    }

    [Test]
    public void Svb_Phase1HasExactlyOneScb()
    {
        // Phase 1 scope: FCSBR only. Fossli Venstre (FCSBL) is Phase 2.
        Assert.That(BoardConfig.Svb.Scbs, Has.Count.EqualTo(1));
        Assert.That(BoardConfig.Svb.Scbs[0].Name, Does.Contain("FCSBR"));
    }
}
