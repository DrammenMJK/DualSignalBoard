namespace DrammenMJKConfig.Tests;

// BoardConfig.Svb is now loaded, not a static readonly field -- tests that
// depend on it (directly, or indirectly via SystemConfigJson's calls into
// BoardConfig.AllSwitchSlots()) need to establish a known state themselves
// rather than relying on module-load-time initialization. Matches the
// canonical two-board state this whole test suite was originally written
// against (Fossli Motors Hoyre/Venstre), so existing assertions keep making
// sense.
static class TestBoardsFixture
{
    public static void Apply()
    {
        var file = new BoardsFile
        {
            AddressMode = "test",
            Svb = new SvbDeclaration { Name = "Fossli", Number = 1 },
            Boards =
            [
                new BoardDeclaration
                {
                    Name = "Fossli Motors Hoyre",
                    Category = "SCB",
                    Bus = 2,
                    Chips = ["0x20"],
                    MotorBitFirst = 0,
                    Switches = ["1", "3", "5/6", "7"],
                    Dreieskive = new DreieskiveDeclaration { Base = 4, Upper = 5 },
                    StatusLedBit = 7,
                },
                new BoardDeclaration
                {
                    Name = "Fossli Motors Venstre",
                    Category = "SCB",
                    Bus = 2,
                    Chips = ["0x21"],
                    MotorBitFirst = 0,
                    Switches = ["101", "2", "4"],
                    StatusLedBit = 7,
                },
            ],
        };

        BoardConfig.ApplyBoardsFile(file);
    }
}
