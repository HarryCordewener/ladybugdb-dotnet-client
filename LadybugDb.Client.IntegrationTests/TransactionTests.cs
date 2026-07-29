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
}
