using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Regresses fix-round-2 finding 2: <see cref="LadybugDatabase.Dispose"/> and
/// <see cref="LadybugConnection.DisposeAsync"/> can legitimately race on separate threads (two
/// independent shutdown paths, not a contrived scenario) and both reach the same still-open
/// <see cref="LadybugTransaction"/>'s close-out path at once. Before this fix, that path
/// (<c>LadybugTransaction.EnsureClosedForDispose</c>) used a plain <c>if (_completed) return;</c>
/// check with no synchronization: forced with a <see cref="Barrier"/>, both threads observed
/// "not completed yet" and both issued a native <c>ROLLBACK</c> through the same connection
/// concurrently, on effectively every contended attempt. The fix makes that transition atomic
/// (<see cref="Interlocked.CompareExchange(ref int, int, int)"/>), so exactly one thread, ever,
/// performs the rollback.
/// </summary>
public class TransactionConcurrentDisposalTests
{
    /// <summary>
    /// Forces <see cref="LadybugDatabase.Dispose"/> (on one thread) and
    /// <see cref="LadybugConnection.DisposeAsync"/> (on another) to start at the same instant via
    /// a <see cref="Barrier"/>, for the same connection with the same still-open transaction, and
    /// asserts both threads complete without throwing, the transaction ends up completed, and the
    /// uncommitted row was rolled back exactly once (not corrupted by two concurrent native
    /// <c>ROLLBACK</c>s racing on one connection). Run 200 times, matching the contention count
    /// used to originally find and confirm this race.
    /// </summary>
    /// <remarks>
    /// <see cref="NotInParallelAttribute"/>: 200 iterations of a full database-open-through-close
    /// lifecycle is enough churn in this one process that running it concurrently with
    /// <c>LeakTests.RepeatedQueries_DoNotGrowProcessMemory</c> - which measures
    /// <see cref="Environment.WorkingSet"/>, a whole-process metric with no way to scope it to one
    /// test - intermittently inflated that unrelated test's measured growth past its bound.
    /// Observed directly: failed once in several consecutive full-suite runs, always exactly this
    /// combination, never when either test ran alone. Isolating this test removes it as a
    /// contributor without touching the leak test's bound, which project policy keeps fixed
    /// (quarantine a flaky leak test, never raise the number).
    /// </remarks>
    [Test]
    [Repeat(199)]
    [NotInParallel]
    public async Task ConcurrentDatabaseAndConnectionDispose_ClosesTransactionExactlyOnce()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            var tx = await conn.BeginTransactionAsync();
            await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

            using var barrier = new Barrier(2);
            var dbTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                db.Dispose();
            });
            var connTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await conn.DisposeAsync();
            });

            await Task.WhenAll(dbTask, connTask);

            await Assert.That(tx.IsCompleted).IsTrue();

            // The direct proof: exactly one of the two racing threads ever won the claim and
            // issued the native ROLLBACK - not merely "the final state happened to be correct",
            // which (per the fix-round-2 investigation) both the racy and fixed versions of this
            // code can produce even when two threads both reached the native call.
            await Assert.That(tx.CompletionClaimCount).IsEqualTo(1);

            using var verifyDb = new LadybugDatabase(path);
            await using var verifyConn = await verifyDb.ConnectAsync();
            await using var result = await verifyConn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in result)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
