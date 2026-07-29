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
// forces the real GC finalizer path - see AbandonWithoutDisposal_TransactionLeftOpen);
// begin-vs-dispose (BeginTransactionAsync racing a concurrent db.Dispose() - live concurrency,
// no GC, no abandonment; [iterations] controls how many fresh-database attempts to loop through
// in this one process, default 500 - see BeginTransactionRacingDatabaseDispose); and
// begin-vs-begin (two concurrent BeginTransactionAsync calls racing each other on the SAME
// connection - regresses the fix-round-4 database-leak finding; [iterations] as above, default
// 40 - see BeginTransactionRacingBeginTransaction). Prints a VMSIZE:<iteration>:<bytes> line
// (parsed from /proc/self/status) every 5 iterations so the spawning test can assert the
// per-database ~8 TiB virtual-address reservation is not accumulating across iterations.
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
        case "begin-vs-begin":
            var beginVsBeginIterations = args.Length > 2 ? int.Parse(args[2]) : 40;
            await BeginTransactionRacingBeginTransaction(path, beginVsBeginIterations);
            Console.WriteLine($"ITERATIONS:{beginVsBeginIterations}");
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

// Two concurrent conn.BeginTransactionAsync() calls racing each other on the SAME connection -
// regresses the fix-round-4 finding: an earlier, unsynchronized version of the nested-begin
// guard (LadybugConnection._activeTransaction, a plain field) plus a boolean (not
// reference-counted) database hold in LbugConnectionHandle let both racing callers pass the
// check-then-act guard and both take/lose the hold, so the loser's failure-path release could
// cancel the winner's still-open hold - LbugDatabaseHandle.ReleaseHandle (and therefore
// lbug_database_destroy, which unmaps this database's ~8 TiB virtual-address reservation) then
// never ran for that iteration's database. Measured pre-fix: ~40% of racing iterations leaked a
// whole lbug_database, VmSize climbing from a flat 8 TiB to 112 TiB by iteration 30 and the
// process dying with a failed mmap around iteration 33-37.
//
// The Barrier only synchronizes the two tasks' START; it does not force them to genuinely
// overlap all the way through BeginTransactionAsync - the scheduler is free to run one task to
// full completion (begin, increment, and the `await using`'s own dispose/rollback) before the
// other's continuation even resumes past the barrier. When that happens both calls legitimately
// succeed, one after the other, which is correct behaviour, not a race hit - so this scenario
// does NOT assert "exactly one success" as a hard failure (an earlier version of this harness
// did, and was itself flaky: it failed the invariant on legitimately-sequential runs even against
// the FIXED client). What it does assert, every iteration: at least one call must succeed (two
// legitimate client-side rejections in a row would itself be a bug), and whenever exactly one
// succeeds, the loser must have failed with InvalidOperationException specifically - client-side,
// before BEGIN TRANSACTION ever reaches the engine - not some other exception type. The actual
// regression signal for the leak/crash this scenario exists to catch is VmSize staying flat
// across iterations and the process exiting 0, checked below and by the caller respectively.
static async Task BeginTransactionRacingBeginTransaction(string basePath, int iterations)
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
            var successCount = 0;
            Exception? exceptionA = null;
            Exception? exceptionB = null;

            var taskA = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await using var tx = await conn.BeginTransactionAsync();
                    Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    exceptionA = ex;
                }
            });
            var taskB = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await using var tx = await conn.BeginTransactionAsync();
                    Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    exceptionB = ex;
                }
            });

            await Task.WhenAll(taskA, taskB);

            var finalSuccessCount = Volatile.Read(ref successCount);
            if (finalSuccessCount == 0)
                throw new InvalidOperationException(
                    $"iteration {i}: expected at least 1 of the 2 racing BeginTransactionAsync " +
                    $"calls to succeed, got 0. exceptionA={exceptionA?.GetType().Name}, " +
                    $"exceptionB={exceptionB?.GetType().Name}.");

            if (finalSuccessCount == 1)
            {
                // Only meaningful to check in this case - see the remarks above on why this
                // scenario does not require exactly 1 success on every iteration.
                var loserException = exceptionA ?? exceptionB;
                if (loserException is not InvalidOperationException)
                    throw new InvalidOperationException(
                        $"iteration {i}: expected the losing BeginTransactionAsync call to fail " +
                        $"with InvalidOperationException, got " +
                        $"{loserException?.GetType().Name ?? "no exception at all"}.");
            }

            await conn.DisposeAsync();
            db.Dispose();

            if (i % 5 == 0 || i == iterations - 1)
            {
                var vmSize = ReadVmSizeBytes();
                if (vmSize is { } bytes) Console.WriteLine($"VMSIZE:{i}:{bytes}");
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

// Parses VmSize (virtual address space reserved, not resident memory - what the ~8 TiB
// per-database mmap reservation shows up as) out of /proc/self/status. Linux-only; returns null
// anywhere that file doesn't exist so this harness degrades gracefully rather than throwing.
static long? ReadVmSizeBytes()
{
    const string statusPath = "/proc/self/status";
    if (!File.Exists(statusPath)) return null;

    foreach (var line in File.ReadLines(statusPath))
    {
        if (!line.StartsWith("VmSize:", StringComparison.Ordinal)) continue;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Format: "VmSize:\t123456 kB"
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb)) return kb * 1024;
        return null;
    }

    return null;
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
