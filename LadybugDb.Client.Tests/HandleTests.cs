using LadybugDb.Client;
using LadybugDb.Client.Interop;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class HandleTests
{
    [Test]
    public async Task NewHandle_IsInvalidBeforeAllocation()
    {
        using var h = new UnallocatedHandle();
        await Assert.That(h.IsInvalid).IsTrue();
    }

    [Test]
    public async Task LadybugException_CarriesTheFailingStatement()
    {
        var ex = new LadybugException("boom", "MATCH (n) RETURN n");
        await Assert.That(ex.Statement).IsEqualTo("MATCH (n) RETURN n");
        await Assert.That(ex.Message).Contains("boom");
    }

    private sealed class UnallocatedHandle : LbugStructHandle
    {
        protected override bool ReleaseHandle() => true;
    }
}
