using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class DatabaseLifecycleTests
{
    [Test]
    public async Task OpenDatabase_CreateTable_AndInsertRow()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            // INT64 primary key: a STRING key costs ~4.8x at equal row count.
            await conn.ExecuteAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))");
            await conn.ExecuteAsync(
                "CREATE (o:Obj {dbref: 42, name: 'Limbo'})");

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
        var path = TestDatabase.NewPath();
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
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn1 = await db.ConnectAsync();
            await using var conn2 = await db.ConnectAsync();

            await conn1.ExecuteAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))");

            await conn1.ExecuteAsync("BEGIN TRANSACTION");
            await conn1.ExecuteAsync("CREATE (o:Obj {dbref: 1, name: 'A'})");

            const string conflicting = "CREATE (o:Obj {dbref: 2, name: 'B'})";
            var ex = await Assert.ThrowsAsync<LadybugWriteConflictException>(
                async () => await conn2.QueryAsync(conflicting));

            await Assert.That(ex!.Statement).IsEqualTo(conflicting);
            await Assert.That(ex.Message).Contains("write transaction");

            await conn1.ExecuteAsync("COMMIT");
        }
        finally
        {
            TestDatabase.Cleanup(path);
        }
    }

    /// <summary>
    /// The same conflict, reached through <see cref="LadybugConnection.PrepareAsync"/> instead of
    /// a query: preparing a WRITE statement contends for the engine's single writer slot exactly as
    /// executing one does, so it can fail with the same conflict message. It must surface as the
    /// same retryable <see cref="LadybugWriteConflictException"/>, not as a plain
    /// <see cref="LadybugException"/> a retry loop would treat as fatal. Found by the benchmark
    /// workload's concurrent-writer section, where every writer prepares its statements on its own
    /// connection while the others are already writing.
    /// </summary>
    [Test]
    public async Task ConcurrentPrepareOfWriteStatement_ThrowsLadybugWriteConflictException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn1 = await db.ConnectAsync();
            await using var conn2 = await db.ConnectAsync();

            await conn1.ExecuteAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))");

            await conn1.ExecuteAsync("BEGIN TRANSACTION");
            await conn1.ExecuteAsync("CREATE (o:Obj {dbref: 1, name: 'A'})");

            const string conflicting = "CREATE (o:Obj {dbref: $d, name: 'B'})";
            var ex = await Assert.ThrowsAsync<LadybugWriteConflictException>(
                async () => await conn2.PrepareAsync(conflicting));

            await Assert.That(ex!.Statement).IsEqualTo(conflicting);
            await Assert.That(ex.Message).Contains("write transaction");

            await conn1.ExecuteAsync("COMMIT");
        }
        finally
        {
            TestDatabase.Cleanup(path);
        }
    }

    /// <summary>
    /// The engine comes from upstream's <c>LadybugDB.Native</c> package, whose version is the engine
    /// version, so the library this process loaded must report at least the version this client was
    /// generated against - and the constructor's compatibility check must accept it.
    /// </summary>
    [Test]
    public async Task EngineVersion_IsReportedAndAtLeastTheMinimum()
    {
        var loaded = LadybugDatabase.EngineVersion;
        await Assert.That(string.IsNullOrWhiteSpace(loaded)).IsFalse();
        await Assert.That(EngineVersion.IsCompatible(loaded, LadybugDatabase.MinimumEngineVersion)).IsTrue();

        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
        }
        finally
        {
            TestDatabase.Cleanup(path);
        }
    }
}
