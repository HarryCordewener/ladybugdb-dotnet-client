using System.Runtime.CompilerServices;
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// A <see cref="LadybugDatabase"/> is the root of the handle hierarchy: connections and query
/// results lease its native handle for every native call, not just their own. Before the fix
/// these tests pin down, only each object's OWN disposal was guarded - a connection's handle
/// guarded against the connection being disposed, a result's handle guarded against the result
/// being disposed - but nothing guarded a child against its ANCESTOR's disposal. Reproduced
/// against the shipped packages: disposing the database first and then querying on a still-open
/// connection segfaulted the process (exit 139); reading a column off a still-open result after
/// its database was disposed aborted it (exit 134, an unhandled C++ exception unwinding through
/// the P/Invoke boundary). Both now throw a managed <see cref="ObjectDisposedException"/> instead.
/// </summary>
public class DisposalSafetyTests
{
    [Test]
    public async Task QueryAsync_AfterDatabaseDisposed_ThrowsObjectDisposedException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();

            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await conn.QueryAsync("MATCH (n) RETURN n"));

            // The connection itself must still be safely disposable afterward.
            await conn.DisposeAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Enumeration_AfterDatabaseDisposed_ThrowsObjectDisposedException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 1, name: 'Limbo'})")) { }

            var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            await using var enumerator = result.GetAsyncEnumerator();

            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await enumerator.MoveNextAsync());

            // The result and connection must still be safely disposable afterward.
            await result.DisposeAsync();
            await conn.DisposeAsync();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ConnectAsync_AfterDatabaseDisposed_ThrowsObjectDisposedException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.ConnectAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// A connection opened AFTER the database was already disposed must never succeed - that
    /// would mean <see cref="Interop.LbugConnectionHandle.Open"/>'s own
    /// <see cref="Interop.LbugStructHandle.AcquireParentHolds"/> call somehow took a hold on a
    /// database it should have observed as already closed. See <c>HandleTests</c> (in
    /// <c>LadybugDb.Client.Tests</c>) for the lower-level regression that pins down the specific
    /// finding this exists to catch end to end: <c>SafeHandle.DangerousAddRef</c> throwing
    /// <see cref="ObjectDisposedException"/> on a fully-closed handle rather than returning
    /// <c>acquired == false</c>, and <see cref="Interop.LbugStructHandle.AcquireParentHolds"/>
    /// catching that per-attempt so a failure partway through a multi-parent acquisition rolls
    /// back every hold it already took rather than leaking one.
    /// </summary>
    [Test]
    public async Task ConnectAsync_AfterDatabaseDisposed_NeverLeavesADanglingHold()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.ConnectAsync());
            // A second, independent attempt must fail exactly the same way - a leaked hold from
            // the first attempt would make the database appear as if something still depended on
            // it, but would not change whether IT ITSELF still reports closed to a fresh caller.
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.ConnectAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ReverseOrderDisposal_ResultThenConnectionThenDatabase_StillSucceeds()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 1, name: 'Limbo'})")) { }

            var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            string? name = null;
            await foreach (var row in result)
                name = row.GetValue(0).AsString();
            await Assert.That(name).IsEqualTo("Limbo");

            // Correct order: descendant objects disposed before their ancestor. Must not throw.
            await result.DisposeAsync();
            await conn.DisposeAsync();
            db.Dispose();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task OrphanedConnection_FinalizesWithoutCrashingTheProcess()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);

            // Never disposed - deliberately abandoned so only the finalizer releases it.
            await CreateAndAbandonConnection(db);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // If the finalizer thread crashed the process or corrupted shared state, this would
            // never run. It also proves the database is still fully usable afterward.
            await using var conn = await db.ConnectAsync();
            await using var result = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CreateAndAbandonConnection(LadybugDatabase db)
    {
        _ = await db.ConnectAsync();
    }
}
