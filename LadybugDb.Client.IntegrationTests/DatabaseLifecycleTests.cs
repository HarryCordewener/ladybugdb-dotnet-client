using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class DatabaseLifecycleTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lbug-test-{Guid.NewGuid():N}");

    [Test]
    public async Task OpenDatabase_CreateTable_AndInsertRow()
    {
        var path = TempDbPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            // INT64 primary key: a STRING key costs ~4.8x at equal row count.
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 42, name: 'Limbo'})")) { }

            await using var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.HasNext).IsTrue();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Test]
    public async Task InvalidCypher_ThrowsLadybugExceptionCarryingTheStatement()
    {
        var path = TempDbPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            const string bad = "MATCH (o:NoSuchTable) RETURN o.nope";
            var ex = await Assert.ThrowsAsync<LadybugException>(
                async () => await conn.QueryAsync(bad));

            await Assert.That(ex!.Statement).IsEqualTo(bad);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
