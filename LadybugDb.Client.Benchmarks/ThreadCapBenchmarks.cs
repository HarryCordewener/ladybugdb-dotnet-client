using BenchmarkDotNet.Attributes;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// Does the engine's worker-thread fan-out help or hurt the tiny queries a MUSH runs? The engine
/// parallelizes every query across <c>max_num_threads</c> workers (default: every core). For a
/// point lookup or a ten-row traversal the work is one morsel, so the fan-out is pure scheduling
/// cost. Same queries as the other classes, under three thread caps.
/// </summary>
public class ThreadCapBenchmarks
{
    private BenchDatabase _db = null!;
    private LadybugConnection _conn = null!;
    private LadybugPreparedStatement _byDbref = null!;
    private LadybugPreparedStatement _contents = null!;
    private LadybugPreparedStatement _objectWithAttrs = null!;
    private LadybugPreparedStatement _twoHop = null!;
    private long _next;

    /// <summary><c>0</c> is the engine default (one worker per logical core: 20 on the benchmark host).</summary>
    [Params(0, 1, 4)]
    public ulong MaxThreads { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _db = BenchDatabase.Create(10_000, config: new LadybugConfig { MaxThreads = MaxThreads });
        _conn = await _db.Database.ConnectAsync();
        _byDbref = await _conn.PrepareAsync("MATCH (o:Obj) WHERE o.dbref = $d RETURN o.name");
        _contents = await _conn.PrepareAsync("MATCH (a:Obj)-[:Located]->(b:Obj) WHERE b.dbref = $r RETURN a.dbref, a.name");
        _objectWithAttrs = await _conn.PrepareAsync("MATCH (o:Obj)-[:Has]->(a:Attr) WHERE o.dbref = $d RETURN a.aname, a.aval");
        _twoHop = await _conn.PrepareAsync("MATCH (a:Obj)-[:Located]->(b:Obj)-[:Located]->(c:Obj) WHERE a.dbref = $d RETURN c.dbref");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _byDbref.DisposeAsync();
        await _contents.DisposeAsync();
        await _objectWithAttrs.DisposeAsync();
        await _twoHop.DisposeAsync();
        await _conn.DisposeAsync();
        _db.Dispose();
    }

    private long NextDbref() => (_next += 7919) % 10_000;
    private long NextRoom() => (_next += 7919) % 1_000;

    [Benchmark(Baseline = true)]
    public async Task<int> PointLookup()
    {
        _byDbref.Bind("d", NextDbref());
        await using var r = await _byDbref.ExecuteAsync();
        await foreach (var row in r) return row.GetString(0).Length;
        return -1;
    }

    [Benchmark]
    public async Task<long> RoomContents()
    {
        _contents.Bind("r", NextRoom());
        long sum = 0;
        await using var r = await _contents.ExecuteAsync();
        await foreach (var row in r) sum += row.GetInt64(0) + row.GetString(1).Length;
        return sum;
    }

    [Benchmark]
    public async Task<long> ObjectWithAllAttributes()
    {
        _objectWithAttrs.Bind("d", NextDbref());
        long sum = 0;
        await using var r = await _objectWithAttrs.ExecuteAsync();
        await foreach (var row in r) sum += row.GetString(0).Length + row.GetString(1).Length;
        return sum;
    }

    [Benchmark]
    public async Task<long> TwoHop()
    {
        _twoHop.Bind("d", NextDbref());
        long sum = 0;
        await using var r = await _twoHop.ExecuteAsync();
        await foreach (var row in r) sum += row.GetInt64(0);
        return sum;
    }

    [Benchmark]
    public async Task<long> Scan10kRows()
    {
        long sum = 0;
        await using var r = await _conn.QueryAsync("MATCH (o:Obj) RETURN o.dbref, o.name, o.loc");
        await foreach (var row in r) sum += row.GetInt64(0) + row.GetString(1).Length + row.GetInt64(2);
        return sum;
    }
}
