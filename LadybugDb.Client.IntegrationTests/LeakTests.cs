using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// <see cref="NotInParallelAttribute"/> at the class level, with no constraint key: both tests
/// below measure <see cref="Environment.WorkingSet"/>, a whole-process metric with no way to scope
/// it to a single test - any concurrent memory churn from another test running at the same time
/// inflates the measured growth here, regardless of what that other test actually is. This bit
/// twice already this milestone, each time fixed by putting <c>[NotInParallel]</c> on the
/// <em>other</em> (churning) test instead - <c>TransactionConcurrentDisposalTests</c> and
/// <c>TransactionBeginBeginRaceTests</c> - which only holds until the next heavy test is added
/// (exactly what happened when <c>DecimalBidirectionalTests</c> landed and produced a third,
/// isolated failure). Chasing every future churner is unbounded; there is exactly one measurer, so
/// the fix belongs here: a global (no constraint key) <c>[NotInParallel]</c> makes both tests in
/// this class run completely alone - not concurrently with each other, and not with anything else
/// in the suite - so no future test, whatever it does, can push this measurement over its bound.
/// Do not parallelise this class, and do not "fix" a future flake here by chasing whatever else
/// happened to be running at the time - that is this exact same bug recurring a fourth time.
/// </summary>
[NotInParallel]
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

    /// <summary>
    /// Container marshalling allocates a native value per element, and every string goes
    /// through lbug_destroy_string. A missed destroy multiplies per element, so this
    /// exercises lists, maps, structs and nodes together rather than scalars.
    /// </summary>
    [Test]
    public async Task RepeatedContainerReads_DoNotGrowProcessMemory()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE L(id INT64, tags STRING[], attrs MAP(STRING,STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:L {id: 1, tags: ['a','b','c','d','e'], " +
                "attrs: map(['k1','k2','k3'],['v1','v2','v3'])})")) { }

            for (var i = 0; i < 300; i++)
            {
                await using var warm = await conn.QueryAsync("MATCH (n:L) RETURN n, n.tags, n.attrs");
                await foreach (var row in warm) { _ = row.GetValue(0).AsNode(); _ = row.GetValue(1).AsList(); _ = row.GetValue(2).AsMap(); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var baseline = Environment.WorkingSet;

            for (var i = 0; i < 3_000; i++)
            {
                await using var r = await conn.QueryAsync("MATCH (n:L) RETURN n, n.tags, n.attrs");
                await foreach (var row in r) { _ = row.GetValue(0).AsNode(); _ = row.GetValue(1).AsList(); _ = row.GetValue(2).AsMap(); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var growthMb = (Environment.WorkingSet - baseline) / 1024.0 / 1024.0;
            Console.WriteLine($"[LeakTests] baseline={baseline / 1024.0 / 1024.0:F2}MB growth={growthMb:F2}MB");
            await Assert.That(growthMb).IsLessThan(32);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
