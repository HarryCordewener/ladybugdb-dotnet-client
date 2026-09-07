# Research: LadybugDB C API cost model and what the official bindings do

Researched 2026-09-06 against a shallow clone of `LadybugDB/ladybug` at commit `28320ef2` (v0.20.2)
and clones of the official Python, Rust, Java, Node, Go and .NET bindings. File references are
relative to the upstream repository. "Verified" means read in source; "inferred" is marked.

## 1. Engine status and C API surface

- Current release v0.20.2 (2026-09-02). Kùzu Inc. was acquired by Apple in October 2025 and the
  `kuzudb/kuzu` repository archived; LadybugDB is the community continuation (first post-Kuzu
  release v0.12.0, November 2025). Cadence: roughly one minor per month with one to three patches
  each; 24 tagged releases in ten months.
- `src/include/c_api/lbug.h` exports 179 functions. Versus v0.18.0 the only signature-level change is
  the addition of `lbug_connection_get_pushed_sql` (0.19.0). Behavioural changes: 0.20.0 added an
  exception floor so no C++ exception crosses the C boundary; 0.20.1 and 0.20.2 fixed a SIGSEGV and
  stale rows when re-executing parameterized statements on the cached-physical-plan fast path.
  Prepared-statement benchmarks should run against 0.20.2 or later.
- Present: Arrow schema and chunk export, per-connection thread cap, `lbug_connection_interrupt`,
  `lbug_connection_set_query_timeout`, `bind_*` for scalars/dates/timestamps/interval/string and
  `bind_value` for everything else, `lbug_value_get_*` for every type, `lbug_flat_tuple_get_value`.
  Absent: `queryAsArrow` (the native columnar result path is C++-only), prepare-time parameters, and
  any catalog function.
- Batch inserts beyond `COPY FROM`: `lbug_connection_create_arrow_table` and relatives (memory-backed
  tables over Arrow buffers, ownership transferred); `UNWIND $rows AS r CREATE (...)` with a list
  parameter (what upstream's own write benchmark does); `COPY FROM` a subquery; `LOAD FROM`.
- `lbug_system_config` fields: buffer pool size, max threads, compression, read-only, max db size,
  `auto_checkpoint`, `checkpoint_threshold`, `throw_on_wal_replay_failure`, `enable_checksums`,
  `enable_multi_writes`. The C `bool` fields are one byte. This client exposes six of these;
  `auto_checkpoint`, `checkpoint_threshold` and `enable_checksums` are not surfaced.

## 2. How the official bindings read results

| Binding | Built on | Row path | Strings | Arrow |
|---|---|---|---|---|
| Python (pybind11) | C++ API | `get_next` converts each column of the reused `FlatTuple` to a Python object; `get_as_df` preallocates numpy columns and still iterates tuples | one copy | `get_as_arrow` builds row batches from tuple iteration unless the result is natively Arrow |
| Rust (cxx) | C++ API | iterator yields `Vec<Value>`; `flat_tuple_get_value` returns a reference | one copy (`&std::string` to `String`) | feature-gated chunk iterator |
| Java (JNI) | C API | `getNext` then `FlatTuple.getValue(i)` into a `Value` wrapper; big switch on type id | malloc copy then `NewStringUTF` copy | none |
| Node (N-API) | C++ API | `getNext()` runs an `AsyncWorker` per row; `getAll()` is a JS loop of awaited `getNext()` | two copies | none |
| Go (cgo) | C API | `lbug_query_result_get_next`, `lbug_flat_tuple_get_value`, `C.GoString` | two copies plus cgo | table creation only |
| .NET official (`LadybugDB` 0.19.1) | C API, `[LibraryImport]` | `Rows()` allocates a `FlatTuple` object per row, an `object?[]` per row, a `Value` object per cell and boxes every scalar | `PtrToStringUTF8` then `lbug_destroy_string` | none |

Among C-API consumers the floor is identical; they differ only in wrapper allocation discipline. The
official .NET binding is the most allocation-heavy of the three C-API bindings. Upstream publishes no
comparison of Arrow versus tuple reads in any language.

## 3. Value access cost model (verified in `src/c_api` and `src/main`)

- **The flat tuple is reused.** `QueryResult` owns one `shared_ptr<FlatTuple>`; `getNext()` overwrites
  it. The C wrapper stores the raw pointer with `_is_owned_by_cpp = true`, so the pointer is stable
  across calls and `lbug_flat_tuple_destroy` is a no-op for it.
- **`get_next` engine cost.** `FactorizedTableIterator::getNext` copies every column into the tuple's
  `Value`s. Scalars are a union assignment. STRING and BLOB construct a `std::string` (a heap
  allocation beyond libstdc++'s 15-byte small-string buffer) before the caller asks for them. UUID is
  formatted to a string on every row. LIST and STRUCT children retain vector capacity across rows.
- **`lbug_flat_tuple_get_value`** allocates nothing: it writes `&values[idx]` into the caller's
  16-byte `lbug_value` struct with `_is_owned_by_cpp = true`. `lbug_value_destroy` on it is a no-op.
  The struct can therefore live on the stack.
- **Typed scalar getters** are a type check plus a union read; zero allocation.
- **`lbug_value_get_string`** returns `getValue<std::string>()` by value (copy one), then
  `convertToOwnedCString` mallocs and memcpys (copy two); the caller frees with
  `lbug_destroy_string`. Per string read: two copies, one malloc, one free, plus the managed decode.
  `get_blob`, `get_uuid` (which formats again), `get_decimal_as_string` and `lbug_value_to_string`
  follow the same pattern. There is no zero-copy accessor for variable-length data in the C API.
- **`lbug_value_get_data_type` allocates** a new `LogicalType` on every call; so do
  `lbug_query_result_get_column_name` and `get_column_data_type`. Fetch these once per result.
- **Arrow chunks from the C API** are built by iterating the same tuple path inside the engine
  (`MaterializedQueryResult::getNextArrowChunk` calls `getNext` and appends to an `ArrowRowBatch`).
  Arrow from C trades `2N+1` foreign calls per row for one call per chunk plus the import cost and
  gives one contiguous UTF-8 buffer per string column; engine-side row materialization is unchanged.
- Errors: a thread-local last-error string retrieved via `lbug_get_last_error` (malloc'd).

## 4. Threading, writers, checkpoints

- A connection is thread-safe by way of a single mutex in `ClientContext` taken by `query`,
  `prepare`, `execute`, `setMaxNumThreadForExec` and `setQueryTimeOut`: one statement in flight per
  connection, fully serialized. `interrupt()` flips an atomic and is safe from another thread.
  `QueryResult` has no locking. Docs: one read-write `Database` object per process; one connection per
  thread.
- `set_max_num_thread_for_exec` caps the worker threads one connection's queries may use; zero
  resets to the database-level maximum.
- Single writer: with `enable_multi_writes` false (the default), `beginTransaction(WRITE)` waits only
  while an existing writer is in its commit phase and otherwise throws "Cannot start a new write
  transaction in the system. Only one write transaction at a time is allowed". Reads never wait.
  `enable_multi_writes` also exists as the runtime setting `CALL debug_enable_multi_writes=true`; the
  docs do not mention it and the name says debug. Upstream's own `node_write_bench.cpp` enables it
  together with `autoCheckpoint=false` and `enable_default_hash_index=false`.
- Commit: each write commit appends its local WAL to the global WAL and waits for fsync, with group
  commit (0.18.0). In-memory databases skip the WAL. After commit, if `auto_checkpoint` is on and the
  WAL exceeds `checkpoint_threshold` (default 16 MiB) a checkpoint runs; it blocks new writers and
  drains active ones for its duration. `COPY FROM` forces a checkpoint when auto-checkpoint is on.
- Guidance: the README positions the engine for analytical workloads; `COPY FROM` is "the fastest way
  to bulk insert"; `CREATE`/`MERGE` are for "small additions or updates on a sporadic basis". No
  per-transaction cost figures are documented.

## 5. Prepared statements

- `lbug_connection_prepare` parses, binds and optimizes with no parameters and caches the logical plan.
- `lbug_connection_execute` copies every bound value, then reuses the cached physical plan when every
  parameter's type equals its prepare-time type and no parameter is unknown; on a type mismatch it
  re-plans from the parsed statement on every execute. Because the C API cannot pass prepare-time
  parameters, whether the fast path engages from the second execution needs measuring via
  `lbug_query_summary_get_compiling_time`.
- Each `bind_*` is a `make_unique<Value>` plus a map erase/insert keyed by a `std::string`; strings
  are copied at bind and again at execute.
- `lbug_connection_query` parses, binds, optimizes, maps and executes every call with no plan cache.

## 6. Point lookups

- The default primary-key index is a hash index (local-storage lookup first inside a write
  transaction); an ART index is optional; 0.18.0 added secondary ART indexes.
- 0.19.0 added a row-driven primary-key lookup operator that fires when the key depends on
  in-scope expressions (`UNWIND $ids AS id MATCH (n:T {pk: id})`); a bare `MATCH (n:T {pk: $x})` takes
  the older scan-with-predicate path (inferred; confirm with `EXPLAIN`).
- Upstream publishes no point-lookup or single-row-write numbers. The only write numbers
  (`ladybug-write-benchmark`, January 2026, Python, `UNWIND` batches of 1000): node `CREATE` 22.9K
  rows/s versus `COPY` Parquet 56.5K rows/s; relationship `CREATE` 1.7K rows/s versus `COPY` 22K rows/s.

## 7. Existing benchmark suites

In-tree `tools/benchmark/` (runner, `node_write_bench.cpp`), the `LadybugDB/benchmarks` submodule (LDBC
SNB sf10/sf100, graph500, LSQB, a ClickBench-style suite), `ladybug-write-benchmark`,
`kuzu-ladybug-benchmark`, prrao87's Kuzu-versus-Neo4j studies, the CIDR 2023 Kùzu paper. No OLTP-style
comparison against SQLite or DuckDB exists anywhere; the workload harness in this repository's
`benchmarks/` directory is new information.

## Implications for the .NET client

Likely to matter: per-row allocation discipline (stack-allocated `lbug_value`, no per-cell handles,
no per-cell type lookup, no boxing), string handling (one managed decode, intern repeated labels),
foreign-call count (`[SuppressGCTransition]` on trivial getters), the Arrow chunk crossover point on
wide or string-heavy scans, prepared-statement reuse with stable parameter types, write batching and
transaction scope (one fsync per auto-commit), connection-per-thread with client-side writer
serialization or retry, and separating engine time (`lbug_query_summary_*`) from client time.

Unlikely to matter: thread-count settings for point lookups, `has_next` overhead, interrupt and
timeout plumbing.

## Sources

Upstream source (`CMakeLists.txt`, `src/include/c_api/lbug.h`, `src/c_api/*.cpp`,
`src/include/main/*.h`, `src/main/*.cpp`, `src/processor/result/*.cpp`, `src/transaction/*.cpp`,
`src/storage/wal/wal.cpp`, `src/storage/checkpointer.cpp`, `src/planner/plan/plan_subquery.cpp`,
`tools/benchmark/node_write_bench.cpp`); the upstream releases API; docs.ladybugdb.com (C API,
concurrency, transactions, import); blog.ladybugdb.com ("Ladybug Flying Solo", "Better Graph Database
Ball"); The Register on the Kuzu shutdown; dbdb.io; the Python, Rust, Java, Node, Go and .NET binding
repositories; `ladybug-write-benchmark/result.txt`; `kuzu-ladybug-benchmark/benchmark.py`;
prrao87/kuzudb-study and graph-benchmark; the CIDR 2023 paper; kuzudb issue #2529 and discussion #5865.
