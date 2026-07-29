namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Shared temp-database cleanup for integration tests. A LadybugDB database at a given path is a
/// single file plus <c>.wal</c>/<c>.shadow</c>/<c>.lock</c>/<c>.tmp</c> sibling files - not a
/// directory - so cleanup must remove all four sibling files. The directory delete is kept
/// alongside it as a defensive fallback with no documented guarantee it is unnecessary.
/// </summary>
internal static class TestDatabase
{
    /// <summary>Generates a fresh, unused temp-database path for one test.</summary>
    internal static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"lbug-test-{Guid.NewGuid():N}");

    internal static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
