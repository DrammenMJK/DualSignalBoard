namespace DrammenMJKConfig.IntegrationTests;

// BoardConfig.Svb is loaded, not a static readonly field -- these tests
// exercise real hardware, so they should load the real boards.json (linked
// into this project's output, see the .csproj) rather than a synthetic
// fixture like the unit tests use.
[SetUpFixture]
public class AssemblySetup
{
    [OneTimeSetUp]
    public void LoadBoards() => BoardConfig.Load("boards.json");
}
