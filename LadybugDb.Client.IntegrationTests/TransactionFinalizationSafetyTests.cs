using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Regresses fix-round-2 finding 1: the round-1 fix for the database-disposed-while-a-
/// -transaction-is-open crash (see <see cref="TransactionDisposalOrderingTests"/>) only ran from
/// <see cref="LadybugConnection.DisposeAsync"/> and <see cref="LadybugDatabase.Dispose"/> -
/// managed wrapper methods. Neither <see cref="LadybugDatabase"/> nor <see cref="LadybugConnection"/>
/// has a finalizer; only the underlying <c>SafeHandle</c>s do. A caller that abandons a database,
/// connection, and open transaction without disposing anything at all bypasses every bit of that
/// round-1 bookkeeping - the ONLY thing that ever runs is the two <c>SafeHandle</c>s' own,
/// independent finalizers, in an order the CLR does not guarantee.
/// </summary>
public class TransactionFinalizationSafetyTests
{
    /// <summary>
    /// Proves the fix (<c>LbugConnectionHandle</c> holding a long-lived
    /// <c>SafeHandle.DangerousAddRef</c> lease on its owning database for as long as a
    /// transaction is open) deterministically, without depending on GC timing at all: it bypasses
    /// <see cref="LadybugDatabase.Dispose"/>'s round-1 forced-rollback bookkeeping entirely by
    /// disposing the raw <c>LbugDatabaseHandle</c> directly - exactly what happens when that
    /// handle's own finalizer runs instead of an explicit <c>Dispose()</c> - and checks
    /// <c>IsClosed</c> directly rather than inferring it indirectly.
    /// </summary>
    [Test]
    public async Task DatabaseHandle_StaysOpenWhileConnectionHoldsAnOpenTransaction()
    {
        var path = TestDatabase.NewPath();
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            var tx = await conn.BeginTransactionAsync();
            await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

            // Bypasses LadybugDatabase.Dispose()'s own round-1 forced-rollback bookkeeping
            // entirely, going straight to the underlying SafeHandle - simulating what happens
            // when a caller abandons everything and only the GC finalizer path ever runs.
            db.Handle.Dispose();

            // Must NOT actually be closed yet: the connection is still holding a long-lived
            // reference on the database (see LbugConnectionHandle's remarks) for as long as its
            // transaction is open. If this were false, the database's native resources would
            // already be gone with a transaction still open on a connection that has not been
            // destroyed yet - the exact precondition for the round-1 crash.
            await Assert.That(db.Handle.IsClosed).IsFalse();

            // Closing the connection's own handle directly (again bypassing LadybugConnection's
            // own DisposeAsync) must succeed without throwing or crashing - the native
            // lbug_connection_destroy's internal auto-rollback finds the database still alive
            // (thanks to the held reference) and completes normally.
            conn.Handle.Dispose();

            // Releasing that reference as part of the connection handle's own release is what
            // finally lets the database's real release proceed.
            await Assert.That(db.Handle.IsClosed).IsTrue();

            _ = tx; // deliberately never committed, rolled back, or disposed - see above

            using var verifyDb = new LadybugDatabase(path);
            await using var verifyConn = await verifyDb.ConnectAsync();
            await using var result = await verifyConn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in result)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// End-to-end confirmation that the fix holds via the REAL finalizer path, not just via the
    /// direct-handle simulation above: opens a database, connection, and transaction inside a
    /// method that returns without disposing anything, forces the GC to actually run finalizers,
    /// then reopens the same database fresh to confirm both that the process survived and that
    /// the uncommitted row was correctly rolled back - not merely "didn't crash", but "closed out
    /// correctly". Run as a subprocess (see <see cref="TransactionDisposalOrderingTests"/> for
    /// why - <c>std::terminate()</c> cannot be caught in-process) 10 times to build confidence,
    /// mirroring the reviewer's own 15-run verification.
    /// </summary>
    [Test]
    [Repeat(10)]
    public async Task AbandonedWithoutDisposal_GcFinalization_DoesNotCrashAndRollsBack()
    {
        var harnessPath = Path.Combine(AppContext.BaseDirectory, "LadybugDb.Client.CrashRepro.dll");
        var dbPath = TestDatabase.NewPath();
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(harnessPath);
            psi.ArgumentList.Add("gc-abandon-open");
            psi.ArgumentList.Add(dbPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the crash-repro subprocess.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Crash-repro subprocess for scenario 'gc-abandon-open' did not exit in time.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            await Assert.That(process.ExitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("ROW_COUNT:0");
            await Assert.That(stdout).Contains("DONE");
            await Assert.That(stderr).DoesNotContain("terminate called");
        }
        finally { TestDatabase.Cleanup(dbPath); }
    }
}
