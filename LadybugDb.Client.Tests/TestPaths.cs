namespace LadybugDb.Client.Tests;

/// <summary>
/// Shared path helpers for tests that need to locate files in the repo
/// checkout rather than the test output directory (e.g. built .nupkg
/// artifacts, or the gitignored native binaries under
/// LadybugDb.Client.Native/runtimes/).
/// </summary>
internal static class TestPaths
{
    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LadybugDb.Client.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
