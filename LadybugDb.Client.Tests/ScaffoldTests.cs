using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class ScaffoldTests
{
    [Test]
    public async Task ClientAssembly_IsReferencedAndLoadable()
    {
        var asm = typeof(LadybugDb.Client.LadybugConfig).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("LadybugDb.Client");
    }
}
