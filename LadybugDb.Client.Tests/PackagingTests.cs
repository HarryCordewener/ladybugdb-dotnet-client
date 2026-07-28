using System.IO.Compression;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LadybugDb.Client.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>
    /// Locates the built .nupkg for the given package id.
    ///
    /// A naive "{id}.*.nupkg" glob is not specific enough: since "*" spans
    /// dots, searching for "LadybugDb.Client" would also match
    /// "LadybugDb.Client.Native.1.0.0.nupkg" (a different package that
    /// happens to have the target id as a filename prefix). Anchor with a
    /// regex requiring the id to be followed immediately by a version
    /// number, so "LadybugDb.Client.Native.*" cannot satisfy a lookup for
    /// "LadybugDb.Client". This also naturally excludes .symbols.nupkg and
    /// any other stale/mismatched artifact left over from a prior build.
    /// </summary>
    private static string? FindPackage(string id)
    {
        var pattern = new Regex($"^{Regex.Escape(id)}\\.\\d.*\\.nupkg$");
        return Directory.EnumerateFiles(RepoRoot(), "*.nupkg", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).Contains(".symbols.", StringComparison.Ordinal))
            .Where(p => pattern.IsMatch(Path.GetFileName(p)))
            .FirstOrDefault();
    }

    [Test]
    public async Task ManagedPackage_ShipsNoNativeBinaries()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();

        using var zip = ZipFile.OpenRead(pkg!);
        var natives = zip.Entries.Where(e => e.FullName.StartsWith("runtimes/")).ToList();
        await Assert.That(natives).IsEmpty();
    }

    [Test]
    public async Task NativePackage_ShipsAllSixRuntimeIdentifiers()
    {
        var pkg = FindPackage("LadybugDb.Client.Native");
        await Assert.That(pkg).IsNotNull();

        using var zip = ZipFile.OpenRead(pkg!);
        string[] rids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];
        foreach (var rid in rids)
        {
            var has = zip.Entries.Any(e => e.FullName.StartsWith($"runtimes/{rid}/native/"));
            await Assert.That(has).IsTrue();
        }
    }
}
