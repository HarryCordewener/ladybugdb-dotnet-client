using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ValueReadTests
{
    [Test]
    public async Task ReadString_ReturnsTheStoredValue()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 7, name: 'Master Room'})")) { }

            await using var result = await conn.QueryAsync(
                "MATCH (o:Obj) WHERE o.dbref = 7 RETURN o.name");

            string? name = null;
            await foreach (var row in result)
                name = row.GetValue(0).AsString();
            await Assert.That(name).IsEqualTo("Master Room");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Regresses the missing <c>LBUG_INTERNAL_ID</c> arm in <c>ValueReader</c>'s type switch:
    /// <c>RETURN id(n)</c> - a bare internal id, not one embedded in a node/rel value - used to fall
    /// through to <see cref="LadybugType.Unsupported"/> with a <see langword="null"/> payload, so
    /// even <see cref="LadybugValue.AsString"/> (which <see cref="LadybugType.Unsupported"/>'s own
    /// XML doc recommends as the fallback) threw. It now reads as <see cref="LadybugType.InternalId"/>,
    /// matching the same table/offset the node's own <see cref="LadybugNode.Id"/> reports for the
    /// identical row.
    /// </summary>
    [Test]
    public async Task ReadInternalId_FromBareIdFunction_ReturnsTheNodesId()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 7, name: 'Master Room'})")) { }

            await using var r = await conn.QueryAsync("MATCH (o:Obj) RETURN o, id(o)");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            var node = row.GetValue(0).AsNode();
            var idValue = row.GetValue(1);

            await Assert.That(idValue.Type).IsEqualTo(LadybugType.InternalId);
            await Assert.That(idValue.AsInternalId()).IsEqualTo(node.Id);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
