using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class LeakTests
{
    /// <summary>
    /// The C API has ten distinct destroy/free entry points and every returned string must be
    /// released. A leak here is invisible in functional tests and fatal in a long-running server,
    /// so it gets a test that fails when it regresses.
    /// </summary>
    [Test]
    public async Task RepeatedQueries_DoNotGrowProcessMemory()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 1, name: 'seed'})")) { }

            for (var i = 0; i < 500; i++)
            {
                await using var warm = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
                await using var warmEnumerator = warm.GetAsyncEnumerator();
                _ = await warmEnumerator.MoveNextAsync();
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = Environment.WorkingSet;

            for (var i = 0; i < 5_000; i++)
            {
                await using var r = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
                await using var enumerator = r.GetAsyncEnumerator();
                _ = await enumerator.MoveNextAsync();
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var after = Environment.WorkingSet;

            var growthMb = (after - baseline) / 1024.0 / 1024.0;
            Console.WriteLine($"[LeakTests] baseline={baseline / 1024.0 / 1024.0:F2}MB after={after / 1024.0 / 1024.0:F2}MB growth={growthMb:F2}MB");
            await Assert.That(growthMb).IsLessThan(32);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
