namespace DrammenMJKConfig.Tests;

public class SystemConfigJsonTests
{
    static SystemConfigFile BuildFullyConfiguredFile()
    {
        var file = new SystemConfigFile { Site = "Fossli", Svb = "Fossli" };
        file.Switches["1"]   = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 0, Polarity = 0, FeedbackVAddr = "0x20", FeedbackRettBit = 0, FeedbackAvvikBit = 1 };
        file.Switches["3"]   = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 1, Polarity = 1, FeedbackVAddr = "0x20", FeedbackRettBit = 3, FeedbackAvvikBit = 2 };
        file.Switches["5/6"] = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 2, Polarity = 0, FeedbackVAddr = "0x20", FeedbackRettBit = 4, FeedbackAvvikBit = 5 };
        file.Switches["7"]   = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 3, Polarity = 0, FeedbackVAddr = "0x20", FeedbackRettBit = 6, FeedbackAvvikBit = 7 };
        file.Dreieskive = new DreieskiveEntry { MotorVAddr = "0x20", MotorPinBase = 4, CwPolarity = null };
        file.StatusLed = new StatusLedEntry { VAddr = "0x20", Bit = 7 };
        file.FadeConfig = new FadeConfigEntry { FadeMs = 1000, FadeSteps = 60, PwmPeriodUs = 1000 };
        return file;
    }

    [Test]
    public void BuildUploadLines_FourConfiguredSwitches_ProducesOneSLinePerSlotInDeclarationOrder()
    {
        var file = BuildFullyConfiguredFile();

        var lines = SystemConfigJson.BuildUploadLines(file, out int switchCount, out int svbSwitchCount);

        Assert.That(switchCount, Is.EqualTo(4));
        Assert.That(svbSwitchCount, Is.EqualTo(0)); // Phase 1: no SVB, svbSwitches always empty

        var sLines = lines.Where(l => l.StartsWith("S ")).ToList();
        Assert.That(sLines, Has.Count.EqualTo(4));
        Assert.That(sLines[0], Is.EqualTo("S 0 20 00 00 20 00 01")); // "1"
        Assert.That(sLines[1], Is.EqualTo("S 1 20 01 01 20 03 02")); // "3"
        Assert.That(sLines[2], Is.EqualTo("S 2 20 02 00 20 04 05")); // "5/6"
        Assert.That(sLines[3], Is.EqualTo("S 3 20 03 00 20 06 07")); // "7"
    }

    [Test]
    public void BuildUploadLines_UnconfiguredSwitch_IsSkipped()
    {
        var file = BuildFullyConfiguredFile();
        file.Switches.Remove("7"); // never scanned

        var lines = SystemConfigJson.BuildUploadLines(file, out int switchCount, out _);

        Assert.That(switchCount, Is.EqualTo(3));
        Assert.That(lines.Any(l => l.StartsWith("S 3 ")), Is.False);
    }

    [Test]
    public void BuildUploadLines_DreieskiveLine_UsesFFSentinelForUnsetCwPolarity()
    {
        var file = BuildFullyConfiguredFile();

        var lines = SystemConfigJson.BuildUploadLines(file, out _, out _);

        Assert.That(lines, Has.Some.EqualTo("D 20 04 FF"));
    }

    [Test]
    public void BuildUploadLines_StatusLedLine_MatchesConfiguredBit()
    {
        var file = BuildFullyConfiguredFile();

        var lines = SystemConfigJson.BuildUploadLines(file, out _, out _);

        Assert.That(lines, Has.Some.EqualTo("L 20 07"));
    }

    [Test]
    public void BuildUploadLines_FadeConfigLine_UsesFourHexDigitsForSixteenBitFields()
    {
        var file = BuildFullyConfiguredFile();

        var lines = SystemConfigJson.BuildUploadLines(file, out _, out _);

        // FadeMs/PwmPeriodUs are 16-bit (1000 doesn't fit one byte) -- 4 hex digits, not 2.
        Assert.That(lines, Has.Some.EqualTo("F 03E8 3C 03E8"));
    }

    [Test]
    public void ApplyDownloadLines_SLine_MapsSlotBackToCorrectLabelViaBoardConfig()
    {
        var file = new SystemConfigFile();

        SystemConfigJson.ApplyDownloadLines(file, ["S 2 20 02 00 20 04 05"]);

        Assert.That(file.Switches.ContainsKey("5/6"), Is.True); // slot 2 == "5/6" per BoardConfig
        var entry = file.Switches["5/6"];
        Assert.That(entry.MotorVAddr, Is.EqualTo("0x20"));
        Assert.That(entry.MotorBit, Is.EqualTo(2));
        Assert.That(entry.FeedbackRettBit, Is.EqualTo(4));
        Assert.That(entry.FeedbackAvvikBit, Is.EqualTo(5));
    }

    [Test]
    public void ApplyDownloadLines_DLine_PopulatesDreieskiveMotorFacts()
    {
        var file = new SystemConfigFile();

        SystemConfigJson.ApplyDownloadLines(file, ["D 20 04 FF"]);

        Assert.That(file.Dreieskive.MotorVAddr, Is.EqualTo("0x20"));
        Assert.That(file.Dreieskive.MotorPinBase, Is.EqualTo(4));
        Assert.That(file.Dreieskive.CwPolarity, Is.Null); // FF sentinel -> null, not 255
    }

    [Test]
    public void ApplyDownloadLines_FLine_PopulatesFadeConfig()
    {
        var file = new SystemConfigFile();

        SystemConfigJson.ApplyDownloadLines(file, ["F 03E8 3C 03E8"]);

        Assert.That(file.FadeConfig.FadeMs, Is.EqualTo(1000));
        Assert.That(file.FadeConfig.FadeSteps, Is.EqualTo(60));
        Assert.That(file.FadeConfig.PwmPeriodUs, Is.EqualTo(1000));
    }

    [Test]
    public void UploadThenDownload_RoundTripsSwitchDataExactly()
    {
        var original = BuildFullyConfiguredFile();
        var lines = SystemConfigJson.BuildUploadLines(original, out _, out _);

        var reloaded = new SystemConfigFile();
        SystemConfigJson.ApplyDownloadLines(reloaded, lines);

        foreach (var label in new[] { "1", "3", "5/6", "7" })
        {
            Assert.That(reloaded.Switches[label].MotorBit, Is.EqualTo(original.Switches[label].MotorBit), $"label {label}");
            Assert.That(reloaded.Switches[label].Polarity, Is.EqualTo(original.Switches[label].Polarity), $"label {label}");
            Assert.That(reloaded.Switches[label].FeedbackRettBit, Is.EqualTo(original.Switches[label].FeedbackRettBit), $"label {label}");
            Assert.That(reloaded.Switches[label].FeedbackAvvikBit, Is.EqualTo(original.Switches[label].FeedbackAvvikBit), $"label {label}");
        }
    }

    [Test]
    public void LoadSave_RoundTripsThroughFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            var original = BuildFullyConfiguredFile();
            SystemConfigJson.Save(original, path);
            var reloaded = SystemConfigJson.Load(path);

            Assert.That(reloaded.Site, Is.EqualTo("Fossli"));
            Assert.That(reloaded.Switches, Has.Count.EqualTo(4));
            Assert.That(reloaded.FadeConfig.FadeSteps, Is.EqualTo(60));
        }
        finally { File.Delete(path); }
    }
}
