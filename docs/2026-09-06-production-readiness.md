# Production-readiness review, benchmarks, and LINQ direction

Date: 2026-09-06. Reviewed: branch `feat/api-ergonomics` at `de3210f` (main `27a3e1c` plus the
parameter-object, `Select<T>`, `ExecuteAsync` and transaction-guard work), against engine v0.18.3.
Host: Intel Core Ultra 7 265F, 20 cores, NVMe, CachyOS, .NET 10.0.8.

**Addendum, same day:** after the review, the repository stopped redistributing engine binaries and
now takes them from upstream's `LadybugDB.Native` packages, with the interop regenerated against
engine v0.19.1 and a version guard at database open. The "Packaging" section below describes the
state the review found; the addendum at its end describes what changed. The benchmark numbers are
from 0.18.3 except where a 0.19.1 re-run is quoted.

Companion documents written the same day:

- [`research/2026-09-06-dotnet-library-standards.md`](research/2026-09-06-dotnet-library-standards.md) — what outside users expect from a .NET native-wrapper client, with the 1.0 checklist.
- [`research/2026-09-06-engine-c-api-cost-model.md`](research/2026-09-06-engine-c-api-cost-model.md) — what every C API call costs, verified in upstream source.
- [`research/2026-09-06-linq-over-graph-databases.md`](research/2026-09-06-linq-over-graph-databases.md) — the survey behind the LINQ design.
- [`superpowers/specs/2026-09-06-linq-query-surface-design.md`](superpowers/specs/2026-09-06-linq-query-surface-design.md) — the proposed LINQ surface.
- [`../benchmarks/`](../benchmarks/) — the .NET workload results (`results-dotnet.json`), the Python control, and the BenchmarkDotNet tables.

## Verdict

**Addendum, same day, after the roadmap was executed:** items 3 through 5 and 8 of the roadmap
below are done (the read path, `IDisposable`, the checkpoint settings, the statement cache,
cancellation, telemetry, package hygiene, and the Extensions package), and the LINQ surface
landed as a separate set of commits; the checklist table is updated in place and the changelog
carries the details. What still stands between this and a 1.0 is now only time in production and
an engine pin that can move once upstream publishes a native package for 0.20.x.

**Ready for SharpMUSH to build against on a branch. Not ready to publish as a general-purpose
package.** The core is sound: the lifetime model, the type coverage and the transaction guard are
better than either other .NET binding, the test suite is 384 tests against the real engine, and
every measured latency sits far inside the thresholds SharpMUSH pre-registered. What stands between
this and a trustworthy public 1.0 is not correctness but currency and packaging: the engine pin is
two minor versions behind an upstream that has since fixed prepared-statement crashes, an official
upstream .NET binding now exists and the README said otherwise, and the package lacks the
validation, API-tracking and observability plumbing outside consumers look for. The concrete list is
in "Roadmap" at the end.

Two library defects were found and fixed during the review (both in the write-conflict path), one
usability gap was closed (unaliased columns now project into records), and one per-query allocation
was removed. Each has a test.

## Method

- Read every source file in `LadybugDb.Client` and the mapping layer; read the interop handles
  against upstream `src/c_api` to check the ownership assumptions the code documents.
- Built a BenchmarkDotNet project (`LadybugDb.Client.Benchmarks`) covering the read path, point
  lookups, writes, traversals, the transaction classifier and the engine thread cap, plus two
  prototypes (a stack-allocated per-tuple reader and an Arrow-chunk reader) to measure the headroom
  the current interop discipline leaves.
- Ported the Python workload harness (`benchmarks/workload_bench.py`) to .NET one-for-one
  (`--workload` mode of the same project) so the pre-registered SharpMUSH thresholds are judged on
  this client, and re-ran the Python harness in the same session on the same engine version as the
  control. Every harness was smoke-run and its failure signatures inspected before its numbers were
  trusted: that inspection is what surfaced both defects below.
- Ran three research passes (library standards, engine cost model, LINQ providers) with sources
  recorded in `docs/research/`.

## Defects found and fixed

| # | Defect | Evidence | Fix | Test |
|---|---|---|---|---|
| 1 | `PrepareAsync` of a write statement can fail with the single-writer conflict and threw a plain `LadybugException`, so a retry loop written to the documented contract died. Preparing a write statement contends for the writer slot exactly as executing one does. | Every writer task in the workload's concurrent section was lost at start-up with "Cannot start a new write transaction" as a plain exception. | `LadybugPreparedStatement.Prepare` now routes through `QueryFailureClassifier`. | `DatabaseLifecycleTests.ConcurrentPrepareOfWriteStatement_ThrowsLadybugWriteConflictException` (real engine) |
| 2 | Under `EnableMultiWrites` the engine reports a collision as "Runtime exception: Write-write conflict of updating the same row.", which contains no "write transaction" and was classified as fatal. `docs/USAGE.md` claimed zero conflicts under multi-writes; the conflicts were being thrown as a different type. | 83 such conflicts in 3 s at 8 writers on 1,000 objects once classified. | Classifier also matches "write-write conflict". | `QueryFailureClassifierTests.MultiWritesRowConflictMessage_ClassifiesAsWriteConflict` |
| 3 | `Select<T>` rejected the most natural query shape, `RETURN o.dbref, o.name`, because the engine names those columns `o.dbref`/`o.name`; every projection needed an `AS` per column. | The `SelectRecord` benchmark failed on first run. | `RowMapper` also matches a constructor parameter against the text after a column name's last dot; an exact alias always wins; duplicates resolve leftmost as before. | Four `RowMapperTests` cases and `SelectTests.Select_MatchesUnaliasedColumnsByTheNameAfterTheDot` (real engine) |
| 4 | `TransactionStatement.Classify` normalized every statement through a `StringBuilder` on every `QueryAsync`: 118 ns and 528 B per ordinary `MATCH`. | `ClassifierBenchmarks` | A keyword-prefix check returns `None` with no allocation unless the statement starts with `BEGIN`, `COMMIT` or `ROLLBACK`; measured after: 1.1 ns and 0 B. | Existing `TransactionStatementTests` (mixed-case, leading whitespace, statements containing the keywords) |

## Readiness by area

### Correctness and native lifetime — strong

The refcounted parent/child model (`LbugStructHandle.AcquireParentHolds`, release after own
destroy) is the right pattern and is what the runtime team recommends; the four process-killing
defects it closed are each pinned by a crash-reproducing subprocess test. The ownership claims in
the value reader match upstream source: `lbug_flat_tuple_get_value` and `get_next` hand back
C++-owned borrows, list/struct/map/node/rel getters hand back caller-owned values, strings and blobs
are caller-freed. `Int128` never crosses the boundary. The single-pass guard on results and the
forward-only guard on `NextResultAsync` close two silent-corruption paths the C API leaves open.

Gaps: none blocking. The `struct tm` exclusions are permanent and documented.

### Concurrency and transactions — strong, with one measured recommendation

The transaction gate, the nested-`BEGIN` refusal and the raw `COMMIT` tracking are correct and
tested, including the `SynchronizationContext` deadlock case. Two things the benchmarks add:

- With the engine default (single writer) the conflict rate under eight writers is not "some
  retries"; it is 280,000–360,000 refused attempts per three seconds, and throughput falls with
  every writer added. With `EnableMultiWrites` the edge model scales from 7,000 to 22,000
  mutations/s at eight writers on 100,000 objects with five conflicts. `LadybugConfig` exposes the
  flag; the docs section on it is corrected below.
- The retry pattern in `docs/USAGE.md` is now actually reachable for both conflict wordings and for
  `PrepareAsync` (defects 1 and 2).

Gap: the engine's `auto_checkpoint`, `checkpoint_threshold` and `enable_checksums` are not
surfaced by `LadybugConfig`. The 100,000-object edge database grew from 102 MB to 434 MB over the
workload's roughly 250,000 mutations; whether that is WAL growth or MVCC versions is the engine's
business, but a client that cannot tune the checkpoint threshold cannot help a long-running server
manage it. Surface all three.

### Error handling — good after today

Exceptions carry the native message and the statement. The classifier is the one place engine
wording is matched; it now covers both conflict wordings and both dispatch paths. Gap: no error
code. The C API exposes none, so this is a documentation matter only.

### API surface and usability — good, three gaps

What works well: parameter objects with silent-corruption cases tested, `Select<T>` resolved from
the column shape (so an empty result still reports a mismatched target), typed row accessors,
`ExecuteAsync` for the no-rows case, and documentation that states thread-safety per type.

Gaps:

1. **No synchronous disposal.** `LadybugConnection`, `LadybugQueryResult`, `LadybugPreparedStatement`
   and `LadybugTransaction` implement only `IAsyncDisposable` although every operation completes
   synchronously. Sync callers (the benchmark fixture, a console tool, a test helper) must write
   `DisposeAsync().AsTask().GetAwaiter().GetResult()`. Microsoft.Data.Sqlite, whose async methods
   also complete synchronously, implements both. Add `IDisposable` alongside.
2. **No prepared-statement cache.** `QueryAsync(cypher, parameters)` prepares on every call, which
   the point-lookup benchmark prices at 60 µs of a 122 µs call. A per-connection cache keyed by
   statement text would halve the cost of the overload the docs recommend for one-shot queries.
   The LINQ design depends on this cache anyway.
3. **No cancellation of a running query.** The token is checked before the call and between rows.
   `lbug_connection_interrupt` exists and is safe to call from another thread; registering it on
   the token is the one place "genuine async" would buy something.

### Packaging, publishing, and currency — the blocking area

- **Engine pin.** `liblbug.version` is v0.18.3 (2026-07-21). Upstream shipped 0.19.0, 0.19.1, 0.20.0,
  0.20.1 and 0.20.2 since; 0.20.1 and 0.20.2 fix a SIGSEGV and stale rows when re-executing
  parameterized statements on the cached-plan path, which is precisely the hot path this client
  documents. The C header changed by one added function in that span, so the bump is mechanical:
  update the version file, `fetch-liblbug.sh --update-lock`, `regen-interop.sh`, run the suite.
- **Positioning.** Upstream now maintains `LadybugDB` (0.19.1) with per-RID native packages. The
  README's "none for .NET" sentence has been replaced with a comparison table. Everything the
  official binding does is now the floor; this client's differentiators are the typed value model,
  async-shaped streaming, `Select<T>`, parameter objects, the transaction guard, the lifetime model
  and win-arm64.
- **Package metadata and validation.** Present: `PackageReadmeFile`, license expression, repository
  URL, XML docs, `[LibraryImport]`, SHA256-pinned natives, Trusted Publishing. Missing:
  `PackageIcon`, `PackageTags`, `PublishRepositoryUrl`, `ContinuousIntegrationBuild` on CI, a
  symbols package, `EnablePackageValidation`, `PublicApiAnalyzers`, `IsAotCompatible`, a
  `CHANGELOG.md`, and a stated versioning policy tying package versions to engine versions.
- **Native package shape.** One `LadybugDb.Client.Native` carrying all six RIDs (roughly 130 MB)
  where every peer ships per-RID packages plus a meta package. Consumers publishing a single-RID
  app pay for five binaries they never load.

**Addendum (done the same day).** Both of the first two items were resolved by adopting upstream's
packaging rather than improving our own:

- `LadybugDb.Client.Native`, the fetch script and the SHA256 lockfile are gone. The engine comes
  from upstream's `LadybugDB.Native` meta package or one `LadybugDB.Native.<rid>` package, chosen
  by the consumer; `LadybugDb.Client` declares no dependency on either (`PackagingTests` pins that).
  Five RIDs, upstream-signed provenance, nothing to redistribute or verify here. win-arm64 is
  dropped until upstream packages it (they publish the engine build; the resolver still probes the
  path for a hand-placed binary).
- The interop is regenerated against the v0.19.1 header (one function added:
  `lbug_connection_get_pushed_sql`); `third-party/liblbug.version` is the single pin. 0.20.x has no
  native package on nuget.org yet, so the pin moves when upstream publishes one.
- New guard: `LadybugDatabase` reads `lbug_get_version()` once and refuses an engine older than
  the pinned major.minor with a `LadybugException` naming the version to install, instead of an
  `EntryPointNotFoundException` from whichever call was missing; newer engines are accepted.
  `LadybugDatabase.EngineVersion` and `MinimumEngineVersion` expose both sides. Unit tests cover the
  comparison; an integration test opens a database against the package's engine.
- CI and the release workflow lost their fetch steps; the release now pushes one package.
- Re-measured on 0.19.1 (point lookups and the read path, same host, same session): the read
  path is unchanged within noise (typed accessors 8.85 ms per 10,000 rows, the typed prototype
  1.89 ms, Arrow 1.73 ms), while every point-lookup shape is 8–12% slower than on 0.18.3 (prepared
  key lookup 66 µs versus 59 µs at 10,000 objects, 69 µs versus 60 µs at 100,000; the traversal-hop
  lookup 503 µs versus 459 µs). Consistent across all eight dispatch shapes and both sizes, so it
  reads as an engine change (0.19.0 reworked the primary-key lookup planner) rather than noise;
  still far inside every threshold. Full table in `benchmarks/dotnet-microbenchmarks.md`.
- **Target framework.** net10.0 only is defensible (net8 leaves support 2026-11-10) if the README
  says so; adding net8.0 costs one conditional for `Lock`.
- **Trimming and AOT.** The reflective paths (`Select<T>`, parameter objects) carry
  `RequiresUnreferencedCode` correctly; the assembly is not marked `IsAotCompatible` and nothing in
  CI publishes an AOT sample.

### Observability — absent

No logging, no `ActivitySource`, no `Meter`, no health check, no DI registration. For SharpMUSH this
is optional; for an outside consumer it is what "production" means. The research appendix gives
the OpenTelemetry database conventions (stable since 1.33) and the Aspire client-integration shape;
put them in an `Extensions` package so the core stays dependency-free.

### Platforms — six packaged, two verified

linux-x64 and win-x64 run in CI. linux-arm64, osx-x64 and osx-arm64 come from upstream's packages
and are never executed here (win-arm64 has no upstream package; see the packaging addendum). A GitHub-hosted macOS runner and an arm64 Linux runner both
exist; add them before claiming the RIDs.

### Documentation — strong

`docs/USAGE.md` documents every public member with samples that are compiled and executed, which is
rarer than it should be. Corrected today: the README positioning, the `Select<T>` matching rule,
and the multi-writes claim.

### Tests and CI — strong

384 tests (unit and integration) pass after today's changes. The interop-drift job guards the
generated bindings against the pinned header, which is the right ABI gate; it cannot notice an
upstream release, so a scheduled job that diffs the latest release's header against the pinned one
would turn "two versions behind" into a notification instead of a discovery.

## Benchmark analysis

Full tables: `benchmarks/dotnet-microbenchmarks.md` (BenchmarkDotNet, 12 iterations, memory
diagnoser) and `benchmarks/results-dotnet.json` rendered by `benchmarks/report.py`. Reproduce with
the commands at the end.

### The read path is the client's only expensive component

Full scan of 10,000 rows (`RETURN o.dbref, o.name, o.loc`), engine time 0.18 ms:

| Path | Time | Per row | Allocated | Relative |
|---|---:|---:|---:|---:|
| `await foreach` + typed accessors (documented allocation-light path) | 8.58 ms (9.52 ms in the second run) | 858 ns | 4.40 MB | 1.00 |
| `Select<ObjRow>` (reflective constructor plan, cached) | 9.50 ms | 950 ns | 5.76 MB | 1.00 (time), 1.31 (memory) |
| `ToListAsync()` over the same enumerator | 12.46 ms | | 4.92 MB | 1.45 |
| `RETURN o` whole nodes | 21.13 ms | 2.1 µs | 10.24 MB | 2.46 |
| Prototype: same `LadybugValue[]` rows, stack-allocated wrappers, column types read once | 3.40 ms | 340 ns | 1.86 MB | 0.40 |
| Prototype: typed record per row, no boxing | 1.89 ms | 189 ns | 1.06 MB | 0.22 |
| Prototype: Arrow chunks (2,048 rows) decoded from buffers | 1.79 ms | 179 ns | 1.06 MB | 0.21 |

Reading: of the 858 ns the client spends per row today, about 520 ns is interop discipline that
upstream's ownership rules make unnecessary. Per cell the shipping path allocates a native block
and a `SafeHandle` for the value wrapper, another pair for the logical type (`lbug_value_get_data_type`
also does a C++ `new` per call), takes three leases, boxes the scalar, and destroys both handles.
Upstream: the tuple and the cell values are C++-owned borrows whose destroy calls are no-ops, and a
column's type cannot change between rows. The prototype keeps the result lease for the whole read
(the same guarantee, taken once), stack-allocates the two 16-byte structs and reads column types
once. That alone is a 2.5× speed-up and 58% fewer bytes with identical output; unboxed typed
materialization (what a source generator or a column-typed reader would emit) reaches 4.5×.

The Arrow path is only 5% faster than the typed per-tuple prototype at this shape because, from
the C API, Arrow chunks are built by the engine iterating the same tuples; its win is one foreign
call per chunk and one contiguous string buffer. It is worth having for wide or string-heavy scans
and for interop with Arrow consumers, not as the default row path. It would add an `Apache.Arrow`
dependency if exposed as Arrow objects; the prototype decodes without one.

Whole-node reads (`RETURN o`) cost 2.5× a projected row and 2.3× the memory: a dictionary per node.
The LINQ layer should project by default and materialize nodes only on request.

`ToListAsync()` from the in-box .NET 10 operators adds 45% over the enumerator on this shape,
almost all of it Gen1/Gen2 collections from list growth. Callers that know the count should
pre-size; the LINQ terminals will.

**Recommendation:** replace `LadybugQueryResult.ReadRow`'s per-cell handles with the prototype's
shape inside the library. It is not an API change. Keep `SafeHandle` for every object whose
lifetime crosses a call boundary (results, statements, connections, the owned values the container
getters return); drop it for the two borrowed structs. Expected: the documented row path goes from
858 ns to roughly 340 ns per three-column row.

### Point lookups: prepare once, and the engine is flat to 100,000 objects

`MATCH (o:Obj) WHERE o.dbref = $d RETURN o.name`:

| Dispatch | 10,000 objects | 100,000 objects | Allocated |
|---|---:|---:|---:|
| Literal interpolated into the text (parse and plan every call) | 101 µs | 100 µs | 880 B |
| `QueryAsync(cypher, new { d })` (prepare, bind, execute, dispose per call) | 122 µs | 125 µs | 1,305 B |
| Prepared once, typed `Bind` | 59 µs | 60 µs | 480 B |
| Prepared once, `ExecuteAsync(new { d })` | 60 µs | 62 µs | 1,128 B |
| Prepared once, `Select<string>` | 61 µs | 62 µs | 1,464 B |
| Prepared once, hopped through `Task.Run` | 67 µs | 70 µs | 752 B |
| Attribute by its own primary key (`Attr.akey = $k`) | 62 µs | 88 µs | 523 B |
| Attribute through the graph (`(o)-[:Has]->(a)` filtered by `aname`) | 459 µs | 649 µs | 480 B |

Reading: the engine's primary-key path costs 60 µs and does not move between 10,000 and 100,000
objects. Preparing per call doubles it; interpolating is between the two and forfeits parameter
safety. Genuine thread-pool offloading would cost 8–10 µs per call, 15% of a lookup, so the
async-shaped-but-synchronous design is the right one for this engine and should stay until
cancellation gives a reason to change it. The reflective parameter object costs 1 µs over a typed
`Bind`; the ergonomics are worth it.

The graph-shaped attribute lookup is 7–10× the key lookup. For SharpMUSH's attribute reads, key
the attribute table by `"dbref/NAME"` (the edge model) and look it up directly; keep the `Has`
edge for enumeration. This matches the Python harness's finding a month earlier and now has a
per-call number.

### Writes: one SET is 250–370 µs, and batching buys little

| Shape | Per SET | Allocated |
|---|---:|---:|
| Auto-commit, parameters object | 266 µs | 1,148 B |
| Auto-commit, prepared | 537 µs (high variance, ±220 µs) | 219 B |
| `BeginTransactionAsync` + SET + `CommitAsync` | 372 µs | 587 B |
| Raw `BEGIN`/`COMMIT` through the classifier | 250 µs | 851 B |
| 100 SETs in one transaction, per SET | 168 µs | 223 B |

Reading: the variance across these rows is larger than the differences between them; every shape is
a WAL append plus an fsync on this NVMe. The managed transaction wrapper costs nothing measurable
over raw statements. Batching a hundred writes saves only a third per write, so per-commit cost is
not what dominates here; the SET itself (a key lookup plus an update) is. The workload harness
confirms it: 5,600–6,900 auto-committed mutations per second at batch size one.

### Traversals

| Query (10,000 objects) | Time |
|---|---:|
| Room contents, ten rows projected (`a.dbref, a.name`) | 1.07 ms (median 0.91, ±0.26) |
| Room contents, `count(a)` | 275 µs |
| One object with all ten attributes | 566 µs |
| Two hops | 944 µs |

Reading: the projected traversal costs four times the counted one, and its variance is the largest
in the suite. The suspect was the engine's default worker fan-out (20 threads for a ten-row result);
the thread-cap table below confirms it accounts for a large part of the cost, though projecting
`a.name` through the edge remains several times the cost of counting.

### Engine thread cap

The engine fans every query out across `max_num_threads` workers, twenty on this host by default.
Same database (10,000 objects), same prepared statements, three caps:

| Query | Default (20 workers) | `MaxThreads = 1` | `MaxThreads = 4` |
|---|---:|---:|---:|
| Point lookup by key | 59 µs | 60 µs | 64 µs |
| Room contents, ten rows projected | 881 µs | 731 µs | 785 µs |
| One object with all attributes | 538 µs | 397 µs | 415 µs |
| Two hops | 1,308 µs | 630 µs | 667 µs |
| Full scan, 10,000 rows | 9.11 ms | 8.57 ms | 8.77 ms |

Reading: for every query that touches an edge, the default fan-out costs 17–50% of the latency;
the two-hop walk halves with a single worker. Point lookups do not care. Even the full scan is not
faster with twenty workers at this size. A MUSH's working set never produces the multi-million-row
scans the fan-out is for, so cap it: `new LadybugConfig { MaxThreads = 1 }` (or 2–4 if analytical
queries share the process), and give the process's parallelism to connections instead.

### The SharpMUSH workload, .NET versus the Python control

Same schema, same operations, same pre-registered thresholds. The .NET harness uses prepared
statements with bound parameters (this client's documented hot path) and `COPY FROM` for bulk
load; the Python harness interpolates statements and loads with `CREATE`, because those are
idiomatic there. Both ran on this host on engine 0.18.3 in the same session.

| Measure, 10,000 objects, edge model | .NET client | Python binding | SQLite (in-process control) | Threshold |
|---|---:|---:|---:|---:|
| Single mutation commit p99 | 0.23 ms | 0.30 ms | 0.01 ms | 5 ms |
| Point lookup p99 | 0.13 ms | 0.24 ms | 0.004 ms | 2 ms |
| Read under continuous write p99 | 0.14 ms | 0.22 ms | 0.015 ms | 5 ms |
| Contents traversal p99 | 0.59 ms | 1.04 ms | 0.003 ms | |
| Mutations/s, batch 1 → 1000 | 6,896 → 10,884 | 4,310 → 7,887 | 147,730 → 436,992 | 10× knee |
| Eight writers, engine default | 2,613/s | 2,529/s | 199,891/s | |
| Eight writers, `EnableMultiWrites` | 14,393/s, 38 conflicts | not measured | | |
| Load 10,000 objects | 0.29 s (`COPY`) | 41.9 s (`CREATE`) | 0.08 s | |

Reading: this client is 1.3–1.8× faster than the Python binding on every latency, which is the
prepared-statement path paying off (the Python harness re-parses every statement) plus lower
per-call overhead, not anything the engine does differently. Both are 25–100× slower than SQLite
on point operations: this is an analytical engine doing OLTP work, and it does it inside SharpMUSH's
budget with an order of magnitude to spare, but it is not an OLTP engine. The 100,000-object rows in
the older `results.json` (point lookups of 2–3 ms) were produced by a different load path (1.4
million `CREATE` statements with no checkpoint) and should not be compared; the .NET run at 100,000
objects measured 0.175 ms p99 after a `COPY` load.

Two thresholds report FAIL and both are the threshold's fault, not the engine's: the batching-knee
threshold demands a 10× gain from batching, which only a store with a bad per-commit cost can show;
this one is already at 6,000–7,000 single-statement commits per second. Treat the batch-knee rows
as informational.

### What the benchmarks say to do, in order

1. Ship the prototype's per-tuple read path inside `LadybugQueryResult` (2.5× on every row read;
   no API change).
2. Add a per-connection prepared-statement cache and use it from `QueryAsync(cypher, parameters)`
   and `Select<T>` (halves the recommended one-shot overload's cost).
3. Document `EnableMultiWrites` as the setting for a multi-writer server and keep the retry loop;
   the corrected numbers are in `docs/USAGE.md`.
4. Set `MaxThreads = 1` (up to 4) for OLTP-shaped use: 17–50% off every traversal, nothing lost
   on lookups.
5. Project columns; materialize nodes only when asked.
6. Keep async-shaped-synchronous. Wire cancellation to `interrupt` when it is needed.

## The standardized-library checklist, scored

From the research appendix, marked against this repository today.

| Item | Status |
|---|---|
| README positions against `LadybugDB` | done today |
| Trusted Publishing, no API keys | done |
| `PackageReadmeFile`, license expression, repository URL | done |
| `PackageIcon`, `PackageTags`, `PublishRepositoryUrl`, release notes | done (package hygiene, same day) |
| `ContinuousIntegrationBuild` on CI, snupkg | done |
| `EnablePackageValidation` | done; the baseline version is set at the first publish |
| `PublicApiAnalyzers` with shipped/unshipped files | missing |
| `IsAotCompatible` + an AOT publish in CI | done (`samples/LadybugDb.Client.AotSample`, `aot-publish` job) |
| `[LibraryImport]` | done; `DisableRuntimeMarshalling` and `SuppressGCTransition` not applied |
| Native binaries with verified provenance, `runtimes/{rid}/native` | done via upstream's `LadybugDB.Native.<rid>` packages (addendum) |
| Resolver remapping `lbug_shared` ↔ `liblbug` | done |
| Thread-safety and disposal contract per type, tested | done |
| SafeHandle everywhere, parent/child lifetime | done |
| SemVer, `CHANGELOG.md`, tagged releases, engine-compatibility policy | done (`CHANGELOG.md`, `docs/RELEASING.md` versioning section, `.github/release.yml`) |
| Exceptions carry native text | done |
| `IDisposable` alongside `IAsyncDisposable` | done |
| Cancellation via `interrupt` | done |
| DI / health / telemetry extensions | done (`LadybugDiagnostics` in the core; `LadybugDb.Client.Extensions` for DI and the health check) |
| Benchmarks in-tree with published numbers | done today |
| CI on every packaged RID | four of five (linux-x64, win-x64, osx-arm64, linux-arm64 legs; osx-x64 has no hosted runner) |

## LINQ direction

The design is in the spec linked above. The short version: a small immutable Cypher AST and
renderer as the substrate (usable on its own as a fluent DSL and as the never-interpolate guarantee
SharpMUSH wants), `[Node]`/`[Rel]` descriptors validated against `CALL show_tables()`/`table_info()`
at start-up, and an `IQueryable<T>` front end whose translator is a published whitelist that throws
on anything else and never evaluates on the client. Async comes from an own
`ExecuteAsync<TResult>` plus `AsAsyncEnumerable()`/`ToListAsync()` extensions, exactly as EF Core,
MongoDB and Marten do it; the queryable root must not implement `IAsyncEnumerable<T>` or every
`Where` becomes ambiguous with the in-box .NET 10 operators. re-linq (frozen since 2018, wrong
clause model), Ix `IAsyncQueryable` (not BCL, not planned) and source-generated Cypher from lambdas
(no prior art) are ruled out with reasons. A source generator is reserved for reflection-free row
readers and a static meta-model.

Phase A (AST, renderer, descriptors, fluent DSL) is worth doing regardless of whether Phase B (the
`IQueryable` layer) ever lands, and materialization reuses the existing `RowMapper`, so the mapping
rules stay in one place.

## SharpMUSH-specific guidance

- **Schema:** attributes as their own node table keyed by `"dbref/NAME"`, plus a `Has` edge for
  enumeration (the edge model). It won every measurement over the MAP column model: sets touch one
  row instead of rewriting a map, lookups are a key probe, and it scales under multi-writes where
  the map model does not.
- **Configuration:** `EnableMultiWrites = true`, a retry loop on `LadybugWriteConflictException`
  (both wordings and `PrepareAsync` now covered), one connection per worker thread, `MaxThreads`
  per the thread-cap table.
- **Startup:** load with `COPY FROM` (100,000 objects with a million attributes and a million edges
  in 1.3 s), never with per-object `CREATE`.
- **Queries:** prepare every hot statement once per connection and bind; project columns; read
  attributes by key, not through the graph.
- **Budget:** a MUSH command that does one lookup and one set costs roughly 0.35 ms of engine time
  on this host, with p99 under 0.7 ms at 100,000 objects.

## Roadmap

In order, each item small enough for one PR:

1. Merge `feat/api-ergonomics` plus today's fixes to `main`.
2. Move the pin to 0.20.x as soon as upstream publishes a `LadybugDB.Native` package for it; re-run
   the suite and the benchmarks (0.19.1 is done, see the addendum).
3. The per-tuple read path from the prototype, inside `LadybugQueryResult`.
4. `IDisposable` on the four async-disposable types; the prepared-statement cache; surface
   `auto_checkpoint`, `checkpoint_threshold`, `enable_checksums` on `LadybugConfig`.
5. Package hygiene: icon, tags, `PublishRepositoryUrl`, CI build flag, snupkg, package validation,
   `PublicApiAnalyzers`, `IsAotCompatible`, `CHANGELOG.md`, versioning policy; per-RID native
   packages plus a meta package; macOS and arm64 CI legs; a scheduled upstream-release check.
6. Publish `0.2.0-beta` under Trusted Publishing; SharpMUSH consumes it from nuget.org.
7. LINQ Phase A, then B.
8. Cancellation via `interrupt`; the `Extensions` package (DI, health, telemetry).

## Reproducing the numbers

```bash
dotnet build -c Release
cd LadybugDb.Client.Benchmarks
dotnet run -c Release --no-build -- --filter '*'                 # BenchmarkDotNet, ~10 minutes
dotnet run -c Release --no-build -- --workload --sizes 1000,10000,100000 --samples 1000 --output ../benchmarks/results-dotnet.json
cd ../benchmarks && python3 report.py --input results-dotnet.json
```

The Python control needs a virtual environment with the same `ladybug` version as the pinned engine
(`third-party/liblbug.version`; the numbers above used 0.18.3) and runs
`python workload_bench.py --sizes 1000 10000 --samples 1000 --out results.json`.
