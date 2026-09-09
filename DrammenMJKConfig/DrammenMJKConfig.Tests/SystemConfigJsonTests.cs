namespace DrammenMJKConfig.Tests;

public class SystemConfigJsonTests
{
    [SetUp]
    public void Setup() => TestBoardsFixture.Apply();

    static SystemConfigFile BuildFullyConfiguredFile()
    {
        var file = new SystemConfigFile { Site = "Fossli", Svb = "Fossli" };
        file.Switches["1"]   = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 0, Polarity = 0, FeedbackVAddr = "0x20", FeedbackRettBit = 0, FeedbackAvvikBit = 1 };
        file.Switches["3"]   = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 1, Polarity = 1, FeedbackVAddr = "0x20", FeedbackRettBit = 3, FeedbackAvvikBit = 2 };
        file.Switches["5/6"] = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 2, Polarity = 0, FeedbackVAddr = "0x20", FeedbackRettBit = 4, FeedbackAvvikBit = 5 };
        file.Switches["7"]   = new SwitchEntry { MotorVAddr = "0x20", MotorBit = 3, Polarity = 0, FeedbackVAddr = "0x20", FeedbackRettBit = 6, FeedbackAvvikBit = 7 };
        file.Dreieskive = new DreieskiveEntry { MotorVAddr = "0x20", MotorPinBase = 4, CwPolarity = null };
        file.StatusLeds["Fossli Motors Hoyre"] = new StatusLedEntry { VAddr = "0x20", Bit = 7 };
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
    public void ApplyDownloadLines_LLine_KeysStatusLedByBoardNameViaVAddr()
    {
        var file = new SystemConfigFile();

        SystemConfigJson.ApplyDownloadLines(file, ["L 21 07"]); // Fossli Motors Venstre's vaddr

        Assert.That(file.StatusLeds.ContainsKey("Fossli Motors Venstre"), Is.True);
        var led = file.StatusLeds["Fossli Motors Venstre"];
        Assert.That(led.VAddr, Is.EqualTo("0x21"));
        Assert.That(led.Bit, Is.EqualTo(7));
    }

    [Test]
    public void BuildUploadLines_MultipleStatusLeds_ProducesOneLLinePerBoard()
    {
        var file = BuildFullyConfiguredFile();
        file.StatusLeds["Fossli Motors Venstre"] = new StatusLedEntry { VAddr = "0x21", Bit = 7 };

        var lines = SystemConfigJson.BuildUploadLines(file, out _, out _);

        var lLines = lines.Where(l => l.StartsWith("L ")).ToList();
        Assert.That(lLines, Has.Count.EqualTo(2));
        Assert.That(lLines, Has.Some.EqualTo("L 20 07"));
        Assert.That(lLines, Has.Some.EqualTo("L 21 07"));
    }

    [Test]
    public void BuildUploadLines_InverterEnableLine_MatchesConfiguredPortAndBit()
    {
        var file = BuildFullyConfiguredFile();
        file.InverterEnables["Fossli Motors Venstre"] = new InverterEnableEntry { VAddr = "0x21", Port = "A", Bit = 6 };

        var lines = SystemConfigJson.BuildUploadLines(file, out _, out _);

        Assert.That(lines, Has.Some.EqualTo("V 21 A 06"));
    }

    [Test]
    public void ApplyDownloadLines_VLine_KeysInverterEnableByBoardNameViaVAddr()
    {
        var file = new SystemConfigFile();

        SystemConfigJson.ApplyDownloadLines(file, ["V 21 A 06"]);

        Assert.That(file.InverterEnables.ContainsKey("Fossli Motors Venstre"), Is.True);
        var inv = file.InverterEnables["Fossli Motors Venstre"];
        Assert.That(inv.VAddr, Is.EqualTo("0x21"));
        Assert.That(inv.Port, Is.EqualTo("A"));
        Assert.That(inv.Bit, Is.EqualTo(6));
    }

    [Test]
    public void BuildUploadLines_TrackDetectionLine_EncodesActiveHighAsOneOrZero()
    {
        var file = BuildFullyConfiguredFile();
        file.TrackDetections["Fossli Motors Venstre"] = new TrackDetectionEntry { VAddr = "0x21", Bit = 7, ActiveHigh = true };

        var lines = SystemConfigJson.BuildUploadLines(file, out _, out _);

        Assert.That(lines, Has.Some.EqualTo("T 21 07 01"));
    }

    [TestCase("00", false)]
    [TestCase("01", true)]
    public void ApplyDownloadLines_TLine_ParsesActiveHigh(string wireValue, bool expectedActiveHigh)
    {
        var file = new SystemConfigFile();

        SystemConfigJson.ApplyDownloadLines(file, [$"T 21 07 {wireValue}"]);

        var track = file.TrackDetections["Fossli Motors Venstre"];
        Assert.That(track.Bit, Is.EqualTo(7));
        Assert.That(track.ActiveHigh, Is.EqualTo(expectedActiveHigh));
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
        }
        finally { File.Delete(path); }
    }

    // Regression coverage for a real incident: StatusLed/Signal/TrackDetection/
    // InverterEnable all moved from single objects to Dictionary<string, T>
    // (one per board) while keeping the same JSON key names. A real
    // SystemConfig.json on disk from before that change still had the old
    // single-object shape for fields a later save hadn't rewritten yet, and
    // deserializing an object where a dictionary was expected threw
    // JsonException at startup. That specific file was fixed by hand (not
    // something this code migrates automatically) -- what IS a real,
    // supported case is a file that predates these fields existing at all,
    // which must still load cleanly with empty dictionaries rather than throw.
    [Test]
    public void Load_FileMissingNewerDictionaryFields_LoadsWithEmptyDictionariesNotThrowing()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "site": "Fossli",
                  "svb": "Fossli",
                  "switches": {}
                }
                """);

            SystemConfigFile? file = null;
            Assert.DoesNotThrow(() => file = SystemConfigJson.Load(path));

            Assert.That(file!.StatusLeds, Is.Empty);
            Assert.That(file.Signals, Is.Empty);
            Assert.That(file.TrackDetections, Is.Empty);
            Assert.That(file.InverterEnables, Is.Empty);
        }
        finally { File.Delete(path); }
    }
}
