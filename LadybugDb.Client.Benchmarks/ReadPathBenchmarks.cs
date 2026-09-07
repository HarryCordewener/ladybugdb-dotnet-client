using BenchmarkDotNet.Attributes;
using LadybugDb.Client.Benchmarks.Prototypes;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// Full-scan read path: how expensive is it to pull every <c>Obj</c> row through each way the
/// client (and two prototypes) can materialize it? Per-row cost is what a "load everything at
/// startup" or "list all objects" path pays.
/// </summary>
public class ReadPathBenchmarks
{
    private const string Scan = "MATCH (o:Obj) RETURN o.dbref, o.name, o.loc";
    private BenchDatabase _db = null!;
    private LadybugConnection _conn = null!;

    [Params(10_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _db = BenchDatabase.Create(Rows);
        _conn = await _db.Database.ConnectAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _conn.DisposeAsync();
        _db.Dispose();
    }

    /// <summary>The engine's own cost for the scan, with nothing marshalled: the floor.</summary>
    [Benchmark]
    public async Task<long> CountOnly()
    {
        await using var r = await _conn.QueryAsync("MATCH (o:Obj) RETURN count(o)");
        await foreach (var row in r) return row.GetInt64(0);
        return 0;
    }

    /// <summary>The documented allocation-light path: <c>await foreach</c> plus typed accessors.</summary>
    [Benchmark(Baseline = true)]
    public async Task<long> RowAccessors()
    {
        long sum = 0;
        await using var r = await _conn.QueryAsync(Scan);
        await foreach (var row in r)
        {
            sum += row.GetInt64(0) + row.GetString(1).Length + row.GetInt64(2);
        }
        return sum;
    }

    /// <summary>The reflective projection: one record per row via the cached constructor plan.</summary>
    [Benchmark]
    public async Task<long> SelectRecord()
    {
        long sum = 0;
        await foreach (var o in _conn.Select<ObjRow>(Scan))
        {
            sum += o.Dbref + o.Name.Length + o.Loc;
        }
        return sum;
    }

    /// <summary>The in-box .NET 10 async LINQ operator on top of the enumerator.</summary>
    [Benchmark]
    public async Task<long> RowsToListAsync()
    {
        await using var r = await _conn.QueryAsync(Scan);
        var list = await r.ToListAsync();
        long sum = 0;
        // The same three columns the other read benchmarks consume, so the comparison is of the
        // enumeration path and not of how much of each row the loop happens to touch.
        foreach (var row in list) sum += row.GetInt64(0) + row.GetString(1).Length + row.GetInt64(2);
        return sum;
    }

    /// <summary>Whole NODE values (<c>RETURN o</c>): every property marshalled into a dictionary per row.</summary>
    [Benchmark]
    public async Task<long> NodeValues()
    {
        long sum = 0;
        await using var r = await _conn.QueryAsync("MATCH (o:Obj) RETURN o");
        await foreach (var row in r)
        {
            var node = row.GetValue(0).AsNode();
            sum += node.Properties["dbref"].AsInt64() + node.Properties["name"].AsString().Length;
        }
        return sum;
    }

    /// <summary>Prototype: same boxed <c>LadybugValue</c> output, without per-cell handles or type lookups.</summary>
    [Benchmark]
    public async Task<long> Prototype_FastPath_Values()
    {
        await using var r = await _conn.QueryAsync(Scan);
        var rows = FastRowReader.ReadValues(r);
        long sum = 0;
        foreach (var cells in rows) sum += cells[0].AsInt64() + cells[1].AsString().Length + cells[2].AsInt64();
        return sum;
    }

    /// <summary>Prototype: typed record per row, unboxed, cached column types - the per-tuple ceiling.</summary>
    [Benchmark]
    public async Task<long> Prototype_FastPath_Typed()
    {
        await using var r = await _conn.QueryAsync(Scan);
        var rows = FastRowReader.ReadObjRows(r);
        long sum = 0;
        foreach (var o in rows) sum += o.Dbref + o.Name.Length + o.Loc;
        return sum;
    }

    /// <summary>Prototype: Arrow C Data Interface chunks decoded straight from the buffers.</summary>
    [Benchmark]
    public async Task<long> Prototype_ArrowChunks()
    {
        await using var r = await _conn.QueryAsync(Scan);
        var rows = ArrowChunkReader.ReadObjRows(r, chunkSize: 2048);
        long sum = 0;
        foreach (var o in rows) sum += o.Dbref + o.Name.Length + o.Loc;
        return sum;
    }
}
