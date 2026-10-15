namespace DrammenMJKConfig.Tests;

public class EepromCheckTests
{
    // Port A all inputs (feedback), port B all outputs (motors, LEDs).
    static readonly HardwareConfig.ResolvedChip[] Chips =
    [
        new(0x20, 0xFF, 0x00, 0xFF, 0x00),
        new(0x21, 0xFF, 0x00, 0xFF, 0x00),
    ];

    [SetUp]
    public void SetUp() => TestBoardsFixture.Apply();

    [Test]
    public void Problems_SoundConfig_IsEmpty()
    {
        string[] lines =
        [
            "S 0 20 00 00 20 00 01",
            "S 1 20 01 01 20 02 03",
            "S 4 21 00 00 21 00 01",
            "D 20 04 00",
            "L 20 07",
            "G 21 01 02 03",
            "T 20 07 01",
            "Q 0 21 B 06 00 01",
        ];

        Assert.That(EepromCheck.Problems(Chips, lines), Is.Empty);
    }

    [TestCase("not in the hardware table", "S 0 22 00 00 20 00 01")]
    [TestCase("does not exist", "S 0 20 08 00 20 00 01")]
    [TestCase("polarity 02", "S 0 20 00 02 20 00 01")]
    [TestCase("same bit", "S 0 20 00 00 20 01 01")]
    [TestCase("no switch in boards.json", "S 31 20 00 00 20 00 01")]
    [TestCase("already driven by Switch 1 motor", "S 0 20 00 00 20 00 01", "S 1 20 00 00 20 02 03")]
    [TestCase("already driven by Dreieskive CW", "D 20 04 00", "L 20 04")]
    [TestCase("is an input", "T 20 07 01", "V 20 A 07")]
    [TestCase("does not fit role", "Q 0 20 B 03 FF 00")]
    [TestCase("unknown kind", "P 0 20 A 00 FF 05 00 00")]
    [TestCase("must be its own slot", "P 0 20 A 00 FF 00 01 00")]
    [TestCase("Unreadable", "S 0 ZZ 00 00 20 00 01")]
    [TestCase("Unreadable", "X 1 2")]
    public void Problems_BrokenRecord_IsReported(string expected, params string[] lines)
    {
        var problems = EepromCheck.Problems(Chips, lines);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.Contain(expected));
    }

    [Test]
    public void Problems_HardwareTableHasUnknownBoard_IsReported()
    {
        var problems = EepromCheck.Problems([.. Chips, new(0x30, 0xFF, 0x00, 0xFF, 0x00)], []);

        Assert.That(problems, Is.EqualTo(new[] { "Hardware table has 0x30, which no board in boards.json uses." }));
    }
}
