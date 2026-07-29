using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// Regresses a Critical found in fix round 1 of Task 6: disposing a
/// <see cref="LadybugDatabase"/> while a <see cref="LadybugTransaction"/> is still open on one
/// of its connections killed the process. Native <c>lbug_connection_destroy</c> auto-rolls-back
/// any transaction still open on the connection it is destroying, and that auto-rollback needs
/// the database to still be alive; if the database was destroyed first, it called
/// <c>std::terminate()</c> (SIGABRT) instead of raising anything catchable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this runs the scenario in a subprocess instead of asserting in-process, unlike every
/// other test in this suite.</b> <c>std::terminate()</c> is not a .NET exception - it is not
/// thrown, it is not caught by any C# <c>catch</c> (including a bare <c>catch</c> around the
/// whole test body), and it does not run any of the CLR's own unhandled-exception handling. It
/// simply kills the process outright. An in-process <c>Assert.ThrowsAsync</c> around the buggy
/// sequence would not fail with a useful message if this regressed - it would just take the
/// entire test host down with it, which reads as "the runner crashed" rather than "this
/// assertion failed", and can as a result mask a genuine regression behind what looks like
/// unrelated CI flakiness. Running the scenario in
/// <c>LadybugDb.Client.CrashRepro</c> - a dedicated subprocess - lets this test observe the
/// outcome from the outside via <see cref="Process.ExitCode"/> without risking the test host
/// itself, and is the only way to reliably tell "crashed" apart from "threw" at all.
/// </para>
/// <para>
/// Reproduced against the pre-fix build, 5/5 runs: the "open" scenario below exited 134
/// (SIGABRT) with <c>terminate called after throwing an instance of 'std::system_error' what():
/// Invalid argument</c> on stderr. Post-fix, all four orderings below exit 0. See
/// <c>task-6-report.md</c> (fix round 1 section) for the full transcripts.
/// </para>
/// </remarks>
public class TransactionDisposalOrderingTests
{
    /// <summary>
    /// Database disposed while a transaction is still open on one of its connections (no
    /// <c>CommitAsync</c>/<c>RollbackAsync</c> ever ran) - the exact ordering from the crash
    /// report. Must exit cleanly and leave the uncommitted row rolled back.
    /// </summary>
    [Test]
    public async Task DatabaseDisposedFirst_TransactionLeftOpen_DoesNotCrashAndRollsBack()
    {
        var (exitCode, stdout, stderr) = await RunScenario("db-first-open");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("ROW_COUNT:0");
        await Assert.That(stdout).Contains("DONE");
        await Assert.That(stderr).DoesNotContain("terminate called");
    }

    /// <summary>
    /// Same ordering, but the transaction was committed first - the already-correct case, kept
    /// as a sanity check that fixing the open-transaction case above did not disturb it.
    /// </summary>
    [Test]
    public async Task DatabaseDisposedFirst_TransactionAlreadyCommitted_DoesNotCrashAndKeepsCommit()
    {
        var (exitCode, stdout, stderr) = await RunScenario("db-first-committed");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("ROW_COUNT:1");
        await Assert.That(stdout).Contains("DONE");
        await Assert.That(stderr).DoesNotContain("terminate called");
    }

    /// <summary>
    /// Connection disposed directly while a transaction is open on it - the
    /// <see cref="LadybugTransaction"/> object itself is never touched again. The database
    /// (still alive at this point) is disposed afterward.
    /// </summary>
    [Test]
    public async Task ConnectionDisposedFirst_TransactionLeftOpen_DoesNotCrashAndRollsBack()
    {
        var (exitCode, stdout, stderr) = await RunScenario("conn-first-open");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("ROW_COUNT:0");
        await Assert.That(stdout).Contains("DONE");
        await Assert.That(stderr).DoesNotContain("terminate called");
    }

    /// <summary>Same ordering, transaction committed first.</summary>
    [Test]
    public async Task ConnectionDisposedFirst_TransactionAlreadyCommitted_DoesNotCrashAndKeepsCommit()
    {
        var (exitCode, stdout, stderr) = await RunScenario("conn-first-committed");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("ROW_COUNT:1");
        await Assert.That(stdout).Contains("DONE");
        await Assert.That(stderr).DoesNotContain("terminate called");
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
