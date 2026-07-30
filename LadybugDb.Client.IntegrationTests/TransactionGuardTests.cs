using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// A nested <c>BEGIN TRANSACTION</c> does not merely fail at the engine - it tears down the
/// transaction already in flight and discards its writes, with no error until a later <c>COMMIT</c>
/// reports there is nothing to commit. These tests pin the client-side refusals that keep that
/// statement from ever being sent, and the engine behaviour that makes them necessary.
/// </summary>
public class TransactionGuardTests
{
    private static async Task<LadybugConnection> WithTable(LadybugDatabase db)
    {
        var conn = await db.ConnectAsync();
        await using (var _ = await conn.QueryAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }
        return conn;
    }

    private static async Task<long> CountAsync(LadybugConnection conn) =>
        await conn.Select<long>("MATCH (t:T) RETURN count(*)").FirstAsync();

    /// <summary>
    /// The engine behaviour this whole guard exists for, asserted directly so the reason is a test
    /// rather than a comment. Sent through <see cref="LadybugConnection.QueryUncheckedAsync"/>, which
    /// is the path that bypasses the guard, because the guarded path can no longer produce it.
    /// </summary>
    [Test]
    public async Task Engine_NestedBeginDestroysTheTransactionInFlight_WhichIsWhyTheGuardExists()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            await using (var _ = await conn.QueryUncheckedAsync("BEGIN TRANSACTION")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:T {id: 1})")) { }

            // The nested BEGIN the engine rejects - and takes the first transaction down with it.
            await Assert.That(async () =>
            {
                await using var _ = await conn.QueryUncheckedAsync("BEGIN TRANSACTION");
            }).Throws<LadybugException>();

            // The write is already gone: COMMIT finds nothing to commit.
            await Assert.That(async () =>
            {
                await using var _ = await conn.QueryUncheckedAsync("COMMIT");
            }).Throws<LadybugException>();

            await Assert.That(await CountAsync(conn)).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task RawNestedBegin_IsRefusedClientSideAndTheWriteSurvives()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            await using (var _ = await conn.QueryAsync("BEGIN TRANSACTION")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:T {id: 1})")) { }

            var ex = await Assert.That(async () =>
            {
                await using var _ = await conn.QueryAsync("BEGIN TRANSACTION");
            }).Throws<InvalidOperationException>();
            await Assert.That(ex!.Message).Contains("already has an active transaction");

            // The point of the refusal: the transaction is untouched, so this commits.
            await using (var _ = await conn.QueryAsync("COMMIT")) { }
            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The direction the original guard could not see: <see cref="LadybugConnection.BeginTransactionAsync"/>
    /// tracked only the transactions it opened itself, so a raw one was invisible to it and it would
    /// send the destroying <c>BEGIN</c> on the caller's behalf.
    /// </summary>
    [Test]
    public async Task BeginTransactionAsync_AfterARawBegin_IsRefusedClientSideAndTheWriteSurvives()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            await using (var _ = await conn.QueryAsync("BEGIN TRANSACTION")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:T {id: 1})")) { }

            var ex = await Assert.That(async () => await conn.BeginTransactionAsync())
                .Throws<InvalidOperationException>();
            await Assert.That(ex!.Message).Contains("raw BEGIN TRANSACTION");

            await using (var _ = await conn.QueryAsync("COMMIT")) { }
            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task RawBegin_WhileAManagedTransactionIsOpen_IsRefusedClientSide()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            await using var tx = await conn.BeginTransactionAsync();
            await using (var _ = await conn.QueryAsync("CREATE (:T {id: 1})")) { }

            await Assert.That(async () =>
            {
                await using var _ = await conn.QueryAsync("BEGIN TRANSACTION");
            }).Throws<InvalidOperationException>();

            await tx.CommitAsync();
            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The escape hatch still works: tracking a raw transaction must not make raw transactions
    /// unusable, only safe to have open.
    /// </summary>
    [Test]
    public async Task ConsecutiveRawTransactions_BothCommit()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            for (var id = 1; id <= 3; id++)
            {
                await using (var _ = await conn.QueryAsync("BEGIN TRANSACTION")) { }
                await using (var _ = await conn.QueryAsync($"CREATE (:T {{id: {id}}})")) { }
                await using (var _ = await conn.QueryAsync("COMMIT")) { }
            }

            await Assert.That(await CountAsync(conn)).IsEqualTo(3L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task RawRollback_ClosesTheTransactionSoTheNextBeginIsAccepted()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            await using (var _ = await conn.QueryAsync("BEGIN TRANSACTION")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:T {id: 1})")) { }
            await using (var _ = await conn.QueryAsync("ROLLBACK")) { }

            // Accepted, which is only possible if ROLLBACK cleared the client's tracking too.
            await using (var _ = await conn.QueryAsync("BEGIN TRANSACTION")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:T {id: 2})")) { }
            await using (var _ = await conn.QueryAsync("COMMIT")) { }

            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <c>BEGIN TRANSACTION READ ONLY</c> is a real spelling the engine accepts, so it must be tracked
    /// like any other - an untracked one would leave the destroying nested <c>BEGIN</c> reachable.
    /// </summary>
    [Test]
    public async Task ReadOnlyRawTransaction_IsTrackedToo()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            await using (var _ = await conn.QueryAsync("BEGIN TRANSACTION READ ONLY")) { }

            await Assert.That(async () => await conn.BeginTransactionAsync())
                .Throws<InvalidOperationException>();

            await using (var _ = await conn.QueryAsync("ROLLBACK")) { }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A statement that merely contains the keywords is not transaction control and must reach the
    /// engine. If classification were a substring match, this would be refused and the guard would
    /// have broken working queries to fix a different bug.
    /// </summary>
    [Test]
    public async Task StringLiteralContainingBeginTransaction_IsExecutedNotRefused()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, s STRING, PRIMARY KEY(id))")) { }

            await using (var _ = await conn.QueryAsync(
                "CREATE (:S {id: 1, s: 'BEGIN TRANSACTION'})")) { }

            var read = await conn.Select<string>("MATCH (s:S) RETURN s.s").FirstAsync();
            await Assert.That(read).IsEqualTo("BEGIN TRANSACTION");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A raw <c>COMMIT</c> closes the transaction at the engine level whichever way it was opened.
    /// The <see cref="LadybugTransaction"/> that opened it must follow, rather than going on
    /// believing it is open and later committing into nothing or rolling back what no longer exists.
    /// </summary>
    [Test]
    public async Task RawCommit_CompletesTheManagedTransactionInsteadOfDesyncingIt()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("CREATE (:T {id: 1})");

            await conn.ExecuteAsync("COMMIT");   // behind the wrapper's back

            await Assert.That(tx.IsCompleted).IsTrue();
            await Assert.That(async () => await tx.CommitAsync()).Throws<InvalidOperationException>();

            // Disposing must not issue a rollback for a transaction that no longer exists.
            await tx.DisposeAsync();
            await Assert.That(await CountAsync(conn)).IsEqualTo(1L);

            // And the connection knows it is free, so a new transaction is accepted.
            await using var next = await conn.BeginTransactionAsync();
            await next.RollbackAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>Same, for a raw <c>ROLLBACK</c>: the work is gone and the wrapper knows it.</summary>
    [Test]
    public async Task RawRollback_CompletesTheManagedTransactionAndDiscardsItsWork()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await WithTable(db);

            var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("CREATE (:T {id: 1})");

            await conn.ExecuteAsync("ROLLBACK");

            await Assert.That(tx.IsCompleted).IsTrue();
            await tx.DisposeAsync();
            await Assert.That(await CountAsync(conn)).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
