using LadybugDb.Client;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// The two failure branches that used to live here - an out-of-range column index and a
/// type-mismatched column, both raised by the old <c>ReadStringAsync</c> reading one column at a
/// time straight off the native tuple - went away with that method itself (Task 4):
/// <see cref="LadybugQueryResult.GetAsyncEnumerator"/> materializes every column of a row eagerly
/// into a <see cref="LadybugRow"/>, so "index out of range" is now a plain array-bounds check on
/// already-marshalled managed data (<see cref="LadybugRow.GetValue"/>), not a native call that can
/// fail with an engine-side error message, and "wrong type" is <see cref="LadybugValue"/>'s own
/// <see cref="InvalidOperationException"/> contract (already covered by
/// <see cref="ScalarValueTests.WrongAccessor_ThrowsInvalidOperationNotGarbage"/>), not a
/// <see cref="LadybugException"/>. What remains here pins the one native precondition that still
/// matters after the rewrite: advancing past the last row fails safely.
/// </summary>
public class QueryResultErrorTests
{
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

    /// <summary>
    /// Pins the native precondition behind <see cref="LadybugQueryResult.GetAsyncEnumerator"/>'s
    /// "Failed to advance to the next row." branch: <c>lbug_query_result_get_next</c> on an
    /// already-exhausted result returns a failure <see cref="lbug_state"/>, not a crash.
    /// </summary>
    /// <remarks>
    /// This deliberately calls <see cref="LbugFlatTupleHandle.GetNext"/> directly at the interop
    /// layer rather than through the public enumerator. The enumerator checks <c>has_next</c>
    /// immediately before calling it, so under single-threaded, sequential use (the only use this
    /// library's API contract supports) that branch is structurally unreachable - by design, not
    /// by accident. It IS reachable through a genuine data race (concurrent callers both observing
    /// <c>has_next == true</c> before either advances, confirmed empirically against the real
    /// engine: only one caller's <c>lbug_query_result_get_next</c> succeeds, the other's returns
    /// <c>LbugError</c>/"Runtime exception: No more tuples in QueryResult, Please check hasNext()
    /// before calling getNext()." through the exact enumerator code path), but that trigger is
    /// inherently non-deterministic (observed anywhere from 0 to several failures across
    /// otherwise-identical runs) and unsuitable for a committed regression test. This test instead
    /// pins the one thing that must stay true for that branch to behave correctly if it is ever
    /// hit: the native call fails safely and reports a state <see cref="LadybugQueryResult"/> can
    /// turn into a managed exception, rather than crashing.
    /// </remarks>
    [Test]
    public async Task DirectGetNext_OnExhaustedResult_ReturnsFailureStateNotCrash()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await SeedOneRow(path);
            using var _db = db;
            await using var _conn = conn;

            await using var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            string? first = null;
            await foreach (var row in result)
                first = row.GetValue(0).AsString();
            await Assert.That(first).IsEqualTo("Limbo");
            await Assert.That(result.HasNext).IsFalse();

            using var tuple = LbugFlatTupleHandle.GetNext(result.Handle, out var state);
            await Assert.That(state).IsNotEqualTo(lbug_state.LbugSuccess);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
