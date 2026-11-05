namespace DrammenMJKConfig.Tests;

public class PanelBenchSessionTests
{
    static readonly (byte, char, int)[] Inputs = [(0x20, 'A', 0), (0x20, 'A', 1), (0x21, 'A', 6), (0x21, 'A', 7)];

    [Test]
    public void FormatHeader_ShowsChipAddressOverFirstColumnAndPinOverEach()
    {
        Assert.That(PanelBenchSession.FormatHeader(Inputs), Is.EqualTo(new[]
        {
            "0x20      | 0x21",
            "A0   A1   | A6   A7",
        }));
    }

    [Test]
    public void FormatInputs_FirstRead_ShowsLevelsWithoutMarks()
    {
        var now = new Dictionary<(byte, char), int> { [(0x20, 'A')] = 0b10, [(0x21, 'A')] = 0b0100_0000 };

        Assert.That(PanelBenchSession.FormatInputs(Inputs, now, previous: null),
            Is.EqualTo("0    1    | 1    0"));
    }

    [Test]
    public void FormatInputs_MarksOnlyChangedPins()
    {
        var before = new Dictionary<(byte, char), int> { [(0x20, 'A')] = 0b10, [(0x21, 'A')] = 0b0100_0000 };
        var now = new Dictionary<(byte, char), int> { [(0x20, 'A')] = 0b11, [(0x21, 'A')] = 0b1000_0000 };

        Assert.That(PanelBenchSession.FormatInputs(Inputs, now, before),
            Is.EqualTo("1*   1    | 0*   1*"));
    }

    [Test]
    public void ChangedNonInputPins_ReportsOnlyOutputBitsThatChanged()
    {
        var masks = new Dictionary<(byte, char), int> { [(0x21, 'A')] = 0b1100_0000 };
        var before = new Dictionary<(byte, char), int> { [(0x21, 'A')] = 0b0000_0000 };
        var now = new Dictionary<(byte, char), int> { [(0x21, 'A')] = 0b0100_1000 };

        Assert.That(PanelBenchSession.ChangedNonInputPins(masks, now, before), Is.EqualTo(new[] { "0x21 A3" }));
    }
}
