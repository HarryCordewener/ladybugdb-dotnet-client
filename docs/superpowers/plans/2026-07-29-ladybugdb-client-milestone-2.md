# LadybugDb.Client Milestone 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the client's data surface — typed value marshalling for every LadybugDB type, all 20 prepared-statement binds, row enumeration via `IAsyncEnumerable`, and Cypher-driven transactions — replacing the foundation's temporary `ReadStringAsync` seam.

**Architecture:** A `LadybugValue` reads the native type tag once and dispatches to the matching `lbug_value_get_*`. Container types (list, struct, map) recurse through the same path. `LadybugRow` wraps a flat tuple and exposes typed column access; `LadybugQueryResult` becomes `IAsyncEnumerable<LadybugRow>`. Prepared statements own their bind surface. Transactions are Cypher statements (`BEGIN TRANSACTION`/`COMMIT`/`ROLLBACK`) — the C API has no transaction functions.

**Tech Stack:** .NET 10, C# 13, TUnit 1.62.0, LadybugDB 0.18.3 C API.

## Global Constraints

- Target `net10.0`; SDK pinned to `10.0.300` in `global.json`, which also carries the `"test": { "runner": "Microsoft.Testing.Platform" }` block that makes `dotnet test` work. Do not remove it.
- `dotnet test --filter` (VSTest syntax) does **not** work — it silently reports "Zero tests ran" and exits 5. Use `dotnet test <project> -- --treenode-filter "/*/*/ClassName/*"`.
- `TreatWarningsAsErrors` is on. `GenerateDocumentationFile` is on, so **every public member needs an XML doc comment** or CS1591 fails the build.
- Everything under `LadybugDb.Client/Native/` and `LadybugDb.Client/Interop/` stays `internal`. Never public.
- **There is no raw pointer accessor.** `LbugStructHandle.Pointer` does not exist. Use `using var lease = handle.Acquire();` then `lease.Pointer`. `Lease` is a `readonly ref struct` — it cannot cross an `await`. Nesting leases is required and safe; the **parent (database) lease must be outermost**.
- Every handle factory follows: `AllocateUnowned` → native call → set `adopted = true` **immediately before** `Adopt` on success → `FreeUnowned` in `finally` otherwise. `ReleaseHandle` is always `try { destroy } catch { return false } finally { FreeStorage(); }` — it runs on the finalizer thread and must never throw.
- **Every `char*` the API returns must go through `NativeString.TakeOwnership` or `TakeOwnershipOrNull`**, which free it with `lbug_destroy_string`. This is the single largest leak risk.
- `lbug_state` is `{ LbugSuccess = 0, LbugError = 1 }`. Check every returned state.
- Native binaries are never committed. Run `bash scripts/fetch-liblbug.sh` before the first build; `dotnet pack -c Release` must run before `dotnet test` or the two `PackagingTests` fail (they inspect real `.nupkg` files).
- Baseline is 39/39 tests passing on `main` at `4f9b8fb`.

## Verified native signatures — use exactly, do not guess

```csharp
// Type identification. NOTE: get_data_type returns void and fills a logical type
// that must itself be destroyed with lbug_data_type_destroy.
internal static partial void          lbug_value_get_data_type(lbug_value* value, lbug_logical_type* out_type);
internal static partial lbug_data_type_id lbug_data_type_get_id(lbug_logical_type* data_type);
internal static partial void          lbug_data_type_destroy(lbug_logical_type* data_type);

// Scalars
internal static partial lbug_state lbug_value_get_bool  (lbug_value*, bool* out_result);
internal static partial lbug_state lbug_value_get_int8  (lbug_value*, sbyte* out_result);
internal static partial lbug_state lbug_value_get_int16 (lbug_value*, short* out_result);
internal static partial lbug_state lbug_value_get_int32 (lbug_value*, int* out_result);
internal static partial lbug_state lbug_value_get_int64 (lbug_value*, long* out_result);
internal static partial lbug_state lbug_value_get_uint8 (lbug_value*, byte* out_result);
internal static partial lbug_state lbug_value_get_uint16(lbug_value*, ushort* out_result);
internal static partial lbug_state lbug_value_get_uint32(lbug_value*, uint* out_result);
internal static partial lbug_state lbug_value_get_uint64(lbug_value*, ulong* out_result);
internal static partial lbug_state lbug_value_get_float (lbug_value*, float* out_result);
internal static partial lbug_state lbug_value_get_double(lbug_value*, double* out_result);
internal static partial lbug_state lbug_value_get_string(lbug_value*, sbyte** out_result);
internal static partial lbug_state lbug_value_get_blob  (lbug_value*, byte** out_result, ulong* out_length);

// Temporal — plain epoch units, no struct tm anywhere
internal partial struct lbug_date_t      { internal int days; }                        // days since 1970-01-01
internal partial struct lbug_timestamp_t { internal long value; }                       // microseconds since epoch
internal partial struct lbug_interval_t  { internal int months; internal int days; internal long micros; }
internal partial struct lbug_internal_id_t { internal ulong table_id; internal ulong offset; }

internal static partial lbug_state lbug_value_get_date        (lbug_value*, lbug_date_t* out_result);
internal static partial lbug_state lbug_value_get_timestamp   (lbug_value*, lbug_timestamp_t* out_result);
internal static partial lbug_state lbug_value_get_interval    (lbug_value*, lbug_interval_t* out_result);
internal static partial lbug_state lbug_value_get_internal_id (lbug_value*, lbug_internal_id_t* out_result);

// Containers — each *_element / *_field_value / *_key / *_value fills a NEW lbug_value
// that the caller owns and must destroy.
internal static partial lbug_state lbug_value_get_list_size        (lbug_value*, ulong* out_result);
internal static partial lbug_state lbug_value_get_list_element     (lbug_value*, ulong index, lbug_value* out_value);
internal static partial lbug_state lbug_value_get_struct_num_fields(lbug_value*, ulong* out_result);
internal static partial lbug_state lbug_value_get_struct_field_name(lbug_value*, ulong index, sbyte** out_result);
internal static partial lbug_state lbug_value_get_struct_field_value(lbug_value*, ulong index, lbug_value* out_value);
internal static partial lbug_state lbug_value_get_map_size         (lbug_value*, ulong* out_result);
internal static partial lbug_state lbug_value_get_map_key          (lbug_value*, ulong index, lbug_value* out_key);
internal static partial lbug_state lbug_value_get_map_value        (lbug_value*, ulong index, lbug_value* out_value);

// Binds — param_name is a plain C string; note bind_bool takes byte, not bool
internal static partial lbug_state lbug_prepared_statement_bind_bool  (lbug_prepared_statement*, sbyte* param_name, byte value);
internal static partial lbug_state lbug_prepared_statement_bind_int64 (lbug_prepared_statement*, sbyte* param_name, long value);
internal static partial lbug_state lbug_prepared_statement_bind_string(lbug_prepared_statement*, sbyte* param_name, sbyte* value);
internal static partial lbug_state lbug_prepared_statement_bind_date  (lbug_prepared_statement*, sbyte* param_name, lbug_date_t value);
internal static partial lbug_state lbug_prepared_statement_bind_interval(lbug_prepared_statement*, sbyte* param_name, lbug_interval_t value);
internal static partial lbug_state lbug_prepared_statement_bind_value (lbug_prepared_statement*, sbyte* param_name, lbug_value* value);
```

Relevant `lbug_data_type_id` members: `LBUG_BOOL=22`, `LBUG_INT64=23`, `LBUG_INT32=24`, `LBUG_INT16=25`, `LBUG_INT8=26`, `LBUG_UINT64=27`, `LBUG_UINT32=28`, `LBUG_UINT16=29`, `LBUG_UINT8=30`, `LBUG_INT128=31`, `LBUG_DOUBLE=32`, `LBUG_FLOAT=33`, `LBUG_DATE=34`, `LBUG_TIMESTAMP=35`, `LBUG_TIMESTAMP_SEC=36`, `LBUG_TIMESTAMP_MS=37`, `LBUG_TIMESTAMP_NS=38`, `LBUG_TIMESTAMP_TZ=39`, `LBUG_INTERVAL=40`, `LBUG_DECIMAL=41`, `LBUG_INTERNAL_ID=42`, `LBUG_STRING=50`, `LBUG_BLOB=51`, `LBUG_LIST=52`, `LBUG_ARRAY=53`, `LBUG_NODE=10`, `LBUG_REL=11`, `LBUG_SERIAL=13`.

**Verify each against `LadybugDb.Client/Native/LbugNative.g.cs` before use.** If the generated code disagrees with anything above, the generated code wins — and say so in your report.

## File Structure

```
LadybugDb.Client/
  Values/LadybugType.cs            public enum, the .NET-facing type tag
  Values/LadybugValue.cs           public readonly struct, typed accessors
  Values/ValueReader.cs            internal, native lbug_value -> LadybugValue
  Values/LadybugNode.cs            public, node/rel projections
  LadybugRow.cs                    public, one tuple; typed column access
  LadybugQueryResult.cs            MODIFY: IAsyncEnumerable<LadybugRow>, NextResultAsync
  LadybugPreparedStatement.cs      public, 20 typed binds + ExecuteAsync
  LadybugTransaction.cs            public, Cypher-driven BEGIN/COMMIT/ROLLBACK
  LadybugConnection.cs             MODIFY: PrepareAsync, BeginTransactionAsync
  LadybugDatabase.cs               MODIFY: consume WriteLock; expose multi-writes config
  Interop/LbugValueHandle.cs       MODIFY: factories for child values
  Interop/LbugLogicalTypeHandle.cs NEW: owns lbug_logical_type
  Interop/LbugPreparedStatementHandle.cs NEW
```

---

### Task 1: Type identification and scalar values

**Files:**
- Create: `LadybugDb.Client/Values/LadybugType.cs`, `LadybugDb.Client/Values/LadybugValue.cs`, `LadybugDb.Client/Values/ValueReader.cs`, `LadybugDb.Client/Interop/LbugLogicalTypeHandle.cs`
- Test: `LadybugDb.Client.IntegrationTests/ScalarValueTests.cs`

**Interfaces:**
- Consumes: `LbugValueHandle`, `LbugStructHandle.Acquire()`, `NativeString`, `LadybugException`.
- Produces: `public enum LadybugType`; `public readonly struct LadybugValue` with `LadybugType Type`, `bool IsNull`, and `AsBoolean()`, `AsInt64()`, `AsInt32()`, `AsInt16()`, `AsSByte()`, `AsUInt64()`, `AsUInt32()`, `AsUInt16()`, `AsByte()`, `AsSingle()`, `AsDouble()`, `AsString()`; `internal static class ValueReader` with `internal static unsafe LadybugValue Read(lbug_value* value)`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/ScalarValueTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ScalarValueTests
{
    [Test]
    public async Task EveryScalarType_RoundTrips()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE S(id INT64, b BOOL, i8 INT8, i16 INT16, i32 INT32, " +
                "u8 UINT8, u16 UINT16, u32 UINT32, u64 UINT64, f FLOAT, d DOUBLE, s STRING, " +
                "PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:S {id: 1, b: true, i8: -8, i16: -16, i32: -32, u8: 8, u16: 16, " +
                "u32: 32, u64: 64, f: 1.5, d: 2.25, s: 'hello'})")) { }

            await using var r = await conn.QueryAsync(
                "MATCH (n:S) RETURN n.b, n.i8, n.i16, n.i32, n.u8, n.u16, n.u32, n.u64, n.f, n.d, n.s");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).AsBoolean()).IsTrue();
            await Assert.That(row.Value.GetValue(1).AsSByte()).IsEqualTo((sbyte)-8);
            await Assert.That(row.Value.GetValue(2).AsInt16()).IsEqualTo((short)-16);
            await Assert.That(row.Value.GetValue(3).AsInt32()).IsEqualTo(-32);
            await Assert.That(row.Value.GetValue(4).AsByte()).IsEqualTo((byte)8);
            await Assert.That(row.Value.GetValue(5).AsUInt16()).IsEqualTo((ushort)16);
            await Assert.That(row.Value.GetValue(6).AsUInt32()).IsEqualTo(32u);
            await Assert.That(row.Value.GetValue(7).AsUInt64()).IsEqualTo(64ul);
            await Assert.That(row.Value.GetValue(8).AsSingle()).IsEqualTo(1.5f);
            await Assert.That(row.Value.GetValue(9).AsDouble()).IsEqualTo(2.25d);
            await Assert.That(row.Value.GetValue(10).AsString()).IsEqualTo("hello");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task TypeTag_ReportsTheDeclaredType()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, s STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1, s: 'x'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN n.id, n.s");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).Type).IsEqualTo(LadybugType.Int64);
            await Assert.That(row.Value.GetValue(1).Type).IsEqualTo(LadybugType.String);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task WrongAccessor_ThrowsInvalidOperationNotGarbage()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE W(id INT64, s STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:W {id: 1, s: 'x'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:W) RETURN n.s");
            var row = await r.ReadRowAsync();

            Assert.Throws<InvalidOperationException>(() => row!.Value.GetValue(0).AsInt64());
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

`TestDatabase` and `ReadRowAsync`/`GetValue` do not exist yet — that is the point of the failing step. Create `TestDatabase` as part of Step 3 (a shared helper replacing the three duplicated cleanup helpers noted in `docs/MILESTONE-2-CARRYOVER.md`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/ScalarValueTests/*"`
Expected: FAIL to compile — `TestDatabase`, `ReadRowAsync`, `LadybugType` do not exist (CS0103/CS1061/CS0246).

- [ ] **Step 3: Implement**

`LadybugDb.Client/Values/LadybugType.cs`:

```csharp
namespace LadybugDb.Client;

/// <summary>The LadybugDB type of a <see cref="LadybugValue"/>.</summary>
public enum LadybugType
{
    /// <summary>A type this client does not model. Use <see cref="LadybugValue.AsString"/> where possible.</summary>
    Unsupported = 0,
    /// <summary>SQL NULL.</summary>
    Null,
    /// <summary>BOOL.</summary>
    Boolean,
    /// <summary>INT8.</summary>
    Int8,
    /// <summary>INT16.</summary>
    Int16,
    /// <summary>INT32.</summary>
    Int32,
    /// <summary>INT64, and SERIAL.</summary>
    Int64,
    /// <summary>UINT8.</summary>
    UInt8,
    /// <summary>UINT16.</summary>
    UInt16,
    /// <summary>UINT32.</summary>
    UInt32,
    /// <summary>UINT64.</summary>
    UInt64,
    /// <summary>FLOAT.</summary>
    Single,
    /// <summary>DOUBLE.</summary>
    Double,
    /// <summary>STRING.</summary>
    String,
    /// <summary>BLOB.</summary>
    Blob,
    /// <summary>DATE.</summary>
    Date,
    /// <summary>TIMESTAMP and its SEC/MS/NS variants.</summary>
    Timestamp,
    /// <summary>TIMESTAMP_TZ.</summary>
    TimestampTz,
    /// <summary>INTERVAL.</summary>
    Interval,
    /// <summary>LIST and ARRAY.</summary>
    List,
    /// <summary>STRUCT.</summary>
    Struct,
    /// <summary>MAP.</summary>
    Map,
    /// <summary>NODE.</summary>
    Node,
    /// <summary>REL.</summary>
    Rel,
    /// <summary>INTERNAL_ID.</summary>
    InternalId,
}
```

`LadybugDb.Client/Interop/LbugLogicalTypeHandle.cs` — follow the exact shape of `LbugValueHandle` in the repo, wrapping `lbug_logical_type` with `lbug_data_type_destroy`. Note `lbug_value_get_data_type` returns `void`, so there is no state to check; adopt unconditionally after the call and document that, matching how `LbugQueryResultHandle.Execute` documents its own unconditional adopt.

`LadybugDb.Client/Values/LadybugValue.cs` — a `public readonly struct` holding a `LadybugType` and the already-marshalled managed payload (`object?`). Do **not** hold a native pointer: the native value is destroyed when its handle scope ends, so `LadybugValue` must own managed data only. Each `As*` accessor checks `Type` and throws `InvalidOperationException` naming both the actual and requested type when it does not match. `AsString()` additionally accepts any type whose payload is already a string.

`LadybugDb.Client/Values/ValueReader.cs` — `Read(lbug_value*)` gets the logical type (through `LbugLogicalTypeHandle`), maps `lbug_data_type_id` to `LadybugType`, then calls the matching `lbug_value_get_*` and boxes the result. Return `LadybugType.Null` when `lbug_value_is_null` reports null — check the generated file for that function's exact name before calling it. Route the `char*` from `lbug_value_get_string` through `NativeString.TakeOwnership`.

`LadybugDb.Client.IntegrationTests/TestDatabase.cs`:

```csharp
namespace LadybugDb.Client.IntegrationTests;

/// <summary>Shared temp-database helpers for integration tests.</summary>
internal static class TestDatabase
{
    internal static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"lbug-test-{Guid.NewGuid():N}");

    /// <summary>
    /// A LadybugDB database is a file plus siblings, not a directory - Directory.Delete
    /// silently no-ops on it and leaves a stale catalog, which makes the next run fail
    /// with "already exists in catalog". Remove both forms.
    /// </summary>
    internal static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
```

Add a temporary `ReadRowAsync` to `LadybugQueryResult` returning `ValueTask<LadybugRow?>`; Task 4 replaces it with full enumeration. `LadybugRow` for now can be a `public readonly struct` wrapping an array of `LadybugValue` with `GetValue(int index)` and `int ColumnCount`.

Then migrate `DatabaseLifecycleTests`, `ValueReadTests`, `QueryResultErrorTests`, and `DisposalSafetyTests` to `TestDatabase.Cleanup` and delete their private copies.

- [ ] **Step 4: Run tests to verify they pass**

Run: `bash scripts/fetch-liblbug.sh && dotnet pack -c Release && dotnet test -c Release`
Expected: PASS. Previously 39, now 42.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/Values LadybugDb.Client/Interop/LbugLogicalTypeHandle.cs \
        LadybugDb.Client/LadybugRow.cs LadybugDb.Client/LadybugQueryResult.cs LadybugDb.Client.IntegrationTests
git commit -m "feat: typed scalar value marshalling"
```

---

### Task 2: Temporal values and blobs

**Files:**
- Modify: `LadybugDb.Client/Values/ValueReader.cs`, `LadybugDb.Client/Values/LadybugValue.cs`
- Test: `LadybugDb.Client.IntegrationTests/TemporalValueTests.cs`

**Interfaces:**
- Consumes: everything from Task 1.
- Produces: `LadybugValue.AsDateOnly()`, `AsDateTime()`, `AsDateTimeOffset()`, `AsTimeSpan()`, `AsBlob()` returning `byte[]`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/TemporalValueTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class TemporalValueTests
{
    [Test]
    public async Task DateTimestampAndInterval_RoundTrip()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE E(id INT64, d DATE, ts TIMESTAMP, iv INTERVAL, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:E {id: 1, d: date('2026-07-29'), " +
                "ts: timestamp('2026-07-29 13:45:30'), iv: interval('3 days')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:E) RETURN n.d, n.ts, n.iv");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).AsDateOnly()).IsEqualTo(new DateOnly(2026, 7, 29));
            await Assert.That(row.Value.GetValue(1).AsDateTime())
                .IsEqualTo(new DateTime(2026, 7, 29, 13, 45, 30, DateTimeKind.Utc));
            await Assert.That(row.Value.GetValue(2).AsTimeSpan()).IsEqualTo(TimeSpan.FromDays(3));
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Blob_RoundTripsExactBytes()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE B(id INT64, data BLOB, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                @"CREATE (n:B {id: 1, data: BLOB('\xDE\xAD\xBE\xEF')})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:B) RETURN n.data");
            var row = await r.ReadRowAsync();

            await Assert.That(row!.Value.GetValue(0).AsBlob())
                .IsEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/TemporalValueTests/*"`
Expected: FAIL — `AsDateOnly` etc. undefined (CS1061).

- [ ] **Step 3: Implement**

Compute conversions **directly from epoch units**. Do not route through `struct tm` — the 12 `*_to_tm`/`*_from_tm` functions are deliberately excluded from the interop because their layout differs across the six target RIDs.

```csharp
// lbug_date_t.days is days since 1970-01-01
DateOnly date = DateOnly.FromDayNumber(new DateOnly(1970, 1, 1).DayNumber + native.days);

// lbug_timestamp_t.value is MICROSECONDS since 1970-01-01T00:00:00Z
DateTime ts = DateTime.UnixEpoch.AddTicks(native.value * (TimeSpan.TicksPerMillisecond / 1000));

// lbug_interval_t is months + days + micros. .NET TimeSpan has no month concept;
// convert months using the LadybugDB convention of 30 days per month and DOCUMENT it
// on AsTimeSpan, since it is lossy for calendar-aware intervals.
TimeSpan interval = TimeSpan.FromDays(native.months * 30 + native.days)
                  + TimeSpan.FromTicks(native.micros * (TimeSpan.TicksPerMillisecond / 1000));
```

Verify the per-variant scale before implementing: `TIMESTAMP_SEC`, `TIMESTAMP_MS`, `TIMESTAMP_NS` use their own getters with different units. Read the header comments in `third-party/lbug.h` for each and state the units you found in your report — if any differs from microseconds, handle it explicitly rather than assuming.

`AsBlob()` copies from the `byte*`/length pair into a managed `byte[]`. Check `third-party/lbug.h` for whether the blob buffer must be freed with `lbug_destroy_blob` — if so, free it exactly once, mirroring `NativeString`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS, 44 tests.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/Values LadybugDb.Client.IntegrationTests/TemporalValueTests.cs
git commit -m "feat: temporal and blob value marshalling from epoch units"
```

---

### Task 3: Container and graph values

**Files:**
- Modify: `LadybugDb.Client/Values/ValueReader.cs`, `LadybugDb.Client/Values/LadybugValue.cs`
- Create: `LadybugDb.Client/Values/LadybugNode.cs`
- Test: `LadybugDb.Client.IntegrationTests/ContainerValueTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: `LadybugValue.AsList()` → `IReadOnlyList<LadybugValue>`; `AsStruct()` → `IReadOnlyDictionary<string, LadybugValue>`; `AsMap()` → `IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>>`; `AsNode()` → `LadybugNode`; `public readonly record struct LadybugInternalId(ulong TableId, ulong Offset)`; `public sealed class LadybugNode` with `LadybugInternalId Id`, `string Label`, `IReadOnlyDictionary<string, LadybugValue> Properties`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/ContainerValueTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ContainerValueTests
{
    [Test]
    public async Task ListAndMap_RoundTrip()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE C(id INT64, tags STRING[], attrs MAP(STRING,STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:C {id: 1, tags: ['a','b','c'], attrs: map(['k1','k2'],['v1','v2'])})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:C) RETURN n.tags, n.attrs");
            var row = await r.ReadRowAsync();

            var list = row!.Value.GetValue(0).AsList();
            await Assert.That(list.Count).IsEqualTo(3);
            await Assert.That(list[1].AsString()).IsEqualTo("b");

            var map = row.Value.GetValue(1).AsMap();
            await Assert.That(map.Count).IsEqualTo(2);
            await Assert.That(map[0].Key.AsString()).IsEqualTo("k1");
            await Assert.That(map[0].Value.AsString()).IsEqualTo("v1");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Node_ExposesIdLabelAndProperties()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE P(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:P {id: 7, name: 'Limbo'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:P) RETURN n");
            var row = await r.ReadRowAsync();

            var node = row!.Value.GetValue(0).AsNode();
            await Assert.That(node.Label).IsEqualTo("P");
            await Assert.That(node.Properties["name"].AsString()).IsEqualTo("Limbo");
            await Assert.That(node.Properties["id"].AsInt64()).IsEqualTo(7L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/ContainerValueTests/*"`
Expected: FAIL — `AsList`/`AsMap`/`AsNode` undefined (CS1061).

- [ ] **Step 3: Implement**

Containers recurse through `ValueReader.Read`. **Every `lbug_value` produced by `get_list_element` / `get_struct_field_value` / `get_map_key` / `get_map_value` is a new value the caller owns** — wrap each in an `LbugValueHandle` inside a `using` so it is destroyed exactly once, then read it recursively. A missed destroy here multiplies per element and will show up in the Task 7 leak test.

Node and rel property access uses the `lbug_node_val_*` / `lbug_rel_val_*` entry points — grep the generated file for their exact names and signatures (e.g. `lbug_node_val_get_property_name_at`, `lbug_node_val_get_property_value_at`, `lbug_node_val_get_property_size`, `lbug_node_val_get_label_val`, `lbug_node_val_get_id_val`) and use what is actually there. Every `char*` returned goes through `NativeString`.

Guard recursion depth. A deeply nested list is user data, and unbounded recursion on user data is a stack-overflow vector — cap it (e.g. 64 levels) and throw `LadybugException` past the cap rather than crashing.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS, 46 tests.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/Values LadybugDb.Client.IntegrationTests/ContainerValueTests.cs
git commit -m "feat: list, struct, map and node value marshalling"
```

---

### Task 4: Row enumeration and multi-result scripts

**Files:**
- Modify: `LadybugDb.Client/LadybugQueryResult.cs`, `LadybugDb.Client/LadybugRow.cs`
- Test: `LadybugDb.Client.IntegrationTests/EnumerationTests.cs`
- Modify: `LadybugDb.Client.IntegrationTests/ValueReadTests.cs` (retire `ReadStringAsync` usage)

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `LadybugQueryResult : IAsyncEnumerable<LadybugRow>` with `IAsyncEnumerator<LadybugRow> GetAsyncEnumerator(CancellationToken)`; `ValueTask<LadybugQueryResult?> NextResultAsync(CancellationToken)`; `LadybugRow` with `int ColumnCount`, `LadybugValue GetValue(int)`, `LadybugValue this[string columnName]`, `string GetColumnName(int)`. **`ReadStringAsync` and the temporary `ReadRowAsync` are removed.**

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/EnumerationTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class EnumerationTests
{
    [Test]
    public async Task AwaitForeach_YieldsEveryRowInOrder()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE N(id INT64, PRIMARY KEY(id))")) { }
            for (var i = 0; i < 5; i++)
                await using (var _ = await conn.QueryAsync($"CREATE (n:N {{id: {i}}})")) { }

            var seen = new List<long>();
            await using var r = await conn.QueryAsync("MATCH (n:N) RETURN n.id ORDER BY n.id");
            await foreach (var row in r)
                seen.Add(row.GetValue(0).AsInt64());

            await Assert.That(seen).IsEquivalentTo(new List<long> { 0, 1, 2, 3, 4 });
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task ColumnsAreAddressableByName()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE M(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:M {id: 1, name: 'x'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:M) RETURN n.id AS ident, n.name AS label");
            await foreach (var row in r)
            {
                await Assert.That(row.GetColumnName(0)).IsEqualTo("ident");
                await Assert.That(row["label"].AsString()).IsEqualTo("x");
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Cancellation_StopsEnumeration()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Q(id INT64, PRIMARY KEY(id))")) { }
            for (var i = 0; i < 50; i++)
                await using (var _ = await conn.QueryAsync($"CREATE (n:Q {{id: {i}}})")) { }

            using var cts = new CancellationTokenSource();
            var count = 0;
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await using var r = await conn.QueryAsync("MATCH (n:Q) RETURN n.id");
                await foreach (var row in r.WithCancellation(cts.Token))
                {
                    if (++count == 5) cts.Cancel();
                }
            });
            await Assert.That(count).IsEqualTo(5);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/EnumerationTests/*"`
Expected: FAIL — `LadybugQueryResult` is not enumerable (CS1061 on `GetAsyncEnumerator`).

- [ ] **Step 3: Implement**

Implement `GetAsyncEnumerator` over `lbug_query_result_has_next` / `lbug_query_result_get_next`. The enumerator must honour the `CancellationToken` between rows.

Column names come from `lbug_query_result_get_column_name` (a `char*` — route through `NativeString`) and `lbug_query_result_get_num_columns`. Read the names **once** when the result is created, not per row.

`NextResultAsync` wraps `lbug_query_result_has_next_query_result` / `lbug_query_result_get_next_query_result`, returning a new `LadybugQueryResult` or `null`. Be explicit about ownership: whether the returned result is a child that dies with its parent or an independent handle determines whether disposing the parent invalidates it. Test both orders, and document what you find.

Delete `ReadStringAsync` and the temporary `ReadRowAsync`. Rewrite `ValueReadTests` to use enumeration. Update `docs/USAGE.md` — it documents `ReadStringAsync`, which no longer exists.

**Guard against the parent-disposal crash class.** The foundation's final review found that using a child after its parent database was disposed segfaulted the process; the fix leases the database handle around native calls. The enumerator makes native calls too — make sure it takes the same lease, and add a test that disposes the database mid-enumeration and asserts `ObjectDisposedException` rather than a crash.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS, 49 tests.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client LadybugDb.Client.IntegrationTests docs/USAGE.md
git commit -m "feat: enumerate rows via IAsyncEnumerable, drop the ReadStringAsync seam"
```

---

### Task 5: Prepared statements and all 20 binds

**Files:**
- Create: `LadybugDb.Client/LadybugPreparedStatement.cs`, `LadybugDb.Client/Interop/LbugPreparedStatementHandle.cs`
- Modify: `LadybugDb.Client/LadybugConnection.cs`
- Test: `LadybugDb.Client.IntegrationTests/PreparedStatementTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces: `LadybugConnection.PrepareAsync(string cypher, CancellationToken)` → `ValueTask<LadybugPreparedStatement>`; `LadybugPreparedStatement : IAsyncDisposable` with `Bind(string name, bool)`, `(string, sbyte)`, `(string, short)`, `(string, int)`, `(string, long)`, `(string, byte)`, `(string, ushort)`, `(string, uint)`, `(string, ulong)`, `(string, float)`, `(string, double)`, `(string, string)`, `(string, DateOnly)`, `(string, TimeSpan)`, `(string, DateTime)`, `(string, DateTimeOffset)`, plus `BindTimestampSeconds`, `BindTimestampMilliseconds`, `BindTimestampNanoseconds`, `BindNull`; and `ValueTask<LadybugQueryResult> ExecuteAsync(CancellationToken)`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/PreparedStatementTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class PreparedStatementTests
{
    [Test]
    public async Task EveryIntegerWidth_BindsAtItsExactBoundary()
    {
        // A mis-sized integer marshal corrupts data silently rather than throwing,
        // so every width is bound at its documented extreme and read back.
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE I(id INT64, i8 INT8, i16 INT16, i32 INT32, i64 INT64, " +
                "u8 UINT8, u16 UINT16, u32 UINT32, u64 UINT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync(
                "CREATE (n:I {id: 1, i8: $i8, i16: $i16, i32: $i32, i64: $i64, " +
                "u8: $u8, u16: $u16, u32: $u32, u64: $u64})");
            stmt.Bind("i8", sbyte.MinValue);
            stmt.Bind("i16", short.MinValue);
            stmt.Bind("i32", int.MinValue);
            stmt.Bind("i64", long.MinValue);
            stmt.Bind("u8", byte.MaxValue);
            stmt.Bind("u16", ushort.MaxValue);
            stmt.Bind("u32", uint.MaxValue);
            stmt.Bind("u64", ulong.MaxValue);
            await using (var _ = await stmt.ExecuteAsync()) { }

            await using var r = await conn.QueryAsync(
                "MATCH (n:I) RETURN n.i8, n.i16, n.i32, n.i64, n.u8, n.u16, n.u32, n.u64");
            await foreach (var row in r)
            {
                await Assert.That(row.GetValue(0).AsSByte()).IsEqualTo(sbyte.MinValue);
                await Assert.That(row.GetValue(1).AsInt16()).IsEqualTo(short.MinValue);
                await Assert.That(row.GetValue(2).AsInt32()).IsEqualTo(int.MinValue);
                await Assert.That(row.GetValue(3).AsInt64()).IsEqualTo(long.MinValue);
                await Assert.That(row.GetValue(4).AsByte()).IsEqualTo(byte.MaxValue);
                await Assert.That(row.GetValue(5).AsUInt16()).IsEqualTo(ushort.MaxValue);
                await Assert.That(row.GetValue(6).AsUInt32()).IsEqualTo(uint.MaxValue);
                await Assert.That(row.GetValue(7).AsUInt64()).IsEqualTo(ulong.MaxValue);
            }
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task PreparedStatement_IsReusableAcrossExecutions()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE R(id INT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:R {id: $id})");
            for (var i = 0; i < 3; i++)
            {
                stmt.Bind("id", (long)i);
                await using var _ = await stmt.ExecuteAsync();
            }

            await using var r = await conn.QueryAsync("MATCH (n:R) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(3L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task BindingUnknownParameter_ThrowsLadybugException()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE U(id INT64, PRIMARY KEY(id))")) { }

            await using var stmt = await conn.PrepareAsync("CREATE (n:U {id: $id})");
            Assert.Throws<LadybugException>(() => stmt.Bind("nosuchparam", 1L));
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/PreparedStatementTests/*"`
Expected: FAIL — `PrepareAsync` undefined (CS1061).

- [ ] **Step 3: Implement**

`LbugPreparedStatementHandle` follows the established factory shape, over `lbug_connection_prepare` and `lbug_prepared_statement_destroy`. Note `lbug_prepared_statement` has **two** pointer fields (`_prepared_statement`, `_bound_values`) — the struct size must come from `sizeof(lbug_prepared_statement)`, not a hardcoded pointer size.

After preparing, check `lbug_prepared_statement_is_success`; on failure pull the message via `lbug_prepared_statement_get_error_message` through `NativeString` and throw `LadybugException` carrying the Cypher.

Each `Bind` overload marshals the parameter name to UTF-8, calls the matching native bind, and throws `LadybugException` on `LbugError`. `bind_bool` takes a `byte`, not a `bool` — convert explicitly. Temporal binds build the epoch-unit structs, inverting Task 2's conversions.

`ExecuteAsync` calls `lbug_connection_execute` and returns a `LadybugQueryResult`, reusing the same ownership pattern as `LadybugConnection.QueryAsync`. It must take the database lease like every other native call path.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS, 52 tests.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client LadybugDb.Client.IntegrationTests/PreparedStatementTests.cs
git commit -m "feat: prepared statements with all 20 typed binds"
```

---

### Task 6: Transactions, the write lock, and the multi-writes question

**Files:**
- Create: `LadybugDb.Client/LadybugTransaction.cs`
- Modify: `LadybugDb.Client/LadybugConnection.cs`, `LadybugDb.Client/LadybugDatabase.cs`, `LadybugDb.Client/LadybugConfig.cs`
- Test: `LadybugDb.Client.IntegrationTests/TransactionTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces: `LadybugConnection.BeginTransactionAsync(CancellationToken)` → `ValueTask<LadybugTransaction>`; `LadybugTransaction : IAsyncDisposable` with `CommitAsync()`, `RollbackAsync()`, `bool IsCompleted`; `LadybugConfig.EnableMultiWrites { get; init; }`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/TransactionTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class TransactionTests
{
    [Test]
    public async Task Commit_PersistsWork()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using (var _ = await conn.QueryAsync("CREATE (n:T {id: 1})")) { }
                await tx.CommitAsync();
            }

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(1L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DisposeWithoutCommit_RollsBack()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using var _ = await conn.QueryAsync("CREATE (n:T {id: 1})");
                // no CommitAsync - dispose must roll back
            }

            await using var r = await conn.QueryAsync("MATCH (n:T) RETURN count(n)");
            await foreach (var row in r)
                await Assert.That(row.GetValue(0).AsInt64()).IsEqualTo(0L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task DoubleCommit_ThrowsInvalidOperation()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE T(id INT64, PRIMARY KEY(id))")) { }

            await using var tx = await conn.BeginTransactionAsync();
            await tx.CommitAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync());
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/TransactionTests/*"`
Expected: FAIL — `BeginTransactionAsync` undefined (CS1061).

- [ ] **Step 3: Implement**

The C API has **no** transaction functions — `BeginTransactionAsync` issues `BEGIN TRANSACTION`, `CommitAsync` issues `COMMIT`, `RollbackAsync` issues `ROLLBACK`, all as ordinary Cypher. Document this on the public type so nobody mistakes it for a native primitive.

`DisposeAsync` rolls back when neither commit nor rollback has run. It must not throw from dispose — if the rollback itself fails (e.g. the connection is already gone) swallow it, exactly as `ReleaseHandle` does, and document why.

**Answer the multi-writes question empirically, and let the answer drive the design.** Add `LadybugConfig.EnableMultiWrites` mapping to `lbug_system_config.enable_multi_writes`. Then write a scratch experiment (not a committed test — it is a measurement, not an assertion): open a database with the flag on, run concurrent writers from two connections, and record whether the engine still raises `Cannot start a new write transaction in the system`.

- **If the flag lifts the constraint:** do not wire `LadybugDatabase.WriteLock`. Delete it, and document that concurrency is the engine's business. Say so in your report.
- **If it does not:** wire `WriteLock` so `BeginTransactionAsync` serializes writers, and document the behaviour change. `LadybugWriteConflictException` stays reachable for the un-transacted path.

Either way, report the measurement with real output. This has been an open question since the foundation benchmark and this task closes it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS, 55 tests.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client LadybugDb.Client.IntegrationTests/TransactionTests.cs
git commit -m "feat: Cypher-driven transactions, and settle the multi-writes question"
```

---

### Task 7: Leak coverage, documentation, and carry-over cleanup

**Files:**
- Modify: `LadybugDb.Client.IntegrationTests/LeakTests.cs`
- Modify: `docs/USAGE.md`, `README.md`, `docs/MILESTONE-2-CARRYOVER.md`
- Create: `.gitattributes`

**Interfaces:**
- Consumes: Tasks 1–6.
- Produces: no new public API.

- [ ] **Step 1: Write the failing test**

Extend `LeakTests.cs` with a case covering the new marshalling paths — the ones that allocate most per call:

```csharp
    /// <summary>
    /// Container marshalling allocates a native value per element, and every string goes
    /// through lbug_destroy_string. A missed destroy multiplies per element, so this
    /// exercises lists, maps, structs and nodes together rather than scalars.
    /// </summary>
    [Test]
    public async Task RepeatedContainerReads_DoNotGrowProcessMemory()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE L(id INT64, tags STRING[], attrs MAP(STRING,STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:L {id: 1, tags: ['a','b','c','d','e'], " +
                "attrs: map(['k1','k2','k3'],['v1','v2','v3'])})")) { }

            for (var i = 0; i < 300; i++)
            {
                await using var warm = await conn.QueryAsync("MATCH (n:L) RETURN n, n.tags, n.attrs");
                await foreach (var row in warm) { _ = row.GetValue(0).AsNode(); _ = row.GetValue(1).AsList(); _ = row.GetValue(2).AsMap(); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var baseline = Environment.WorkingSet;

            for (var i = 0; i < 3_000; i++)
            {
                await using var r = await conn.QueryAsync("MATCH (n:L) RETURN n, n.tags, n.attrs");
                await foreach (var row in r) { _ = row.GetValue(0).AsNode(); _ = row.GetValue(1).AsList(); _ = row.GetValue(2).AsMap(); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var growthMb = (Environment.WorkingSet - baseline) / 1024.0 / 1024.0;
            await Assert.That(growthMb).IsLessThan(32);
        }
        finally { TestDatabase.Cleanup(path); }
    }
```

- [ ] **Step 2: Run test to verify it fails or passes honestly**

Run: `dotnet test LadybugDb.Client.IntegrationTests -- --treenode-filter "/*/*/LeakTests/*"`

If it fails, you have a real leak in the container paths — find the missing destroy. **Do not raise the 32 MB bound**; that bound is a standing ruling from the project owner, and a leak test with a meaningless threshold is worse than none. If it proves flaky rather than failing, quarantine it explicitly and say so.

- [ ] **Step 3: Close out the carry-over items**

From `docs/MILESTONE-2-CARRYOVER.md`, these are now actionable:

- `ReadStringAsync`'s untested failure branches — gone with the method itself (Task 4).
- The duplicated test cleanup helper — replaced by `TestDatabase` (Task 1).
- `HandleTests` doc comment overstating coverage — trim it to what it actually verifies.
- Add `.gitattributes` marking the generated interop so GitHub collapses it in diffs:

```
LadybugDb.Client/Native/LbugNative.g.cs linguist-generated=true
```

Rewrite `docs/MILESTONE-2-CARRYOVER.md` to list only what genuinely remains, or delete it if nothing does.

- [ ] **Step 4: Update the documentation to match the shipped API**

`docs/USAGE.md` documents `ReadStringAsync`, which no longer exists. Rewrite the reading section around `await foreach` and typed accessors, and add sections for prepared statements and transactions. `README.md`'s quick-start sample must be updated too.

**Compile and run every code sample.** Create a scratch console project, paste each sample verbatim, build it, run it, confirm the output matches what the docs claim, then delete the scratch project. Say in your report that you did this. Two defects in this project have come from samples that were never executed.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet pack -c Release && dotnet test -c Release`
Expected: PASS, 56 tests.

```bash
git add LadybugDb.Client.IntegrationTests docs README.md .gitattributes
git commit -m "test: leak coverage for container marshalling; refresh docs for the M2 API"
```

---

## What this milestone completes

Every LadybugDB type marshals to a .NET type, all 20 binds work, results enumerate, and transactions are available. The temporary `ReadStringAsync` seam is gone. Combined with the foundation, `LadybugDb.Client` is a usable general-purpose client.

## Explicitly still out of scope

- Apache Arrow interop (`lbug_query_result_get_arrow_schema`, `get_next_arrow_chunk`).
- `INT128` and `DECIMAL` beyond `lbug_value_get_decimal_as_string`. .NET has no native Int128 mapping in this client yet; expose the string form and document it.
- Extension/registry management and CLI wrapping.
- ADO.NET `DbProviderFactory` conformance.
- Genuine async offloading — signatures stay async-shaped and synchronous, per the design's Decision 2.
