using System.IO.Compression;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class PackagingTests
{
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
        return Directory.EnumerateFiles(TestPaths.RepoRoot(), "*.nupkg", SearchOption.AllDirectories)
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

    /// <summary>
    /// The engine binaries come from upstream's <c>LadybugDB.Native</c> packages, chosen by the
    /// consumer, and the managed package must not drag one in: that choice (meta package or a single
    /// RID, and which engine version) is theirs, and a hidden dependency would also pin them to the
    /// version this repository happened to test against.
    /// </summary>
    [Test]
    public async Task ManagedPackage_DeclaresNoNativeDependency()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();
        using var zip = ZipFile.OpenRead(pkg!);
        var nuspec = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspec.Open();
        var doc = await System.Xml.Linq.XDocument.LoadAsync(stream, System.Xml.Linq.LoadOptions.None, CancellationToken.None);
        // The package description names LadybugDB.Native on purpose (it tells the consumer what to
        // add), so only the declared <dependency> ids are inspected, not the whole nuspec text.
        var dependencyIds = doc.Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(e => (string?)e.Attribute("id") ?? string.Empty)
            .ToList();
        await Assert.That(dependencyIds.Where(id => id.Contains("Native", StringComparison.OrdinalIgnoreCase))).IsEmpty();
    }
}
