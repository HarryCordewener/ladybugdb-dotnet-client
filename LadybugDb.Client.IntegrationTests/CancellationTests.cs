using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// A cancelled token interrupts the running query through <c>lbug_connection_interrupt</c>: the
/// call throws <see cref="OperationCanceledException"/> promptly, and the connection is usable
/// afterwards. Also pins the engine behaviour the design depends on: an interrupt that lands after
/// a query already finished does not poison the next one.
/// </summary>
public class CancellationTests
{
    /// <summary>
    /// Slow in CPU, cheap in memory: two small lists cross-joined into 400 million iterations
    /// (about 1.5 s here, 130 MB). Not one big <c>range()</c> - the engine materializes a range as
    /// a list, and a 30,000,000-element one reached 13 GB and got the test process OOM-killed.
    /// </summary>
    private const string SlowQuery =
        "UNWIND range(1, 20000) AS a UNWIND range(1, 20000) AS b WITH a * b AS x WHERE x % 7 = 0 RETURN count(x)";

    [Test]
    public async Task CancellingTheToken_InterruptsTheRunningQuery()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await conn.QueryAsync(SlowQuery, cts.Token));
            sw.Stop();

            await Assert.That(ex!.CancellationToken).IsEqualTo(cts.Token);
            await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));

            await using var r = await conn.QueryAsync("RETURN 1");
            await foreach (var row in r) await Assert.That(row.GetInt64(0)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A token that fires while the engine is still parsing and planning (here: one millisecond
    /// in) lands before the engine clears its interrupt flag at execution start. The scope
    /// re-sends the interrupt until the call returns, so the query is still cancelled instead of
    /// running to completion.
    /// </summary>
    [Test]
    public async Task CancellingDuringPlanning_StillInterruptsTheQuery()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
            var sw = Stopwatch.StartNew();
            // The throw is the proof the interrupt landed: a query that ran to completion returns
            // its result. The time bound only guards against a hang - a shared CI runner has taken
            // 1.4 s here where this host takes milliseconds.
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await conn.QueryAsync(SlowQuery, cts.Token));
            await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task CancellingAPreparedStatement_InterruptsIt()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using var stmt = await conn.PrepareAsync(
                "UNWIND range(1, $n) AS a UNWIND range(1, $n) AS b WITH a * b AS x WHERE x % 7 = 0 RETURN count(x)");
            stmt.Bind("n", 20_000L);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await stmt.ExecuteAsync(cts.Token));

            // 1..70 squared: a*b divisible by 7 when a or b is - 10*70 + 70*10 - 10*10 pairs.
            stmt.Bind("n", 70L);
            await using var r = await stmt.ExecuteAsync();
            await foreach (var row in r) await Assert.That(row.GetInt64(0)).IsEqualTo(1300L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ATokenCancelledAfterCompletion_DoesNotAffectTheNextQuery()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            using var cts = new CancellationTokenSource();
            await using (var r = await conn.QueryAsync("RETURN 1", cts.Token)) { _ = r.HasNext; }
            await cts.CancelAsync();

            await using var r2 = await conn.QueryAsync("UNWIND range(1, 1000) AS a UNWIND range(1, 1000) AS b RETURN count(a)");
            await foreach (var row in r2) await Assert.That(row.GetInt64(0)).IsEqualTo(1_000_000L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task AnAlreadyCancelledToken_ThrowsBeforeReachingTheEngine()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            var cancelled = new CancellationToken(canceled: true);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await conn.QueryAsync("RETURN 1", cancelled));
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await conn.QueryAsync("RETURN $x", new { x = 1L }, cancelled));
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
