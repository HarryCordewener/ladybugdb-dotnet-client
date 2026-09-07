using System.Globalization;
using BenchmarkDotNet.Attributes;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// The unit of MUSH write work - one attribute set - through every transaction shape the client
/// offers, plus the batching case. Every variant runs the same <c>SET</c> on the same table.
/// </summary>
public class WriteBenchmarks
{
    private const string SetAttr = "MATCH (a:Attr) WHERE a.akey = $k SET a.aval = $v";

    private BenchDatabase _db = null!;
    private LadybugConnection _conn = null!;
    private LadybugPreparedStatement _set = null!;
    private long _next;

    [Params(10_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _db = BenchDatabase.Create(Size);
        _conn = await _db.Database.ConnectAsync();
        _set = await _conn.PrepareAsync(SetAttr);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _set.DisposeAsync();
        await _conn.DisposeAsync();
        _db.Dispose();
    }

    private string NextKey()
    {
        var d = (_next += 7919) % Size;
        return string.Create(CultureInfo.InvariantCulture, $"{d}/{BenchDatabase.AttrNames[d % BenchDatabase.AttrsPerObj]}");
    }

    /// <summary>One statement, engine auto-commits: the cheapest thing a caller can write.</summary>
    [Benchmark(Baseline = true)]
    public async Task SetAutoCommit_ParametersObject()
    {
        await _conn.ExecuteAsync(SetAttr, new { k = NextKey(), v = "x" });
    }

    /// <summary>Same, on a statement prepared once.</summary>
    [Benchmark]
    public async Task SetAutoCommit_Prepared()
    {
        _set.Bind("k", NextKey());
        _set.Bind("v", "x");
        await _set.ExecuteNonQueryAsync();
    }

    /// <summary>The managed transaction wrapper: BEGIN, SET, COMMIT as three engine calls.</summary>
    [Benchmark]
    public async Task SetInManagedTransaction()
    {
        await using var tx = await _conn.BeginTransactionAsync();
        _set.Bind("k", NextKey());
        _set.Bind("v", "x");
        await _set.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    /// <summary>Raw BEGIN/COMMIT through the classifier path, for the cost of the bookkeeping itself.</summary>
    [Benchmark]
    public async Task SetInRawTransaction()
    {
        await _conn.ExecuteAsync("BEGIN TRANSACTION");
        _set.Bind("k", NextKey());
        _set.Bind("v", "x");
        await _set.ExecuteNonQueryAsync();
        await _conn.ExecuteAsync("COMMIT");
    }

    /// <summary>One hundred sets in one transaction, reported per set.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public async Task Batch100InOneTransaction()
    {
        await using var tx = await _conn.BeginTransactionAsync();
        for (var i = 0; i < 100; i++)
        {
            _set.Bind("k", NextKey());
            _set.Bind("v", "x");
            await _set.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
