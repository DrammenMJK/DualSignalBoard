using System.Text.Json;

namespace DrammenMJKConfig.Tests;

// SVB panel inputs ("P" lines) and panel LEDs ("Z"/"Q" lines) -- the SCU/SCD
// wire format and its round trip through SystemConfig.json. Fixture slot
// order: 1,3,5/6,7 (Hoyre) = 0-3, 101,2,4 (Venstre) = 4-6; svbSwitches add
// dreieskive = 7, askGreen = 8.
public class PanelConfigJsonTests
{
    [SetUp]
    public void Setup() => TestBoardsFixture.Apply();

    static SystemConfigFile PanelFile()
    {
        var file = new SystemConfigFile();
        file.SvbSwitches["5/6"] = new SvbSwitchEntry { VAddr = "0x20", Port = "A", Bit = 2, Polarity = 0 };
        file.SvbSwitches["101"] = new SvbSwitchEntry { VAddr = "0x20", Port = "A", Bit = 4, Polarity = 1 };
        file.SvbSwitches[SystemConfigJson.Dreieskive] = new SvbSwitchEntry { VAddr = "0x21", Port = "A", BitCw = 6, BitCcw = 7 };
        file.SvbSwitches[SystemConfigJson.AskGreen] = new SvbSwitchEntry { VAddr = "0x20", Port = "A", Bit = 7 };
        file.PanelLeds =
        [
            new PanelLedEntry { VAddr = "0x20", Port = "B", Bit = 3, Target = "5/6", Role = "rett" },
            new PanelLedEntry { VAddr = "0x21", Port = "A", Bit = 0, Target = "5/6", Role = "rett" },
            new PanelLedEntry { VAddr = "0x20", Port = "B", Bit = 4, Target = "5/6", Role = "avvik" },
            new PanelLedEntry { VAddr = "0x21", Port = "A", Bit = 5, Target = "signal", Role = "green2" },
        ];
        return file;
    }

    [Test]
    public void SvbSwitchLines_EncodeSlotPortKindTargetAndPolarity()
    {
        var lines = SystemConfigJson.SvbSwitchLines(PanelFile().SvbSwitches);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "P 2 20 A 02 FF 00 02 00", // 5/6: pens kind 0, target slot 2, Rett = Low
            "P 4 20 A 04 FF 00 04 01", // 101: target slot 4, Rett = High
            "P 7 21 A 06 07 01 FF FF", // dreieskive: CW 6, CCW 7, kind 1
            "P 8 20 A 07 FF 02 FF FF", // green request: kind 2
        }));
    }

    [Test]
    public void PanelLedLines_ClearFirstThenOneQPerLed()
    {
        var lines = SystemConfigJson.PanelLedLines(PanelFile().PanelLeds);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "Z",
            "Q 0 20 B 03 02 00",
            "Q 1 21 A 00 02 00",
            "Q 2 20 B 04 02 01",
            "Q 3 21 A 05 FF 04", // signal lamp: target FF, role 4 = green2
        }));
    }

    [Test]
    public void PanelLedLines_NoLeds_SendsNothing_SoAnOlderFileDoesNotWipeTheBoard()
    {
        Assert.That(SystemConfigJson.PanelLedLines([]), Is.Empty);
    }

    [Test]
    public void UploadThenDownload_RoundTripsPanelInputsAndLeds()
    {
        var original = PanelFile();
        // SCD sends back exactly what SCU stored, minus the Z control line.
        var wire = SystemConfigJson.BuildUploadLines(original, out _, out int svbCount).Where(l => l != "Z");

        var restored = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(restored, wire);

        Assert.That(svbCount, Is.EqualTo(4));
        Assert.That(JsonSerializer.Serialize(restored.SvbSwitches.OrderBy(kv => kv.Key)),
            Is.EqualTo(JsonSerializer.Serialize(original.SvbSwitches.OrderBy(kv => kv.Key))));
        Assert.That(JsonSerializer.Serialize(restored.PanelLeds), Is.EqualTo(JsonSerializer.Serialize(original.PanelLeds)));
    }

    [Test]
    public void ApplyDownloadLines_QLines_ReplaceTheFilesLedsRatherThanAppend()
    {
        var file = PanelFile();

        SystemConfigJson.ApplyDownloadLines(file, ["Q 0 20 B 01 00 01"]);

        Assert.That(file.PanelLeds, Has.Count.EqualTo(1));
        Assert.That(file.PanelLeds[0].Target, Is.EqualTo("1"));
        Assert.That(file.PanelLeds[0].Role, Is.EqualTo("avvik"));
    }
}
