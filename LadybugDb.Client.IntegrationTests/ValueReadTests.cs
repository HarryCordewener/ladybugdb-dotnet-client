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
        var path = Path.Combine(Path.GetTempPath(), $"lbug-val-{Guid.NewGuid():N}");
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
        finally { Cleanup(path); }
    }

    internal static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
