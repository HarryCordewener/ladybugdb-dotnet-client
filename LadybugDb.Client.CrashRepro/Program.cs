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
// Usage: LadybugDb.Client.CrashRepro <scenario> <db-path>
// Scenarios: db-first-open, db-first-committed, conn-first-open, conn-first-committed (explicit
// dispose orderings) and gc-abandon-open (abandons everything without disposing anything and
// forces the real GC finalizer path - see AbandonWithoutDisposal_TransactionLeftOpen).
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
