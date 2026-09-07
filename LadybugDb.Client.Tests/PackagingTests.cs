using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    /// <summary>Loads the package's nuspec. Element names are compared by local name: the nuspec has a default namespace.</summary>
    private static async Task<XDocument> ReadNuspecAsync(string pkg)
    {
        using var zip = ZipFile.OpenRead(pkg);
        var nuspec = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspec.Open();
        return await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
    }

    private static XElement? MetadataElement(XDocument nuspec, string localName) =>
        nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    /// <summary>
    /// nuget.org renders <c>&lt;icon&gt;</c> from a file inside the package; a dangling reference
    /// (declared but not packed) is a silent blank on the gallery page, so both halves are checked.
    /// </summary>
    [Test]
    public async Task ManagedPackage_ShipsAndDeclaresIcon()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();

        var nuspec = await ReadNuspecAsync(pkg!);
        await Assert.That(MetadataElement(nuspec, "icon")?.Value).IsEqualTo("icon.png");

        using var zip = ZipFile.OpenRead(pkg!);
        await Assert.That(zip.Entries.Select(e => e.FullName)).Contains("icon.png");
    }

    [Test]
    public async Task ManagedPackage_HasSearchTags()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();

        var nuspec = await ReadNuspecAsync(pkg!);
        var tags = (MetadataElement(nuspec, "tags")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var expected in new[] { "ladybugdb", "graph", "cypher", "embedded" })
            await Assert.That(tags).Contains(expected);
    }

    /// <summary>
    /// Source Link's <c>commit</c> attribute is what lets a debugger fetch the exact sources a
    /// package was built from; it is only emitted when <c>PublishRepositoryUrl</c> is on and the
    /// build ran inside a git checkout.
    /// </summary>
    [Test]
    public async Task ManagedPackage_RecordsSourceCommit()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();

        var nuspec = await ReadNuspecAsync(pkg!);
        var repository = MetadataElement(nuspec, "repository");
        await Assert.That(repository).IsNotNull();
        await Assert.That((string?)repository!.Attribute("url")).IsEqualTo("https://github.com/HarryCordewener/ladybugdb-dotnet-client");
        await Assert.That((string?)repository.Attribute("commit") ?? string.Empty).Matches("^[0-9a-f]{40}$");
    }

    [Test]
    public async Task ManagedPackage_HasSymbolPackageBesideIt()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();

        var snupkg = Path.ChangeExtension(pkg!, ".snupkg");
        await Assert.That(File.Exists(snupkg)).IsTrue();

        using var zip = ZipFile.OpenRead(snupkg);
        await Assert.That(zip.Entries.Select(e => e.FullName)).Contains("lib/net10.0/LadybugDb.Client.pdb");
    }

    /// <summary>
    /// <c>IsAotCompatible=true</c> stamps the assembly with this metadata (since .NET 10), which
    /// consumers' <c>VerifyReferenceAotCompatibility</c> check reads. Losing the flag would be a
    /// silent regression for every AOT consumer, hence the guard.
    /// </summary>
    [Test]
    public async Task ManagedAssembly_DeclaresAotCompatibility()
    {
        var value = typeof(LadybugDatabase).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "IsAotCompatible")?.Value;
        await Assert.That(value).IsEqualTo("True");
    }

    /// <summary>
    /// The two packages ship together at one version (release.yml packs both from the same
    /// <c>-p:Version</c>), and the Extensions package is compiled against exactly that core. A
    /// project reference packs as a <c>&gt;= version</c> dependency by default, which would let
    /// NuGet pair this package with any newer core, including one whose public API it was not
    /// built against; the exact range <c>[version]</c> closes that.
    /// </summary>
    [Test]
    public async Task ExtensionsPackage_DependsOnTheCoreAtExactlyItsOwnVersion()
    {
        var core = FindPackage("LadybugDb.Client");
        var extensions = FindPackage("LadybugDb.Client.Extensions");
        await Assert.That(core).IsNotNull();
        await Assert.That(extensions).IsNotNull();

        var coreVersion = MetadataElement(await ReadNuspecAsync(core!), "version")?.Value;
        var nuspec = await ReadNuspecAsync(extensions!);
        await Assert.That(MetadataElement(nuspec, "version")?.Value).IsEqualTo(coreVersion);

        var dependency = nuspec.Descendants()
            .Single(e => e.Name.LocalName == "dependency" && (string?)e.Attribute("id") == "LadybugDb.Client");
        await Assert.That((string?)dependency.Attribute("version")).IsEqualTo($"[{coreVersion}]");
    }

    /// <summary>Same gallery presence as the core: the icon and README are declared and packed.</summary>
    [Test]
    public async Task ExtensionsPackage_ShipsIconAndReadme()
    {
        var pkg = FindPackage("LadybugDb.Client.Extensions");
        await Assert.That(pkg).IsNotNull();

        var nuspec = await ReadNuspecAsync(pkg!);
        await Assert.That(MetadataElement(nuspec, "icon")?.Value).IsEqualTo("icon.png");
        await Assert.That(MetadataElement(nuspec, "readme")?.Value).IsEqualTo("README.md");

        using var zip = ZipFile.OpenRead(pkg!);
        var entries = zip.Entries.Select(e => e.FullName).ToList();
        await Assert.That(entries).Contains("icon.png");
        await Assert.That(entries).Contains("README.md");
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
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
        // The package description names LadybugDB.Native on purpose (it tells the consumer what to
        // add), so only the declared <dependency> ids are inspected, not the whole nuspec text.
        var dependencyIds = doc.Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(e => (string?)e.Attribute("id") ?? string.Empty)
            .ToList();
        await Assert.That(dependencyIds.Where(id => id.Contains("Native", StringComparison.OrdinalIgnoreCase))).IsEmpty();
    }
}
