# Core production hardening — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the core-library gaps the readiness review found: the interop-heavy row read path, async-only disposal, the unexposed checkpoint settings, per-call statement preparation, cancellation that cannot stop a running query, and the absence of telemetry hooks.

**Architecture:** Every change stays inside `LadybugDb.Client` and its two test projects. The row reader keeps `SafeHandle` for every object whose lifetime crosses a call boundary and drops it for the two structs upstream documents as C++-owned borrows. Cancellation registers `lbug_connection_interrupt` on the token around the native call, so the async-shaped-synchronous design is unchanged. Telemetry uses `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter` from the BCL, so the core package gains no dependency.

**Tech Stack:** .NET 10, `[LibraryImport]` interop, TUnit, BenchmarkDotNet (`LadybugDb.Client.Benchmarks`).

**Spec:** `docs/2026-09-06-production-readiness.md` ("Readiness by area" and "What the benchmarks say to do, in order") and `docs/research/2026-09-06-engine-c-api-cost-model.md` (§3, ownership rules).

## Global Constraints

- `TreatWarningsAsErrors` and `AnalysisMode=Recommended` on the shipping project: every public member needs XML docs.
- Never let `System.Int128` cross the P/Invoke boundary.
- The lifetime rule: every child handle holds a refcounted reference on its parent from construction until after its own native destroy. Do not reintroduce per-operation holds.
- Keep every existing test green; run `dotnet pack -c Release` before `dotnet test` (PackagingTests reads the nupkg).
- `dotnet test --filter` does not work; use `--treenode-filter "/*/*/ClassName/*"` from the test project directory.
- Commit after every task.

---

### Task 1: Read rows without per-cell handles or per-cell type lookups

**Files:**
- Modify: `LadybugDb.Client/LadybugQueryResult.cs` (`ReadRow`, constructor: cache column type ids)
- Modify: `LadybugDb.Client/Values/ValueReader.cs` (add `Read(lbug_value*, lbug_data_type_id)` overload that skips the type lookup)
- Test: `LadybugDb.Client.IntegrationTests/ReadPathTests.cs` (new)
- Benchmark: `LadybugDb.Client.Benchmarks/ReadPathBenchmarks.cs` (existing; `RowAccessors` should approach `Prototype_FastPath_Values`)

**Interfaces:**
- Consumes: `LbugNative.lbug_query_result_get_column_data_type`, `lbug_query_result_get_next`, `lbug_flat_tuple_get_value`, `lbug_value_is_null`, `LbugStructHandle.Acquire()`.
- Produces: no public change. `LadybugQueryResult` gains `private readonly lbug_data_type_id[] _columnTypes`.

- [ ] **Step 1: Write the failing test.** The test pins the behaviour the rewrite must preserve: every type still reads correctly through the fast path, NULLs are NULL, and a nested container column still goes through the full reader.

```csharp
public class ReadPathTests
{
    [Test]
    public async Task EveryScalarColumnReadsThroughTheCachedColumnType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, s STRING, d DOUBLE, b BOOL, n INT32, l INT64[], PRIMARY KEY(id))");
            await conn.ExecuteAsync("CREATE (:T {id: 1, s: 'a', d: 1.5, b: true, n: 7, l: [1, 2]})");
            await conn.ExecuteAsync("CREATE (:T {id: 2, s: 'b', d: 2.5, b: false, n: 8, l: []})");
            await conn.ExecuteAsync("CREATE (:T {id: 3})");

            var rows = new List<(long, string?, double?, bool?, int?, int?)>();
            await using var r = await conn.QueryAsync("MATCH (t:T) RETURN t.id, t.s, t.d, t.b, t.n, t.l ORDER BY t.id");
            await foreach (var row in r)
            {
                rows.Add((row.GetInt64(0),
                    row.GetValue(1).IsNull ? null : row.GetString(1),
                    row.GetValue(2).IsNull ? null : row.GetDouble(2),
                    row.GetValue(3).IsNull ? null : row.GetBoolean(3),
                    row.GetValue(4).IsNull ? null : row.GetInt32(4),
                    row.GetValue(5).IsNull ? null : row.GetValue(5).AsList().Count));
            }
            await Assert.That(rows).IsEquivalentTo([(1L, "a", 1.5, true, 7, 2), (2L, "b", 2.5, false, 8, 0), (3L, null, null, null, null, null)]);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run it; it passes already** (this is a characterization test). Run: `cd LadybugDb.Client.IntegrationTests && dotnet run -c Release -- --treenode-filter "/*/*/ReadPathTests/*"`. Expected: PASS. Keep it; it guards the rewrite.

- [ ] **Step 3: Cache the column types once per result.** In the `LadybugQueryResult` constructor, after `_columnNames = ReadColumnNames();`, add `_columnTypes = ReadColumnTypes();` implemented like `ReadColumnNames` but calling `lbug_query_result_get_column_data_type(result, i, &type)` into a stack `lbug_logical_type`, reading `lbug_data_type_get_id(&type)`, then `lbug_data_type_destroy(&type)` in a `finally`. (`FastRowReader.ReadColumnTypes` in the benchmark project is the reference; move that logic here.)

- [ ] **Step 4: Rewrite `ReadRow`.** Keep the `_database`/`_root` leases and the `has_next` check exactly as they are. Replace the `LbugFlatTupleHandle`/`LbugValueHandle` allocations with stack structs:

```csharp
using var lease = _handle.Acquire();
var qr = (lbug_query_result*)lease.Pointer;
if (LbugNative.lbug_query_result_has_next(qr) == 0) return null;

lbug_flat_tuple tuple;
if (LbugNative.lbug_query_result_get_next(qr, &tuple) != lbug_state.LbugSuccess)
    throw new LadybugException(NativeString.WithErrorDetail("Failed to advance to the next row."));

var values = new LadybugValue[_columnNames.Length];
lbug_value cell;
for (var i = 0; i < values.Length; i++)
{
    if (LbugNative.lbug_flat_tuple_get_value(&tuple, (ulong)i, &cell) != lbug_state.LbugSuccess)
        throw new LadybugException(NativeString.WithErrorDetail($"Failed to read column {i}."));
    values[i] = ValueReader.Read(&cell, _columnTypes[i]);
}
return new LadybugRow(values, _columnNames);
```

Document in the method's remarks why no destroy calls appear: both structs are C++-owned borrows (`_is_owned_by_cpp = true`) whose `lbug_flat_tuple_destroy`/`lbug_value_destroy` are no-ops (upstream `src/c_api/flat_tuple.cpp` and `value.cpp`), and the result lease held for the whole read is the guarantee the per-call leases used to provide.

- [ ] **Step 5: Add the typed `ValueReader.Read` overload.** `internal static unsafe LadybugValue Read(lbug_value* value, lbug_data_type_id typeId)` performs the null check, then the same `switch` as today but without calling `lbug_value_get_data_type`. Refactor the existing `Read(lbug_value*, int depth)` to obtain the type id and call a shared `ReadKnownType(value, typeId, depth)`. Nested containers keep the existing per-element type lookup (their element types are not cached).

- [ ] **Step 6: Delete `LbugFlatTupleHandle`** and the `LbugValueHandle.GetValue(LbugFlatTupleHandle, ...)` factory if nothing else uses them (`grep -rn LbugFlatTupleHandle`). Keep the other `LbugValueHandle` factories: list/struct/map/node/rel element values are caller-owned and still need destroy.

- [ ] **Step 7: Run the whole suite.** `dotnet pack -c Release && dotnet test`. Expected: all green (the LeakTests are the ones most likely to notice a mistake).

- [ ] **Step 8: Benchmark.** `cd LadybugDb.Client.Benchmarks && dotnet run -c Release -- --filter '*ReadPathBenchmarks*'`. Expected: `RowAccessors` within 15% of `Prototype_FastPath_Values` (about 3 ms per 10,000 rows, down from about 8.6 ms). Record the numbers in the commit message.

- [ ] **Step 9: Commit.** `git commit -am "perf: read rows through stack-allocated borrows and cached column types"`.

### Task 2: Synchronous disposal alongside `IAsyncDisposable`

**Files:**
- Modify: `LadybugDb.Client/LadybugConnection.cs`, `LadybugQueryResult.cs`, `LadybugPreparedStatement.cs`, `LadybugTransaction.cs`
- Test: `LadybugDb.Client.IntegrationTests/SyncDisposalTests.cs` (new)
- Docs: `docs/USAGE.md` "Disposal and lifetime" section: one paragraph stating both are equivalent.

**Interfaces:**
- Produces: each of the four types implements `IDisposable`; `Dispose()` performs exactly what `DisposeAsync()` does today (they all complete synchronously) and `DisposeAsync()` becomes `Dispose(); return ValueTask.CompletedTask;`.

- [ ] **Step 1: Write the failing test.**

```csharp
public class SyncDisposalTests
{
    [Test]
    public async Task UsingBlocks_DisposeEveryType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            using var conn = await db.ConnectAsync();
            await conn.ExecuteAsync("CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))");
            using (var tx = await conn.BeginTransactionAsync())
            {
                await conn.ExecuteAsync("CREATE (:T {id: 1})");
                await tx.CommitAsync();
            }
            using var stmt = await conn.PrepareAsync("MATCH (t:T) WHERE t.id = $id RETURN t.id");
            stmt.Bind("id", 1L);
            using var result = await stmt.ExecuteAsync();
            await Assert.That(result.HasNext).IsTrue();
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposeAndDisposeAsync_AreIdempotentTogether()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            var conn = await db.ConnectAsync();
            var result = await conn.QueryAsync("RETURN 1");
            result.Dispose();
            await result.DisposeAsync();
            result.Dispose();
            conn.Dispose();
            await conn.DisposeAsync();
            await Assert.That(() => conn.QueryAsync("RETURN 1").AsTask()).Throws<ObjectDisposedException>();
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run it; expected: compile failure** (`using var` on a type that is not `IDisposable`).
- [ ] **Step 3: Implement.** Add `: IDisposable` to each class; move the body of `DisposeAsync` into `Dispose()`; `DisposeAsync` calls `Dispose()` and returns `ValueTask.CompletedTask`. For `LadybugTransaction`, `Dispose()` performs the same rollback-and-swallow as `DisposeAsync` does (the rollback query completes synchronously; call `QueryUncheckedAsync(...).AsTask().GetAwaiter().GetResult()` is NOT acceptable — instead add an internal synchronous `LadybugConnection.ExecuteUncheckedSync(string)` that both paths use). Check `LiveCount` decrements exactly once.
- [ ] **Step 4: Run the suite.** Expected: green, including `SelectDisposalTests` and `LeakTests`.
- [ ] **Step 5: Update `docs/USAGE.md`** ("Disposal and lifetime"): "Every disposable type implements both `IDisposable` and `IAsyncDisposable`; the two are equivalent because every operation completes synchronously. Use whichever fits the call site."
- [ ] **Step 6: Commit.** `git commit -am "feat: IDisposable on connection, result, statement and transaction"`.

### Task 3: Expose the checkpoint and checksum settings

**Files:**
- Modify: `LadybugDb.Client/LadybugConfig.cs`, `LadybugDb.Client/LadybugDatabase.cs` (`BuildConfig`)
- Test: `LadybugDb.Client.IntegrationTests/DatabaseConfigTests.cs` (new)
- Docs: `docs/USAGE.md` `LadybugConfig` table.

**Produces:** `LadybugConfig.AutoCheckpoint` (`bool`, default `true`), `CheckpointThreshold` (`ulong`, bytes, `0` = engine default of 16 MiB), `EnableChecksums` (`bool`, default `true`), `ThrowOnWalReplayFailure` (`bool`, default `true`). Defaults must match `lbug_default_system_config()`; read them from it in the test rather than hard-coding.

- [ ] **Step 1: Failing test.**

```csharp
[Test]
public async Task CheckpointSettings_ReachTheEngine()
{
    var path = TestDatabase.NewPath();
    try
    {
        using var db = new LadybugDatabase(path, new LadybugConfig { AutoCheckpoint = false, CheckpointThreshold = 1024 * 1024 });
        await using var conn = await db.ConnectAsync();
        await using var r = await conn.QueryAsync("CALL current_setting('auto_checkpoint') RETURN *");
        await foreach (var row in r) await Assert.That(row.GetString(0)).IsEqualTo("False");
    }
    finally { TestDatabase.Cleanup(path); }
}
```
(Confirm the exact setting name and value format against the engine first: run `CALL current_setting('auto_checkpoint') RETURN *` in a scratch program; adjust the assertion to the observed text.)

- [ ] **Step 2: Run; expected: compile failure** (no `AutoCheckpoint`).
- [ ] **Step 3: Implement** the four properties with XML docs quoting the engine defaults, and map them in `BuildConfig` (`native.auto_checkpoint = ToNativeBool(...)`, etc.; only override `checkpoint_threshold` when non-zero).
- [ ] **Step 4: Run; expected: PASS.** Then the full suite.
- [ ] **Step 5: Document** the four rows in `docs/USAGE.md`'s config table, with the readiness report's observation (a 100,000-object database grew from 102 MB to 434 MB over 250,000 mutations) as the reason a server wants to tune them.
- [ ] **Step 6: Commit.** `git commit -am "feat: expose auto_checkpoint, checkpoint_threshold, enable_checksums and throw_on_wal_replay_failure"`.

### Task 4: Per-connection prepared-statement cache

**Files:**
- Create: `LadybugDb.Client/StatementCache.cs`
- Modify: `LadybugDb.Client/LadybugConnection.cs` (`QueryAsync(string, object, ct)`, `ExecuteAsync(string, object, ct)`, `SelectCore`, `DisposeAsync`), `LadybugDb.Client/LadybugPreparedStatement.cs` (needs an internal "reset bound values" step? No: rebinding overwrites by name, and a parameter missing from a later call keeps the previous value — so the cache must bind every parameter each time and reject a parameter set whose names differ from the first use; see Step 3)
- Test: `LadybugDb.Client.Tests/StatementCacheTests.cs`, `LadybugDb.Client.IntegrationTests/StatementCacheIntegrationTests.cs`

**Produces:** `LadybugConfig.StatementCacheSize` (`int`, default 128, `0` disables). `LadybugConnection` owns one `StatementCache`. `internal sealed class StatementCache : IDisposable` with `TryGet(string cypher, out LadybugPreparedStatement)`, `Add(string cypher, LadybugPreparedStatement)`, `Evict(string cypher)`; least-recently-used, capacity from config.

- [ ] **Step 1: Unit test the cache policy** (no engine): construct with capacity 2, add A, B, touch A, add C → B evicted (its statement disposed: use a fake `IDisposable` counter through an internal constructor that takes `Func<string, T>`; make the cache generic `StatementCache<T> where T : IDisposable`).
- [ ] **Step 2: Integration test.** Prepare-count evidence: `LadybugPreparedStatement` gains `internal static long PreparedCount` (Interlocked, like `LadybugQueryResult.LiveCount`); run `conn.QueryAsync(cypher, new { d = 1L })` three times; assert `PreparedCount` advanced by exactly one. Then the parameter-shape rule: `conn.QueryAsync(cypher, new { d = 1L })` followed by `conn.QueryAsync(cypher, new { d = 1L, extra = 2L })` throws `ArgumentException` naming the extra parameter (a statement cached for one shape must not silently carry stale bindings for another). Then eviction on failure: cause a cached statement to fail at execute (drop the table), assert the next call re-prepares (`PreparedCount` advances) and reports the engine error.
- [ ] **Step 3: Implement.** Key = the exact cypher string. On hit: `ParameterBinder.BindAll` binds every supplied name; `ParameterBinder.Enumerate`'s names are compared (ordinal set equality) with the names recorded at first use, and a mismatch throws. On execute failure with `LadybugException`, evict and rethrow. `Select<T>` and `ExecuteAsync(string, object)` go through the same path. `Dispose` disposes every cached statement. `StatementCacheSize = 0` keeps today's prepare-per-call behaviour.
- [ ] **Step 4: Suite green; benchmark** `--filter '*PointLookupBenchmarks*'`: `ParametersObject` should drop from ~122 µs to ~62 µs.
- [ ] **Step 5: Docs.** `docs/USAGE.md`: a "Statement cache" subsection under parameterized queries: what is cached, the parameter-shape rule, the config knob, and that `PrepareAsync` is still the right call when you hold the statement yourself.
- [ ] **Step 6: Commit.** `git commit -am "perf: cache prepared statements per connection for the parameters overloads"`.

### Task 5: Cancellation that interrupts the engine

**Files:**
- Modify: `LadybugDb.Client/LadybugConnection.cs` (`Execute` gains a `CancellationToken`), `LadybugDb.Client/LadybugPreparedStatement.cs` (`Execute` likewise), `LadybugDb.Client/QueryFailureClassifier.cs` (map the engine's `"Interrupted."` to `OperationCanceledException`)
- Test: `LadybugDb.Client.IntegrationTests/CancellationTests.cs`, `LadybugDb.Client.Tests/QueryFailureClassifierTests.cs`

**Produces:** a `CancellationToken` passed to `QueryAsync`/`ExecuteAsync`/`Select<T>`/`LadybugPreparedStatement.ExecuteAsync` cancels a running query: the engine returns the `"Interrupted."` error and the call throws `OperationCanceledException` carrying the token.

- [ ] **Step 1: Failing tests.** Classifier: `Classify("Interrupted.", stmt)` returns... the classifier returns `LadybugException`; add `internal static bool IsInterrupted(string? message)` and test it. Integration: start a query that runs for a while (`UNWIND range(1, 5000000) AS i WITH i WHERE i % 7 = 0 RETURN count(i)` — measure first that it takes > 200 ms on the host, else scale up), cancel the token after 50 ms from a `Task.Delay`, assert `OperationCanceledException` within one second, and assert the connection is still usable afterwards (`RETURN 1`).
- [ ] **Step 2: Run; expected: the integration test times out / the query completes** (no interrupt today).
- [ ] **Step 3: Implement.** In `Execute(string cypher, CancellationToken ct)`: `ct.ThrowIfCancellationRequested()`; if `ct.CanBeCanceled`, take a connection lease and `using var reg = ct.UnsafeRegister(static state => ((LadybugConnection)state!).Interrupt(), this)` where `Interrupt()` acquires a lease on the connection handle and calls `lbug_connection_interrupt` (upstream: it only flips an atomic; safe from any thread). After the native call, if the result failed and `QueryFailureClassifier.IsInterrupted(message)` and `ct.IsCancellationRequested`, throw `new OperationCanceledException(message, ct)`. Same in the prepared path.
- [ ] **Step 4: Suite green.** The interrupt flag: check in upstream `client_context.cpp` whether an interrupt set while no query is running is cleared at the next query start (`activeQuery.reset()` at `query` entry); if it is not, a token cancelled after completion could poison the next query — test that case explicitly (cancel after the query finished, then run another query, expect success).
- [ ] **Step 5: Docs.** `docs/USAGE.md` concurrency/cancellation: one paragraph on what cancellation does now, and that it is best-effort between engine operators.
- [ ] **Step 6: Commit.** `git commit -am "feat: cancellation interrupts the running query"`.

### Task 6: Telemetry hooks in the core (no dependencies)

**Files:**
- Create: `LadybugDb.Client/Diagnostics/LadybugDiagnostics.cs`
- Modify: `LadybugDb.Client/LadybugConnection.cs`, `LadybugPreparedStatement.cs` (wrap `Execute`), `LadybugTransaction.cs` (begin/commit/rollback)
- Test: `LadybugDb.Client.IntegrationTests/DiagnosticsTests.cs`
- Docs: `docs/USAGE.md` "Observability" section.

**Produces:** `public static class LadybugDiagnostics { public const string ActivitySourceName = "LadybugDb.Client"; public const string MeterName = "LadybugDb.Client"; }`. Internally an `ActivitySource` and a `Meter` with a `Histogram<double>` named `db.client.operation.duration` (unit `s`, OpenTelemetry semantic conventions 1.33 database stable attributes: `db.system.name = "ladybugdb"`, `db.namespace = <database path>`, `db.query.text = <cypher>`, `db.operation.name` = first keyword, `error.type` on failure). Activities are created only when a listener exists (`ActivitySource.HasListeners()`), so the cost without a listener is one branch.

- [ ] **Step 1: Failing test.** Register an `ActivityListener` for `LadybugDiagnostics.ActivitySourceName`, run a query, assert one activity with `db.query.text` equal to the cypher and `db.system.name == "ladybugdb"`; register a `MeterListener` for the histogram and assert one measurement > 0 with the same `db.system.name` tag.
- [ ] **Step 2: Run; expected: compile failure.**
- [ ] **Step 3: Implement** with a private `static Activity? Start(string cypher, string dbPath)` helper and a `record struct` timer using `Stopwatch.GetTimestamp()`; record on success and on failure (`error.type` = exception type full name).
- [ ] **Step 4: Suite green; run `--filter '*PointLookupBenchmarks*'`** to confirm no listener costs < 100 ns (compare against the previous run).
- [ ] **Step 5: Docs.** `docs/USAGE.md` "Observability": the source and meter names, the attributes, how to subscribe with OpenTelemetry (`AddSource("LadybugDb.Client")`, `AddMeter("LadybugDb.Client")`).
- [ ] **Step 6: Commit.** `git commit -am "feat: ActivitySource and Meter following the OpenTelemetry database conventions"`.

### Task 7: Refresh the readiness report

- [ ] Update `docs/2026-09-06-production-readiness.md` "Readiness by area" bullets and the checklist rows that these tasks close; add the new read-path and point-lookup numbers to the benchmark section; commit `docs: readiness report reflects the core hardening`.
