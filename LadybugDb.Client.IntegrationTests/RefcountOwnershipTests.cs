using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Regresses the refcount-ownership fix: every native child handle (a connection, a query
/// result, a prepared statement) now holds a long-lived reference on its parent(s) for its own
/// ENTIRE lifetime - not merely for the duration of the call that created it - via
/// <see cref="Interop.LbugStructHandle.AcquireParentHolds"/>. Before this fix, only
/// <see cref="Interop.LbugConnectionHandle"/> held such a reference, and only while a transaction
/// was open on it; every other child handle's own eventual native destroy ran with no reference
/// on its ancestor database at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Finding 1: a DML result's own destroy is a use-after-free once its database is disposed
/// first - no concurrency required.</b> Calibrated directly against the pre-fix build, 4/4 runs:
/// keeping a DML statement's (<c>CREATE</c>) <see cref="LadybugQueryResult"/> alive across an
/// ordinary, single-threaded <see cref="LadybugDatabase.Dispose"/> and then disposing the result
/// afterward crashed the process with SIGSEGV (exit 139) - the
/// <c>FactorizedTable::~FactorizedTable</c> / <c>MaterializedQueryResult::~MaterializedQueryResult</c>
/// destructor chain reaching into memory the now-destroyed database owned. The identical shape
/// with a plain read (<c>MATCH</c>) result in the same position crashed 0/4 times, both before and
/// after the fix - kept below as a control (<see cref="ReadResultOutlivesDatabaseDispose_DoesNotCrashAndKeepsData"/>)
/// so a regression there would be caught exactly as loudly as the DML case regressing would be.
/// Post-fix: both scenarios exit 0, 4/4 (this class runs each once per test invocation; see
/// <c>.superpowers/sdd/refcount-ownership-report.md</c> for the full 4-run calibration transcript
/// run directly against the built harness).
/// </para>
/// <para>
/// <b>Finding 2 (part b): concurrent <c>Bind</c> calls on the SAME <see cref="LadybugPreparedStatement"/>
/// corrupt the native heap.</b> <c>lbug_prepared_statement._bound_values</c> is mutable engine-side
/// state the engine does not lock - the header's "each connection is thread-safe" guarantee does
/// not reach it. Calibrated pre-fix: two threads calling <c>Bind</c> on one statement
/// concurrently, 20,000 calls each, crashed 3/3 runs (exit 134/139, <c>free(): invalid pointer</c>
/// on stderr on at least one run - native heap corruption, not a clean segfault). Fixed by
/// serializing every <c>Bind*</c>/<c>BindNull</c> call on an instance against every other one (see
/// <see cref="LadybugPreparedStatement"/>'s remarks); post-fix, 0/3.
/// </para>
/// <para>
/// <b>Why a subprocess, not an in-process assertion</b> - same reasoning as
/// <see cref="TransactionDisposalOrderingTests"/> and <see cref="TransactionBeginDisposeRaceTests"/>:
/// SIGSEGV/SIGABRT and native heap corruption are not .NET exceptions, are not caught by any C#
/// <c>catch</c>, and simply kill the process (or corrupt it silently) - <see cref="Process.ExitCode"/>
/// from a dedicated subprocess (<c>LadybugDb.Client.CrashRepro</c>) is the only reliable way to
/// observe the outcome from outside.
/// </para>
/// </remarks>
public class RefcountOwnershipTests
{
    [Test]
    public async Task DmlResultOutlivesDatabaseDispose_DoesNotCrashAndKeepsData()
    {
        var (exitCode, stdout, stderr) = await RunScenario("dml-result-outlives-dispose");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("ROW_COUNT:1");
        await Assert.That(stdout).Contains("DONE");
        await Assert.That(stderr).IsEmpty();
    }

    /// <summary>Control for <see cref="DmlResultOutlivesDatabaseDispose_DoesNotCrashAndKeepsData"/> - see this class's remarks.</summary>
    [Test]
    public async Task ReadResultOutlivesDatabaseDispose_DoesNotCrashAndKeepsData()
    {
        var (exitCode, stdout, stderr) = await RunScenario("read-result-outlives-dispose");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("ROW_COUNT:1");
        await Assert.That(stdout).Contains("DONE");
        await Assert.That(stderr).IsEmpty();
    }

    [Test]
    public async Task ConcurrentBind_OnSameStatement_DoesNotCrash()
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
            psi.ArgumentList.Add("concurrent-bind");
            psi.ArgumentList.Add(dbPath);
            psi.ArgumentList.Add("20000");

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
                throw new TimeoutException("Crash-repro subprocess for scenario 'concurrent-bind' did not exit in time.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            await Assert.That(process.ExitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains("DONE");
            await Assert.That(stderr).IsEmpty();
        }
        finally { TestDatabase.Cleanup(dbPath); }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunScenario(string scenario)
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
            psi.ArgumentList.Add(scenario);
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
                throw new TimeoutException($"Crash-repro subprocess for scenario '{scenario}' did not exit in time.");
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            TestDatabase.Cleanup(dbPath);
        }
    }
}
