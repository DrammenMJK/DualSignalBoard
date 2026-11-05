namespace DrammenMJKConfig.Tests;

public class RuntimeStatusTests
{
    [Test]
    public void Parse_ReadsStateAndErrors()
    {
        var rt = RuntimeStatus.Parse("RT 1 00000005 1");

        Assert.That(rt, Is.Not.Null);
        Assert.That(rt!.On, Is.True);
        Assert.That(rt.PensErrorSlots, Is.EqualTo(new[] { 0, 2 }));
        Assert.That(rt.DreieskiveError, Is.True);
    }

    [Test]
    public void Parse_PausedWithoutErrors()
    {
        var rt = RuntimeStatus.Parse("RT 0 00000000 0");

        Assert.That(rt, Is.EqualTo(new RuntimeStatus(false, 0, false)));
        Assert.That(rt!.PensErrorSlots, Is.Empty);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ERR unknown: RT")]
    [TestCase("RT 2 00000000 0")]
    [TestCase("RT 1 XYZ 0")]
    [TestCase("RT 1 00000000")]
    public void Parse_RejectsAnythingElse(string? line)
    {
        Assert.That(RuntimeStatus.Parse(line), Is.Null);
    }
}
