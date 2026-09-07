using BenchmarkDotNet.Attributes;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// The small traversals a MUSH does constantly: what is in this room, load an object with all of
/// its attributes, and a two-hop walk.
/// </summary>
public class TraversalBenchmarks
{
    private BenchDatabase _db = null!;
    private LadybugConnection _conn = null!;
    private LadybugPreparedStatement _contents = null!;
    private LadybugPreparedStatement _contentsCount = null!;
    private LadybugPreparedStatement _objectWithAttrs = null!;
    private LadybugPreparedStatement _twoHop = null!;
    private long _next;

    [Params(10_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _db = BenchDatabase.Create(Size);
        _conn = await _db.Database.ConnectAsync();
        _contents = await _conn.PrepareAsync("MATCH (a:Obj)-[:Located]->(b:Obj) WHERE b.dbref = $r RETURN a.dbref, a.name");
        _contentsCount = await _conn.PrepareAsync("MATCH (a:Obj)-[:Located]->(b:Obj) WHERE b.dbref = $r RETURN count(a)");
        _objectWithAttrs = await _conn.PrepareAsync("MATCH (o:Obj)-[:Has]->(a:Attr) WHERE o.dbref = $d RETURN a.aname, a.aval");
        _twoHop = await _conn.PrepareAsync("MATCH (a:Obj)-[:Located]->(b:Obj)-[:Located]->(c:Obj) WHERE a.dbref = $d RETURN c.dbref");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _contents.DisposeAsync();
        await _contentsCount.DisposeAsync();
        await _objectWithAttrs.DisposeAsync();
        await _twoHop.DisposeAsync();
        await _conn.DisposeAsync();
        _db.Dispose();
    }

    private long NextRoom() => (_next += 7919) % Math.Max(1, Size / 10);
    private long NextDbref() => (_next += 7919) % Size;

    /// <summary>Every object located in a room (about ten rows), read as columns.</summary>
    [Benchmark(Baseline = true)]
    public async Task<long> RoomContents()
    {
        _contents.Bind("r", NextRoom());
        long sum = 0;
        await using var r = await _contents.ExecuteAsync();
        await foreach (var row in r) sum += row.GetInt64(0) + row.GetString(1).Length;
        return sum;
    }

    /// <summary>The same traversal, aggregated in the engine: the shape the Python control measured.</summary>
    [Benchmark]
    public async Task<long> RoomContentsCount()
    {
        _contentsCount.Bind("r", NextRoom());
        await using var r = await _contentsCount.ExecuteAsync();
        await foreach (var row in r) return row.GetInt64(0);
        return 0;
    }

    /// <summary>One object with all ten of its attributes: the "load an object" shape.</summary>
    [Benchmark]
    public async Task<long> ObjectWithAllAttributes()
    {
        _objectWithAttrs.Bind("d", NextDbref());
        long sum = 0;
        await using var r = await _objectWithAttrs.ExecuteAsync();
        await foreach (var row in r) sum += row.GetString(0).Length + row.GetString(1).Length;
        return sum;
    }

    /// <summary>Location of the location.</summary>
    [Benchmark]
    public async Task<long> TwoHop()
    {
        _twoHop.Bind("d", NextDbref());
        long sum = 0;
        await using var r = await _twoHop.ExecuteAsync();
        await foreach (var row in r) sum += row.GetInt64(0);
        return sum;
    }
}
