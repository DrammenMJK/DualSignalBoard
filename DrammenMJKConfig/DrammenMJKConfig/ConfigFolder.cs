namespace DrammenMJKConfig;

// The one folder that holds boards.json, hardware.json and SystemConfig.json:
// DrammenMJKConfig/DrammenMJKConfig under the repo root. Found by walking up
// from the exe to the folder with .git, so it is the same files whether the
// tool runs via `dotnet run`, from bin, or from a clone at another path --
// never copies in bin.
static class ConfigFolder
{
    const string RelativeToRepoRoot = "DrammenMJKConfig/DrammenMJKConfig";

    public static string? Find(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir != null; dir = dir.Parent)
        {
            // .git is a folder in a normal clone, a file in a worktree.
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return Path.GetFullPath(Path.Combine(dir.FullName, RelativeToRepoRoot));
        }
        return null;
    }
}
