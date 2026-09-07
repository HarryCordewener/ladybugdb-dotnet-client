using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Every disposable type implements <see cref="IDisposable"/> alongside
/// <see cref="IAsyncDisposable"/>, and the two are equivalent: every operation completes
/// synchronously, so a plain <c>using</c> is as correct as <c>await using</c>.
/// </summary>
public class SyncDisposalTests
{
    [Test]
    public async Task UsingBlocks_DisposeEveryType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))");
            using (var tx = await conn.BeginTransactionAsync())
            {
                await conn.ExecuteAsync("CREATE (:T {id: 1})");
                await tx.CommitAsync();
            }
            using var stmt = await conn.PrepareAsync("MATCH (t:T) WHERE t.id = $id RETURN t.id");
            stmt.Bind("id", 1L);
            using var result = await stmt.ExecuteAsync();
            await Assert.That(result.HasNext).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposeAndDisposeAsync_AreIdempotentTogether()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            var result = await conn.QueryAsync("RETURN 1");
            var liveBefore = LadybugQueryResult.LiveCount;
            result.Dispose();
            await result.DisposeAsync();
            result.Dispose();
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(liveBefore - 1);

            conn.Dispose();
            await conn.DisposeAsync();
            await Assert.That(async () => await conn.QueryAsync("RETURN 1")).Throws<ObjectDisposedException>();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>A synchronously disposed transaction rolls back exactly as an asynchronously disposed one.</summary>
    [Test]
    public async Task SyncDisposeOfAnOpenTransaction_RollsBack()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))");

            var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("CREATE (:T {id: 1})");
            tx.Dispose();

            await using var r = await conn.QueryAsync("MATCH (t:T) RETURN count(t)");
            await foreach (var row in r) await Assert.That(row.GetInt64(0)).IsEqualTo(0L);

            // A second transaction opens cleanly: the first was fully closed out.
            using var tx2 = await conn.BeginTransactionAsync();
            await tx2.CommitAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
