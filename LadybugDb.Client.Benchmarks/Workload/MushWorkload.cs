using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LadybugDb.Client.Benchmarks.Workload;

/// <summary>
/// The .NET twin of <c>benchmarks/workload_bench.py</c>: the same schema models, the same measured
/// operations, the same pre-registered thresholds, emitted as JSON <c>benchmarks/report.py</c>
/// renders unchanged. Run it to compare this client against the Python binding on the same host.
/// </summary>
/// <remarks>
/// Differences from the Python harness, all deliberate: every measured statement is a prepared
/// statement with bound parameters rather than interpolated text (the shape this client documents
/// as its hot path - the Python harness interpolates because that is idiomatic there); bulk load
/// uses <c>COPY FROM</c>; and the concurrent-writer section is run twice, with
/// <c>enable_multi_writes</c> off and on, since that flag is the engine's own answer to the
/// conflict counts the Python run reported.
/// </remarks>
internal static class MushWorkload
{
    private static readonly Dictionary<string, double> Thresholds = new()
    {
        ["single_write_p99_ms"] = 5.0,
        ["point_lookup_p99_ms"] = 2.0,
        ["read_under_write_p99_ms"] = 5.0,
        ["batch_knee_min_speedup"] = 10.0,
    };

    internal static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        var sizes = new List<int> { 1_000, 10_000 };
        var samples = 1000;
        var output = "results-dotnet.json";
        var models = new List<string> { "map", "edge" };
        for (var i = 0; i < args.Length; i++)
        {
            // Every option here takes a value; a trailing option without one is a usage error, not
            // an IndexOutOfRangeException.
            string Value(string option)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{option} needs a value.");
                return args[++i];
            }

            try
            {
                switch (args[i])
                {
                    case "--sizes": sizes = [.. Value("--sizes").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))]; break;
                    case "--samples": samples = int.Parse(Value("--samples"), CultureInfo.InvariantCulture); break;
                    case "--output": output = Value("--output"); break;
                    case "--models": models = [.. Value("--models").Split(',')]; break;
                    default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        var results = new List<ModelResult>();
        foreach (var size in sizes)
        {
            foreach (var model in models)
            {
                Console.WriteLine($"== {model} @ {size:N0}");
                var r = await RunModelAsync(model, size, samples);
                results.Add(r);
                Console.WriteLine($"   load {r.LoadS}s  write p99 {r.SingleWrite.P99:F2}ms  lookup p99 {r.PointLookup.P99:F3}ms  " +
                                  $"traversal p99 {r.ContentsTraversal.P99:F3}ms  batch1000 {r.BatchRates["1000"]:F0}/s");
            }
        }

        var doc = new
        {
            meta = new
            {
                seed = 20260727,
                thresholds = Thresholds,
                attrs_per_obj = BenchDatabase.AttrsPerObj,
                host = new
                {
                    cpu_count = Environment.ProcessorCount,
                    storage = Path.GetTempPath(),
                    python = $".NET {Environment.Version} LadybugDb.Client",
                    ladybug = NativeVersion(),
                },
            },
            results,
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
        }));
        Console.WriteLine($"wrote {output}");
        return 0;
    }

    private static unsafe string NativeVersion() =>
        Interop.NativeString.TakeOwnership(Native.LbugNative.lbug_get_version());

    private static async Task<ModelResult> RunModelAsync(string model, int size, int samples)
    {
        var rng = new Random(20260727);
        var res = new ModelResult { Model = $"dotnet-{model}", Size = size };

        using var db = BenchDatabase.Create(size, model);
        res.LoadS = Math.Round(db.LoadSeconds, 2);
        res.DiskMbAfterLoad = Math.Round(db.DiskBytes() / 1048576.0, 2);
        res.ColdOpenMs = Math.Round((await db.ReopenAsync()).TotalMilliseconds, 2);

        var conn = await db.Database.ConnectAsync();
        var ops = await Ops.CreateAsync(conn, model);

        // Warm the JIT on every measured shape before measuring anything. The Python control has
        // no tiered compilation, so without this the first samples would carry JIT cost instead of
        // engine cost.
        for (var k = 0; k < 50; k++)
        {
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await ops.SetAttrAsync(rng.Next(size), "DESC", "warm");
                await tx.CommitAsync();
            }
            await ops.ReadAttrAsync(rng.Next(size), "DESC");
            await ops.ContentsCountAsync(rng.Next(Math.Max(1, size / 10)));
        }

        // 1. single-mutation commit latency
        var lat = new List<double>(samples);
        for (var k = 0; k < samples; k++)
        {
            var d = rng.Next(size);
            var a = BenchDatabase.AttrNames[rng.Next(BenchDatabase.AttrsPerObj)];
            var t0 = Stopwatch.GetTimestamp();
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await ops.SetAttrAsync(d, a, $"s{k}");
                await tx.CommitAsync();
            }
            lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        res.SingleWrite = Stat.Of(lat);

        // 2. batching knee
        foreach (var batch in new[] { 1, 10, 100, 1000 })
        {
            var total = Math.Min(batch * 20, 2000);
            var sw = Stopwatch.StartNew();
            var done = 0;
            while (done < total)
            {
                await using var tx = await conn.BeginTransactionAsync();
                for (var i = 0; i < Math.Min(batch, total - done); i++)
                {
                    await ops.SetAttrAsync(rng.Next(size), BenchDatabase.AttrNames[rng.Next(BenchDatabase.AttrsPerObj)], "b");
                    done++;
                }
                await tx.CommitAsync();
            }
            res.BatchRates[batch.ToString(CultureInfo.InvariantCulture)] = Math.Round(total / sw.Elapsed.TotalSeconds, 1);
        }

        // 3. point lookup
        lat.Clear();
        for (var k = 0; k < samples; k++)
        {
            var d = rng.Next(size);
            var a = BenchDatabase.AttrNames[rng.Next(BenchDatabase.AttrsPerObj)];
            var t0 = Stopwatch.GetTimestamp();
            await ops.ReadAttrAsync(d, a);
            lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        res.PointLookup = Stat.Of(lat);

        // 4. contents traversal
        lat.Clear();
        for (var k = 0; k < Math.Max(200, samples / 4); k++)
        {
            var r = rng.Next(Math.Max(1, size / 10));
            var t0 = Stopwatch.GetTimestamp();
            await ops.ContentsCountAsync(r);
            lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        res.ContentsTraversal = Stat.Of(lat);

        // 5. read latency under a continuous writer on a second connection
        {
            using var stop = new CancellationTokenSource();
            var errors = new List<string>();
            var writer = Task.Run(async () =>
            {
                try
                {
                    await using var wc = await db.Database.ConnectAsync();
                    await using var wops = await Ops.CreateAsync(wc, model);
                    var wr = new Random(1);
                    var i = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        await using var tx = await wc.BeginTransactionAsync();
                        await wops.SetAttrAsync(wr.Next(size), "DESC", $"w{i++}");
                        await tx.CommitAsync();
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            });
            await Task.Delay(300);
            lat.Clear();
            for (var k = 0; k < Math.Min(samples, 500); k++)
            {
                var d = rng.Next(size);
                var a = BenchDatabase.AttrNames[rng.Next(BenchDatabase.AttrsPerObj)];
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    await ops.ReadAttrAsync(d, a);
                    lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"read-under-write {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }
            await stop.CancelAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            res.ReadUnderWrite = Stat.Of(lat);
            if (errors.Count > 0) res.Notes.Add($"read_under_write errors: {string.Join(" | ", errors.Take(3))}");
        }

        await ops.DisposeAsync();
        await conn.DisposeAsync();

        // 6. concurrent writers, retrying on write conflict. Once with the engine default
        // (single writer, conflicts raised) and once with enable_multi_writes.
        foreach (var multi in new[] { false, true })
        {
            db.Database.Dispose();
            var reopened = new LadybugDatabase(db.Path, new LadybugConfig { EnableMultiWrites = multi });
            try
            {
                foreach (var nw in new[] { 1, 2, 4, 8 })
                {
                    var counts = new int[nw];
                    var conflicts = new int[nw];
                    var fatal = new List<string>();
                    using var stop = new CancellationTokenSource();
                    var tasks = new Task[nw];
                    for (var w = 0; w < nw; w++)
                    {
                        var idx = w;
                        tasks[w] = Task.Run(async () =>
                        {
                            try
                            {
                                await using var wc = await reopened.ConnectAsync();
                                await using var wops = await Ops.CreateAsync(wc, model);
                                var wr = new Random(100 + idx);
                                while (!stop.IsCancellationRequested)
                                {
                                    try
                                    {
                                        await using var tx = await wc.BeginTransactionAsync();
                                        await wops.SetAttrAsync(wr.Next(size), "DESC", "c");
                                        await tx.CommitAsync();
                                        counts[idx]++;
                                    }
                                    catch (LadybugWriteConflictException)
                                    {
                                        conflicts[idx]++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (fatal) fatal.Add($"{ex.GetType().Name}: {ex.Message}");
                            }
                        });
                    }
                    await Task.Delay(3000);
                    await stop.CancelAsync();
                    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
                    var key = nw.ToString(CultureInfo.InvariantCulture);
                    (multi ? res.ConcurrentRatesMultiWrites : res.ConcurrentRates)[key] = Math.Round(counts.Sum() / 3.0, 1);
                    (multi ? res.ConcurrentConflictsMultiWrites : res.ConcurrentConflicts)[key] = conflicts.Sum();
                    if (fatal.Count > 0) res.Notes.Add($"concurrent(multi={multi},nw={nw}) fatal: {string.Join(" | ", fatal.Take(2))}");
                }
            }
            finally
            {
                reopened.Dispose();
            }
        }

        res.DiskMbAfterMutations = Math.Round(db.DiskBytes() / 1048576.0, 2);
        res.Notes.Add("LadybugDb.Client; prepared statements with bound parameters; COPY FROM bulk load; concurrent_rates_multi_writes = enable_multi_writes on");
        return res;
    }

    /// <summary>The measured statements, prepared once per connection.</summary>
    private sealed class Ops : IAsyncDisposable
    {
        private readonly string _model;
        private readonly LadybugPreparedStatement _set;
        private readonly LadybugPreparedStatement _read;
        private readonly LadybugPreparedStatement _contents;

        private Ops(string model, LadybugPreparedStatement set, LadybugPreparedStatement read, LadybugPreparedStatement contents)
        {
            _model = model;
            _set = set;
            _read = read;
            _contents = contents;
        }

        public static async Task<Ops> CreateAsync(LadybugConnection conn, string model)
        {
            LadybugPreparedStatement set, read;
            if (model == "map")
            {
                // MAP has no per-key update: the whole map is rewritten, as in the Python harness.
                var keys = string.Join(",", BenchDatabase.AttrNames.Select(a => $"'{a}'"));
                var vals = string.Join(",", Enumerable.Range(0, BenchDatabase.AttrsPerObj).Select(j => $"$v{j}"));
                set = await PrepareRetryingAsync(conn, $"MATCH (o:Obj) WHERE o.dbref = $d SET o.attrs = map([{keys}],[{vals}])");
                read = await conn.PrepareAsync("MATCH (o:Obj) WHERE o.dbref = $d RETURN o.attrs");
            }
            else
            {
                set = await PrepareRetryingAsync(conn, "MATCH (a:Attr) WHERE a.akey = $k SET a.aval = $v");
                read = await conn.PrepareAsync("MATCH (a:Attr) WHERE a.akey = $k RETURN a.aval");
            }
            var contents = await conn.PrepareAsync("MATCH (a:Obj)-[:Located]->(b:Obj) WHERE b.dbref = $r RETURN count(a)");
            return new Ops(model, set, read, contents);
        }

        /// <summary>
        /// Preparing a WRITE statement contends for the single writer slot exactly like executing
        /// one, so under concurrent writers it can fail with the retryable conflict. The concurrent
        /// section prepares on every writer's own connection while others are already writing.
        /// </summary>
        private static async Task<LadybugPreparedStatement> PrepareRetryingAsync(LadybugConnection conn, string cypher)
        {
            while (true)
            {
                try
                {
                    return await conn.PrepareAsync(cypher);
                }
                catch (LadybugWriteConflictException)
                {
                    await Task.Yield();
                }
            }
        }

        public async Task SetAttrAsync(int dbref, string attr, string val)
        {
            if (_model == "map")
            {
                _set.Bind("d", (long)dbref);
                for (var j = 0; j < BenchDatabase.AttrsPerObj; j++)
                {
                    _set.Bind($"v{j}", BenchDatabase.AttrNames[j] == attr ? val : $"v{dbref}_{j}");
                }
            }
            else
            {
                _set.Bind("k", $"{dbref}/{attr}");
                _set.Bind("v", val);
            }
            await _set.ExecuteNonQueryAsync();
        }

        public async Task ReadAttrAsync(int dbref, string attr)
        {
            if (_model == "map") _read.Bind("d", (long)dbref); else _read.Bind("k", $"{dbref}/{attr}");
            await using var r = await _read.ExecuteAsync();
            await foreach (var row in r) { _ = row.GetValue(0); break; }
        }

        public async Task<long> ContentsCountAsync(int room)
        {
            _contents.Bind("r", (long)room);
            await using var r = await _contents.ExecuteAsync();
            await foreach (var row in r) return row.GetInt64(0);
            return 0;
        }

        public async ValueTask DisposeAsync()
        {
            await _set.DisposeAsync();
            await _read.DisposeAsync();
            await _contents.DisposeAsync();
        }
    }

    internal sealed class Stat
    {
        public int N { get; set; }
        public double P50 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }

        public static Stat Of(List<double> samples)
        {
            if (samples.Count == 0) return new Stat();
            var s = samples.Order().ToArray();
            double Q(double p) => s[Math.Min(s.Length - 1, (int)(s.Length * p))];
            return new Stat { N = s.Length, P50 = Q(0.50), P95 = Q(0.95), P99 = Q(0.99), Max = s[^1], Mean = s.Average() };
        }
    }

    internal sealed class ModelResult
    {
        public string Model { get; set; } = "";
        public int Size { get; set; }
        public double LoadS { get; set; }
        public double DiskMbAfterLoad { get; set; }
        public double ColdOpenMs { get; set; }
        public Stat SingleWrite { get; set; } = new();
        public Stat PointLookup { get; set; } = new();
        public Stat ContentsTraversal { get; set; } = new();
        public Stat ReadUnderWrite { get; set; } = new();
        public Dictionary<string, double> BatchRates { get; } = new();
        public Dictionary<string, double> ConcurrentRates { get; } = new();
        public Dictionary<string, int> ConcurrentConflicts { get; } = new();
        public Dictionary<string, double> ConcurrentRatesMultiWrites { get; } = new();
        public Dictionary<string, int> ConcurrentConflictsMultiWrites { get; } = new();
        public double DiskMbAfterMutations { get; set; }
        public List<string> Notes { get; } = new();
    }
}
