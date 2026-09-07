using System.Globalization;
using BenchmarkDotNet.Attributes;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// The single most common MUSH read: one object, or one attribute of one object, by key. Compares
/// every way the client can dispatch that query, and two graph shapes for "attribute of object".
/// </summary>
public class PointLookupBenchmarks
{
    private const string ByDbref = "MATCH (o:Obj) WHERE o.dbref = $d RETURN o.name";
    private const string AttrByKey = "MATCH (a:Attr) WHERE a.akey = $k RETURN a.aval";
    private const string AttrByTraversal = "MATCH (o:Obj)-[:Has]->(a:Attr) WHERE o.dbref = $d AND a.aname = $n RETURN a.aval";

    private BenchDatabase _db = null!;
    private LadybugConnection _conn = null!;
    private LadybugPreparedStatement _byDbref = null!;
    private LadybugPreparedStatement _attrByKey = null!;
    private LadybugPreparedStatement _attrByTraversal = null!;
    private long _next;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _db = BenchDatabase.Create(Size);
        _conn = await _db.Database.ConnectAsync();
        _byDbref = await _conn.PrepareAsync(ByDbref);
        _attrByKey = await _conn.PrepareAsync(AttrByKey);
        _attrByTraversal = await _conn.PrepareAsync(AttrByTraversal);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _byDbref.DisposeAsync();
        await _attrByKey.DisposeAsync();
        await _attrByTraversal.DisposeAsync();
        await _conn.DisposeAsync();
        _db.Dispose();
    }

    // A deterministic stride through the key space so consecutive calls never hit the same row.
    private long NextDbref() => (_next += 7919) % Size;

    private static async Task<int> ReadOneString(LadybugQueryResult result)
    {
        await using (result)
        {
            await foreach (var row in result) return row.GetString(0).Length;
            return -1;
        }
    }

    /// <summary>Literal interpolated into the Cypher text: a fresh parse and plan every call, no binding.</summary>
    [Benchmark]
    public async Task<int> Interpolated()
    {
        var d = NextDbref();
        return await ReadOneString(await _conn.QueryAsync(
            string.Create(CultureInfo.InvariantCulture, $"MATCH (o:Obj) WHERE o.dbref = {d} RETURN o.name")));
    }

    /// <summary>Parameters object on the one-shot overload: prepare + bind + execute + dispose per call.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> ParametersObject()
    {
        var d = NextDbref();
        return await ReadOneString(await _conn.QueryAsync(ByDbref, new { d }));
    }

    /// <summary>Statement prepared once, typed <c>Bind</c> per call: the documented hot-path shape.</summary>
    [Benchmark]
    public async Task<int> PreparedTypedBind()
    {
        _byDbref.Bind("d", NextDbref());
        return await ReadOneString(await _byDbref.ExecuteAsync());
    }

    /// <summary>Statement prepared once, parameters object per call: reflection over the anonymous type each time.</summary>
    [Benchmark]
    public async Task<int> PreparedParametersObject()
    {
        var d = NextDbref();
        return await ReadOneString(await _byDbref.ExecuteAsync(new { d }));
    }

    /// <summary>Typed scalar projection over the prepared statement.</summary>
    [Benchmark]
    public async Task<int> PreparedSelectScalar()
    {
        var d = NextDbref();
        await foreach (var name in _byDbref.Select<string>(new { d })) return name.Length;
        return -1;
    }

    /// <summary>What genuine async offloading would cost on top: the same call hopped to the thread pool.</summary>
    [Benchmark]
    public Task<int> PreparedTypedBind_TaskRun() => Task.Run(PreparedTypedBind);

    /// <summary>Attribute by its own primary key (<c>"dbref/NAME"</c>): the shape the Python control measured.</summary>
    [Benchmark]
    public async Task<int> AttrByPrimaryKey()
    {
        var d = NextDbref();
        _attrByKey.Bind("k", string.Create(CultureInfo.InvariantCulture, $"{d}/{BenchDatabase.AttrNames[d % BenchDatabase.AttrsPerObj]}"));
        return await ReadOneString(await _attrByKey.ExecuteAsync());
    }

    /// <summary>Attribute reached through the graph: object by key, then one hop filtered by name.</summary>
    [Benchmark]
    public async Task<int> AttrByTraversalHop()
    {
        var d = NextDbref();
        _attrByTraversal.Bind("d", d);
        _attrByTraversal.Bind("n", BenchDatabase.AttrNames[d % BenchDatabase.AttrsPerObj]);
        return await ReadOneString(await _attrByTraversal.ExecuteAsync());
    }
}
