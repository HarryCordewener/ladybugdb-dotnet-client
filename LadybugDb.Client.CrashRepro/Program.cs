using LadybugDb.Client;

// Subprocess harness for TransactionDisposalOrderingTests (LadybugDb.Client.IntegrationTests).
//
// The bug this reproduces is a native std::terminate() - an unhandled C++ exception thrown from
// inside lbug_connection_destroy's internal auto-rollback of a still-open transaction, when the
// database it would need to roll back against has already been destroyed. std::terminate cannot
// be caught from managed code: it aborts the whole process (SIGABRT) before a C# catch block, or
// even the CLR's own top-level unhandled-exception machinery, ever gets a chance to run. That
// means the *only* way to tell "crashed" apart from "threw a normal exception" from the outside
// is to run the scenario in its own process and look at how that process died - hence this
// separate executable instead of an in-process TUnit assertion, and hence the test that spawns
// it inspects Process.ExitCode rather than catching anything itself.
//
// Usage: LadybugDb.Client.CrashRepro <scenario> <db-path> [iterations]
// Scenarios: db-first-open, db-first-committed, conn-first-open, conn-first-committed (explicit
// dispose orderings); gc-abandon-open (abandons everything without disposing anything and
// forces the real GC finalizer path - see AbandonWithoutDisposal_TransactionLeftOpen); and
// begin-vs-dispose (BeginTransactionAsync racing a concurrent db.Dispose() - live concurrency,
// no GC, no abandonment; [iterations] controls how many fresh-database attempts to loop through
// in this one process, default 500 - see BeginTransactionRacingDatabaseDispose).
// Exit 0 and "DONE" on stdout: the scenario completed and the process is healthy.
// Exit 1 and "MANAGED_EXCEPTION:...": a normal (non-fatal) exception was thrown and caught here.
// Any other exit code (e.g. 134 = SIGABRT, 139 = SIGSEGV on Linux): the process was killed.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: LadybugDb.Client.CrashRepro <scenario> <db-path>");
    return 2;
}

var scenario = args[0];
var path = args[1];

try
{
    switch (scenario)
    {
        case "db-first-open":
            await DbDisposedFirst_TransactionLeftOpen(path);
            break;
        case "db-first-committed":
            await DbDisposedFirst_TransactionAlreadyCommitted(path);
            break;
        case "conn-first-open":
            await ConnectionDisposedFirst_TransactionLeftOpen(path);
            break;
        case "conn-first-committed":
            await ConnectionDisposedFirst_TransactionAlreadyCommitted(path);
            break;
        case "gc-abandon-open":
            await AbandonWithoutDisposal_TransactionLeftOpen(path);
            ForceFullGarbageCollection();
            break;
        case "begin-vs-dispose":
            // Uses path-0, path-1, ... (a fresh database per iteration) rather than a single
            // reopenable path at the end - see BeginTransactionRacingDatabaseDispose. Reports
            // its own completion and returns directly instead of falling into the shared
            // single-database "reopen and report ROW_COUNT" epilogue below.
            var iterations = args.Length > 2 ? int.Parse(args[2]) : 500;
            await BeginTransactionRacingDatabaseDispose(path, iterations);
            Console.WriteLine($"ITERATIONS:{iterations}");
            Console.WriteLine("DONE");
            return 0;
        default:
            Console.Error.WriteLine($"unknown scenario: {scenario}");
            return 2;
    }
}
catch (Exception ex)
{
    // A normal managed exception here is NOT the bug this harness hunts for - it is evidence
    // the process is still alive and behaving. Reported distinctly from a crash (which never
    // reaches this catch at all) so the spawning test can tell the two apart.
    Console.WriteLine($"MANAGED_EXCEPTION:{ex.GetType().Name}:{ex.Message}");
    return 1;
}

// Reopen the database fresh in a brand-new LadybugDatabase instance and report the row count.
// Reaching this line at all is itself part of the evidence (the process survived); the count
// additionally proves the engine's on-disk state is exactly what the scenario should have left
// behind - a real rollback (0 rows) or a real commit (1 row), not silent corruption.
using var verifyDb = new LadybugDatabase(path);
await using var verifyConn = await verifyDb.ConnectAsync();
await using var verifyResult = await verifyConn.QueryAsync("MATCH (n:T) RETURN count(n)");
await foreach (var row in verifyResult)
    Console.WriteLine($"ROW_COUNT:{row.GetValue(0).AsInt64()}");

Console.WriteLine("DONE");
return 0;

// db.Dispose() runs while the transaction is still open (no CommitAsync/RollbackAsync ever
// ran) - the exact ordering from the crash report: parent disposed first, then the transaction,
// then the connection.
static async Task DbDisposedFirst_TransactionLeftOpen(string path)
{
    var db = new LadybugDatabase(path);
    var conn = await db.ConnectAsync();
    await using (var _ = await conn.QueryAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

    var tx = await conn.BeginTransactionAsync();
    await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

    db.Dispose();
    await tx.DisposeAsync();
    await conn.DisposeAsync();
}

// Same ordering, but the transaction was committed before the database was disposed - a
// sanity check that the fix for the open-transaction case does not disturb the already-correct
// committed case.
static async Task DbDisposedFirst_TransactionAlreadyCommitted(string path)
{
    var db = new LadybugDatabase(path);
    var conn = await db.ConnectAsync();
    await using (var _ = await conn.QueryAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

    var tx = await conn.BeginTransactionAsync();
    await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }
    await tx.CommitAsync();

    db.Dispose();
    await tx.DisposeAsync();
    await conn.DisposeAsync();
}

// The transaction object itself is never touched again after BeginTransactionAsync - the
// connection is disposed directly while a transaction is still open on it, and only then is
// the database disposed (via the `using` at the end of this method).
static async Task ConnectionDisposedFirst_TransactionLeftOpen(string path)
{
    using var db = new LadybugDatabase(path);
    var conn = await db.ConnectAsync();
    await using (var _ = await conn.QueryAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

    var tx = await conn.BeginTransactionAsync();
    await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

    await conn.DisposeAsync();
    _ = tx; // deliberately never committed, rolled back, or disposed directly
}

static async Task ConnectionDisposedFirst_TransactionAlreadyCommitted(string path)
{
    using var db = new LadybugDatabase(path);
    var conn = await db.ConnectAsync();
    await using (var _ = await conn.QueryAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

    var tx = await conn.BeginTransactionAsync();
    await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }
    await tx.CommitAsync();

    await conn.DisposeAsync();
}

// No `using`/`await using` and no explicit Dispose/DisposeAsync call anywhere below - once this
// method returns, db/conn/tx are reachable from nowhere else, so the GC is free to collect and
// finalize them whenever it likes. LadybugDatabase and LadybugConnection have no finalizers of
// their own; only the underlying SafeHandles (LbugDatabaseHandle/LbugConnectionHandle) do, and
// their finalization order relative to each other is not guaranteed by the CLR - this scenario
// exists specifically to exercise that real GC finalizer path end to end, not just the explicit
// Dispose/DisposeAsync paths the other scenarios above cover.
static async Task AbandonWithoutDisposal_TransactionLeftOpen(string path)
{
    var db = new LadybugDatabase(path);
    var conn = await db.ConnectAsync();
    await using (var _ = await conn.QueryAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

    var tx = await conn.BeginTransactionAsync();
    await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }

    _ = tx; // never committed, rolled back, or disposed - and neither are conn or db
}

static void ForceFullGarbageCollection()
{
    for (var i = 0; i < 3; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

// conn.BeginTransactionAsync() racing a concurrent db.Dispose() on the owning database - pure
// live concurrency, no GC and no abandonment involved. A Barrier forces both operations to start
// at the same instant: the engine-level BEGIN TRANSACTION can succeed and open a transaction
// before ANY C# bookkeeping registers it, and if the database is destroyed inside that window
// the connection is later left holding a transaction the engine considers open against memory
// that no longer exists.
//
// The window this is trying to hit is narrow (a handful of native/managed instructions), so a
// single Barrier-synchronized attempt is not reliable on every machine/scheduler - looped many
// times within one process (fresh database each time) rather than relying on the outer test
// re-invoking the whole process per attempt, since process startup cost would otherwise dominate
// and starve the loop of attempts within any reasonable time budget.
static async Task BeginTransactionRacingDatabaseDispose(string basePath, int iterations)
{
    for (var i = 0; i < iterations; i++)
    {
        var path = $"{basePath}-{i}";
        try
        {
            var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            using var barrier = new Barrier(2);
            var beginTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await using var tx = await conn.BeginTransactionAsync();
                }
                catch
                {
                    // Any managed exception here (ObjectDisposedException, LadybugException,
                    // etc.) is fine - this scenario is only hunting for a process crash, not a
                    // specific outcome.
                }
            });
            var disposeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                db.Dispose();
            });

            await Task.WhenAll(beginTask, disposeTask);

            try
            {
                await conn.DisposeAsync();
            }
            catch
            {
                // Best effort - the race above may have already left the connection in a state
                // where this itself throws a normal managed exception, which is fine.
            }
        }
        finally
        {
            foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            }
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
        }
    }
}
