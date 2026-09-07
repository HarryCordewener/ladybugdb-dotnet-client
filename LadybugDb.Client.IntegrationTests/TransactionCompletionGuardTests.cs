using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Two ways a caller could believe work is transactional when it is not, both raised in review of
/// PR #3: statements issued through a <see cref="LadybugTransaction"/> that has already completed,
/// and transaction-control statements smuggled through the parameterized query overload, which
/// bypassed <see cref="LadybugConnection"/>'s classification and so opened a transaction the
/// nested-BEGIN guard could not see.
/// </summary>
public class TransactionCompletionGuardTests
{
    private static async Task<(LadybugDatabase Db, LadybugConnection Connection)> Open(string path)
    {
        var db = new LadybugDatabase(path);
        try
        {
            var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))");
            return (db, conn);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static async Task<long> CountAsync(LadybugConnection conn)
    {
        await using var r = await conn.QueryAsync("MATCH (t:T) RETURN count(t)");
        await foreach (var row in r) return row.GetInt64(0);
        return -1;
    }

    [Test]
    public async Task ExecutingThroughACommittedTransaction_IsRefused_NotAutoCommitted()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            var tx = await conn.BeginTransactionAsync();
            await tx.ExecuteAsync("CREATE (:T {id: 1})");
            await tx.CommitAsync();

            // The API says these run inside this transaction. There no longer is one, so running
            // them would auto-commit work the caller believes it can still roll back.
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.ExecuteAsync("CREATE (:T {id: 2})"));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.ExecuteAsync("CREATE (:T {id: $id})", new { id = 3L }));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.QueryAsync("MATCH (t:T) RETURN t.id"));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.QueryAsync("MATCH (t:T) WHERE t.id = $id RETURN t.id", new { id = 1L }));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in tx.Select<long>("MATCH (t:T) RETURN t.id")) { }
            });

            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>A rolled-back transaction is as completed as a committed one.</summary>
    [Test]
    public async Task ExecutingThroughARolledBackTransaction_IsRefused()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            var tx = await conn.BeginTransactionAsync();
            await tx.RollbackAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.ExecuteAsync("CREATE (:T {id: 1})"));
            await Assert.That(await CountAsync(conn)).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A <see cref="LadybugTransaction.Select{T}"/> stream created while the transaction was open
    /// but first enumerated after it completed must refuse too: the rows would come from outside
    /// the transaction the caller asked for.
    /// </summary>
    [Test]
    public async Task ASelectStreamEnumeratedAfterCompletion_IsRefused()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            var tx = await conn.BeginTransactionAsync();
            var stream = tx.Select<long>("MATCH (t:T) RETURN t.id");
            await tx.CommitAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in stream) { }
            });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ParameterizedTransactionControl_IsRefusedBeforeReachingTheEngine()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await Open(path);
            using var _db = db;
            await using var _conn = conn;

            var empty = new Dictionary<string, object?>();

            // Left unclassified, this opened a transaction the connection knew nothing about, and
            // the next guarded BEGIN then reached the engine and destroyed it, discarding its writes.
            await Assert.ThrowsAsync<ArgumentException>(async () => await conn.QueryAsync("BEGIN TRANSACTION", empty));
            await Assert.ThrowsAsync<ArgumentException>(async () => await conn.ExecuteAsync("COMMIT", empty));
            await Assert.ThrowsAsync<ArgumentException>(async () => await conn.ExecuteAsync("ROLLBACK", empty));

            // Nothing was opened, so the ordinary path still works and its guard is intact.
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await conn.ExecuteAsync("CREATE (:T {id: 1})");
                await tx.CommitAsync();
            }
            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
