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
            await Assert.That(result.HasNext).IsTrue();
        }
        finally
        {
            TestDatabase.Cleanup(path);
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
            TestDatabase.Cleanup(path);
        }
    }

    /// <summary>
    /// Triggers a genuine write conflict against the real engine: one connection holds an open
    /// write transaction (<c>BEGIN TRANSACTION</c>, then an uncommitted write) while a second
    /// connection attempts its own write. LadybugDB permits exactly one write transaction at a
    /// time and raises rather than queueing, so the second write must fail - and this asserts it
    /// fails as the typed, retryable <see cref="LadybugWriteConflictException"/>, not a plain
    /// <see cref="LadybugException"/>.
    /// </summary>
    /// <remarks>
    /// This is the real-engine counterpart to
    /// <c>QueryFailureClassifierTests.RealEngineMessage_ClassifiesAsWriteConflict</c>, which
    /// pins the exact message text this test observes so the classifier stays covered without
    /// needing the real engine on every run. If the message asserted here ever needs to change,
    /// that unit test's constant must change with it.
    /// </remarks>
    [Test]
    public async Task ConcurrentWrite_ThrowsLadybugWriteConflictException()
    {
        var path = TempDbPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn1 = await db.ConnectAsync();
            await using var conn2 = await db.ConnectAsync();

            await using (var _ = await conn1.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }

            await using (var _ = await conn1.QueryAsync("BEGIN TRANSACTION")) { }
            await using (var _ = await conn1.QueryAsync("CREATE (o:Obj {dbref: 1, name: 'A'})")) { }

            const string conflicting = "CREATE (o:Obj {dbref: 2, name: 'B'})";
            var ex = await Assert.ThrowsAsync<LadybugWriteConflictException>(
                async () => await conn2.QueryAsync(conflicting));

            await Assert.That(ex!.Statement).IsEqualTo(conflicting);
            await Assert.That(ex.Message).Contains("write transaction");

            await using (var _ = await conn1.QueryAsync("COMMIT")) { }
        }
        finally
        {
            TestDatabase.Cleanup(path);
        }
    }
}
