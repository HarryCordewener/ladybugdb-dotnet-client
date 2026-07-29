using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class EnumerationTests
{
    [Test]
    public async Task AwaitForeach_YieldsEveryRowInOrder()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE N(id INT64, PRIMARY KEY(id))")) { }
            for (var i = 0; i < 5; i++)
                await using (var _ = await conn.QueryAsync($"CREATE (n:N {{id: {i}}})")) { }

            var seen = new List<long>();
            await using var r = await conn.QueryAsync("MATCH (n:N) RETURN n.id ORDER BY n.id");
            await foreach (var row in r)
                seen.Add(row.GetValue(0).AsInt64());

            await Assert.That(seen).IsEquivalentTo(new List<long> { 0, 1, 2, 3, 4 });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ColumnsAreAddressableByName()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE M(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:M {id: 1, name: 'x'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:M) RETURN n.id AS ident, n.name AS label");
            await foreach (var row in r)
            {
                await Assert.That(row.GetColumnName(0)).IsEqualTo("ident");
                await Assert.That(row["label"].AsString()).IsEqualTo("x");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Cancellation_StopsEnumeration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Q(id INT64, PRIMARY KEY(id))")) { }
            for (var i = 0; i < 50; i++)
                await using (var _ = await conn.QueryAsync($"CREATE (n:Q {{id: {i}}})")) { }

            using var cts = new CancellationTokenSource();
            var count = 0;
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await using var r = await conn.QueryAsync("MATCH (n:Q) RETURN n.id");
                await foreach (var row in r.WithCancellation(cts.Token))
                {
                    if (++count == 5) cts.Cancel();
                }
            });
            await Assert.That(count).IsEqualTo(5);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The same parent-disposal crash class <c>DisposalSafetyTests</c> pins for <c>HasNext</c>/the
    /// old <c>ReadStringAsync</c>, now against the enumerator: <see cref="LadybugQueryResult.GetAsyncEnumerator"/>'s
    /// <c>MoveNextAsync</c> makes native calls per row exactly like those did, so it needs the same
    /// database lease. Disposing the database mid-enumeration must throw a managed
    /// <see cref="ObjectDisposedException"/> from the next <c>MoveNextAsync</c>, never crash the
    /// process.
    /// </summary>
    [Test]
    public async Task Enumeration_DatabaseDisposedMidEnumeration_ThrowsObjectDisposedException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE R(id INT64, PRIMARY KEY(id))")) { }
            for (var i = 0; i < 5; i++)
                await using (var _ = await conn.QueryAsync($"CREATE (n:R {{id: {i}}})")) { }

            var r = await conn.QueryAsync("MATCH (n:R) RETURN n.id ORDER BY n.id");
            await using var e = r.GetAsyncEnumerator();

            // Consume one row while the database is still open, to prove the enumerator works
            // normally first.
            await Assert.That(await e.MoveNextAsync()).IsTrue();

            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await e.MoveNextAsync());

            // The result, enumerator, and connection must all still be safely disposable afterward.
            await r.DisposeAsync();
            await conn.DisposeAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Pins the ownership finding behind <see cref="LadybugQueryResult.NextResultAsync"/> and
    /// <c>Interop.LbugQueryResultHandle.GetNextQueryResult</c>: a result returned from
    /// <c>NextResultAsync</c> is a child that dies with the ORIGINAL result the chain started
    /// from, not an independent handle. Disposing that original first and then using the child
    /// must throw a managed <see cref="ObjectDisposedException"/> - the <c>_root</c> lease is what
    /// turns what would otherwise be a use-after-free (reproduced as a SIGSEGV against a
    /// standalone probe process while developing this, exit code 139) into this instead.
    /// </summary>
    [Test]
    public async Task NextResultAsync_OriginalDisposedFirst_ChildThrowsObjectDisposedException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, PRIMARY KEY(id))")) { }

            var original = await conn.QueryAsync(
                "MATCH (n:S) RETURN n.id; MATCH (n:S) RETURN count(*);");
            var child = await original.NextResultAsync();
            await Assert.That(child).IsNotNull();

            await original.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = child!.HasNext);

            // The child must still be safely disposable afterward.
            await child!.DisposeAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The other disposal order: disposing the child first must be inert (per the empirical
    /// finding, its native <c>lbug_query_result_destroy</c> is a documented no-op for this exact
    /// case) and must never invalidate the original result it came from.
    /// </summary>
    [Test]
    public async Task NextResultAsync_ChildDisposedFirst_OriginalStillUsable()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

            await using var original = await conn.QueryAsync(
                "MATCH (n:T) RETURN n.id; MATCH (n:T) RETURN count(*);");
            var child = await original.NextResultAsync();
            await Assert.That(child).IsNotNull();

            await child!.DisposeAsync();

            // The original must remain fully usable: read its own row, and confirm its own
            // handle is unaffected by the (inert) child disposal.
            var seen = new List<long>();
            await foreach (var row in original)
                seen.Add(row.GetValue(0).AsInt64());
            await Assert.That(seen).IsEquivalentTo(new List<long> { 1 });
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
