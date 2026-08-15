namespace DrammenMJKConfig.Tests;

public class HardwareConfigTests
{
    static HardwareConfigFile BuildSingleChipBoard(string portAValue = "in-pu", string portBValue = "out")
    {
        var file = new HardwareConfigFile();
        file.Boards.Add(new HwBoard
        {
            Name = "Test Board",
            Category = "SCB",
            Chips =
            {
                new HwChip
                {
                    VAddr = "0x20",
                    PortA = Enumerable.Repeat(portAValue, 8).ToArray(),
                    PortB = Enumerable.Repeat(portBValue, 8).ToArray(),
                },
            },
        });
        return file;
    }

    [Test]
    public void Flatten_FcsbrPattern_AllInPuPortA_AllOutPortB()
    {
        var file = BuildSingleChipBoard();

        var rows = HardwareConfig.Flatten(file, out string? error);

        Assert.That(error, Is.Null);
        Assert.That(rows, Has.Count.EqualTo(1));
        var row = rows![0];
        Assert.That(row.VAddr, Is.EqualTo(0x20));
        Assert.That(row.IodirA, Is.EqualTo(0xFF)); // all in-pu -> all input
        Assert.That(row.IodirB, Is.EqualTo(0x00)); // all out -> all output
        Assert.That(row.GppuA, Is.EqualTo(0xFF));  // in-pu -> pull-up on
        Assert.That(row.GppuB, Is.EqualTo(0x00));  // output bits never have pull-up
    }

    [Test]
    public void Flatten_PlainInWithoutPullup_SetsIodirButNotGppu()
    {
        var file = BuildSingleChipBoard(portAValue: "in");

        var rows = HardwareConfig.Flatten(file, out string? error);

        Assert.That(error, Is.Null);
        Assert.That(rows![0].IodirA, Is.EqualTo(0xFF));
        Assert.That(rows[0].GppuA, Is.EqualTo(0x00)); // "in" (no pull-up) must not set GPPU
    }

    [TestCase("out", false, false)]
    [TestCase("in", true, false)]
    [TestCase("in-pu", true, true)]
    public void Flatten_SingleBitEncoding_MatchesExpectedRegisterBits(string value, bool expectIodirBit, bool expectGppuBit)
    {
        var portA = Enumerable.Repeat("out", 8).ToArray();
        var portB = Enumerable.Repeat("out", 8).ToArray();
        const int bit = 3;
        portB[bit] = value;

        var file = new HardwareConfigFile();
        file.Boards.Add(new HwBoard { Name = "Test", Chips = { new HwChip { VAddr = "0x20", PortA = portA, PortB = portB } } });

        var rows = HardwareConfig.Flatten(file, out string? error);

        Assert.That(error, Is.Null);
        int mask = 1 << bit;
        Assert.That((rows![0].IodirB & mask) != 0, Is.EqualTo(expectIodirBit));
        Assert.That((rows[0].GppuB & mask) != 0, Is.EqualTo(expectGppuBit));
    }

    [Test]
    public void Flatten_TbdInPortA_RefusesAndReportsError()
    {
        var file = BuildSingleChipBoard();
        file.Boards[0].Chips[0].PortA[3] = "tbd";

        var rows = HardwareConfig.Flatten(file, out string? error);

        Assert.That(rows, Is.Null);
        Assert.That(error, Does.Contain("tbd"));
    }

    [Test]
    public void Flatten_TbdInPortB_RefusesAndReportsError()
    {
        var file = BuildSingleChipBoard();
        file.Boards[0].Chips[0].PortB[5] = "tbd";

        var rows = HardwareConfig.Flatten(file, out string? error);

        Assert.That(rows, Is.Null);
        Assert.That(error, Does.Contain("tbd"));
    }

    [Test]
    public void Flatten_TwoChipBoard_ProducesOneRowPerChip()
    {
        var file = new HardwareConfigFile();
        file.Boards.Add(new HwBoard
        {
            Name = "LedAndSwitchesFossli",
            Category = "SVB",
            Chips =
            {
                new HwChip { VAddr = "0x20", PortA = Enumerable.Repeat("out", 8).ToArray(), PortB = Enumerable.Repeat("in-pu", 8).ToArray() },
                new HwChip { VAddr = "0x21", PortA = Enumerable.Repeat("out", 8).ToArray(), PortB = Enumerable.Repeat("in-pu", 8).ToArray() },
            },
        });

        var rows = HardwareConfig.Flatten(file, out string? error);

        Assert.That(error, Is.Null);
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows!.Select(r => r.VAddr), Is.EquivalentTo(new byte[] { 0x20, 0x21 }));
    }

    [Test]
    public void LoadSave_RoundTripsThroughFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            var original = BuildSingleChipBoard();
            original.Todo.Add("test note");

            HardwareConfig.Save(original, path);
            var reloaded = HardwareConfig.Load(path);

            Assert.That(reloaded.Todo, Is.EqualTo(original.Todo));
            Assert.That(reloaded.Boards, Has.Count.EqualTo(1));
            Assert.That(reloaded.Boards[0].Chips[0].VAddrByte, Is.EqualTo(0x20));
            Assert.That(reloaded.Boards[0].Chips[0].PortA, Is.EqualTo(original.Boards[0].Chips[0].PortA));
        }
        finally { File.Delete(path); }
    }
}
