using BenchmarkDotNet.Attributes;

namespace LadybugDb.Client.Benchmarks;

/// <summary>
/// <c>TransactionStatement.Classify</c> runs on every <c>QueryAsync</c>. It should be free for the
/// overwhelmingly common non-transaction statement; this measures whether it is.
/// </summary>
public class ClassifierBenchmarks
{
    private const string Typical =
        "MATCH (o:Obj)-[:Has]->(a:Attr) WHERE o.dbref = $d AND a.aname = $n RETURN a.aval ORDER BY a.aname LIMIT 10";

    [Benchmark(Baseline = true)]
    public int TypicalQuery() => (int)TransactionStatement.Classify(Typical);

    [Benchmark]
    public int BeginTransaction() => (int)TransactionStatement.Classify("BEGIN TRANSACTION");
}
