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

            var name = await result.ReadStringAsync(0);
            await Assert.That(name).IsEqualTo("Master Room");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
