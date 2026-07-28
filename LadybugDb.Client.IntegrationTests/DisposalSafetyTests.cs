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
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lbug-dispose-{Guid.NewGuid():N}");

    [Test]
    public async Task QueryAsync_AfterDatabaseDisposed_ThrowsObjectDisposedException()
    {
        var path = TempDbPath();
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
        finally { Cleanup(path); }
    }

    [Test]
    public async Task ReadStringAsync_AfterDatabaseDisposed_ThrowsObjectDisposedException()
    {
        var path = TempDbPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 1, name: 'Limbo'})")) { }

            var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");

            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await result.ReadStringAsync(0));

            // The result and connection must still be safely disposable afterward.
            await result.DisposeAsync();
            await conn.DisposeAsync();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task ConnectAsync_AfterDatabaseDisposed_ThrowsObjectDisposedException()
    {
        var path = TempDbPath();
        try
        {
            var db = new LadybugDatabase(path);
            db.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await db.ConnectAsync());
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task ReverseOrderDisposal_ResultThenConnectionThenDatabase_StillSucceeds()
    {
        var path = TempDbPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 1, name: 'Limbo'})")) { }

            var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            var name = await result.ReadStringAsync(0);
            await Assert.That(name).IsEqualTo("Limbo");

            // Correct order: descendant objects disposed before their ancestor. Must not throw.
            await result.DisposeAsync();
            await conn.DisposeAsync();
            db.Dispose();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task OrphanedConnection_FinalizesWithoutCrashingTheProcess()
    {
        var path = TempDbPath();
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
        finally { Cleanup(path); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CreateAndAbandonConnection(LadybugDatabase db)
    {
        _ = await db.ConnectAsync();
    }

    private static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
