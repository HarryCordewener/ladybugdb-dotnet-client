using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// <see cref="LadybugConnection.Select{T}"/> owns the <see cref="LadybugQueryResult"/> it reads: the
/// caller never receives it, so nothing else can dispose it. This class proves it is released on every
/// path out of an <c>await foreach</c> - completion, an early <c>break</c>, a throw from the caller's
/// loop body, cancellation, and a mapping failure before the first row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this measures a counter rather than process memory.</b> A leaked
/// <see cref="LadybugQueryResult"/> per query is the defect class this client has found four times, and
/// the obvious harness for it - <see cref="Environment.WorkingSet"/>, as in <c>LeakTests</c> - is the
/// wrong instrument here twice over: it is a whole-process metric already quarantined on hosted CI, and
/// the handle underneath a leaked result has a finalizer, so a <c>GC.Collect()</c> before the
/// measurement can release the very leak the test is looking for and report success.
/// <see cref="LadybugQueryResult.LiveCount"/> counts explicit disposal only and is therefore exact:
/// a leaked result never decrements it, at any collection pressure.
/// </para>
/// <para>
/// <b>Each test asserts the count went UP first.</b> A test that only checked "the count is back at its
/// baseline afterwards" would pass against an implementation whose result the counter never observed at
/// all - a false negative of exactly the kind already found on this branch, where a test overwrote its
/// own evidence and passed against deliberately broken code. Asserting <c>baseline + 1</c> while the
/// stream is still open is what makes the second assertion mean something.
/// </para>
/// <para>
/// <b>Calibrated.</b> Hoisting the <c>await using</c> out of <c>SelectCore</c>'s body (leaving the
/// result undisposed, the exact defect) turns all six tests below red, reporting one live result too
/// many; restoring it turns them green again.
/// </para>
/// <para>
/// <see cref="NotInParallelAttribute"/> at class level with no constraint key, for the same reason
/// <c>LeakTests</c> has it: <see cref="LadybugQueryResult.LiveCount"/> is process-wide with no way to
/// scope it to one test, so any concurrent test holding a result open would inflate the reading. These
/// tests therefore run alone. Everything about <see cref="LadybugConnection.Select{T}"/> that does not
/// read the counter lives in <c>SelectTests</c> and runs in parallel.
/// </para>
/// </remarks>
[NotInParallel]
public class SelectDisposalTests
{
    private record Person(long Dbref, string Name);

    /// <summary>Names a column the query does not return, so plan resolution fails before the first row.</summary>
    private record Mismatched(long Dbref, string Nmae);

    /// <summary>Thrown from a caller's loop body, distinct from every exception the client itself raises.</summary>
    private sealed class BoomException : Exception;

    private const string Cypher =
        "MATCH (o:Object) RETURN o.dbref AS Dbref, o.name AS Name ORDER BY o.dbref";

    private static async Task<(LadybugDatabase Db, LadybugConnection Connection)> OpenWithObjects(string path)
    {
        var db = new LadybugDatabase(path);
        var conn = await db.ConnectAsync();
        await using (var _ = await conn.QueryAsync(
            "CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
        for (var i = 1; i <= 5; i++)
            await using (var _ = await conn.QueryAsync($"CREATE (n:Object {{dbref: {i}, name: 'n{i}'}})")) { }
        return (db, conn);
    }

    /// <summary>
    /// <b>The early-<c>break</c> path</b>, and the reason this class exists: the caller abandons the
    /// stream after one row, and the result is released anyway - by the compiler-generated enumerator's
    /// <c>DisposeAsync</c>, which <c>await foreach</c> runs on the way out of the loop.
    /// </summary>
    [Test]
    public async Task EarlyBreak_ReleasesTheResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var baseline = LadybugQueryResult.LiveCount;
            var seen = 0;
            var whileStreaming = -1L;

            await foreach (var person in conn.Select<Person>(Cypher))
            {
                seen++;
                whileStreaming = LadybugQueryResult.LiveCount;
                await Assert.That(person.Dbref).IsEqualTo(1L);
                break;
            }

            // One row read, so the query really ran and there really were rows left unread.
            await Assert.That(seen).IsEqualTo(1);

            // The counter saw the result the iterator opened - without this, the assertion below would
            // also hold for an implementation the counter never observed at all.
            await Assert.That(whileStreaming).IsEqualTo(baseline + 1);

            // ...and the break released it.
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>Running the stream to completion releases the result too - the ordinary path.</summary>
    [Test]
    public async Task FullEnumeration_ReleasesTheResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var baseline = LadybugQueryResult.LiveCount;
            var seen = 0;
            var whileStreaming = -1L;

            await foreach (var _ in conn.Select<Person>(Cypher))
            {
                if (++seen == 1) whileStreaming = LadybugQueryResult.LiveCount;
            }

            await Assert.That(seen).IsEqualTo(5);
            await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// An exception thrown from inside the caller's loop body leaves the stream mid-flight, exactly like
    /// a <c>break</c> does - <c>await foreach</c>'s own <c>finally</c> still disposes the enumerator, so
    /// the result is still released, and the caller's exception is what propagates.
    /// </summary>
    [Test]
    public async Task ExceptionFromTheLoopBody_ReleasesTheResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var baseline = LadybugQueryResult.LiveCount;
            var seen = 0;
            var whileStreaming = -1L;

            await Assert.ThrowsAsync<BoomException>(async () =>
            {
                await foreach (var _ in conn.Select<Person>(Cypher))
                {
                    seen++;
                    whileStreaming = LadybugQueryResult.LiveCount;
                    throw new BoomException();
                }
            });

            await Assert.That(seen).IsEqualTo(1);
            await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// Cancellation mid-enumeration throws out of the iterator itself rather than out of the caller's
    /// body, which unwinds the iterator's own <c>await using</c> instead of relying on the caller's
    /// disposal - a different path to the same release, so it gets its own assertion.
    /// </summary>
    [Test]
    public async Task CancellationMidEnumeration_ReleasesTheResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var baseline = LadybugQueryResult.LiveCount;
            var seen = 0;
            var whileStreaming = -1L;

            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in conn.Select<Person>(Cypher, null, cts.Token))
                {
                    seen++;
                    whileStreaming = LadybugQueryResult.LiveCount;
                    await cts.CancelAsync();
                }
            });

            await Assert.That(seen).IsEqualTo(1);
            await Assert.That(whileStreaming).IsEqualTo(baseline + 1);
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A projection that cannot be resolved throws <em>after</em> the result exists but before any row
    /// is yielded - the one failure path where the caller's loop body never runs at all, and so the one
    /// most easily left leaking.
    /// </summary>
    [Test]
    public async Task MappingFailureBeforeTheFirstRow_ReleasesTheResult()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var baseline = LadybugQueryResult.LiveCount;

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in conn.Select<Mismatched>(Cypher)) { }
            });

            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The accumulating form of the same defect: 500 abandoned streams. A per-query leak that a single
    /// early <c>break</c> makes visible as one live result makes itself visible here as 500 - the shape
    /// it would actually take in a long-running process, and the assertion that no path is releasing
    /// only some of the time.
    /// </summary>
    [Test]
    public async Task ManyAbandonedStreams_DoNotAccumulateResults()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var (db, conn) = await OpenWithObjects(path);
            using var _db = db;
            await using var _conn = conn;

            var baseline = LadybugQueryResult.LiveCount;
            var peak = -1L;

            for (var i = 0; i < 500; i++)
            {
                await foreach (var _ in conn.Select<Person>(Cypher))
                {
                    peak = Math.Max(peak, LadybugQueryResult.LiveCount);
                    break;
                }
            }

            // Never more than one live at a time, and none left over.
            await Assert.That(peak).IsEqualTo(baseline + 1);
            await Assert.That(LadybugQueryResult.LiveCount).IsEqualTo(baseline);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
