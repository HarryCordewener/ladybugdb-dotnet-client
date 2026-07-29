using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class TransactionTests
{
    [Test]
    public async Task Commit_PersistsWork()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }
                await tx.CommitAsync();
            }

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposeWithoutCommit_RollsBack()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using var _ = await conn.QueryAsync("CREATE (n:T {id: 1})");
                // no CommitAsync - dispose must roll back
            }

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DoubleCommit_ThrowsInvalidOperation()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using var tx = await conn.BeginTransactionAsync();
            await tx.CommitAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Regresses a fix-round-1 finding: calling <c>BeginTransactionAsync</c> a second time on a
    /// connection that already has one open used to send a nested <c>BEGIN TRANSACTION</c> to the
    /// engine. The engine rejected it with a usable <see cref="LadybugException"/>, but as a side
    /// effect left the FIRST transaction invalid engine-side too - a subsequent
    /// <c>tx1.CommitAsync()</c> then failed with <see cref="LadybugException"/> ("No active
    /// transaction for COMMIT") instead of honouring the documented contract, because the managed
    /// <see cref="LadybugTransaction"/> wrapper had no way to know the engine had invalidated it
    /// out from under it. The fix detects the nested attempt client-side and never sends the
    /// second <c>BEGIN TRANSACTION</c> at all, so the first transaction is never touched and stays
    /// fully usable.
    /// </summary>
    [Test]
    public async Task NestedBeginTransaction_ThrowsAndFirstTransactionStaysValid()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using var tx1 = await conn.BeginTransactionAsync();
            await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await conn.BeginTransactionAsync());

            // The first transaction must still be valid - not silently invalidated by the
            // rejected nested attempt above.
            await Assert.That(tx1.IsCompleted).IsFalse();
            await tx1.CommitAsync();
            await Assert.That(tx1.IsCompleted).IsTrue();

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
