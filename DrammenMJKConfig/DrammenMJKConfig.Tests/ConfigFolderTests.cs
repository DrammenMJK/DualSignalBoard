namespace DrammenMJKConfig.Tests;

public class ConfigFolderTests
{
    string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "DrammenMJKConfig", "DrammenMJKConfig", "bin", "Debug"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase(true)]
    [TestCase(false)]
    public void Find_FromBin_ReturnsProjectFolderUnderRepoRoot(bool gitIsFile)
    {
        string git = Path.Combine(_root, ".git");
        if (gitIsFile) File.WriteAllText(git, "gitdir: elsewhere"); else Directory.CreateDirectory(git);

        string? found = ConfigFolder.Find(Path.Combine(_root, "DrammenMJKConfig", "DrammenMJKConfig", "bin", "Debug"));

        Assert.That(found, Is.EqualTo(Path.Combine(_root, "DrammenMJKConfig", "DrammenMJKConfig")));
    }

    [Test]
    public void Find_NoRepoRoot_ReturnsNull() =>
        Assert.That(ConfigFolder.Find(_root), Is.Null);
}
