using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class EngineVersionTests
{
    [Test]
    [Arguments("0.19.1", "0.19.1")]
    [Arguments("0.19.0", "0.19.1")]
    [Arguments("0.20.2", "0.19.1")]
    [Arguments("1.0.0", "0.19.1")]
    [Arguments("v0.19.3", "v0.19.1")]
    public async Task SameOrNewerMajorMinor_IsCompatible(string loaded, string pinned)
    {
        await Assert.That(EngineVersion.IsCompatible(loaded, pinned)).IsTrue();
    }

    [Test]
    [Arguments("0.18.3", "0.19.1")]
    [Arguments("0.9.9", "0.19.1")]
    public async Task OlderMinor_IsNotCompatible(string loaded, string pinned)
    {
        await Assert.That(EngineVersion.IsCompatible(loaded, pinned)).IsFalse();
    }

    [Test]
    [Arguments("unknown", "0.19.1")]
    [Arguments("", "0.19.1")]
    [Arguments("0.19.1", "garbage")]
    public async Task Unparseable_IsTreatedAsCompatible(string loaded, string pinned)
    {
        await Assert.That(EngineVersion.IsCompatible(loaded, pinned)).IsTrue();
    }

    [Test]
    public async Task Pinned_IsTheVersionFileWithoutItsV()
    {
        var file = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "third-party", "liblbug.version")).Trim();
        await Assert.That(EngineVersion.Pinned).IsEqualTo(file.TrimStart('v'));
        await Assert.That(LadybugDatabase.MinimumEngineVersion).IsEqualTo(EngineVersion.Pinned);
    }
}
