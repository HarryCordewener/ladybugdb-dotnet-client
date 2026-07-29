using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Regresses fix-round-3 finding 1: <see cref="LadybugConnection.BeginTransactionAsync"/> racing
/// a concurrent <see cref="LadybugDatabase.Dispose"/> on the owning database segfaulted the
/// process (SIGSEGV) - a distinct crash from the earlier <see cref="TransactionDisposalOrderingTests"/>/
/// <see cref="TransactionFinalizationSafetyTests"/> ones (which are about disposal ORDERING and
/// GC finalization), reached instead through pure live concurrency with no GC and no abandonment
/// involved at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Root cause.</b> The engine considers a transaction open the instant its own
/// <c>BEGIN TRANSACTION</c> call returns success - before ANY C# bookkeeping runs, including the
/// short per-call database lease <c>BEGIN TRANSACTION</c>'s own query takes (released the moment
/// that native call returns) and the <c>await using</c> that then disposes its query result
/// inside <c>LadybugTransaction.BeginAsync</c>. A concurrent <c>db.Dispose()</c> could complete
/// FULLY inside that window - nothing was holding the database open yet - freeing its native
/// memory while the engine still had a live, completely untracked open transaction on the
/// connection. See <c>Interop.LbugConnectionHandle</c>'s remarks for the fix: the long-lived
/// database hold is now acquired BEFORE <c>BEGIN TRANSACTION</c> is issued at all, not after it
/// succeeds, closing the window rather than narrowing it.
/// </para>
/// <para>
/// <b>Why <c>QueryAsync</c>/<c>PrepareAsync</c>/<c>ExecuteAsync</c> do not need the same fix.</b>
/// Every one of them leases the database only for the duration of its own native call
/// (<c>LbugQueryResultHandle.Execute</c>/<c>ExecutePrepared</c>,
/// <c>LbugPreparedStatementHandle.Prepare</c> all wrap their native call in
/// <c>using (database.Acquire())</c>), and whatever that call produces - a query result, a
/// prepared statement - is captured by its OWN dedicated <c>SafeHandle</c> via <c>Adopt</c>
/// before the lease is released, so nothing engine-side is ever left unprotected once the lease
/// ends. <c>BEGIN TRANSACTION</c> is different in kind, not degree: beyond producing a (transient,
/// immediately-disposed) query result like any other statement, it also durably opens a
/// transaction AT THE ENGINE LEVEL, scoped to the connection itself rather than to any object this
/// library hands back - there is no handle for "the open transaction" the way there is for a
/// query result or a prepared statement, so nothing protects it once the short per-call lease
/// ends except bookkeeping this library adds deliberately. It is the only operation with that
/// shape today; any future one that leaves similar durable, handle-less engine state open after
/// its own native call returns needs the same "take the long-lived hold first" treatment.
/// Confirmed empirically, not just by code inspection: a harness racing <c>QueryAsync</c>,
/// <c>PrepareAsync</c>, and <c>ExecuteAsync</c> against a concurrent <c>db.Dispose()</c> the same
/// way as this test - calibrated first against the known <c>BeginTransactionAsync</c> crash to
/// confirm it could actually observe the bug - found 0 crashes in 180 attempts for each of the
/// three, against the same crash showing up on effectively every attempt for
/// <c>BeginTransactionAsync</c>.
/// </para>
/// <para>
/// <b>Why a subprocess, not an in-process assertion</b> - same reasoning as
/// <see cref="TransactionDisposalOrderingTests"/>: a SIGSEGV, like the <c>std::terminate()</c>
/// case those tests guard, is not a .NET exception and cannot be caught from managed code; it
/// simply kills the process. <see cref="Process.ExitCode"/> from a dedicated subprocess
/// (<c>LadybugDb.Client.CrashRepro</c>, scenario <c>begin-vs-dispose</c>) is the only reliable way
/// to observe the outcome.
/// </para>
/// <para>
/// <b>Why the harness loops internally rather than this test re-invoking the process per
/// attempt.</b> The race window is narrow enough that a single <see cref="Barrier"/>-synchronized
/// attempt is not reliably observed on every machine/scheduler; looping many attempts (fresh
/// database each time) inside one process finds it far more reliably than paying full process
/// -startup cost per attempt would allow within a reasonable time budget. Calibrated directly on
/// this machine: at HEAD (pre-fix), 30 attempts in one process crashed 6/6 consecutive runs; the
/// same 30 attempts passed cleanly 6/6 consecutive runs after the fix, in roughly 650ms each - see
/// <c>task-6-report.md</c>'s fix-round-3 section for the full transcripts, including the higher
/// -iteration (500 attempts) confirmation run this number was chosen from.
/// </para>
/// </remarks>
public class TransactionBeginDisposeRaceTests
{
    /// <summary>Attempts per subprocess run - see this class's remarks for the calibration behind this number.</summary>
    private const int AttemptsPerRun = 30;

    [Test]
    [Repeat(2)]
    public async Task BeginTransactionAsync_RacingDatabaseDispose_DoesNotCrash()
    {
        var harnessPath = Path.Combine(AppContext.BaseDirectory, "LadybugDb.Client.CrashRepro.dll");
        var basePath = TestDatabase.NewPath();
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(harnessPath);
            psi.ArgumentList.Add("begin-vs-dispose");
            psi.ArgumentList.Add(basePath);
            psi.ArgumentList.Add(AttemptsPerRun.ToString());

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the crash-repro subprocess.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Crash-repro subprocess for scenario 'begin-vs-dispose' did not exit in time.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            await Assert.That(process.ExitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains($"ITERATIONS:{AttemptsPerRun}");
            await Assert.That(stdout).Contains("DONE");
            await Assert.That(stderr).DoesNotContain("terminate called");
        }
        finally
        {
            for (var i = 0; i < AttemptsPerRun; i++)
                TestDatabase.Cleanup($"{basePath}-{i}");
        }
    }
}
