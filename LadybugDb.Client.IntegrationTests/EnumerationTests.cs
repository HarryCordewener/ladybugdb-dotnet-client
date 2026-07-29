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

    /// <summary>
    /// Walks a chain deeper than one link (3 statements, 2 <c>NextResultAsync</c> hops) using the
    /// only pattern the native API supports safely: always advancing from the most recently
    /// returned result (<c>current = await current.NextResultAsync();</c>), never re-calling it on
    /// an earlier one. No test previously exercised more than one hop, which is how the
    /// single-shared-cursor behavior documented on <see cref="LadybugQueryResult.NextResultAsync"/>
    /// went unnoticed - a two-statement chain never calls <c>NextResultAsync</c> more than once at
    /// all, so it can't distinguish "single cursor" from "one cursor per result".
    /// </summary>
    /// <remarks>
    /// Every link stays open (undisposed) until the whole chain has been walked and every link's
    /// rows read, then all are disposed root-last: the first link IS <c>_root</c> for every
    /// descendant (see <see cref="LadybugQueryResult"/>'s remarks), so disposing it early - before
    /// the rest of the chain has been used - is expected to invalidate the remainder, exactly like
    /// <see cref="NextResultAsync_OriginalDisposedFirst_ChildThrowsObjectDisposedException"/> pins
    /// deliberately. That is not what this test is checking.
    /// </remarks>
    [Test]
    public async Task NextResultAsync_WalksChainDeeperThanOneLink()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Z(id INT64, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:Z {id: 1})")) { }

            var script = "MATCH (n:Z) RETURN n.id; MATCH (n:Z) RETURN count(*); MATCH (n:Z) RETURN n.id + 100;";

            var chain = new List<LadybugQueryResult> { await conn.QueryAsync(script) };
            while (await chain[^1].NextResultAsync() is { } next)
                chain.Add(next);
            await Assert.That(chain.Count).IsEqualTo(3);

            var seen = new List<long>();
            foreach (var result in chain)
                await foreach (var row in result)
                    seen.Add(row.GetValue(0).AsInt64());
            await Assert.That(seen).IsEquivalentTo(new List<long> { 1, 1, 101 });

            // Root-last: see remarks.
            for (var i = chain.Count - 1; i >= 0; i--)
                await chain[i].DisposeAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Pins the guard <see cref="LadybugQueryResult.NextResultAsync"/> now raises instead of
    /// silently returning a stale duplicate: calling it again on a result that already returned a
    /// real "next" once must throw, not re-read the shared cursor's current (further-advanced)
    /// position.
    /// </summary>
    [Test]
    public async Task NextResultAsync_CalledTwiceOnSameResult_ThrowsInvalidOperationException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE ZZ(id INT64, PRIMARY KEY(id))")) { }

            await using var root = await conn.QueryAsync(
                "MATCH (n:ZZ) RETURN n.id; MATCH (n:ZZ) RETURN count(*); MATCH (n:ZZ) RETURN 1;");
            await using var first = await root.NextResultAsync();
            await Assert.That(first).IsNotNull();

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await root.NextResultAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Pins the guard <see cref="LadybugQueryResult.GetAsyncEnumerator"/> now raises instead of
    /// silently vending a second enumerator that shares the same already-partially-consumed native
    /// cursor as the first - exactly the failure mode BCL <c>System.Linq.AsyncEnumerable</c>
    /// operators like <c>CountAsync</c>-then-<c>ToListAsync</c> would trigger for free.
    /// </summary>
    [Test]
    public async Task GetAsyncEnumerator_CalledTwice_ThrowsInvalidOperationException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE ZZZ(id INT64, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:ZZZ {id: 1})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:ZZZ) RETURN n.id");
            await foreach (var _ in r) { }

            Assert.Throws<InvalidOperationException>(() => r.GetAsyncEnumerator());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <c>Current</c> must throw <see cref="InvalidOperationException"/>, never
    /// <see cref="NullReferenceException"/>, both before the first successful
    /// <c>MoveNextAsync</c> and after one has returned <see langword="false"/>.
    /// </summary>
    [Test]
    public async Task Enumerator_CurrentOutsideAValidPosition_ThrowsInvalidOperationException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE W(id INT64, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:W {id: 1})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:W) RETURN n.id");
            await using var e = r.GetAsyncEnumerator();

            Assert.Throws<InvalidOperationException>(() => _ = e.Current);

            await Assert.That(await e.MoveNextAsync()).IsTrue();
            _ = e.Current; // does not throw once positioned on a row

            await Assert.That(await e.MoveNextAsync()).IsFalse();
            Assert.Throws<InvalidOperationException>(() => _ = e.Current);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task LadybugRow_OutOfRangeAccessors_ThrowExpectedExceptions()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE V(id INT64, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:V {id: 1})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:V) RETURN n.id");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            await Assert.That(row.ColumnCount).IsEqualTo(1);
            Assert.Throws<IndexOutOfRangeException>(() => row.GetValue(99));
            Assert.Throws<IndexOutOfRangeException>(() => row.GetColumnName(99));
            Assert.Throws<ArgumentException>(() => _ = row["no-such-column"]);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// <c>RETURN n.a AS x, n.b AS x</c> is legal Cypher; <see cref="LadybugRow.this[string]"/>
    /// documents "first match wins" for that case rather than throwing or picking arbitrarily.
    /// </summary>
    [Test]
    public async Task LadybugRow_DuplicateColumnNames_IndexerResolvesToFirstMatch()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE U(a INT64, b INT64, PRIMARY KEY(a))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:U {a: 1, b: 2})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:U) RETURN n.a AS dup, n.b AS dup");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            await Assert.That(row.GetColumnName(0)).IsEqualTo("dup");
            await Assert.That(row.GetColumnName(1)).IsEqualTo("dup");
            await Assert.That(row["dup"].AsInt64()).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
