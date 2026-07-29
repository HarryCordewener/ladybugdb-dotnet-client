using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Regresses the fix-round-4 finding: two concurrent <see cref="LadybugConnection.BeginTransactionAsync"/>
/// calls racing each other on the SAME connection could leak the entire underlying
/// <see cref="LadybugDatabase"/> - a distinct bug from <see cref="TransactionBeginDisposeRaceTests"/>
/// (which races <c>BeginTransactionAsync</c> against a concurrent <c>db.Dispose()</c>, not against
/// another <c>BeginTransactionAsync</c> call).
/// </summary>
/// <remarks>
/// <para>
/// <b>Root cause.</b> <see cref="LadybugConnection"/>'s nested-begin guard
/// (<c>_activeTransaction is not null</c>) and <c>Interop.LbugConnectionHandle</c>'s long-lived
/// database hold (<c>TryAcquireDatabaseHoldForTransaction</c>/<c>ReleaseDatabaseHoldForTransaction</c>,
/// from the <see cref="TransactionBeginDisposeRaceTests"/> fix) were both plain, unsynchronized
/// fields before this fix. Two threads calling <c>BeginTransactionAsync</c> on the same connection
/// at once could both pass the null check before either set it, both take the hold, and then the
/// LOSER's failure-path release (the hold was a plain <c>bool</c>) could cancel the WINNER's
/// still-active hold - so the winner's own eventual release became a no-op,
/// <c>LbugDatabaseHandle.ReleaseHandle</c> (and therefore <c>lbug_database_destroy</c>, which
/// unmaps the database's ~8 TiB virtual-address reservation) never ran, and the documented
/// client-side <see cref="InvalidOperationException"/> guard essentially never fired under the
/// race (measured pre-fix: 0-1 rejections out of 31 racing attempts, so the nested
/// <c>BEGIN TRANSACTION</c> reached the engine almost every time despite the guard existing).
/// </para>
/// <para>
/// <b>Measured pre-fix</b> (three isolated modes, to attribute the leak specifically to the
/// missing synchronization rather than to racing at all): a serial nested-begin control and a
/// race-plus-external-lock control both survived 120/120 iterations with a flat 8 TiB VmSize and
/// an <see cref="InvalidOperationException"/> 120/120 times; the unsynchronized race grew VmSize
/// from a flat 8 TiB to 40, 80, then 112 TiB by iteration 30, and died at iteration 33-37 (3/3
/// runs) with a failed native <c>mmap</c> for the next 8 TiB reservation.
/// </para>
/// <para>
/// <b>The fix</b> - see <c>LadybugConnection._transactionGate</c>'s remarks - serializes
/// <c>BeginTransactionAsync</c> against itself (and against transaction completion/disposal) with
/// a <see cref="System.Threading.SemaphoreSlim"/>, and separately makes
/// <c>Interop.LbugConnectionHandle</c>'s hold a real <see cref="Interlocked"/> reference count
/// rather than a boolean, so a loser's release can never cancel a winner's still-active hold even
/// if some future caller reaches that type without going through the connection's own gate.
/// </para>
/// <para>
/// <b>Why a subprocess, not an in-process assertion</b> - same reasoning as
/// <see cref="TransactionBeginDisposeRaceTests"/>: the pre-fix failure mode is a process death
/// (failed <c>mmap</c> eventually aborts the process, not a catchable .NET exception), and even
/// short of that, one leaked ~8 TiB-per-database reservation per iteration is not something an
/// in-process test should be measuring against <see cref="Environment.WorkingSet"/> (which is
/// resident memory, not the virtual-address reservation this bug leaks) or safely alongside
/// <c>LeakTests</c>' own working-set-based bound. <see cref="Process.ExitCode"/> plus parsed
/// <c>VMSIZE:</c> lines (from <c>/proc/self/status</c>, read by the harness itself) from a
/// dedicated subprocess (<c>LadybugDb.Client.CrashRepro</c>, scenario <c>begin-vs-begin</c>) is
/// the reliable way to observe both the crash and the leak trend.
/// </para>
/// </remarks>
public class TransactionBeginBeginRaceTests
{
    /// <summary>
    /// Iterations per subprocess run. High enough that the pre-fix leak (measured above) would
    /// have driven VmSize growth well past this test's bound - and, left running further, into
    /// the process-killing failed-<c>mmap</c> range - while staying comfortably under a budget
    /// this test can run every time.
    /// </summary>
    private const int AttemptsPerRun = 30;

    /// <summary>
    /// Generous bound on VmSize growth across the whole run, in bytes: three whole leaked
    /// ~8 TiB database reservations. Each iteration fully disposes its database before the next
    /// opens, so a correctly-fixed run should show growth close to zero (a few hundred MB of
    /// runtime/JIT noise at most) - the leak this regresses grows by whole 8 TiB steps, so this
    /// bound has three full orders of margin against measurement noise while still catching the
    /// bug well before it would need 30 iterations to become a full <c>mmap</c> failure.
    /// </summary>
    private const long MaxVmSizeGrowthBytes = 3L * 8L * 1024 * 1024 * 1024 * 1024;

    /// <summary>
    /// <see cref="NotInParallelAttribute"/>: same reasoning as
    /// <see cref="TransactionConcurrentDisposalTests"/>'s own use of it - a `dotnet`-subprocess
    /// spawn plus 30 full database-open-through-close iterations in the child is enough
    /// process-wide memory churn that, run concurrently with <c>LeakTests</c>'s
    /// <see cref="Environment.WorkingSet"/>-based measurements, intermittently pushed those
    /// unrelated tests' measured growth past their bound. Observed directly (this test was the
    /// new contributor): a handful of failures in several consecutive full-suite runs, always
    /// <c>LeakTests</c>, never this test itself. Isolating this test removes it as a contributor
    /// without touching the leak tests' bound, which project policy keeps fixed.
    /// </summary>
    [Test]
    [Repeat(2)]
    [NotInParallel]
    public async Task BeginTransactionAsync_RacingAnotherBeginTransactionAsync_DoesNotLeakOrCrash()
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
            psi.ArgumentList.Add("begin-vs-begin");
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
                throw new TimeoutException("Crash-repro subprocess for scenario 'begin-vs-begin' did not exit in time.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            await Assert.That(process.ExitCode).IsEqualTo(0);
            await Assert.That(stdout).Contains($"ITERATIONS:{AttemptsPerRun}");
            await Assert.That(stdout).Contains("DONE");
            await Assert.That(stderr).DoesNotContain("terminate called");

            // Parse every "VMSIZE:<iteration>:<bytes>" line the harness printed and assert the
            // last sample has not grown past the first by more than the bound above - the direct
            // measurement this regression is about, not just "the process happened to survive".
            var vmSizes = stdout
                .Split('\n')
                .Where(line => line.StartsWith("VMSIZE:", StringComparison.Ordinal))
                .Select(line => long.Parse(line.Split(':')[2]))
                .ToList();

            await Assert.That(vmSizes.Count).IsGreaterThanOrEqualTo(2);
            var growth = vmSizes[^1] - vmSizes[0];
            await Assert.That(growth).IsLessThan(MaxVmSizeGrowthBytes);
        }
        finally
        {
            for (var i = 0; i < AttemptsPerRun; i++)
                TestDatabase.Cleanup($"{basePath}-{i}");
        }
    }
}
