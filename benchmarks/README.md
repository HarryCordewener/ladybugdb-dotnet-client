# Benchmarks

Two harnesses measure the same MUSH-shaped workload, and one BenchmarkDotNet project measures the
client's own overheads. The write-up that interprets all of it is
[`docs/2026-09-06-production-readiness.md`](../docs/2026-09-06-production-readiness.md).

| File | What |
|---|---|
| `workload_bench.py` | The original Python harness: engine viability for a MUSH, with pre-registered thresholds. Loads with `CREATE`, interpolates statements. |
| `results.json` | Its first run (2026-07-27, engine 0.18.3). The 100,000-object rows were loaded through 1.4 million `CREATE` statements and are not comparable with a `COPY`-loaded database. |
| `results-python-2026-09-06.json` | The Python harness re-run on 2026-09-06 at 1,000 and 10,000 objects, same host and engine as the .NET run, as the control. |
| `results-dotnet.json` | The .NET twin (`LadybugDb.Client.Benchmarks --workload`): same schema, operations and thresholds; prepared statements with bound parameters; `COPY FROM` load; the concurrent-writer section run with and without `EnableMultiWrites`. |
| `dotnet-microbenchmarks.md` | BenchmarkDotNet tables: read path, point lookups, writes, traversals, thread cap, and the two read-path prototypes. |
| `report.py` | Renders any of the JSON files as markdown judged against the thresholds. |

## Running

```bash
# .NET workload (writes results-dotnet.json here)
cd ../LadybugDb.Client.Benchmarks
dotnet run -c Release -- --workload --sizes 1000,10000,100000 --samples 1000 --output ../benchmarks/results-dotnet.json

# BenchmarkDotNet, all classes (about ten minutes) or a filter
dotnet run -c Release -- --filter '*'
dotnet run -c Release -- --filter '*PointLookup*'

# Python control and rendering run from this directory, which the first block left
cd ../benchmarks

# Python control (needs a venv with the ladybug version matching third-party/liblbug.version)
python workload_bench.py --sizes 1000 10000 --samples 1000 --out results-python.json

# render
python report.py --input results-dotnet.json
```

Run one harness at a time; they measure sub-millisecond latencies and a concurrent build or test
run shows up in the p99 column.
