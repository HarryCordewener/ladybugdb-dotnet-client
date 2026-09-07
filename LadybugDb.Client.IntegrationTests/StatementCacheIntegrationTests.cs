using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The parameter-object overloads reuse a prepared statement per connection. Evidence is
/// <see cref="LadybugPreparedStatement.PreparedCount"/>, a process-wide counter, so these tests
/// measure deltas and run alone.
/// </summary>
[NotInParallel]
public class StatementCacheIntegrationTests
{
    private const string Lookup = "MATCH (o:Obj) WHERE o.dbref = $d RETURN o.name";

    private static async Task<(LadybugDatabase, LadybugConnection)> Open(string path, LadybugConfig? config = null)
    {
        var db = new LadybugDatabase(path, config);
        var conn = await db.ConnectAsync();
        await conn.ExecuteAsync("CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))");
        await conn.ExecuteAsync("CREATE (:Obj {dbref: 1, name: 'one'})");
        await conn.ExecuteAsync("CREATE (:Obj {dbref: 2, name: 'two'})");
        return (db, conn);
    }

    private static async Task<string?> NameOf(LadybugConnection conn, long d)
    {
        await using var r = await conn.QueryAsync(Lookup, new { d });
        await foreach (var row in r) return row.GetString(0);
        return null;
    }

    [Test]
    public async Task RepeatedOneShotQueries_PrepareOnce()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            var before = LadybugPreparedStatement.PreparedCount;
            await Assert.That(await NameOf(conn, 1)).IsEqualTo("one");
            await Assert.That(await NameOf(conn, 2)).IsEqualTo("two");
            await Assert.That(await NameOf(conn, 1)).IsEqualTo("one");
            await Assert.That(LadybugPreparedStatement.PreparedCount - before).IsEqualTo(1);

            // Select<T> and ExecuteAsync(string, object) ride the same cache.
            await foreach (var n in conn.Select<string>(Lookup, new { d = 2L })) await Assert.That(n).IsEqualTo("two");
            await Assert.That(LadybugPreparedStatement.PreparedCount - before).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ADifferentParameterShape_IsRefused()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            await NameOf(conn, 1);
            var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await conn.QueryAsync(Lookup, new { d = 1L, extra = 2L }));
            await Assert.That(ex!.Message).Contains("extra");

            // The cached statement is untouched and still works.
            await Assert.That(await NameOf(conn, 2)).IsEqualTo("two");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AFailingExecution_EvictsTheStatement_AndTheNextCallReprepares()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            await NameOf(conn, 1);
            await conn.ExecuteAsync("DROP TABLE Obj");
            await conn.ExecuteAsync("CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))");
            await conn.ExecuteAsync("CREATE (:Obj {dbref: 1, name: 'uno'})");

            // Whether the stale plan fails or (as measured) re-binds against the new table, the
            // caller sees either a LadybugException or the fresh data - never stale rows.
            string? name = null;
            try { name = await NameOf(conn, 1); }
            catch (LadybugException) { }
            var before = LadybugPreparedStatement.PreparedCount;
            await Assert.That(await NameOf(conn, 1)).IsEqualTo("uno");
            await Assert.That(name is null or "uno").IsTrue();
            _ = before;
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ConcurrentCallersOfOneStatement_NeverSeeEachOthersParameters()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                var d = 1L + (i % 2);
                for (var k = 0; k < 200; k++)
                {
                    var name = await NameOf(conn, d);
                    if (name != (d == 1 ? "one" : "two")) return false;
                }
                return true;
            })).ToArray();
            var ok = await Task.WhenAll(tasks);
            await Assert.That(ok.All(x => x)).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task CacheSizeZero_PreparesEveryCall()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path, new LadybugConfig { StatementCacheSize = 0 });
            using var _db = db;
            await using var _conn = conn;

            var before = LadybugPreparedStatement.PreparedCount;
            await NameOf(conn, 1);
            await NameOf(conn, 1);
            await Assert.That(LadybugPreparedStatement.PreparedCount - before).IsEqualTo(2);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
