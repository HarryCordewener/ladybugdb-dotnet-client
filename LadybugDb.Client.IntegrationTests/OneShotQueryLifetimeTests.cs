using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

/// <summary>
/// <see cref="LadybugConnection.QueryAsync(string, object, CancellationToken)"/> prepares a
/// statement, binds, executes, and disposes that statement - all before the caller ever sees the
/// <see cref="LadybugQueryResult"/> it returns. <b>The result therefore outlives the statement that
/// produced it by construction, on every single call.</b> This class exists to verify that ordering
/// rather than assume it.
/// </summary>
/// <remarks>
/// <para>
/// It is safe because of what a result leases and what it does not: a
/// <see cref="LadybugQueryResult"/> from a prepared execution holds long-lived references on its
/// parent database and its own handle chain (<c>LbugQueryResultHandle.ExecutePrepared</c>) and
/// <em>never</em> on the prepared statement. <c>PreparedStatementTests.DisposingStatementBeforeConsumingResult_ResultRemainsUsable</c>
/// pins that for the explicit-statement path; the tests here pin it for the one-shot path, whose
/// disposal happens inside the client where a caller cannot see or reorder it.
/// </para>
/// <para>
/// <b>Why the ordering warranted a probe and not just a functional test.</b> The nearest neighbour
/// to this shape is a documented process-killer in this client: a DML result kept alive across its
/// <em>database</em>'s disposal crashed with SIGSEGV 4/4 runs pre-refcount-fix (see
/// <see cref="RefcountOwnershipTests"/>), in the destructor chain rather than as any catchable
/// exception. A one-shot DML query produces exactly that kind of result and immediately releases one
/// of the handles in its ancestry, so
/// <see cref="OneShotDmlResultOutlivingStatementAndDatabase_DoesNotCrashTheProcess"/> runs the
/// combined ordering in a subprocess and inspects <see cref="Process.ExitCode"/> - the only way to
/// tell "crashed" apart from "threw", since a fatal signal takes the test host down and reads as
/// runner flakiness rather than as a failed assertion.
/// </para>
/// <para>
/// <b>Measured result: it does not crash.</b> The subprocess exits 0 with the row committed, and
/// stderr carries no <c>terminate called</c>. The scenario body is
/// <see cref="Scenario_OneShotDmlResultOutlivesStatementAndDatabase"/>, which is skipped during an
/// ordinary suite run (see its remarks) precisely so a regression cannot take the whole suite down
/// with it - the probe below is the thing that runs it.
/// </para>
/// <para>
/// <b>What that does not extend to, found by the probe's first draft failing.</b> That draft
/// <em>read</em> the result after disposing the database and expected it to work; it threw
/// <see cref="ObjectDisposedException"/> instead - correctly, per the lifetime rule's
/// close-for-new-work half. Destroying a result after its database is safe; reading one is refused.
/// The distinction is now pinned by
/// <see cref="OneShotResultReadAfterItsDatabaseIsDisposed_ThrowsObjectDisposedException"/> so it
/// stays written down rather than being rediscovered.
/// </para>
/// </remarks>
public class OneShotQueryLifetimeTests
{
    /// <summary>
    /// Set by <see cref="OneShotDmlResultOutlivingStatementAndDatabase_DoesNotCrashTheProcess"/> on
    /// the subprocess it spawns, and holds the path the scenario writes its outcome to. Unset in an
    /// ordinary suite run, which is what makes the scenario skip itself there.
    /// </summary>
    private const string ScenarioMarkerVariable = "LADYBUG_ONESHOT_LIFETIME_MARKER";

    // ------------------------------------------------------------------------------- in-process

    /// <summary>
    /// A read result is fully enumerable after the one-shot call returns - by which point the
    /// internal statement has already been disposed.
    /// </summary>
    [Test]
    public async Task OneShotQueryAsync_ReadResult_IsEnumerableAfterTheInternalStatementIsGone()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:S {id: 1, name: 'a'})")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:S {id: 2, name: 'b'})")) { }

            var result = await conn.QueryAsync(
                "MATCH (n:S) WHERE n.id >= $min RETURN n.id, n.name ORDER BY n.id", new { min = 1L });

            // Nothing has been read yet, and the statement that produced this is already destroyed.
            await using (result)
            {
                var seen = new List<string>();
                await foreach (var row in result)
                    seen.Add(row.GetValue(1).AsString());

                await Assert.That(seen).IsEquivalentTo(new[] { "a", "b" });
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The riskier shape: a <em>DML</em> result - the kind whose destructor chain reaches into
    /// database-owned memory - consumed after the one-shot call returned and disposed the statement.
    /// </summary>
    [Test]
    public async Task OneShotQueryAsync_DmlResult_IsEnumerableAfterTheInternalStatementIsGone()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, name STRING, PRIMARY KEY(id))")) { }

            var result = await conn.QueryAsync(
                "CREATE (n:S {id: $id, name: $name}) RETURN n.id, n.name",
                new { id = 5L, name = "dml" });

            await using (result)
            {
                var rows = 0;
                await foreach (var row in result)
                {
                    rows++;
                    await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(5L);
                    await Assert.That(row.GetValue(1).AsString()).IsEqualTo("dml");
                }

                await Assert.That(rows).IsEqualTo(1);
            }

            // And the write actually landed.
            await using var check = await conn.QueryAsync("MATCH (n:S) RETURN count(n)");
            await foreach (var row in check)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The failure paths dispose the internal statement too - both a binding rejection (which happens
    /// before the statement is ever executed) and an execution failure. Asserted functionally rather
    /// than by measuring process memory: a leaked prepared statement holds a reference on its parent
    /// database, so if these paths leaked, the loop below would exhaust something and the final
    /// successful query would fail. Deliberately not a <see cref="Environment.WorkingSet"/>
    /// assertion - see <see cref="LeakTests"/> for why this suite has exactly one such measurer.
    /// </summary>
    [Test]
    public async Task OneShotQueryAsync_FailurePaths_DoNotLeakTheInternalStatement()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, name STRING, PRIMARY KEY(id))")) { }

            for (var i = 0; i < 500; i++)
            {
                // Rejected during binding: the statement was prepared, never executed.
                await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await conn.QueryAsync(
                        "CREATE (n:S {id: $id, name: 'x'})", new { id = new object() }));

                // Rejected by the engine at execution: 'x' is not an INT64.
                await Assert.ThrowsAsync<LadybugException>(async () =>
                    await conn.QueryAsync(
                        "CREATE (n:S {id: $id, name: 'x'})", new { id = "not-an-integer" }));
            }

            await using var r = await conn.QueryAsync(
                "CREATE (n:S {id: $id, name: 'after'}) RETURN n.id", new { id = 1L });
            var rows = 0;
            await foreach (var row in r)
            {
                rows++;
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(1L);
            }

            await Assert.That(rows).IsEqualTo(1);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    /// <summary>
    /// The boundary of what a one-shot result outliving things buys you, established while building
    /// the probe below (whose first draft asserted the opposite and failed): a result outlives its
    /// <em>statement</em>, but <b>reading</b> one after its <em>database</em> has been disposed throws
    /// <see cref="ObjectDisposedException"/>, per the lifetime rule's close-for-new-work half. The
    /// result's own destruction is still safe in that ordering - which is what the subprocess probe
    /// covers - but a read is new work against a closed database, and is refused rather than served.
    /// </summary>
    [Test]
    public async Task OneShotResultReadAfterItsDatabaseIsDisposed_ThrowsObjectDisposedException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            LadybugQueryResult result;
            var db = new LadybugDatabase(path);
            await using (var conn = await db.ConnectAsync())
            {
                await using (var _ = await conn.QueryAsync(
                    "CREATE NODE TABLE S(id INT64, name STRING, PRIMARY KEY(id))")) { }

                result = await conn.QueryAsync(
                    "CREATE (n:S {id: $id, name: 'x'}) RETURN n.id", new { id = 1L });
            }

            db.Dispose();

            await using (result)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                {
                    await foreach (var _ in result) { }
                });
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    // ------------------------------------------------------------------------------- subprocess

    /// <summary>
    /// Runs <see cref="Scenario_OneShotDmlResultOutlivesStatementAndDatabase"/> in a subprocess and
    /// asserts the process survived it. See this class's remarks for why a subprocess is the only
    /// harness that can distinguish a fatal signal from a failed assertion.
    /// </summary>
    [Test]
    public async Task OneShotDmlResultOutlivingStatementAndDatabase_DoesNotCrashTheProcess()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"lbug-oneshot-{Guid.NewGuid():N}.marker");
        var results = Path.Combine(Path.GetTempPath(), $"lbug-oneshot-results-{Guid.NewGuid():N}");

        // The test assembly is its own harness: it is an OutputType=Exe with a TUnit entry point, so
        // a single scenario can be selected out of it by treenode filter. CrashRepro would be the
        // usual home for a scenario like this, but it is owned elsewhere; re-entering this assembly
        // costs nothing extra and keeps the scenario next to the assertions it belongs to.
        var harness = Path.Combine(AppContext.BaseDirectory, "LadybugDb.Client.IntegrationTests.dll");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add("--treenode-filter");
        psi.ArgumentList.Add(
            "/*/*/OneShotQueryLifetimeTests/Scenario_OneShotDmlResultOutlivesStatementAndDatabase");
        psi.ArgumentList.Add("--results-directory");
        psi.ArgumentList.Add(results);
        psi.Environment[ScenarioMarkerVariable] = marker;

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the lifetime-probe subprocess.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The lifetime-probe subprocess did not exit in time.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Exit 0 means the child ran the scenario and it passed. A fatal signal shows up here as
            // 134 (SIGABRT) or 139 (SIGSEGV) - which is the whole reason for the subprocess.
            await Assert.That(process.ExitCode).IsEqualTo(0);

            // ...and that the scenario really ran, rather than the filter silently matching nothing
            // and the child exiting 0 having done no work. `dotnet test --filter` failing this way
            // (exit 5, "Zero tests ran") is a known trap in this repo; the marker file makes the
            // difference observable instead of implied.
            await Assert.That(File.Exists(marker)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(marker)).IsEqualTo("ROW_COUNT:1");

            await Assert.That(stdout).Contains("total: 1");
            await Assert.That(stderr).DoesNotContain("terminate called");
            await Assert.That(stderr).DoesNotContain("Segmentation");
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }
            try { if (Directory.Exists(results)) Directory.Delete(results, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The scenario body driven by the probe above: an unconsumed one-shot DML result held across
    /// both the internal statement's disposal (which the one-shot overload does before returning)
    /// <em>and</em> its database's, and destroyed last. Reopens the database afterward and writes the
    /// committed row count to the marker file named by <see cref="ScenarioMarkerVariable"/>, so the
    /// probe can confirm the work actually happened.
    /// </summary>
    /// <remarks>
    /// Skipped unless that variable is set, i.e. skipped in every ordinary suite run. That is
    /// deliberate: if this ordering ever regresses it kills the process, and a process-killer inside
    /// the ordinary suite reads as CI flakiness rather than as this assertion failing - the failure
    /// mode the subprocess harness exists to avoid. Running it here as well would reintroduce exactly
    /// that.
    /// </remarks>
    [Test]
    public async Task Scenario_OneShotDmlResultOutlivesStatementAndDatabase()
    {
        var marker = Environment.GetEnvironmentVariable(ScenarioMarkerVariable);
        Skip.When(marker is null,
            "Scenario body, run only in the subprocess spawned by " +
            "OneShotDmlResultOutlivingStatementAndDatabase_DoesNotCrashTheProcess - see its remarks " +
            "for why it must not run in-process alongside the rest of the suite.");

        var path = TestDatabase.NewPath();
        try
        {
            LadybugQueryResult result;
            var db = new LadybugDatabase(path);
            await using (var conn = await db.ConnectAsync())
            {
                await using (var _ = await conn.QueryAsync(
                    "CREATE NODE TABLE S(id INT64, name STRING, PRIMARY KEY(id))")) { }

                // One-shot: the internal prepared statement is disposed before this returns. The
                // result is deliberately left UNCONSUMED - the hazard is the destructor chain, which
                // runs whether or not anything was read.
                result = await conn.QueryAsync(
                    "CREATE (n:S {id: $id, name: $name}) RETURN n.id",
                    new { id = 1L, name = "dml" });
            }

            // Drop the database with the DML result still alive, then destroy the result last: the
            // ordering that crashed 4/4 pre-refcount-fix, now with a one-shot statement's corpse in
            // the ancestry as well.
            db.Dispose();
            await result.DisposeAsync();

            // Reopening proves the write committed - i.e. the process did not merely survive by
            // having done nothing.
            long rows;
            using (var reopened = new LadybugDatabase(path))
            {
                await using var conn = await reopened.ConnectAsync();
                await using var r = await conn.QueryAsync("MATCH (n:S) RETURN count(n)");
                rows = 0;
                await foreach (var row in r)
                    rows = row.GetValue(0).AsInt64();
            }

            await File.WriteAllTextAsync(marker!, $"ROW_COUNT:{rows}");
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
