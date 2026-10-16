namespace DrammenMJKConfig.IntegrationTests;

// BoardConfig.Svb is loaded, not a static readonly field -- these tests
// exercise real hardware, so they load the real boards.json from the config
// folder (same file the tool uses) rather than a synthetic fixture like the
// unit tests use.
[SetUpFixture]
public class AssemblySetup
{
    [OneTimeSetUp]
    public void LoadBoards() =>
        BoardConfig.Load(Path.Combine(ConfigFolder.Find(AppContext.BaseDirectory)!, "boards.json"));
}
