namespace DrammenMJKConfig.Tests;

public class BoardConfigTests
{
    [SetUp]
    public void Setup() => TestBoardsFixture.Apply();

    [Test]
    public void AllSwitchSlots_AssignsSlotsInDeclarationOrder()
    {
        var slots = BoardConfig.AllSwitchSlots().ToList();

        Assert.That(slots.Select(s => s.Label), Is.EqualTo(new[] { "1", "3", "5/6", "7", "101", "2", "4" }));
        Assert.That(slots.Select(s => s.Slot), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
    }

    [Test]
    public void AllSwitchSlots_MotorBitsAreSequentialFromScbsMotorBitFirst()
    {
        var slots = BoardConfig.AllSwitchSlots().ToList();

        // Hoyre's bits 0-3, then Venstre's own 0-2 -- independent per-chip
        // numbering, not continuing from Hoyre's.
        Assert.That(slots.Select(s => s.MotorBit), Is.EqualTo(new[] { 0, 1, 2, 3, 0, 1, 2 }));
    }

    [TestCase("1", 0)]
    [TestCase("3", 1)]
    [TestCase("5/6", 2)]
    [TestCase("7", 3)]
    [TestCase("101", 4)]
    [TestCase("2", 5)]
    [TestCase("4", 6)]
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
    public void Svb_HasHoyreAndVenstre()
    {
        Assert.That(BoardConfig.Svb.Scbs, Has.Count.EqualTo(2));
        Assert.That(BoardConfig.Svb.Scbs[0].Name, Does.Contain("Hoyre"));
        Assert.That(BoardConfig.Svb.Scbs[1].Name, Does.Contain("Venstre"));
    }

    [Test]
    public void Hoyre_HasADreieskive_VenstreDoesNot()
    {
        // Exactly one dreieskive for the whole site, owned by Fossli Motors Hoyre.
        Assert.That(BoardConfig.Svb.Scbs[0].DreieskivePins, Is.Not.Null);
        Assert.That(BoardConfig.Svb.Scbs[1].DreieskivePins, Is.Null);
    }

    [Test]
    public void Load_ResolvesVirtualAddressAsBusZeroInTestMode_IgnoringDeclaredProductionBus()
    {
        // Both boards are declared with production bus 2, but addressMode
        // "test" (TestBoardsFixture's default) must ignore that and resolve
        // as bus 0 -- matches the physical bench reality of one board wired
        // up at a time, no I2C mux built yet.
        Assert.That(BoardConfig.Svb.Scbs[0].VirtualAddress, Is.EqualTo((byte)0x20));
        Assert.That(BoardConfig.Svb.Scbs[1].VirtualAddress, Is.EqualTo((byte)0x21));
    }

    [Test]
    public void ApplyBoardsFile_ProdAddressMode_UsesDeclaredBus()
    {
        var file = new BoardsFile
        {
            AddressMode = "prod",
            Svb = new SvbDeclaration { Name = "Fossli", Number = 1 },
            Boards = [new BoardDeclaration { Name = "Test Board", Category = "SCB", Bus = 2, Chips = ["0x20"] }],
        };

        BoardConfig.ApplyBoardsFile(file);

        // vaddr = bus*0x10 + realAddr = 2*0x10 + 0x20 = 0x40
        Assert.That(BoardConfig.Svb.Scbs[0].VirtualAddress, Is.EqualTo((byte)0x40));
    }

    [Test]
    public void ApplyBoardsFile_MultiChipBoard_ListsAllVirtualAddresses()
    {
        var file = new BoardsFile
        {
            AddressMode = "test",
            Svb = new SvbDeclaration { Name = "Fossli", Number = 1 },
            Boards = [new BoardDeclaration { Name = "Havna Motors", Category = "SCB", Bus = 1, Chips = ["0x20", "0x21"] }],
        };

        BoardConfig.ApplyBoardsFile(file);

        Assert.That(BoardConfig.Svb.Scbs[0].VirtualAddresses, Is.EqualTo(new byte[] { 0x20, 0x21 }));
    }

    [Test]
    public void ApplyBoardsFile_SvbOnlyCategory_ExcludedFromScbsButKeptInAllBoards()
    {
        var file = new BoardsFile
        {
            AddressMode = "test",
            Svb = new SvbDeclaration { Name = "Fossli", Number = 1 },
            Boards =
            [
                new BoardDeclaration { Name = "An SCB", Category = "SCB", Chips = ["0x20"] },
                new BoardDeclaration { Name = "A Panel", Category = "SVB", Chips = ["0x22"] },
            ],
        };

        BoardConfig.ApplyBoardsFile(file);

        Assert.That(BoardConfig.Svb.Scbs.Select(s => s.Name), Is.EqualTo(new[] { "An SCB" }));
        Assert.That(BoardConfig.AllBoards.Select(b => b.Name), Is.EquivalentTo(new[] { "An SCB", "A Panel" }));
    }

    [Test]
    public void MotorBitFor_ExplicitMotorBitsDeclared_UsesThemDirectly_NotMotorBitFirst()
    {
        // Fossli Venstre's real layout: motors and signal lamps are
        // interleaved on Port B (motors 1,3,5; signals 0,2,4) -- a single
        // "first" value can't represent that, so an explicit list overrides
        // it. Regression coverage for the bug this caused: firing "motor bit
        // 0" (wrongly assumed consecutive from 0) lit a signal lamp instead
        // of moving anything, because bit 0 is actually a signal bit here.
        var file = new BoardsFile
        {
            AddressMode = "test",
            Svb = new SvbDeclaration { Name = "Fossli", Number = 1 },
            Boards =
            [
                new BoardDeclaration
                {
                    Name = "Fossli Motors Venstre", Category = "SCB", Chips = ["0x21"],
                    MotorBitFirst = 0, // deliberately present and wrong, to prove MotorBits wins
                    MotorBits = [1, 3, 5],
                    Switches = ["101", "2", "4"],
                },
            ],
        };

        BoardConfig.ApplyBoardsFile(file);

        var scb = BoardConfig.Svb.Scbs[0];
        Assert.That(scb.MotorBitFor(0), Is.EqualTo(1));
        Assert.That(scb.MotorBitFor(1), Is.EqualTo(3));
        Assert.That(scb.MotorBitFor(2), Is.EqualTo(5));
        Assert.That(BoardConfig.AllSwitchSlots().Select(s => s.MotorBit), Is.EqualTo(new[] { 1, 3, 5 }));
    }

    [Test]
    public void MotorBitFor_NoExplicitMotorBits_FallsBackToMotorBitFirstPlusIndex()
    {
        var file = new BoardsFile
        {
            AddressMode = "test",
            Svb = new SvbDeclaration { Name = "Fossli", Number = 1 },
            Boards = [new BoardDeclaration { Name = "Fossli Motors Hoyre", Category = "SCB", Chips = ["0x20"], MotorBitFirst = 0, Switches = ["1", "3", "5/6", "7"] }],
        };

        BoardConfig.ApplyBoardsFile(file);

        var scb = BoardConfig.Svb.Scbs[0];
        Assert.That(scb.MotorBitFor(0), Is.EqualTo(0));
        Assert.That(scb.MotorBitFor(3), Is.EqualTo(3));
    }
}
