using LadybugDb.Client;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Covers <see cref="LadybugQueryResult.ReadStringAsync"/>'s three failure branches - previously
/// only its success path had a committed test. All three now route through the same error
/// enrichment <see cref="LbugDatabaseHandle.Open"/> and <see cref="LbugConnectionHandle.Open"/>
/// already used: fold <c>lbug_get_last_error()</c> into the thrown message via
/// <see cref="NativeString.TakeOwnershipOrNull"/> rather than leaving it unconsumed for some
/// unrelated later call to misattribute.
/// </summary>
public class QueryResultErrorTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lbug-qerr-{Guid.NewGuid():N}");

    private static async Task<(LadybugDatabase db, LadybugConnection conn)> SeedOneRow(string path)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await using (var _ = await conn.QueryAsync(
            "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
        await using (var _ = await conn.QueryAsync(
            "CREATE (o:Obj {dbref: 1, name: 'Limbo'})")) { }
        return (db, conn);
    }

    [Test]
    public async Task ReadStringAsync_ColumnIndexOutOfRange_ThrowsWithColumnDetail()
    {
        var path = TempDbPath();
        try
        {
            var (db, conn) = await SeedOneRow(path);
            using var _db = db;
            await using var _conn = conn;

            // The query projects exactly one column (index 0); 5 is out of range.
            await using var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");

            var ex = await Assert.ThrowsAsync<LadybugException>(async () => await result.ReadStringAsync(5));
            await Assert.That(ex!.Message).Contains("Failed to read column 5.");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ReadStringAsync_ColumnIsNotAString_ThrowsWithColumnDetail()
    {
        var path = TempDbPath();
        try
        {
            var (db, conn) = await SeedOneRow(path);
            using var _db = db;
            await using var _conn = conn;

            // o.dbref is INT64, not STRING.
            await using var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.dbref");

            var ex = await Assert.ThrowsAsync<LadybugException>(async () => await result.ReadStringAsync(0));
            await Assert.That(ex!.Message).Contains("Column 0 is not a string.");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Pins the native precondition behind <c>ReadString</c>'s "Failed to advance to the next
    /// row." branch: <c>lbug_query_result_get_next</c> on an already-exhausted result returns a
    /// failure <see cref="lbug_state"/>, not a crash.
    /// </summary>
    /// <remarks>
    /// This deliberately calls <see cref="LbugFlatTupleHandle.GetNext"/> directly at the interop
    /// layer rather than through <see cref="LadybugQueryResult.ReadStringAsync"/>. The public
    /// method checks <c>HasNext</c> immediately before calling it, so under single-threaded,
    /// sequential use (the only use this library's API contract supports) that branch is
    /// structurally unreachable - by design, not by accident. It IS reachable through a genuine
    /// data race (concurrent callers both observing <c>HasNext == true</c> before either advances,
    /// confirmed empirically against the real engine: only one caller's
    /// <c>lbug_query_result_get_next</c> succeeds, the other's returns
    /// <c>LbugError</c>/"Runtime exception: No more tuples in QueryResult, Please check hasNext()
    /// before calling getNext()." through the exact <c>ReadString</c> code path), but that trigger
    /// is inherently non-deterministic (observed anywhere from 0 to several failures across
    /// otherwise-identical runs) and unsuitable for a committed regression test. This test instead
    /// pins the one thing that must stay true for that branch to behave correctly if it is ever
    /// hit: the native call fails safely and reports a state <see cref="LadybugQueryResult"/> can
    /// turn into a managed exception, rather than crashing.
    /// </remarks>
    [Test]
    public async Task DirectGetNext_OnExhaustedResult_ReturnsFailureStateNotCrash()
    {
        var path = TempDbPath();
        try
        {
            var (db, conn) = await SeedOneRow(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            var first = await result.ReadStringAsync(0);
            await Assert.That(first).IsEqualTo("Limbo");
            await Assert.That(result.HasNext).IsFalse();

            using var tuple = LbugFlatTupleHandle.GetNext(result.Handle, out var state);
            await Assert.That(state).IsNotEqualTo(lbug_state.LbugSuccess);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
